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

    /// <summary>Whether this row is as loaded, or added/removed on screen and waiting for Save.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingLabel))]
    private PendingMembership _pending;

    public string PendingLabel => PendingMembershipText.Label(Pending);

    public override string ToString() => Name;
}

/// <summary>A row in a membership list that can be pending a change. Lets one helper serve two lists.</summary>
public interface IPendingRow
{
    string Name { get; }
    string Dn { get; }
    PendingMembership Pending { get; }
}

/// <summary>An object that is a member of the selected group: its display <see cref="Name"/> and on-prem DN.</summary>
public sealed record GroupMemberRow(string Name, string Dn) : IPendingRow
{
    /// <summary>As loaded, or added/removed on screen and waiting for Save.</summary>
    public PendingMembership Pending { get; init; }

    public string PendingLabel => PendingMembershipText.Label(Pending);
}

/// <summary>A user who reports to the selected user (their <c>manager</c> points at it): display <see cref="Name"/> and DN.</summary>
public sealed record DirectReportRow(string Name, string Dn) : IPendingRow
{
    /// <summary>As loaded, or added/removed on screen and waiting for Save.</summary>
    public PendingMembership Pending { get; init; }

    public string PendingLabel => PendingMembershipText.Label(Pending);
}

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

    /// <summary>
    /// Bumped by every load. A load whose token is stale has been superseded and must not touch the pane —
    /// it would repopulate every field from the object it read while <see cref="_dn"/> names a different
    /// one, and the next Save would write those values to the wrong object.
    ///
    /// A token rather than a DN comparison because the pane reloads the SAME object after a save, so
    /// "is this still the same DN" cannot tell a superseded load from the current one.
    /// </summary>
    private int _loadToken;

    /// <summary>Raised after a successful write so the host can refresh the list view.</summary>
    public event Action? ObjectChanged;

    private readonly List<AdAttribute> _editableFields = new();
    private string? _pendingManagerDn; // null = unchanged, "" = clear, otherwise new DN
    private string? _pendingManagedByDn; // groups: null = unchanged, "" = clear, otherwise new DN

    // Group scope/category, decoded from the groupType bitmask and edited via the two General-tab combos.
    private GroupScope _originalGroupScope;
    private GroupCategory _originalGroupCategory;
    private bool _groupTypeKnown; // false when groupType is missing/unparseable (e.g. a built-in group)

    [ObservableProperty] private string _title = "No object selected";
    [ObservableProperty] private bool _hasObject;
    [ObservableProperty] private bool _isUser;
    [ObservableProperty] private bool _isComputer;
    [ObservableProperty] private bool _isGroup;
    /// <summary>
    /// True while the pane is reading or writing. The view shows a moving bar with <see cref="BusyMessage"/>,
    /// disables the pane and shows a busy cursor. Cloud and Exchange calls take seconds; without this the
    /// pane looked hung.
    /// </summary>
    [ObservableProperty] private bool _isBusy;
    /// <summary>What the pane is doing while <see cref="IsBusy"/>: "Removing from cloud group Sales… (2 of 5)".</summary>
    [ObservableProperty] private string _busyMessage = string.Empty;
    /// <summary>
    /// True while a user's cloud groups are still being read into Member Of. That read runs after the pane is
    /// shown and can take seconds, so until it finishes the list is missing its cloud groups. This flag tells
    /// the operator so.
    /// </summary>
    [ObservableProperty] private bool _isLoadingCloudGroups;
    /// <summary>The save now writing, if any. See <see cref="ConfirmLeaveAsync"/>.</summary>
    private Task<bool>? _saving;
    private int _stepIndex, _stepCount;
    /// <summary>The objects that are members of the selected group (display name + DN; sortable in the UI).</summary>
    public ObservableCollection<GroupMemberRow> Members { get; } = new();
    /// <summary>Users who report to the selected user (the <c>directReports</c> back-link of <c>manager</c>).</summary>
    public ObservableCollection<DirectReportRow> DirectReports { get; } = new();
    [ObservableProperty] private string _managerDisplay = "(none)";

    /// <summary>Groups: the object that manages/owns the group (<c>managedBy</c>), edited via a picker.</summary>
    [ObservableProperty] private string _managedByDisplay = "(none)";

    // Group scope + category, written back as the groupType bitmask. ShowGroupType is false when groupType
    // couldn't be decoded (built-in/system groups), so the editor isn't offered for something we can't safely set.
    [ObservableProperty] private GroupScope _groupScope;
    [ObservableProperty] private GroupCategory _groupCategory;
    [ObservableProperty] private bool _showGroupType;

    public IReadOnlyList<OptionItem<GroupScope>> GroupScopeOptions { get; } = new[]
    {
        new OptionItem<GroupScope>(GroupScope.Global, "Global"),
        new OptionItem<GroupScope>(GroupScope.DomainLocal, "Domain local"),
        new OptionItem<GroupScope>(GroupScope.Universal, "Universal"),
    };

    public IReadOnlyList<OptionItem<GroupCategory>> GroupCategoryOptions { get; } = new[]
    {
        new OptionItem<GroupCategory>(GroupCategory.Security, "Security"),
        new OptionItem<GroupCategory>(GroupCategory.Distribution, "Distribution"),
    };

    /// <summary>The object's parent container/OU DN (shown in the General tab; changed via Move…).</summary>
    [ObservableProperty] private string _location = string.Empty;

    // Lockout status (read-only indicator; lockoutTime is an INTEGER8 FILETIME).
    [ObservableProperty] private bool _isLockedOut;
    [ObservableProperty] private string _lockoutStatus = "Not locked out";

    // Accidental-deletion protection (ACL-based; applies to any object type). Applied on Save.
    [ObservableProperty] private bool _isProtectedFromDeletion;
    private bool _originalProtected;

    // Password options (users only). Applied on Save. Stored in different places -- one is a
    // userAccountControl flag, the other a DACL permission -- see PasswordOptions.
    [ObservableProperty] private bool _passwordNeverExpires;
    [ObservableProperty] private bool _cannotChangePassword;
    private bool _originalPasswordNeverExpires;
    private bool _originalCannotChangePassword;

    /// <summary>
    /// False until the password options have actually been read.
    /// </summary>
    /// <remarks>
    /// The two checkboxes start unticked, and unticked is also a legitimate value. Without this a failed
    /// read would leave them looking like a real "no", and saving would then actively CLEAR a never-expires
    /// flag or a cannot-change permission that the account really has.
    /// </remarks>
    [ObservableProperty] private bool _passwordOptionsKnown;

    /// <summary>True only when the read was attempted and FAILED -- not while it is still in flight.</summary>
    [ObservableProperty] private bool _passwordOptionsUnreadable;

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
        _loadToken++; // cancel any in-flight load: it must not repopulate the pane we are clearing
        _dn = null;
        HasObject = false;
        IsLoadingCloudGroups = false;
        // The cancelled load will not let go of IsBusy itself: its finally only does that while it is still the
        // current load, and the new token just made it stale. Without this the empty pane says "Loading…" forever.
        IsBusy = false;
        Title = "No object selected";
        foreach (var c in new[] { General, Account, Address, Organization, Email }) c.Clear();
        ProxyAddresses.Clear();
        MemberOf.Clear();
        Members.Clear();
        DirectReports.Clear();
        AllAttributes.Clear();
        _editableFields.Clear();
        _pendingManagerDn = null;
        _pendingManagedByDn = null;
        ManagedByDisplay = "(none)";
        _groupTypeKnown = false;
        ShowGroupType = false;
        _originalProtected = false;
        IsProtectedFromDeletion = false;
        _originalPasswordNeverExpires = _originalCannotChangePassword = false;
        PasswordNeverExpires = CannotChangePassword = false;
        PasswordOptionsKnown = false;
        PasswordOptionsUnreadable = false;
        ShowCloudTab = false;
        ShowExchangeTab = false;
        _cloudUpn = _cloudSid = _cloudComputerName = null;
        Cloud.Reset();
        Exchange.Reset();
    }

    public async Task LoadAsync(string distinguishedName, AdObjectType type)
    {
        var token = ++_loadToken;

        // Nothing may target this object until it is actually loaded. _dn is what SaveAsync and the group
        // commands write to, so assigning it up front is what let a Save aimed at the object still on screen
        // land on the one being loaded — with a confirmation naming the object it did NOT write to.
        //
        // HasObject goes down for the same reason: the pane was still showing the previous object's fields,
        // including unsaved edits, with its buttons live. An empty pane during a load is the honest state.
        _dn = null;
        HasObject = false;
        Location = DirectoryService.ParentDn(distinguishedName);
        IsUser = type == AdObjectType.User;
        IsComputer = type == AdObjectType.Computer;
        IsGroup = type == AdObjectType.Group;
        IsLoadingCloudGroups = false; // a superseded load's cloud read must not leave this showing
        BusyMessage = "Loading…";
        IsBusy = true;
        try
        {
            var attrs = await _directory.LoadObjectAsync(distinguishedName);
            if (token != _loadToken) return; // superseded — the pane now belongs to another object
            _dn = distinguishedName; // this load owns the pane from here, so writes may target it
            var map = attrs.GroupBy(a => a.LdapName, StringComparer.OrdinalIgnoreCase)
                           .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            General.Clear(); Account.Clear(); Address.Clear(); Organization.Clear(); Email.Clear();
            ProxyAddresses.Clear(); MemberOf.Clear(); Members.Clear(); DirectReports.Clear(); AllAttributes.Clear();
            _editableFields.Clear(); _pendingManagerDn = null; _pendingManagedByDn = null;

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

            // Managed by (groups; DN-valued, edited via picker)
            ManagedByDisplay = map.TryGetValue("managedBy", out var mgdBy) && mgdBy.DisplayValues.Count > 0
                ? mgdBy.DisplayValues[0] : "(none)";

            // Group scope + category, decoded from the groupType bitmask. Built-in/system groups whose groupType
            // carries no recognised scope bit aren't offered the editor (we couldn't write a safe value back).
            _groupTypeKnown = false;
            if (type == AdObjectType.Group
                && map.TryGetValue("groupType", out var gt) && gt.RawValues.Count > 0
                && GroupTypeClassifier.TryParse(gt.RawValues[0], out var loadedScope, out var loadedCategory))
            {
                _originalGroupScope = loadedScope;
                _originalGroupCategory = loadedCategory;
                GroupScope = loadedScope;
                GroupCategory = loadedCategory;
                _groupTypeKnown = true;
            }
            ShowGroupType = _groupTypeKnown;

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

            // Group members (objects inside this group) — read through the range-aware call, NOT the "member"
            // attribute from the load above: AD renames that property past ~1500 values, so a large group would
            // silently show an empty Members tab.
            if (IsGroup)
            {
                try
                {
                    var membership = await _directory.GetGroupMembersAsync(distinguishedName);
                    if (token != _loadToken) return; // superseded
                    foreach (var m in membership.Members)
                        Members.Add(new GroupMemberRow(m.Name, m.DistinguishedName));
                    if (membership.Truncated)
                        AppLog.Instance.Warn($"Only part of the membership of {distinguishedName} could be read.");
                }
                catch (Exception ex) { AppLog.Instance.Warn("Could not read group members: " + DirectoryService.Friendly(ex)); }
            }

            PopulateAttributeEditor(attrs, type);

            // Accidental-deletion protection (read via the object's DACL).
            bool protectedNow;
            try { protectedNow = await _directory.GetDeletionProtectionAsync(distinguishedName); }
            catch { protectedNow = false; } // unreadable DACL: show as unprotected rather than failing the load
            if (token != _loadToken) return; // superseded — must not touch the shared baseline below
            _originalProtected = protectedNow;
            IsProtectedFromDeletion = _originalProtected;

            // Password options -- users only. A failed read leaves PasswordOptionsKnown false, which keeps
            // the checkboxes disabled and out of the save: see PasswordOptionsKnown.
            if (IsUser)
            {
                PasswordOptions? options = null;
                try { options = await _directory.GetPasswordOptionsAsync(distinguishedName); }
                catch (Exception ex) { AppLog.Instance.Warn("Could not read the password options: " + DirectoryService.Friendly(ex)); }
                if (token != _loadToken) return; // superseded
                if (options is not null)
                {
                    _originalPasswordNeverExpires = options.NeverExpires;
                    _originalCannotChangePassword = options.CannotChange;
                    PasswordNeverExpires = options.NeverExpires;
                    CannotChangePassword = options.CannotChange;
                    PasswordOptionsKnown = true;
                }
                else PasswordOptionsUnreadable = true;
            }

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
            {
                IsLoadingCloudGroups = true;
                _ = LoadCloudMembershipsAsync(_cloudUpn!, token);
            }

            // Classify the on-prem groups (Security/Distribution + scope) for the Member Of "Type" column (best-effort).
            if (MemberOf.Any(m => !m.IsCloud))
                _ = LoadOnPremGroupKindsAsync(token);

            HasObject = true;
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return; // superseded: its failure is not this pane's problem
            Clear(); // first, so the error is not shown over a pane still saying "Loading…"
            _onError(DirectoryService.Friendly(ex));
        }
        finally { if (token == _loadToken) IsBusy = false; }
    }

    /// <summary>
    /// Re-reads the object and updates only what cannot be edited — system-maintained values such as the
    /// account options, lockout time and password-last-set. Every edit, pending membership change and picker
    /// choice stays exactly as it was.
    /// </summary>
    private async Task RefreshReadOnlyAsync()
    {
        if (_dn is not { } dn) return;
        var token = _loadToken;
        var attrs = await _directory.LoadObjectAsync(dn);
        if (token != _loadToken || !string.Equals(_dn, dn, StringComparison.OrdinalIgnoreCase)) return; // pane moved on
        var map = attrs.GroupBy(a => a.LdapName, StringComparer.OrdinalIgnoreCase)
                       .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var field in General.Concat(Account).Concat(Address).Concat(Organization).Concat(Email))
        {
            if (!field.IsReadOnly) continue;
            field.OriginalText = map.TryGetValue(field.LdapName, out var fresh) ? fresh.DisplaySummary : string.Empty;
            field.EditText = field.OriginalText;
        }
        for (var i = 0; i < AllAttributes.Count; i++)
            if (AllAttributes[i].IsReadOnly && map.TryGetValue(AllAttributes[i].LdapName, out var fresh))
                AllAttributes[i] = fresh;
        LoadLockoutStatus(map);
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
    private Task SaveAsync() => SaveCoreAsync(askFirst: true);

    /// <summary>
    /// Writes everything pending: property edits first, then membership changes.
    /// </summary>
    /// <param name="askFirst">
    /// Show the confirmation. False only when the operator has just been shown the same list and chose Save
    /// in the unsaved-changes prompt — asking twice in a row for one decision is noise.
    /// </param>
    /// <returns>True when everything pending was written.</returns>
    /// <remarks>
    /// <para>
    /// The target and the membership changes are captured BEFORE the first await. A save takes seconds when
    /// it includes cloud groups, and the pane's lists belong to whatever object is loaded at the time they
    /// are read — reading them after an await could apply one user's pending groups to another.
    /// </para>
    /// <para>
    /// If the property write fails, nothing else is attempted and every pending change stays on screen, so
    /// the cause can be fixed and Save pressed again. Membership changes are written one at a time and a
    /// failure is reported rather than stopping the rest — the same as when they were written immediately.
    /// </para>
    /// </remarks>
    private async Task<bool> SaveCoreAsync(bool askFirst)
    {
        if (CaptureTarget() is not { } who) return false;

        var attributeChanges = BuildAttributeChanges(explainRefusal: true);
        if (attributeChanges is null) return false; // refused, and the reason already shown

        var plan = PlanMembershipChanges();
        var membershipLines = DescribeMembershipChanges(plan);
        if (attributeChanges.Count == 0 && membershipLines.Count == 0)
        {
            if (askFirst) _dialogs.Alert("Save", "There are no changes to save.");
            return true;
        }

        if (askFirst)
        {
            var lines = attributeChanges.Select(c => c.Describe()).Concat(membershipLines).Concat(MembershipNotes(plan));
            if (!_dialogs.Confirm("Confirm changes",
                    $"Apply {attributeChanges.Count + membershipLines.Count} change(s) to “{who.Title}”?", lines))
                return false;
        }

        var writing = WriteAsync(who, attributeChanges, plan);
        _saving = writing;
        return await writing;
    }

    /// <summary>
    /// The writing half of a save, once the operator has confirmed it. The busy message names each change as it
    /// goes, with a count when there is more than one.
    /// </summary>
    private async Task<bool> WriteAsync(WriteTarget who, List<PendingChange> attributeChanges, MembershipPlan plan)
    {
        _stepIndex = 0;
        _stepCount = (attributeChanges.Count > 0 ? 1 : 0)
                     + plan.Groups.Count
                     + (plan.Members.Any(x => x.Add) ? 1 : 0)   // members go in one write per direction
                     + (plan.Members.Any(x => !x.Add) ? 1 : 0)
                     + plan.Reports.Count;
        BusyMessage = "Saving…";
        IsBusy = true;
        try
        {
            if (attributeChanges.Count > 0)
            {
                Step("Saving properties");
                try { await _directory.ApplyChangesAsync(who.Dn, attributeChanges); }
                catch (Exception ex) { _onError(DirectoryService.Friendly(ex)); return false; }
            }

            var errors = new List<string>();
            await ApplyGroupChangesAsync(who, plan, errors);
            await ApplyMemberChangesAsync(who, plan, errors);
            await ApplyDirectReportChangesAsync(who, plan, errors);

            await ReloadAsync();
            ObjectChanged?.Invoke();
            if (errors.Count > 0)
                _onError("Saved, but these changes did not complete:\n" + string.Join("\n", errors));
            return errors.Count == 0;
        }
        finally { IsBusy = false; }
    }

    /// <summary>Moves the busy message on to a save's next change: "Removing from cloud group Sales… (2 of 5)".</summary>
    private void Step(string doing) =>
        BusyMessage = _stepCount > 1 ? $"{doing}… ({++_stepIndex} of {_stepCount})" : $"{doing}…";

    /// <summary>Counts changes a save had to give up on unattempted, so the next one shows its real place.</summary>
    private void SkipSteps(int count) => _stepIndex += count;

    /// <summary>
    /// Before this pane's object is replaced or its window closes: if anything is unsaved, asks whether to save
    /// it, discard it, or stay.
    /// </summary>
    /// <returns>True to go ahead (saved, discarded, or nothing to lose); false to stay where you are.</returns>
    /// <remarks>
    /// A failed save returns false. The operator stays on the object and can see what did and did not go,
    /// rather than being moved on with the failure behind them.
    /// </remarks>
    public async Task<bool> ConfirmLeaveAsync()
    {
        // A save that is still writing is not unsaved work. Its rows stay marked until it finishes, so asking
        // now would list changes already on their way, and Save in that prompt would write them a second time.
        // Let it finish, then ask about whatever it left behind.
        if (_saving is { IsCompleted: false } saving)
        {
            try { await saving; }
            catch { /* the save reports its own failure */ }
        }

        if (_dn is null || !HasUnsavedChanges()) return true;

        var attributeChanges = BuildAttributeChanges(explainRefusal: false);
        var plan = PlanMembershipChanges();
        var lines = (attributeChanges?.Select(c => c.Describe())
                     ?? new[] { "A group scope change that cannot be saved as it is" })
            .Concat(DescribeMembershipChanges(plan))
            .Concat(MembershipNotes(plan))
            .ToList();

        switch (_dialogs.AskUnsavedChanges("Unsaved changes", $"You have unsaved changes to “{Title}”.", lines))
        {
            case UnsavedChoice.Save:
                return await SaveCoreAsync(askFirst: false);
            case UnsavedChoice.Discard:
                Clear();
                return true;
            default:
                return false;
        }
    }

    /// <summary>The object currently loaded in the pane, or null while nothing is (including mid-load).</summary>
    public string? CurrentDn => _dn;

    /// <summary>
    /// The property edits as changes to write, or null when one of them cannot be saved as it stands.
    /// </summary>
    /// <param name="explainRefusal">Say why when refusing. False when only describing the edits.</param>
    private List<PendingChange>? BuildAttributeChanges(bool explainRefusal)
    {
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
            // On a group, managedBy is owned by the General-tab picker (as manager is for a user).
            if (IsGroup && attr.LdapName.Equals("managedBy", StringComparison.OrdinalIgnoreCase)) continue;
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

        // Managed by (groups, via picker)
        if (_pendingManagedByDn is not null)
        {
            byLdap["managedBy"] = string.IsNullOrEmpty(_pendingManagedByDn)
                ? new PendingChange { Op = ChangeOp.Clear, LdapName = "managedBy", FriendlyName = "Managed by" }
                : new PendingChange { Op = ChangeOp.Set, LdapName = "managedBy", FriendlyName = "Managed by", Values = { _pendingManagedByDn } };
        }

        // Group scope / category → the groupType bitmask. groupType is flagged IsInteger, so the write is routed
        // through the LDAP integer path (ModifyViaLdap) rather than DirectoryEntry, which is what this attribute needs.
        if (_groupTypeKnown && (GroupScope != _originalGroupScope || GroupCategory != _originalGroupCategory))
        {
            // AD has no single step between Global and Domain local — reject it here with an explanation rather
            // than letting the DC return a bare constraint violation. Everything else is left to AD's own rules.
            if (GroupTypeClassifier.IsIllegalScopeChange(_originalGroupScope, GroupScope))
            {
                if (!explainRefusal) return null;
                _dialogs.Alert("Group scope",
                    $"Active Directory can't change a group directly from {GroupTypeClassifier.Label(_originalGroupScope)} " +
                    $"to {GroupTypeClassifier.Label(GroupScope)}.\n\n" +
                    "Change it to Universal and save, then change it again to the scope you want.");
                return null;
            }
            byLdap["groupType"] = new PendingChange
            {
                Op = ChangeOp.Set,
                LdapName = "groupType",
                FriendlyName = "Group type",
                Values = { GroupTypeClassifier.Build(GroupScope, GroupCategory).ToString(System.Globalization.CultureInfo.InvariantCulture) },
            };
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

        foreach (var change in BuildPasswordOptionChanges())
            byLdap[change.LdapName] = change;

        return byLdap.Values.ToList();
    }

    /// <summary>True when the pane has anything a reload would discard: property edits or pending membership
    /// changes. Mirrors the change-collection logic in <see cref="SaveCoreAsync"/>.</summary>
    public bool HasUnsavedChanges()
    {
        if (MemberOf.Any(m => m.Pending != PendingMembership.None)
            || Members.Any(m => m.Pending != PendingMembership.None)
            || DirectReports.Any(r => r.Pending != PendingMembership.None))
            return true;
        if (_editableFields.Any(f => !f.IsReadOnly && !f.IsDnValued && !f.IsMultiValued && f.IsDirty))
            return true;
        foreach (var attr in AllAttributes)
        {
            if (attr.IsReadOnly) continue;
            if (attr.LdapName.Equals("manager", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsGroup && attr.LdapName.Equals("managedBy", StringComparison.OrdinalIgnoreCase)) continue;
            if (attr.LdapName.Equals("accountExpires", StringComparison.OrdinalIgnoreCase)) continue;
            if (attr.IsMultiValued ? attr.MultiValueEdited : attr.IsDirty) return true;
        }
        if (_pendingManagerDn is not null) return true;
        if (_pendingManagedByDn is not null) return true;
        if (_groupTypeKnown && (GroupScope != _originalGroupScope || GroupCategory != _originalGroupCategory)) return true;
        if (BuildAccountExpiresChange() is not null) return true;
        if (BuildCountryChanges().Any()) return true;
        if (IsProtectedFromDeletion != _originalProtected) return true;
        if (BuildPasswordOptionChanges().Any()) return true;
        return false;
    }

    // --- Membership changes are held until Save -------------------------------------------------------
    //
    // Add and Remove used to write at once and then reload the whole pane, which threw away any property edit
    // not yet saved. They now mark rows, and Save writes everything together — as ADUC's Member Of tab waits
    // for Apply. Removing a row still pending "Adding" just cancels the add; adding a group pending "Removing"
    // cancels the removal; Reload discards all of it. No confirmation here: nothing is written until Save,
    // which lists every change.

    [RelayCommand]
    private void AddToGroups()
    {
        if (_dn is null) return;
        var picked = _dialogs.PickGroupsHybrid($"Add “{Title}” to groups");
        if (picked is null || picked.Count == 0) return;

        foreach (var g in picked)
        {
            var row = g.Channel switch
            {
                GroupChannel.OnPremAd when g.Dn is not null =>
                    new GroupMembership(g.Name, g.Dn, "On-prem", isCloud: false),
                GroupChannel.EntraGraph when g.CloudId is not null =>
                    new GroupMembership(g.Name, g.CloudId, "Cloud", isCloud: true),
                GroupChannel.ExchangeOnline =>
                    new GroupMembership(g.Name, g.CloudId ?? g.Smtp ?? g.Name, "Exchange", isCloud: true, isExchange: true, smtp: g.Smtp),
                _ => null, // an on-prem pick with no DN, or a cloud pick with no id, has nothing to write to
            };
            if (row is null) continue;

            var existing = MemberOf.FirstOrDefault(m => SameGroup(m, row));
            if (existing is null)
            {
                row.Pending = PendingMembership.Adding;
                MemberOf.Add(row);
            }
            else if (existing.Pending == PendingMembership.Removing)
                existing.Pending = PendingMembership.None; // re-adding a group being removed takes the removal back
            // otherwise already a member, or already being added
        }
    }

    [RelayCommand]
    private void RemoveFromGroup(System.Collections.IList? selected)
    {
        // The "Remove selected" button passes the list's SelectedItems, so several groups go at once.
        var rows = selected?.Cast<GroupMembership>().ToList() ?? new List<GroupMembership>();
        foreach (var row in rows)
        {
            if (row.Pending == PendingMembership.Adding) MemberOf.Remove(row); // never written: just drop it
            else row.Pending = PendingMembership.Removing;
        }
    }

    /// <summary>
    /// Two rows name the same group. Exchange groups compare on SMTP when both carry one, because the picker
    /// and the cloud read can identify one group by different ids; everything else by its DN or object id.
    /// </summary>
    private static bool SameGroup(GroupMembership a, GroupMembership b)
    {
        if (a.IsExchange && b.IsExchange && !string.IsNullOrWhiteSpace(a.Smtp) && !string.IsNullOrWhiteSpace(b.Smtp))
            return string.Equals(a.Smtp, b.Smtp, StringComparison.OrdinalIgnoreCase);
        return a.IsCloud == b.IsCloud && string.Equals(a.Dn, b.Dn, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every pending membership change in the pane, copied out so a save can outlive the pane's lists.</summary>
    private sealed record MembershipPlan(
        IReadOnlyList<(GroupMembership Group, bool Add)> Groups,
        IReadOnlyList<(GroupMemberRow Member, bool Add)> Members,
        IReadOnlyList<(DirectReportRow Report, bool Add)> Reports);

    private MembershipPlan PlanMembershipChanges()
    {
        static List<(T, bool)> Pending<T>(IEnumerable<T> rows, Func<T, PendingMembership> state) =>
            rows.Where(r => state(r) == PendingMembership.Adding).Select(r => (r, true))
                .Concat(rows.Where(r => state(r) == PendingMembership.Removing).Select(r => (r, false)))
                .ToList();
        return new MembershipPlan(
            Pending(MemberOf, g => g.Pending),
            Pending(Members, m => m.Pending),
            Pending(DirectReports, r => r.Pending));
    }

    /// <summary>One line per pending membership change, for the Save confirmation and the unsaved prompt.</summary>
    private static List<string> DescribeMembershipChanges(MembershipPlan plan)
    {
        var lines = new List<string>();
        foreach (var (g, add) in plan.Groups)
            lines.Add($"{(add ? "Add to group" : "Remove from group")}: {g.Name} ({g.Source})");
        foreach (var (m, add) in plan.Members)
            lines.Add($"{(add ? "Add member" : "Remove member")}: {m.Name}");
        foreach (var (r, add) in plan.Reports)
            lines.Add(add ? $"Add direct report: {r.Name} (sets their manager to this user)"
                          : $"Remove direct report: {r.Name} (clears their manager)");
        return lines;
    }

    private static IEnumerable<string> MembershipNotes(MembershipPlan plan)
    {
        if (plan.Groups.Any(x => x.Group.IsCloud))
            yield return "Note: membership of a group synced from on-prem AD is mastered on-prem and can't be changed in the cloud.";
    }

    /// <summary>
    /// Writes the pending Member Of changes, one group at a time so one failure cannot stop the rest. Each
    /// group goes through the service that owns it: LDAP on-prem, Graph for Entra groups, the Exchange module
    /// for distribution lists and mail-enabled security groups, which Graph cannot change.
    /// </summary>
    private async Task ApplyGroupChangesAsync(WriteTarget who, MembershipPlan plan, List<string> errors)
    {
        static string Verb(bool add) => add ? "Add to" : "Remove from";
        static string Doing(bool add) => add ? "Adding to" : "Removing from";

        foreach (var (g, add) in plan.Groups.Where(x => !x.Group.IsCloud))
        {
            Step($"{Doing(add)} group {g.Name}");
            try
            {
                var change = new PendingChange { Op = add ? ChangeOp.AddToGroups : ChangeOp.RemoveFromGroups, Values = { g.Dn } };
                await _directory.ApplyChangesAsync(who.Dn, new[] { change });
            }
            catch (Exception ex) { errors.Add($"{Verb(add)} {g.Name}: {DirectoryService.Friendly(ex)}"); }
        }

        var cloud = plan.Groups.Where(x => x.Group.IsCloud && !x.Group.IsExchange).ToList();
        if (cloud.Count > 0)
        {
            // "No Entra twin" and "the lookup failed" are different facts and must not share a message:
            // blaming sync for a throttled request sends the operator to the wrong place.
            string? cloudId = null;
            var lookupFailed = false;
            BusyMessage = $"Looking up “{who.Title}” in Entra ID…";
            try { cloudId = await ResolveCloudObjectIdAsync(who); }
            catch (Exception ex)
            {
                lookupFailed = true;
                errors.Add("Couldn't check Entra ID for this object, so its cloud group changes were NOT made: " + GraphErrors.Friendly(ex));
            }
            if (!lookupFailed && cloudId is null)
                errors.Add("Couldn't find this object in Entra ID (it may not be synced yet), so its cloud group changes were NOT made.");
            if (cloudId is null)
                SkipSteps(cloud.Count);
            else
                foreach (var (g, add) in cloud)
                {
                    Step($"{Doing(add)} cloud group {g.Name}");
                    try
                    {
                        if (add) await _graph.AddMemberToGroupAsync(g.Dn, cloudId);
                        else await _graph.RemoveMemberFromGroupAsync(g.Dn, cloudId);
                    }
                    catch (Exception ex) { errors.Add($"{Verb(add)} {g.Name}: {GraphErrors.Friendly(ex)}"); }
                }
        }

        var exchange = plan.Groups.Where(x => x.Group.IsExchange).ToList();
        if (exchange.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(who.CloudUpn))
            {
                errors.Add("No mailbox identity for this object, so its Exchange distribution group changes were NOT made.");
                SkipSteps(exchange.Count);
            }
            else
                foreach (var (g, add) in exchange)
                {
                    Step($"{Doing(add)} Exchange group {g.Name}");
                    // The group is addressed by its primary SMTP where known — what the cmdlets expect.
                    var hasSmtp = !string.IsNullOrWhiteSpace(g.Smtp);
                    try
                    {
                        if (add) await _exchange.AddDistributionGroupMemberAsync(hasSmtp ? g.Smtp! : g.Dn, who.CloudUpn!);
                        else await _exchange.RemoveDistributionGroupMemberAsync(hasSmtp ? g.Smtp! : g.Name, who.CloudUpn!);
                    }
                    catch (Exception ex) { errors.Add($"{Verb(add)} {g.Name} (Exchange): {ex.Message}"); }
                }
        }
    }

    /// <summary>Writes the pending changes to a group's own Members list.</summary>
    private async Task ApplyMemberChangesAsync(WriteTarget who, MembershipPlan plan, List<string> errors)
    {
        var adds = plan.Members.Where(x => x.Add).Select(x => x.Member.Dn).ToList();
        var removes = plan.Members.Where(x => !x.Add).Select(x => x.Member.Dn).ToList();
        static string Count(int n) => n == 1 ? "1 member" : $"{n} members";
        if (adds.Count > 0)
        {
            Step($"Adding {Count(adds.Count)}");
            try { await _directory.AddMembersAsync(who.Dn, adds); }
            catch (Exception ex) { errors.Add($"Add {adds.Count} member(s): {DirectoryService.Friendly(ex)}"); }
        }
        if (removes.Count > 0)
        {
            Step($"Removing {Count(removes.Count)}");
            try { await _directory.RemoveMembersAsync(who.Dn, removes); }
            catch (Exception ex) { errors.Add($"Remove {removes.Count} member(s): {DirectoryService.Friendly(ex)}"); }
        }
    }

    /// <summary>
    /// Writes the pending direct-report changes. A direct report is the other user's <c>manager</c>, so each
    /// one is a write to THAT user — setting it to this user, or clearing it.
    /// </summary>
    private async Task ApplyDirectReportChangesAsync(WriteTarget who, MembershipPlan plan, List<string> errors)
    {
        foreach (var (r, add) in plan.Reports)
        {
            Step(add ? $"Adding direct report {r.Name}" : $"Removing direct report {r.Name}");
            try
            {
                var change = add
                    ? new PendingChange { Op = ChangeOp.Set, LdapName = "manager", FriendlyName = "Manager", Values = { who.Dn } }
                    : new PendingChange { Op = ChangeOp.Clear, LdapName = "manager", FriendlyName = "Manager" };
                await _directory.ApplyChangesAsync(r.Dn, new[] { change });
            }
            catch (Exception ex)
            {
                errors.Add($"{(add ? "Add direct report" : "Remove direct report")} {r.Name}: {DirectoryService.Friendly(ex)}");
            }
        }
    }

    /// <summary>Adds a row as pending, or takes back its pending removal. Shared by Members and Direct reports.</summary>
    private static void StagePendingAdd<T>(ObservableCollection<T> list, string dn, Func<T> create, Func<T, T> asLoaded)
        where T : IPendingRow
    {
        var i = IndexOfDn(list, dn);
        if (i < 0) list.Add(create());
        else if (list[i].Pending == PendingMembership.Removing) list[i] = asLoaded(list[i]);
    }

    /// <summary>Marks rows as pending removal, or drops a row that was only ever pending addition.</summary>
    private static void StagePendingRemove<T>(ObservableCollection<T> list, IEnumerable<T> rows, Func<T, T> asRemoving)
        where T : IPendingRow
    {
        foreach (var row in rows.ToList())
        {
            var i = IndexOfDn(list, row.Dn);
            if (i < 0) continue;
            if (list[i].Pending == PendingMembership.Adding) list.RemoveAt(i);
            else list[i] = asRemoving(list[i]);
        }
    }

    private static int IndexOfDn<T>(IList<T> list, string dn) where T : IPendingRow
    {
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i].Dn, dn, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// Who a write is FOR, captured when the operator confirms it. A group add/remove takes seconds — each
    /// Exchange call is a round trip on a serialised channel — and nothing stops the operator selecting
    /// another object meanwhile. Reading the live fields across those awaits meant the second half of a
    /// write could target whoever was selected by then: removing the newly-selected user from the previous
    /// user's distribution lists, and reporting success.
    /// </summary>
    private readonly record struct WriteTarget(
        string Dn, string Title, string? CloudUpn, string? CloudSid, string? ComputerName, bool IsUser, bool IsComputer);

    /// <summary>Snapshots the current object. Null when nothing is loaded, so callers bail as they did before.</summary>
    private WriteTarget? CaptureTarget() =>
        _dn is null ? null : new WriteTarget(_dn, Title, _cloudUpn, _cloudSid, _cloudComputerName, IsUser, IsComputer);

    /// <summary>Resolves the captured object's Entra object id (user by UPN, computer by name).</summary>
    /// <returns>The id, or null when the object genuinely has no Entra twin. Throws when the lookup FAILED —
    /// the caller must tell those apart, because "not synced" and "Graph is throttling" need different words.</returns>
    private async Task<string?> ResolveCloudObjectIdAsync(WriteTarget who)
    {
        if (who.IsUser && !string.IsNullOrWhiteSpace(who.CloudUpn))
            return (await _graph.GetUserByUpnAsync(who.CloudUpn))?.Id;
        if (who.IsComputer && !string.IsNullOrWhiteSpace(who.ComputerName))
            return (await _graph.GetDevicesByComputerAsync(who.ComputerName, who.CloudSid)).FirstOrDefault()?.Id;
        return null;
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
    private async Task LoadCloudMembershipsAsync(string upn, int token)
    {
        try
        {
            var groups = await _graph.GetUserGroupsByUpnAsync(upn);
            if (token != _loadToken) return; // superseded (a reload of the same object counts)
            foreach (var g in groups.Where(g => !g.IsSynced))
            {
                // Distribution lists / mail-enabled security groups are Exchange-managed — label their source
                // "Exchange" (matching the unified group picker), not the generic "Cloud", and carry the SMTP
                // so a removal from this tab can go through the Exchange module (Graph can't modify them).
                var loaded = new GroupMembership(g.DisplayName, g.Id, g.IsExchangeManaged ? "Exchange" : "Cloud",
                    isCloud: true, kind: g.KindLabel, isExchange: g.IsExchangeManaged, smtp: g.Mail);
                var existing = MemberOf.FirstOrDefault(m => SameGroup(m, loaded));
                if (existing is null) MemberOf.Add(loaded);
                // This read is slower than the pane, so a group can be added on screen before it arrives. If the
                // object turns out to be a member already, the pending add would only fail on Save — drop it.
                else if (existing.Pending == PendingMembership.Adding) existing.Pending = PendingMembership.None;
            }
        }
        catch (Exception ex)
        {
            // Same rule as the Copy flows: a short membership list must not be presented as the whole one.
            // Remove-from-groups operates off this list, so a silent gap here is a removal that quietly
            // misses groups.
            if (token == _loadToken)
                _onError("Cloud group memberships could not be read, so any cloud groups are MISSING from Member Of: "
                         + GraphErrors.Friendly(ex));
            AppLog.Instance.Warn("Could not load cloud group memberships: " + ex.Message);
        }
        finally
        {
            // Only the current load's read may clear it. A superseded read finishing must not report the
            // current one done.
            if (token == _loadToken) IsLoadingCloudGroups = false;
        }
    }

    /// <summary>Best-effort fill of the on-prem groups' <see cref="GroupMembership.Kind"/> column
    /// (Security/Distribution + scope, read from each group's <c>groupType</c> bitmask in one query).</summary>
    private async Task LoadOnPremGroupKindsAsync(int token)
    {
        try
        {
            var dns = MemberOf.Where(m => !m.IsCloud).Select(m => m.Dn)
                              .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (dns.Count == 0) return;
            var kinds = await _directory.GetGroupTypesAsync(dns);
            if (token != _loadToken) return; // superseded (a reload of the same object counts)
            foreach (var m in MemberOf.Where(m => !m.IsCloud))
                if (kinds.TryGetValue(m.Dn, out var k) && !string.IsNullOrEmpty(k))
                    m.Kind = k;
        }
        catch (Exception ex) { AppLog.Instance.Warn("Could not classify on-prem groups: " + ex.Message); }
    }

    [RelayCommand]
    private void AddMembers()
    {
        if (_dn is null || !IsGroup) return;
        var picked = _dialogs.PickObjects("Add members to group", AdObjectType.Unknown, multiSelect: true);
        if (picked is null || picked.Count == 0) return;
        foreach (var p in picked) StageMember(p.Name, p.DistinguishedName);
    }

    /// <summary>Adds a member as pending. A group cannot contain itself, so that pick is ignored.</summary>
    private void StageMember(string name, string dn)
    {
        if (string.Equals(dn, _dn, StringComparison.OrdinalIgnoreCase)) return;
        StagePendingAdd(Members, dn,
            () => new GroupMemberRow(name, dn) { Pending = PendingMembership.Adding },
            row => row with { Pending = PendingMembership.None });
    }

    /// <summary>
    /// Opens a member of this group in its OWN window. Not in this pane: the pane is showing the group being
    /// worked on, and replacing it to look at one member loses that.
    ///
    /// The type has to be known first — the edit pane uses it to pick the field layout, and the members list
    /// holds only a name and a DN. That is deliberate: the list derives names from each DN's RDN because
    /// resolving every member meant a bind each, thousands of them on a large group. One lookup for the ONE
    /// member being opened is a different proposition entirely.
    /// </summary>
    [RelayCommand]
    private async Task OpenMemberAsync(GroupMemberRow? member)
    {
        if (member is null || string.IsNullOrWhiteSpace(member.Dn)) return;

        AdObjectType type;
        try
        {
            var attrs = await _directory.LoadObjectAsync(member.Dn);
            type = ClassifyFrom(attrs);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn($"Could not open '{member.Dn}': {DirectoryService.Friendly(ex)}");
            _dialogs.Alert("Open member", "Could not open that member: " + DirectoryService.Friendly(ex));
            return;
        }
        _dialogs.ShowAdObjectProperties(member.Dn, type);
    }

    /// <summary>
    /// Reads the object type off objectClass. COMPUTER IS CHECKED FIRST on purpose: a computer's objectClass
    /// contains "user" as well, because computer derives from user in the schema — testing for user first
    /// would open every server as if it were a person.
    /// </summary>
    private static AdObjectType ClassifyFrom(IReadOnlyList<AdAttribute> attrs)
    {
        var classes = attrs
            .Where(a => string.Equals(a.LdapName, "objectClass", StringComparison.OrdinalIgnoreCase))
            .SelectMany(a => a.RawValues)
            .ToList();
        var all = string.Join(";", classes);

        if (all.Contains("computer", StringComparison.OrdinalIgnoreCase)) return AdObjectType.Computer;
        if (all.Contains("group", StringComparison.OrdinalIgnoreCase)) return AdObjectType.Group;
        if (all.Contains("user", StringComparison.OrdinalIgnoreCase)) return AdObjectType.User;
        return AdObjectType.Unknown;
    }

    /// <summary>
    /// Adds many members at once from a pasted list. Resolved against Active Directory, because that is what
    /// owns this group's membership — a cloud-only user has no distinguished name to add.
    /// </summary>
    /// <remarks>
    /// A member pending removal is offered again rather than skipped as "already a member": pasting them is
    /// how that removal is taken back. The paste dialog has its own review step, and Save lists the result.
    /// </remarks>
    [RelayCommand]
    private void PasteMembers()
    {
        if (_dn is null || !IsGroup) return;

        var resolved = _dialogs.PasteMembers(
            $"Add members to “{Title}” from a list", MemberBackend.ActiveDirectory,
            Members.Where(m => m.Pending != PendingMembership.Removing).Select(m => m.Dn), _dn);
        if (resolved is null || resolved.Count == 0) return;
        foreach (var c in resolved) StageMember(c.DisplayName, c.Identity);
    }

    [RelayCommand]
    private void RemoveMember(object? selected)
    {
        if (!IsGroup) return;
        // The view passes its SelectedItems (sorting reorders the view, so a row index would be unreliable —
        // act on the selected row objects directly).
        var rows = selected is System.Collections.IList list ? list.Cast<GroupMemberRow>().ToList() : new List<GroupMemberRow>();
        StagePendingRemove(Members, rows, row => row with { Pending = PendingMembership.Removing });
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
            // A move used to reload the object and DISCARD unsaved edits, with a warning here saying so. It no
            // longer does: the object is the same object afterwards, only its DN has changed, so the edits stay
            // and Save writes them to it where it now lives.
            lines.Add(string.Empty);
            lines.Add("Your unsaved changes stay in the properties pane. Click Save afterwards to apply them.");
        }
        if (!_dialogs.Confirm("Move object", $"Move “{Title}” to another OU?", lines))
            return;

        BusyMessage = $"Moving to {NameResolver.RdnFallback(target)}…";
        IsBusy = true;
        try
        {
            _dn = await _directory.MoveObjectAsync(_dn!, target); // DN changes after a move
            Location = DirectoryService.ParentDn(_dn);
            BusyMessage = "Refreshing…";
            if (HasUnsavedChanges()) await RefreshReadOnlyAsync();
            else await ReloadAsync();
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

    /// <summary>Picks the group's <c>managedBy</c> owner. AD allows a user, group or computer here, so the picker
    /// is opened across all of them rather than users only.</summary>
    [RelayCommand]
    private void PickManagedBy()
    {
        var picked = _dialogs.PickObjects("Select who manages this group", AdObjectType.Unknown, multiSelect: false);
        if (picked is null || picked.Count == 0) return;
        _pendingManagedByDn = picked[0].DistinguishedName;
        ManagedByDisplay = picked[0].Name + " (pending save)";
    }

    [RelayCommand]
    private void ClearManagedBy()
    {
        _pendingManagedByDn = string.Empty;
        ManagedByDisplay = "(none — pending save)";
    }

    /// <summary>Adds direct reports. Held until Save, which sets each one's <c>manager</c> to this user.</summary>
    [RelayCommand]
    private void AddDirectReports()
    {
        if (_dn is null || !IsUser) return;
        var picked = _dialogs.PickObjects("Add direct reports", AdObjectType.User, multiSelect: true);
        if (picked is null || picked.Count == 0) return;

        // Can't be your own manager; skip any selection that resolves to this user.
        var targets = picked.Where(p => !string.Equals(p.DistinguishedName, _dn, StringComparison.OrdinalIgnoreCase)).ToList();
        if (targets.Count == 0) { _dialogs.Alert("Add direct reports", "A user can't be their own direct report."); return; }

        foreach (var t in targets)
            StagePendingAdd(DirectReports, t.DistinguishedName,
                () => new DirectReportRow(t.Name, t.DistinguishedName) { Pending = PendingMembership.Adding },
                row => row with { Pending = PendingMembership.None });
    }

    /// <summary>Removes direct reports. Held until Save, which clears each one's <c>manager</c>.</summary>
    [RelayCommand]
    private void RemoveDirectReports(System.Collections.IList? selected)
    {
        if (!IsUser) return;
        var rows = selected?.Cast<DirectReportRow>().ToList() ?? new List<DirectReportRow>();
        StagePendingRemove(DirectReports, rows, row => row with { Pending = PendingMembership.Removing });
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
        await RunWrite(enabled ? "Enabling the account" : "Disabling the account",
            () => _directory.ApplyChangesAsync(_dn!, new[] { change }));
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

        await RunWrite("Resetting the password", () => _directory.ResetPasswordAsync(
            _dn!, request.Password, request.MustChangeAtNextLogon, request.Unlock));
    }

    [RelayCommand]
    private async Task UnlockAsync()
    {
        if (_dn is null || !IsUser) return;
        if (!_dialogs.Confirm("Unlock", $"Unlock account “{Title}”?", new[] { "Unlock the account" }))
            return;
        await RunWrite("Unlocking the account", () => _directory.UnlockAccountAsync(_dn!));
    }

    /// <summary>
    /// For the header actions that write at once — Enable, Disable, Unlock, Reset password. Writes, then brings
    /// the pane up to date without losing anything unsaved.
    /// </summary>
    /// <remarks>
    /// With nothing unsaved it reloads, as it always did. With edits pending it refreshes only the read-only
    /// values: these actions change nothing an operator can edit — the account options, the lockout time, the
    /// password-last-set — so that is all that can be out of date, and a full reload would throw the edits away.
    /// </remarks>
    private async Task RunWrite(string doing, Func<Task> write)
    {
        BusyMessage = doing + "…";
        IsBusy = true;
        try
        {
            await write();
            BusyMessage = "Refreshing…";
            if (HasUnsavedChanges()) await RefreshReadOnlyAsync();
            else await ReloadAsync();
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

    /// <summary>
    /// The password-option changes to save, one per checkbox that differs from what was loaded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing at all unless the options were actually read (<see cref="PasswordOptionsKnown"/>): an
    /// unticked box from a failed read is not a decision, and saving it would clear a real setting.
    /// </para>
    /// <para>
    /// Synthetic keys, as deletion protection uses, so neither can collide with a real attribute.
    /// "Password never expires" is NOT keyed on userAccountControl: that would let a raw edit of the
    /// attribute in the Attribute Editor silently replace it, or be replaced by it.
    /// </para>
    /// </remarks>
    private IEnumerable<PendingChange> BuildPasswordOptionChanges()
    {
        if (!IsUser || !PasswordOptionsKnown) yield break;

        if (PasswordNeverExpires != _originalPasswordNeverExpires)
            yield return new PendingChange
            {
                Op = PasswordNeverExpires ? ChangeOp.SetPasswordNeverExpires : ChangeOp.UnsetPasswordNeverExpires,
                LdapName = "__passwordNeverExpires__",
            };

        if (CannotChangePassword != _originalCannotChangePassword)
            yield return new PendingChange
            {
                Op = CannotChangePassword ? ChangeOp.SetCannotChangePassword : ChangeOp.UnsetCannotChangePassword,
                LdapName = "__cannotChangePassword__",
            };
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
            // On a group, managedBy is edited by the General-tab picker, so don't offer a raw DN box for it too.
            if (type == AdObjectType.Group && meta.LdapName.Equals("managedBy", StringComparison.OrdinalIgnoreCase)) continue;
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
            // groupType is deliberately absent: it's shown/edited as the Scope + Type combos on the General tab
            // (a raw signed bitmask like -2147483646 is meaningless to an operator).
            return (
                new[] { "cn", "sAMAccountName", "displayName", "description", "mail", "info" },
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
