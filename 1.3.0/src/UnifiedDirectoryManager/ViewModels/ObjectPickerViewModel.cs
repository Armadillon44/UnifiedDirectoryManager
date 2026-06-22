using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>Searchable picker for groups (multi) or a single principal (e.g. a manager).</summary>
public partial class ObjectPickerViewModel : ObservableObject
{
    private readonly IDirectoryService _directory;

    public AdObjectType ObjectType { get; }
    public bool MultiSelect { get; }
    public string Heading { get; }

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Type a name and search.";
    [ObservableProperty] private AdObjectRow? _selectedResult;

    public ObservableCollection<AdObjectRow> Results { get; } = new();
    public ObservableCollection<AdObjectRow> Basket { get; } = new();

    /// <summary>Final selection set by the dialog on OK.</summary>
    public List<AdObjectRow> Picked { get; } = new();

    public ObjectPickerViewModel(IDirectoryService directory, AdObjectType type, bool multiSelect)
    {
        _directory = directory;
        ObjectType = type;
        MultiSelect = multiSelect;
        Heading = multiSelect ? "Search and add one or more objects." : "Search and select an object.";
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        IsBusy = true;
        Status = "Searching…";
        try
        {
            var results = await _directory.SearchByNameAsync(SearchText, ObjectType);
            Results.Clear();
            foreach (var r in results) Results.Add(r);
            Status = $"{Results.Count} result(s)" + (Results.Count >= 200 ? " (showing first 200)" : string.Empty);
        }
        catch (Exception ex)
        {
            Status = DirectoryService.Friendly(ex);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void AddToBasket(System.Collections.IList? selected)
    {
        // The Add button passes the results list's SelectedItems so several objects can be added at
        // once; fall back to the single highlighted row (e.g. when invoked without a parameter).
        var rows = selected?.Cast<AdObjectRow>().ToList() ?? new List<AdObjectRow>();
        if (rows.Count == 0 && SelectedResult is not null) rows.Add(SelectedResult);
        if (rows.Count == 0) return;

        if (!MultiSelect)
        {
            Basket.Clear();
            Basket.Add(rows[0]);
            return;
        }

        foreach (var row in rows)
            if (Basket.All(b => !string.Equals(b.DistinguishedName, row.DistinguishedName, StringComparison.OrdinalIgnoreCase)))
                Basket.Add(row);
    }

    [RelayCommand]
    private void RemoveFromBasket(AdObjectRow? row)
    {
        if (row is not null) Basket.Remove(row);
    }

    /// <summary>Builds <see cref="Picked"/> from the basket (multi) or the highlighted result (single).</summary>
    public bool Commit()
    {
        Picked.Clear();
        if (MultiSelect)
            Picked.AddRange(Basket);
        else if (Basket.Count > 0)
            Picked.Add(Basket[0]);
        else if (SelectedResult is not null)
            Picked.Add(SelectedResult);
        return Picked.Count > 0;
    }
}
