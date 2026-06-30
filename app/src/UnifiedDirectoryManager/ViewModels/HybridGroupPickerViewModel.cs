using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Searchable picker that spans on-prem AD groups and Entra ID (cloud) groups, so an object can be added
/// to a mix of both in one action. Cloud results appear only when signed in to Entra; otherwise it behaves
/// like the on-prem group picker. Mirrors <see cref="ObjectPickerViewModel"/>'s search → basket → commit flow.
/// </summary>
public partial class HybridGroupPickerViewModel : ObservableObject
{
    private readonly IDirectoryService _directory;
    private readonly IGraphService _graph;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Type a name and search on-prem AD and Entra ID groups.";
    [ObservableProperty] private GroupRef? _selectedResult;

    public ObservableCollection<GroupRef> Results { get; } = new();
    public ObservableCollection<GroupRef> Basket { get; } = new();

    /// <summary>Final selection, set on OK.</summary>
    public List<GroupRef> Picked { get; } = new();

    public bool CloudAvailable => _graph.IsSignedIn;

    public HybridGroupPickerViewModel(IDirectoryService directory, IGraphService graph)
    {
        _directory = directory;
        _graph = graph;
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        IsBusy = true;
        Status = "Searching…";
        Results.Clear();
        var notes = new List<string>();
        try
        {
            var onPrem = await _directory.SearchByNameAsync(SearchText, AdObjectType.Group);
            foreach (var g in onPrem)
                Results.Add(new GroupRef(GroupOrigin.OnPrem, g.Name, g.DistinguishedName, null,
                    DirectoryService.ParentDn(g.DistinguishedName)));
            notes.Add($"{onPrem.Count} on-prem");
        }
        catch (Exception ex) { notes.Add("on-prem failed: " + DirectoryService.Friendly(ex)); }

        if (_graph.IsSignedIn)
        {
            try
            {
                // Exclude synced groups: they already appear in the on-prem AD results (the manageable copy),
                // and their Entra twin's membership can't be changed in the cloud.
                var cloud = (await _graph.SearchGroupsAsync(SearchText)).Where(g => !g.IsSynced).ToList();
                foreach (var g in cloud)
                    Results.Add(new GroupRef(GroupOrigin.Cloud, g.DisplayName, null, g.Id,
                        $"{g.GroupKind} · {g.Origin}"));
                notes.Add($"{cloud.Count} cloud-only");
            }
            catch (Exception ex) { notes.Add("cloud failed: " + ex.Message); }
        }
        else
        {
            notes.Add("cloud: not signed in (File ▸ Settings ▸ Cloud)");
        }

        IsBusy = false;
        Status = $"{Results.Count} result(s) — " + string.Join(", ", notes);
    }

    [RelayCommand]
    private void AddToBasket(System.Collections.IList? selected)
    {
        var rows = selected?.Cast<GroupRef>().ToList() ?? new List<GroupRef>();
        if (rows.Count == 0 && SelectedResult is not null) rows.Add(SelectedResult);
        foreach (var row in rows)
            if (Basket.All(b => !string.Equals(b.Key, row.Key, StringComparison.OrdinalIgnoreCase)))
                Basket.Add(row);
    }

    [RelayCommand]
    private void RemoveFromBasket(GroupRef? row)
    {
        if (row is not null) Basket.Remove(row);
    }

    public bool Commit()
    {
        Picked.Clear();
        Picked.AddRange(Basket);
        return Picked.Count > 0;
    }
}
