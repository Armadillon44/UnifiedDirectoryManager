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

/// <summary>Builds an advanced LDAP query from friendly-labelled conditions, with a live filter preview.</summary>
public partial class AdvancedSearchViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;

    [ObservableProperty] private AdObjectType _objectType = AdObjectType.User;
    [ObservableProperty] private bool _matchAll = true;
    [ObservableProperty] private SearchScope _scope = SearchScope.Subtree;
    [ObservableProperty] private string _rawFilter = string.Empty;
    [ObservableProperty] private bool _useRawFilter;

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
        new OptionItem<AdObjectType>(AdObjectType.Unknown, "Any (users/computers/groups)"),
    };

    public IReadOnlyList<OptionItem<SearchScope>> ScopeOptions { get; } = new[]
    {
        new OptionItem<SearchScope>(SearchScope.Subtree, "Each selected OU and everything below"),
        new OptionItem<SearchScope>(SearchScope.OneLevel, "Each selected OU only (one level)"),
    };

    /// <summary>Effective LDAP filter, recomputed as inputs change.</summary>
    public string PreviewFilter => BuildQuery().BuildFilter();

    /// <summary>Set when the user runs the search; the dialog closes and the host runs it.</summary>
    public SearchQuery? Result { get; private set; }

    public AdvancedSearchViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        Conditions.CollectionChanged += OnConditionsChanged;
        Conditions.Add(new SearchCondition { LdapName = "cn", Operator = ConditionOperator.Contains });
    }

    partial void OnObjectTypeChanged(AdObjectType value) => RefreshPreview();
    partial void OnMatchAllChanged(bool value) => RefreshPreview();
    partial void OnRawFilterChanged(string value) => RefreshPreview();
    partial void OnUseRawFilterChanged(bool value) => RefreshPreview();

    [RelayCommand] private void AddCondition() => Conditions.Add(new SearchCondition());
    [RelayCommand] private void RemoveCondition(SearchCondition? condition) { if (condition is not null) Conditions.Remove(condition); }
    [RelayCommand] private void Search() => Result = BuildQuery();

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
