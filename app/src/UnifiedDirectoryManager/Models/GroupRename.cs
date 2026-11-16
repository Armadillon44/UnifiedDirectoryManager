namespace UnifiedDirectoryManager.Models;

/// <summary>Which service a group is renamed through. Decided by what the group is, not by which list it was opened from.</summary>
public enum GroupRenameKind
{
    /// <summary>An on-prem AD group, synced to Microsoft 365 or not. Written over LDAP; the cloud copy follows at sync.</summary>
    OnPremAd,
    /// <summary>A cloud-only Entra security group that is not mail-enabled. Written through Microsoft Graph.</summary>
    EntraSecurity,
    /// <summary>A cloud-only Microsoft 365 group, including one behind a Team. Graph for the name, Exchange for the address.</summary>
    Microsoft365,
    /// <summary>A cloud-only distribution list, mail-enabled security group or room list. Written through Exchange Online.</summary>
    ExchangeDistribution,
}

/// <summary>Where the operator opened the group from, which says how to find it — not how to rename it.</summary>
public enum GroupRenameOrigin
{
    /// <summary><see cref="GroupRenameSource.Identity"/> is an on-prem distinguished name.</summary>
    OnPremDn,
    /// <summary><see cref="GroupRenameSource.Identity"/> is an Entra object ID.</summary>
    EntraGroupId,
    /// <summary><see cref="GroupRenameSource.Identity"/> is anything Exchange resolves: a GUID, an address or an ID.</summary>
    ExchangeIdentity,
}

/// <summary>A group to rename, as the place it was opened from knows it.</summary>
public sealed record GroupRenameSource(GroupRenameOrigin Origin, string Identity, string? DisplayHint = null);

/// <summary>
/// A group as read immediately before renaming it: where it lives, how to address the write, and every name it
/// has now. The renamer compares the request against this, so anything not read here cannot be changed.
/// </summary>
public sealed record GroupRenameSubject
{
    public required GroupRenameKind Kind { get; init; }

    /// <summary>
    /// How the write addresses the group: the DN on-prem, the object ID in Entra, the Exchange GUID for an
    /// Exchange group. The GUID rather than an address, because the rename may change the address.
    /// </summary>
    public required string Identity { get; init; }

    /// <summary>The Entra object ID, when known: what templates and scenarios store a cloud group by.</summary>
    public string? EntraId { get; init; }

    /// <summary>The on-prem objectGUID: what templates and scenarios find an on-prem group by.</summary>
    public string? ObjectGuid { get; init; }

    /// <summary>The on-prem SID, used to recognise the group's own cloud copy when checking an address.</summary>
    public string? Sid { get; init; }

    /// <summary>The naming value: on-prem <c>cn</c>, Exchange <c>Name</c>, or the Entra display name.</summary>
    public required string Name { get; init; }

    /// <summary>The display name. Empty when an on-prem group has none, which ADUC leaves unset.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>On-prem only: the pre-Windows 2000 name.</summary>
    public string? SamAccountName { get; init; }

    /// <summary>The primary SMTP address, or null when the group is not mail-enabled.</summary>
    public string? PrimaryAddress { get; init; }

    /// <summary>Every address with its type prefix (SMTP:, smtp:, X500:, …), as stored.</summary>
    public IReadOnlyList<string> Addresses { get; init; } = Array.Empty<string>();

    /// <summary>True when a Microsoft 365 group backs a Team.</summary>
    public bool IsTeam { get; init; }

    /// <summary>How the dialog describes the group, e.g. "Microsoft 365 group (Team)".</summary>
    public string KindLabel { get; init; } = string.Empty;

    /// <summary>True when the group was opened from a cloud list but is synced, so it is renamed on-prem instead.</summary>
    public bool RedirectedFromCloud { get; init; }

    public bool IsMailEnabled => !string.IsNullOrWhiteSpace(PrimaryAddress);

    /// <summary>On-prem and Exchange groups have a name separate from their display name; Entra groups have only the display name.</summary>
    public bool HasSeparateName => Kind is GroupRenameKind.OnPremAd or GroupRenameKind.ExchangeDistribution;
}

/// <summary>What the operator typed. Null <see cref="NewPrimaryAddress"/> means the address is not being changed.</summary>
public sealed record GroupRenameRequest(string Name, string DisplayName, string? SamAccountName, string? NewPrimaryAddress);

/// <summary>One line of the before-and-after confirmation.</summary>
public sealed record GroupRenameChange(string What, string Before, string After);

/// <summary>The result of checking a request before anything is written.</summary>
/// <param name="Problems">Reasons the rename cannot go ahead. Any one blocks it.</param>
/// <param name="Warnings">Things the operator should know, which do not block.</param>
/// <param name="Changes">What will change, for the confirmation.</param>
public sealed record GroupRenameCheck(
    IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings, IReadOnlyList<GroupRenameChange> Changes)
{
    public bool CanApply => Problems.Count == 0 && Changes.Count > 0;
}

/// <summary>What a rename did, read back from the service that holds the group rather than assumed from the request.</summary>
/// <param name="Succeeded">True when everything asked for was written.</param>
/// <param name="Identity">How to address the group now: the new DN on-prem; unchanged elsewhere.</param>
/// <param name="ActualName">The naming value now, as read back.</param>
/// <param name="ActualDisplayName">The display name now, as read back; a naming policy may have changed it.</param>
/// <param name="ActualPrimaryAddress">The primary address now, as read back.</param>
/// <param name="Notes">What else happened worth saying: a naming policy applied, the old address kept, references updated.</param>
/// <param name="Error">Why it did not finish, when it did not.</param>
/// <param name="NamePending">On-prem only: the attributes were written but the name in the tree was not; only that is retried.</param>
public sealed record GroupRenameOutcome(
    bool Succeeded, string Identity, string ActualName, string ActualDisplayName, string? ActualPrimaryAddress,
    IReadOnlyList<string> Notes, string? Error, bool NamePending);

/// <summary>A cloud group's names, as Microsoft Graph reports them, for renaming it.</summary>
public sealed record CloudGroupNaming(
    string Id, string DisplayName, string? Mail, string? MailNickname, IReadOnlyList<string> ProxyAddresses,
    string GroupKind, bool IsTeam, bool IsSynced, string? OnPremSid);

/// <summary>An Entra object that holds an address, found when checking that a new address is free.</summary>
public sealed record CloudAddressOwner(string Id, string DisplayName, string Kind, string? OnPremSid);

/// <summary>An Exchange Online distribution group's names, as Exchange reports them.</summary>
public sealed record DistributionGroupNaming(
    string Guid, string Name, string DisplayName, string Alias, string PrimarySmtpAddress,
    IReadOnlyList<string> EmailAddresses, bool IsDirSynced, string? ExternalDirectoryObjectId, string RecipientTypeDetails);

/// <summary>An Exchange recipient that already holds a name or an address the rename wants.</summary>
/// <param name="Field">"Name" or "Address": which of the two it holds.</param>
/// <param name="EntraId">The recipient's Entra object ID (ExternalDirectoryObjectId), or null when it has none.
/// It is how a group renamed through AD or Graph is told apart from other holders of its own addresses.</param>
public sealed record RecipientConflict(string Identity, string DisplayName, string Kind, string Field, string? EntraId);
