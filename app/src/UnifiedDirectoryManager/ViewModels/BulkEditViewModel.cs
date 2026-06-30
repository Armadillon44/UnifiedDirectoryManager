using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Assembles a set of changes (set attribute, enable/disable, add/remove groups) and applies them
/// to multiple selected objects, reporting per-object success/failure.
/// </summary>
public partial class BulkEditViewModel : ObservableObject
{
    private readonly IDirectoryService _directory;
    private readonly IDialogService _dialogs;

    public IReadOnlyList<AdObjectRow> Targets { get; }
    public ObservableCollection<PendingChange> Changes { get; } = new();

    public IReadOnlyList<AttributeMeta> Attributes { get; } =
        AttributeCatalog.All.Where(a => !a.IsReadOnly && !a.IsDnValued && !a.IsMultiValued)
                            .OrderBy(a => a.Friendly, StringComparer.CurrentCultureIgnoreCase).ToList();

    [ObservableProperty] private AttributeMeta? _selectedAttribute;
    [ObservableProperty] private string _attributeValue = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _progress;
    [ObservableProperty] private string _status = string.Empty;

    public int TargetCount => Targets.Count;
    public bool Applied { get; private set; }

    public BulkEditViewModel(IDirectoryService directory, IDialogService dialogs, IReadOnlyList<AdObjectRow> targets)
    {
        _directory = directory;
        _dialogs = dialogs;
        Targets = targets;
        SelectedAttribute = Attributes.FirstOrDefault();
    }

    [RelayCommand]
    private void AddSetChange()
    {
        if (SelectedAttribute is null) return;
        Changes.Add(string.IsNullOrWhiteSpace(AttributeValue)
            ? new PendingChange { Op = ChangeOp.Clear, LdapName = SelectedAttribute.LdapName, FriendlyName = SelectedAttribute.Friendly }
            : new PendingChange { Op = ChangeOp.Set, LdapName = SelectedAttribute.LdapName, FriendlyName = SelectedAttribute.Friendly, Values = { AttributeValue.Trim() } });
        AttributeValue = string.Empty;
    }

    [RelayCommand] private void AddEnable() => Changes.Add(new PendingChange { Op = ChangeOp.Enable });
    [RelayCommand] private void AddDisable() => Changes.Add(new PendingChange { Op = ChangeOp.Disable });

    [RelayCommand]
    private void AddGroups()
    {
        var picked = _dialogs.PickObjects("Add to groups", AdObjectType.Group, multiSelect: true);
        if (picked is { Count: > 0 })
            Changes.Add(new PendingChange { Op = ChangeOp.AddToGroups, FriendlyName = string.Join(", ", picked.Select(p => p.Name)), Values = picked.Select(p => p.DistinguishedName).ToList() });
    }

    [RelayCommand]
    private void RemoveGroups()
    {
        var picked = _dialogs.PickObjects("Remove from groups", AdObjectType.Group, multiSelect: true);
        if (picked is { Count: > 0 })
            Changes.Add(new PendingChange { Op = ChangeOp.RemoveFromGroups, FriendlyName = string.Join(", ", picked.Select(p => p.Name)), Values = picked.Select(p => p.DistinguishedName).ToList() });
    }

    [RelayCommand] private void RemoveChange(PendingChange? change) { if (change is not null) Changes.Remove(change); }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (Changes.Count == 0) { _dialogs.Alert("Bulk edit", "Add at least one change first."); return; }

        var lines = new[] { $"Apply to {TargetCount} object(s):" }.Concat(Changes.Select(c => "• " + c.Describe()));
        if (!_dialogs.Confirm("Confirm bulk changes", $"This will modify {TargetCount} object(s).", lines))
            return;

        IsBusy = true;
        Progress = 0;
        Status = "Applying…";
        try
        {
            var progress = new Progress<int>(n => { Progress = n; Status = $"Applied to {n}/{TargetCount}…"; });
            var result = await _directory.BulkApplyAsync(Targets, Changes.ToList(), progress);
            Applied = result.SuccessCount > 0;
            Status = $"Done: {result.SuccessCount} succeeded, {result.FailureCount} failed.";
            _dialogs.ShowBulkResult(result);
        }
        catch (Exception ex)
        {
            Status = DirectoryService.Friendly(ex);
            _dialogs.Alert("Bulk edit failed", Status);
        }
        finally { IsBusy = false; }
    }
}
