using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>A group the object belongs to, shown in the Member Of tab. Carries the display name, the
/// identifier (on-prem DN or cloud group id), the friendly <see cref="Source"/> ("On-prem"/"Cloud"),
/// the group <see cref="Kind"/> classification, and whether it's a cloud group (cloud-mastered group
/// membership can be removed; on-prem-synced cloud groups will be rejected by Graph and reported per-group).</summary>
public sealed partial class GroupMembership : ObservableObject
{
    public GroupMembership(string name, string dn, string source, bool isCloud, string kind = "", bool isExchange = false, string? smtp = null)
    {
        Name = name;
        Dn = dn;
        Source = source;
        IsCloud = isCloud;
        IsExchange = isExchange;
        Smtp = smtp;
        _kind = kind;
    }

    public string Name { get; }
    public string Dn { get; }
    public string Source { get; }
    public bool IsCloud { get; }

    /// <summary>True for an Exchange-managed cloud group (distribution list / mail-enabled security group):
    /// membership must be changed via the Exchange module, not Graph. <see cref="Smtp"/> is its primary SMTP,
    /// the identity Remove-DistributionGroupMember expects.</summary>
    public bool IsExchange { get; }
    public string? Smtp { get; }

    /// <summary>Friendly group classification. On-prem: scope+category from the groupType bitmask
    /// (e.g. "Security · Global", "Distribution · Universal") — filled asynchronously after load.
    /// Cloud: the Entra kind ("Microsoft 365" / "Security" / …). Blank until resolved.</summary>
    [ObservableProperty] private string _kind;

    public override string ToString() => Name;
}

/// <summary>An object that is a member of the selected group: its display <see cref="Name"/> and on-prem DN.</summary>
public sealed record GroupMemberRow(string Name, string Dn);

/// <summary>A user who reports to the selected user (their <c>manager</c> points at it): display <see cref="Name"/> and DN.</summary>
public sealed record DirectReportRow(string Name, string Dn);

/// <summary>
/// The editable attribute pane. Shows friendly-labelled fields (logic uses lDAPDisplayName),
/// resolves DN-valued attributes to names, and commits writes only after user confirmation.
/// </summary>
public partial class EditPaneViewModel : ObservableObject
{
    private readonly IDirectoryService _directory;
    private readonly IDialogService _dialogs;
    private readonly Action<string> _onError;
    private readonly IGraphService _graph;
    private readonly IExchangeService _exchange;

    // Cloud correlation keys for the selected object (used to add it to cloud groups).
    private string? _cloudUpn;
    private string? _cloudSid;
    private string? _cloudComputerName;

    /// <summary>Read-only Entra ID (cloud) view for the selected synced object; backs the Cloud tab.</summary>
    public CloudTabViewModel Cloud { get; }

    /// <summary>Read-only Exchange Online mailbox view for the selected user; backs the Exchange tab.</summary>
    public ExchangeTabViewModel Exchange { get; }

    /// <summary>Whether to show the Cloud tab (users, groups and computers can be synced to Entra).</summary>
    [ObservableProperty] private bool _showCloudTab;

    /// <summary>Whether to show the Exchange tab (users only — mailbox actions apply to user mailboxes).</summary>
    [ObservableProperty] private bool _showExchangeTab;

    private string? _dn;

    /// <summary>Raised after a successful write so the host can refresh the list view.</summary>
    public event Action? ObjectChanged;

    private readonly List<AdAttribute> _editableFields = new();
    private string? _pendingManagerDn; // null = unchanged, "" = clear, otherwise new DN

    [ObservableProperty] private string _title = "No object selected";
    [ObservableProperty] private bool _hasObject;
    [ObservableProperty] private bool _isUser;
    [ObservableProperty] private bool _isComputer;
    [ObservableProperty] private bool _isGroup;
    [ObservableProperty] private bool _isBusy;
    /// <summary>The objects that are members of the selected group (display name + DN; sortable in the UI).</summary>
    public ObservableCollection<GroupMemberRow> Members { get; } = new();
    /// <summary>Users who report to the selected user (the <c>directReports</c> back-link of <c>manager</c>).</summary>
    public ObservableCollection<DirectReportRow> DirectReports { get; } = new();
    [ObservableProperty] private string _managerDisplay = "(none)";

    /// <summary>The object's parent container/OU DN (shown in the General tab; changed via Move…).</summary>
    [ObservableProperty] private string _location = string.Empty;

    // Lockout status (read-only indicator; lockoutTime is an INTEGER8 FILETIME).
    [ObservableProperty] private bool _isLockedOut;
    [ObservableProperty] private string _lockoutStatus = "Not locked out";

    // Accidental-deletion protection (ACL-based; applies to any object type). Applied on Save.
    [ObservableProperty] private bool _isProtectedFromDeletion;
    private bool _originalProtected;

    // Account expiration (accountExpires is an INTEGER8 FILETIME, written via LDAP).
    [ObservableProperty] private bool _accountNeverExpires = true;
    [ObservableProperty] private DateTime _accountExpiresDate = DateTime.Today.AddMonths(1);
    [ObservableProperty] private string _accountExpiresTime = "00:00";
    private string _originalAccountExpires = "0";

    // Country picker (sets co / c / countryCode together, ADUC-style).
    public IReadOnlyList<CountryInfo> Countries => Services.Countries.All;
    [ObservableProperty] private CountryInfo? _selectedCountry;
    private string _originalCountryAlpha2 = string.Empty;

    public ObservableCollection<AdAttribute> General { get; } = new();
    public ObservableCollection<AdAttribute> Account { get; } = new();
    public ObservableCollection<AdAttribute> Address { get; } = new();
    public ObservableCollection<AdAttribute> Organization { get; } = new();
    public ObservableCollection<AdAttribute> Email { get; } = new();
    public ObservableCollection<string> ProxyAddresses { get; } = new();
    public ObservableCollection<GroupMembership> MemberOf { get; } = new();
    public ObservableCollection<AdAttribute> AllAttributes { get; } = new();

    public EditPaneViewModel(IDirectoryService directory, IDialogService dialogs, Action<string> onError, IGraphService graph, IExchangeService exchange)
    {
        _directory = directory;
        _dialogs = dialogs;
        _onError = onError;
        _graph = graph;
        _exchange = exchange;
        Cloud = new CloudTabViewModel(graph, exchange, dialogs);
        Exchange = new ExchangeTabViewModel(exchange, graph, dialogs);
    }

    public void Clear()
    {
        _dn = null;
        HasObject = false;
        Title = "No object selected";
        foreach (var c in new[] { General, Account, Address, Organization, Email }) c.Clear();
        ProxyAddresses.Clear();
        MemberOf.Clear();
        Members.Clear();
        DirectReports.Clear();
        AllAttributes.Clear();
        _editableFields.Clear();
        _pendingManagerDn = null;
        _originalProtected = false;
        IsProtectedFromDeletion = false;
        ShowCloudTab = false;
        ShowExchangeTab = false;
        _cloudUpn = _cloudSid = _cloudComputerName = null;
        Cloud.Reset();
        Exchange.Reset();
    }

    public async Task LoadAsync(string distinguishedName, AdObjectType type)
    {
        _dn = distinguishedName;
        Location = DirectoryService.ParentDn(distinguishedName);
        IsUser = type == AdObjectType.User;
        IsComputer = type == AdObjectType.Computer;
        IsGroup = type == AdObjectType.Group;
        IsBusy = true;
        try
        {
            var attrs = await _directory.LoadObjectAsync(distinguishedName);
            var map = attrs.GroupBy(a => a.LdapName, StringComparer.OrdinalIgnoreCase)
                           .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            General.Clear(); Account.Clear(); Address.Clear(); Organization.Clear(); Email.Clear();
            ProxyAddresses.Clear(); MemberOf.Clear(); Members.Clear(); DirectReports.Clear(); AllAttributes.Clear();
            _editableFields.Clear(); _pendingManagerDn = null;

            var (general, account, address, org, email) = FieldLayout(type);
            Populate(General, general, map);
            Populate(Account, account, map);
            Populate(Address, address, map);
            Populate(Organization, org, map);
            Populate(Email, email, map);

            // Account expiration
            LoadAccountExpiration(map);

            // Lockout status (informational; the Unlock action is always available for users)
            LoadLockoutStatus(map);

            // Country (match on the two-letter code, falling back to the friendly name)
            _originalCountryAlpha2 = map.TryGetValue("c", out var cAttr) && cAttr.RawValues.Count > 0 ? cAttr.RawValues[0] : string.Empty;
            SelectedCountry = Services.Countries.ByAlpha2(_originalCountryAlpha2)
                ?? (map.TryGetValue("co", out var coAttr) && coAttr.RawValues.Count > 0 ? Services.Countries.ByName(coAttr.RawValues[0]) : null)
                ?? Services.Countries.NotSet;

            // Manager (DN-valued, edited via picker)
            ManagerDisplay = map.TryGetValue("manager", out var mgr) && mgr.DisplayValues.Count > 0
                ? mgr.DisplayValues[0] : "(none)";

            // Direct reports (the read-only directReports back-link; add/remove writes the report's manager)
            if (map.TryGetValue("directReports", out var reports))
                for (int i = 0; i < reports.DisplayValues.Count; i++)
                {
                    var dn = i < reports.RawValues.Count ? reports.RawValues[i] : reports.DisplayValues[i];
                    DirectReports.Add(new DirectReportRow(reports.DisplayValues[i], dn));
                }

            // Proxy addresses (display only here; full editing via Attribute Editor)
            if (map.TryGetValue("proxyAddresses", out var proxies))
                foreach (var p in proxies.DisplayValues) ProxyAddresses.Add(p);

            // Group memberships (groups this object belongs to)
            if (map.TryGetValue("memberOf", out var memberOf))
            {
                for (int i = 0; i < memberOf.DisplayValues.Count; i++)
                {
                    var dn = i < memberOf.RawValues.Count ? memberOf.RawValues[i] : memberOf.DisplayValues[i];
                    MemberOf.Add(new GroupMembership(memberOf.DisplayValues[i], dn, "On-prem", isCloud: false));
                }
            }

            // Group members (objects inside this group) — only meaningful for groups
            if (IsGroup && map.TryGetValue("member", out var member))
            {
                for (int i = 0; i < member.DisplayValues.Count; i++)
                {
                    var dn = i < member.RawValues.Count ? member.RawValues[i] : member.DisplayValues[i];
                    Members.Add(new GroupMemberRow(member.DisplayValues[i], dn));
                }
            }

            PopulateAttributeEditor(attrs, type);

            // Accidental-deletion protection (read via the object's DACL).
            try { _originalProtected = await _directory.GetDeletionProtectionAsync(distinguishedName); }
            catch { _originalProtected = false; } // unreadable DACL: show as unprotected rather than failing the load
            IsProtectedFromDeletion = _originalProtected;

            Title = map.TryGetValue("displayName", out var dn0) && dn0.DisplayValues.Count > 0
                ? dn0.DisplayValues[0]
                : (map.TryGetValue("name", out var n) && n.DisplayValues.Count > 0 ? n.DisplayValues[0] : distinguishedName);

            // Unify with the Entra counterpart: a synced user/group/computer gets the Cloud tab, keyed by
            // the right correlation value (UPN for users; on-prem SID for groups; computer name for devices).
            ShowCloudTab = IsUser || IsGroup || IsComputer;
            _cloudUpn = IsUser && map.TryGetValue("userPrincipalName", out var u) && u.RawValues.Count > 0 ? u.RawValues[0] : null;
            _cloudSid = map.TryGetValue("objectSid", out var s) && s.DisplayValues.Count > 0 ? s.DisplayValues[0] : null; // formatted S-1-5-…
            _cloudComputerName = IsComputer
                ? (map.TryGetValue("cn", out var cn) && cn.RawValues.Count > 0 ? cn.RawValues[0]
                    : map.TryGetValue("name", out var nm) && nm.RawValues.Count > 0 ? nm.RawValues[0] : null)
                : null;
            Cloud.SetTarget(type, _cloudUpn, _cloudSid, _cloudComputerName);

            // Exchange tab: users only (mailbox actions are user-only), keyed by the same UPN.
            ShowExchangeTab = IsUser;
            Exchange.SetTarget(type, _cloudUpn);

            // Merge the user's Entra (cloud) group memberships into the Member Of tab (best-effort, when signed in).
            if (IsUser && _graph.IsSignedIn && !string.IsNullOrWhiteSpace(_cloudUpn))
                _ = LoadCloudMembershipsAsync(_cloudUpn!, distinguishedName);

            // Classify the on-prem groups (Security/Distribution + scope) for the Member Of "Type" column (best-effort).
            if (MemberOf.Any(m => !m.IsCloud))
                _ = LoadOnPremGroupKindsAsync(distinguishedName);

            HasObject = true;
        }
        catch (Exception ex)
        {
            _onError(DirectoryService.Friendly(ex));
            Clear();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (_dn is not null)
            await LoadAsync(_dn, IsUser ? AdObjectType.User
                : IsComputer ? AdObjectType.Computer
                : IsGroup ? AdObjectType.Group
                : AdObjectType.Unknown);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_dn is null) return;

        // Collect changes keyed by attribute so the same attribute edited in two places is applied once.
        var byLdap = new Dictionary<string, PendingChange>(StringComparer.OrdinalIgnoreCase);

        // Fixed-field tabs (General/Account/Address/Organization/Email)
        foreach (var field in _editableFields)
        {
            if (field.IsReadOnly || field.IsDnValued || field.IsMultiValued || !field.IsDirty) continue;
            byLdap[field.LdapName] = MakeSetOrClear(field.LdapName, field.FriendlyName, field.EditText);
        }

        // Attribute Editor edits (single-valued text + multi-valued via the value editor)
        foreach (var attr in AllAttributes)
        {
            if (attr.IsReadOnly) continue;
            if (attr.LdapName.Equals("manager", StringComparison.OrdinalIgnoreCase)) continue;
            if (attr.LdapName.Equals("accountExpires", StringComparison.OrdinalIgnoreCase)) continue;

            if (attr.IsMultiValued)
            {
                if (!attr.MultiValueEdited) continue;
                byLdap[attr.LdapName] = attr.RawValues.Count == 0
                    ? new PendingChange { Op = ChangeOp.Clear, LdapName = attr.LdapName, FriendlyName = attr.FriendlyName }
                    : new PendingChange { Op = ChangeOp.Set, LdapName = attr.LdapName, FriendlyName = attr.FriendlyName, Values = attr.RawValues.ToList() };
            }
            else if (attr.IsDirty)
            {
                byLdap[attr.LdapName] = MakeSetOrClear(attr.LdapName, attr.FriendlyName, attr.EditText);
            }
        }

        // Manager (via picker)
        if (_pendingManagerDn is not null)
        {
            byLdap["manager"] = string.IsNullOrEmpty(_pendingManagerDn)
                ? new PendingChange { Op = ChangeOp.Clear, LdapName = "manager", FriendlyName = "Manager" }
                : new PendingChange { Op = ChangeOp.Set, LdapName = "manager", FriendlyName = "Manager", Values = { _pendingManagerDn } };
        }

        // Account expiration
        var expiresChange = BuildAccountExpiresChange();
        if (expiresChange is not null)
            byLdap["accountExpires"] = expiresChange;

        // Country (co / c / countryCode set together)
        foreach (var change in BuildCountryChanges())
            byLdap[change.LdapName] = change;

        // Accidental-deletion protection (ACL change; synthetic key so it never collides with an attribute).
        if (IsProtectedFromDeletion != _originalProtected)
            byLdap["__deletionProtection__"] = new PendingChange
            {
                Op = IsProtectedFromDeletion ? ChangeOp.Protect : ChangeOp.Unprotect,
            };

        var changes = byLdap.Values.ToList();
        if (changes.Count == 0)
        {
            _dialogs.Alert("Save", "There are no changes to save.");
            return;
        }

        if (!_dialogs.Confirm("Confirm changes", $"Apply {changes.Count} change(s) to “{Title}”?", changes.Select(c => c.Describe())))
            return;

        await RunWrite(() => _directory.ApplyChangesAsync(_dn!, changes));
    }

    /// <summary>True when the pane has pending edits that a reload (e.g. after a Move) would discard.
    /// Mirrors the change-collection logic in <see cref="SaveAsync"/>.</summary>
    private bool HasUnsavedChanges()
    {
        if (_editableFields.Any(f => !f.IsReadOnly && !f.IsDnValued && !f.IsMultiValued && f.IsDirty))
            return true;
        foreach (var attr in AllAttributes)
        {
            if (attr.IsReadOnly) continue;
            if (attr.LdapName.Equals("manager", StringComparison.OrdinalIgnoreCase)) continue;
            if (attr.LdapName.Equals("accountExpires", StringComparison.OrdinalIgnoreCase)) continue;
            if (attr.IsMultiValued ? attr.MultiValueEdited : attr.IsDirty) return true;
        }
        if (_pendingManagerDn is not null) return true;
        if (BuildAccountExpiresChange() is not null) return true;
        if (BuildCountryChanges().Any()) return true;
        if (IsProtectedFromDeletion != _originalProtected) return true;
        return false;
    }

    [RelayCommand]
    private async Task AddToGroupsAsync()
    {
        if (_dn is null) return;
        var picked = _dialogs.PickGroupsHybrid($"Add “{Title}” to groups");
        if (picked is null || picked.Count == 0) return;

        var onPrem = picked.Where(g => g.Channel == GroupChannel.OnPremAd && g.Dn is not null).ToList();
        var cloud = picked.Where(g => g.Channel == GroupChannel.EntraGraph && g.CloudId is not null).ToList();
        var exchange = picked.Where(g => g.Channel == GroupChannel.ExchangeOnline).ToList();

        if (!_dialogs.Confirm("Confirm", $"Add “{Title}” to {picked.Count} group(s)?",
                picked.Select(g => $"{g.ChannelLabel}: {g.Name}")))
            return;

        IsBusy = true;
        try
        {
            // On-prem groups via the existing membership write.
            if (onPrem.Count > 0)
            {
                var change = new PendingChange { Op = ChangeOp.AddToGroups, Values = onPrem.Select(g => g.Dn!).ToList() };
                await _directory.ApplyChangesAsync(_dn!, new[] { change });
            }

            // Cloud groups: resolve this object's Entra id, then add it to each.
            var cloudErrors = new List<string>();
            if (cloud.Count > 0)
            {
                var cloudId = await ResolveCloudObjectIdAsync();
                if (cloudId is null)
                    cloudErrors.Add("Couldn't find this object in Entra ID (it may not be synced yet) — cloud groups were skipped.");
                else
                    foreach (var g in cloud)
                    {
                        try { await _graph.AddMemberToGroupAsync(g.CloudId!, cloudId); }
                        catch (Exception ex) { cloudErrors.Add($"{g.Name}: {GraphErrors.Friendly(ex)}"); }
                    }
            }

            // Exchange distribution / mail-enabled security groups: Graph can't add these, so use the EXO module.
            // The member identity is this user's UPN (mailboxes/users only).
            if (exchange.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(_cloudUpn))
                    cloudErrors.Add("No mailbox identity for this object — Exchange distribution groups were skipped.");
                else
                    foreach (var g in exchange)
                    {
                        var groupId = !string.IsNullOrWhiteSpace(g.Smtp) ? g.Smtp! : (g.CloudId ?? g.Name);
                        try { await _exchange.AddDistributionGroupMemberAsync(groupId, _cloudUpn!); }
                        catch (Exception ex) { cloudErrors.Add($"{g.Name} (Exchange): {ex.Message}"); }
                    }
            }

            await ReloadAsync();
            ObjectChanged?.Invoke();
            if (cloudErrors.Count > 0)
                _onError("Some cloud group additions didn't complete:\n" + string.Join("\n", cloudErrors));
        }
        catch (Exception ex) { _onError(DirectoryService.Friendly(ex)); }
        finally { IsBusy = false; }
    }

    /// <summary>Resolves the selected object's Entra object id (user by UPN, computer by name); null if not synced.</summary>
    private async Task<string?> ResolveCloudObjectIdAsync()
    {
        try
        {
            if (IsUser && !string.IsNullOrWhiteSpace(_cloudUpn))
                return (await _graph.GetUserByUpnAsync(_cloudUpn))?.Id;
            if (IsComputer && !string.IsNullOrWhiteSpace(_cloudComputerName))
                return (await _graph.GetDevicesByComputerAsync(_cloudComputerName, _cloudSid)).FirstOrDefault()?.Id;
        }
        catch (Exception ex) { AppLog.Instance.Warn("Cloud object-id resolution failed: " + ex.Message); }
        return null;
    }

    [RelayCommand]
    private async Task RemoveFromGroupAsync(System.Collections.IList? selected)
    {
        if (_dn is null) return;

        // The "Remove selected" button passes the list's SelectedItems so several groups can be
        // removed at once; fall back to nothing if the parameter is missing.
        var groups = selected?.Cast<GroupMembership>().ToList() ?? new List<GroupMembership>();
        if (groups.Count == 0) return;

        var onPrem = groups.Where(g => !g.IsCloud).ToList();
        var cloud = groups.Where(g => g.IsCloud && !g.IsExchange).ToList();   // Entra (Graph) groups
        var exchange = groups.Where(g => g.IsExchange).ToList();               // distribution / mail-enabled security

        var heading = groups.Count == 1
            ? $"Remove “{Title}” from this group?"
            : $"Remove “{Title}” from these {groups.Count} groups?";
        var lines = groups.Select(g => $"{g.Source}: {g.Name}").ToList();
        if (cloud.Count > 0)
            lines.Add("Note: membership of a group synced from on-prem AD is mastered on-prem and can't be removed in the cloud.");
        if (!_dialogs.Confirm("Confirm", heading, lines))
            return;

        IsBusy = true;
        try
        {
            // On-prem groups via the existing membership write.
            if (onPrem.Count > 0)
            {
                var change = new PendingChange { Op = ChangeOp.RemoveFromGroups, Values = onPrem.Select(g => g.Dn).ToList() };
                await _directory.ApplyChangesAsync(_dn!, new[] { change });
            }

            // Cloud (Graph) groups: resolve this object's Entra id, then remove it from each (the group id is the row's Dn).
            var cloudErrors = new List<string>();
            if (cloud.Count > 0)
            {
                var cloudId = await ResolveCloudObjectIdAsync();
                if (cloudId is null)
                    cloudErrors.Add("Couldn't find this object in Entra ID — cloud groups were skipped.");
                else
                    foreach (var g in cloud)
                    {
                        try { await _graph.RemoveMemberFromGroupAsync(g.Dn, cloudId); }
                        catch (Exception ex) { cloudErrors.Add($"{g.Name}: {GraphErrors.Friendly(ex)}"); }
                    }
            }

            // Exchange-managed groups (distribution / mail-enabled security): Graph can't modify these, so use the
            // Exchange module. The member identity is this user's UPN; the group is addressed by its primary SMTP.
            if (exchange.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(_cloudUpn))
                    cloudErrors.Add("No mailbox identity for this object — Exchange distribution groups were skipped.");
                else
                    foreach (var g in exchange)
                    {
                        var groupId = !string.IsNullOrWhiteSpace(g.Smtp) ? g.Smtp! : g.Name;
                        try { await _exchange.RemoveDistributionGroupMemberAsync(groupId, _cloudUpn!); }
                        catch (Exception ex) { cloudErrors.Add($"{g.Name} (Exchange): {ex.Message}"); }
                    }
            }

            await ReloadAsync();
            ObjectChanged?.Invoke();
            if (cloudErrors.Count > 0)
                _onError("Some cloud / Exchange group removals didn't complete:\n" + string.Join("\n", cloudErrors));
        }
        catch (Exception ex) { _onError(DirectoryService.Friendly(ex)); }
        finally { IsBusy = false; }
    }

    /// <summary>Copies this user's group memberships onto another user (the operator picks which in the dialog).
    /// The currently-open object is unchanged, so no reload is needed here.</summary>
    [RelayCommand]
    private void CopyGroupsToUser()
    {
        if (_dn is null) return;
        _dialogs.ShowCopyGroupsToUser(_dn);
    }

    /// <summary>Best-effort merge of the user's <b>cloud-only</b> Entra group memberships into the Member Of list
    /// (marked "Cloud"). Synced groups are deliberately excluded — they're already shown as their on-prem rows;
    /// listing their Entra twin too would conflate synced and cloud-only groups.</summary>
    private async Task LoadCloudMembershipsAsync(string upn, string forDn)
    {
        try
        {
            var groups = await _graph.GetUserGroupsByUpnAsync(upn);
            if (!string.Equals(_dn, forDn, StringComparison.OrdinalIgnoreCase)) return; // selection changed mid-load
            foreach (var g in groups.Where(g => !g.IsSynced))
                if (MemberOf.All(m => !(m.IsCloud && string.Equals(m.Dn, g.Id, StringComparison.OrdinalIgnoreCase))))
                    // Distribution lists / mail-enabled security groups are Exchange-managed — label their source
                    // "Exchange" (matching the unified group picker), not the generic "Cloud", and carry the SMTP
                    // so a removal from this tab can go through the Exchange module (Graph can't modify them).
                    MemberOf.Add(new GroupMembership(g.DisplayName, g.Id, g.IsExchangeManaged ? "Exchange" : "Cloud",
                        isCloud: true, kind: g.KindLabel, isExchange: g.IsExchangeManaged, smtp: g.Mail));
        }
        catch (Exception ex) { AppLog.Instance.Warn("Could not load cloud group memberships: " + ex.Message); }
    }

    /// <summary>Best-effort fill of the on-prem groups' <see cref="GroupMembership.Kind"/> column
    /// (Security/Distribution + scope, read from each group's <c>groupType</c> bitmask in one query).</summary>
    private async Task LoadOnPremGroupKindsAsync(string forDn)
    {
        try
        {
            var dns = MemberOf.Where(m => !m.IsCloud).Select(m => m.Dn)
                              .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (dns.Count == 0) return;
            var kinds = await _directory.GetGroupTypesAsync(dns);
            if (!string.Equals(_dn, forDn, StringComparison.OrdinalIgnoreCase)) return; // selection changed mid-load
            foreach (var m in MemberOf.Where(m => !m.IsCloud))
                if (kinds.TryGetValue(m.Dn, out var k) && !string.IsNullOrEmpty(k))
                    m.Kind = k;
        }
        catch (Exception ex) { AppLog.Instance.Warn("Could not classify on-prem groups: " + ex.Message); }
    }

    [RelayCommand]
    private async Task AddMembersAsync()
    {
        if (_dn is null || !IsGroup) return;
        var picked = _dialogs.PickObjects("Add members to group", AdObjectType.Unknown, multiSelect: true);
        if (picked is null || picked.Count == 0) return;

        if (!_dialogs.Confirm("Confirm", $"Add these object(s) as members of “{Title}”?", picked.Select(p => p.Name)))
            return;

        await RunWrite(() => _directory.AddMembersAsync(_dn!, picked.Select(p => p.DistinguishedName).ToList()));
    }

    [RelayCommand]
    private async Task RemoveMemberAsync(object? selected)
    {
        if (_dn is null || !IsGroup) return;
        // The view passes its SelectedItems (sorting reorders the view, so a row index would be unreliable —
        // act on the selected row objects directly).
        var rows = selected is System.Collections.IList list
            ? list.Cast<GroupMemberRow>().ToList()
            : new List<GroupMemberRow>();
        if (rows.Count == 0) return;

        var prompt = rows.Count == 1 ? "Remove this member from" : $"Remove these {rows.Count} members from";
        if (!_dialogs.Confirm("Confirm", $"{prompt} “{Title}”?", rows.Select(r => r.Name).ToList()))
            return;

        await RunWrite(() => _directory.RemoveMembersAsync(_dn!, rows.Select(r => r.Dn).ToList()));
    }

    [RelayCommand]
    private void EditAttribute(AdAttribute? attr)
    {
        if (attr is null || attr.IsReadOnly || !attr.IsMultiValued) return;
        var result = _dialogs.EditMultiValue(attr.FriendlyName, attr.RawValues);
        if (result is null) return;

        attr.RawValues.Clear();
        attr.DisplayValues.Clear();
        foreach (var v in result)
        {
            attr.RawValues.Add(v);
            attr.DisplayValues.Add(v);
        }
        attr.MultiValueEdited = true;
        attr.NotifyValuesChanged();
    }

    [RelayCommand]
    private async Task MoveAsync()
    {
        if (_dn is null) return;
        var currentParent = DirectoryService.ParentDn(_dn);
        var target = _dialogs.PickContainer(currentParent);
        if (target is null) return;
        if (string.Equals(target, currentParent, StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.Alert("Move", "The object is already in that OU.");
            return;
        }

        var lines = new List<string> { "From: " + currentParent, "To:   " + target };
        if (HasUnsavedChanges())
        {
            // The move reloads the object, so any pending attribute edits would be discarded — warn first.
            lines.Add(string.Empty);
            lines.Add("⚠ You have unsaved changes in the properties pane. Moving reloads the object, so those "
                    + "edits will be DISCARDED. Cancel and click Save first if you want to keep them.");
        }
        if (!_dialogs.Confirm("Move object", $"Move “{Title}” to another OU?", lines))
            return;

        IsBusy = true;
        try
        {
            _dn = await _directory.MoveObjectAsync(_dn!, target); // DN changes after a move
            await ReloadAsync();
            ObjectChanged?.Invoke();
        }
        catch (Exception ex) { _onError(DirectoryService.Friendly(ex)); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void PickManager()
    {
        var picked = _dialogs.PickObjects("Select manager", AdObjectType.User, multiSelect: false);
        if (picked is null || picked.Count == 0) return;
        _pendingManagerDn = picked[0].DistinguishedName;
        ManagerDisplay = picked[0].Name + " (pending save)";
    }

    [RelayCommand]
    private void ClearManager()
    {
        _pendingManagerDn = string.Empty;
        ManagerDisplay = "(none — pending save)";
    }

    /// <summary>Adds direct reports by setting each picked user's <c>manager</c> to this user. Applied immediately
    /// (it writes other objects, not the pane's pending edits), then reloads to refresh the directReports list.</summary>
    [RelayCommand]
    private async Task AddDirectReportsAsync()
    {
        if (_dn is null || !IsUser) return;
        var picked = _dialogs.PickObjects("Add direct reports", AdObjectType.User, multiSelect: true);
        if (picked is null || picked.Count == 0) return;

        // Can't be your own manager; skip any selection that resolves to this user.
        var targets = picked.Where(p => !string.Equals(p.DistinguishedName, _dn, StringComparison.OrdinalIgnoreCase)).ToList();
        if (targets.Count == 0) { _dialogs.Alert("Add direct reports", "A user can't be their own direct report."); return; }

        if (!_dialogs.Confirm("Confirm",
                $"Set “{Title}” as the manager of {targets.Count} user(s)?", targets.Select(p => p.Name)))
            return;

        IsBusy = true;
        try
        {
            var errors = new List<string>();
            foreach (var t in targets)
            {
                try
                {
                    var change = new PendingChange { Op = ChangeOp.Set, LdapName = "manager", FriendlyName = "Manager", Values = { _dn! } };
                    await _directory.ApplyChangesAsync(t.DistinguishedName, new[] { change });
                }
                catch (Exception ex) { errors.Add($"{t.Name}: {DirectoryService.Friendly(ex)}"); }
            }
            await ReloadAsync();
            ObjectChanged?.Invoke();
            if (errors.Count > 0) _onError("Some direct reports couldn't be set:\n" + string.Join("\n", errors));
        }
        catch (Exception ex) { _onError(DirectoryService.Friendly(ex)); }
        finally { IsBusy = false; }
    }

    /// <summary>Removes the selected direct reports by clearing each one's <c>manager</c> (they had this user as manager).</summary>
    [RelayCommand]
    private async Task RemoveDirectReportsAsync(System.Collections.IList? selected)
    {
        if (_dn is null || !IsUser) return;
        var rows = selected?.Cast<DirectReportRow>().ToList() ?? new List<DirectReportRow>();
        if (rows.Count == 0) return;

        var heading = rows.Count == 1
            ? $"Remove this direct report of “{Title}”? Their manager will be cleared."
            : $"Remove these {rows.Count} direct reports of “{Title}”? Their manager will be cleared.";
        if (!_dialogs.Confirm("Confirm", heading, rows.Select(r => r.Name)))
            return;

        IsBusy = true;
        try
        {
            var errors = new List<string>();
            foreach (var r in rows)
            {
                try
                {
                    var change = new PendingChange { Op = ChangeOp.Clear, LdapName = "manager", FriendlyName = "Manager" };
                    await _directory.ApplyChangesAsync(r.Dn, new[] { change });
                }
                catch (Exception ex) { errors.Add($"{r.Name}: {DirectoryService.Friendly(ex)}"); }
            }
            await ReloadAsync();
            ObjectChanged?.Invoke();
            if (errors.Count > 0) _onError("Some direct reports couldn't be removed:\n" + string.Join("\n", errors));
        }
        catch (Exception ex) { _onError(DirectoryService.Friendly(ex)); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private Task EnableAsync() => SetEnabledAsync(true);

    [RelayCommand]
    private Task DisableAsync() => SetEnabledAsync(false);

    private async Task SetEnabledAsync(bool enabled)
    {
        if (_dn is null) return;
        var verb = enabled ? "Enable" : "Disable";
        if (!_dialogs.Confirm(verb, $"{verb} account “{Title}”?", new[] { $"{verb} the account" }))
            return;
        var change = new PendingChange { Op = enabled ? ChangeOp.Enable : ChangeOp.Disable };
        await RunWrite(() => _directory.ApplyChangesAsync(_dn!, new[] { change }));
    }

    [RelayCommand]
    private async Task ResetPasswordAsync()
    {
        if (_dn is null || !IsUser) return;

        var request = _dialogs.PromptPasswordReset(Title);
        if (request is null) return; // cancelled

        // Confirm the action without ever showing the password itself.
        var lines = new List<string> { "Set a new password" };
        if (request.MustChangeAtNextLogon) lines.Add("Require a password change at next logon");
        if (request.Unlock) lines.Add("Unlock the account");
        if (!_dialogs.Confirm("Reset password", $"Reset the password for “{Title}”?", lines))
            return;

        await RunWrite(() => _directory.ResetPasswordAsync(
            _dn!, request.Password, request.MustChangeAtNextLogon, request.Unlock));
    }

    [RelayCommand]
    private async Task UnlockAsync()
    {
        if (_dn is null || !IsUser) return;
        if (!_dialogs.Confirm("Unlock", $"Unlock account “{Title}”?", new[] { "Unlock the account" }))
            return;
        await RunWrite(() => _directory.UnlockAccountAsync(_dn!));
    }

    private async Task RunWrite(Func<Task> write)
    {
        IsBusy = true;
        try
        {
            await write();
            await ReloadAsync();
            ObjectChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _onError(DirectoryService.Friendly(ex));
        }
        finally { IsBusy = false; }
    }

    private static PendingChange MakeSetOrClear(string ldapName, string friendly, string editText) =>
        string.IsNullOrWhiteSpace(editText)
            ? new PendingChange { Op = ChangeOp.Clear, LdapName = ldapName, FriendlyName = friendly }
            : new PendingChange { Op = ChangeOp.Set, LdapName = ldapName, FriendlyName = friendly, Values = { editText.Trim() } };

    private void LoadAccountExpiration(IReadOnlyDictionary<string, AdAttribute> map)
    {
        _originalAccountExpires = "0";
        AccountNeverExpires = true;
        if (map.TryGetValue("accountExpires", out var ae) && ae.RawValues.Count > 0
            && long.TryParse(ae.RawValues[0], out var ticks) && ticks > 0 && ticks != long.MaxValue)
        {
            try
            {
                var local = DateTime.FromFileTimeUtc(ticks).ToLocalTime();
                AccountNeverExpires = false;
                AccountExpiresDate = local.Date;
                AccountExpiresTime = local.ToString("HH:mm");
                _originalAccountExpires = ticks.ToString();
            }
            catch { /* leave as never on out-of-range values */ }
        }
    }

    /// <summary>Reads lockoutTime into the read-only lockout indicator (lockoutTime &gt; 0 means locked).</summary>
    private void LoadLockoutStatus(IReadOnlyDictionary<string, AdAttribute> map)
    {
        IsLockedOut = false;
        LockoutStatus = "Not locked out";
        if (map.TryGetValue("lockoutTime", out var lt) && lt.RawValues.Count > 0
            && long.TryParse(lt.RawValues[0], out var ticks) && ticks > 0)
        {
            try
            {
                var when = DateTime.FromFileTimeUtc(ticks).ToLocalTime();
                IsLockedOut = true;
                LockoutStatus = $"Locked out (since {when:yyyy-MM-dd HH:mm})";
            }
            catch { /* out-of-range value: leave as not locked */ }
        }
    }

    /// <summary>Builds co/c/countryCode changes if the selected country differs from what was loaded.</summary>
    private IEnumerable<PendingChange> BuildCountryChanges()
    {
        var selected = SelectedCountry ?? Services.Countries.NotSet;
        if (string.Equals(selected.Alpha2, _originalCountryAlpha2, StringComparison.OrdinalIgnoreCase))
            yield break;

        if (string.IsNullOrEmpty(selected.Alpha2))
        {
            // "(not set)" — clear all three.
            yield return new PendingChange { Op = ChangeOp.Clear, LdapName = "co", FriendlyName = "Country/region" };
            yield return new PendingChange { Op = ChangeOp.Clear, LdapName = "c", FriendlyName = "Country code" };
            yield return new PendingChange { Op = ChangeOp.Clear, LdapName = "countryCode", FriendlyName = "Country code (numeric)" };
        }
        else
        {
            yield return new PendingChange { Op = ChangeOp.Set, LdapName = "co", FriendlyName = "Country/region", Values = { selected.Name } };
            yield return new PendingChange { Op = ChangeOp.Set, LdapName = "c", FriendlyName = "Country code", Values = { selected.Alpha2 } };
            yield return new PendingChange { Op = ChangeOp.Set, LdapName = "countryCode", FriendlyName = "Country code (numeric)", Values = { selected.Numeric.ToString() } };
        }
    }

    /// <summary>Builds an accountExpires change if the expiry differs from what was loaded; else null.</summary>
    private PendingChange? BuildAccountExpiresChange()
    {
        // Only meaningful for users (computers don't surface this control).
        if (!IsUser) return null;

        string newValue;
        if (AccountNeverExpires)
        {
            newValue = "0";
        }
        else
        {
            var time = TimeSpan.TryParse(AccountExpiresTime, out var t) ? t : TimeSpan.Zero;
            var local = AccountExpiresDate.Date + time;
            if (local.Year < 1601) return null;
            newValue = local.ToFileTimeUtc().ToString();
        }

        if (string.Equals(newValue, _originalAccountExpires, StringComparison.Ordinal))
            return null;

        var friendly = AccountNeverExpires ? "Account expires: Never" : $"Account expires: {AccountExpiresDate:yyyy-MM-dd} {AccountExpiresTime}";
        return new PendingChange { Op = ChangeOp.Set, LdapName = "accountExpires", FriendlyName = friendly, Values = { newValue } };
    }

    private void Populate(ObservableCollection<AdAttribute> target, IEnumerable<string> ldapNames, IReadOnlyDictionary<string, AdAttribute> map)
    {
        foreach (var ldap in ldapNames)
        {
            var meta = AttributeCatalog.Meta(ldap);
            var field = new AdAttribute
            {
                LdapName = ldap,
                FriendlyName = meta.Friendly,
                IsMultiValued = meta.IsMultiValued,
                IsDnValued = meta.IsDnValued,
                IsReadOnly = meta.IsReadOnly,
            };

            if (map.TryGetValue(ldap, out var existing))
            {
                field.OriginalText = meta.IsReadOnly
                    ? existing.DisplaySummary
                    : (existing.RawValues.Count > 0 ? existing.RawValues[0] : string.Empty);
            }
            field.EditText = field.OriginalText;

            target.Add(field);
            if (!field.IsReadOnly && !field.IsMultiValued && !field.IsDnValued)
                _editableFields.Add(field);
        }
    }

    /// <summary>
    /// Fills the Attribute Editor: every set attribute (minus on-prem Exchange <c>msExch*</c> clutter)
    /// plus the curated, writable attributes that aren't set yet — shown as empty rows so they can be
    /// populated. Manager is handled by its own picker, so it's not surfaced here as a blank row.
    /// </summary>
    private void PopulateAttributeEditor(IReadOnlyList<AdAttribute> attrs, AdObjectType type)
    {
        var shown = new List<AdAttribute>();
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in attrs)
        {
            if (a.LdapName.StartsWith("msExch", StringComparison.OrdinalIgnoreCase)) continue;
            shown.Add(a);
            present.Add(a.LdapName);
        }

        foreach (var meta in AttributeCatalog.All)
        {
            if (meta.IsReadOnly || present.Contains(meta.LdapName)) continue;
            if (meta.LdapName.StartsWith("msExch", StringComparison.OrdinalIgnoreCase)) continue;
            if (meta.LdapName.Equals("manager", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsRelevantCategory(meta.Category, type)) continue;

            shown.Add(new AdAttribute
            {
                LdapName = meta.LdapName,
                FriendlyName = meta.Friendly,
                IsMultiValued = meta.IsMultiValued,
                IsDnValued = meta.IsDnValued,
                IsReadOnly = meta.IsReadOnly,
            });
        }

        foreach (var a in shown.OrderBy(a => a.FriendlyName, StringComparer.CurrentCultureIgnoreCase))
            AllAttributes.Add(a);
    }

    /// <summary>Whether an attribute category is worth offering as a settable blank row for this object type.</summary>
    private static bool IsRelevantCategory(AttributeCategory category, AdObjectType type) => type switch
    {
        AdObjectType.Computer => category is AttributeCategory.General or AttributeCategory.Computer
            or AttributeCategory.Account or AttributeCategory.Membership,
        AdObjectType.Group => category is AttributeCategory.General or AttributeCategory.Email
            or AttributeCategory.Membership,
        AdObjectType.User => category is AttributeCategory.General or AttributeCategory.Account
            or AttributeCategory.Address or AttributeCategory.Organization or AttributeCategory.Email
            or AttributeCategory.Membership,
        _ => category == AttributeCategory.General,
    };

    private static (string[] General, string[] Account, string[] Address, string[] Org, string[] Email) FieldLayout(AdObjectType type)
    {
        if (type == AdObjectType.Computer)
        {
            return (
                new[] { "cn", "dNSHostName", "description", "location", "operatingSystem", "operatingSystemVersion", "operatingSystemServicePack" },
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        }

        if (type == AdObjectType.Group)
        {
            return (
                new[] { "cn", "sAMAccountName", "displayName", "description", "mail", "info", "groupType" },
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        }

        return (
            General: new[] { "displayName", "givenName", "middleName", "sn", "initials", "description", "physicalDeliveryOfficeName", "telephoneNumber", "wWWHomePage" },
            Account: new[] { "sAMAccountName", "userPrincipalName", "userAccountControl", "pwdLastSet", "lastLogonTimestamp", "homeDirectory", "homeDrive", "scriptPath", "profilePath" },
            Address: new[] { "streetAddress", "l", "st", "postalCode" },
            Org: new[] { "title", "department", "company", "employeeID", "mobile", "homePhone", "pager", "facsimileTelephoneNumber", "ipPhone" },
            Email: new[] { "mail" });
    }
}
