using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Drives the live scenario-run window: it runs the scenario through <see cref="ScenarioRunner"/> and
/// surfaces each step as it happens (live log + a "done of total" counter), then a final summary. The
/// caller reads <see cref="Result"/> after the run completes to write the operation log, refresh the list, etc.
/// </summary>
public partial class ScenarioProgressViewModel : ObservableObject
{
    private readonly Scenario _scenario;
    private readonly IReadOnlyList<AdObjectRow> _targets;
    private readonly ScenarioRunner _runner;
    private readonly IList<string>? _operationLog;

    public string ScenarioName => _scenario.Name;
    public int Total => _targets.Count;

    /// <summary>The live, human-readable step-by-step log shown as the scenario runs.</summary>
    public ObservableCollection<string> Lines { get; } = new();

    [ObservableProperty] private int _done;
    [ObservableProperty] private bool _isRunning = true;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private string _summary = string.Empty;

    public string ProgressText => $"{Done} of {Total} object(s) processed";

    /// <summary>Set when the run finishes; null while still running.</summary>
    public BulkResult? Result { get; private set; }

    /// <summary>Raised when a line is appended, so the view can auto-scroll to the newest entry.</summary>
    public event Action? LineAdded;

    public ScenarioProgressViewModel(Scenario scenario, IReadOnlyList<AdObjectRow> targets,
        ScenarioRunner runner, IList<string>? operationLog)
    {
        _scenario = scenario;
        _targets = targets;
        _runner = runner;
        _operationLog = operationLog;
    }

    partial void OnDoneChanged(int value) => OnPropertyChanged(nameof(ProgressText));

    /// <summary>Runs the scenario, streaming progress into <see cref="Lines"/>. Called once by the window on load.</summary>
    public async Task RunAsync()
    {
        // Progress<T> marshals callbacks to the captured (UI) context, so collection updates stay thread-safe.
        var live = new Progress<string>(line => { Lines.Add(line); LineAdded?.Invoke(); });
        var count = new Progress<int>(n => Done = n);
        try
        {
            Result = await _runner.RunAsync(_scenario, _targets, count, default, _operationLog, live);
            Summary = $"Done — {Result.SuccessCount} succeeded, {Result.FailureCount} failed.";
        }
        catch (Exception ex)
        {
            // The runner handles per-target failures internally; reaching here means the run itself faulted.
            Summary = "The scenario run stopped: " + DirectoryService.Friendly(ex);
            Lines.Add("✗ " + Summary);
            LineAdded?.Invoke();
        }
        finally
        {
            IsRunning = false;
            IsDone = true;
        }
    }
}
