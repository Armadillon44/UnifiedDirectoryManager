using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>One picked owner or member, shown as a removable row.</summary>
public sealed record CloudPrincipalRow(CloudGroupPrincipal Principal)
{
    public string Name => Principal.DisplayName;
    public string Detail => string.IsNullOrWhiteSpace(Principal.Address) ? Principal.Id : Principal.Address;
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

    partial void OnTypeChanged(CloudGroupType value)
    {
        OnPropertyChanged(nameof(IsExchangeType));
        OnPropertyChanged(nameof(ShowVisibility));
        OnPropertyChanged(nameof(ShowHiddenMembership));
        OnPropertyChanged(nameof(NicknameLabel));
        // The two rule sets differ, so re-check the alias against the newly selected backend rather than
        // letting a value that was valid a moment ago fail at the server.
        Status = string.Empty;
    }

    /// <summary>Keeps the alias in step with the name until the operator types their own.</summary>
    partial void OnNameChanged(string value)
    {
        if (!string.Equals(MailNickname, _lastNicknameSuggestion, StringComparison.Ordinal)) return; // manually edited
        _lastNicknameSuggestion = CloudGroupValidator.DeriveNickname(value);
        MailNickname = _lastNicknameSuggestion;
    }

    [RelayCommand]
    private void AddOwners() => Pick("Choose owners for the new group", Owners);

    [RelayCommand]
    private void AddMembers() => Pick("Choose members for the new group", Members);

    private void Pick(string title, ObservableCollection<CloudPrincipalRow> into)
    {
        var picked = _dialogs.PickCloudMembers(title);
        if (picked is null) return;

        var skipped = new List<string>();
        foreach (var row in picked)
        {
            // Which principals are legal depends on the kind: a Microsoft 365 group accepts ONLY users, and
            // Exchange addresses members by mail so a device can't be one. A plain security group genuinely can
            // contain devices, and Graph binds them through the same directoryObjects reference used for users,
            // so they are allowed there rather than refused for uniformity's sake.
            var deviceAllowed = Type == CloudGroupType.Security;
            if (row.Kind != CloudObjectKind.User && !(deviceAllowed && row.Kind == CloudObjectKind.Device))
            {
                skipped.Add($"{row.DisplayName} ({(row.Kind == CloudObjectKind.Device ? "devices can only be members of a security group" : "not a user")})");
                continue;
            }

            var address = row.Get("mail");
            if (string.IsNullOrWhiteSpace(address)) address = row.Get("userPrincipalName");
            if (IsExchangeType && string.IsNullOrWhiteSpace(address)) { skipped.Add($"{row.DisplayName} (no mail address)"); continue; }

            if (into.Any(p => string.Equals(p.Principal.Id, row.Id, StringComparison.OrdinalIgnoreCase))) continue;
            into.Add(new CloudPrincipalRow(new CloudGroupPrincipal(row.Id, row.DisplayName, address)));
        }

        Status = skipped.Count == 0 ? string.Empty : "Skipped: " + string.Join(", ", skipped);
    }

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
