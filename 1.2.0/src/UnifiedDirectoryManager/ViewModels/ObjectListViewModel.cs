using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

public enum ListFilter { Users, Computers, Groups, All }

/// <summary>
/// The object-list pane: loads users/computers under a container (or from an advanced query),
/// with selectable columns, sorting (handled in the view), a quick-filter, and multi-selection.
/// </summary>
public partial class ObjectListViewModel : ObservableObject
{
    private readonly IDirectoryService _directory;
    private readonly Action<string> _onError;
    private readonly ISettingsStore? _settingsStore;
    private readonly AppSettings? _settings;
    private bool _columnsInitialized;

    private string? _baseDn;
    private SearchQuery? _query;
    private string? _lastContainerDn; // remembered across a search so "Clear search" can return to browsing

    public ObservableCollection<ColumnDefinition> Columns { get; } = new();
    public ObservableCollection<AdObjectRow> Rows { get; } = new();
    public ICollectionView RowsView { get; }

    /// <summary>Multi-selection pushed from the view; used by Bulk Edit.</summary>
    public List<AdObjectRow> SelectedRows { get; } = new();

    [ObservableProperty] private AdObjectRow? _selectedRow;
    [ObservableProperty] private ListFilter _filter = ListFilter.All;
    [ObservableProperty] private string _quickFilter = string.Empty;
    [ObservableProperty] private bool _subtree;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _isBusy;

    /// <summary>True while the list is showing advanced-search results (rather than a browsed container);
    /// drives the "Clear search" affordance.</summary>
    [ObservableProperty] private bool _isSearchActive;

    /// <summary>Raised when the visible column set changes so the view can rebuild GridView columns.</summary>
    public event EventHandler? ColumnsChanged;

    /// <summary>Raised when the single selection changes so the host can refresh the edit pane.</summary>
    public event EventHandler<AdObjectRow?>? SelectionChanged;

    /// <summary>Raised when a row is activated (double-clicked) so the host can open a separate editor.</summary>
    public event EventHandler<AdObjectRow>? OpenRequested;

    public void RequestOpen(AdObjectRow row) => OpenRequested?.Invoke(this, row);

    public ObjectListViewModel(IDirectoryService directory, Action<string> onError,
        ISettingsStore? settingsStore = null, AppSettings? settings = null)
    {
        _directory = directory;
        _onError = onError;
        _settingsStore = settingsStore;
        _settings = settings;

        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = QuickFilterPredicate;

        // Restore the saved visible-column set if present, else fall back to defaults.
        var saved = settings?.VisibleColumns;
        var useSaved = saved is { Count: > 0 };
        foreach (var col in DefaultColumns())
        {
            if (useSaved) col.IsVisible = saved!.Contains(col.LdapName, StringComparer.OrdinalIgnoreCase);
            col.PropertyChanged += OnColumnPropertyChanged;
            Columns.Add(col);
        }
        _columnsInitialized = true;
    }

    partial void OnSelectedRowChanged(AdObjectRow? value) => SelectionChanged?.Invoke(this, value);
    partial void OnFilterChanged(ListFilter value) => _ = ReloadAsync();
    partial void OnSubtreeChanged(bool value) => _ = ReloadAsync();
    partial void OnQuickFilterChanged(string value) => RowsView.Refresh();

    public IReadOnlyList<string> VisibleColumnLdapNames =>
        Columns.Where(c => c.IsVisible).Select(c => c.LdapName).ToList();

    /// <summary>Builds a CSV of the current view (visible columns + Name/Type/Status), respecting sort/filter.</summary>
    public string BuildCsv()
    {
        var visible = Columns.Where(c => c.IsVisible).ToList();
        var headers = new List<string> { "Name", "Type", "Status", "Protected" };
        headers.AddRange(visible.Select(c => c.Header));

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(Csv)));

        foreach (var item in RowsView) // RowsView reflects the current filter + sort order
        {
            if (item is not AdObjectRow row) continue;
            var cells = new List<string> { row.Name, row.Type.ToString(), row.StatusText, row.IsProtected ? "Yes" : "No" };
            cells.AddRange(visible.Select(c => row.Get(c.LdapName)));
            sb.AppendLine(string.Join(",", cells.Select(Csv)));
        }
        return sb.ToString();
    }

    private static string Csv(string value)
    {
        value ??= string.Empty;
        // Mitigate CSV/formula injection: neutralize cells that a spreadsheet would treat as a formula.
        if (value.Length > 0 && (value[0] is '=' or '+' or '-' or '@' || value[0] == '\t'))
            value = "'" + value;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    /// <summary>Loads the leaf objects under a container.</summary>
    public async Task LoadContainerAsync(string baseDn)
    {
        _baseDn = baseDn;
        _lastContainerDn = baseDn;
        _query = null;
        IsSearchActive = false;
        await ReloadAsync();
    }

    /// <summary>Loads the results of an advanced query.</summary>
    public async Task LoadQueryAsync(SearchQuery query)
    {
        _query = query;
        _baseDn = null;
        IsSearchActive = true;
        Filter = query.ObjectType switch
        {
            AdObjectType.User => ListFilter.Users,
            AdObjectType.Computer => ListFilter.Computers,
            AdObjectType.Group => ListFilter.Groups,
            _ => ListFilter.All,
        };
        await ReloadAsync();
    }

    /// <summary>Clears the advanced-search results and returns to browsing the last-viewed container.</summary>
    [RelayCommand]
    public async Task ClearSearchAsync()
    {
        if (!IsSearchActive) return;
        if (_lastContainerDn is null)
        {
            _query = null;
            IsSearchActive = false;
            Rows.Clear();
            Status = string.Empty;
            return;
        }
        await LoadContainerAsync(_lastContainerDn);
    }

    // Generation counter: a newer reload (fast filter/subtree toggling or node switching on a slow DC)
    // invalidates an older in-flight one so stale results can't clobber the current list.
    private int _reloadToken;

    [RelayCommand]
    public async Task ReloadAsync()
    {
        if (_baseDn is null && _query is null) return;
        var token = ++_reloadToken;
        IsBusy = true;
        Status = "Loading…";
        var previousDn = SelectedRow?.DistinguishedName;
        try
        {
            var columns = VisibleColumnLdapNames;
            IReadOnlyList<AdObjectRow> result = _query is not null
                ? await _directory.SearchAsync(_query, columns)
                : await _directory.ListObjectsAsync(_baseDn!, MapFilter(Filter), columns, Subtree);

            if (token != _reloadToken) return; // a newer reload superseded this one

            Rows.Clear();
            foreach (var row in result) Rows.Add(row);

            // Re-select the same object after a refresh (e.g. following an edit) so the user keeps their place.
            SelectedRow = previousDn is not null
                ? Rows.FirstOrDefault(r => string.Equals(r.DistinguishedName, previousDn, StringComparison.OrdinalIgnoreCase))
                : null;
            Status = $"{Rows.Count} object(s)";
        }
        catch (Exception ex)
        {
            if (token != _reloadToken) return;
            Status = DirectoryService.Friendly(ex);
            _onError(Status);
        }
        finally { if (token == _reloadToken) IsBusy = false; }
    }

    private void OnColumnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ColumnDefinition.IsVisible) || !_columnsInitialized) return;
        PersistColumns();
        ColumnsChanged?.Invoke(this, EventArgs.Empty);
        _ = ReloadAsync(); // fetch data for newly-visible columns
    }

    /// <summary>Saves the current visible-column set so it survives a restart.</summary>
    private void PersistColumns()
    {
        if (_settings is null || _settingsStore is null) return;
        _settings.VisibleColumns = Columns.Where(c => c.IsVisible).Select(c => c.LdapName).ToList();
        _settingsStore.Save(_settings);
    }

    private bool QuickFilterPredicate(object item)
    {
        if (string.IsNullOrWhiteSpace(QuickFilter)) return true;
        if (item is not AdObjectRow row) return false;
        if (row.Name.Contains(QuickFilter, StringComparison.OrdinalIgnoreCase)) return true;
        return row.Values.Values.Any(v => v.Contains(QuickFilter, StringComparison.OrdinalIgnoreCase));
    }

    private static AdObjectType MapFilter(ListFilter filter) => filter switch
    {
        ListFilter.Users => AdObjectType.User,
        ListFilter.Computers => AdObjectType.Computer,
        ListFilter.Groups => AdObjectType.Group,
        _ => AdObjectType.Unknown,
    };

    private static IEnumerable<ColumnDefinition> DefaultColumns()
    {
        (string Ldap, bool Visible)[] defs =
        {
            ("sAMAccountName", true),
            ("displayName", true),
            ("description", true),
            ("givenName", false),
            ("sn", false),
            ("manager", false),
            ("employeeID", false),
            ("title", false),
            ("department", false),
            ("mail", false),
            ("physicalDeliveryOfficeName", false),
            ("l", false),
            ("st", false),
            ("co", false),
            ("userPrincipalName", false),
            ("operatingSystem", false),
            ("lastLogonTimestamp", false),
            ("whenCreated", false),
        };
        foreach (var (ldap, visible) in defs)
            yield return new ColumnDefinition { LdapName = ldap, Header = AttributeCatalog.Friendly(ldap), IsVisible = visible };
    }
}
