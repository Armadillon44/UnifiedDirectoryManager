using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Searchable picker for Entra ID groups to add an object to, multi-select. Mirrors the search → basket →
/// commit flow of the other cloud pickers. Cloud-only (no on-prem groups — the target is a cloud object).
/// </summary>
public partial class CloudGroupPickerViewModel : ObservableObject
{
    private readonly IGraphService _graph;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Search Entra ID groups, then add the ones to include.";
    [ObservableProperty] private CloudGroup? _selectedResult;

    public ObservableCollection<CloudGroup> Results { get; } = new();
    public ObservableCollection<CloudGroup> Basket { get; } = new();

    /// <summary>Final selection set on OK.</summary>
    public List<CloudGroup> Picked { get; } = new();

    public CloudGroupPickerViewModel(IGraphService graph) => _graph = graph;

    [RelayCommand]
    private async Task SearchAsync()
    {
        IsBusy = true;
        Status = "Searching…";
        Results.Clear();
        try
        {
            // This picker is for cloud membership writes, so offer only cloud-only groups — a synced group's
            // membership is mastered on-prem (manage it in AD), so listing it here would just fail on add.
            var groups = (await _graph.SearchGroupsAsync(SearchText)).Where(g => !g.IsSynced).ToList();
            foreach (var g in groups) Results.Add(g);
            Status = $"{Results.Count} cloud-only group(s).";
        }
        catch (Exception ex) { Status = "Search failed: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void AddToBasket(System.Collections.IList? selected)
    {
        var rows = selected?.Cast<CloudGroup>().ToList() ?? new List<CloudGroup>();
        if (rows.Count == 0 && SelectedResult is not null) rows.Add(SelectedResult);
        foreach (var row in rows)
            if (Basket.All(b => !string.Equals(b.Id, row.Id, StringComparison.OrdinalIgnoreCase)))
                Basket.Add(row);
    }

    [RelayCommand]
    private void RemoveFromBasket(CloudGroup? row)
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
