using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

public enum EditPaneDock { Right, Bottom }

/// <summary>Root view model for the main window: tree, object list, and the editable detail pane.</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IDirectoryService _directory;
    private readonly IDialogService _dialogs;
    private readonly IScenarioStore _scenarios;
    private readonly ISettingsStore _settingsStore;
    private readonly IGraphService _graph;
    private readonly ICredentialStore _credentials;
    private TreeNodeViewModel? _cloudRoot;
    private bool _scenarioRunning; // guards against a second (fire-and-forget) scenario run overlapping

    public AppSettings Settings { get; }

    public ObservableCollection<TreeNodeViewModel> RootNodes { get; } = new();
    public ObjectListViewModel List { get; }
    public EditPaneViewModel Edit { get; }

    /// <summary>The Entra ID (cloud) object list, shown when a cloud tree node is selected.</summary>
    public CloudObjectListViewModel Cloud { get; }

    /// <summary>True when a cloud tree node is selected (show the cloud list instead of the on-prem panes).</summary>
    [ObservableProperty] private bool _isCloudView;
    public bool IsAdView => !IsCloudView;

    /// <summary>Saved scenarios, each carrying a command that runs it on the current selection.</summary>
    public ObservableCollection<ScenarioMenuItem> ScenarioMenuItems { get; } = new();

    [ObservableProperty] private TreeNodeViewModel? _selectedNode;
    [ObservableProperty] private string _connectedInfo = string.Empty;
    [ObservableProperty] private string _statusMessage = "Ready.";
    [ObservableProperty] private EditPaneDock _editDock = EditPaneDock.Right;

    /// <summary>True once bound to on-prem AD. The app runs without it (cloud-only); a warning bar shows when false.</summary>
    [ObservableProperty] private bool _adConnected;

    /// <summary>Non-empty drives the yellow "not connected to on-prem AD" warning bar; cleared once connected.</summary>
    [ObservableProperty] private string _adWarning = string.Empty;

    // Selection-driven flags so the right-click menu can show only the relevant account actions.
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private bool _selectionHasDisabled;
    [ObservableProperty] private bool _selectionHasEnabled;
    [ObservableProperty] private bool _selectionHasUsers;
    [ObservableProperty] private bool _hasScenarios;

    public bool EditDockRight => EditDock == EditPaneDock.Right;
    public bool EditDockBottom => EditDock == EditPaneDock.Bottom;

    public MainViewModel(IDirectoryService directory, IDialogService dialogs, IScenarioStore scenarios,
        ISettingsStore settingsStore, AppSettings settings, IGraphService graph,
        ICredentialStore credentials)
    {
        _directory = directory;
        _dialogs = dialogs;
        _scenarios = scenarios;
        _settingsStore = settingsStore;
        _graph = graph;
        _credentials = credentials;
        Settings = settings;
        _editDock = Enum.TryParse<EditPaneDock>(settings.EditDock, out var dock) ? dock : EditPaneDock.Right;

        List = new ObjectListViewModel(directory, SetError, settingsStore, settings);
        Edit = new EditPaneViewModel(directory, dialogs, SetError, graph);
        Cloud = new CloudObjectListViewModel(graph, dialogs, settingsStore, settings);

        List.SelectionChanged += (_, row) =>
        {
            UpdateSelectionState();
            if (row is not null) _ = Edit.LoadAsync(row.DistinguishedName, row.Type);
            else Edit.Clear();
        };

        ReloadScenarios();

        // Double-click a row → open it in its own editor window; refresh the list when it changes.
        List.OpenRequested += (_, row) =>
            _dialogs.OpenObjectEditor(row.DistinguishedName, row.Type, row.Name, () => _ = List.ReloadAsync());

        // Double-click a cloud row → open its read-only properties window.
        Cloud.OpenRequested += (_, row) => _dialogs.ShowCloudObjectProperties(row);

        // When the edit pane commits a change (disable, attribute edit, group/member change),
        // refresh the list so it reflects the change (e.g. the greyed-out disabled state).
        Edit.ObjectChanged += () => _ = List.ReloadAsync();
    }

    /// <summary>
    /// On launch: attempt a silent on-prem connection from the saved profile + stored credentials, then
    /// build the UI. A failure (or no saved connection) is non-fatal — the app continues in cloud-only
    /// mode with a warning bar; the user can connect later via Settings.
    /// </summary>
    public async Task StartupAsync()
    {
        await TryAutoConnectAsync();
        Initialize();
    }

    /// <summary>Best-effort silent reconnect using the last successful profile and a saved credential.</summary>
    private async Task TryAutoConnectAsync()
    {
        var s = Settings;
        if (string.IsNullOrWhiteSpace(s.LastDomainFqdn) || string.IsNullOrWhiteSpace(s.LastPrimaryDc))
            return; // never connected before — nothing to retry
        try
        {
            var cred = _credentials.TryLoad(s.LastDomainFqdn!);
            if (cred is null) return; // no saved password — can't connect without prompting

            var profile = new ConnectionProfile
            {
                DomainFqdn = s.LastDomainFqdn!,
                PrimaryDc = s.LastPrimaryDc!,
                FallbackDcs = new List<string>(s.LastFallbackDcs),
                UseLdaps = s.LastUseLdaps,
                Username = cred.Username,
                SaveCredentials = true,
            };
            StatusMessage = $"Connecting to {profile.DomainFqdn}…";
            await _directory.ConnectAsync(profile, cred.Password);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("Automatic on-prem connection on startup failed: " + ex.Message);
        }
    }

    /// <summary>Builds the tree. Works whether or not on-prem AD is connected (cloud-only is supported).</summary>
    public void Initialize()
    {
        RootNodes.Clear();
        _cloudRoot = null;

        if (_directory.IsConnected)
        {
            var root = new TreeNodeViewModel(_directory.GetRootNode(), _directory, SetError);
            RootNodes.Add(root);
            root.IsExpanded = true;
            SelectedNode = root;
            EnsureCloudRoot();

            var c = _directory.Current!;
            ConnectedInfo = $"Connected to {c.Server}  •  {c.DomainFqdn}  •  {c.DefaultNamingContext}";
            AdConnected = true;
            AdWarning = string.Empty;
            StatusMessage = "Ready.";
        }
        else
        {
            EnsureCloudRoot();
            // With no on-prem tree, land on the cloud section if signed in so the app is immediately usable.
            SelectedNode = _cloudRoot;
            ConnectedInfo = "Not connected to on-prem AD";
            AdConnected = false;
            AdWarning = "Not connected to on-prem Active Directory. On-prem features are unavailable until you connect. " +
                        (_graph.IsSignedIn ? "Cloud (Entra ID) management is available." : string.Empty);
            StatusMessage = "Not connected to on-prem AD.";
        }
    }

    /// <summary>Adds (or removes) the "Entra ID (cloud)" tree root based on the current sign-in state.</summary>
    public void EnsureCloudRoot()
    {
        if (_cloudRoot is not null) { RootNodes.Remove(_cloudRoot); _cloudRoot = null; }
        if (!_graph.IsSignedIn) return;

        var children = new[]
        {
            new TreeNodeViewModel(CloudNodeKind.Users, "Users", _directory, SetError),
            new TreeNodeViewModel(CloudNodeKind.Groups, "Groups", _directory, SetError),
            new TreeNodeViewModel(CloudNodeKind.Devices, "Devices", _directory, SetError),
        };
        _cloudRoot = new TreeNodeViewModel(CloudNodeKind.Tenant, "Entra ID (cloud)", _directory, SetError, children)
        {
            IsExpanded = true,
        };
        RootNodes.Add(_cloudRoot);
    }

    partial void OnIsCloudViewChanged(bool value) => OnPropertyChanged(nameof(IsAdView));

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value)
    {
        if (value is null) return;

        if (value.CloudKind is { } kind)
        {
            IsCloudView = true;
            switch (kind)
            {
                case CloudNodeKind.Groups: _ = Cloud.LoadAsync(CloudListMode.Groups); break;
                case CloudNodeKind.Devices: _ = Cloud.LoadAsync(CloudListMode.Devices); break;
                default: _ = Cloud.LoadAsync(CloudListMode.Users); break; // Users + Tenant root
            }
            return;
        }

        IsCloudView = false;
        if (value is { IsPlaceholder: false })
            _ = List.LoadContainerAsync(value.DistinguishedName);
    }

    partial void OnEditDockChanged(EditPaneDock value)
    {
        OnPropertyChanged(nameof(EditDockRight));
        OnPropertyChanged(nameof(EditDockBottom));
        Settings.EditDock = value.ToString();
        _settingsStore.Save(Settings);
    }

    /// <summary>Persists the current settings (called by the window when sizes change / on close).</summary>
    public void SaveSettings() => _settingsStore.Save(Settings);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsCloudView)
        {
            await Cloud.ReloadAsync();
            return;
        }

        if (SelectedNode is { IsPlaceholder: false } node)
        {
            node.Invalidate();
            await node.EnsureChildrenAsync();
        }
        await List.ReloadAsync();
    }

    [RelayCommand]
    private void NewUser() =>
        _dialogs.ShowNewUser(SelectedNode?.DistinguishedName, onCreated: () => _ = List.ReloadAsync());

    [RelayCommand]
    private void BulkCreateUsers() =>
        _dialogs.ShowBulkCreateUsers(SelectedNode?.DistinguishedName, onCreated: () => _ = List.ReloadAsync());

    [RelayCommand]
    private void ManageTemplates() => _dialogs.ShowTemplateEditor();

    [RelayCommand]
    private void OpenSettings()
    {
        _dialogs.ShowSettings(RefreshAfterReconnect);
        EnsureCloudRoot(); // a sign-in/out in Settings may have added/removed the cloud section
    }

    /// <summary>Rebinds the UI to the (possibly new) connection after a reconnect from the Settings dialog.</summary>
    private void RefreshAfterReconnect()
    {
        try
        {
            Initialize();   // re-roots the tree; selecting the root triggers the list reload
            Edit.Clear();
            StatusMessage = ConnectedInfo;
        }
        catch (Exception ex) { SetError(DirectoryService.Friendly(ex)); }
    }

    [RelayCommand]
    private void AdvancedSearch()
    {
        var query = _dialogs.ShowAdvancedSearch(SelectedNode?.DistinguishedName ?? string.Empty);
        if (query is not null)
        {
            StatusMessage = "Showing advanced search results.";
            _ = List.LoadQueryAsync(query);
        }
    }

    [RelayCommand]
    private void BulkEdit()
    {
        var rows = List.SelectedRows.ToList();
        if (rows.Count == 0)
        {
            _dialogs.Alert("Bulk edit", "Select one or more objects in the list first.");
            return;
        }
        if (_dialogs.ShowBulkEdit(rows))
            _ = List.ReloadAsync();
    }

    [RelayCommand]
    private async Task AddSelectedToGroupsAsync()
    {
        var rows = List.SelectedRows.ToList();
        if (rows.Count == 0)
        {
            _dialogs.Alert("Add to groups", "Select one or more objects in the list first.");
            return;
        }

        var picked = _dialogs.PickGroupsHybrid("Add selected objects to groups");
        if (picked is null || picked.Count == 0) return;

        var onPrem = picked.Where(g => g.Origin == GroupOrigin.OnPrem && g.Dn is not null).ToList();
        var cloud = picked.Where(g => g.Origin == GroupOrigin.Cloud && g.CloudId is not null).ToList();

        var lines = new[] { $"Add {rows.Count} object(s) to {picked.Count} group(s):" }
            .Concat(picked.Select(g => $"• {g.OriginLabel}: {g.Name}"));
        if (!_dialogs.Confirm("Confirm", $"Add {rows.Count} object(s) to the selected group(s)?", lines))
            return;

        // On-prem groups: one batched membership write across all rows.
        BulkResult? onPremResult = null;
        if (onPrem.Count > 0)
        {
            var change = new PendingChange
            {
                Op = ChangeOp.AddToGroups,
                FriendlyName = string.Join(", ", onPrem.Select(g => g.Name)),
                Values = onPrem.Select(g => g.Dn!).ToList(),
            };
            StatusMessage = "Adding selected objects to on-prem groups…";
            onPremResult = await _directory.BulkApplyAsync(rows, new[] { change });
        }

        // No cloud groups: keep the original single-result behaviour.
        if (cloud.Count == 0)
        {
            if (onPremResult is not null) _dialogs.ShowBulkResult(onPremResult);
            await List.ReloadAsync();
            StatusMessage = "Ready.";
            return;
        }

        // Cloud groups: resolve each row's Entra twin, then add it to every picked cloud group.
        var onPremByDn = onPremResult?.Items.ToDictionary(i => i.DistinguishedName, StringComparer.OrdinalIgnoreCase);
        var combined = new List<BulkItemResult>();
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            StatusMessage = $"Adding to cloud groups… ({i + 1}/{rows.Count})";
            var errors = new List<string>();

            if (onPremByDn is not null && onPremByDn.TryGetValue(row.DistinguishedName, out var op) && !op.Success && op.Error is not null)
                errors.Add("On-prem: " + op.Error);

            var cloudId = await ResolveCloudIdForRowAsync(row);
            if (cloudId is null)
                errors.Add("Not found in Entra ID (may not be synced) — cloud groups skipped.");
            else
                foreach (var g in cloud)
                {
                    try { await _graph.AddMemberToGroupAsync(g.CloudId!, cloudId); }
                    catch (Exception ex) { errors.Add($"{g.Name}: {GraphErrors.Friendly(ex)}"); }
                }

            combined.Add(new BulkItemResult(row.DistinguishedName, row.Name, errors.Count == 0, errors.Count == 0 ? null : string.Join("; ", errors)));
        }

        _dialogs.ShowBulkResult(new BulkResult(combined));
        await List.ReloadAsync();
        StatusMessage = "Ready.";
    }

    /// <summary>Resolves an on-prem list row to its Entra (cloud) object id; null if not synced/found.</summary>
    private async Task<string?> ResolveCloudIdForRowAsync(AdObjectRow row)
    {
        try
        {
            switch (row.Type)
            {
                case AdObjectType.User:
                    var upn = row.Get("userPrincipalName");
                    if (string.IsNullOrWhiteSpace(upn)) upn = await LoadCorrelationAsync(row.DistinguishedName, "userPrincipalName", formatted: false);
                    return string.IsNullOrWhiteSpace(upn) ? null : (await _graph.GetUserByUpnAsync(upn))?.Id;
                case AdObjectType.Computer:
                    return string.IsNullOrWhiteSpace(row.Name) ? null : (await _graph.GetDevicesByComputerAsync(row.Name, null)).FirstOrDefault()?.Id;
                case AdObjectType.Group:
                    var sid = await LoadCorrelationAsync(row.DistinguishedName, "objectSid", formatted: true); // formatted S-1-5-…
                    return string.IsNullOrWhiteSpace(sid) ? null : (await _graph.GetGroupByOnPremSidAsync(sid))?.Id;
                default:
                    return null;
            }
        }
        catch (Exception ex) { AppLog.Instance.Warn($"Cloud id resolution failed for {row.Name}: {ex.Message}"); return null; }
    }

    /// <summary>Loads one correlation attribute (raw or display-formatted) from an on-prem object; null if absent.</summary>
    private async Task<string?> LoadCorrelationAsync(string dn, string ldapName, bool formatted)
    {
        var attrs = await _directory.LoadObjectAsync(dn);
        var a = attrs.FirstOrDefault(x => string.Equals(x.LdapName, ldapName, StringComparison.OrdinalIgnoreCase));
        if (a is null) return null;
        var vals = formatted ? a.DisplayValues : a.RawValues;
        return vals.Count > 0 ? vals[0] : null;
    }

    // --- Right-click / context actions (operate on the current selection) ---

    [RelayCommand] private Task EnableSelectedAsync() => SetEnabledSelectedAsync(true);
    [RelayCommand] private Task DisableSelectedAsync() => SetEnabledSelectedAsync(false);

    private async Task SetEnabledSelectedAsync(bool enable)
    {
        var verb = enable ? "Enable" : "Disable";
        var rows = SelectedRowsOrSingle()
            .Where(r => r.Type is AdObjectType.User or AdObjectType.Computer).ToList();
        if (rows.Count == 0) { _dialogs.Alert(verb, "Select one or more user or computer accounts first."); return; }

        var lines = new[] { $"{verb} {rows.Count} account(s):" }.Concat(rows.Select(r => "• " + r.Name));
        if (!_dialogs.Confirm(verb, $"{verb} {rows.Count} account(s)?", lines)) return;

        StatusMessage = $"{verb} {rows.Count} account(s)…";
        var result = await _directory.BulkApplyAsync(rows, new[] { new PendingChange { Op = enable ? ChangeOp.Enable : ChangeOp.Disable } });
        await List.ReloadAsync();
        ReportBulk(result, enable ? "Enabled" : "Disabled");
    }

    [RelayCommand]
    private async Task UnlockSelectedAsync()
    {
        var rows = SelectedRowsOrSingle().Where(r => r.Type == AdObjectType.User).ToList();
        if (rows.Count == 0) { _dialogs.Alert("Unlock", "Select one or more user accounts first."); return; }
        if (!_dialogs.Confirm("Unlock", $"Unlock {rows.Count} account(s)?", rows.Select(r => "• " + r.Name))) return;

        var ok = 0;
        var errors = new List<string>();
        foreach (var r in rows)
        {
            try { await _directory.UnlockAccountAsync(r.DistinguishedName); ok++; }
            catch (Exception ex) { errors.Add($"{r.Name}: {DirectoryService.Friendly(ex)}"); }
        }
        await List.ReloadAsync();
        if (errors.Count > 0) _dialogs.Alert("Unlock", $"Unlocked {ok}; {errors.Count} failed:\n" + string.Join("\n", errors));
        else StatusMessage = $"Unlocked {ok} account(s).";
    }

    [RelayCommand]
    private async Task MoveSelectedToOuAsync()
    {
        var rows = SelectedRowsOrSingle();
        if (rows.Count == 0) { _dialogs.Alert("Move", "Select one or more objects first."); return; }

        var target = _dialogs.PickContainer(DirectoryService.ParentDn(rows[0].DistinguishedName));
        if (target is null) return;
        await MoveRowsToOuAsync(rows, target);
    }

    /// <summary>Moves the given rows into <paramref name="targetOuDn"/> (used by the command and by tree drag-and-drop).</summary>
    public async Task MoveRowsToOuAsync(IReadOnlyList<AdObjectRow> rows, string targetOuDn)
    {
        // Skip anything already directly under the target so a no-op move can't error.
        var toMove = rows.Where(r => !string.Equals(
            DirectoryService.ParentDn(r.DistinguishedName), targetOuDn, StringComparison.OrdinalIgnoreCase)).ToList();
        if (toMove.Count == 0) { _dialogs.Alert("Move", "The selected object(s) are already in that OU."); return; }

        var lines = new[] { $"Move {toMove.Count} object(s) to:", "• " + targetOuDn, string.Empty }
            .Concat(toMove.Select(r => $"   {r.Type}: {r.Name}"));
        if (!_dialogs.Confirm("Move objects", $"Move {toMove.Count} object(s) to another OU?", lines)) return;

        var ok = 0;
        var errors = new List<string>();
        foreach (var r in toMove)
        {
            try { await _directory.MoveObjectAsync(r.DistinguishedName, targetOuDn); ok++; }
            catch (Exception ex) { errors.Add($"{r.Name}: {DirectoryService.Friendly(ex)}"); }
        }
        Edit.Clear();
        await List.ReloadAsync();
        if (errors.Count > 0) _dialogs.Alert("Move", $"Moved {ok}; {errors.Count} failed:\n" + string.Join("\n", errors));
        else StatusMessage = $"Moved {ok} object(s).";
    }

    // --- Scenarios ---

    [RelayCommand]
    private void ManageScenarios() => _dialogs.ShowScenarioEditor(ReloadScenarios);

    /// <summary>Rebuilds the runnable-scenario menu items from the store (called at startup and after edits).</summary>
    public void ReloadScenarios()
    {
        ScenarioMenuItems.Clear();
        foreach (var s in _scenarios.LoadAll()) ScenarioMenuItems.Add(new ScenarioMenuItem(s, this));
        HasScenarios = ScenarioMenuItems.Count > 0;
    }

    /// <summary>Runs a scenario against the current selection (invoked by a scenario menu item).</summary>
    public async Task RunScenarioAsync(Scenario scenario)
    {
        if (_scenarioRunning) return;
        var rows = SelectedRowsOrSingle();
        if (rows.Count == 0) { _dialogs.Alert(scenario.Name, "Select one or more objects to run this scenario on."); return; }

        var lines = new List<string> { "Steps (run in order):" };
        lines.AddRange(scenario.Steps.Select((s, i) => $"   {i + 1}. {DescribeStep(s)}"));

        // Call out the consequences that aren't obvious from the step list — especially in a hybrid tenant.
        var cautions = new List<string>();
        if (scenario.Steps.Any(s => s.Action == ScenarioActionType.RemoveAllGroups))
            cautions.Add("• Removes ALL on-prem group memberships — they are NOT recorded, so they can't be auto-restored.");
        if (scenario.Steps.Any(s => s.Action == ScenarioActionType.CloudRemoveAllGroups))
            cautions.Add("• Removes ALL cloud (Entra) group memberships — not recorded/auto-restorable; dynamic and on-prem-synced groups are skipped.");
        if (scenario.Steps.Any(s => s.Action == ScenarioActionType.MoveToOu && !string.IsNullOrWhiteSpace(s.TargetOu)))
            cautions.Add("• Moves the object. On a directory-synced (hybrid) object, the next Entra Connect sync may "
                       + "disable or SOFT-DELETE the cloud user and mailbox (recoverable for ~30 days).");
        if (scenario.Steps.Any(s => s.Action is ScenarioActionType.CloudDisableAccount or ScenarioActionType.CloudEnableAccount
                or ScenarioActionType.CloudRevokeSessions or ScenarioActionType.CloudAddToGroups or ScenarioActionType.CloudRemoveFromGroups
                or ScenarioActionType.CloudRemoveAllGroups))
            cautions.Add("• Includes Entra ID (cloud) steps — requires being signed in to Entra ID; they act on each "
                       + "object's synced cloud twin (skipped per-object if no cloud match is found).");
        if (cautions.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("⚠ Caution:");
            lines.AddRange(cautions);
        }

        // Does this scenario ask for an operation log? If so, remind the operator where it will be saved.
        var wantsLog = scenario.Steps.Any(s => s.Action == ScenarioActionType.SaveOperationLog);
        var defaultLogDir = OperationLog.ResolveDirectory(Settings);
        var defaultLogName = $"{OperationLog.SafeFileNamePart(scenario.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}.log";
        if (wantsLog)
        {
            lines.Add(string.Empty);
            lines.Add("📝 An operation log of the steps taken and changes made will be saved to:");
            lines.Add($"   {Path.Combine(defaultLogDir, defaultLogName)}");
            lines.Add("   (You can change the location after confirming.)");
        }

        lines.Add(string.Empty);
        lines.Add($"Targets ({rows.Count}):");
        lines.AddRange(rows.Select(r => $"   {r.Type}: {r.Name}"));

        // A scenario can be destructive and hard to undo at scale, so running it on more than one object
        // requires typing the object count (the same safeguard as bulk delete); a single object is a plain confirm.
        var title = $"Run scenario: {scenario.Name}";
        var heading = $"Run “{scenario.Name}” on {rows.Count} object(s)?";
        var approved = rows.Count == 1
            ? _dialogs.Confirm(title, heading, lines)
            : _dialogs.ConfirmWithPhrase(title, heading + "  This may be hard to undo.", lines, rows.Count.ToString());
        if (!approved) return;

        // Let the operator override the log destination (default = the global folder + a timestamped name).
        string? logPath = null;
        if (wantsLog)
        {
            logPath = _dialogs.PromptSaveFile(
                "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                defaultLogName, defaultLogDir);
            if (logPath is null &&
                !_dialogs.Confirm(title, "No log location was chosen — run the scenario without saving a log?",
                    new[] { "The scenario will still run; just no log file will be written." }))
                return;
        }

        _scenarioRunning = true;
        try
        {
            StatusMessage = $"Running “{scenario.Name}”…";
            var operationLog = logPath is not null ? new List<string>() : null;
            // The progress window runs the scenario and streams each step live; it returns once the operator closes it.
            var result = _dialogs.ShowScenarioRun(scenario, rows, operationLog);
            Edit.Clear();
            await List.ReloadAsync();

            var savedTo = logPath is not null && operationLog is not null
                ? WriteOperationLog(logPath, scenario, rows, result, operationLog)
                : null;

            StatusMessage = $"Scenario “{scenario.Name}”: {result.SuccessCount} ok, {result.FailureCount} failed."
                          + (savedTo is not null ? $"  Log saved to {savedTo}." : string.Empty);
        }
        finally { _scenarioRunning = false; }
    }

    /// <summary>Writes the run's operation log (header + per-target steps/changes + summary) to <paramref name="path"/>.
    /// Returns the path on success; surfaces (but doesn't rethrow) a write failure so the run still reports its result.</summary>
    private string? WriteOperationLog(string path, Scenario scenario, IReadOnlyList<AdObjectRow> targets, BulkResult result, IReadOnlyList<string> body)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Unified Directory Manager — Operation Log");
            sb.AppendLine(new string('=', 64));
            sb.AppendLine($"Scenario : {scenario.Name}");
            if (!string.IsNullOrWhiteSpace(scenario.Description)) sb.AppendLine($"Purpose  : {scenario.Description}");
            sb.AppendLine($"Run by   : {_directory.Current?.Username ?? "(unknown)"}");
            sb.AppendLine($"Run at   : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Targets  : {targets.Count}");
            sb.AppendLine();
            foreach (var line in body) sb.AppendLine(line);
            sb.AppendLine(new string('-', 64));
            sb.AppendLine($"Summary  : {result.SuccessCount} succeeded, {result.FailureCount} failed.");

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString());
            AppLog.Instance.Info($"Saved scenario operation log to {path}.");
            return path;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("Failed to write scenario operation log.", ex);
            _dialogs.Alert("Operation log",
                $"The scenario ran, but its log couldn't be saved to:\n{path}\n\n{ex.Message}");
            return null;
        }
    }

    private static string DescribeStep(ScenarioStep step) => step.Action switch
    {
        ScenarioActionType.Disable => "Disable account",
        ScenarioActionType.Enable => "Enable account",
        ScenarioActionType.Unlock => "Unlock account",
        ScenarioActionType.RemoveAllGroups => "Remove all group memberships",
        ScenarioActionType.AddToGroups => $"Add to {step.GroupDns.Count} group(s)",
        ScenarioActionType.RemoveFromGroups => $"Remove from {step.GroupDns.Count} group(s)",
        ScenarioActionType.SetAttribute => $"Set {step.Attribute} = {step.Value}",
        ScenarioActionType.ClearAttribute => $"Clear {step.Attribute}",
        ScenarioActionType.SetDescription => $"Set description = {step.Value}",
        ScenarioActionType.MoveToOu => $"Move to {step.TargetOu}",
        ScenarioActionType.CloudDisableAccount => "Cloud: disable account",
        ScenarioActionType.CloudEnableAccount => "Cloud: enable account",
        ScenarioActionType.CloudRevokeSessions => "Cloud: revoke sign-in sessions",
        ScenarioActionType.CloudAddToGroups => $"Cloud: add to {step.CloudGroups.Count} group(s)",
        ScenarioActionType.CloudRemoveFromGroups => $"Cloud: remove from {step.CloudGroups.Count} group(s)",
        ScenarioActionType.CloudRemoveAllGroups => "Cloud: remove from all groups",
        _ => step.Action.ToString(),
    };

    [RelayCommand]
    private void SaveSelectedAsTemplate()
    {
        var user = SelectedRowsOrSingle().FirstOrDefault(r => r.Type == AdObjectType.User);
        if (user is null) { _dialogs.Alert("Save as template", "Select a user to copy into a new template."); return; }
        _dialogs.ShowCopyUserToTemplate(user.DistinguishedName);
    }

    [RelayCommand]
    private void CopyUser()
    {
        var user = SelectedRowsOrSingle().FirstOrDefault(r => r.Type == AdObjectType.User);
        if (user is null) { _dialogs.Alert("Copy user", "Select a user to copy."); return; }
        _dialogs.ShowCopyUser(user.DistinguishedName, onCreated: () => _ = List.ReloadAsync());
    }

    private void UpdateSelectionState()
    {
        var rows = List.SelectedRows;
        HasSelection = rows.Count > 0;
        SelectionHasDisabled = rows.Any(r => r.Type is AdObjectType.User or AdObjectType.Computer && r.IsDisabled);
        SelectionHasEnabled = rows.Any(r => r.Type is AdObjectType.User or AdObjectType.Computer && !r.IsDisabled);
        SelectionHasUsers = rows.Any(r => r.Type == AdObjectType.User);
    }

    private List<AdObjectRow> SelectedRowsOrSingle() =>
        List.SelectedRows.Count > 0
            ? List.SelectedRows.ToList()
            : (List.SelectedRow is { } single ? new List<AdObjectRow> { single } : new List<AdObjectRow>());

    private void ReportBulk(BulkResult result, string pastVerb)
    {
        if (result.FailureCount > 0) _dialogs.ShowBulkResult(result);
        else StatusMessage = $"{pastVerb} {result.SuccessCount} object(s).";
    }

    [RelayCommand]
    private void ToggleDock() => EditDock = EditDock == EditPaneDock.Right ? EditPaneDock.Bottom : EditPaneDock.Right;

    [RelayCommand]
    private void OpenLogs()
    {
        try
        {
            var dir = AppLog.LogDirectory;
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
            else
                _dialogs.Alert("Logs", "No log directory is available.");
        }
        catch (Exception ex)
        {
            _dialogs.Alert("Logs", "Could not open the logs folder: " + ex.Message);
        }
    }

    [RelayCommand]
    private void ExportCsv()
    {
        if (List.Rows.Count == 0)
        {
            _dialogs.Alert("Export", "There is nothing in the list to export.");
            return;
        }
        var path = _dialogs.PromptSaveFile("CSV files (*.csv)|*.csv|All files (*.*)|*.*", "ad-export.csv");
        if (path is null) return;
        try
        {
            System.IO.File.WriteAllText(path, List.BuildCsv(), new System.Text.UTF8Encoding(true));
            AppLog.Instance.Info($"Exported {List.Rows.Count} row(s) to {path}.");
            _dialogs.Alert("Export", $"Exported {List.Rows.Count} object(s) to:\n{path}");
        }
        catch (Exception ex)
        {
            _dialogs.Alert("Export failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var rows = List.SelectedRows.Count > 0
            ? List.SelectedRows.ToList()
            : (List.SelectedRow is { } single ? new List<AdObjectRow> { single } : new List<AdObjectRow>());
        if (rows.Count == 0)
        {
            _dialogs.Alert("Delete", "Select one or more objects in the list first.");
            return;
        }

        var lines = new[] { "This is PERMANENT and cannot be undone:" }
            .Concat(rows.Select(r => $"• {r.Type}: {r.Name}  ({r.DistinguishedName})"));

        // Single delete: a plain confirm. Bulk delete: require typing the count as an extra safeguard.
        bool approved = rows.Count == 1
            ? _dialogs.Confirm("Delete object", "Delete this object?", lines)
            : _dialogs.ConfirmWithPhrase("Delete objects", $"Delete {rows.Count} objects? This is permanent.", lines, rows.Count.ToString());
        if (!approved)
            return;

        var ok = 0;
        var errors = new List<string>();
        foreach (var row in rows)
        {
            try { await _directory.DeleteObjectAsync(row.DistinguishedName); ok++; }
            catch (Exception ex) { errors.Add($"{row.Name}: {DirectoryService.Friendly(ex)}"); }
        }

        Edit.Clear();
        await List.ReloadAsync();

        if (errors.Count > 0)
            _dialogs.Alert("Delete", $"Deleted {ok}; {errors.Count} failed:\n" + string.Join("\n", errors));
        else
            StatusMessage = $"Deleted {ok} object(s).";
    }

    [RelayCommand]
    private void OpenSelected()
    {
        var row = List.SelectedRow;
        if (row is null) { _dialogs.Alert("Open", "Select an object in the list first."); return; }
        _dialogs.OpenObjectEditor(row.DistinguishedName, row.Type, row.Name, () => _ = List.ReloadAsync());
    }

    [RelayCommand]
    private void ViewLog() => _dialogs.ShowLogViewer();

    [RelayCommand]
    private void About() => _dialogs.ShowAbout();

    [RelayCommand]
    private void ViewReadme() => _dialogs.ShowReadme();

    [RelayCommand]
    private void EntraSync() => _dialogs.ShowEntraSync();

    [RelayCommand]
    private void Exit() => System.Windows.Application.Current.Shutdown();

    private void SetError(string message)
    {
        StatusMessage = message;
        AppLog.Instance.Warn(message);
    }
}

/// <summary>A saved scenario surfaced in menus: its name plus a command that runs it on the selection.</summary>
public sealed class ScenarioMenuItem
{
    public string Name { get; }
    public IRelayCommand RunCommand { get; }

    public ScenarioMenuItem(Scenario scenario, MainViewModel owner)
    {
        Name = scenario.Name;
        RunCommand = new RelayCommand(() => _ = owner.RunScenarioAsync(scenario));
    }
}
