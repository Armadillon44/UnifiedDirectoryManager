using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>One picked owner or member, shown as a removable row.</summary>
public sealed record CloudPrincipalRow(
    CloudGroupPrincipal Principal,
    CloudObjectKind Kind = CloudObjectKind.User,
    bool Seeded = false)
{
    public string Name => Principal.DisplayName;
    public string Detail => string.IsNullOrWhiteSpace(Principal.Address) ? Principal.Id : Principal.Address;

    /// <summary>
    /// The row remembers what it is because only one group type accepts a device, and only as a member. When the
    /// type changes there is no other way to find the rows that just became illegal — the principal record
    /// itself carries only identifiers, deliberately, because that is all either backend is sent.
    /// </summary>
    public bool IsDevice => Kind == CloudObjectKind.Device;
}

/// <summary>
/// Creates a cloud-only group. One dialog covers four kinds across two backends, because the split is invisible
/// to the operator but mandatory underneath: security and Microsoft 365 groups are created through Microsoft
/// Graph, while distribution lists and mail-enabled security groups exist only to Exchange Online and are
/// rejected by Graph with a 400.
///
/// The type selector therefore drives which fields apply, which rules the alias is validated against, and which
/// list is reloaded afterwards.
/// </summary>
public partial class NewCloudGroupViewModel : ObservableObject
{
    private readonly IGraphService _graph;
    private readonly IExchangeService _exchange;
    private readonly IDialogService _dialogs;
    private bool _creating;                          // guards a double-submit while the create is in flight
    private string _lastNicknameSuggestion = string.Empty; // only overwrite an alias the operator hasn't edited

    [ObservableProperty] private CloudGroupType _type = CloudGroupType.Security;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _mailNickname = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _visibility = "Private";
    [ObservableProperty] private bool _hiddenMembership;
    [ObservableProperty] private bool _allowExternalSenders;
    [ObservableProperty] private bool _sendWelcomeEmail;
    [ObservableProperty] private bool _hideInOutlook;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = string.Empty;

    public ObservableCollection<CloudPrincipalRow> Owners { get; } = new();
    public ObservableCollection<CloudPrincipalRow> Members { get; } = new();

    public IReadOnlyList<OptionItem<CloudGroupType>> TypeOptions { get; } = new[]
    {
        new OptionItem<CloudGroupType>(CloudGroupType.Security, "Security (Entra ID)"),
        new OptionItem<CloudGroupType>(CloudGroupType.Microsoft365, "Microsoft 365 (Entra ID)"),
        new OptionItem<CloudGroupType>(CloudGroupType.Distribution, "Distribution list (Exchange Online)"),
        new OptionItem<CloudGroupType>(CloudGroupType.MailEnabledSecurity, "Mail-enabled security (Exchange Online)"),
    };

    public IReadOnlyList<OptionItem<string>> VisibilityOptions { get; } = new[]
    {
        new OptionItem<string>("Private", "Private"),
        new OptionItem<string>("Public", "Public"),
    };

    /// <summary>True for the two kinds Exchange owns — drives the mail-only options.</summary>
    public bool IsExchangeType => CloudGroupValidator.IsExchangeType(Type);

    /// <summary>Visibility is a Microsoft 365 concept; it means nothing for the other kinds.</summary>
    public bool ShowVisibility => Type == CloudGroupType.Microsoft365;

    /// <summary>A plain security group has no membership list to hide.</summary>
    public bool ShowHiddenMembership => Type != CloudGroupType.Security;

    /// <summary>
    /// Graph's <c>resourceBehaviorOptions</c> is a Microsoft 365 concept. A security group has no mailbox to
    /// send a welcome email from and nothing to hide in Outlook, and the Exchange kinds don't have the
    /// collection at all, so these options are meaningless outside the one type.
    /// </summary>
    public bool ShowMicrosoft365Options => Type == CloudGroupType.Microsoft365;

    /// <summary>The label for the alias field, which is called different things by each backend.</summary>
    public string NicknameLabel => IsExchangeType ? "Alias" : "Mail nickname";

    /// <summary>The created group's id; null until then.</summary>
    public string? CreatedId { get; private set; }

    /// <summary>The created group's display name, so the host can search the list for it.</summary>
    public string? CreatedName { get; private set; }

    /// <summary>True when the created group came from Exchange, so the host reloads the right list.</summary>
    public bool CreatedInExchange { get; private set; }

    /// <summary>Raised after a successful create so the (modal) window can close.</summary>
    public event Action? Created;

    public NewCloudGroupViewModel(IGraphService graph, IExchangeService exchange, IDialogService dialogs, CloudGroupType? initialType)
    {
        _graph = graph;
        _exchange = exchange;
        _dialogs = dialogs;
        if (initialType is { } t) _type = t;
    }

    /// <summary>
    /// Seeds the signed-in admin as the default owner. Separate from the constructor because it needs a Graph
    /// round trip; the host starts it and shows the dialog immediately, so the row appears a moment later.
    ///
    /// An <b>admin</b> creating a security group with no owner gets an <b>ownerless</b> group: Graph auto-assigns
    /// the caller only for Microsoft 365 groups, or when the caller isn't an admin. The common path through this
    /// dialog is exactly that combination — an admin, a security group, nobody picked — so the field defaults
    /// rather than sitting empty and producing the thing the requirement exists to prevent.
    ///
    /// The seed is for the GRAPH kinds only. Exchange takes owners as <c>-ManagedBy</c> ON the create, where a
    /// principal it can't resolve fails the whole command and no group is made — and the row's address is a UPN,
    /// which is not necessarily a mail address, so an admin without a mailbox would lose every Exchange create
    /// to a default they never asked for. There is no ownerless hazard to solve there either: Exchange assigns
    /// an owner itself.
    ///
    /// Fails soft, and deliberately: the field is optional and the row is removable, so a lookup that doesn't
    /// work leaves it empty and logs, rather than blocking a dialog opened to do something else.
    /// </summary>
    public async Task InitializeAsync()
    {
        var upn = _graph.SignedInAccount;
        if (string.IsNullOrWhiteSpace(upn) || Owners.Count > 0 || IsExchangeType) return;
        try
        {
            var me = await _graph.GetUserByUpnAsync(upn!);
            if (me is null || string.IsNullOrWhiteSpace(me.Id)) return;
            // Re-check both guards: the lookup was in flight, so the operator may have picked an owner or
            // switched to an Exchange type in the meantime.
            if (Owners.Count > 0 || IsExchangeType) return;
            Owners.Add(new CloudPrincipalRow(new CloudGroupPrincipal(
                me.Id,
                string.IsNullOrWhiteSpace(me.DisplayName) ? upn! : me.DisplayName!,
                string.IsNullOrWhiteSpace(me.UserPrincipalName) ? upn! : me.UserPrincipalName!),
                CloudObjectKind.User,
                Seeded: true));
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn(
                $"Could not default the new group's owner to the signed-in admin ({upn}): {GraphErrors.Friendly(ex)}");
        }
    }

    partial void OnTypeChanged(CloudGroupType value)
    {
        OnPropertyChanged(nameof(IsExchangeType));
        OnPropertyChanged(nameof(ShowVisibility));
        OnPropertyChanged(nameof(ShowHiddenMembership));
        OnPropertyChanged(nameof(ShowMicrosoft365Options));
        OnPropertyChanged(nameof(NicknameLabel));

        // A device is a legal MEMBER of a plain security group and of nothing else, so switching away from
        // Security would otherwise strand one in Members to be refused at create time, on a group that by then
        // exists. Drop it here, visibly.
        var dropped = value == CloudGroupType.Security ? null : DropDevices();

        // Same reasoning for the owner this dialog seeded: its address is a UPN, and Exchange takes owners on
        // the create itself, so an unresolvable one loses the whole group rather than degrading to a warning.
        // Only the seeded row goes — an owner the operator picked deliberately is theirs to keep or remove.
        var unseeded = CloudGroupValidator.IsExchangeType(value) ? DropSeededOwner() : null;

        // The two backends cap the name differently and forbid different characters in the alias, so a value the
        // operator already typed can stop being legal the instant they change the type, without them touching
        // it. Re-check both here rather than letting it fail at the server on submit. Empty fields report
        // nothing: the required-field messages belong to the create, not to flipping a radio button.
        var problem = CloudGroupValidator.ValidateName(value, Name)
                      ?? CloudGroupValidator.ValidateNickname(value, MailNickname);

        Status = string.Join("  ", new[] { dropped, unseeded, problem }.Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>
    /// Removes the owner row this dialog seeded (never one the operator picked). Returns a message naming it, or
    /// null when there wasn't one.
    /// </summary>
    private string? DropSeededOwner()
    {
        var seeded = Owners.Where(o => o.Seeded).ToList();
        if (seeded.Count == 0) return null;
        foreach (var row in seeded) Owners.Remove(row);
        return "Removed the default owner (Exchange assigns its own): " + string.Join(", ", seeded.Select(s => s.Name));
    }

    /// <summary>
    /// Removes device rows, which only the Security type accepts. Returns a message naming them, or null when
    /// there were none. Done on the type change rather than at submit so the operator watches the list change
    /// instead of learning about it from a warning after the group has been created.
    /// </summary>
    private string? DropDevices()
    {
        var dropped = Members.Where(m => m.IsDevice).ToList();
        if (dropped.Count == 0) return null;
        foreach (var row in dropped) Members.Remove(row);
        return "Removed (devices can only be members of a security group): " +
               string.Join(", ", dropped.Select(d => d.Name));
    }

    /// <summary>Keeps the alias in step with the name until the operator types their own.</summary>
    partial void OnNameChanged(string value)
    {
        if (!string.Equals(MailNickname, _lastNicknameSuggestion, StringComparison.Ordinal)) return; // manually edited
        _lastNicknameSuggestion = CloudGroupValidator.DeriveNickname(value);
        MailNickname = _lastNicknameSuggestion;
    }

    [RelayCommand]
    private void AddOwners() => Pick("Choose owners for the new group", Owners, owners: true);

    [RelayCommand]
    private void AddMembers() => Pick("Choose members for the new group", Members, owners: false);

    private void Pick(string title, ObservableCollection<CloudPrincipalRow> into, bool owners)
    {
        var picked = _dialogs.PickCloudMembers(title);
        if (picked is null) return;

        var skipped = new List<string>();
        foreach (var row in picked)
        {
            // Which principals are legal depends on the kind AND on which list is being filled. A Microsoft 365
            // group accepts ONLY users, and Exchange addresses members by mail so a device can't be one. A plain
            // security group genuinely can CONTAIN devices, and Graph binds them through the same
            // directoryObjects reference used for users — but nothing owns a group except a user. The owners
            // relationship is bound through /users/ (GraphService.BindPrincipalsAsync), where a device id does
            // not resolve, so accepting one here buys an unexplained "these owners were not set" warning on a
            // group that was otherwise created correctly.
            var deviceAllowed = !owners && Type == CloudGroupType.Security;
            if (row.Kind != CloudObjectKind.User && !(deviceAllowed && row.Kind == CloudObjectKind.Device))
            {
                skipped.Add($"{row.DisplayName} ({DeclineReason(row.Kind, owners)})");
                continue;
            }

            var address = row.Get("mail");
            if (string.IsNullOrWhiteSpace(address)) address = row.Get("userPrincipalName");
            if (IsExchangeType && string.IsNullOrWhiteSpace(address)) { skipped.Add($"{row.DisplayName} (no mail address)"); continue; }

            if (into.Any(p => string.Equals(p.Principal.Id, row.Id, StringComparison.OrdinalIgnoreCase))) continue;
            into.Add(new CloudPrincipalRow(new CloudGroupPrincipal(row.Id, row.DisplayName, address), row.Kind));
        }

        Status = skipped.Count == 0 ? string.Empty : "Skipped: " + string.Join(", ", skipped);
    }

    /// <summary>Why a picked principal was refused, phrased for whichever list it was headed for.</summary>
    private static string DeclineReason(CloudObjectKind kind, bool owners) => kind switch
    {
        CloudObjectKind.Device when owners => "a device can't own a group",
        CloudObjectKind.Device => "devices can only be members of a security group",
        _ => "not a user",
    };

    [RelayCommand]
    private void RemoveOwner(CloudPrincipalRow? row) { if (row is not null) Owners.Remove(row); }

    [RelayCommand]
    private void RemoveMember(CloudPrincipalRow? row) { if (row is not null) Members.Remove(row); }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (_creating) return;

        var request = new CloudGroupCreateRequest
        {
            Type = Type,
            DisplayName = Name.Trim(),
            MailNickname = MailNickname.Trim(),
            Description = Description,
            Owners = Owners.Select(o => o.Principal).ToList(),
            Members = Members.Select(m => m.Principal).ToList(),
            Visibility = Visibility,
            HiddenMembership = ShowHiddenMembership && HiddenMembership,
            AllowExternalSenders = IsExchangeType && AllowExternalSenders,
            SendWelcomeEmail = ShowMicrosoft365Options && SendWelcomeEmail,
            HideInOutlook = ShowMicrosoft365Options && HideInOutlook,
        };

        if (CloudGroupValidator.Validate(request) is { } problem) { Status = problem; return; }

        // Both settings are irreversible, and neither can be undone from the group's properties afterwards.
        if (request.HiddenMembership &&
            !_dialogs.Confirm("Hide membership permanently?",
                $"“{request.DisplayName}” will be created with its membership hidden.",
                new[] { "This cannot be changed later — the group would have to be deleted and recreated." }))
            return;

        _creating = true;
        IsBusy = true;
        Status = "Creating…";
        try
        {
            var exchange = CloudGroupValidator.IsExchangeType(Type);
            var result = exchange
                ? await _exchange.CreateDistributionGroupAsync(request)
                : await _graph.CreateGroupAsync(request);

            CreatedId = result.Id;
            // Prefer the name the service echoed back: a tenant naming policy can rewrite what was typed.
            CreatedName = string.IsNullOrWhiteSpace(result.DisplayName) ? request.DisplayName : result.DisplayName;
            CreatedInExchange = exchange;

            if (result.Warnings.Count > 0)
                _dialogs.Alert("Group created — some steps didn't complete",
                    $"“{result.DisplayName}” was created, but:\n\n• " + string.Join("\n• ", result.Warnings) +
                    "\n\nYou can finish these from the group's properties.");

            Created?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("Cloud group create failed.", ex);
            Status = "Create failed: " + (CloudGroupValidator.IsExchangeType(Type)
                ? ExchangeErrors.Friendly(ex)
                : GraphErrors.Friendly(ex));
        }
        finally { IsBusy = false; _creating = false; }
    }
}
