using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Searchable picker for Entra ID members to add to a cloud group — users and devices (computers),
/// multi-select. Mirrors the search → basket → commit flow of the other pickers.
/// </summary>
public partial class CloudMemberPickerViewModel : ObservableObject
{
    private readonly IGraphService _graph;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Search Entra ID users and devices, then add the ones to include.";
    [ObservableProperty] private CloudObjectRow? _selectedResult;

    public ObservableCollection<CloudObjectRow> Results { get; } = new();
    public ObservableCollection<CloudObjectRow> Basket { get; } = new();

    /// <summary>Final selection set on OK.</summary>
    public List<CloudObjectRow> Picked { get; } = new();

    public CloudMemberPickerViewModel(IGraphService graph) => _graph = graph;

    [RelayCommand]
    private async Task SearchAsync()
    {
        IsBusy = true;
        Status = "Searching…";
        Results.Clear();
        var notes = new List<string>();
        try
        {
            var users = await _graph.ListUsersAsync(SearchText, null);
            foreach (var u in users.Items) Results.Add(u);
            notes.Add($"{users.Items.Count} user(s)");
        }
        catch (Exception ex) { notes.Add("users failed: " + ex.Message); }

        try
        {
            var devices = await _graph.ListDevicesAsync(SearchText, null);
            foreach (var d in devices.Items) Results.Add(d);
            notes.Add($"{devices.Items.Count} device(s)");
        }
        catch (Exception ex) { notes.Add("devices failed: " + ex.Message); }

        IsBusy = false;
        Status = $"{Results.Count} result(s) — " + string.Join(", ", notes);
    }

    [RelayCommand]
    private void AddToBasket(System.Collections.IList? selected)
    {
        var rows = selected?.Cast<CloudObjectRow>().ToList() ?? new List<CloudObjectRow>();
        if (rows.Count == 0 && SelectedResult is not null) rows.Add(SelectedResult);
        foreach (var row in rows)
            if (Basket.All(b => !string.Equals(b.Id, row.Id, StringComparison.OrdinalIgnoreCase)))
                Basket.Add(row);
    }

    [RelayCommand]
    private void RemoveFromBasket(CloudObjectRow? row)
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
