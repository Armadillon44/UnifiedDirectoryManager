using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Backs Tools ▸ Deleted Objects… — a read-only view of the domain's Deleted Objects container.
/// </summary>
/// <remarks>
/// <para>
/// Its own window rather than the main object list, because a deleted object answers different questions:
/// what it used to be called, where it used to live, when it went. The main list's columns and its
/// right-click actions (Enable, Move to OU, Bulk Edit) are all meaningless for something that no longer
/// exists, and offering them would be worse than not having the view.
/// </para>
/// <para>
/// Read-only on purpose. Restoring is a write to a part of AD most tools never touch, with its own
/// failure modes — the old parent may be gone, the name may now be taken, and a user comes back without
/// the group memberships that made the account useful. It belongs in its own change.
/// </para>
/// </remarks>
public partial class DeletedObjectsViewModel : ObservableObject
{
    private readonly IDirectoryService _directory;
    private readonly ICollectionView _view;

    public ObservableCollection<DeletedObjectRow> Rows { get; } = new();

    /// <summary>The filtered, sortable view the window binds to.</summary>
    public ICollectionView RowsView => _view;

    /// <summary>The sentence above the list. Carries the whole "empty vs not allowed vs off" distinction.</summary>
    [ObservableProperty] private string _summary = string.Empty;

    /// <summary>True when the summary is reporting a problem rather than describing the list.</summary>
    [ObservableProperty] private bool _isProblem;

    [ObservableProperty] private bool _isBusy;

    /// <summary>Free-text filter over the name, the old location and the logon name.</summary>
    [ObservableProperty] private string _quickFilter = string.Empty;

    /// <summary>
    /// Also list objects past the deleted-object lifetime. Off by default: those cannot be brought back,
    /// and a list that mixes them in invites trying.
    /// </summary>
    [ObservableProperty] private bool _includeRecycled;

    /// <summary>Count line under the list, so a filtered view cannot be mistaken for the whole container.</summary>
    public string CountText
    {
        get
        {
            var shown = _view.Cast<object>().Count();
            return Rows.Count == shown
                ? $"{Rows.Count} object(s)"
                : $"{shown} of {Rows.Count} object(s)";
        }
    }

    public DeletedObjectsViewModel(IDirectoryService directory)
    {
        _directory = directory;
        _view = CollectionViewSource.GetDefaultView(Rows);
        _view.Filter = o => o is DeletedObjectRow r && Matches(r, QuickFilter);
    }

    /// <summary>Whether a row survives the quick filter. Case-insensitive across the columns on screen.</summary>
    internal static bool Matches(DeletedObjectRow row, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var text = filter.Trim();
        return row.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || row.SamAccountName.Contains(text, StringComparison.OrdinalIgnoreCase)
            || row.LastKnownParentText.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnQuickFilterChanged(string value)
    {
        _view.Refresh();
        OnPropertyChanged(nameof(CountText));
    }

    partial void OnIncludeRecycledChanged(bool value) => _ = LoadAsync();

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Summary = "Reading Deleted Objects…";
        IsProblem = false;
        try
        {
            var result = await _directory.ListDeletedObjectsAsync(IncludeRecycled);

            Rows.Clear();
            foreach (var row in result.Rows) Rows.Add(row);

            Summary = DeletedObjects.Describe(result, _directory.Current?.DomainFqdn ?? string.Empty, IncludeRecycled);
            IsProblem = result.Status != DeletedObjectsStatus.Ok;
            _view.Refresh();
            OnPropertyChanged(nameof(CountText));
        }
        finally
        {
            IsBusy = false;
        }
    }
}
