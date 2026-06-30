using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Drives the bulk-create window: pick a batch template, fill an editable grid (or import a CSV), then run
/// the phased <see cref="BulkUserCreator"/> (create all on-prem → one delta sync → wait → per-row cloud
/// groups / TAP). Generated passphrases and TAP codes come back on the report, shown after the run.
/// </summary>
public partial class BulkCreateUsersViewModel : ObservableObject
{
    private readonly IDirectoryService _directory;
    private readonly ITemplateStore _store;
    private readonly IDialogService _dialogs;
    private readonly IGraphService _graph;
    private readonly BulkUserCreator _creator;
    private readonly BulkUserCsvImporter _importer;
    private readonly AppSettings _settings;

    public ObservableCollection<UserTemplate> Templates { get; } = new();
    public ObservableCollection<BulkCreateRowViewModel> Rows { get; } = new();
    public ObservableCollection<string> ProgressSteps { get; } = new();

    [ObservableProperty] private UserTemplate? _selectedTemplate;
    [ObservableProperty] private string _targetOu = string.Empty;
    [ObservableProperty] private string _upnSuffix = string.Empty;

    /// <summary>Batch-wide: force a password change at next logon for every created user (off by default).</summary>
    [ObservableProperty] private bool _requirePasswordChange;

    // Batch-wide Entra Connect sync (used only when at least one row needs cloud).
    [ObservableProperty] private string _entraConnectServer = string.Empty;
    [ObservableProperty] private bool _syncSpecifyCredentials;
    [ObservableProperty] private string _syncUsername = string.Empty;
    /// <summary>Sync-account password, set from the PasswordBox code-behind (PasswordBox can't be bound).</summary>
    public string SyncPassword { get; set; } = string.Empty;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canRun = true;
    [ObservableProperty] private bool _canRetryCloud;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private int _done;
    [ObservableProperty] private int _total;

    public string ProgressText => $"{Done} of {Total} created";

    /// <summary>Raised after the batch runs so a non-modal host can refresh its list.</summary>
    public event Action? UsersCreated;

    // Captured for a cloud retry after a sync failure (accounts already exist; never recreated).
    private IReadOnlyList<BulkCreateRequest> _lastRequests = Array.Empty<BulkCreateRequest>();
    private IReadOnlyList<BulkCreateRowViewModel> _lastRows = Array.Empty<BulkCreateRowViewModel>();
    private BulkCloudOptions _lastCloud = new();
    private BulkCreateReport? _lastReport;

    public BulkCreateUsersViewModel(IDirectoryService directory, ITemplateStore store, IDialogService dialogs,
        IGraphService graph, BulkUserCreator creator, BulkUserCsvImporter importer, AppSettings settings)
    {
        _directory = directory;
        _store = store;
        _dialogs = dialogs;
        _graph = graph;
        _creator = creator;
        _importer = importer;
        _settings = settings;
        _entraConnectServer = settings.EntraConnectServer ?? string.Empty;
        ReloadTemplates();
    }

    public string? DefaultOu { private get; set; }

    partial void OnDoneChanged(int value) => OnPropertyChanged(nameof(ProgressText));
    partial void OnTotalChanged(int value) => OnPropertyChanged(nameof(ProgressText));

    public void ReloadTemplates()
    {
        var previous = SelectedTemplate?.Name;
        Templates.Clear();
        foreach (var t in _store.LoadAll()) Templates.Add(t);
        SelectedTemplate = Templates.FirstOrDefault(t => t.Name == previous) ?? Templates.FirstOrDefault();
    }

    partial void OnSelectedTemplateChanged(UserTemplate? value)
    {
        if (value is null) return;
        TargetOu = string.IsNullOrWhiteSpace(value.TargetOu) ? (DefaultOu ?? string.Empty) : value.TargetOu;
        UpnSuffix = value.UpnSuffix;
        foreach (var row in Rows) { row.Template = value; row.UpnSuffix = UpnSuffix; }
    }

    partial void OnUpnSuffixChanged(string value)
    {
        foreach (var row in Rows) row.UpnSuffix = value;
    }

    /// <summary>Opens the standard New User window (capture mode) to configure a new batch row.</summary>
    [RelayCommand]
    private void AddUser()
    {
        if (SelectedTemplate is null) { Status = "Select a template first."; return; }
        // Non-modal: the row is added via the callback when the operator commits the capture window.
        _dialogs.CaptureBatchUser(SelectedTemplate, string.IsNullOrWhiteSpace(TargetOu) ? DefaultOu : TargetOu, UpnSuffix,
            existing: null, onCaptured: row => Rows.Add(row));
    }

    /// <summary>Re-opens the New User window prefilled to edit an existing batch row.</summary>
    [RelayCommand]
    private void EditRow(BulkCreateRowViewModel? row)
    {
        if (row is null) return;
        // Re-find the index in the callback — the window is non-modal, so the list may have changed by then.
        _dialogs.CaptureBatchUser(SelectedTemplate, string.IsNullOrWhiteSpace(TargetOu) ? DefaultOu : TargetOu, UpnSuffix,
            existing: row, onCaptured: updated =>
            {
                var i = Rows.IndexOf(row);
                if (i >= 0) Rows[i] = updated; else Rows.Add(updated);
            });
    }

    [RelayCommand]
    private void RemoveRow(BulkCreateRowViewModel? row) { if (row is not null) Rows.Remove(row); }

    [RelayCommand]
    private void ClearRows() => Rows.Clear();

    [RelayCommand]
    private void BrowseOu()
    {
        var dn = _dialogs.PickContainer(TargetOu);
        if (dn is not null) TargetOu = dn;
    }

    [RelayCommand]
    private async Task ImportCsvAsync()
    {
        var path = _dialogs.PromptOpenFile("CSV files (*.csv)|*.csv|All files (*.*)|*.*");
        if (path is null) return;
        IsBusy = true;
        Status = "Importing…";
        try
        {
            var text = await File.ReadAllTextAsync(path);

            // Check the CSV is well-formed before importing anything.
            var check = BulkUserCsvImporter.ValidateFormat(text);
            if (!check.IsImportable)
            {
                _dialogs.Alert("CSV can’t be imported",
                    "This CSV has formatting problems:\n\n• " + string.Join("\n• ", check.Errors)
                    + "\n\nUse “Download CSV template” for the expected layout.");
                Status = "Import cancelled — CSV format problem.";
                return;
            }

            var imported = await _importer.ImportAsync(text);
            var warnings = new List<string>(check.Warnings);
            var n = 0;
            var defaultOu = string.IsNullOrWhiteSpace(TargetOu) ? DefaultOu : TargetOu;
            foreach (var src in imported)
            {
                Rows.Add(BulkCreateRowViewModel.FromImport(src, SelectedTemplate, UpnSuffix, defaultOu));
                n++;
                var who = $"{src.FirstName} {src.LastName}".Trim();
                warnings.AddRange(src.Warnings.Select(w => (who.Length > 0 ? who + " — " : string.Empty) + w));
            }
            Status = $"Imported {n} row(s)." + (warnings.Count > 0 ? $" {warnings.Count} warning(s)." : string.Empty);
            if (warnings.Count > 0) _dialogs.Alert("Import warnings", string.Join(Environment.NewLine, warnings));
        }
        catch (Exception ex) { _dialogs.Alert("Import failed", DirectoryService.Friendly(ex)); Status = "Import failed."; }
        finally { IsBusy = false; }
    }

    /// <summary>Saves a sample CSV showing the expected columns/format, so the operator can fill it in and re-import.</summary>
    [RelayCommand]
    private void DownloadTemplate()
    {
        var path = _dialogs.PromptSaveFile("CSV files (*.csv)|*.csv|All files (*.*)|*.*", "bulk-create-users-template.csv");
        if (path is null) return;
        try
        {
            File.WriteAllText(path, BulkUserCsvImporter.TemplateCsv());
            Status = $"Template saved to {path}.";
        }
        catch (Exception ex) { _dialogs.Alert("Couldn’t save template", DirectoryService.Friendly(ex)); }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (SelectedTemplate is null) { Status = "Select a template first."; return; }

        var rows = Rows.Where(r => !r.IsEmpty).ToList();
        if (rows.Count == 0) { Status = "Add at least one user (or import a CSV)."; return; }

        var problems = new List<string>();
        var built = new List<(BulkCreateRowViewModel Row, BulkCreateRequest Request)>();
        var samSeen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var anyCloud = false;

        var idx = 0;
        foreach (var row in rows)
        {
            idx++;
            var baseInput = row.ToInput() with { Template = row.Template ?? SelectedTemplate! };
            var sam = UserAttributeBuilder.ComputeSam(baseInput);
            if (string.IsNullOrWhiteSpace(sam)) { problems.Add($"Row {idx}: can’t derive a logon name (sAMAccountName)."); continue; }
            samSeen[sam] = samSeen.GetValueOrDefault(sam) + 1;

            var sugg = UserAttributeBuilder.Suggest(baseInput);
            var input = baseInput with
            {
                Email = string.IsNullOrWhiteSpace(baseInput.Email) ? sugg.Email : baseInput.Email,
                Upn = string.IsNullOrWhiteSpace(baseInput.Upn) ? sugg.Upn : baseInput.Upn,
                ProxyAddressesText = string.IsNullOrWhiteSpace(baseInput.ProxyAddressesText) ? sugg.ProxyText : baseInput.ProxyAddressesText,
            };
            var b = UserAttributeBuilder.Build(input);
            var attrs = new Dictionary<string, string>(b.Attributes, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in row.AttributeOverrides) if (!string.IsNullOrWhiteSpace(v)) attrs[k] = v;

            if (!attrs.TryGetValue("cn", out var cn) || string.IsNullOrWhiteSpace(cn))
            { problems.Add($"Row {idx} ({sam}): can’t derive a name (cn)."); continue; }

            var ou = string.IsNullOrWhiteSpace(row.TargetOu) ? TargetOu.Trim() : row.TargetOu.Trim();
            if (string.IsNullOrWhiteSpace(ou)) { problems.Add($"Row {idx} ({sam}): no target OU set."); continue; }

            var upn = attrs.TryGetValue("userPrincipalName", out var u) ? u : null;
            var cloudGroups = row.CloudGroups.ToList();
            var needsCloud = cloudGroups.Count > 0 || row.IssueTap;
            if (needsCloud)
            {
                anyCloud = true;
                if (string.IsNullOrWhiteSpace(upn)) problems.Add($"Row {idx} ({sam}): a routable UPN is required for cloud groups / a Temporary Access Pass.");
                if (row.IssueTap && (row.TapLifetimeMinutes < 10 || row.TapLifetimeMinutes > 43200))
                    problems.Add($"Row {idx} ({sam}): Temporary Access Pass lifetime must be 10–43200 minutes.");
            }

            built.Add((row, new BulkCreateRequest
            {
                Label = cn,
                TargetOu = ou,
                Attributes = attrs,
                Proxies = b.Proxies,
                OnPremGroupDns = row.OnPremGroupDns,
                CloudGroups = cloudGroups,
                IssueTap = row.IssueTap,
                TapLifetimeMinutes = row.TapLifetimeMinutes,
                TapOneTimeUse = row.TapOneTimeUse,
                Enabled = row.Enabled,
                MustChangePassword = RequirePasswordChange,
                Upn = upn,
            }));
        }

        foreach (var dup in samSeen.Where(kv => kv.Value > 1))
            problems.Add($"Duplicate logon name “{dup.Key}” on {dup.Value} rows — each must be unique.");

        // Verify every logon name against the directory before proceeding (none may already exist).
        if (built.Count > 0)
        {
            Status = "Checking logon names…";
            try
            {
                var existing = await _directory.FindExistingSamAccountNamesAsync(
                    built.Select(x => x.Request.Attributes["sAMAccountName"]));
                foreach (var s in built.Select(x => x.Request.Attributes["sAMAccountName"]).Where(existing.Contains).Distinct(StringComparer.OrdinalIgnoreCase))
                    problems.Add($"Logon name “{s}” already exists in the directory — choose another.");
            }
            catch (Exception ex)
            {
                problems.Add("Couldn't verify logon names against the directory: " + DirectoryService.Friendly(ex)
                    + ". Fix the connection and try again.");
            }
        }

        if (anyCloud)
        {
            if (!_graph.IsSignedIn) problems.Add("Sign in to Entra ID (File ▸ Settings ▸ Cloud) before a batch with cloud groups / a Temporary Access Pass.");
            if (string.IsNullOrWhiteSpace(EntraConnectServer)) problems.Add("Enter the Entra Connect server for the post-create delta sync.");
            if (SyncSpecifyCredentials && string.IsNullOrWhiteSpace(SyncUsername)) problems.Add("Enter the sync-account username, or clear “Use a specific account”.");
        }

        if (problems.Count > 0) { _dialogs.Alert("Fix these before creating", string.Join(Environment.NewLine, problems)); Status = "Validation failed — see the list."; return; }

        var lines = new List<string> { $"Create {built.Count} user(s):", string.Empty };
        lines.AddRange(built.Select(x => $"• {x.Request.Attributes["sAMAccountName"]} — {x.Request.Label}  →  {NameResolver.RdnFallback(x.Request.TargetOu)}"));
        if (anyCloud)
        {
            lines.Add(string.Empty);
            lines.Add($"Then run ONE Entra Connect delta sync on {EntraConnectServer}, wait for the users to appear in Entra, and apply per-row cloud groups / Temporary Access Passes.");
        }
        lines.Add(string.Empty);
        lines.Add("A unique password is generated per user (shown in the post-run report). "
            + (RequirePasswordChange ? "Users must change it at next logon." : "Users are NOT forced to change it at next logon."));
        if (!_dialogs.Confirm("Bulk create users", $"Create {built.Count} user(s)?", lines)) return;

        _lastRequests = built.Select(x => x.Request).ToList();
        _lastRows = built.Select(x => x.Row).ToList();
        _lastCloud = new BulkCloudOptions
        {
            EntraConnectServer = EntraConnectServer.Trim(),
            SpecifyCredentials = SyncSpecifyCredentials,
            Username = SyncUsername.Trim(),
            Password = SyncPassword,
        };

        IsBusy = true;
        CanRun = false;
        CanRetryCloud = false;
        ShowProgress = true;
        ProgressSteps.Clear();
        Done = 0;
        Total = _lastRequests.Count;
        var live = new Progress<string>(s => { ProgressSteps.Add(s); Status = s; });
        var prog = new Progress<int>(n => Done = n);
        try
        {
            _lastReport = await _creator.RunAsync(_lastRequests, _lastCloud, prog, live);
            ApplyResultsToRows();
            UsersCreated?.Invoke();
            EvaluateRetry();
            Status = $"Done — {_lastReport.SuccessCount} created, {_lastReport.FailureCount} failed.";
            _dialogs.ShowBulkCreateReport(_lastReport);
        }
        catch (Exception ex)
        {
            Status = DirectoryService.Friendly(ex);
            ProgressSteps.Add("✗ " + Status);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RetryCloudAsync()
    {
        if (_lastReport is null) return;
        IsBusy = true;
        CanRetryCloud = false;
        var live = new Progress<string>(s => { ProgressSteps.Add(s); Status = s; });
        try
        {
            ProgressSteps.Add("• Retrying cloud steps…");
            await _creator.RunCloudPhasesAsync(_lastRequests, _lastReport.Items, _lastCloud, live);
            ApplyResultsToRows();
            EvaluateRetry();
            _dialogs.ShowBulkCreateReport(_lastReport);
        }
        catch (Exception ex) { Status = DirectoryService.Friendly(ex); ProgressSteps.Add("✗ " + Status); CanRetryCloud = true; }
        finally { IsBusy = false; }
    }

    private void ApplyResultsToRows()
    {
        if (_lastReport is null) return;
        for (var i = 0; i < _lastRows.Count && i < _lastReport.Items.Count; i++)
        {
            var row = _lastRows[i];
            var res = _lastReport.Items[i];
            row.Status = res.Success
                ? (res.PasswordSet ? "✓ Created" : "⚠ Created (password not set)") + (string.IsNullOrWhiteSpace(res.CloudSummary) ? string.Empty : " — " + res.CloudSummary)
                : "✗ " + res.Error;
            row.GeneratedPassword = res.GeneratedPassword;
            row.TapCode = res.TapCode ?? string.Empty;
        }
    }

    private void EvaluateRetry()
    {
        CanRetryCloud = _lastReport is not null && _lastRequests
            .Where((r, i) => r.NeedsCloud && _lastReport.Items[i].Success && !_lastReport.Items[i].CloudApplied)
            .Any();
    }
}
