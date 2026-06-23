using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>One source-user group membership offered for copying, with an Include checkbox.</summary>
public sealed partial class CopyGroupRow : ObservableObject
{
    [ObservableProperty] private bool _include = true;
    public string Name { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty; // "On-prem" / "Cloud" (shown beside the name)
    public string Id { get; init; } = string.Empty;      // on-prem DN, or Entra group id
    public bool IsCloud { get; init; }
}

/// <summary>
/// Copies a source user's group memberships onto another (existing) user. The operator unticks any they
/// don't want, picks the target user, and applies: on-prem groups via a single membership write, cloud-only
/// groups via Graph. Groups synced from on-prem AD are not listed separately — they carry across with their
/// on-prem group. The target's existing memberships are left untouched (this only adds).
/// </summary>
public partial class CopyGroupsViewModel : ObservableObject
{
    private readonly IDirectoryService _directory;
    private readonly IGraphService _graph;
    private readonly IDialogService _dialogs;
    private readonly string _sourceDn;

    public ObservableCollection<CopyGroupRow> Groups { get; } = new();
    public ObservableCollection<string> ProgressSteps { get; } = new();

    [ObservableProperty] private string _sourceDisplay = string.Empty;
    [ObservableProperty] private string _targetDisplay = "(none selected)";
    private string? _targetDn;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private string _status = string.Empty;

    /// <summary>True once at least one membership has been written, so the host can refresh.</summary>
    public bool Applied { get; private set; }

    public CopyGroupsViewModel(IDirectoryService directory, IGraphService graph, IDialogService dialogs, string sourceDn)
    {
        _directory = directory;
        _graph = graph;
        _dialogs = dialogs;
        _sourceDn = sourceDn;
        _sourceDisplay = NameResolver.RdnFallback(sourceDn);
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        Status = "Loading the source user's groups…";
        try
        {
            var attrs = await _directory.LoadObjectAsync(_sourceDn);
            var map = attrs.GroupBy(a => a.LdapName, StringComparer.OrdinalIgnoreCase)
                           .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            if (map.TryGetValue("displayName", out var disp) && disp.RawValues.Count > 0 && !string.IsNullOrWhiteSpace(disp.RawValues[0]))
                SourceDisplay = disp.RawValues[0];

            // On-prem group memberships (memberOf: DisplayValues are names, RawValues are DNs).
            if (map.TryGetValue("memberOf", out var memberOf))
                for (int i = 0; i < memberOf.DisplayValues.Count; i++)
                {
                    var dn = i < memberOf.RawValues.Count ? memberOf.RawValues[i] : memberOf.DisplayValues[i];
                    Groups.Add(new CopyGroupRow { Name = memberOf.DisplayValues[i], Id = dn, IsCloud = false, Detail = "On-prem" });
                }

            // Cloud-only group memberships (best-effort, when signed in). Synced groups are excluded — they
            // come across with their on-prem group above.
            if (_graph.IsSignedIn && map.TryGetValue("userPrincipalName", out var srcUpn) && srcUpn.RawValues.Count > 0)
            {
                try
                {
                    var cloud = await _graph.GetUserGroupsByUpnAsync(srcUpn.RawValues[0]);
                    foreach (var g in cloud.Where(g => !g.IsSynced))
                        Groups.Add(new CopyGroupRow { Name = g.DisplayName, Id = g.Id, IsCloud = true, Detail = "Cloud" });
                }
                catch (Exception ex) { AppLog.Instance.Warn("Could not load source cloud groups for copy-groups: " + ex.Message); }
            }

            Status = Groups.Count == 0
                ? "This user isn't a member of any groups."
                : $"Loaded {Groups.Count} group(s). Untick any you don't want, pick a target user, then Copy.";
        }
        catch (Exception ex) { Status = "Could not load the source user's groups: " + DirectoryService.Friendly(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand] private void SelectAll() { foreach (var g in Groups) g.Include = true; }
    [RelayCommand] private void SelectNone() { foreach (var g in Groups) g.Include = false; }

    [RelayCommand]
    private void PickTarget()
    {
        var picked = _dialogs.PickObjects("Select the target user", AdObjectType.User, multiSelect: false);
        if (picked is null || picked.Count == 0) return;
        _targetDn = picked[0].DistinguishedName;
        TargetDisplay = picked[0].Name;
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (string.IsNullOrWhiteSpace(_targetDn)) { Status = "Pick a target user first."; return; }
        if (string.Equals(_targetDn, _sourceDn, StringComparison.OrdinalIgnoreCase)) { Status = "The target user is the same as the source — pick a different user."; return; }

        var selected = Groups.Where(g => g.Include).ToList();
        if (selected.Count == 0) { Status = "Tick at least one group to copy."; return; }

        var onPrem = selected.Where(g => !g.IsCloud).ToList();
        var cloud = selected.Where(g => g.IsCloud).ToList();

        var lines = new List<string> { $"Copy {selected.Count} group membership(s) from “{SourceDisplay}” to “{TargetDisplay}”:", string.Empty };
        lines.AddRange(selected.Select(g => $"• {g.Detail}: {g.Name}"));
        if (cloud.Count > 0 && !_graph.IsSignedIn)
        {
            lines.Add(string.Empty);
            lines.Add("⚠ Not signed in to Entra ID — the cloud groups will be SKIPPED.");
        }
        if (!_dialogs.Confirm("Copy groups", $"Add “{TargetDisplay}” to {selected.Count} group(s)?", lines))
            return;

        IsBusy = true;
        ShowProgress = true;
        ProgressSteps.Clear();
        Status = "Copying…";
        try
        {
            if (onPrem.Count > 0)
            {
                Step($"• Adding to {onPrem.Count} on-prem group(s)…");
                var change = new PendingChange { Op = ChangeOp.AddToGroups, Values = onPrem.Select(g => g.Id).ToList() };
                await _directory.ApplyChangesAsync(_targetDn!, new[] { change });
                Applied = true;
                Step($"✓ Added to {onPrem.Count} on-prem group(s).");
            }

            if (cloud.Count > 0)
            {
                if (!_graph.IsSignedIn)
                {
                    Step("⚠ Skipped cloud groups — not signed in to Entra ID.");
                }
                else
                {
                    // Resolve the TARGET user's Entra object id (by UPN) to add it to the cloud groups.
                    var targetAttrs = await _directory.LoadObjectAsync(_targetDn!);
                    var targetUpn = targetAttrs.FirstOrDefault(a => a.LdapName.Equals("userPrincipalName", StringComparison.OrdinalIgnoreCase))
                                               ?.RawValues.FirstOrDefault();
                    var cloudId = string.IsNullOrWhiteSpace(targetUpn) ? null : (await _graph.GetUserByUpnAsync(targetUpn))?.Id;
                    if (cloudId is null)
                    {
                        Step("⚠ Couldn't find the target user in Entra ID (it may not be synced yet) — cloud groups were skipped.");
                    }
                    else
                    {
                        int ok = 0;
                        var errors = new List<string>();
                        foreach (var g in cloud)
                        {
                            try { await _graph.AddMemberToGroupAsync(g.Id, cloudId); ok++; Applied = true; }
                            catch (Exception ex) { errors.Add($"{g.Name}: {GraphErrors.Friendly(ex)}"); }
                        }
                        Step($"✓ Added to {ok} cloud group(s)" + (errors.Count > 0 ? $", {errors.Count} failed:" : "."));
                        foreach (var e in errors) Step("   ✗ " + e);
                    }
                }
            }

            Step(Applied ? $"Done — group memberships copied to “{TargetDisplay}”." : "Nothing was copied.");
        }
        catch (Exception ex)
        {
            Status = DirectoryService.Friendly(ex);
            Step("✗ " + Status);
        }
        finally { IsBusy = false; }
    }

    private void Step(string text)
    {
        ProgressSteps.Add(text);
        Status = text;
    }
}
