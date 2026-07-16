using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Unified, searchable group picker that spans all three directories in one basket: on-prem AD groups,
/// Entra ID security / Microsoft 365 groups (Graph), and Exchange Online distribution lists / mail-enabled
/// security groups. Each cloud result is classified by its group kind into the right apply channel
/// (<see cref="GroupChannel.EntraGraph"/> vs <see cref="GroupChannel.ExchangeOnline"/>) so the caller knows
/// how to add the member later — one Graph search surfaces both, so no separate Exchange search is needed for
/// discovery. Cloud results appear only when signed in to Entra; otherwise it behaves like the on-prem picker.
/// Mirrors <see cref="ObjectPickerViewModel"/>'s search → basket → commit flow.
/// </summary>
public partial class HybridGroupPickerViewModel : ObservableObject
{
    private readonly IDirectoryService _directory;
    private readonly IGraphService _graph;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Type a name and search on-prem AD, Entra ID, and Exchange distribution groups.";
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
                Results.Add(new GroupRef(GroupChannel.OnPremAd, g.Name, g.DistinguishedName, null, null,
                    DirectoryService.ParentDn(g.DistinguishedName)));
            notes.Add($"{onPrem.Count} on-prem");
        }
        catch (Exception ex) { notes.Add("on-prem failed: " + DirectoryService.Friendly(ex)); }

        if (_graph.IsSignedIn)
        {
            try
            {
                // Exclude synced groups: they already appear in the on-prem AD results (the manageable copy),
                // and their Entra twin's membership can't be changed in the cloud. Classify each cloud group by
                // kind: distribution lists / mail-enabled security groups can only be modified through Exchange
                // Online (Add-DistributionGroupMember), everything else through Graph.
                var cloud = (await _graph.SearchGroupsAsync(SearchText)).Where(g => !g.IsSynced).ToList();
                foreach (var g in cloud)
                {
                    var viaExchange = string.Equals(g.GroupKind, "Distribution", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(g.GroupKind, "Mail-enabled security", StringComparison.OrdinalIgnoreCase);
                    Results.Add(viaExchange
                        ? new GroupRef(GroupChannel.ExchangeOnline, g.DisplayName, null, g.Id, g.Mail, $"{g.GroupKind} · {g.Origin}")
                        : new GroupRef(GroupChannel.EntraGraph, g.DisplayName, null, g.Id, null, $"{g.GroupKind} · {g.Origin}"));
                }
                notes.Add($"{cloud.Count} cloud");
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
