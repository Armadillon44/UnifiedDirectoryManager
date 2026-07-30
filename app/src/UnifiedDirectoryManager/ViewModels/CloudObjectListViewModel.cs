using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>A named client-side filter for the cloud list (e.g. "Enabled", "Synced", "Compliant").</summary>
public sealed record CloudFilterOption(string Name, Func<CloudObjectRow, bool> Match);

/// <summary>
/// The cloud object list. Serves both cloud sections: Entra ID (Users / Groups / Devices, paged through
/// Microsoft Graph) and Exchange Online (Mailboxes / Distribution groups, read through the hosted
/// PowerShell channel). Offers a server-side name search, a client-side quick-filter + per-kind filter, a
/// runtime column chooser, checkbox multi-select and CSV export. Selecting a row drives the read-only
/// <see cref="Detail"/> pane; double-clicking raises <see cref="OpenRequested"/>.
///
/// The two backends page differently and that difference is deliberately visible: Graph hands back a
/// continuation link, while Exchange has none and is therefore capped, with the cap reported in
/// <see cref="Status"/> rather than passed off as a complete result.
/// </summary>
public partial class CloudObjectListViewModel : ObservableObject
{
    /// <summary>Rows fetched per Exchange list load. Exchange can't continue from a cursor, so this is a hard
    /// cap rather than a page size: narrowing with the search box is the way to see past it.</summary>
    private const int ExchangeListCap = 200;

    /// <summary>Higher cap for "Export all" — still bounded, because an unbounded sweep of a large tenant on
    /// one 180-second round trip would time out and take the shared host process down with it.</summary>
    private const int ExchangeExportCap = 5000;

    private readonly IGraphService _graph;
    private readonly IExchangeService _exchange;
    private readonly IDialogService _dialogs;
    private readonly ISettingsStore _settingsStore;
    private readonly AppSettings _settings;

    private string? _nextLink;
    private bool _columnsInitialized;
    private bool _suppressSelectAll;
    private bool _exchangeCapped;

    public ObservableCollection<ColumnDefinition> Columns { get; } = new();
    public ObservableCollection<CloudObjectRow> Rows { get; } = new();
    public ICollectionView RowsView { get; }
    public ObservableCollection<CloudFilterOption> FilterOptions { get; } = new();

    /// <summary>Read-only details of the selected row (backs the cloud properties pane).</summary>
    public CloudObjectDetailViewModel Detail { get; }

    [ObservableProperty] private CloudListMode _mode = CloudListMode.Users;
    [ObservableProperty] private string _header = "Entra ID";
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _quickFilter = string.Empty;
    [ObservableProperty] private CloudFilterOption? _selectedFilter;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private bool _selectAll;
    [ObservableProperty] private int _checkedCount;
    [ObservableProperty] private CloudObjectRow? _selectedRow;

    /// <summary>Raised when the visible column set changes so the view can rebuild GridView columns.</summary>
    public event EventHandler? ColumnsChanged;

    /// <summary>Raised when a row is activated (double-clicked) so the host can open a properties window.</summary>
    public event EventHandler<CloudObjectRow>? OpenRequested;

    /// <summary>
    /// Raises <see cref="OpenRequested"/> so the host opens a properties window — except in the Exchange lists,
    /// which have no Exchange-backed properties view yet. The window is driven entirely by Microsoft Graph: it
    /// would issue Graph reads that 403 for a distribution list and describe nothing for a mailbox, and it would
    /// offer Graph-backed member edits and saves that Exchange objects cannot accept. Say so instead.
    /// </summary>
    public void RequestOpen(CloudObjectRow row)
    {
        if (IsExchangeMode)
        {
            Status = "Properties for Exchange Online objects aren't available yet.";
            return;
        }
        OpenRequested?.Invoke(this, row);
    }

    public IReadOnlyList<CloudObjectRow> CheckedRows => Rows.Where(r => r.IsChecked).ToList();

    public CloudObjectListViewModel(IGraphService graph, IExchangeService exchange, IDialogService dialogs, ISettingsStore settingsStore, AppSettings settings)
    {
        _graph = graph;
        _exchange = exchange;
        _dialogs = dialogs;
        _settingsStore = settingsStore;
        _settings = settings;
        Detail = new CloudObjectDetailViewModel(graph, exchange, dialogs);

        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = RowPredicate;
    }

    partial void OnQuickFilterChanged(string value) => RowsView.Refresh();
    partial void OnSelectedFilterChanged(CloudFilterOption? value) => RowsView.Refresh();

    // The detail pane reads through Microsoft Graph, which can't describe a mailbox and returns 403 for a
    // distribution list. Leave it empty in the Exchange lists rather than showing a failure or a half-truth.
    partial void OnSelectedRowChanged(CloudObjectRow? value)
    {
        Detail.SetTarget(IsExchangeMode ? null : value);
        // SetTarget(null) restores the "select an object" prompt, which would tell an operator who just
        // selected a row to do the thing they did. Unconditional in Exchange mode: selecting nothing there
        // is no more actionable than selecting something, so the prompt is wrong either way.
        if (IsExchangeMode) Detail.EmptyHint = ExchangeDetailHint;
    }

    /// <summary>Explains the empty detail pane in the Exchange lists, where Graph-backed details don't apply.</summary>
    private const string ExchangeDetailHint = "Details for Exchange Online objects aren't available here yet.";
    partial void OnIsBusyChanged(bool value)
    {
        LoadMoreCommand.NotifyCanExecuteChanged();
        ExportAllCsvCommand.NotifyCanExecuteChanged();
        NotifyBulkCanExec();
    }
    partial void OnCheckedCountChanged(int value) => NotifyBulkCanExec();
    partial void OnModeChanged(CloudListMode value) { OnPropertyChanged(nameof(ShowUserActions)); NotifyBulkCanExec(); }

    /// <summary>The bulk user actions (Enable/Disable/Revoke) apply only in the Users list.</summary>
    public bool ShowUserActions => Mode == CloudListMode.Users;

    /// <summary>True for the two Exchange Online lists, which read through the PowerShell channel rather than
    /// Graph and are capped instead of paged.</summary>
    private bool IsExchangeMode => Mode is CloudListMode.Mailboxes or CloudListMode.DistributionGroups;

    partial void OnSelectAllChanged(bool value)
    {
        if (_suppressSelectAll) return;
        foreach (var row in Rows) row.IsChecked = value;
    }

    /// <summary>Switches the list to a mode and loads the first page.</summary>
    public async Task LoadAsync(CloudListMode mode)
    {
        Mode = mode;
        Header = mode switch
        {
            CloudListMode.Users => "Entra ID — Users",
            CloudListMode.Groups => "Entra ID — Groups",
            CloudListMode.Devices => "Entra ID — Devices",
            CloudListMode.Mailboxes => "Exchange Online — Mailboxes",
            CloudListMode.DistributionGroups => "Exchange Online — Distribution groups",
            _ => "Entra ID",
        };

        BuildColumns(mode);
        BuildFilterOptions(mode);
        SearchText = string.Empty;
        QuickFilter = string.Empty;
        // Set the pane's hint on the mode switch as well, so it is already right before the first selection.
        if (IsExchangeMode) Detail.EmptyHint = ExchangeDetailHint;
        await LoadFirstPageAsync();
    }

    [RelayCommand]
    private Task SearchAsync() => LoadFirstPageAsync();

    [RelayCommand]
    private Task RefreshAsync() => LoadFirstPageAsync();

    /// <summary>Reloads the current mode's first page (used by the main toolbar's view-aware Refresh).</summary>
    public Task ReloadAsync() => LoadFirstPageAsync();

    // Generation counter: each first-page load bumps it; an in-flight fetch whose token no longer matches
    // has been superseded (e.g. the user switched tree node Users→Groups) and must not touch shared state.
    private int _loadToken;

    private async Task LoadFirstPageAsync()
    {
        if (NotReady() is { } reason)
        {
            // Reset the paging state too, or "Load more" stays visible from the previous mode and one click
            // replaces this explanation with a bare "0 object(s)".
            Rows.Clear();
            SelectedRow = null;
            _nextLink = null;
            HasMore = false;
            _exchangeCapped = false;
            Status = reason;
            return;
        }

        var token = ++_loadToken;
        IsBusy = true;
        _suppressSelectAll = true; SelectAll = false; _suppressSelectAll = false;
        Rows.Clear();
        SelectedRow = null;
        _nextLink = null;
        // HasMore too: if this load throws (pwsh missing, module missing, consent, timeout — all routine for
        // the Exchange lists) the catch only sets Status, and a stale HasMore would leave "Load more" offering
        // pages that an empty, failed list does not have.
        HasMore = false;
        _exchangeCapped = false;
        Status = IsExchangeMode ? "Loading from Exchange Online…" : "Loading…";
        try
        {
            var (page, capped) = await FetchAsync(null, ExchangeListCap);
            if (token != _loadToken) return; // a newer load / mode switch superseded this one
            _exchangeCapped = capped;
            AppendPage(page);
            UpdateStatus();
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return;
            AppLog.Instance.Error("Cloud list load failed.", ex);
            Status = "Load failed: " + ex.Message;
        }
        finally { if (token == _loadToken) IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private async Task LoadMoreAsync()
    {
        if (_nextLink is null) return;
        var token = _loadToken; // continue the current generation; a reload/mode-switch will bump it
        IsBusy = true;
        try
        {
            var (page, capped) = await FetchAsync(_nextLink, ExchangeListCap);
            if (token != _loadToken) return; // superseded while paging
            _exchangeCapped = capped;
            AppendPage(page);
            UpdateStatus();
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return;
            AppLog.Instance.Error("Cloud list paging failed.", ex);
            Status = "Load more failed: " + ex.Message;
        }
        finally { if (token == _loadToken) IsBusy = false; }
    }

    private bool CanLoadMore() => HasMore && !IsBusy;

    /// <summary>The reason the list can't load right now, or null when it can. Both cloud sections hang off the
    /// same Entra sign-in (Exchange borrows its token from it), so that gate comes first.</summary>
    private string? NotReady()
    {
        if (!_graph.IsSignedIn) return "Not signed in to Entra ID — sign in under File ▸ Settings ▸ Cloud.";
        if (IsExchangeMode && !_exchange.IsConfigured)
            return "Exchange Online isn't configured — set the tenant under File ▸ Settings ▸ Cloud, then reopen this list.";
        return null;
    }

    /// <summary>
    /// One fetch for the current mode. <paramref name="nextLink"/> is Graph's continuation link and is ignored
    /// by the Exchange modes, which have no cursor: they return everything they are going to return in one call,
    /// bounded by <paramref name="exchangeMax"/>.
    ///
    /// Returns the cap flag rather than assigning it: this runs after an await, so a fetch that has been
    /// superseded by a mode switch must not write shared state its caller's generation check would have rejected.
    /// </summary>
    private async Task<(CloudPage Page, bool Capped)> FetchAsync(string? nextLink, int exchangeMax)
    {
        switch (Mode)
        {
            case CloudListMode.Users:
                return (await _graph.ListUsersAsync(SearchText, nextLink), false);
            case CloudListMode.Groups:
                return (await _graph.ListGroupsAsync(SearchText, nextLink), false);
            case CloudListMode.Devices:
                return (await _graph.ListDevicesAsync(SearchText, nextLink), false);
            case CloudListMode.Mailboxes:
            case CloudListMode.DistributionGroups:
            {
                if (nextLink is not null) return (new CloudPage(Array.Empty<CloudObjectRow>(), null), false); // no paging
                var page = Mode == CloudListMode.Mailboxes
                    ? await _exchange.ListMailboxesAsync(SearchText, exchangeMax)
                    : await _exchange.ListDistributionGroupsAsync(SearchText, exchangeMax);
                return (new CloudPage(page.Items, null), page.Capped);
            }
            default:
                return (new CloudPage(Array.Empty<CloudObjectRow>(), null), false);
        }
    }

    private void AppendPage(CloudPage page)
    {
        foreach (var row in page.Items)
        {
            row.PropertyChanged += OnRowPropertyChanged;
            Rows.Add(row);
        }
        _nextLink = page.NextLink;
        HasMore = _nextLink is not null;
        LoadMoreCommand.NotifyCanExecuteChanged();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CloudObjectRow.IsChecked)) CheckedCount = Rows.Count(r => r.IsChecked);
    }

    private void UpdateStatus() =>
        Status = $"{Rows.Count} object(s)"
            + (HasMore ? " (more available — Load more)" : string.Empty)
            // Exchange can't continue from where it stopped, so say plainly that rows were left behind and
            // point at the only way to reach them. A bare count would read as "that's all of them".
            + (IsExchangeMode && _exchangeCapped ? $" — capped at {ExchangeListCap}; narrow the search to see the rest" : string.Empty);

    /// <summary>Exports only the rows currently loaded into the list (respecting the active filter/sort).</summary>
    [RelayCommand]
    private void ExportCsv()
    {
        var path = _dialogs.PromptSaveFile("CSV files (*.csv)|*.csv|All files (*.*)|*.*", $"{ExportPrefix}-{Mode}.csv");
        if (path is null) return;
        try
        {
            File.WriteAllText(path, BuildCsv(RowsView.Cast<CloudObjectRow>()));
            Status = "Exported the loaded rows to " + path;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("Cloud CSV export failed.", ex);
            Status = "Export failed: " + ex.Message;
        }
    }

    /// <summary>Pages through <b>every</b> object from Entra ID (not just the loaded page) and exports
    /// them, applying the active server-side search + client-side filter but ignoring pagination.</summary>
    [RelayCommand(CanExecute = nameof(CanExportAll))]
    private async Task ExportAllCsvAsync()
    {
        if (NotReady() is { } reason)
        {
            Status = reason;
            return;
        }

        var path = _dialogs.PromptSaveFile("CSV files (*.csv)|*.csv|All files (*.*)|*.*", $"{ExportPrefix}-{Mode}-all.csv");
        if (path is null) return;

        IsBusy = true;
        try
        {
            var all = new List<CloudObjectRow>();
            // Tracked locally, not in _exchangeCapped: the export fetches under a different cap, and its
            // outcome must not rewrite what the list itself is showing.
            var exportCapped = false;
            string? next = null;
            do
            {
                var (page, capped) = await FetchAsync(next, ExchangeExportCap);
                all.AddRange(page.Items);
                exportCapped |= capped;
                next = page.NextLink;
                Status = $"Fetching all… {all.Count} object(s) so far";
            }
            while (next is not null);

            // Apply the same client-side filter the user sees (quick-filter + per-kind filter), just across
            // the full result set rather than only the loaded page.
            var filtered = all.Where(RowPredicate).ToList();
            File.WriteAllText(path, BuildCsv(filtered));
            // An Exchange export can still be truncated (at a much higher cap); say so rather than letting
            // "Exported all" imply completeness.
            Status = $"Exported all {filtered.Count} object(s) to {path}"
                + (exportCapped ? $" — capped at {ExchangeExportCap}; narrow the search for a complete export" : string.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("Cloud full CSV export failed.", ex);
            Status = "Export all failed: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    private bool CanExportAll() => !IsBusy;

    /// <summary>Filename prefix for exports, so an Exchange export isn't named "entra-…".</summary>
    private string ExportPrefix => IsExchangeMode ? "exchange" : "entra";

    /// <summary>CSV of the supplied rows (Name + visible columns).</summary>
    public string BuildCsv(IEnumerable<CloudObjectRow> rows)
    {
        var visible = Columns.Where(c => c.IsVisible).ToList();
        var headers = new List<string> { "Name" };
        headers.AddRange(visible.Select(c => c.Header));

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(Csv)));
        foreach (var row in rows)
        {
            var cells = new List<string> { row.DisplayName };
            cells.AddRange(visible.Select(c => row.Get(c.LdapName)));
            sb.AppendLine(string.Join(",", cells.Select(Csv)));
        }
        return sb.ToString();
    }

    private static string Csv(string value)
    {
        value ??= string.Empty;
        if (value.Length > 0 && (value[0] is '=' or '+' or '-' or '@' || value[0] == '\t'))
            value = "'" + value;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    // --- Bulk user actions over the checked rows (writes; confirm first) ---

    private bool CanBulkAct() => Mode == CloudListMode.Users && CheckedCount > 0 && !IsBusy;
    private void NotifyBulkCanExec()
    {
        EnableCheckedCommand.NotifyCanExecuteChanged();
        DisableCheckedCommand.NotifyCanExecuteChanged();
        RevokeCheckedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanBulkAct))]
    private Task EnableCheckedAsync() => RunBulkAsync("Enable", r => _graph.SetUserAccountEnabledAsync(r.Id, true));

    [RelayCommand(CanExecute = nameof(CanBulkAct))]
    private Task DisableCheckedAsync() => RunBulkAsync("Disable", r => _graph.SetUserAccountEnabledAsync(r.Id, false));

    [RelayCommand(CanExecute = nameof(CanBulkAct))]
    private Task RevokeCheckedAsync() => RunBulkAsync("Revoke sessions for", r => _graph.RevokeSignInSessionsAsync(r.Id));

    private async Task RunBulkAsync(string verb, Func<CloudObjectRow, Task> action)
    {
        var rows = CheckedRows.Where(r => r.Kind == CloudObjectKind.User).ToList();
        if (rows.Count == 0) return;

        var lines = rows.Select(r => "• " + r.DisplayName);
        var approved = rows.Count == 1
            ? _dialogs.Confirm(verb, $"{verb} {rows.Count} cloud user?", lines)
            : _dialogs.ConfirmWithPhrase(verb, $"{verb} {rows.Count} cloud users?", lines, rows.Count.ToString());
        if (!approved) return;

        IsBusy = true;
        Status = $"{verb} {rows.Count} user(s)…";
        var items = new List<BulkItemResult>();
        foreach (var r in rows)
        {
            try { await action(r); items.Add(new BulkItemResult(r.Id, r.DisplayName, true, null)); }
            catch (Exception ex) { items.Add(new BulkItemResult(r.Id, r.DisplayName, false, ex.Message)); }
        }
        IsBusy = false;
        _dialogs.ShowBulkResult(new BulkResult(items));
        await ReloadAsync();
    }

    // --- Columns (per-mode, persisted) ---

    private void BuildColumns(CloudListMode mode)
    {
        _columnsInitialized = false;
        foreach (var c in Columns) c.PropertyChanged -= OnColumnPropertyChanged;
        Columns.Clear();

        var saved = SavedColumnsFor(mode);
        var useSaved = saved is { Count: > 0 };
        foreach (var col in CloudColumnCatalog.Columns(mode))
        {
            if (useSaved) col.IsVisible = saved!.Contains(col.LdapName, StringComparer.OrdinalIgnoreCase);
            col.PropertyChanged += OnColumnPropertyChanged;
            Columns.Add(col);
        }
        _columnsInitialized = true;
        ColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnColumnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ColumnDefinition.IsVisible) || !_columnsInitialized) return;
        PersistColumns();
        ColumnsChanged?.Invoke(this, EventArgs.Empty); // rows already carry all values — just rebuild the grid
    }

    private List<string>? SavedColumnsFor(CloudListMode mode) => mode switch
    {
        CloudListMode.Users => _settings.VisibleCloudUserColumns,
        CloudListMode.Groups => _settings.VisibleCloudGroupColumns,
        CloudListMode.Devices => _settings.VisibleCloudDeviceColumns,
        CloudListMode.Mailboxes => _settings.VisibleExchangeMailboxColumns,
        CloudListMode.DistributionGroups => _settings.VisibleExchangeGroupColumns,
        _ => null,
    };

    private void PersistColumns()
    {
        var keys = Columns.Where(c => c.IsVisible).Select(c => c.LdapName).ToList();
        switch (Mode)
        {
            case CloudListMode.Users: _settings.VisibleCloudUserColumns = keys; break;
            case CloudListMode.Groups: _settings.VisibleCloudGroupColumns = keys; break;
            case CloudListMode.Devices: _settings.VisibleCloudDeviceColumns = keys; break;
            case CloudListMode.Mailboxes: _settings.VisibleExchangeMailboxColumns = keys; break;
            case CloudListMode.DistributionGroups: _settings.VisibleExchangeGroupColumns = keys; break;
            default: return;
        }
        _settingsStore.Save(_settings);
    }

    // --- Filters ---

    private bool RowPredicate(object item)
    {
        if (item is not CloudObjectRow row) return false;
        if (SelectedFilter is { } f && !f.Match(row)) return false;
        if (string.IsNullOrWhiteSpace(QuickFilter)) return true;
        if (row.DisplayName.Contains(QuickFilter, StringComparison.OrdinalIgnoreCase)) return true;
        return row.Values.Values.Any(v => v.Contains(QuickFilter, StringComparison.OrdinalIgnoreCase));
    }

    private void BuildFilterOptions(CloudListMode mode)
    {
        FilterOptions.Clear();
        FilterOptions.Add(new CloudFilterOption("All", _ => true));
        switch (mode)
        {
            case CloudListMode.Users:
                FilterOptions.Add(new("Enabled", r => r.Get("accountEnabled") == "Yes"));
                FilterOptions.Add(new("Disabled", r => r.Get("accountEnabled") == "No"));
                FilterOptions.Add(new("Synced from on-prem", r => r.Get("onPremisesSyncEnabled") == "Synced"));
                FilterOptions.Add(new("Cloud-only", r => r.Get("onPremisesSyncEnabled") == "Cloud-only"));
                break;
            case CloudListMode.Groups:
                FilterOptions.Add(new("Security", r => r.Get("groupType").Contains("Security", StringComparison.OrdinalIgnoreCase)));
                FilterOptions.Add(new("Distribution", r => r.Get("groupType") == "Distribution"));
                FilterOptions.Add(new("Microsoft 365", r => r.Get("groupType") == "Microsoft 365"));
                FilterOptions.Add(new("Teams", r => r.Get("teams") == "Yes"));
                FilterOptions.Add(new("Synced from on-prem", r => r.Get("origin") == "Synced"));
                FilterOptions.Add(new("Cloud-only", r => r.Get("origin") == "Cloud-only"));
                break;
            case CloudListMode.Devices:
                FilterOptions.Add(new("Compliant", r => r.Get("isCompliant") == "Yes"));
                FilterOptions.Add(new("Non-compliant", r => r.Get("isCompliant") == "No"));
                FilterOptions.Add(new("Hybrid joined", r => r.Get("trustType").Contains("ServerAd", StringComparison.OrdinalIgnoreCase)));
                FilterOptions.Add(new("Entra joined", r => r.Get("trustType").Contains("Entra", StringComparison.OrdinalIgnoreCase)));
                FilterOptions.Add(new("Enabled", r => r.Get("accountEnabled") == "Yes"));
                break;
            case CloudListMode.Mailboxes:
                FilterOptions.Add(new("User mailboxes", r => r.Get("mailboxType") == "User"));
                FilterOptions.Add(new("Shared mailboxes", r => r.Get("mailboxType") == "Shared"));
                FilterOptions.Add(new("Rooms and equipment", r => r.Get("mailboxType") is "Room" or "Equipment"));
                FilterOptions.Add(new("Synced from on-prem", r => r.Get("dirSynced") == "Synced"));
                FilterOptions.Add(new("Cloud-only", r => r.Get("dirSynced") == "Cloud-only"));
                break;
            case CloudListMode.DistributionGroups:
                FilterOptions.Add(new("Distribution", r => r.Get("groupType") == "Distribution"));
                FilterOptions.Add(new("Mail-enabled security", r => r.Get("groupType") == "Mail-enabled security"));
                // Synced groups are read-only in Exchange Online, so this is the filter that answers
                // "which of these can I actually change from here?".
                FilterOptions.Add(new("Cloud-only (editable here)", r => r.Get("dirSynced") == "Cloud-only"));
                FilterOptions.Add(new("Synced from on-prem", r => r.Get("dirSynced") == "Synced"));
                FilterOptions.Add(new("External senders allowed", r => r.Get("externalSenders") == "Allowed"));
                break;
        }
        SelectedFilter = FilterOptions[0];
    }
}
