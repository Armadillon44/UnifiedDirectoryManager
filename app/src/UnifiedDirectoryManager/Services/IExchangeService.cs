using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Exchange Online mailbox management for a pure-cloud tenant. Hosts the ExchangeOnlineManagement
/// PowerShell module in-process and connects with an access token borrowed from the existing Entra
/// sign-in (<see cref="IGraphService.GetAccessTokenAsync"/>), so no second browser prompt is needed.
///
/// v2.0 scope: convert mailboxes between Regular and Shared, and set/clear internal (recipient)
/// forwarding. These operations are Exchange-only (Set-Mailbox) and have no Microsoft Graph equivalent.
/// Callers confirm destructive changes first, exactly as with <see cref="IGraphService"/>.
/// </summary>
public interface IExchangeService
{
    /// <summary>True once a tenant/organization has been supplied (a connect can be attempted).</summary>
    bool IsConfigured { get; }

    /// <summary>True once an Exchange Online session is open.</summary>
    bool IsConnected { get; }

    /// <summary>The organization (tenant) the session is bound to, for display; null until configured.</summary>
    string? Organization { get; }

    /// <summary>Records the tenant/organization to connect to. Does not connect.</summary>
    void Configure(string organization);

    /// <summary>
    /// Opens an Exchange Online session: borrows an outlook.office365.com token from the Entra sign-in,
    /// spins up the hosted runspace, and runs Connect-ExchangeOnline. Idempotent — a no-op if already
    /// connected. Throws if not configured, the admin isn't signed in, or the connect fails.
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the Exchange Online session (best-effort) and releases the runspace.</summary>
    void Disconnect();

    /// <summary>
    /// Reads a mailbox by identity (UPN / primary SMTP). Returns null if no mailbox exists for that
    /// identity. Connects first if needed.
    /// </summary>
    Task<MailboxInfo?> GetMailboxAsync(string identity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches mail-enabled recipients (users, shared mailboxes, distribution groups) by display name
    /// or address — used to pick the internal target of a forwarding rule. Empty text returns a first page.
    /// </summary>
    Task<IReadOnlyList<MailboxRecipient>> SearchRecipientsAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts a mailbox to <paramref name="type"/> (Regular or Shared) via Set-Mailbox -Type. Throws for
    /// <see cref="MailboxType.Unknown"/>. Converting to Shared lets the account be unlicensed without losing
    /// the mailbox (the core termination use case).
    /// </summary>
    Task ConvertMailboxAsync(string identity, MailboxType type, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets internal forwarding on a mailbox to <paramref name="forwardingTargetIdentity"/> (a mail-enabled
    /// recipient), via Set-Mailbox -ForwardingAddress. When <paramref name="deliverToMailboxAndForward"/> is
    /// true the message is kept in the mailbox as well as forwarded; when false it is only forwarded.
    /// </summary>
    Task SetForwardingAsync(string identity, string forwardingTargetIdentity, bool deliverToMailboxAndForward, CancellationToken cancellationToken = default);

    /// <summary>Clears any internal forwarding on a mailbox (ForwardingAddress = null, DeliverToMailboxAndForward = false).</summary>
    Task ClearForwardingAsync(string identity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists a mailbox's delegates — recipients granted Full Access, Send As, and/or Send on Behalf — merged
    /// into one row per delegate (inherited and NT AUTHORITY\SELF entries excluded).
    /// </summary>
    Task<IReadOnlyList<MailboxDelegate>> GetDelegatesAsync(string identity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants a delegate the given <paramref name="access"/> on a mailbox. <paramref name="autoMapping"/> applies to
    /// Full Access only (auto-mount the mailbox in the delegate's Outlook). Each permission is applied
    /// independently and idempotently — re-granting one the delegate already holds is tolerated as success
    /// (Send As in particular errors on a duplicate ACE, which the implementation swallows).
    /// </summary>
    Task AddDelegateAsync(string identity, string delegateIdentity, DelegateAccess access, bool autoMapping, CancellationToken cancellationToken = default);

    /// <summary>Removes the given <paramref name="access"/> permissions for a delegate on a mailbox.</summary>
    Task RemoveDelegateAsync(string identity, string delegateIdentity, DelegateAccess access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes <paramref name="memberIdentity"/> from a distribution list or mail-enabled security group
    /// (<paramref name="groupIdentity"/>, by primary SMTP / alias / name) via Remove-DistributionGroupMember.
    /// These groups can't be modified through Microsoft Graph, so this is the only path to remove a member.
    /// Idempotent — if the member isn't in the group, it's treated as success. Does not apply to Exchange
    /// dynamic distribution groups (their membership is query-computed).
    /// </summary>
    Task RemoveDistributionGroupMemberAsync(string groupIdentity, string memberIdentity, CancellationToken cancellationToken = default);
}
