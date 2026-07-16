using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.DirectoryServices;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

public sealed record OptionItem<T>(T Value, string Label);

/// <summary>An OU/container chosen as a search base: its DN plus a friendly path for display.</summary>
public sealed record OuScopeItem(string Dn, string Display);

/// <summary>Builds an advanced LDAP query from friendly-labelled conditions, with a live filter preview.
/// Supports saving the current query (conditions or a raw LDAP filter) and recalling saved ones.</summary>
public partial class AdvancedSearchViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly ISavedSearchStore _savedSearches;

    [ObservableProperty] private AdObjectType _objectType = AdObjectType.User;
    [ObservableProperty] private bool _matchAll = true;
    [ObservableProperty] private SearchScope _scope = SearchScope.Subtree;
    [ObservableProperty] private string _rawFilter = string.Empty;
    [ObservableProperty] private bool _useRawFilter;

    // --- Saved searches ---
    /// <summary>The saved searches available to load.</summary>
    public ObservableCollection<SavedSearch> SavedSearches { get; } = new();

    [ObservableProperty] private SavedSearch? _selectedSavedSearch;

    /// <summary>Name to save the current query under (prefilled when a saved search is loaded).</summary>
    [ObservableProperty] private string _saveName = string.Empty;

    /// <summary>Status/feedback line for save/load/delete actions.</summary>
    [ObservableProperty] private string _savedStatus = string.Empty;

    /// <summary>Directory saved searches live in (shown in the UI).</summary>
    public string SearchesDirectory => _savedSearches.SearchesDirectory;

    public ObservableCollection<SearchCondition> Conditions { get; } = new();

    /// <summary>The OUs/containers to search; empty means the whole domain. Each is searched with <see cref="Scope"/>.</summary>
    public ObservableCollection<OuScopeItem> SearchBases { get; } = new();

    public IReadOnlyList<AttributeMeta> Attributes { get; } =
        AttributeCatalog.All.OrderBy(a => a.Friendly, StringComparer.CurrentCultureIgnoreCase).ToList();

    public IReadOnlyList<ConditionOperator> Operators { get; } = Enum.GetValues<ConditionOperator>();

    public IReadOnlyList<OptionItem<AdObjectType>> ObjectTypeOptions { get; } = new[]
    {
        new OptionItem<AdObjectType>(AdObjectType.User, "Users"),
        new OptionItem<AdObjectType>(AdObjectType.Computer, "Computers"),
        new OptionItem<AdObjectType>(AdObjectType.Group, "Groups"),
        new OptionItem<AdObjectType>(AdObjectType.Contact, "Contacts"),
        new OptionItem<AdObjectType>(AdObjectType.OrganizationalUnit, "Organizational units"),
        new OptionItem<AdObjectType>(AdObjectType.Unknown, "Any (users/computers/groups)"),
    };

    public IReadOnlyList<OptionItem<SearchScope>> ScopeOptions { get; } = new[]
    {
        new OptionItem<SearchScope>(SearchScope.Subtree, "Each selected OU and everything below"),
        new OptionItem<SearchScope>(SearchScope.OneLevel, "Each selected OU only (one level)"),
        new OptionItem<SearchScope>(SearchScope.Base, "Only the selected OU object itself"),
    };

    /// <summary>Effective LDAP filter, recomputed as inputs change.</summary>
    public string PreviewFilter => BuildQuery().BuildFilter();

    /// <summary>Set when the user runs the search; the dialog closes and the host runs it.</summary>
    public SearchQuery? Result { get; private set; }

    public AdvancedSearchViewModel(IDialogService dialogs, ISavedSearchStore savedSearches)
    {
        _dialogs = dialogs;
        _savedSearches = savedSearches;
        Conditions.CollectionChanged += OnConditionsChanged;
        Conditions.Add(new SearchCondition { LdapName = "cn", Operator = ConditionOperator.Contains });
        ReloadSavedSearches();
    }

    /// <summary>Refreshes the saved-search list from the store.</summary>
    public void ReloadSavedSearches()
    {
        var previous = SelectedSavedSearch?.Name;
        SavedSearches.Clear();
        foreach (var s in _savedSearches.LoadAll()) SavedSearches.Add(s);
        SelectedSavedSearch = SavedSearches.FirstOrDefault(s => string.Equals(s.Name, previous, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnObjectTypeChanged(AdObjectType value) => RefreshPreview();
    partial void OnMatchAllChanged(bool value) => RefreshPreview();
    partial void OnRawFilterChanged(string value) => RefreshPreview();
    partial void OnUseRawFilterChanged(bool value) => RefreshPreview();

    [RelayCommand] private void AddCondition() => Conditions.Add(new SearchCondition());
    [RelayCommand] private void RemoveCondition(SearchCondition? condition) { if (condition is not null) Conditions.Remove(condition); }
    [RelayCommand] private void Search() => Result = BuildQuery();

    // --- Saved-search commands (these do NOT close the dialog; only Search does) ---

    /// <summary>Saves the current query under <see cref="SaveName"/> (confirming an overwrite of an existing name).</summary>
    [RelayCommand]
    private void SaveSearch()
    {
        var name = SaveName.Trim();
        if (string.IsNullOrWhiteSpace(name)) { SavedStatus = "Enter a name to save this search."; return; }
        if (SavedSearches.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            && !_dialogs.Confirm("Overwrite saved search", $"A saved search named “{name}” already exists. Overwrite it?", new[] { name }))
            return;
        try
        {
            // Preserve the existing entry's description across an overwrite (there's no description input yet,
            // so re-saving a seeded/imported search must not blank its note).
            var existing = SavedSearches.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            _savedSearches.Save(new SavedSearch { Name = name, Description = existing?.Description ?? string.Empty, Query = BuildQuery() });
            ReloadSavedSearches();
            SelectedSavedSearch = SavedSearches.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            SavedStatus = $"Saved “{name}”.";
        }
        catch (Exception ex) { SavedStatus = "Save failed: " + ex.Message; }
    }

    /// <summary>Loads the selected saved search into the builder (does not run it — click Search to run).</summary>
    [RelayCommand]
    private void LoadSearch()
    {
        if (SelectedSavedSearch is null) { SavedStatus = "Select a saved search to load."; return; }
        try
        {
            LoadFrom(SelectedSavedSearch.Query);
            SaveName = SelectedSavedSearch.Name;
            SavedStatus = $"Loaded “{SelectedSavedSearch.Name}”. Review, then click Search.";
        }
        catch (Exception ex) { SavedStatus = "Load failed: " + ex.Message; }
    }

    /// <summary>Deletes the selected saved search (after confirmation).</summary>
    [RelayCommand]
    private void DeleteSearch()
    {
        if (SelectedSavedSearch is null) { SavedStatus = "Select a saved search to delete."; return; }
        var name = SelectedSavedSearch.Name;
        if (!_dialogs.Confirm("Delete saved search", $"Delete saved search “{name}”?", new[] { name })) return;
        try
        {
            _savedSearches.Delete(name);
            ReloadSavedSearches();
            SavedStatus = $"Deleted “{name}”.";
        }
        catch (Exception ex) { SavedStatus = "Delete failed: " + ex.Message; }
    }

    /// <summary>Exports the selected saved search to a JSON file (for sharing).</summary>
    [RelayCommand]
    private void ExportSearch()
    {
        if (SelectedSavedSearch is null) { SavedStatus = "Select a saved search to export."; return; }
        var path = _dialogs.PromptSaveFile("Saved search (*.json)|*.json|All files (*.*)|*.*", SelectedSavedSearch.Name + ".json");
        if (path is null) return;
        try { _savedSearches.ExportTo(SelectedSavedSearch, path); SavedStatus = $"Exported to {path}."; }
        catch (Exception ex) { SavedStatus = "Export failed: " + ex.Message; }
    }

    /// <summary>Imports a saved search from a JSON file into the store (confirming an overwrite).</summary>
    [RelayCommand]
    private void ImportSearch()
    {
        var path = _dialogs.PromptOpenFile("Saved search (*.json)|*.json|All files (*.*)|*.*");
        if (path is null) return;
        try
        {
            var imported = _savedSearches.ImportFrom(path);
            if (SavedSearches.Any(s => string.Equals(s.Name, imported.Name, StringComparison.OrdinalIgnoreCase))
                && !_dialogs.Confirm("Import saved search", $"A saved search named “{imported.Name}” already exists. Overwrite it?", new[] { imported.Name }))
                return;
            _savedSearches.Save(imported);
            ReloadSavedSearches();
            SelectedSavedSearch = SavedSearches.FirstOrDefault(s => string.Equals(s.Name, imported.Name, StringComparison.OrdinalIgnoreCase));
            SavedStatus = $"Imported “{imported.Name}”.";
        }
        catch (Exception ex) { SavedStatus = "Import failed: " + ex.Message; }
    }

    /// <summary>Populates the builder fields from a stored query (the inverse of <see cref="BuildQuery"/>).</summary>
    public void LoadFrom(SearchQuery q)
    {
        ObjectType = q.ObjectType;
        MatchAll = q.MatchAll;
        Scope = q.Scope;
        RawFilter = q.RawFilter ?? string.Empty;
        UseRawFilter = !string.IsNullOrWhiteSpace(q.RawFilter);

        Conditions.Clear();
        foreach (var c in q.Conditions)
            Conditions.Add(new SearchCondition { LdapName = c.LdapName, Operator = c.Operator, Value = c.Value });

        SearchBases.Clear();
        foreach (var dn in q.EffectiveBaseDns()) AddBase(dn);

        RefreshPreview();
    }

    /// <summary>Opens the OU picker (multi-select) and adds the ticked containers to the search bases.</summary>
    [RelayCommand]
    private void BrowseBases()
    {
        var picked = _dialogs.PickContainers(SearchBases.Select(b => b.Dn).ToList());
        if (picked is null) return;
        foreach (var dn in picked) AddBase(dn);
    }

    [RelayCommand]
    private void RemoveBase(OuScopeItem? item)
    {
        if (item is not null) SearchBases.Remove(item);
    }

    /// <summary>Adds a base DN (deduped, case-insensitive) with a friendly display path.</summary>
    public void AddBase(string dn)
    {
        if (string.IsNullOrWhiteSpace(dn)) return;
        var trimmed = dn.Trim();
        if (SearchBases.Any(b => string.Equals(b.Dn, trimmed, StringComparison.OrdinalIgnoreCase))) return;
        SearchBases.Add(new OuScopeItem(trimmed, FriendlyPath(trimmed)));
    }

    /// <summary>Renders a DN as a readable OU path (root-most first), for display only.</summary>
    private static string FriendlyPath(string dn)
    {
        var labels = Regex.Split(dn.Trim(), @"(?<!\\),")
            .Select(rdn => rdn.Trim())
            .Where(rdn => rdn.StartsWith("OU=", StringComparison.OrdinalIgnoreCase)
                       || rdn.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            .Select(rdn => rdn[3..])
            .Reverse()
            .ToList();
        return labels.Count > 0 ? string.Join(" / ", labels) : "Whole domain";
    }

    private void OnConditionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (SearchCondition c in e.NewItems) c.PropertyChanged += OnConditionPropertyChanged;
        if (e.OldItems is not null)
            foreach (SearchCondition c in e.OldItems) c.PropertyChanged -= OnConditionPropertyChanged;
        RefreshPreview();
    }

    private void OnConditionPropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshPreview();

    private void RefreshPreview() => OnPropertyChanged(nameof(PreviewFilter));

    private SearchQuery BuildQuery() => new()
    {
        ObjectType = ObjectType,
        Conditions = Conditions.ToList(),
        MatchAll = MatchAll,
        Scope = Scope,
        BaseDns = SearchBases.Select(b => b.Dn).ToList(),
        RawFilter = UseRawFilter && !string.IsNullOrWhiteSpace(RawFilter) ? RawFilter : null,
    };
}
