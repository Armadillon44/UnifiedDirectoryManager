using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Read-only details of one cloud (Entra ID) object, shown in the properties pane and the
/// double-click properties window. Shows a quick summary immediately from the list row, then loads the
/// full grouped property set (and, for a user, licenses + memberships; for a group, members).
/// </summary>
public partial class CloudObjectDetailViewModel : ObservableObject
{
    private readonly IGraphService _graph;
    private readonly IDialogService _dialogs;
    private readonly IExchangeService _exchange; // for the license-removal mailbox guardrail
    private CloudObjectRow? _currentTarget; // guards stale async results on fast re-selection

    [ObservableProperty] private bool _hasTarget;
    // User action-bar visibility (cloud user writes).
    [ObservableProperty] private bool _showEnable;
    [ObservableProperty] private bool _showDisable;
    [ObservableProperty] private string _emptyHint = "Select an object to view its properties.";
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _kindLabel = string.Empty;
    [ObservableProperty] private bool _isUser;
    [ObservableProperty] private bool _isGroup;
    [ObservableProperty] private bool _isDevice;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = string.Empty;

    [ObservableProperty] private bool _hasLicenses;
    [ObservableProperty] private bool _hasMemberships;
    [ObservableProperty] private bool _hasMembers;
    [ObservableProperty] private bool _canAddMembers; // cloud-only group (synced group membership is on-prem-mastered)
    [ObservableProperty] private bool _canManageLicenses; // users only (assign/remove direct licenses)
    [ObservableProperty] private bool _canManageMemberships; // users + devices (add/remove cloud group membership)
    private string? _usageLocation; // captured at load; required before a license can be assigned

    public ObservableCollection<CloudPropertySection> Sections { get; } = new();
    public ObservableCollection<CloudLicense> Licenses { get; } = new();
    public ObservableCollection<CloudGroup> Memberships { get; } = new();
    public ObservableCollection<CloudMember> Members { get; } = new();

    public CloudObjectDetailViewModel(IGraphService graph, IExchangeService exchange, IDialogService dialogs)
    {
        _graph = graph;
        _exchange = exchange;
        _dialogs = dialogs;
    }

    /// <summary>Shows a row's details (null clears the pane).</summary>
    public void SetTarget(CloudObjectRow? row)
    {
        Clear();
        _currentTarget = row;
        if (row is null) { HasTarget = false; EmptyHint = "Select an object to view its properties."; return; }

        HasTarget = true;
        EmptyHint = string.Empty;
        Title = row.DisplayName;
        KindLabel = row.Kind switch
        {
            CloudObjectKind.User => "User",
            CloudObjectKind.Group => "Group",
            CloudObjectKind.Device => "Device",
            _ => "Object",
        };
        IsUser = row.Kind == CloudObjectKind.User;
        IsGroup = row.Kind == CloudObjectKind.Group;
        IsDevice = row.Kind == CloudObjectKind.Device;
        CanManageLicenses = IsUser;
        CanManageMemberships = IsUser || IsDevice;

        // Account-action visibility (the list row carries the current enabled state).
        var enabled = !string.Equals(row.Get("accountEnabled"), "No", StringComparison.OrdinalIgnoreCase);
        ShowDisable = IsUser && enabled;
        ShowEnable = IsUser && !enabled;

        // Instant summary from the list row, replaced by the full grouped set once it loads.
        Sections.Add(BuildSummary(row));
        _ = LoadDetailAsync(row);
    }

    public void Reset() => SetTarget(null);

    private void Clear()
    {
        Title = KindLabel = Status = string.Empty;
        IsUser = IsGroup = IsDevice = false;
        ShowEnable = ShowDisable = false;
        CanAddMembers = false;
        CanManageLicenses = false;
        CanManageMemberships = false;
        _usageLocation = null;
        UnwireSections();
        Sections.Clear();
        HasChanges = false;
        Licenses.Clear(); HasLicenses = false;
        Memberships.Clear(); HasMemberships = false;
        Members.Clear(); HasMembers = false;
    }

    // --- Editing (dirty tracking + Save/Revert) ---

    [ObservableProperty] private bool _hasChanges;

    partial void OnHasChangesChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    private void WireSections()
    {
        foreach (var p in Sections.SelectMany(s => s.Properties))
            p.PropertyChanged += OnPropertyValueChanged;
        RecomputeHasChanges();
    }

    private void UnwireSections()
    {
        foreach (var p in Sections.SelectMany(s => s.Properties))
            p.PropertyChanged -= OnPropertyValueChanged;
    }

    private void OnPropertyValueChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CloudProperty.Value)) RecomputeHasChanges();
    }

    private void RecomputeHasChanges() =>
        HasChanges = Sections.SelectMany(s => s.Properties).Any(p => p.IsDirty);

    private bool CanSave() => HasChanges && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var row = _currentTarget;
        if (row is null) return;
        var dirty = Sections.SelectMany(s => s.Properties).Where(p => p.IsDirty).ToList();
        if (dirty.Count == 0) return;

        var changes = dirty.ToDictionary(p => p.Key, p => string.IsNullOrWhiteSpace(p.Value) ? (string?)null : p.Value.Trim());
        var lines = dirty.Select(p => $"{p.Label}: {(string.IsNullOrWhiteSpace(p.Value) ? "(clear)" : p.Value)}");
        if (!_dialogs.Confirm("Save cloud changes", $"Apply {changes.Count} change(s) to “{row.DisplayName}”?", lines))
            return;

        IsBusy = true;
        try
        {
            if (row.Kind == CloudObjectKind.User) await _graph.UpdateUserAsync(row.Id, changes);
            else if (row.Kind == CloudObjectKind.Group) await _graph.UpdateGroupAsync(row.Id, changes);
            else { Status = "This object type can't be edited."; IsBusy = false; return; }

            Status = $"Saved {changes.Count} change(s).";
            SetTarget(row); // re-read live state (also clears IsBusy via load)
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("Cloud property save failed.", ex);
            Status = "Save failed: " + ex.Message;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Revert()
    {
        if (_currentTarget is { } row) SetTarget(row); // reload discards edits
    }

    /// <summary>Adds picked Entra users/devices to the current cloud group.</summary>
    [RelayCommand]
    private async Task AddMembersAsync()
    {
        var row = _currentTarget;
        if (row is null || row.Kind != CloudObjectKind.Group) return;

        var picked = _dialogs.PickCloudMembers($"Add members to “{row.DisplayName}”");
        if (picked is null || picked.Count == 0) return;
        if (!_dialogs.Confirm("Add members", $"Add {picked.Count} member(s) to “{row.DisplayName}”?",
                picked.Select(p => $"{p.Kind}: {p.DisplayName}")))
            return;

        IsBusy = true;
        var items = new List<BulkItemResult>();
        foreach (var m in picked)
        {
            try { await _graph.AddMemberToGroupAsync(row.Id, m.Id); items.Add(new BulkItemResult(m.Id, m.DisplayName, true, null)); }
            catch (Exception ex) { items.Add(new BulkItemResult(m.Id, m.DisplayName, false, GraphErrors.Friendly(ex))); }
        }
        IsBusy = false;
        _dialogs.ShowBulkResult(new BulkResult(items));
        SetTarget(row); // refresh the members list
    }

    /// <summary>Removes the selected members from the current cloud group.</summary>
    [RelayCommand]
    private async Task RemoveMembersAsync(System.Collections.IList? selected)
    {
        var row = _currentTarget;
        if (row is null || row.Kind != CloudObjectKind.Group) return;

        var members = selected?.Cast<CloudMember>().ToList() ?? new List<CloudMember>();
        if (members.Count == 0) { _dialogs.Alert("Remove members", "Select one or more members to remove."); return; }

        var heading = members.Count == 1
            ? $"Remove this member from “{row.DisplayName}”?"
            : $"Remove {members.Count} members from “{row.DisplayName}”?";
        if (!_dialogs.Confirm("Remove members", heading, members.Select(m => $"{m.ObjectType}: {m.DisplayName}")))
            return;

        IsBusy = true;
        var items = new List<BulkItemResult>();
        foreach (var m in members)
        {
            try { await _graph.RemoveMemberFromGroupAsync(row.Id, m.Id); items.Add(new BulkItemResult(m.Id, m.DisplayName, true, null)); }
            catch (Exception ex) { items.Add(new BulkItemResult(m.Id, m.DisplayName, false, GraphErrors.Friendly(ex))); }
        }
        IsBusy = false;
        _dialogs.ShowBulkResult(new BulkResult(items));
        SetTarget(row); // refresh the members list
    }

    private static CloudPropertySection BuildSummary(CloudObjectRow row)
    {
        // Instant read-only placeholder from the list row; replaced by the full classified sections on load.
        var headers = CloudColumnCatalog.Headers(ModeFor(row.Kind));
        var props = new List<CloudProperty>
        {
            new("displayName", "Display name", row.DisplayName, CloudPropertyEditability.SystemReadOnly, null),
        };
        foreach (var kv in row.Values)
            props.Add(new CloudProperty(kv.Key, headers.TryGetValue(kv.Key, out var h) ? h : kv.Key,
                string.IsNullOrEmpty(kv.Value) ? "—" : kv.Value, CloudPropertyEditability.SystemReadOnly, null));
        return new CloudPropertySection("Summary", props);
    }

    private async Task LoadDetailAsync(CloudObjectRow row)
    {
        if (!_graph.IsSignedIn) return;
        IsBusy = true;
        try
        {
            var sections = await _graph.GetObjectDetailAsync(row.Id, row.Kind);
            if (!ReferenceEquals(_currentTarget, row)) return; // selection moved on
            if (sections.Count > 0)
            {
                UnwireSections();
                Sections.Clear();
                foreach (var s in sections) Sections.Add(s);
                WireSections();
            }

            if (IsUser)
            {
                var upn = row.Get("userPrincipalName");
                if (!string.IsNullOrEmpty(upn))
                {
                    var info = await _graph.GetUserByUpnAsync(upn);
                    if (!ReferenceEquals(_currentTarget, row)) return;
                    if (info is not null)
                    {
                        _usageLocation = info.UsageLocation;
                        foreach (var l in info.Licenses) Licenses.Add(l);
                        HasLicenses = Licenses.Count > 0;
                        foreach (var g in info.Groups) Memberships.Add(g);
                        HasMemberships = Memberships.Count > 0;
                    }
                }
            }
            else if (IsGroup)
            {
                // Members can be managed here only for cloud-only, assigned (non-dynamic) groups: synced group
                // membership is on-prem-mastered, and a dynamic group's membership is rule-managed by Entra.
                var origin = Sections.SelectMany(s => s.Properties).FirstOrDefault(p => p.Key == "origin")?.Value;
                var rule = Sections.SelectMany(s => s.Properties).FirstOrDefault(p => p.Key == "membershipRule")?.Value;
                var isDynamic = !string.IsNullOrWhiteSpace(rule) && rule != "—";
                CanAddMembers = !string.Equals(origin, "Synced", StringComparison.OrdinalIgnoreCase) && !isDynamic;

                var members = await _graph.GetGroupMembersAsync(row.Id);
                if (!ReferenceEquals(_currentTarget, row)) return;
                foreach (var m in members) Members.Add(m);
                HasMembers = Members.Count > 0;
            }
            else if (IsDevice)
            {
                // Devices can be group members too — load their memberships so they can be managed here.
                var groups = await _graph.GetObjectMemberOfAsync(row.Id, row.Kind);
                if (!ReferenceEquals(_currentTarget, row)) return;
                foreach (var g in groups) Memberships.Add(g);
                HasMemberships = Memberships.Count > 0;
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("Could not load full cloud details: " + ex.Message);
            if (ReferenceEquals(_currentTarget, row)) Status = "Could not load full details: " + ex.Message;
        }
        finally { if (ReferenceEquals(_currentTarget, row)) IsBusy = false; }
    }

    // --- Cloud user write actions (confirm first) ---

    [RelayCommand] private Task EnableAsync() => SetEnabledAsync(true);
    [RelayCommand] private Task DisableAsync() => SetEnabledAsync(false);

    private async Task SetEnabledAsync(bool enabled)
    {
        var row = _currentTarget;
        if (row is null || row.Kind != CloudObjectKind.User) return;
        var verb = enabled ? "Enable" : "Disable";
        if (!_dialogs.Confirm($"{verb} account", $"{verb} the cloud account “{row.DisplayName}”?",
                new[] { $"{verb} {row.DisplayName}" }))
            return;
        IsBusy = true;
        try
        {
            await _graph.SetUserAccountEnabledAsync(row.Id, enabled);
            Status = $"{verb}d {row.DisplayName}.";
            SetTarget(row); // re-read live state
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("Cloud user enable/disable failed.", ex);
            Status = $"{verb} failed: " + ex.Message;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RevokeSessionsAsync()
    {
        var row = _currentTarget;
        if (row is null || row.Kind != CloudObjectKind.User) return;
        if (!_dialogs.Confirm("Revoke sessions", $"Revoke all sign-in sessions for “{row.DisplayName}”?",
                new[] { "Invalidates refresh tokens — the user must sign in again everywhere." }))
            return;
        IsBusy = true;
        try
        {
            await _graph.RevokeSignInSessionsAsync(row.Id);
            Status = $"Revoked sign-in sessions for {row.DisplayName}.";
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("Revoke sessions failed.", ex);
            Status = "Revoke failed: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    // --- License management (users only; prefer group-based licensing) ---

    /// <summary>Assigns one or more directly-chosen license SKUs, nudging toward group membership where it exists.</summary>
    [RelayCommand]
    private async Task AssignLicenseAsync()
    {
        var row = _currentTarget;
        if (row is null || row.Kind != CloudObjectKind.User) return;

        // Microsoft requires a usage location before any license assignment.
        var usage = !string.IsNullOrWhiteSpace(_usageLocation) ? _usageLocation : row.Get("usageLocation");
        if (string.IsNullOrWhiteSpace(usage))
        {
            _dialogs.Alert("Assign license",
                "Set the user's Usage location first (in the properties above, then Save) — Microsoft requires it before a license can be assigned.");
            return;
        }

        IReadOnlyList<CloudSku> all;
        IsBusy = true;
        try { all = await _graph.GetSubscribedSkusAsync(); }
        catch (Exception ex) { IsBusy = false; Status = "Couldn't read tenant SKUs: " + ex.Message; return; }
        IsBusy = false;

        // Offer SKUs the user doesn't already hold directly (a group-only SKU can still be added directly).
        var heldDirect = new HashSet<Guid>(Licenses.Where(l => l.HasDirect).Select(l => l.SkuId));
        var candidates = all.Where(s => !heldDirect.Contains(s.SkuId)).ToList();
        if (candidates.Count == 0)
        {
            _dialogs.Alert("Assign license", "The user already holds every available license directly.");
            return;
        }

        var picked = _dialogs.PickLicenses($"Assign licenses to “{row.DisplayName}”", candidates);
        if (picked is null || picked.Count == 0) return;

        IsBusy = true;
        var items = new List<BulkItemResult>();
        foreach (var sku in picked)
        {
            // Group-first guardrail: when a group already grants this SKU, recommend group membership.
            if (sku.HasGroupAssignment)
            {
                var lines = new List<string> { "This license is granted by group-based licensing via:" };
                lines.AddRange(sku.AssigningGroups.Select(g => "• " + g));
                lines.Add("Adding the user to one of those groups is preferred over a direct assignment.");
                lines.Add("Continue with a DIRECT assignment anyway?");
                if (!_dialogs.Confirm("Prefer group membership?", $"“{sku.FriendlyName}” is available via a group", lines))
                {
                    items.Add(new BulkItemResult(sku.SkuId.ToString(), sku.FriendlyName, false, "Skipped — use group membership instead"));
                    continue;
                }
            }
            try { await _graph.AssignLicenseToUserAsync(row.Id, sku.SkuId); items.Add(new BulkItemResult(sku.SkuId.ToString(), sku.FriendlyName, true, null)); }
            catch (Exception ex) { items.Add(new BulkItemResult(sku.SkuId.ToString(), sku.FriendlyName, false, GraphErrors.Friendly(ex))); }
        }
        IsBusy = false;
        _dialogs.ShowBulkResult(new BulkResult(items));
        SetTarget(row); // re-read live license state
    }

    /// <summary>Removes the selected directly-assigned licenses; group-inherited ones are skipped (change the group).</summary>
    [RelayCommand]
    private async Task RemoveLicensesAsync(System.Collections.IList? selected)
    {
        var row = _currentTarget;
        if (row is null || row.Kind != CloudObjectKind.User) return;

        var lics = selected?.Cast<CloudLicense>().ToList() ?? new List<CloudLicense>();
        if (lics.Count == 0) { _dialogs.Alert("Remove license", "Select one or more licenses to remove."); return; }

        var removable = lics.Where(l => l.CanRemoveDirectly).ToList();
        var inheritedOnly = lics.Where(l => l.IsInheritedOnly).ToList();
        if (removable.Count == 0)
        {
            _dialogs.Alert("Remove license",
                "The selected license(s) are inherited from a group and can't be removed here — remove the user from the assigning group instead (see the “Assigned via” column).");
            return;
        }

        var heading = removable.Count == 1
            ? $"Remove this license from “{row.DisplayName}”?"
            : $"Remove {removable.Count} licenses from “{row.DisplayName}”?";
        var lines = removable.Select(l => l.FriendlyName).ToList();
        if (inheritedOnly.Count > 0)
            lines.Add($"(skipping {inheritedOnly.Count} group-inherited license(s) — remove via the group)");

        // Guardrail: unlicensing a REGULAR mailbox deletes it after ~30 days; a shared mailbox survives. If we can
        // determine (best-effort) the user still has a regular mailbox, warn prominently in the confirm dialog.
        await AddMailboxGuardrailAsync(row, lines);

        if (!_dialogs.Confirm("Remove license", heading, lines)) return;

        IsBusy = true;
        var items = new List<BulkItemResult>();
        foreach (var l in removable)
        {
            try { await _graph.RemoveLicenseFromUserAsync(row.Id, l.SkuId); items.Add(new BulkItemResult(l.SkuId.ToString(), l.FriendlyName, true, null)); }
            catch (Exception ex) { items.Add(new BulkItemResult(l.SkuId.ToString(), l.FriendlyName, false, GraphErrors.Friendly(ex))); }
        }
        IsBusy = false;
        _dialogs.ShowBulkResult(new BulkResult(items));
        SetTarget(row); // re-read live license state
    }

    /// <summary>Best-effort: if the user still has a REGULAR mailbox, append a strong caution to the confirm
    /// lines — removing an Exchange-providing license from a regular mailbox deletes it after ~30 days, whereas
    /// a shared mailbox survives unlicensed. Skipped silently when Exchange can't be reached.</summary>
    private async Task AddMailboxGuardrailAsync(CloudObjectRow row, List<string> lines)
    {
        if (!_exchange.IsConfigured || !_graph.IsSignedIn) return; // no connectable Exchange session to check with
        var mailboxId = MailboxIdentityFor(row);
        if (mailboxId is null) return;
        try
        {
            var mb = await _exchange.GetMailboxAsync(mailboxId);
            if (mb is not null && mb.Type == MailboxType.Regular)
            {
                lines.Add(string.Empty);
                lines.Add("⚠ This user still has a REGULAR mailbox. If a removed license provides Exchange Online, "
                        + "the mailbox will be DELETED after ~30 days. Convert it to a shared mailbox first "
                        + "(ExOL tab ▸ Convert to Shared) to keep it after unlicensing.");
            }
        }
        catch (Exception ex) { AppLog.Instance.Warn("License guardrail: couldn't check the mailbox type: " + ex.Message); }
    }

    private static string? MailboxIdentityFor(CloudObjectRow row)
    {
        var upn = row.Get("userPrincipalName");
        if (!string.IsNullOrWhiteSpace(upn)) return upn;
        var mail = row.Get("mail");
        return string.IsNullOrWhiteSpace(mail) ? null : mail;
    }

    // --- Cloud group membership (users + devices; add/remove this object to/from Entra groups) ---

    /// <summary>Adds this object to one or more picked Entra groups.</summary>
    [RelayCommand]
    private async Task AddToGroupsAsync()
    {
        var row = _currentTarget;
        if (row is null || !(row.Kind == CloudObjectKind.User || row.Kind == CloudObjectKind.Device)) return;

        var picked = _dialogs.PickCloudGroups($"Add “{row.DisplayName}” to Entra groups");
        if (picked is null || picked.Count == 0) return;
        if (!_dialogs.Confirm("Add to groups", $"Add “{row.DisplayName}” to {picked.Count} group(s)?",
                picked.Select(g => $"{g.GroupKind}: {g.DisplayName}")))
            return;

        IsBusy = true;
        var items = new List<BulkItemResult>();
        foreach (var g in picked)
        {
            // Membership of dynamic groups (rule-managed) and on-prem-synced groups can't be set directly.
            if (string.Equals(g.MembershipKind, "Dynamic", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new BulkItemResult(g.Id, g.DisplayName, false, "Dynamic group — membership is rule-managed by Entra; can't add directly."));
                continue;
            }
            if (string.Equals(g.Origin, "Synced", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new BulkItemResult(g.Id, g.DisplayName, false, "Synced from on-prem AD — manage this membership in Active Directory."));
                continue;
            }
            try { await _graph.AddMemberToGroupAsync(g.Id, row.Id); items.Add(new BulkItemResult(g.Id, g.DisplayName, true, null)); }
            catch (Exception ex) { items.Add(new BulkItemResult(g.Id, g.DisplayName, false, GraphErrors.Friendly(ex))); }
        }
        IsBusy = false;
        _dialogs.ShowBulkResult(new BulkResult(items));
        SetTarget(row); // refresh memberships
    }

    /// <summary>Removes this object from the selected Entra groups (synced groups are on-prem-mastered → reported).</summary>
    [RelayCommand]
    private async Task RemoveFromGroupsAsync(System.Collections.IList? selected)
    {
        var row = _currentTarget;
        if (row is null || !(row.Kind == CloudObjectKind.User || row.Kind == CloudObjectKind.Device)) return;

        var groups = selected?.Cast<CloudGroup>().ToList() ?? new List<CloudGroup>();
        if (groups.Count == 0) { _dialogs.Alert("Remove from groups", "Select one or more groups to remove."); return; }

        var heading = groups.Count == 1
            ? $"Remove “{row.DisplayName}” from this group?"
            : $"Remove “{row.DisplayName}” from these {groups.Count} groups?";
        var lines = groups.Select(g => $"{g.GroupKind}: {g.DisplayName}").ToList();
        if (groups.Any(g => string.Equals(g.Origin, "Synced", StringComparison.OrdinalIgnoreCase)))
            lines.Add("Note: membership of a group synced from on-prem AD is mastered on-prem and can't be removed in the cloud.");
        if (groups.Any(g => string.Equals(g.MembershipKind, "Dynamic", StringComparison.OrdinalIgnoreCase)))
            lines.Add("Note: dynamic-group membership is rule-managed by Entra and can't be removed directly.");
        if (!_dialogs.Confirm("Remove from groups", heading, lines)) return;

        IsBusy = true;
        var items = new List<BulkItemResult>();
        foreach (var g in groups)
        {
            if (string.Equals(g.MembershipKind, "Dynamic", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new BulkItemResult(g.Id, g.DisplayName, false, "Dynamic group — membership is rule-managed by Entra; can't remove directly."));
                continue;
            }
            if (string.Equals(g.Origin, "Synced", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new BulkItemResult(g.Id, g.DisplayName, false, "Synced from on-prem AD — manage this membership in Active Directory."));
                continue;
            }
            try { await _graph.RemoveMemberFromGroupAsync(g.Id, row.Id); items.Add(new BulkItemResult(g.Id, g.DisplayName, true, null)); }
            catch (Exception ex) { items.Add(new BulkItemResult(g.Id, g.DisplayName, false, GraphErrors.Friendly(ex))); }
        }
        IsBusy = false;
        _dialogs.ShowBulkResult(new BulkResult(items));
        SetTarget(row); // refresh memberships
    }

    private static CloudListMode ModeFor(CloudObjectKind kind) => kind switch
    {
        CloudObjectKind.Group => CloudListMode.Groups,
        CloudObjectKind.Device => CloudListMode.Devices,
        _ => CloudListMode.Users,
    };
}
