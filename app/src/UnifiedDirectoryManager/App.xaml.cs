using System.Windows;
using System.Windows.Threading;
using UnifiedDirectoryManager.Services;
using UnifiedDirectoryManager.ViewModels;
using UnifiedDirectoryManager.Views;

namespace UnifiedDirectoryManager;

/// <summary>
/// Application entry point and composition root. Wires the services, shows the connection dialog,
/// and opens the main window once a domain controller has been bound.
/// </summary>
public partial class App : Application
{
    private IDirectoryService _directory = null!;
    private ExchangeService? _exchange;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Install the rolling file logger before anything else can fail.
        var logger = new FileLogger();
        AppLog.Instance = logger;
        AppLog.LogDirectory = logger.Directory;
        AppLog.Instance.Info($"Unified Directory Manager started (v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}, " +
                             $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}).");

        // Never let an unhandled exception silently terminate the app — surface it instead.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // Catch faults that never reach the dispatcher: exceptions on background/non-UI threads, and
        // un-awaited Task faults (fire-and-forget) that would otherwise vanish unobserved.
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _directory = new DirectoryService();
        var locator = new DomainLocator();
        var credentials = new WindowsCredentialStore();
        var templates = new TemplateStore();
        var scenarios = new ScenarioStore();
        var settingsStore = new SettingsStore();
        var settings = settingsStore.Load();

        // Entra ID / Microsoft Graph cloud layer. Configure (but don't sign in) from saved
        // identifiers so a cached token can be reused silently; sign-in stays interactive.
        IGraphService graph = new GraphService();
        if (!string.IsNullOrWhiteSpace(settings.EntraTenantId) && !string.IsNullOrWhiteSpace(settings.EntraClientId))
            graph.Configure(settings.EntraTenantId!, settings.EntraClientId!);

        // Exchange Online layer (v2.0): hosts the ExchangeOnlineManagement module and reuses the Graph
        // sign-in for its token. Configured (but not connected) from the saved tenant; connect is lazy.
        // NOTE: -Organization is seeded with the tenant id; confirm the tenant-domain form during live testing.
        _exchange = new ExchangeService(graph);
        if (!string.IsNullOrWhiteSpace(settings.EntraTenantId))
            _exchange.Configure(settings.EntraTenantId!);

        // Scenario runner needs the cloud client too (scenarios can include Entra ID steps).
        var scenarioRunner = new ScenarioRunner(_directory, graph);

        var dialogs = new DialogService(_directory, templates, scenarios, settingsStore, settings, graph, locator, credentials, scenarioRunner);

        // The app no longer gates on an on-prem connection at startup. Show the main window immediately,
        // then attempt a silent connect from the saved profile in the background; a failure surfaces as a
        // (non-blocking) warning bar and the app continues — useful for cloud-only / Entra-only sessions.
        try
        {
            var mainVm = new MainViewModel(_directory, dialogs, scenarios, settingsStore, settings, graph, credentials);
            var main = new MainWindow { DataContext = mainVm };
            MainWindow = main;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            main.Show();
            _ = mainVm.StartupAsync(); // best-effort auto-connect + tree build (errors handled inside)
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("Main window failed to open.", ex);
            MessageBox.Show(
                "The main window failed to open:\n\n" + ex,
                "Unified Directory Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    // Burst tracking so a cascade of UI faults doesn't trap the user in an endless dialog loop on a broken UI.
    private long _lastUiFaultTick;
    private int _uiFaultBurst;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Instance.Error("Unhandled UI exception.", e.Exception);
        e.Handled = true; // keep the app alive for isolated, recoverable UI faults

        // A handful of faults in quick succession usually means the UI is in a bad state; continuing would have
        // the user acting on inconsistent data. Reset the burst when faults are well spaced out.
        var now = Environment.TickCount64;
        if (now - _lastUiFaultTick > 10_000) _uiFaultBurst = 0;
        _lastUiFaultTick = now;
        _uiFaultBurst++;

        if (_uiFaultBurst >= 3)
        {
            AppLog.Instance.Error("Repeated UI exceptions in a short window — shutting down to avoid acting on inconsistent state.");
            MessageBox.Show(
                "The app hit repeated errors and will close to avoid acting on inconsistent data.\n\n" +
                "Details were written to the log (Logs button on the toolbar).",
                "Unified Directory Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        MessageBox.Show(
            "An unexpected error occurred:\n\n" + e.Exception.Message +
            "\n\nDetails were written to the log (Logs button on the toolbar).",
            "Unified Directory Manager", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // A fire-and-forget Task faulted and nothing awaited it. Log it (and mark observed so it doesn't
        // escalate), rather than letting it disappear silently.
        AppLog.Instance.Error("Unobserved background task exception.", e.Exception);
        e.SetObserved();
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Last-ditch logging for a fault on a thread with no other handler. The runtime usually terminates
        // after this (IsTerminating), so there's nothing to recover — just make sure it's recorded.
        if (e.ExceptionObject is Exception ex)
            AppLog.Instance.Error($"Fatal unhandled exception (terminating={e.IsTerminating}).", ex);
        else
            AppLog.Instance.Error($"Fatal unhandled error (terminating={e.IsTerminating}).");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Tear down the Exchange Online session and its hosted runspace cleanly.
        try { _exchange?.Dispose(); } catch (Exception ex) { AppLog.Instance.Warn("Exchange dispose failed: " + ex.Message); }
        AppLog.Instance.Info("Unified Directory Manager exiting.");
        base.OnExit(e);
    }
}
