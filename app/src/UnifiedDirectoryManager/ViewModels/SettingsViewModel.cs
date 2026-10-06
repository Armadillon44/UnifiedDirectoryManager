using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Backs the File ▸ Settings dialog. Composes the on-prem AD connection form
/// (<see cref="Connection"/>, reused from the startup flow), the cloud sign-in section
/// (<see cref="Cloud"/>), and the Logs section (operation-log folder). A successful reconnect on the
/// AD tab fires the host's refresh callback so the tree/list rebind in place without restarting the app.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly Action _onReconnected;
    private readonly ISettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly ICredentialStore _credentials;

    public ConnectionViewModel Connection { get; }
    public CloudSignInViewModel Cloud { get; }

    /// <summary>The Toolbar page (audit T1).</summary>
    public ToolbarEditorViewModel Toolbar { get; }

    // --- Retries: how long to keep asking a service that has answered “not yet” ---
    [ObservableProperty] private int _entraRetryAttempts;
    [ObservableProperty] private int _entraRetryWaitSeconds;
    [ObservableProperty] private int _exchangeRetryAttempts;
    [ObservableProperty] private int _exchangeRetryWaitSeconds;
    [ObservableProperty] private string _retryStatus = string.Empty;

    public string RetryLimits =>
        $"{RetryPolicy.MinAttempts}–{RetryPolicy.MaxAttempts} attempts, " +
        $"{RetryPolicy.MinWaitSeconds}–{RetryPolicy.MaxWaitSeconds} seconds apart.";

    /// <summary>
    /// The cost of the current numbers, in words, recomputed as they are typed.
    /// </summary>
    /// <remarks>
    /// “50 attempts, 60 seconds apart” means nothing to read. “Up to 49 min of waiting per group” is the
    /// thing an operator can actually decide about, and seeing it move while typing is what stops a
    /// number being chosen without its consequence.
    /// </remarks>
    public string EntraRetrySummary
    {
        get
        {
            var p = new RetryPolicy(EntraRetryAttempts, EntraRetryWaitSeconds).Clamped();
            return $"→ Up to {RetryPolicy.Humanise(p.TotalWait)} of waiting per group. " +
                   "A Graph failure returns almost at once, so this is nearly all of it.";
        }
    }

    public string ExchangeRetrySummary
    {
        get
        {
            var p = new RetryPolicy(ExchangeRetryAttempts, ExchangeRetryWaitSeconds).Clamped();
            // 90s is ExchangeService.OpTimeout -- what one attempt costs when Exchange HANGS rather than
            // answering “not found yet”. The fast answer costs nothing, so both figures are shown.
            return $"→ Up to {RetryPolicy.Humanise(p.TotalWait)} of waiting per group, or " +
                   $"{RetryPolicy.Humanise(p.WorstCase(90))} if Exchange stops answering.";
        }
    }

    partial void OnEntraRetryAttemptsChanged(int value) => OnPropertyChanged(nameof(EntraRetrySummary));
    partial void OnEntraRetryWaitSecondsChanged(int value) => OnPropertyChanged(nameof(EntraRetrySummary));
    partial void OnExchangeRetryAttemptsChanged(int value) => OnPropertyChanged(nameof(ExchangeRetrySummary));
    partial void OnExchangeRetryWaitSecondsChanged(int value) => OnPropertyChanged(nameof(ExchangeRetrySummary));

    [RelayCommand]
    private void SaveRetrySettings()
    {
        // Clamped on the way in, so a value typed outside the range is corrected on screen rather than
        // saved and quietly ignored later.
        var entra = new RetryPolicy(EntraRetryAttempts, EntraRetryWaitSeconds).Clamped();
        var exchange = new RetryPolicy(ExchangeRetryAttempts, ExchangeRetryWaitSeconds).Clamped();
        LoadRetry(entra, exchange);

        _settings.EntraRetryAttempts = entra.Attempts;
        _settings.EntraRetryWaitSeconds = entra.WaitSeconds;
        _settings.ExchangeRetryAttempts = exchange.Attempts;
        _settings.ExchangeRetryWaitSeconds = exchange.WaitSeconds;
        _settingsStore.Save(_settings);
        RetryStatus = "Saved. The next user you create or copy uses these.";
    }

    [RelayCommand]
    private void ResetRetrySettings()
    {
        LoadRetry(RetryPolicy.Default, RetryPolicy.Default);
        RetryStatus = "Back to the defaults. Save to keep them.";
    }

    private void LoadRetry(RetryPolicy entra, RetryPolicy exchange)
    {
        EntraRetryAttempts = entra.Attempts;
        EntraRetryWaitSeconds = entra.WaitSeconds;
        ExchangeRetryAttempts = exchange.Attempts;
        ExchangeRetryWaitSeconds = exchange.WaitSeconds;
    }

    /// <summary>Operation-log folder override; blank means use the default shown in <see cref="DefaultLogDirectory"/>.</summary>
    [ObservableProperty] private string _operationLogDirectory = string.Empty;
    [ObservableProperty] private string _logStatus = string.Empty;

    // --- Entra Connect (directory sync) account ---
    /// <summary>The Entra Connect server that runs the delta sync (Start-ADSyncSyncCycle over WinRM).</summary>
    [ObservableProperty] private string _syncServer = string.Empty;
    /// <summary>When true, the sync runs as a saved account; otherwise as the current Windows user.</summary>
    [ObservableProperty] private bool _syncUseSavedAccount;
    [ObservableProperty] private string _syncUsername = string.Empty;
    [ObservableProperty] private string _syncStatus = string.Empty;
    /// <summary>True when a password is already saved, so the password box can be left blank to keep it.</summary>
    [ObservableProperty] private bool _syncHasSavedPassword;

    /// <summary>Sync-account password, set from the PasswordBox code-behind (PasswordBox can't be bound).</summary>
    public string SyncPassword { get; set; } = string.Empty;

    /// <summary>The built-in default folder, shown as a hint when no override is set.</summary>
    public string DefaultLogDirectory => OperationLog.DefaultDirectory;

    public SettingsViewModel(ConnectionViewModel connection, CloudSignInViewModel cloud,
        ISettingsStore settingsStore, AppSettings settings, ICredentialStore credentials, Action onReconnected)
    {
        Connection = connection;
        Cloud = cloud;
        _settingsStore = settingsStore;
        _settings = settings;
        _credentials = credentials;
        _onReconnected = onReconnected;
        _operationLogDirectory = settings.OperationLogDirectory ?? string.Empty;
        Toolbar = new ToolbarEditorViewModel(settingsStore, settings);
        LoadRetry(settings.EntraRetry, settings.ExchangeRetry);
        Connection.ConnectionSucceeded += (_, _) => _onReconnected();

        // Prefill the sync account from the server's saved credential, if any.
        _syncServer = settings.EntraConnectServer ?? string.Empty;
        if (_credentials.TryLoadSyncCredential(_syncServer) is { } savedSync)
        {
            _syncUseSavedAccount = true;
            _syncUsername = savedSync.Username;
            _syncHasSavedPassword = true;
        }
    }

    [RelayCommand]
    private void SaveSyncSettings()
    {
        var server = SyncServer.Trim();
        if (string.IsNullOrWhiteSpace(server)) { SyncStatus = "Enter the Entra Connect server name."; return; }

        _settings.EntraConnectServer = server;
        _settingsStore.Save(_settings);
        SyncServer = server;

        if (!SyncUseSavedAccount)
        {
            // Run as the current Windows user — remove any saved account for this server.
            _credentials.DeleteSyncCredential(server);
            SyncHasSavedPassword = false;
            SyncUsername = string.Empty;
            SyncPassword = string.Empty;
            SyncStatus = $"Saved. The sync on {server} will run as the current Windows user.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SyncUsername))
        {
            SyncStatus = "Enter the sync-account username, or clear “Use a saved account”.";
            return;
        }

        // Keep the existing password when the box is left blank and one is already stored.
        var password = SyncPassword;
        if (string.IsNullOrEmpty(password))
        {
            var existing = _credentials.TryLoadSyncCredential(server);
            if (existing is not null) password = existing.Password;
            else { SyncStatus = "Enter the sync-account password."; return; }
        }

        _credentials.SaveSyncCredential(server, SyncUsername.Trim(), password);
        SyncPassword = string.Empty;
        SyncHasSavedPassword = true;
        SyncStatus = $"Saved. The sync on {server} will run as {SyncUsername.Trim()} (stored in Windows Credential Manager).";
    }

    [RelayCommand]
    private void ClearSyncCredential()
    {
        var server = SyncServer.Trim();
        _credentials.DeleteSyncCredential(server);
        SyncUseSavedAccount = false;
        SyncUsername = string.Empty;
        SyncPassword = string.Empty;
        SyncHasSavedPassword = false;
        SyncStatus = string.IsNullOrWhiteSpace(server)
            ? "Saved sync account cleared."
            : $"Saved sync account for {server} cleared. The sync will run as the current Windows user.";
    }

    [RelayCommand]
    private void SaveLogSettings()
    {
        _settings.OperationLogDirectory = string.IsNullOrWhiteSpace(OperationLogDirectory)
            ? null : OperationLogDirectory.Trim();
        _settingsStore.Save(_settings);
        LogStatus = $"Saved. Logs will be written to: {OperationLog.ResolveDirectory(_settings)}";
    }

    [RelayCommand]
    private void ResetLogDirectory()
    {
        OperationLogDirectory = string.Empty;
        SaveLogSettings();
    }
}
