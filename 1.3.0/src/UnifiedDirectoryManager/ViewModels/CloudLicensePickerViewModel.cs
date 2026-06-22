using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Picks one or more tenant license SKUs to assign directly to a user. The list already excludes SKUs the
/// user holds directly; the availability column and the "assigned via group" hint steer admins toward
/// group-based licensing (the caller prompts again per SKU before a direct assignment commits).
/// </summary>
public partial class CloudLicensePickerViewModel : ObservableObject
{
    [ObservableProperty] private string _status;

    public ObservableCollection<CloudSku> Skus { get; } = new();

    /// <summary>Final selection set on OK.</summary>
    public List<CloudSku> Picked { get; } = new();

    public CloudLicensePickerViewModel(IReadOnlyList<CloudSku> skus)
    {
        foreach (var s in skus) Skus.Add(s);
        _status = Skus.Count == 0
            ? "No assignable SKUs — the user already holds every available license, or none are in stock."
            : $"{Skus.Count} SKU(s) available. Prefer group membership where a SKU is group-assigned.";
    }

    public bool Commit(System.Collections.IList? selected)
    {
        Picked.Clear();
        if (selected is not null)
            foreach (var item in selected)
                if (item is CloudSku sku) Picked.Add(sku);
        return Picked.Count > 0;
    }
}
