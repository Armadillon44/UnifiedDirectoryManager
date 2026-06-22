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
/// The cloud (Entra ID) object list: loads Users / Groups / Devices one page at a time, with a
/// server-side name search, client-side quick-filter + per-kind filter, a runtime column chooser,
/// checkbox multi-select and CSV export. Selecting a row drives the read-only <see cref="Detail"/>
/// pane; double-clicking raises <see cref="OpenRequested"/> (the host opens a properties window).
/// Read-only — the bulk-action buttons in the view are intentionally disabled this round.
/// </summary>
public partial class CloudObjectListViewModel : ObservableObject
{
    private readonly IGraphService _graph;
    private readonly IDialogService _dialogs;
    private readonly ISettingsStore _settingsStore;
    private readonly AppSettings _settings;

    private string? _nextLink;
    private bool _columnsInitialized;
    private bool _suppressSelectAll;

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

    public void RequestOpen(CloudObjectRow row) => OpenRequested?.Invoke(this, row);

    public IReadOnlyList<CloudObjectRow> CheckedRows => Rows.Where(r => r.IsChecked).ToList();

    public CloudObjectListViewModel(IGraphService graph, IDialogService dialogs, ISettingsStore settingsStore, AppSettings settings)
    {
        _graph = graph;
        _dialogs = dialogs;
        _settingsStore = settingsStore;
        _settings = settings;
        Detail = new CloudObjectDetailViewModel(graph, dialogs);

        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = RowPredicate;
    }

    partial void OnQuickFilterChanged(string value) => RowsView.Refresh();
    partial void OnSelectedFilterChanged(CloudFilterOption? value) => RowsView.Refresh();
    partial void OnSelectedRowChanged(CloudObjectRow? value) => Detail.SetTarget(value);
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

    partial void OnSelectAllChanged(bool value)
    {
        if (_suppressSelectAll) return;
        foreach (var row in Rows) row.IsChecked = value;
    }

    /// <summary>Switches the list to a mode (Users/Groups/Devices) and loads page 1.</summary>
    public async Task LoadAsync(CloudListMode mode)
    {
        Mode = mode;
        Header = mode switch
        {
            CloudListMode.Users => "Entra ID — Users",
            CloudListMode.Groups => "Entra ID — Groups",
            CloudListMode.Devices => "Entra ID — Devices",
            _ => "Entra ID",
        };

        BuildColumns(mode);
        BuildFilterOptions(mode);
        SearchText = string.Empty;
        QuickFilter = string.Empty;
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
        if (!_graph.IsSignedIn)
        {
            Rows.Clear();
            Status = "Not signed in to Entra ID — sign in under File ▸ Settings ▸ Cloud.";
            return;
        }

        var token = ++_loadToken;
        IsBusy = true;
        _suppressSelectAll = true; SelectAll = false; _suppressSelectAll = false;
        Rows.Clear();
        SelectedRow = null;
        _nextLink = null;
        Status = "Loading…";
        try
        {
            var page = await FetchAsync(null);
            if (token != _loadToken) return; // a newer load / mode switch superseded this one
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
            var page = await FetchAsync(_nextLink);
            if (token != _loadToken) return; // superseded while paging
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

    private Task<CloudPage> FetchAsync(string? nextLink) => Mode switch
    {
        CloudListMode.Users => _graph.ListUsersAsync(SearchText, nextLink),
        CloudListMode.Groups => _graph.ListGroupsAsync(SearchText, nextLink),
        CloudListMode.Devices => _graph.ListDevicesAsync(SearchText, nextLink),
        _ => Task.FromResult(new CloudPage(Array.Empty<CloudObjectRow>(), null)),
    };

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
        Status = $"{Rows.Count} object(s)" + (HasMore ? " (more available — Load more)" : string.Empty);

    /// <summary>Exports only the rows currently loaded into the list (respecting the active filter/sort).</summary>
    [RelayCommand]
    private void ExportCsv()
    {
        var path = _dialogs.PromptSaveFile("CSV files (*.csv)|*.csv|All files (*.*)|*.*", $"entra-{Mode}.csv");
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
        if (!_graph.IsSignedIn)
        {
            Status = "Not signed in to Entra ID — sign in under File ▸ Settings ▸ Cloud.";
            return;
        }

        var path = _dialogs.PromptSaveFile("CSV files (*.csv)|*.csv|All files (*.*)|*.*", $"entra-{Mode}-all.csv");
        if (path is null) return;

        IsBusy = true;
        try
        {
            var all = new List<CloudObjectRow>();
            string? next = null;
            do
            {
                var page = await FetchAsync(next);
                all.AddRange(page.Items);
                next = page.NextLink;
                Status = $"Fetching all… {all.Count} object(s) so far";
            }
            while (next is not null);

            // Apply the same client-side filter the user sees (quick-filter + per-kind filter), just across
            // the full result set rather than only the loaded page.
            var filtered = all.Where(RowPredicate).ToList();
            File.WriteAllText(path, BuildCsv(filtered));
            Status = $"Exported all {filtered.Count} object(s) to {path}";
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("Cloud full CSV export failed.", ex);
            Status = "Export all failed: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    private bool CanExportAll() => !IsBusy;

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
        }
        SelectedFilter = FilterOptions[0];
    }
}
