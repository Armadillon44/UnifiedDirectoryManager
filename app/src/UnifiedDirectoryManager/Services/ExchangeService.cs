using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Out-of-process implementation of <see cref="IExchangeService"/>. Runs the ExchangeOnlineManagement module
/// in a separate <c>pwsh</c> process rather than in-process, because the EXO module bundles its own
/// MSAL/Azure.Core that clash with the Graph SDK's newer versions inside a single process (the connect fails
/// with "Module could not be correctly formed"). A child process gets its own clean assemblies and a correct
/// PowerShell module environment, so the module works reliably.
///
/// A single long-lived pwsh session is kept and reused across operations (connecting per-operation would be far
/// too slow for bulk scenario runs). Communication is a line protocol over stdin/stdout: the C# side sends
/// <c>VERB &lt;base64-json&gt;</c> lines; the host script replies with a framed JSON envelope. The EXO access token
/// is borrowed from the Entra sign-in (<see cref="IGraphService.GetAccessTokenAsync"/>) and passed via stdin
/// (never on the command line). Requires PowerShell 7 + the ExchangeOnlineManagement module on the machine.
/// </summary>
public sealed class ExchangeService : IExchangeService, IDisposable
{
    private static readonly string[] ExoScopes = { "https://outlook.office365.com/.default" };
    private const string Begin = "<<<UDM-BEGIN>>>";
    private const string End = "<<<UDM-END>>>";
    private const string Ready = "<<<UDM-READY>>>";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan OpTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Timeout for the list operations, which sweep many recipients rather than touching one. A
    /// timeout kills the host process (see <see cref="ReadLineLockedAsync"/>), taking the session down for
    /// every other feature, so a list is given room rather than being allowed to trip the single-object budget.</summary>
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(180);

    private readonly IGraphService _graph;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _organization;
    private Process? _pwsh;
    private readonly StringBuilder _stderr = new();
    private bool _connected;

    public ExchangeService(IGraphService graph) => _graph = graph;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_organization);
    public bool IsConnected => _connected && _pwsh is { HasExited: false };
    public string? Organization => _organization;

    public void Configure(string organization)
    {
        var org = organization?.Trim();
        if (string.Equals(org, _organization, StringComparison.OrdinalIgnoreCase)) return;
        Disconnect(); // a different org invalidates any existing session
        _organization = org;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await EnsureConnectedLockedAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public void Disconnect()
    {
        _gate.Wait();
        try { KillLocked(); }
        finally { _gate.Release(); }
    }

    public async Task<MailboxInfo?> GetMailboxAsync(string identity, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identity)) return null;
        var data = await RunOpAsync(new { op = "get-mailbox", identity }, cancellationToken).ConfigureAwait(false);
        return data is { ValueKind: JsonValueKind.Object } d ? MapMailbox(d) : null; // null data = mailbox not found
    }

    public async Task<IReadOnlyList<MailboxRecipient>> SearchRecipientsAsync(string text, CancellationToken cancellationToken = default)
    {
        var data = await RunOpAsync(new { op = "search-recipients", text = text ?? string.Empty }, cancellationToken).ConfigureAwait(false);
        var list = new List<MailboxRecipient>();
        if (data is { ValueKind: JsonValueKind.Array } arr)
            foreach (var e in arr.EnumerateArray()) list.Add(MapRecipient(e));
        else if (data is { ValueKind: JsonValueKind.Object } one) // ConvertTo-Json renders a single result as an object
            list.Add(MapRecipient(one));
        return list;
    }

    public Task<ExchangePage> ListMailboxesAsync(string? search, int max, CancellationToken cancellationToken = default)
        => ListAsync("list-mailboxes", search, max, MapMailboxRow, cancellationToken);

    public Task<ExchangePage> ListDistributionGroupsAsync(string? search, int max, CancellationToken cancellationToken = default)
        => ListAsync("list-distribution-groups", search, max, MapDistributionGroupRow, cancellationToken);

    /// <summary>
    /// Shared shape for the two list ops. Asks the host for one row more than the cap, so a result that exactly
    /// fills the cap can be told apart from one that was truncated, then trims back and reports the truncation.
    /// </summary>
    private async Task<ExchangePage> ListAsync(
        string op, string? search, int max, Func<JsonElement, CloudObjectRow> map, CancellationToken ct)
    {
        if (max < 1) max = 1;
        var data = await RunOpAsync(
            new { op, search = search ?? string.Empty, max = max + 1 }, ct, ListTimeout).ConfigureAwait(false);

        var rows = new List<CloudObjectRow>();
        if (data is { ValueKind: JsonValueKind.Array } arr)
            foreach (var e in arr.EnumerateArray()) rows.Add(map(e));
        else if (data is { ValueKind: JsonValueKind.Object } one) // ConvertTo-Json renders a single result as an object
            rows.Add(map(one));

        var capped = rows.Count > max;
        if (capped) rows.RemoveRange(max, rows.Count - max);
        return new ExchangePage(rows, capped);
    }

    public async Task<CloudGroupCreateResult> CreateDistributionGroupAsync(CloudGroupCreateRequest request, CancellationToken cancellationToken = default)
    {
        if (!CloudGroupValidator.IsExchangeType(request.Type))
            throw new ArgumentException("Security and Microsoft 365 groups are created through Microsoft Graph, not Exchange Online.", nameof(request));

        // Exchange addresses principals by SMTP; anyone without a mail identity can't be named here.
        var owners = request.Owners.Select(o => o.Address).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var members = request.Members.Select(m => m.Address).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var data = await RunOpAsync(new
        {
            op = "new-distribution-group",
            name = request.DisplayName.Trim(),
            alias = request.MailNickname.Trim(),
            type = request.Type == CloudGroupType.MailEnabledSecurity ? "Security" : "Distribution",
            description = request.Description?.Trim() ?? string.Empty,
            owners,
            members,
            // Exchange's flag is the double negative of the operator-facing option.
            requireAuth = !request.AllowExternalSenders,
            hiddenMembership = request.HiddenMembership,
        }, cancellationToken, ListTimeout).ConfigureAwait(false);

        string S(string p) => data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty : string.Empty;

        var id = S("ExternalDirectoryObjectId");
        var smtp = S("PrimarySmtpAddress");
        AppLog.Instance.Info($"Created Exchange group '{request.DisplayName}' ({request.Type}) as {(id.Length > 0 ? id : smtp)}.");
        // Prefer the directory object id so the row lines up with the Entra list; fall back to the address,
        // which is what Exchange itself addresses the group by.
        return new CloudGroupCreateResult(id.Length > 0 ? id : smtp, S("DisplayName"));
    }

    public Task ConvertMailboxAsync(string identity, MailboxType type, CancellationToken cancellationToken = default)
    {
        if (type == MailboxType.Unknown)
            throw new ArgumentException("Only Regular and Shared are convertible.", nameof(type));
        return RunOpAsync(new { op = "convert", identity, type = type == MailboxType.Shared ? "Shared" : "Regular" }, cancellationToken);
    }

    public Task SetForwardingAsync(string identity, string forwardingTargetIdentity, bool deliverToMailboxAndForward, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(forwardingTargetIdentity))
            throw new ArgumentException("A forwarding target is required.", nameof(forwardingTargetIdentity));
        return RunOpAsync(new { op = "set-forwarding", identity, target = forwardingTargetIdentity, deliver = deliverToMailboxAndForward }, cancellationToken);
    }

    public Task ClearForwardingAsync(string identity, CancellationToken cancellationToken = default)
        => RunOpAsync(new { op = "clear-forwarding", identity }, cancellationToken);

    public async Task<IReadOnlyList<MailboxDelegate>> GetDelegatesAsync(string identity, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identity)) return Array.Empty<MailboxDelegate>();
        var data = await RunOpAsync(new { op = "list-delegates", identity }, cancellationToken).ConfigureAwait(false);
        var list = new List<MailboxDelegate>();
        if (data is { ValueKind: JsonValueKind.Array } arr)
            foreach (var e in arr.EnumerateArray()) list.Add(MapDelegate(e));
        else if (data is { ValueKind: JsonValueKind.Object } one)
            list.Add(MapDelegate(one));
        return list;
    }

    public Task AddDelegateAsync(string identity, string delegateIdentity, DelegateAccess access, bool autoMapping, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(delegateIdentity)) throw new ArgumentException("A delegate is required.", nameof(delegateIdentity));
        if (access == DelegateAccess.None) throw new ArgumentException("At least one permission is required.", nameof(access));
        return RunOpAsync(new
        {
            op = "add-delegate",
            identity,
            delegateId = delegateIdentity,
            fullAccess = access.HasFlag(DelegateAccess.FullAccess),
            sendAs = access.HasFlag(DelegateAccess.SendAs),
            sendOnBehalf = access.HasFlag(DelegateAccess.SendOnBehalf),
            autoMapping,
        }, cancellationToken);
    }

    public Task RemoveDelegateAsync(string identity, string delegateIdentity, DelegateAccess access, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(delegateIdentity)) throw new ArgumentException("A delegate is required.", nameof(delegateIdentity));
        if (access == DelegateAccess.None) throw new ArgumentException("At least one permission is required.", nameof(access));
        return RunOpAsync(new
        {
            op = "remove-delegate",
            identity,
            delegateId = delegateIdentity,
            fullAccess = access.HasFlag(DelegateAccess.FullAccess),
            sendAs = access.HasFlag(DelegateAccess.SendAs),
            sendOnBehalf = access.HasFlag(DelegateAccess.SendOnBehalf),
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<CloudPropertySection>> GetMailboxDetailAsync(string identity, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identity)) throw new ArgumentException("A mailbox is required.", nameof(identity));
        var data = await RunOpAsync(new { op = "get-mailbox-detail", identity }, cancellationToken).ConfigureAwait(false);
        if (data is not { ValueKind: JsonValueKind.Object } d)
            throw new ExchangeException("Exchange Online returned no details for that mailbox.");

        string S(string p) => d.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

        var (primary, secondary, other) = SplitAddresses(d);
        static CloudProperty P(string key, string label, string value) => Prop(key, label, value, MailboxRowTip);

        var protocolsError = S("ProtocolsError");
        var sections = new List<CloudPropertySection>
        {
            Section("Identity",
                P("displayName", "Display name", S("DisplayName")),
                P("alias", "Alias", S("Alias")),
                P("userPrincipalName", "User principal name", S("UserPrincipalName")),
                P("primarySmtpAddress", "Primary SMTP", S("PrimarySmtpAddress")),
                P("exchangeGuid", "Exchange GUID", S("ExchangeGuid")),
                P("whenMailboxCreated", "Mailbox created", S("WhenMailboxCreated")),
                P("isDirSynced", "Directory sync", S("IsDirSynced") == "Yes" ? "Synced from on-premises" : "Cloud-only")),
            Section("Type and visibility",
                P("recipientTypeDetails", "Mailbox type", FriendlyRecipientType(S("RecipientTypeDetails"))),
                P("hiddenFromAddressLists", "Hidden from address lists", S("HiddenFromAddressLists"))),
            Section("Addresses",
                P("primaryAddress", "Primary", primary ?? string.Empty),
                P("secondaryAddresses", "Secondary", string.Join("; ", secondary)),
                P("otherAddresses", "Other", string.Join("; ", other))),
            Section("Delivery and forwarding",
                P("forwardingAddress", "Forwarding to (internal)", S("ForwardingAddress")),
                P("forwardingSmtpAddress", "Forwarding to (external)", S("ForwardingSmtpAddress")),
                P("deliverToMailboxAndForward", "Also deliver to this mailbox", S("DeliverToMailboxAndForward"))),
            Section("Quotas",
                P("issueWarningQuota", "Issue warning at", S("IssueWarningQuota")),
                P("prohibitSendQuota", "Prohibit send at", S("ProhibitSendQuota")),
                P("prohibitSendReceiveQuota", "Prohibit send/receive at", S("ProhibitSendReceiveQuota")),
                P("useDatabaseQuotaDefaults", "Using database defaults", S("UseDatabaseQuotaDefaults"))),
            Section("Hold and retention",
                P("litigationHoldEnabled", "Litigation hold", S("LitigationHoldEnabled")),
                P("litigationHoldDate", "Hold applied", S("LitigationHoldDate")),
                P("litigationHoldOwner", "Hold set by", S("LitigationHoldOwner")),
                P("litigationHoldDuration", "Hold duration", S("LitigationHoldDuration")),
                P("retentionPolicy", "Retention policy", S("RetentionPolicy"))),
            Section("Archive",
                // ArchiveStatus is shown but not trusted: Microsoft documents it reading "None" for a live
                // archive after a re-license or migration, so the presence answer comes from ArchiveGuid.
                P("hasArchive", "Archive mailbox", S("HasArchive")),
                P("archiveStatus", "Archive status (reported)", S("ArchiveStatus")),
                P("archiveState", "Archive state", S("ArchiveState")),
                P("archiveGuid", "Archive GUID", S("ArchiveGuid"))),
            // Protocols come from a second cmdlet behind a second permission. When that read fails, the section
            // says so ONCE and shows nothing else: rendering the six flags as "—" would use this pane's symbol
            // for "no value" to mean "we couldn't read it", and an operator would conclude ActiveSync is off.
            protocolsError.Length > 0
                ? Section("Protocols",
                    P("protocolsError", "Could not be read", protocolsError))
                : Section("Protocols",
                    P("owaEnabled", "Outlook on the web", S("OwaEnabled")),
                    P("activeSyncEnabled", "Exchange ActiveSync", S("ActiveSyncEnabled")),
                    P("mapiEnabled", "MAPI (Outlook desktop)", S("MapiEnabled")),
                    P("ewsEnabled", "Exchange Web Services", S("EwsEnabled")),
                    P("imapEnabled", "IMAP", S("ImapEnabled")),
                    P("popEnabled", "POP", S("PopEnabled"))),
        };
        return sections;
    }

    public async Task<CloudPropertySection> GetMailboxUsageAsync(string identity, string? exchangeGuid = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identity)) throw new ArgumentException("A mailbox is required.", nameof(identity));
        var data = await RunOpAsync(
            new { op = "get-mailbox-usage", identity, exchangeGuid = exchangeGuid ?? string.Empty },
            cancellationToken).ConfigureAwait(false);
        if (data is not { ValueKind: JsonValueKind.Object } d)
            throw new ExchangeException("Exchange Online returned no usage figures for that mailbox.");

        string S(string p) => d.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        static CloudProperty P(string key, string label, string value) => Prop(key, label, value, MailboxRowTip);
        return Section("Size and usage",
            P("totalItemSize", "Mailbox size", S("TotalItemSize")),
            P("itemCount", "Items", S("ItemCount")),
            P("totalDeletedItemSize", "Recoverable items size", S("TotalDeletedItemSize")),
            P("deletedItemCount", "Recoverable items", S("DeletedItemCount")),
            P("lastLogonTime", "Last logon", S("LastLogonTime")));
    }

    /// <summary>
    /// Splits an EmailAddresses collection into primary, secondary and anything else. Exchange prefixes each
    /// entry: a capital <c>SMTP:</c> marks the primary and a lowercase <c>smtp:</c> a secondary, and other
    /// prefixes (x500, sip) appear too and are neither.
    /// </summary>
    private static (string? Primary, List<string> Secondary, List<string> Other) SplitAddresses(JsonElement d)
    {
        var all = new List<string>();
        if (d.TryGetProperty("EmailAddresses", out var addrs) && addrs.ValueKind == JsonValueKind.Array)
            foreach (var a in addrs.EnumerateArray())
                if (a.GetString() is { Length: > 0 } s) all.Add(s);

        var primary = all.FirstOrDefault(a => a.StartsWith("SMTP:", StringComparison.Ordinal));
        var secondary = all.Where(a => a.StartsWith("smtp:", StringComparison.Ordinal)).Select(a => a[5..]).ToList();
        var other = all.Where(a => !a.StartsWith("SMTP:", StringComparison.Ordinal)
                                && !a.StartsWith("smtp:", StringComparison.Ordinal)).ToList();
        return (primary is null ? null : primary[5..], secondary, other);
    }

    public async Task<DlRecipientList> GetDistributionGroupRecipientsAsync(
        string identity, string rowKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identity)) throw new ArgumentException("A group is required.", nameof(identity));
        if (!DlRecipientFields.TryGetValue(rowKey, out var field))
            throw new ExchangeException($"'{rowKey}' is not a recipient list this app reads.");

        var data = await RunOpAsync(new { op = "list-dl-recipients", identity, field }, cancellationToken, ListTimeout)
            .ConfigureAwait(false);

        var entries = new List<MailboxRecipient>();
        var unresolved = new List<string>();
        var notLookedUp = 0;
        void Take(JsonElement e)
        {
            var (recipient, reason) = MapResolvedRecipient(e);
            entries.Add(recipient);
            // Being over the budget is a different answer from not existing, and the two must not be
            // reported as the same thing: one is this app's limit, the other is a deleted account.
            if (reason == "skipped") notLookedUp++;
            else if (string.IsNullOrWhiteSpace(recipient.PrimarySmtpAddress)) unresolved.Add(recipient.DisplayName);
        }
        if (data is { ValueKind: JsonValueKind.Array } arr)
            foreach (var e in arr.EnumerateArray()) Take(e);
        else if (data is { ValueKind: JsonValueKind.Object } one) // a single result serialises as an object
            Take(one);
        return new DlRecipientList(entries, unresolved, notLookedUp);
    }

    private static (MailboxRecipient Recipient, string Reason) MapResolvedRecipient(JsonElement e)
    {
        string S(string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        var smtp = S("Smtp");
        return (new MailboxRecipient
        {
            DisplayName = S("Name"),
            PrimarySmtpAddress = smtp,
            Identity = smtp,
            RecipientType = S("Type"),
        }, S("Reason"));
    }

    public async Task<IReadOnlyList<CloudPropertySection>> GetDistributionGroupDetailAsync(string identity, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identity)) throw new ArgumentException("A group is required.", nameof(identity));
        var data = await RunOpAsync(new { op = "get-dl-detail", identity }, cancellationToken).ConfigureAwait(false);
        if (data is not { ValueKind: JsonValueKind.Object } d)
            throw new ExchangeException("Exchange Online returned no details for that group.");

        string S(string p) => d.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        var (primary, secondary, other) = SplitAddresses(d);
        static CloudProperty P(string key, string label, string value) => Prop(key, label, value, GroupRowTip);

        // Three facts decide what this pane may offer to change.
        var synced = S("IsDirSynced") == "Yes";
        var typeDetails = S("RecipientTypeDetails");
        var isSecurity = string.Equals(typeDetails, "MailUniversalSecurityGroup", StringComparison.OrdinalIgnoreCase);
        var isRoomList = string.Equals(typeDetails, "RoomList", StringComparison.OrdinalIgnoreCase);

        // One editable row. The blocked argument gives the reason this particular setting is not offered, if
        // any; a synced group overrides every such reason, because Exchange rejects all writes against one.
        CloudProperty E(string key, string label, string value, string? blocked = null, string[]? choices = null,
                        string? editableTip = null, bool recipients = false)
        {
            var help = CloudPropertyHelp.For(key);
            if (synced) return new(key, label, Show(value), CloudPropertyEditability.OnPremMastered, SyncedRowTip, help: help);
            if (blocked is not null) return new(key, label, Show(value), CloudPropertyEditability.SystemReadOnly, blocked, help: help);
            // A recipient list shows names and writes addresses, so it is never typed and never validated here.
            if (recipients)
                return new(key, label, Show(value), CloudPropertyEditability.Editable, editableTip,
                           CloudPropertyEditor.Recipients, help: help);
            // A drop-down whose current value is not one of its own choices means Exchange answered with
            // something this pane does not understand, or did not answer at all. Editing from there would
            // write over a value that was never read.
            if (choices is not null && !choices.Contains(value, StringComparer.Ordinal))
                return new(key, label, Show(value), CloudPropertyEditability.SystemReadOnly, UnreadRowTip, help: help);
            return new(key, label, value, CloudPropertyEditability.Editable, editableTip,
                       choices is null ? CloudPropertyEditor.Text : CloudPropertyEditor.Choice, choices, help);
        }

        // The onmicrosoft.com addresses are the ones Microsoft 365 maintains for routing and hybrid mail flow.
        // Removing one breaks delivery and the service puts it back, so they are held out of the editable row
        // entirely rather than being offered and then rejected.
        var service = secondary.Where(IsServiceAddress).ToList();
        var vanity = secondary.Where(a => !IsServiceAddress(a)).ToList();

        // Join and depart restrictions are type-specific. Exchange rejects Open on a mail-enabled security
        // group outright, and accepts ApprovalRequired there while never routing the request to an owner: a
        // setting that appears to work and does not. Closed is the only honest value for one, so both rows
        // are shown rather than offered.
        var restrictionBlocked = isSecurity
            ? "A mail-enabled security group can only be Closed. Exchange rejects Open, and accepts "
              + "ApprovalRequired without ever sending join requests to an owner."
            : null;

        // ReportToManagerEnabled and ReportToOriginatorEnabled are one decision wearing two flags. Microsoft
        // documents that setting both, or neither, leaves messages to the group without a return path, which
        // some mail servers reject outright. Presented as a single choice so neither state can be created
        // here; a group already in one of them is shown as it really is rather than quietly normalised.
        var toManager = S("ReportToManagerEnabled") == Yes;
        var toOriginator = S("ReportToOriginatorEnabled") == Yes;
        var reportsInvalid = toManager == toOriginator;
        var reportsValue = (toManager, toOriginator) switch
        {
            (false, true) => ReportsToSender,
            (true, false) => ReportsToOwner,
            (true, true) => "Both the sender and the group owner",
            _ => "Nobody",
        };

        return new List<CloudPropertySection>
        {
            Section("Identity",
                // Not editable: a group naming policy rewrites a display name on edit as well as on create,
                // and administrators are exempt from it, so the same save behaves differently for others.
                P("displayName", "Display name", S("DisplayName")),
                P("name", "Name", S("Name")),
                E("alias", "Alias", S("Alias"), blocked: AliasBlockedTip),
                P("groupType", "Group type", FriendlyRecipientType(typeDetails)),
                // A room list is a distribution list whose members are all room mailboxes; Outlook uses it to
                // offer a building when booking a meeting. Exchange has no room-list form of a security group.
                //
                // Offered in ONE direction only. -RoomList is a switch with no documented off value, and the
                // repair Microsoft documents for a room list that needs to become an ordinary group again is
                // an Active Directory attribute, which a cloud-only group does not have. A Yes-to-No choice
                // would therefore report success and change nothing, so the row is closed once it reads Yes.
                E("roomList", "Room list", isRoomList ? Yes : No,
                    blocked: isSecurity ? "Only a distribution list can be marked as a room list."
                        : isRoomList ? "Exchange Online has no supported way to turn a room list back into an "
                            + "ordinary distribution list, so this is not offered here once it is set."
                        : null,
                    choices: YesNoChoices,
                    editableTip: "Setting this CANNOT BE UNDONE. Exchange Online has no supported way to turn a "
                        + "room list back into an ordinary distribution list, and the repair Microsoft documents "
                        + "is an Active Directory attribute that a cloud-only group does not have."),
                // Shown because it explains why changing the alias can move the primary address.
                P("emailAddressPolicy", "Email address policy",
                    S("EmailAddressPolicyEnabled") == Yes ? "Applied - the alias sets the primary address" : "Not applied"),
                P("id", "Object ID", S("ExternalDirectoryObjectId")),
                P("exchangeGuid", "Exchange GUID", S("Guid")),
                P("created", "Created", S("WhenCreated")),
                P("changed", "Last changed", S("WhenChanged")),
                // The one property that decides whether anything here can be edited at all.
                P("dirSynced", "Directory sync", synced
                    ? "Synced from on-premises — read-only in Exchange Online"
                    : "Cloud-only")),
            Section("Description",
                E("description", "Description", S("Description")),
                E("mailTip", "MailTip", S("MailTip"))),
            Section("Addresses",
                E("primaryAddress", "Primary", primary ?? string.Empty, blocked: PrimaryAddressTip),
                // Editable. Semicolon-separated, and written as an add/remove rather than as a replacement of
                // the whole collection, which is how a primary address gets moved by accident.
                E("secondaryAddresses", "Secondary", string.Join("; ", vanity),
                    editableTip: "Extra addresses that also deliver here, separated by semicolons. Adding or "
                        + "removing one never changes the address replies come from."),
                E("serviceAddresses", "Service (routing)", string.Join("; ", service), blocked: ServiceAddressTip),
                E("otherAddresses", "Other", string.Join("; ", other), blocked: AddressBlockedTip)),
            Section("Visibility",
                E("hiddenFromAddressLists", "Hidden from address lists", S("HiddenFromAddressLists"), choices: YesNoChoices),
                // Shown because it cannot be undone: an operator needs to know it is already on.
                P("hiddenGroupMembership", "Membership hidden (permanent)", S("HiddenGroupMembership"))),
            Section("Ownership and joining",
                E("managedBy", "Owners", S("ManagedBy"), recipients: true),
                E("joinRestriction", "Members can join", S("MemberJoinRestriction"),
                    blocked: restrictionBlocked, choices: JoinChoices),
                E("departRestriction", "Members can leave", S("MemberDepartRestriction"),
                    blocked: restrictionBlocked, choices: DepartChoices),
                E("grantSendOnBehalfTo", "Send on behalf", S("GrantSendOnBehalfTo"), recipients: true)),
            Section("Mail flow",
                // Exchange stores the double negative; this is how an operator thinks about it.
                E("externalSenders", "External senders",
                    S("RequireSenderAuthenticationEnabled") == Yes ? Blocked : Allowed, choices: SenderChoices),
                E("acceptFrom", "Accept only from", S("AcceptMessagesOnlyFromSendersOrMembers"), recipients: true),
                E("rejectFrom", "Reject from", S("RejectMessagesFromSendersOrMembers"), recipients: true),
                E("bccBlocked", "BCC blocked", S("BccBlocked"), choices: YesNoChoices),
                // Microsoft documents both size parameters as on-premises only. Exchange Online takes the
                // message-size limit from the organization, so an editor here would be one that never works.
                E("maxSendSize", "Max send size", S("MaxSendSize"), blocked: SizeBlockedTip),
                E("maxReceiveSize", "Max receive size", S("MaxReceiveSize"), blocked: SizeBlockedTip),
                E("deliveryReports", "Delivery reports go to", reportsValue,
                    blocked: reportsInvalid
                        ? "This group currently sends delivery reports to "
                          + (toManager ? "both the sender and its owner" : "nobody")
                          + ". Microsoft documents that as leaving messages to the group without a return path, "
                          + "which some mail servers reject. Correct it in the Exchange admin center and this "
                          + "becomes editable."
                        : null,
                    choices: ReportChoices),
                E("sendOof", "Send auto-replies to sender", S("SendOofMessageToOriginatorEnabled"), choices: YesNoChoices)),
            Section("Moderation",
                E("moderationEnabled", "Moderated", S("ModerationEnabled"), choices: YesNoChoices),
                E("moderatedBy", "Moderators", S("ModeratedBy"), recipients: true),
                E("moderationNotifications", "Notify senders", S("SendModerationNotifications"), choices: NotifyChoices),
                E("bypassModeration", "Bypass moderation", S("BypassModerationFromSendersOrMembers"), recipients: true)),
        };
    }

    // The vocabulary shared by the read projection above and the write translation below. They must agree
    // exactly: a drop-down offers these strings, and the translation is the only thing that understands them.
    private const string Yes = "Yes";
    private const string No = "No";
    private const string Allowed = "Allowed";
    private const string Blocked = "Blocked";
    private const string ReportsToSender = "Message sender";
    private const string ReportsToOwner = "Group owner";
    private static readonly string[] YesNoChoices = [Yes, No];
    private static readonly string[] SenderChoices = [Allowed, Blocked];
    private static readonly string[] ReportChoices = [ReportsToSender, ReportsToOwner];
    private static readonly string[] JoinChoices = ["Open", "Closed", "ApprovalRequired"];
    private static readonly string[] DepartChoices = ["Open", "Closed"];
    private static readonly string[] NotifyChoices = ["Always", "Internal", "Never"];

    private const string SyncedRowTip =
        "Synchronized from on-premises Active Directory. Exchange Online rejects every write against a synced "
        + "group, so this has to be changed in Active Directory.";
    private const string UnreadRowTip =
        "Exchange Online did not return a value this pane recognises for this setting, so it is not offered "
        + "for editing rather than overwriting something that was never read.";
    private const string AddressBlockedTip =
        "Read-only here. These are not SMTP addresses and exist so replies to old messages still resolve.";
    private const string PrimaryAddressTip =
        "Read-only here. Changing the address replies come from redirects the group's mail and stops the old "
        + "address working, so it is left to the Exchange admin center.";
    private const string ServiceAddressTip =
        "Read-only here. Microsoft 365 maintains these for routing; removing one breaks mail flow and the "
        + "service restores it anyway.";
    private const string AliasBlockedTip =
        "Read-only here. Wherever an email address policy applies — the usual case for a cloud group — changing "
        + "the alias also rewrites the primary address, which redirects mail and stops the old one working.";

    /// <summary>
    /// True for the addresses Microsoft 365 owns: the tenant routing addresses. They are shown but never
    /// offered for editing, which is what "lock the default secondary addresses" has to mean in practice.
    /// </summary>
    private static bool IsServiceAddress(string address) =>
        address.EndsWith(".onmicrosoft.com", StringComparison.OrdinalIgnoreCase);
    private const string SizeBlockedTip =
        "Exchange Online does not accept a per-group message size limit; Microsoft documents the parameter as "
        + "on-premises only. The limit comes from the organization.";

    /// <summary>The pane renders an absent value as an em dash; it must never be mistaken for a value.</summary>
    private static string Show(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    /// <summary>How one editable row is written back. The pane speaks in display values; Exchange does not.</summary>
    private enum DlValue { Text, YesNo, SenderPolicy, DeliveryReports, Addresses, Recipients }

    /// <param name="Parameter">The Set-DistributionGroup parameter this row writes.</param>
    /// <param name="Clearable">Whether an empty value is a legitimate instruction to clear the setting.</param>
    private sealed record DlSetting(string Parameter, DlValue Kind, bool Clearable = false, int MaxLength = 0);

    /// <summary>
    /// The only distribution group settings this app writes, keyed by the property-row key that
    /// <see cref="GetDistributionGroupDetailAsync"/> produces. A row that becomes editable without an entry
    /// here fails loudly on save rather than silently doing nothing, which is the failure that would otherwise
    /// look exactly like a successful save.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, DlSetting> DlEditable =
        new Dictionary<string, DlSetting>(StringComparer.OrdinalIgnoreCase)
        {
            ["description"] = new("Description", DlValue.Text, Clearable: true),
            ["secondaryAddresses"] = new("EmailAddresses", DlValue.Addresses, Clearable: true),
            // 175 displayed characters is the documented maximum.
            ["mailTip"] = new("MailTip", DlValue.Text, Clearable: true, MaxLength: 175),
            ["roomList"] = new("RoomList", DlValue.YesNo),
            ["hiddenFromAddressLists"] = new("HiddenFromAddressListsEnabled", DlValue.YesNo),
            ["joinRestriction"] = new("MemberJoinRestriction", DlValue.Text),
            ["departRestriction"] = new("MemberDepartRestriction", DlValue.Text),
            ["externalSenders"] = new("RequireSenderAuthenticationEnabled", DlValue.SenderPolicy),
            ["bccBlocked"] = new("BccBlocked", DlValue.YesNo),
            ["deliveryReports"] = new("ReportToOriginatorEnabled", DlValue.DeliveryReports),
            ["sendOof"] = new("SendOofMessageToOriginatorEnabled", DlValue.YesNo),
            ["moderationEnabled"] = new("ModerationEnabled", DlValue.YesNo),
            ["moderationNotifications"] = new("SendModerationNotifications", DlValue.Text),
            // The recipient lists. Each is replaced whole: Exchange offers no add/remove form for them, and
            // the picker always hands back the complete list it was seeded with.
            ["managedBy"] = new("ManagedBy", DlValue.Recipients, Clearable: true),
            ["grantSendOnBehalfTo"] = new("GrantSendOnBehalfTo", DlValue.Recipients, Clearable: true),
            ["acceptFrom"] = new("AcceptMessagesOnlyFromSendersOrMembers", DlValue.Recipients, Clearable: true),
            ["rejectFrom"] = new("RejectMessagesFromSendersOrMembers", DlValue.Recipients, Clearable: true),
            ["moderatedBy"] = new("ModeratedBy", DlValue.Recipients, Clearable: true),
            ["bypassModeration"] = new("BypassModerationFromSendersOrMembers", DlValue.Recipients, Clearable: true),
        };

    /// <summary>
    /// The Exchange property behind each recipient row. The same name reads and writes, so one map serves the
    /// on-demand resolution and the save, and they cannot name different fields.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> DlRecipientFields =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["managedBy"] = "ManagedBy",
            ["grantSendOnBehalfTo"] = "GrantSendOnBehalfTo",
            ["acceptFrom"] = "AcceptMessagesOnlyFromSendersOrMembers",
            ["rejectFrom"] = "RejectMessagesFromSendersOrMembers",
            ["moderatedBy"] = "ModeratedBy",
            ["bypassModeration"] = "BypassModerationFromSendersOrMembers",
        };

    /// <summary>
    /// Turns the pane's display values into Set-DistributionGroup parameters. Throws rather than guessing:
    /// every value here came from a drop-down or a text box this class defined, so anything unrecognised is a
    /// defect in this file, not something a user typed.
    /// </summary>
    /// <returns>
    /// The parameters to send, and the keys of any rows that turned out to need nothing sent. A row can be
    /// dirty and still produce no parameter: dirtiness is an exact string comparison, while Exchange matches
    /// addresses without regard to case or order. Those rows are named rather than dropped, because a change
    /// that vanishes silently is indistinguishable from one that was applied.
    /// </returns>
    private static (Dictionary<string, object?> Parameters, List<string> Unchanged) ToSetParameters(
        IReadOnlyList<CloudProperty> changes)
    {
        var p = new Dictionary<string, object?>(StringComparer.Ordinal);
        var unchanged = new List<string>();
        foreach (var row in changes)
        {
            var key = row.Key;
            if (!DlEditable.TryGetValue(key, out var setting))
                throw new ExchangeException($"'{key}' is not a distribution group setting this app can change.");

            // The pane renders an absent value as an em dash. It is a placeholder, never something to save.
            var value = (row.Value ?? string.Empty).Trim();
            if (value == "—") value = string.Empty;

            switch (setting.Kind)
            {
                case DlValue.Text:
                    if (value.Length == 0 && !setting.Clearable)
                        throw new ExchangeException($"{key} can't be emptied.");
                    if (setting.MaxLength > 0 && value.Length > setting.MaxLength)
                        throw new ExchangeException(
                            $"{key} is {value.Length} characters; Exchange allows at most {setting.MaxLength}.");
                    // null, not "", is how Exchange clears a value — an empty string sets an empty one.
                    p[setting.Parameter] = value.Length == 0 ? null : value;
                    break;

                case DlValue.YesNo:
                    p[setting.Parameter] = Choice(key, value, Yes, No);
                    break;

                case DlValue.SenderPolicy:
                    // Exchange stores the double negative: requiring authentication is what blocks outsiders.
                    p[setting.Parameter] = Choice(key, value, Blocked, Allowed);
                    break;

                case DlValue.DeliveryReports:
                    // One row, two parameters, always written together. Microsoft documents both-on and
                    // both-off as leaving messages to the group without a return path.
                    var toSender = Choice(key, value, ReportsToSender, ReportsToOwner);
                    p["ReportToOriginatorEnabled"] = toSender;
                    p["ReportToManagerEnabled"] = !toSender;
                    break;

                case DlValue.Recipients:
                    // A delta, never a replacement. These lists can hold entries this app could not resolve —
                    // a role group is not a mail recipient and Exchange will not take one back — and replacing
                    // the list would silently delete them. Sending only the difference leaves them alone.
                    var wanted = row.Identities;
                    var had = row.OriginalIdentities;
                    var addRecipients = wanted.Except(had, StringComparer.OrdinalIgnoreCase).ToArray();
                    var dropRecipients = had.Except(wanted, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (addRecipients.Length == 0 && dropRecipients.Length == 0) { unchanged.Add(key); break; }
                    var delta = new Dictionary<string, object?>(StringComparer.Ordinal);
                    if (addRecipients.Length > 0) delta["Add"] = addRecipients;
                    if (dropRecipients.Length > 0) delta["Remove"] = dropRecipients;
                    p[setting.Parameter] = delta;
                    break;

                case DlValue.Addresses:
                    // Add and remove, never a replacement. Handing Exchange a whole collection is how a primary
                    // address gets moved by accident: with no uppercase SMTP: entry it promotes the first one.
                    var before = SplitList(row.OriginalValue);
                    var after = SplitList(value);
                    var add = after.Except(before, StringComparer.OrdinalIgnoreCase).ToList();
                    var drop = before.Except(after, StringComparer.OrdinalIgnoreCase).ToList();
                    foreach (var a in add) ValidateAddress(a);
                    // Reordered, re-cased or re-spaced: the row looks edited and describes the same set.
                    if (add.Count == 0 && drop.Count == 0) { unchanged.Add(key); break; }
                    var ops = new Dictionary<string, object?>(StringComparer.Ordinal);
                    // Lowercase smtp: keeps every one of them a secondary address.
                    if (add.Count > 0) ops["Add"] = add.Select(a => "smtp:" + a).ToArray();
                    if (drop.Count > 0) ops["Remove"] = drop.Select(a => "smtp:" + a).ToArray();
                    p[setting.Parameter] = ops;
                    break;
            }
        }
        return (p, unchanged);

        static bool Choice(string key, string value, string whenTrue, string whenFalse) =>
            value == whenTrue ? true
            : value == whenFalse ? false
            : throw new ExchangeException($"'{value}' isn't a value {key} accepts.");

        static List<string> SplitList(string value) =>
            value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Where(s => s != "—").ToList();

        static void ValidateAddress(string address)
        {
            // A type prefix is exactly how the reply address gets moved — "SMTP:" on any entry promotes it —
            // so an operator must not be able to type one into this box at all.
            if (address.Contains(':'))
                throw new ExchangeException($"'{address}' must be a plain address, with no prefix before it.");
            var at = address.IndexOf('@');
            if (at <= 0 || at != address.LastIndexOf('@') || at == address.Length - 1 || address.Any(char.IsWhiteSpace))
                throw new ExchangeException($"'{address}' is not a valid email address.");
        }
    }

    public async Task<IReadOnlyList<string>> SetDistributionGroupPropertiesAsync(
        string identity, IReadOnlyList<CloudProperty> changes, CancellationToken cancellationToken = default)
    {
        // A Get- cmdlet given a blank identity returns EVERY group, and the write op reads before it writes.
        if (string.IsNullOrWhiteSpace(identity)) throw new ArgumentException("A group is required.", nameof(identity));
        if (changes is null || changes.Count == 0) throw new ArgumentException("Nothing to save.", nameof(changes));

        var (parameters, unchanged) = ToSetParameters(changes);
        // Every edit turned out to be a no-op. Sending an empty change set would only earn an error from the
        // host, and there is nothing for Exchange to do.
        if (parameters.Count == 0) return unchanged;
        await RunOpAsync(new { op = "set-dl-properties", identity, changes = parameters }, cancellationToken)
            .ConfigureAwait(false);
        return unchanged;
    }

    // The tooltip names the object type: a mailbox and a distribution group send the operator to different
    // places, so a group row explaining where mailbox settings are changed is the wrong explanation.
    private const string MailboxRowTip =
        "Read-only here. Mailbox settings are changed through their own actions, not by editing this list.";
    private const string GroupRowTip =
        "Read-only here. Distribution group settings are changed in the Exchange admin center, not by editing this list.";

    /// <summary>Every Exchange-sourced row is read-only: these views report state, and the write paths live on
    /// their own confirmed actions rather than in a property grid.</summary>
    private static CloudProperty Prop(string key, string label, string value, string tooltip) =>
        new(key, label, string.IsNullOrWhiteSpace(value) ? "—" : value, CloudPropertyEditability.SystemReadOnly, tooltip,
            help: CloudPropertyHelp.For(key));

    private static CloudPropertySection Section(string title, params CloudProperty[] properties) =>
        new(title, properties);

    public async Task<IReadOnlyList<MailboxRecipient>> GetDistributionGroupMembersAsync(string groupIdentity, CancellationToken cancellationToken = default)
    {
        // Validate before calling: a Get- cmdlet given a non-existent identity returns EVERY object, so an
        // empty group identity here would come back as "the whole tenant is in this group".
        if (string.IsNullOrWhiteSpace(groupIdentity)) throw new ArgumentException("A group is required.", nameof(groupIdentity));

        var data = await RunOpAsync(new { op = "list-dl-members", group = groupIdentity }, cancellationToken, ListTimeout).ConfigureAwait(false);
        var list = new List<MailboxRecipient>();
        if (data is { ValueKind: JsonValueKind.Array } arr)
            foreach (var e in arr.EnumerateArray()) list.Add(MapRecipient(e));
        else if (data is { ValueKind: JsonValueKind.Object } one) // ConvertTo-Json renders a single result as an object
            list.Add(MapRecipient(one));
        return list;
    }

    public Task RemoveDistributionGroupMemberAsync(string groupIdentity, string memberIdentity, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(groupIdentity)) throw new ArgumentException("A group is required.", nameof(groupIdentity));
        if (string.IsNullOrWhiteSpace(memberIdentity)) throw new ArgumentException("A member is required.", nameof(memberIdentity));
        return RunOpAsync(new { op = "remove-dl-member", group = groupIdentity, member = memberIdentity }, cancellationToken);
    }

    public Task AddDistributionGroupMemberAsync(string groupIdentity, string memberIdentity, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(groupIdentity)) throw new ArgumentException("A group is required.", nameof(groupIdentity));
        if (string.IsNullOrWhiteSpace(memberIdentity)) throw new ArgumentException("A member is required.", nameof(memberIdentity));
        return RunOpAsync(new { op = "add-dl-member", group = groupIdentity, member = memberIdentity }, cancellationToken);
    }

    // --- session plumbing (all callers hold _gate) ---

    /// <summary>Runs one operation against the reused session, reconnecting once if the session lapsed. Throws
    /// <see cref="ExchangeException"/> on failure; returns the response's <c>data</c> element (may be null).</summary>
    private async Task<JsonElement?> RunOpAsync(object request, CancellationToken ct, TimeSpan? timeout = null)
    {
        var budget = timeout ?? OpTimeout;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedLockedAsync(ct).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(request);
            var resp = await SendAndReadLockedAsync("OP", json, budget, ct).ConfigureAwait(false);
            if (!resp.Ok && IsSessionExpired(resp.Error))
            {
                AppLog.Instance.Warn("Exchange Online session looks expired; restarting the host and reconnecting once.");
                KillLocked();
                await EnsureConnectedLockedAsync(ct).ConfigureAwait(false);
                resp = await SendAndReadLockedAsync("OP", json, budget, ct).ConfigureAwait(false);
            }
            if (!resp.Ok)
            {
                if (!string.IsNullOrWhiteSpace(resp.Detail))
                    AppLog.Instance.Error("Exchange Online operation failed. Full PowerShell error record:" + Environment.NewLine + resp.Detail);
                throw new ExchangeException(ExchangeErrors.Friendly(resp.Error));
            }
            if (!string.IsNullOrWhiteSpace(resp.Detail))
                AppLog.Instance.Info("Exchange op diagnostics: " + resp.Detail);
            return resp.Data;
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureConnectedLockedAsync(CancellationToken ct)
    {
        if (IsConnected) return;
        if (!IsConfigured) throw new ExchangeException("Exchange Online isn't configured — set the tenant/organization first.");

        StartProcessLocked();
        await ReadUntilReadyLockedAsync(ct).ConfigureAwait(false);

        string token;
        try
        {
            token = await _graph.GetAccessTokenAsync(ExoScopes, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            KillLocked();
            throw new ExchangeException(
                "Couldn't get an Exchange Online access token. Grant the app registration the Office 365 Exchange "
                + "Online permission with admin consent, then retry. (" + ExchangeErrors.Friendly(ex) + ")", ex);
        }

        LogTokenClaims(token); // diagnostic: which delegated scopes did the token actually get? (never logs the token)

        // Delegated token → connect with -UserPrincipalName (the signed-in admin); -Organization is the
        // app-only pattern and leaves a delegated connect malformed. Pass both; the host prefers the UPN.
        var resp = await SendAndReadLockedAsync("CONNECT",
                JsonSerializer.Serialize(new { token, org = _organization, upn = _graph.SignedInAccount }), ConnectTimeout, ct)
            .ConfigureAwait(false);
        if (!resp.Ok)
        {
            KillLocked();
            if (!string.IsNullOrWhiteSpace(resp.Detail))
                AppLog.Instance.Error("Exchange Online connect failed. Full PowerShell error record:" + Environment.NewLine + resp.Detail);
            throw new ExchangeException("Connect to Exchange Online failed: " + ExchangeErrors.Friendly(resp.Error));
        }
        _connected = true;
        AppLog.Instance.Info($"Connected to Exchange Online ({_organization}) via pwsh host.");
    }

    private void StartProcessLocked()
    {
        KillLocked();
        var scriptPath = WriteHostScript();

        var psi = new ProcessStartInfo
        {
            FileName = ResolvePwsh(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in new[] { "-NoProfile", "-NoLogo", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath })
            psi.ArgumentList.Add(a);

        var p = new Process { StartInfo = psi };
        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            try { p.Dispose(); } catch { /* ignore */ }
            throw new ExchangeException(
                "Couldn't start PowerShell 7 (pwsh). Exchange Online features require PowerShell 7 and the "
                + "ExchangeOnlineManagement module installed on this machine (see the README ▸ Exchange Online "
                + "prerequisites). (" + ex.Message + ")", ex);
        }
        // stdin encoding can't be set via ProcessStartInfo reliably on all hosts; wrap it as UTF-8 (no BOM).
        // (Base64 payloads are ASCII anyway, so this only matters for safety.)
        _stderr.Clear();
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_stderr) { if (_stderr.Length < 8192) _stderr.AppendLine(e.Data); } };
        p.BeginErrorReadLine();
        _pwsh = p;
    }

    private void KillLocked()
    {
        _connected = false;
        var p = _pwsh;
        _pwsh = null;
        if (p is null) return;
        try
        {
            if (!p.HasExited)
            {
                try { p.StandardInput.WriteLine("QUIT"); p.StandardInput.Flush(); } catch { /* ignore */ }
                if (!p.WaitForExit(1500)) p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) { AppLog.Instance.Warn("Stopping the Exchange host process failed: " + ex.Message); }
        finally { try { p.Dispose(); } catch { /* ignore */ } }
    }

    private async Task ReadUntilReadyLockedAsync(CancellationToken ct)
    {
        while (true)
        {
            var line = await ReadLineLockedAsync(StartupTimeout, ct).ConfigureAwait(false);
            if (line is null)
                throw new ExchangeException("The Exchange Online host (pwsh) exited during startup. " + DrainStderr());
            if (line == Ready) return;
            if (line == Begin) // an error envelope before ready (e.g. the module failed to import)
            {
                var payload = await ReadLineLockedAsync(StartupTimeout, ct).ConfigureAwait(false);
                await ReadLineLockedAsync(StartupTimeout, ct).ConfigureAwait(false); // consume END
                var r = Parse(payload);
                throw new ExchangeException(ExchangeErrors.Friendly(r.Error ?? "The ExchangeOnlineManagement module could not be loaded."));
            }
            // ignore any other stray startup output
        }
    }

    private async Task<Resp> SendAndReadLockedAsync(string verb, string json, TimeSpan timeout, CancellationToken ct)
    {
        if (_pwsh is null) throw new ExchangeException("The Exchange Online host is not running.");
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        try
        {
            await _pwsh.StandardInput.WriteLineAsync((verb + " " + b64).AsMemory(), ct).ConfigureAwait(false);
            await _pwsh.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ExchangeException("The Exchange Online host stopped accepting input. " + DrainStderr(), ex);
        }

        // Read until the framed envelope; tolerate stray lines before BEGIN.
        while (true)
        {
            var line = await ReadLineLockedAsync(timeout, ct).ConfigureAwait(false);
            if (line is null) throw new ExchangeException("The Exchange Online host (pwsh) exited unexpectedly. " + DrainStderr());
            if (line != Begin) continue;
            var payload = await ReadLineLockedAsync(timeout, ct).ConfigureAwait(false);
            await ReadLineLockedAsync(timeout, ct).ConfigureAwait(false); // consume END
            return Parse(payload);
        }
    }

    /// <summary>Reads one stdout line with a timeout; kills the host and throws on timeout so a hung EXO call
    /// can't block the app forever.</summary>
    private async Task<string?> ReadLineLockedAsync(TimeSpan timeout, CancellationToken ct)
    {
        var reader = _pwsh?.StandardOutput ?? throw new ExchangeException("The Exchange Online host is not running.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await Task.Run(() => reader.ReadLine(), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Kill the host either way — an EXO cmdlet can't be interrupted mid-run, so abandon the process
            // (the next operation restarts it). This unblocks the orphaned blocking ReadLine too.
            KillLocked();
            if (ct.IsCancellationRequested) throw;                 // user cancellation → propagate
            throw new ExchangeException("The Exchange Online operation timed out.");
        }
    }

    private string DrainStderr()
    {
        lock (_stderr)
        {
            var s = _stderr.ToString().Trim();
            return s.Length == 0 ? string.Empty : "Details: " + s;
        }
    }

    // Heuristic: did the op fail because the EXO session/token lapsed (worth one reconnect+retry)?
    private static bool IsSessionExpired(string? error)
    {
        if (string.IsNullOrEmpty(error)) return false;
        var m = error;
        return m.Contains("session", StringComparison.OrdinalIgnoreCase)
            || m.Contains("expired", StringComparison.OrdinalIgnoreCase)
            || m.Contains("reconnect", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Connect-ExchangeOnline", StringComparison.OrdinalIgnoreCase)
            || m.Contains("not recognized", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct Resp(bool Ok, string? Error, JsonElement? Data, string? Detail);

    private static Resp Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return new Resp(false, "The Exchange Online host returned no response.", null, null);
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            string? error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            string? detail = root.TryGetProperty("detail", out var dt) && dt.ValueKind == JsonValueKind.String ? dt.GetString() : null;
            JsonElement? data = root.TryGetProperty("data", out var d) && d.ValueKind != JsonValueKind.Null ? d.Clone() : null;
            return new Resp(ok, error, data, detail);
        }
        catch (Exception ex)
        {
            return new Resp(false, "Unparseable response from the Exchange Online host: " + ex.Message, null, null);
        }
    }

    private static MailboxInfo MapMailbox(JsonElement d)
    {
        string S(string p) => d.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        bool B(string p) => d.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;

        var rtd = S("RecipientTypeDetails");
        var type = rtd.Equals("SharedMailbox", StringComparison.OrdinalIgnoreCase) ? MailboxType.Shared
                 : rtd.Equals("UserMailbox", StringComparison.OrdinalIgnoreCase) ? MailboxType.Regular
                 : MailboxType.Unknown;
        var upn = S("UserPrincipalName");
        var fwd = S("ForwardingAddress");

        return new MailboxInfo
        {
            Identity = upn.Length > 0 ? upn : S("PrimarySmtpAddress"),
            DisplayName = S("DisplayName"),
            PrimarySmtpAddress = S("PrimarySmtpAddress"),
            Type = type,
            ForwardingAddress = fwd.Length > 0 ? fwd : null,
            DeliverToMailboxAndForward = B("DeliverToMailboxAndForward"),
        };
    }

    private static CloudObjectRow MapMailboxRow(JsonElement d)
    {
        string S(string p) => d.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        bool B(string p) => d.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;

        var smtp = S("PrimarySmtpAddress");
        var oid = S("ExternalDirectoryObjectId");
        var row = new CloudObjectRow
        {
            // Prefer the Entra object id so a row can be correlated with the Entra list; fall back to the SMTP
            // address, which is what Exchange itself addresses the mailbox by.
            Id = oid.Length > 0 ? oid : smtp,
            DisplayName = S("DisplayName") is { Length: > 0 } n ? n : smtp,
            Kind = CloudObjectKind.Mailbox,
            Source = CloudObjectSource.Exchange,
        };
        row.Values["primarySmtpAddress"] = smtp;
        row.Values["mailboxType"] = FriendlyRecipientType(S("RecipientTypeDetails"));
        row.Values["userPrincipalName"] = S("UserPrincipalName");
        row.Values["alias"] = S("Alias");
        row.Values["dirSynced"] = B("IsDirSynced") ? "Synced" : "Cloud-only";
        row.Values["hiddenFromAddressLists"] = B("HiddenFromAddressListsEnabled") ? "Yes" : "No";
        row.Values["created"] = S("WhenMailboxCreated");
        row.Values["id"] = oid;
        return row;
    }

    private static CloudObjectRow MapDistributionGroupRow(JsonElement d)
    {
        string S(string p) => d.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        bool B(string p) => d.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;

        var smtp = S("PrimarySmtpAddress");
        var oid = S("ExternalDirectoryObjectId");
        var row = new CloudObjectRow
        {
            Id = oid.Length > 0 ? oid : smtp,
            DisplayName = S("DisplayName") is { Length: > 0 } n ? n : smtp,
            Kind = CloudObjectKind.Group,
            Source = CloudObjectSource.Exchange,
        };
        row.Values["primarySmtpAddress"] = smtp;
        row.Values["groupType"] = FriendlyRecipientType(S("RecipientTypeDetails"));
        row.Values["dirSynced"] = B("IsDirSynced") ? "Synced" : "Cloud-only";
        row.Values["managedBy"] = S("ManagedBy");
        // RequireSenderAuthenticationEnabled means "authenticated senders only", i.e. external mail is rejected.
        // Phrase the column the way an operator thinks about it rather than mirroring the double negative.
        row.Values["externalSenders"] = B("RequireSenderAuthenticationEnabled") ? "Blocked" : "Allowed";
        row.Values["joinRestriction"] = S("MemberJoinRestriction");
        row.Values["alias"] = S("Alias");
        row.Values["hiddenFromAddressLists"] = B("HiddenFromAddressListsEnabled") ? "Yes" : "No";
        row.Values["created"] = S("WhenCreated");
        row.Values["id"] = oid;
        return row;
    }

    /// <summary>Exchange's <c>RecipientTypeDetails</c> in the wording the rest of the app uses. Unknown values
    /// pass through unchanged rather than being flattened to "Other" — a new Exchange type should still be legible.</summary>
    private static string FriendlyRecipientType(string value) => value switch
    {
        "UserMailbox" => "User",
        "SharedMailbox" => "Shared",
        "RoomMailbox" => "Room",
        "EquipmentMailbox" => "Equipment",
        "SchedulingMailbox" => "Scheduling",
        "DiscoveryMailbox" => "Discovery",
        "TeamMailbox" => "Team",
        "LinkedMailbox" => "Linked",
        "GroupMailbox" => "Microsoft 365 group",
        "MailUniversalDistributionGroup" => "Distribution",
        "MailUniversalSecurityGroup" => "Mail-enabled security",
        "MailNonUniversalGroup" => "Mail-enabled (non-universal)",
        "RoomList" => "Room list",
        _ => value,
    };

    private static MailboxRecipient MapRecipient(JsonElement d)
    {
        string S(string p) => d.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        var smtp = S("PrimarySmtpAddress");
        return new MailboxRecipient
        {
            Identity = smtp.Length > 0 ? smtp : S("Identity"),
            DisplayName = S("DisplayName"),
            PrimarySmtpAddress = smtp,
            RecipientType = S("RecipientType"),
        };
    }

    private static MailboxDelegate MapDelegate(JsonElement d)
    {
        string S(string p) => d.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        bool B(string p) => d.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;
        return new MailboxDelegate
        {
            Identity = S("Identity"),
            DisplayName = S("DisplayName"),
            FullAccess = B("FullAccess"),
            SendAs = B("SendAs"),
            SendOnBehalf = B("SendOnBehalf"),
        };
    }

    /// <summary>Logs the audience + delegated scopes (scp) + app roles from the EXO access token, to confirm
    /// which Exchange permission the token actually carries. Never logs the token itself.</summary>
    private static void LogTokenClaims(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4) { case 2: payload += "=="; break; case 3: payload += "="; break; }
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            var root = doc.RootElement;
            string Claim(string n) => root.TryGetProperty(n, out var v) ? v.ToString() : "(none)";
            AppLog.Instance.Info($"EXO token claims — aud={Claim("aud")}; scp={Claim("scp")}; roles={Claim("roles")}; appid={Claim("appid")}");
        }
        catch (Exception ex) { AppLog.Instance.Warn("Could not decode EXO token claims: " + ex.Message); }
    }

    private static string ResolvePwsh()
    {
        var known = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        return File.Exists(known) ? known : "pwsh.exe"; // else rely on PATH
    }

    private static string WriteHostScript()
    {
        var dir = Path.Combine(Path.GetTempPath(), "UnifiedDirectoryManager");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "exo-host.ps1");
        File.WriteAllText(path, HostScript, new UTF8Encoding(false));
        return path;
    }

    public void Dispose()
    {
        try { Disconnect(); } catch { /* best effort */ }
        _gate.Dispose();
    }

    // The pwsh host loop. Started via -File; reads command lines from stdin and replies with a framed JSON
    // envelope on stdout. Only __emit writes to stdout, so the framing stays clean (cmdlet output is captured
    // into variables; warnings/info are suppressed or go to stderr).
    private const string HostScript = """
        $ErrorActionPreference = 'Stop'
        try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch {}

        function __emit($obj) {
            [Console]::Out.WriteLine('<<<UDM-BEGIN>>>')
            [Console]::Out.WriteLine(($obj | ConvertTo-Json -Depth 8 -Compress))
            [Console]::Out.WriteLine('<<<UDM-END>>>')
            [Console]::Out.Flush()
        }
        function __arg($b64) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b64)) | ConvertFrom-Json }

        # Exchange returns almost everything as a type that does not survive ConvertTo-Json intact, so values
        # are flattened to display strings here. The grouping into sections is done in C# — this literal gets
        # no compiler and no IntelliSense, so it carries as little logic as possible.
        function __yn($v) { if ([string]$v -eq 'True') { 'Yes' } else { 'No' } }
        function __dt($v) { if ($v) { ([datetime]$v).ToString('yyyy-MM-dd HH:mm') } else { '' } }
        # ADObjectId (ForwardingAddress, RetentionPolicy) stringifies to a canonical name, not an address.
        # Same rule as the owner projection: a dotted first segment marks a canonical name; take its last part.
        function __leaf($v) {
            $s = [string]$v
            if ($s -match '^[^/]+\.[^/]+/') { ($s -split '/')[-1] } else { $s }
        }

        # True when an object Exchange returned is the one that was asked for. Needed because a non-existent
        # -Identity makes a Get- cmdlet return EVERY object rather than erroring, so a bare "take the first"
        # would silently substitute an unrelated recipient.
        function __isWanted($obj, $want) {
            if ([string]::IsNullOrWhiteSpace($want)) { return $false }
            $w = $want.Trim('{', '}')
            $ids = @(
                [string]$obj.UserPrincipalName
                [string]$obj.PrimarySmtpAddress
                [string]$obj.ExternalDirectoryObjectId
                [string]$obj.Guid
                [string]$obj.Alias
                [string]$obj.Identity
                [string]$obj.Name
            )
            foreach ($id in $ids) { if ($id -and $id.Trim('{', '}') -eq $w) { return $true } }
            return $false
        }

        # Group-owner projection. ManagedBy entries arrive in three shapes and each needs different handling:
        #   * a canonical name ("contoso.onmicrosoft.com/Users/Jane Doe") — take the last segment
        #   * a plain display name, including role groups such as "Organization Management" — use as-is
        #   * a bare GUID, when Exchange could not render a display name for that owner (typically an account
        #     that has been deleted) — resolve it, and say so plainly if it can't be resolved
        # Resolution is cached and hard-capped: it costs one round trip per distinct unresolved owner on a
        # channel that serialises every call, and one slow list must not exhaust the operation timeout.
        $script:__ownerCache = @{}
        $script:__ownerBudget = 0
        $script:__ownerSkipped = 0
        $script:__ownerErrors = @()
        $script:__recipCache = @{}
        function __ownerNames($values) {
            $out = @()
            foreach ($v in @($values)) {
                $s = [string]$v
                if ([string]::IsNullOrWhiteSpace($s)) { continue }
                # Reduce a canonical name to its last segment, then FALL THROUGH to the GUID test rather than
                # returning here: Exchange Online replaces the Name of a synced recipient with its directory
                # object id, so a canonical name routinely ends in a GUID and still needs resolving.
                # The dotted first segment is what distinguishes a canonical name from a display name that
                # merely contains a slash ("Sales/Marketing Owners"), which must be left intact.
                if ($s -match '^[^/]+\.[^/]+/') { $s = ($s -split '/')[-1] }
                if ($s -notmatch '^\{?[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\}?$') { $out += $s; continue }
                # Normalise to the bare GUID: the cache key and the lookup should not differ over braces.
                $key = $s.Trim('{', '}')
                if ($script:__ownerCache.ContainsKey($key)) { $out += $script:__ownerCache[$key]; continue }
                # Budget spent: show the raw value rather than caching a guess, and count it so the caller
                # can report that the column was degraded instead of leaving it to look like real data.
                if ($script:__ownerBudget -le 0) { $script:__ownerSkipped++; $out += $s; continue }
                $script:__ownerBudget--
                $name = "Unresolved owner ($key)"
                try {
                    $rc = Get-Recipient -Identity $key -ErrorAction Stop | Select-Object -First 1
                    # Documented trap on every Exchange Get- cmdlet: a NON-EXISTENT -Identity returns EVERY
                    # object instead of erroring. A deleted owner's GUID is exactly that, so a blind -First 1
                    # would label the group with an unrelated recipient's name. Only accept a result that
                    # actually carries the identifier we asked for.
                    if ($rc -and (@([string]$rc.ExternalDirectoryObjectId, [string]$rc.Guid, [string]$rc.ExchangeObjectId) -contains $key)) {
                        $name = [string]$rc.DisplayName
                    }
                } catch {
                    # "Not found" is the answer, not a failure. Anything else (throttling, permissions) is a
                    # lookup that FAILED, which must not be reported as a deleted owner or cached as settled.
                    if ($_.Exception.Message -notmatch "couldn't be found|not found|does not exist|wasn't found") {
                        $name = "Owner lookup failed ($key)"
                        $script:__ownerErrors += $_.Exception.Message
                        $out += $name
                        continue
                    }
                }
                $script:__ownerCache[$key] = $name
                $out += $name
            }
            return $out
        }
        # Every op that projects recipients starts from a clean slate. The host process is long-lived and these
        # counters are script-scoped, so a skip left behind by a previous op would be reported as this one's
        # degradation. Resetting through one function is what stops an op from forgetting one of them.
        function __ownerReset($budget) {
            $script:__ownerCache = @{}
            $script:__recipCache = @{}
            $script:__ownerBudget = $budget
            $script:__ownerSkipped = 0
            $script:__ownerErrors = @()
        }
        # Prefer the server-resolved *WithDisplayNames variant of a recipient property. Without the matching
        # -Include*WithDisplayNames switch Exchange Online returns these fields as bare GUIDs, and resolving one
        # here costs a round trip against the budget. The variant is empty when the switch was not passed or
        # could not bind, so fall back to the raw property rather than showing nothing.
        function __recipNames($withNames, $raw) {
            $v = @()
            if ($withNames) { $v = @($withNames) }
            elseif ($raw) { $v = @($raw) }
            return (__ownerNames $v)
        }
        # Report degradation of the recipient projection. RunOpAsync logs 'detail' at Info, so a partially
        # resolved field leaves a trace instead of looking like fact.
        function __ownerDiag {
            $d = @()
            if ($script:__ownerSkipped -gt 0) { $d += "recipient lookups skipped (budget): $($script:__ownerSkipped)" }
            if ($script:__ownerErrors.Count -gt 0) { $d += "recipient lookup errors: $(($script:__ownerErrors | Select-Object -Unique) -join ' | ')" }
            return ($d -join '; ')
        }
        # Resolves recipient entries to a name AND an address. __ownerNames answers "what do I show"; this
        # answers "what can I write back", which needs an identity a cmdlet will accept. An entry that cannot
        # be resolved comes back with an empty Smtp, and the caller refuses to edit the list rather than
        # dropping the entry it could not name.
        function __recipList($values) {
            $out = @()
            foreach ($v in @($values)) {
                $s = [string]$v
                if ([string]::IsNullOrWhiteSpace($s)) { continue }
                # Same canonical-name reduction as __ownerNames, and for the same reason: Exchange Online
                # replaces the Name of a synced recipient with its object id, so the leaf is often a GUID.
                if ($s -match '^[^/]+\.[^/]+/') { $s = ($s -split '/')[-1] }
                $key = $s.Trim('{', '}')
                if ($script:__recipCache.ContainsKey($key)) { $out += $script:__recipCache[$key]; continue }
                if ($script:__ownerBudget -le 0) {
                    $script:__ownerSkipped++
                    # 'skipped' is NOT 'does not exist'. The caller has to tell an operator which happened,
                    # and an empty address alone cannot carry that difference.
                    $out += @{ Name = $s; Smtp = ''; Type = ''; Reason = 'skipped' }
                    continue
                }
                $script:__ownerBudget--
                $entry = @{ Name = $s; Smtp = ''; Type = ''; Reason = 'notfound' }
                try {
                    $rc = Get-Recipient -Identity $key -ErrorAction Stop | Select-Object -First 1
                    # The returns-everything trap: only accept a result that carries the identifier asked for.
                    if ($rc -and (__isWanted $rc $key)) {
                        # A mail contact or mail user can have no primary SMTP; list-dl-members already relies
                        # on that and falls back to Name, which Set-DistributionGroup accepts just as well.
                        $addr = [string]$rc.PrimarySmtpAddress
                        if ([string]::IsNullOrWhiteSpace($addr)) {
                            $addr = ([string]$rc.ExternalEmailAddress) -replace '^SMTP:', ''
                        }
                        if ([string]::IsNullOrWhiteSpace($addr)) { $addr = [string]$rc.Name }
                        $entry = @{
                            Name = [string]$rc.DisplayName
                            Smtp = $addr
                            Type = [string]$rc.RecipientTypeDetails
                            Reason = $(if ([string]::IsNullOrWhiteSpace($addr)) { 'noaddress' } else { '' })
                        }
                    }
                } catch {
                    if ($_.Exception.Message -notmatch "couldn't be found|not found|does not exist|wasn't found") {
                        $script:__ownerErrors += $_.Exception.Message
                        $out += @{ Name = "Lookup failed ($key)"; Smtp = ''; Type = ''; Reason = 'error' }
                        continue
                    }
                }
                $script:__recipCache[$key] = $entry
                $out += $entry
            }
            return $out
        }
        # --- end recipient projection ----------------------------------------------------------------------
        function __delAdd($map, $who, $perm) {
            if ([string]::IsNullOrWhiteSpace([string]$who)) { return }
            $rec = Get-Recipient -Identity $who -ErrorAction SilentlyContinue | Select-Object -First 1
            $key = if ($rec) { [string]$rec.PrimarySmtpAddress } else { [string]$who }
            $name = if ($rec) { [string]$rec.DisplayName } else { [string]$who }
            if (-not $map.ContainsKey($key)) { $map[$key] = [ordered]@{ Identity = $key; DisplayName = $name; FullAccess = $false; SendAs = $false; SendOnBehalf = $false } }
            $map[$key][$perm] = $true
        }

        # Builds the New-DistributionGroup parameter splat. Factored out of the op so it can be unit-tested
        # without a tenant (build/test-hostscript.ps1): everything decided here is create-time only, so a
        # mistake is not correctable afterwards, and this whole file is an unchecked string literal.
        function __newDgParams($r) {
            $np = @{
                Name = [string]$r.name
                Type = [string]$r.type
                ErrorAction = 'Stop'
            }
            if (-not [string]::IsNullOrWhiteSpace([string]$r.alias)) { $np['Alias'] = [string]$r.alias }
            if (-not [string]::IsNullOrWhiteSpace([string]$r.description)) { $np['Description'] = [string]$r.description }
            $own = @($r.owners | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
            if ($own.Count -gt 0) { $np['ManagedBy'] = $own }
            $mem = @($r.members | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
            if ($mem.Count -gt 0) { $np['Members'] = $mem }
            # RequireSenderAuthenticationEnabled defaults to $true, i.e. a new group silently rejects ALL
            # external mail, so it is always passed explicitly rather than left to that default. Note the
            # value is the inverse of the operator-facing option: requireAuth = !AllowExternalSenders.
            $np['RequireSenderAuthenticationEnabled'] = [bool]$r.requireAuth
            if ([bool]$r.hiddenMembership) { $np['HiddenGroupMembershipEnabled'] = $true }
            # A mail-enabled security group is a security principal, so self-service join and depart aren't
            # meaningful for it and Open is rejected. Pin both to Closed rather than depending on a service
            # default that isn't documented per type ('Default value: None' means the cmdlet sends nothing).
            if ([string]$r.type -eq 'Security') {
                $np['MemberJoinRestriction'] = 'Closed'
                $np['MemberDepartRestriction'] = 'Closed'
            }
            return $np
        }

        try {
            Import-Module ExchangeOnlineManagement -ErrorAction Stop
        } catch {
            __emit @{ ok = $false; error = ("The ExchangeOnlineManagement module could not be loaded. Install it, e.g. Install-Module ExchangeOnlineManagement -Scope CurrentUser. Details: " + $_.Exception.Message) }
            exit 1
        }
        [Console]::Out.WriteLine('<<<UDM-READY>>>'); [Console]::Out.Flush()

        while ($true) {
            $line = [Console]::In.ReadLine()
            if ($null -eq $line) { break }
            $sp = $line.IndexOf(' ')
            $verb = if ($sp -lt 0) { $line } else { $line.Substring(0, $sp) }
            $payload = if ($sp -lt 0) { '' } else { $line.Substring($sp + 1) }
            switch ($verb) {
                'QUIT' { try { Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue | Out-Null } catch {}; break }
                'CONNECT' {
                    try {
                        $p = __arg $payload
                        if ($p.upn) {
                            Connect-ExchangeOnline -AccessToken $p.token -UserPrincipalName $p.upn -ShowBanner:$false -WarningAction SilentlyContinue -ErrorAction Stop 6>$null
                        } else {
                            Connect-ExchangeOnline -AccessToken $p.token -Organization $p.org -ShowBanner:$false -WarningAction SilentlyContinue -ErrorAction Stop 6>$null
                        }
                        __emit @{ ok = $true }
                    } catch { __emit @{ ok = $false; error = $_.Exception.Message; detail = ($_ | Out-String) } }
                }
                'OP' {
                    try {
                        $r = __arg $payload
                        switch ($r.op) {
                            'get-mailbox' {
                                $m = Get-Mailbox -Identity $r.identity -ErrorAction SilentlyContinue
                                if ($null -eq $m) { __emit @{ ok = $true; data = $null } }
                                else {
                                    __emit @{ ok = $true; data = @{
                                        DisplayName = [string]$m.DisplayName
                                        PrimarySmtpAddress = [string]$m.PrimarySmtpAddress
                                        RecipientTypeDetails = [string]$m.RecipientTypeDetails
                                        ForwardingAddress = [string]$m.ForwardingAddress
                                        DeliverToMailboxAndForward = ([string]$m.DeliverToMailboxAndForward -eq 'True')
                                        UserPrincipalName = [string]$m.UserPrincipalName
                                    } }
                                }
                            }
                            'convert' { Set-Mailbox -Identity $r.identity -Type $r.type -ErrorAction Stop 6>$null; __emit @{ ok = $true } }
                            'set-forwarding' { Set-Mailbox -Identity $r.identity -ForwardingAddress $r.target -DeliverToMailboxAndForward ([bool]$r.deliver) -ErrorAction Stop 6>$null; __emit @{ ok = $true } }
                            'clear-forwarding' { Set-Mailbox -Identity $r.identity -ForwardingAddress $null -DeliverToMailboxAndForward $false -ErrorAction Stop 6>$null; __emit @{ ok = $true } }
                            'search-recipients' {
                                if ([string]::IsNullOrWhiteSpace([string]$r.text)) {
                                    $rc = Get-Recipient -ResultSize 50 -ErrorAction SilentlyContinue
                                } else {
                                    $esc = ([string]$r.text).Replace("'", "''")
                                    $rc = Get-Recipient -ResultSize 50 -Filter "DisplayName -like '*$esc*' -or PrimarySmtpAddress -like '*$esc*'" -ErrorAction SilentlyContinue
                                }
                                $data = @($rc | ForEach-Object { @{
                                    Identity = [string]$_.PrimarySmtpAddress
                                    DisplayName = [string]$_.DisplayName
                                    PrimarySmtpAddress = [string]$_.PrimarySmtpAddress
                                    RecipientType = [string]$_.RecipientTypeDetails
                                } })
                                __emit @{ ok = $true; data = $data }
                            }
                            'list-delegates' {
                                # NOTE: EXO v3 returns IsInherited/Deny as STRINGS ("False"/"True"), and in PowerShell
                                # -not "False" is $false (any non-empty string is truthy). Compare stringified values
                                # with -ne 'True' (correct whether the property is a bool or a string). AccessRights is a
                                # single flags value (e.g. "FullAccess, ReadPermission"), so match it as a substring.
                                $map = @{}
                                $diag = @()
                                try {
                                    $raw = @(Get-MailboxPermission -Identity $r.identity -ErrorAction Stop)
                                    $fp = @($raw | Where-Object { (([string]$_.AccessRights) -like '*FullAccess*') -and ([string]$_.IsInherited -ne 'True') -and ([string]$_.Deny -ne 'True') -and ([string]$_.User -notlike 'NT AUTHORITY\*') })
                                    $diag += "FullAccess raw=$($raw.Count) matched=$($fp.Count)"
                                    $fp | ForEach-Object { __delAdd $map $_.User 'FullAccess' }
                                } catch { $diag += "MbxPerm ERR: $($_.Exception.Message)" }
                                try {
                                    $rawS = @(Get-RecipientPermission -Identity $r.identity -ErrorAction Stop)
                                    $sp = @($rawS | Where-Object { (([string]$_.AccessRights) -like '*SendAs*') -and ([string]$_.Trustee -notlike 'NT AUTHORITY\*') })
                                    $diag += "SendAs raw=$($rawS.Count) matched=$($sp.Count)"
                                    $sp | ForEach-Object { __delAdd $map $_.Trustee 'SendAs' }
                                } catch { $diag += "RcptPerm ERR: $($_.Exception.Message)" }
                                try {
                                    $mbx = Get-Mailbox -Identity $r.identity -ErrorAction Stop
                                    $sob = @($mbx.GrantSendOnBehalfTo)
                                    $diag += "SendOnBehalf=$($sob.Count)"
                                    foreach ($g in $sob) { __delAdd $map $g 'SendOnBehalf' }
                                } catch { $diag += "Mbx(SoB) ERR: $($_.Exception.Message)" }
                                __emit @{ ok = $true; data = @($map.Values | Sort-Object { $_.DisplayName }); detail = ($diag -join '; ') }
                            }
                            'add-delegate' {
                                # Each permission is applied independently and idempotently: 'already exists' is treated
                                # as success, and one sub-op failing doesn't skip the others (errors are aggregated).
                                $errs = @()
                                if ($r.fullAccess) { try { Add-MailboxPermission -Identity $r.identity -User $r.delegateId -AccessRights FullAccess -InheritanceType All -AutoMapping ([bool]$r.autoMapping) -Confirm:$false -ErrorAction Stop | Out-Null } catch { if ($_.Exception.Message -notmatch 'already') { $errs += 'Full Access: ' + $_.Exception.Message } } }
                                if ($r.sendAs) { try { Add-RecipientPermission -Identity $r.identity -Trustee $r.delegateId -AccessRights SendAs -Confirm:$false -ErrorAction Stop | Out-Null } catch { if ($_.Exception.Message -notmatch 'already') { $errs += 'Send As: ' + $_.Exception.Message } } }
                                if ($r.sendOnBehalf) { try { Set-Mailbox -Identity $r.identity -GrantSendOnBehalfTo @{ Add = $r.delegateId } -ErrorAction Stop 6>$null } catch { if ($_.Exception.Message -notmatch 'already') { $errs += 'Send on Behalf: ' + $_.Exception.Message } } }
                                if ($errs.Count -gt 0) { __emit @{ ok = $false; error = ($errs -join '; ') } } else { __emit @{ ok = $true } }
                            }
                            'remove-delegate' {
                                # Idempotent removal: 'not present' is success; sub-ops are independent + aggregated.
                                $errs = @()
                                if ($r.fullAccess) { try { Remove-MailboxPermission -Identity $r.identity -User $r.delegateId -AccessRights FullAccess -Confirm:$false -ErrorAction Stop | Out-Null } catch { if ($_.Exception.Message -notmatch 'not found|does not exist|doesn''t|isn''t|cannot be found|not have') { $errs += 'Full Access: ' + $_.Exception.Message } } }
                                if ($r.sendAs) { try { Remove-RecipientPermission -Identity $r.identity -Trustee $r.delegateId -AccessRights SendAs -Confirm:$false -ErrorAction Stop | Out-Null } catch { if ($_.Exception.Message -notmatch 'not found|does not exist|doesn''t|isn''t|cannot be found|not have') { $errs += 'Send As: ' + $_.Exception.Message } } }
                                if ($r.sendOnBehalf) { try { Set-Mailbox -Identity $r.identity -GrantSendOnBehalfTo @{ Remove = $r.delegateId } -ErrorAction Stop 6>$null } catch { if ($_.Exception.Message -notmatch 'not found|does not exist|doesn''t|isn''t|cannot be found|not have') { $errs += 'Send on Behalf: ' + $_.Exception.Message } } }
                                if ($errs.Count -gt 0) { __emit @{ ok = $false; error = ($errs -join '; ') } } else { __emit @{ ok = $true } }
                            }
                            'remove-dl-member' {
                                # Remove a member from a distribution list / mail-enabled security group. Microsoft Graph
                                # can't modify these, so this is the only path. -BypassSecurityGroupManagerCheck lets an
                                # Exchange admin who isn't the group's ManagedBy owner make the change. Idempotent: if the
                                # member isn't in the group, treat it as success (there is nothing left to remove).
                                try {
                                    Remove-DistributionGroupMember -Identity $r.group -Member $r.member -BypassSecurityGroupManagerCheck -Confirm:$false -ErrorAction Stop 6>$null
                                    __emit @{ ok = $true }
                                } catch {
                                    if ($_.Exception.Message -match "isn't a member|is not a member|not a member of the group") {
                                        __emit @{ ok = $true; detail = "member was not in the group" }
                                    } else {
                                        __emit @{ ok = $false; error = $_.Exception.Message; detail = ($_ | Out-String) }
                                    }
                                }
                            }
                            'add-dl-member' {
                                # Add a member to a distribution list / mail-enabled security group. Microsoft Graph
                                # can't modify these, so this is the only path. -BypassSecurityGroupManagerCheck lets an
                                # Exchange admin who isn't the group's ManagedBy owner make the change. Idempotent: if the
                                # member is already in the group, treat it as success.
                                try {
                                    Add-DistributionGroupMember -Identity $r.group -Member $r.member -BypassSecurityGroupManagerCheck -Confirm:$false -ErrorAction Stop 6>$null
                                    __emit @{ ok = $true }
                                } catch {
                                    if ($_.Exception.Message -match "already a member|is already a member of the group") {
                                        __emit @{ ok = $true; detail = "member already in the group" }
                                    } else {
                                        __emit @{ ok = $false; error = $_.Exception.Message; detail = ($_ | Out-String) }
                                    }
                                }
                            }
                            'list-mailboxes' {
                                # Exchange has no continuation token, so the caller caps with -ResultSize and asks for
                                # one row more than it wants in order to detect truncation. Get-EXOMailbox (not
                                # Get-Mailbox) because the classic cmdlet returns 200+ properties per object.
                                $take = [int]$r.max
                                $mp = @{ ResultSize = $take; ErrorAction = 'Stop' }
                                if (-not [string]::IsNullOrWhiteSpace([string]$r.search)) {
                                    # Double single quotes to close the filter, and strip wildcard characters:
                                    # only the leading/trailing * added here are supported, and a * typed by the
                                    # user would land mid-string, which these cmdlets reject.
                                    $esc = ((([string]$r.search).Replace("'", "''")) -replace '[\*\?]', '')
                                    $mp['Filter'] = "DisplayName -like '*$esc*' -or PrimarySmtpAddress -like '*$esc*' -or Alias -like '*$esc*'"
                                }
                                $mb = @(Get-EXOMailbox @mp -PropertySets Minimum,AddressList -Properties IsDirSynced,WhenMailboxCreated)
                                $data = @($mb | ForEach-Object {
                                    $created = ''
                                    if ($_.WhenMailboxCreated) { $created = ([datetime]$_.WhenMailboxCreated).ToString('yyyy-MM-dd') }
                                    @{
                                        DisplayName = [string]$_.DisplayName
                                        PrimarySmtpAddress = [string]$_.PrimarySmtpAddress
                                        UserPrincipalName = [string]$_.UserPrincipalName
                                        Alias = [string]$_.Alias
                                        RecipientTypeDetails = [string]$_.RecipientTypeDetails
                                        ExternalDirectoryObjectId = [string]$_.ExternalDirectoryObjectId
                                        # EXO V3 returns flags as the STRINGS 'True'/'False' (see the note on
                                        # list-delegates); compare stringified, never with -not.
                                        IsDirSynced = ([string]$_.IsDirSynced -eq 'True')
                                        HiddenFromAddressListsEnabled = ([string]$_.HiddenFromAddressListsEnabled -eq 'True')
                                        WhenMailboxCreated = $created
                                    }
                                })
                                __emit @{ ok = $true; data = $data }
                            }
                            'list-distribution-groups' {
                                # Distribution lists + mail-enabled security groups. Get-DistributionGroup does not
                                # return Microsoft 365 (unified) groups, which is correct here: those are Graph-managed.
                                $take = [int]$r.max
                                $gp = @{ ResultSize = $take; ErrorAction = 'Stop' }
                                if (-not [string]::IsNullOrWhiteSpace([string]$r.search)) {
                                    # Same escaping + wildcard-stripping rule as list-mailboxes.
                                    $esc = ((([string]$r.search).Replace("'", "''")) -replace '[\*\?]', '')
                                    $gp['Filter'] = "DisplayName -like '*$esc*' -or PrimarySmtpAddress -like '*$esc*' -or Alias -like '*$esc*'"
                                }
                                # ManagedBy comes back as raw GUIDs unless this switch is passed, which populates a
                                # parallel ManagedByWithDisplayNames. Fall back if the switch isn't bindable.
                                try { $gl = @(Get-DistributionGroup @gp -IncludeManagedByWithDisplayNames) }
                                catch [System.Management.Automation.ParameterBindingException] { $gl = @(Get-DistributionGroup @gp) }
                                # Fresh cache per load so a renamed owner isn't shown from a stale session entry.
                                __ownerReset 50
                                $data = @($gl | ForEach-Object {
                                    $created = ''
                                    if ($_.WhenCreated) { $created = ([datetime]$_.WhenCreated).ToString('yyyy-MM-dd') }
                                    # Prefer the display-name array, but fall back per GROUP only — a group whose
                                    # ManagedByWithDisplayNames is empty still has raw values in ManagedBy, and
                                    # __ownerNames resolves whatever shape each entry turns out to be.
                                    $owners = @()
                                    if ($_.ManagedByWithDisplayNames) { $owners = @($_.ManagedByWithDisplayNames) }
                                    elseif ($_.ManagedBy) { $owners = @($_.ManagedBy) }
                                    $ownerText = ((__ownerNames $owners) -join '; ')
                                    @{
                                        DisplayName = [string]$_.DisplayName
                                        PrimarySmtpAddress = [string]$_.PrimarySmtpAddress
                                        Alias = [string]$_.Alias
                                        RecipientTypeDetails = [string]$_.RecipientTypeDetails
                                        ExternalDirectoryObjectId = [string]$_.ExternalDirectoryObjectId
                                        ManagedBy = $ownerText
                                        MemberJoinRestriction = [string]$_.MemberJoinRestriction
                                        IsDirSynced = ([string]$_.IsDirSynced -eq 'True')
                                        RequireSenderAuthenticationEnabled = ([string]$_.RequireSenderAuthenticationEnabled -eq 'True')
                                        HiddenFromAddressListsEnabled = ([string]$_.HiddenFromAddressListsEnabled -eq 'True')
                                        WhenCreated = $created
                                    }
                                })
                                # Report any degradation of the owner column, so a partially-resolved column
                                # leaves a trace instead of looking like fact.
                                $odiag = __ownerDiag
                                if ($odiag) { __emit @{ ok = $true; data = $data; detail = $odiag } }
                                else { __emit @{ ok = $true; data = $data } }
                            }
                            'get-mailbox-detail' {
                                # -ErrorAction Stop, deliberately NOT SilentlyContinue like the older get-mailbox
                                # op: access-denied and not-found are different answers, and reporting a
                                # permissions problem as "no mailbox found" sends the operator the wrong way.
                                # Small budget: at most one forwarding target needs resolving on this path.
                                __ownerReset 2
                                $want = ([string]$r.identity).Trim()
                                $m = Get-EXOMailbox -Identity $r.identity `
                                        -PropertySets Minimum,AddressList,Delivery,Hold,Policy,Quota,Archive,StatisticsSeed `
                                        -Properties WhenMailboxCreated,IsDirSynced -ErrorAction Stop |
                                     Select-Object -First 1
                                if ($null -eq $m) { throw "No mailbox was returned for '$want'." }
                                # A NON-EXISTENT -Identity makes an Exchange Get- cmdlet return EVERY object
                                # instead of erroring, and -First 1 would then hand back a stranger's mailbox
                                # under the selected person's name. Only accept the one that was asked for.
                                if (-not (__isWanted $m $want)) { throw "Exchange did not return the mailbox '$want'." }

                                # Protocol flags live on a different cmdlet and a different permission, so a
                                # failure there must not lose the mailbox — it is reported alongside instead.
                                $cas = $null; $casError = ''
                                try {
                                    $cas = Get-EXOCasMailbox -Identity $r.identity -ErrorAction Stop | Select-Object -First 1
                                    if ($cas -and -not (__isWanted $cas $want)) { $cas = $null; $casError = 'Exchange returned a different mailbox for the protocol settings.' }
                                }
                                catch { $casError = $_.Exception.Message }

                                # ArchiveStatus alone lies: Microsoft documents it reading "None" for an active
                                # archive after a re-license or migration. ArchiveGuid is the reliable test.
                                $agid = [string]$m.ArchiveGuid
                                $hasArchive = ($agid -and $agid -ne '00000000-0000-0000-0000-000000000000')

                                __emit @{ ok = $true; data = @{
                                    DisplayName = [string]$m.DisplayName
                                    Alias = [string]$m.Alias
                                    UserPrincipalName = [string]$m.UserPrincipalName
                                    PrimarySmtpAddress = [string]$m.PrimarySmtpAddress
                                    RecipientTypeDetails = [string]$m.RecipientTypeDetails
                                    ExchangeGuid = [string]$m.ExchangeGuid
                                    WhenMailboxCreated = (__dt $m.WhenMailboxCreated)
                                    IsDirSynced = (__yn $m.IsDirSynced)
                                    HiddenFromAddressLists = (__yn $m.HiddenFromAddressListsEnabled)
                                    EmailAddresses = @($m.EmailAddresses | ForEach-Object { [string]$_ })
                                    # Through the owner projection, not __leaf: Exchange replaces the Name of a
                                    # synced recipient with its object id, so a canonical name routinely ends in
                                    # a GUID and __leaf alone would render the forwarding target as a bare id.
                                    ForwardingAddress = ((__ownerNames @($m.ForwardingAddress)) -join '; ')
                                    ForwardingSmtpAddress = (([string]$m.ForwardingSmtpAddress) -replace '^smtp:', '')
                                    DeliverToMailboxAndForward = (__yn $m.DeliverToMailboxAndForward)
                                    IssueWarningQuota = [string]$m.IssueWarningQuota
                                    ProhibitSendQuota = [string]$m.ProhibitSendQuota
                                    ProhibitSendReceiveQuota = [string]$m.ProhibitSendReceiveQuota
                                    UseDatabaseQuotaDefaults = (__yn $m.UseDatabaseQuotaDefaults)
                                    LitigationHoldEnabled = (__yn $m.LitigationHoldEnabled)
                                    LitigationHoldDate = (__dt $m.LitigationHoldDate)
                                    LitigationHoldOwner = [string]$m.LitigationHoldOwner
                                    LitigationHoldDuration = [string]$m.LitigationHoldDuration
                                    RetentionPolicy = (__leaf $m.RetentionPolicy)
                                    HasArchive = $(if ($hasArchive) { 'Yes' } else { 'No' })
                                    ArchiveStatus = [string]$m.ArchiveStatus
                                    ArchiveState = [string]$m.ArchiveState
                                    ArchiveGuid = $(if ($hasArchive) { $agid } else { '' })
                                    OwaEnabled = $(if ($cas) { __yn $cas.OWAEnabled } else { '' })
                                    ActiveSyncEnabled = $(if ($cas) { __yn $cas.ActiveSyncEnabled } else { '' })
                                    ImapEnabled = $(if ($cas) { __yn $cas.ImapEnabled } else { '' })
                                    PopEnabled = $(if ($cas) { __yn $cas.PopEnabled } else { '' })
                                    MapiEnabled = $(if ($cas) { __yn $cas.MAPIEnabled } else { '' })
                                    EwsEnabled = $(if ($cas) { __yn $cas.EwsEnabled } else { '' })
                                    ProtocolsError = $casError
                                }; detail = $(if ($casError) { "protocol settings unavailable: $casError" } else { '' }) }
                            }
                            'get-mailbox-usage' {
                                # Store-backed and one mailbox per call — the expensive read, which is why it is
                                # on demand rather than part of opening a mailbox.
                                $wantU = ([string]$r.identity).Trim()
                                $wantGuid = ([string]$r.exchangeGuid).Trim().Trim('{', '}')
                                # This cmdlet returns NONE of the usual identity fields — only DisplayName,
                                # MailboxGuid, counts and sizes — so __isWanted cannot be used here. Address it
                                # by -ExchangeGuid where we have one, which is exact, and verify the MailboxGuid
                                # that comes back. Falling back to -Identity leaves nothing to verify against.
                                $sp = @{ PropertySets = 'All'; ErrorAction = 'Stop' }
                                if ($wantGuid) { $sp['ExchangeGuid'] = [guid]$wantGuid } else { $sp['Identity'] = $wantU }
                                $s = Get-EXOMailboxStatistics @sp | Select-Object -First 1
                                if ($null -eq $s) { throw "No mailbox statistics were returned for '$wantU'." }
                                if ($wantGuid -and (([string]$s.MailboxGuid).Trim('{', '}') -ne $wantGuid)) {
                                    throw "Exchange returned statistics for a different mailbox than '$wantU'."
                                }
                                __emit @{ ok = $true; data = @{
                                    TotalItemSize = [string]$s.TotalItemSize
                                    ItemCount = [string]$s.ItemCount
                                    TotalDeletedItemSize = [string]$s.TotalDeletedItemSize
                                    DeletedItemCount = [string]$s.DeletedItemCount
                                    LastLogonTime = (__dt $s.LastLogonTime)
                                } }
                            }
                            'get-dl-detail' {
                                # Owner-style properties repeat the same people across a group, so one shared
                                # cache and one budget covers every multi-valued recipient field.
                                __ownerReset 25
                                $wantG = ([string]$r.identity).Trim()
                                # Have Exchange resolve the recipient fields server-side. Without these switches
                                # it returns them as bare GUIDs and each distinct one then costs a Get-Recipient
                                # round trip against the budget. There is deliberately no Reject* switch here:
                                # Exchange does not offer one, so that field alone is still resolved locally.
                                $dgn = @{
                                    IncludeManagedByWithDisplayNames = $true
                                    IncludeModeratedByWithDisplayNames = $true
                                    IncludeGrantSendOnBehalfToWithDisplayNames = $true
                                    IncludeAcceptMessagesOnlyFromSendersOrMembersWithDisplayNames = $true
                                    IncludeBypassModerationFromSendersOrMembersWithDisplayNames = $true
                                }
                                try { $g = Get-DistributionGroup -Identity $r.identity @dgn -ErrorAction Stop | Select-Object -First 1 }
                                catch [System.Management.Automation.ParameterBindingException] {
                                    $g = Get-DistributionGroup -Identity $r.identity -ErrorAction Stop | Select-Object -First 1
                                }
                                if ($null -eq $g) { throw "No group was returned for '$wantG'." }
                                # A non-existent -Identity makes an Exchange Get- cmdlet return EVERY object, and
                                # -First 1 would then describe an unrelated group under this one's name.
                                if (-not (__isWanted $g $wantG)) { throw "Exchange did not return the group '$wantG'." }

                                $created = ''
                                if ($g.WhenCreated) { $created = ([datetime]$g.WhenCreated).ToString('yyyy-MM-dd HH:mm') }
                                $changed = ''
                                if ($g.WhenChanged) { $changed = ([datetime]$g.WhenChanged).ToString('yyyy-MM-dd HH:mm') }

                                __emit @{ ok = $true; data = @{
                                    DisplayName = [string]$g.DisplayName
                                    Name = [string]$g.Name
                                    Alias = [string]$g.Alias
                                    PrimarySmtpAddress = [string]$g.PrimarySmtpAddress
                                    ExternalDirectoryObjectId = [string]$g.ExternalDirectoryObjectId
                                    Guid = [string]$g.Guid
                                    RecipientTypeDetails = [string]$g.RecipientTypeDetails
                                    IsDirSynced = (__yn $g.IsDirSynced)
                                    # Decides whether an alias change also rewrites the primary address.
                                    EmailAddressPolicyEnabled = (__yn $g.EmailAddressPolicyEnabled)
                                    WhenCreated = $created
                                    WhenChanged = $changed
                                    Description = (@($g.Description | ForEach-Object { [string]$_ }) -join ' ')
                                    MailTip = [string]$g.MailTip
                                    EmailAddresses = @($g.EmailAddresses | ForEach-Object { [string]$_ })
                                    HiddenFromAddressLists = (__yn $g.HiddenFromAddressListsEnabled)
                                    HiddenGroupMembership = (__yn $g.HiddenGroupMembershipEnabled)
                                    ManagedBy = ((__recipNames $g.ManagedByWithDisplayNames $g.ManagedBy) -join '; ')
                                    MemberJoinRestriction = [string]$g.MemberJoinRestriction
                                    MemberDepartRestriction = [string]$g.MemberDepartRestriction
                                    GrantSendOnBehalfTo = ((__recipNames $g.GrantSendOnBehalfToWithDisplayNames $g.GrantSendOnBehalfTo) -join '; ')
                                    RequireSenderAuthenticationEnabled = (__yn $g.RequireSenderAuthenticationEnabled)
                                    AcceptMessagesOnlyFromSendersOrMembers = ((__recipNames $g.AcceptMessagesOnlyFromSendersOrMembersWithDisplayNames $g.AcceptMessagesOnlyFromSendersOrMembers) -join '; ')
                                    # The one recipient field Exchange has no display-name switch for.
                                    RejectMessagesFromSendersOrMembers = ((__ownerNames $g.RejectMessagesFromSendersOrMembers) -join '; ')
                                    BccBlocked = (__yn $g.BccBlocked)
                                    MaxSendSize = [string]$g.MaxSendSize
                                    MaxReceiveSize = [string]$g.MaxReceiveSize
                                    ReportToManagerEnabled = (__yn $g.ReportToManagerEnabled)
                                    ReportToOriginatorEnabled = (__yn $g.ReportToOriginatorEnabled)
                                    SendOofMessageToOriginatorEnabled = (__yn $g.SendOofMessageToOriginatorEnabled)
                                    ModerationEnabled = (__yn $g.ModerationEnabled)
                                    ModeratedBy = ((__recipNames $g.ModeratedByWithDisplayNames $g.ModeratedBy) -join '; ')
                                    SendModerationNotifications = [string]$g.SendModerationNotifications
                                    BypassModerationFromSendersOrMembers = ((__recipNames $g.BypassModerationFromSendersOrMembersWithDisplayNames $g.BypassModerationFromSendersOrMembers) -join '; ')
                                }; detail = (__ownerDiag) }
                            }
                            'set-dl-properties' {
                                $wantW = ([string]$r.identity).Trim()
                                if ([string]::IsNullOrWhiteSpace($wantW)) { throw 'A group is required.' }
                                # Read before writing. A non-existent -Identity makes an Exchange Get- cmdlet
                                # return EVERY object, so this both proves the group exists and pins down which
                                # one is about to be changed.
                                $wg = Get-DistributionGroup -Identity $r.identity -ErrorAction Stop | Select-Object -First 1
                                if ($null -eq $wg) { throw "No group was returned for '$wantW'." }
                                if (-not (__isWanted $wg $wantW)) { throw "Exchange did not return the group '$wantW'." }
                                # Exchange rejects every write against a synced object, and its own error names
                                # neither the group nor the reason. Say it plainly instead.
                                if ([string]$wg.IsDirSynced -eq 'True') {
                                    throw "'$([string]$wg.DisplayName)' is synchronized from on-premises Active Directory. Exchange Online cannot change a synced group; edit it in Active Directory."
                                }
                                # The parameter names are chosen in C#, but this is the last point at which they
                                # can be checked before reaching a cmdlet that changes far more than this app
                                # offers. Anything outside the list is a defect, so it stops here.
                                $wAllowed = @(
                                    'ManagedBy', 'GrantSendOnBehalfTo', 'ModeratedBy',
                                    'AcceptMessagesOnlyFromSendersOrMembers', 'RejectMessagesFromSendersOrMembers',
                                    'BypassModerationFromSendersOrMembers',
                                    'Description', 'MailTip', 'RoomList', 'EmailAddresses', 'HiddenFromAddressListsEnabled',
                                    'MemberJoinRestriction', 'MemberDepartRestriction', 'RequireSenderAuthenticationEnabled',
                                    'BccBlocked', 'ReportToOriginatorEnabled', 'ReportToManagerEnabled',
                                    'SendOofMessageToOriginatorEnabled', 'ModerationEnabled', 'SendModerationNotifications'
                                )
                                $wsp = @{}
                                foreach ($wp in $r.changes.PSObject.Properties) {
                                    if ($wAllowed -notcontains $wp.Name) {
                                        throw "'$($wp.Name)' is not a distribution group setting this app may change."
                                    }
                                    # These six can hold entries this app cannot resolve — a role group such
                                    # as Organization Management is not a mail recipient, and Set-DistributionGroup
                                    # will not accept one back. Replacing the list would delete them, so only
                                    # the difference is ever sent and everything else is left untouched.
                                    $wRecipient = @(
                                        'ManagedBy', 'GrantSendOnBehalfTo', 'ModeratedBy',
                                        'AcceptMessagesOnlyFromSendersOrMembers', 'RejectMessagesFromSendersOrMembers',
                                        'BypassModerationFromSendersOrMembers'
                                    )
                                    if ($wRecipient -contains $wp.Name) {
                                        $ra = @{}
                                        foreach ($side in 'Add', 'Remove') {
                                            $rvals = @()
                                            foreach ($rv in @($wp.Value.$side)) {
                                                $rs = [string]$rv
                                                if (-not [string]::IsNullOrWhiteSpace($rs)) { $rvals += $rs }
                                            }
                                            if ($rvals.Count -gt 0) { $ra[$side] = $rvals }
                                        }
                                        if ($ra.Count -eq 0) { continue }
                                        $wsp[$wp.Name] = $ra
                                        continue
                                    }
                                    if ($wp.Name -eq 'EmailAddresses') {
                                        # Addresses arrive as an add/remove, never as a replacement collection:
                                        # handing Exchange the whole set is how a primary address gets moved,
                                        # and an uppercase SMTP: entry promotes itself to the reply address.
                                        # JSON gives an object here, and the parameter needs a real hashtable.
                                        $ea = @{}
                                        foreach ($side in 'Add', 'Remove') {
                                            $clean = @()
                                            foreach ($av in @($wp.Value.$side)) {
                                                $as = [string]$av
                                                if ([string]::IsNullOrWhiteSpace($as)) { continue }
                                                # Case-sensitive: lowercase smtp: is a secondary, uppercase is the primary.
                                                if ($as -cnotmatch '^smtp:') {
                                                    throw "'$as' must be a secondary address, written as smtp:<address>."
                                                }
                                                $abare = $as.Substring(5)
                                                if ($abare -eq [string]$wg.PrimarySmtpAddress) {
                                                    throw "'$abare' is this group's primary address, which is not changed here."
                                                }
                                                if ($abare -like '*.onmicrosoft.com') {
                                                    throw "'$abare' is a routing address Microsoft 365 maintains, which is not changed here."
                                                }
                                                $clean += $as
                                            }
                                            if ($clean.Count -gt 0) { $ea[$side] = $clean }
                                        }
                                        if ($ea.Count -eq 0) { continue }
                                        $wsp['EmailAddresses'] = $ea
                                        continue
                                    }
                                    $wsp[$wp.Name] = $wp.Value
                                }
                                if ($wsp.Count -eq 0) { throw 'No changes were supplied.' }
                                # Address the write by GUID, not by the identity that was searched for: changing
                                # the alias can rewrite the primary address, and the value used to find the group
                                # may no longer name it a moment later.
                                $wsp['Identity'] = ([string]$wg.Guid)
                                # The owner of a group is usually a role group, so no individual passes Exchange's
                                # "manager of the group" check and every edit would fail for a reason unrelated to
                                # the edit.
                                $wsp['BypassSecurityGroupManagerCheck'] = $true
                                $wsp['ErrorAction'] = 'Stop'
                                Set-DistributionGroup @wsp
                                __emit @{ ok = $true }
                            }
                            'list-dl-recipients' {
                                # Resolves ONE recipient-valued property to names and addresses, on demand.
                                # Not part of opening a group: it costs a lookup per entry on a channel that
                                # serialises every call, and the pane only needs addresses when an operator
                                # actually opens the editor. The read path keeps using the display-name
                                # switches, which cost nothing.
                                __ownerReset 200
                                $wantR = ([string]$r.identity).Trim()
                                if ([string]::IsNullOrWhiteSpace($wantR)) { throw 'A group is required.' }
                                # Same allow-list discipline as the write: the field name is chosen in C#, but
                                # this is the last place it can be checked before it indexes a live object.
                                $rAllowed = @(
                                    'ManagedBy', 'GrantSendOnBehalfTo', 'ModeratedBy',
                                    'AcceptMessagesOnlyFromSendersOrMembers', 'RejectMessagesFromSendersOrMembers',
                                    'BypassModerationFromSendersOrMembers'
                                )
                                $rField = [string]$r.field
                                if ($rAllowed -notcontains $rField) { throw "'$rField' is not a recipient property this app reads." }

                                $rg = Get-DistributionGroup -Identity $r.identity -ErrorAction Stop | Select-Object -First 1
                                if ($null -eq $rg) { throw "No group was returned for '$wantR'." }
                                # A non-existent -Identity makes an Exchange Get- cmdlet return EVERY object.
                                if (-not (__isWanted $rg $wantR)) { throw "Exchange did not return the group '$wantR'." }

                                # The RAW property, not the display-name variant: a display name cannot be
                                # written back, and these entries have to survive a round trip.
                                $data = @(__recipList $rg.$rField)
                                __emit @{ ok = $true; data = $data; detail = (__ownerDiag) }
                            }
                            'list-dl-members' {
                                # -ResultSize Unlimited: the default page is 1,000 and truncates silently, which
                                # on a membership list would read as the complete membership.
                                $mem = @(Get-DistributionGroupMember -Identity $r.group -ResultSize Unlimited -ErrorAction Stop)
                                $data = @($mem | ForEach-Object {
                                    $smtp = [string]$_.PrimarySmtpAddress
                                    # Mail contacts and mail users can lack a primary SMTP; Name still addresses
                                    # them for Remove-DistributionGroupMember.
                                    $id = if ([string]::IsNullOrWhiteSpace($smtp)) { [string]$_.Name } else { $smtp }
                                    @{
                                        Identity = $id
                                        DisplayName = [string]$_.DisplayName
                                        PrimarySmtpAddress = $smtp
                                        RecipientType = [string]$_.RecipientTypeDetails
                                    }
                                })
                                __emit @{ ok = $true; data = $data }
                            }
                            'new-distribution-group' {
                                # Owners and members go on the create itself here, unlike the Graph path, where
                                # anything in the request BODY that fails to bind destroys the whole create. A
                                # failure here means nothing was created, so there is no partial state to report.
                                $np = __newDgParams $r
                                $g = New-DistributionGroup @np 6>$null
                                $g = $g | Select-Object -First 1
                                __emit @{ ok = $true; data = @{
                                    DisplayName = [string]$g.DisplayName
                                    PrimarySmtpAddress = [string]$g.PrimarySmtpAddress
                                    ExternalDirectoryObjectId = [string]$g.ExternalDirectoryObjectId
                                } }
                            }
                            default { __emit @{ ok = $false; error = ("Unknown op: " + [string]$r.op) } }
                        }
                    } catch { __emit @{ ok = $false; error = $_.Exception.Message; detail = ($_ | Out-String) } }
                }
                default { __emit @{ ok = $false; error = ("Unknown verb: " + $verb) } }
            }
        }
        """;
}
