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
    /// Resolves a batch of pasted terms to Exchange recipients, for adding many members at once. Sent in
    /// chunks: each term costs a directory lookup inside the op, and a whole paste in one call would run at
    /// the operation timeout — which kills the hosted pwsh process and every open session with it.
    ///
    /// Each term is tried EXACTLY first (address — including proxy addresses — alias, then whole display
    /// name) and only then ambiguously, via ANR. The distinction is carried back, because only an exact match
    /// may resolve a row on its own: a search hit is a guess about a half-specified name and always asks.
    /// </summary>
    /// <param name="progress">Reports how many terms have been resolved, for a paste that takes a while.</param>
    Task<IReadOnlyList<MemberResolution>> ResolveMembersAsync(IReadOnlyList<PastedTerm> terms, IProgress<int>? progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists mailboxes as list rows, optionally narrowed by <paramref name="search"/> (matched server-side
    /// against display name, primary SMTP, and alias). At most <paramref name="max"/> rows are returned;
    /// see <see cref="ExchangePage.Capped"/> — Exchange has no continuation token, so a capped result is the
    /// only way to bound the call, and the cap must be surfaced rather than passed off as the full set.
    /// </summary>
    Task<ExchangePage> ListMailboxesAsync(string? search, int max, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists distribution groups and mail-enabled security groups as list rows, with the properties Microsoft
    /// Graph can't expose (directory-sync state, owners, external-sender policy, join restriction). Same
    /// search and capping semantics as <see cref="ListMailboxesAsync"/>. Microsoft 365 (unified) groups are
    /// deliberately absent: <c>Get-DistributionGroup</c> doesn't return them and they are managed via Graph.
    /// </summary>
    Task<ExchangePage> ListDistributionGroupsAsync(string? search, int max, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a <b>distribution list</b> or <b>mail-enabled security group</b> via <c>New-DistributionGroup</c>.
    /// These two kinds are read-only in Microsoft Graph, so this is the only path. Throws for the Graph-managed
    /// kinds (<see cref="IGraphService.CreateGroupAsync"/> handles those).
    ///
    /// Unlike the Graph path, owners and members are supplied on the create itself, so a failure here means the
    /// group was not created at all. The returned result therefore never carries warnings.
    /// </summary>
    Task<CloudGroupCreateResult> CreateDistributionGroupAsync(CloudGroupCreateRequest request, CancellationToken cancellationToken = default);

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
    /// Reads a mailbox's state as grouped, read-only property sections for the details pane: identity, type,
    /// addresses, delivery and forwarding, quotas, hold and retention, archive, and protocol access.
    ///
    /// Two cheap directory-backed calls (<c>Get-EXOMailbox</c> + <c>Get-EXOCasMailbox</c>) and deliberately not
    /// <see cref="GetMailboxAsync"/>, which runs with <c>-ErrorAction SilentlyContinue</c> and so reports an
    /// access-denied as "no mailbox found". Microsoft Graph can replace none of this — forwarding, holds,
    /// quotas, archive state and the protocol flags have no Graph equivalent.
    /// </summary>
    Task<IReadOnlyList<CloudPropertySection>> GetMailboxDetailAsync(string identity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads size, item counts and last logon for ONE mailbox. Separate from
    /// <see cref="GetMailboxDetailAsync"/> and never called as part of opening a mailbox: this hits the mailbox
    /// store rather than the directory, takes one mailbox per call, and fanning it across a list is the
    /// documented way to exhaust the Exchange throttling budget.
    /// </summary>
    /// <param name="exchangeGuid">The mailbox's <c>ExchangeGuid</c> when known. Preferred over the identity
    /// because it is exact, and because it is the ONLY identifier this cmdlet echoes back — its result carries
    /// no UPN, address or alias to check, so without it there is nothing to verify the answer against.</param>
    Task<CloudPropertySection> GetMailboxUsageAsync(string identity, string? exchangeGuid = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a distribution list's or mail-enabled security group's state as grouped, read-only property
    /// sections. Microsoft Graph cannot describe these — it exposes almost none of what matters here (owners,
    /// join restriction, sender authentication, moderation) and is read-only for them besides.
    /// </summary>
    Task<IReadOnlyList<CloudPropertySection>> GetDistributionGroupDetailAsync(string identity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves ONE recipient-valued property of a distribution group to names and addresses, on demand.
    /// Separate from <see cref="GetDistributionGroupDetailAsync"/> and never part of opening a group: it costs
    /// a directory lookup per entry on a channel that serialises every call, and the addresses are only needed
    /// when an operator opens the editor. An entry that could not be resolved comes back with an empty
    /// <see cref="MailboxRecipient.PrimarySmtpAddress"/> — it cannot be written back, so the caller must
    /// refuse to edit the list rather than save it and drop that entry.
    /// </summary>
    /// <param name="rowKey">The property-row key, e.g. <c>managedBy</c>; the implementation owns the mapping.</param>
    Task<DlRecipientList> GetDistributionGroupRecipientsAsync(string identity, string rowKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies scalar changes to a distribution list or mail-enabled security group via
    /// <c>Set-DistributionGroup</c>. <paramref name="changes"/> is keyed by the property-row key that
    /// <see cref="GetDistributionGroupDetailAsync"/> produced, carrying the value as the pane displays it; the
    /// implementation owns the translation to Exchange parameters and rejects any key it does not recognise,
    /// because a change that is silently dropped is indistinguishable from one that was saved. The rows are
    /// passed whole rather than as key/value pairs because the address editor needs the ORIGINAL value too:
    /// addresses are written as an add/remove against what was there, never as a replacement.
    ///
    /// Writes are refused for a group synced from on-premises Active Directory: Exchange rejects them anyway,
    /// but its own error names neither the group nor the reason. Runs with
    /// <c>-BypassSecurityGroupManagerCheck</c>, since the owner is usually a role group that no individual is a
    /// "manager of", and addresses the group by its Exchange GUID rather than by an address a rename may move.
    /// </summary>
    /// <returns>The keys of any rows that needed nothing sent after all — a row can be edited into an
    /// equivalent value, and the caller must not report those as saved.</returns>
    Task<IReadOnlyList<string>> SetDistributionGroupPropertiesAsync(string identity, IReadOnlyList<CloudProperty> changes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists a distribution list's or mail-enabled security group's members. Reads the whole membership
    /// (<c>-ResultSize Unlimited</c>): the default page is 1,000 and truncates in silence, which on a
    /// membership list would read as "these are all the members".
    ///
    /// Does not apply to Exchange <b>dynamic</b> distribution groups, whose membership is query-computed —
    /// those never appear in this app's list, since <c>Get-DistributionGroup</c> doesn't return them.
    /// </summary>
    Task<IReadOnlyList<MailboxRecipient>> GetDistributionGroupMembersAsync(string groupIdentity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes <paramref name="memberIdentity"/> from a distribution list or mail-enabled security group
    /// (<paramref name="groupIdentity"/>, by primary SMTP / alias / name) via Remove-DistributionGroupMember.
    /// These groups can't be modified through Microsoft Graph, so this is the only path to remove a member.
    /// Idempotent — if the member isn't in the group, it's treated as success. Does not apply to Exchange
    /// dynamic distribution groups (their membership is query-computed).
    /// </summary>
    Task RemoveDistributionGroupMemberAsync(string groupIdentity, string memberIdentity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds <paramref name="memberIdentity"/> to a distribution list or mail-enabled security group
    /// (<paramref name="groupIdentity"/>, by primary SMTP / alias / name) via Add-DistributionGroupMember —
    /// the only path, since Graph can't modify these groups. Idempotent: if the member is already in the group
    /// it's treated as success. The member must already exist as an Exchange recipient (a freshly-synced user
    /// can take a short while to provision in Exchange Online, so a create-time add may need a retry).
    /// </summary>
    Task AddDistributionGroupMemberAsync(string groupIdentity, string memberIdentity, CancellationToken cancellationToken = default);
}
