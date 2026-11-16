using Microsoft.Graph.Models.ODataErrors;
using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Renames a group of any kind: on-prem AD, Entra security, Microsoft 365 (and the Team behind one), or an Exchange
/// Online distribution list, mail-enabled security group or room list. See docs/rename-plan.md.
/// </summary>
/// <remarks>
/// <para>
/// The kind decides the route, not the list the group was opened from. A synced group opened from a cloud list is
/// renamed on-prem, because Entra and Exchange both refuse writes to it; a distribution list opened from the Entra
/// list goes to Exchange, because Graph refuses those.
/// </para>
/// <para>
/// Every outcome is read back from the service that holds the group. A naming policy can change the name that was
/// asked for, and reporting the request instead of the result would show a name the group does not have.
/// </para>
/// </remarks>
public sealed class GroupRenameService
{
    private readonly IDirectoryService _directory;
    private readonly IGraphService _graph;
    private readonly IExchangeService _exchange;
    private readonly GroupReferenceMaintenance? _references;

    public GroupRenameService(IDirectoryService directory, IGraphService graph, IExchangeService exchange,
        GroupReferenceMaintenance? references)
    {
        _directory = directory;
        _graph = graph;
        _exchange = exchange;
        _references = references;
    }

    // --- Finding the group ------------------------------------------------------------------------------------

    /// <summary>Reads the group named by <paramref name="source"/>, routing it to the service that owns it.</summary>
    /// <exception cref="InvalidOperationException">With a message for the operator, when it cannot be renamed from here.</exception>
    public Task<GroupRenameSubject> LoadAsync(GroupRenameSource source, CancellationToken cancellationToken = default) =>
        source.Origin switch
        {
            GroupRenameOrigin.OnPremDn => LoadOnPremAsync(source.Identity, redirected: false, cancellationToken),
            GroupRenameOrigin.EntraGroupId => LoadEntraAsync(source.Identity, cancellationToken),
            _ => LoadExchangeAsync(source.Identity, cancellationToken),
        };

    private async Task<GroupRenameSubject> LoadOnPremAsync(string dn, bool redirected, CancellationToken ct)
    {
        if (!_directory.IsConnected)
            throw new InvalidOperationException("Connect to Active Directory to rename this group.");
        var attrs = await _directory.LoadObjectAsync(dn, ct);
        if (attrs.Count == 0) throw new InvalidOperationException("The group could not be read. It may have been deleted or moved.");

        AdAttribute? A(string ldap) => attrs.FirstOrDefault(a => a.LdapName.Equals(ldap, StringComparison.OrdinalIgnoreCase));
        string? Raw(string ldap) => A(ldap)?.RawValues.FirstOrDefault(v => !string.IsNullOrEmpty(v));
        string? Shown(string ldap) => A(ldap)?.DisplayValues.FirstOrDefault(v => !string.IsNullOrEmpty(v));

        var classes = A("objectClass")?.RawValues.ToList() ?? new List<string>();
        if (classes.Count > 0 && !classes.Any(c => c.Equals("group", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Only groups can be renamed here.");

        var proxies = A("proxyAddresses")?.RawValues.Where(v => !string.IsNullOrWhiteSpace(v)).ToList() ?? new List<string>();
        var guid = Shown("objectGUID") ?? await _directory.GetObjectGuidAsync(dn, ct);
        var kind = GroupTypeClassifier.Describe(Raw("groupType") ?? string.Empty);
        return new GroupRenameSubject
        {
            Kind = GroupRenameKind.OnPremAd,
            Identity = dn,
            ObjectGuid = guid,
            Sid = Shown("objectSid"),
            Name = Raw("cn") ?? Raw("name") ?? NameResolver.RdnFallback(dn),
            DisplayName = Raw("displayName") ?? string.Empty,
            SamAccountName = Raw("sAMAccountName") ?? string.Empty,
            PrimaryAddress = GroupNaming.PrimaryOf(proxies) ?? Raw("mail"),
            Addresses = proxies,
            KindLabel = string.IsNullOrWhiteSpace(kind) ? "Active Directory group" : $"Active Directory group ({kind})",
            RedirectedFromCloud = redirected,
        };
    }

    private async Task<GroupRenameSubject> LoadEntraAsync(string groupId, CancellationToken ct)
    {
        if (!_graph.IsSignedIn) throw new InvalidOperationException("Sign in to Entra ID to rename this group.");
        var g = await _graph.GetGroupNamingAsync(groupId, ct)
                ?? throw new InvalidOperationException("Entra ID has no group with that ID any more.");

        // Entra refuses writes to a synced group; it is renamed where it lives.
        if (g.IsSynced) return await RedirectToOnPremAsync(g.OnPremSid, g.DisplayName, ct);

        // Graph cannot change a distribution list or a mail-enabled security group; Exchange owns those.
        if (g.GroupKind is "Distribution" or "Mail-enabled security")
            return await LoadExchangeAsync(!string.IsNullOrWhiteSpace(g.Mail) ? g.Mail! : g.Id, ct);

        var m365 = g.GroupKind == "Microsoft 365";
        return new GroupRenameSubject
        {
            Kind = m365 ? GroupRenameKind.Microsoft365 : GroupRenameKind.EntraSecurity,
            Identity = g.Id,
            EntraId = g.Id,
            Name = g.DisplayName,
            DisplayName = g.DisplayName,
            PrimaryAddress = m365 ? g.Mail : null,
            Addresses = g.ProxyAddresses,
            IsTeam = g.IsTeam,
            KindLabel = m365 ? (g.IsTeam ? "Microsoft 365 group (Team)" : "Microsoft 365 group") : "Security group (Entra ID)",
        };
    }

    private async Task<GroupRenameSubject> LoadExchangeAsync(string identity, CancellationToken ct)
    {
        var n = await _exchange.GetDistributionGroupNamingAsync(identity, ct);
        if (n.IsDirSynced)
        {
            // Exchange refuses writes to a synced group too. Its on-prem object is found through the SID Entra holds.
            string? sid = null;
            if (n.ExternalDirectoryObjectId is { } id && _graph.IsSignedIn)
                sid = (await _graph.GetGroupNamingAsync(id, ct))?.OnPremSid;
            return await RedirectToOnPremAsync(sid, n.DisplayName, ct);
        }
        return new GroupRenameSubject
        {
            Kind = GroupRenameKind.ExchangeDistribution,
            // The GUID, not the address: the rename may change the address the group was found by.
            Identity = string.IsNullOrWhiteSpace(n.Guid) ? identity : n.Guid,
            EntraId = n.ExternalDirectoryObjectId,
            Name = n.Name,
            DisplayName = n.DisplayName,
            PrimaryAddress = string.IsNullOrWhiteSpace(n.PrimarySmtpAddress) ? null : n.PrimarySmtpAddress,
            Addresses = n.EmailAddresses,
            KindLabel = n.RecipientTypeDetails switch
            {
                "MailUniversalSecurityGroup" => "Mail-enabled security group",
                "RoomList" => "Room list",
                _ => "Distribution list",
            },
        };
    }

    private async Task<GroupRenameSubject> RedirectToOnPremAsync(string? sid, string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sid))
            throw new InvalidOperationException(
                $"“{name}” is synchronized from Active Directory, so it has to be renamed there. Its on-premises group " +
                "could not be identified from here; rename it from the Active Directory tree.");
        if (!_directory.IsConnected)
            throw new InvalidOperationException(
                $"“{name}” is synchronized from Active Directory, so it has to be renamed there. Connect to Active Directory first.");
        var dn = await _directory.FindDnBySidAsync(sid, ct)
                 ?? throw new InvalidOperationException(
                     $"“{name}” is synchronized from Active Directory, but no group with its SID was found in the connected domain.");
        return await LoadOnPremAsync(dn, redirected: true, ct);
    }

    // --- Checking before writing ------------------------------------------------------------------------------

    /// <summary>
    /// Checks a request against the group and everything else that could hold the names and address it asks for.
    /// Never writes. A check that cannot run (a service is unreachable) becomes a warning rather than a pass that
    /// looks like a result.
    /// </summary>
    public async Task<GroupRenameCheck> CheckAsync(GroupRenameSubject s, GroupRenameRequest r, CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();
        var warnings = new List<string>();
        var changes = new List<GroupRenameChange>();
        var name = (r.Name ?? string.Empty).Trim();
        var display = (r.DisplayName ?? string.Empty).Trim();
        var address = NewAddress(s, r);

        if (address is not null && !s.IsMailEnabled) problems.Add("This group has no email address to change.");

        switch (s.Kind)
        {
            case GroupRenameKind.OnPremAd:
                await CheckOnPremAsync(s, r, name, display, problems, warnings, changes, cancellationToken);
                break;
            case GroupRenameKind.ExchangeDistribution:
                await CheckExchangeAsync(s, name, display, problems, warnings, changes, cancellationToken);
                break;
            default:
                await CheckEntraAsync(s, display, problems, warnings, changes, cancellationToken);
                break;
        }

        if (address is not null && s.IsMailEnabled)
            await CheckAddressAsync(s, address, problems, warnings, changes, cancellationToken);

        return new GroupRenameCheck(problems, warnings, changes);
    }

    /// <summary>The new primary address asked for, or null when it is not changing (an unchanged or blank box).</summary>
    private static string? NewAddress(GroupRenameSubject s, GroupRenameRequest r)
    {
        var a = r.NewPrimaryAddress?.Trim();
        if (string.IsNullOrEmpty(a)) return null;
        return s.PrimaryAddress is not null && string.Equals(a, s.PrimaryAddress, StringComparison.OrdinalIgnoreCase) ? null : a;
    }

    private async Task CheckOnPremAsync(GroupRenameSubject s, GroupRenameRequest r, string name, string display,
        List<string> problems, List<string> warnings, List<GroupRenameChange> changes, CancellationToken ct)
    {
        if (GroupNaming.NameProblem(name, "group name", GroupNaming.MaxCommonName) is { } np) problems.Add(np);
        else if (!string.Equals(name, s.Name, StringComparison.Ordinal))
        {
            changes.Add(new("Group name", s.Name, name));
            // Only another object in the same OU can clash; the same object with its case changed cannot.
            var newDn = DirectoryService.BuildRenamedDn(s.Identity, name);
            if (!string.Equals(newDn, s.Identity, StringComparison.OrdinalIgnoreCase)
                && await Try(() => _directory.ExistsAsync(newDn, ct), warnings, "whether the name is free in its OU") == true)
                problems.Add($"Another object in this OU is already called “{name}”.");
        }

        var sam = (r.SamAccountName ?? string.Empty).Trim();
        if (GroupNaming.SamProblem(sam) is { } sp) problems.Add(sp);
        else if (!string.Equals(sam, s.SamAccountName, StringComparison.Ordinal))
        {
            changes.Add(new("Pre-Windows 2000 name", s.SamAccountName ?? string.Empty, sam));
            // A change of case alone is the same name to AD, so it cannot clash with anything.
            if (!string.Equals(sam, s.SamAccountName, StringComparison.OrdinalIgnoreCase))
            {
                var taken = await Try(() => _directory.FindExistingSamAccountNamesAsync(new[] { sam }, ct), warnings,
                                      "whether the pre-Windows 2000 name is free");
                if (taken?.Contains(sam) == true)
                    problems.Add($"The pre-Windows 2000 name “{sam}” is already used by another account or group in the domain.");
            }
        }

        if (display.Length > GroupNaming.MaxDisplayName)
            problems.Add($"The display name is {display.Length} characters; the limit is {GroupNaming.MaxDisplayName}.");
        else if (display.Length == 0 && s.IsMailEnabled)
            // Microsoft: a mail-enabled group with no display name does not sync to Microsoft 365 at all.
            problems.Add("A mail-enabled group needs a display name, or it stops syncing to Microsoft 365.");
        else if (!string.Equals(display, s.DisplayName, StringComparison.Ordinal))
            changes.Add(new("Display name", Blank(s.DisplayName), Blank(display)));

        if (changes.Count > 0)
            warnings.Add(s.RedirectedFromCloud
                ? "This group is synchronized from Active Directory, so it is renamed there. Entra ID, Teams and Exchange Online show the new name after the next Entra Connect sync."
                : "If this group is synced to Microsoft 365, Entra ID, Teams and Exchange Online show the new name after the next Entra Connect sync.");
    }

    private async Task CheckEntraAsync(GroupRenameSubject s, string display, List<string> problems,
        List<string> warnings, List<GroupRenameChange> changes, CancellationToken ct)
    {
        if (GroupNaming.NameProblem(display, "name", GroupNaming.MaxDisplayName) is { } np) { problems.Add(np); return; }
        if (string.Equals(display, s.DisplayName, StringComparison.Ordinal)) return;
        changes.Add(new("Name", s.DisplayName, display));

        // Entra allows two groups with one name. Not a reason to stop, but a picker showing two identical names is.
        if (!string.Equals(display, s.DisplayName, StringComparison.OrdinalIgnoreCase))
        {
            var same = await Try(() => _graph.FindGroupIdsByDisplayNameAsync(display, ct), warnings, "whether another group has that name");
            if (same?.Any(id => !string.Equals(id, s.Identity, StringComparison.OrdinalIgnoreCase)) == true)
                warnings.Add($"Another group in Entra ID is already called “{display}”. Entra allows it, but the two will look the same in every list.");
        }
        if (s.Kind == GroupRenameKind.Microsoft365)
            warnings.Add("If your organization has a naming policy for Microsoft 365 groups, Entra ID may add a prefix or suffix. The name it sets is shown afterwards.");
        if (s.IsTeam)
            warnings.Add("The Team takes the new name too. Its SharePoint site address does not change.");
    }

    private async Task CheckExchangeAsync(GroupRenameSubject s, string name, string display, List<string> problems,
        List<string> warnings, List<GroupRenameChange> changes, CancellationToken ct)
    {
        if (GroupNaming.NameProblem(name, "name", GroupNaming.MaxExchangeName) is { } np) problems.Add(np);
        else if (!string.Equals(name, s.Name, StringComparison.Ordinal))
        {
            changes.Add(new("Name", s.Name, name));
            if (!string.Equals(name, s.Name, StringComparison.OrdinalIgnoreCase))
            {
                var held = await Try(() => _exchange.FindRecipientConflictsAsync(name, null, ct), warnings, "whether the name is free in Exchange Online");
                var other = held?.FirstOrDefault(c => c.Field == "Name" && !IsSelf(s, c));
                if (other is not null)
                    problems.Add($"“{name}” is already the name of {Describe(other)} in Exchange Online.");
            }
        }

        if (GroupNaming.NameProblem(display, "display name", GroupNaming.MaxDisplayName) is { } dp) problems.Add(dp);
        else if (!string.Equals(display, s.DisplayName, StringComparison.Ordinal))
            changes.Add(new("Display name", s.DisplayName, display));

        if (changes.Count > 0)
            warnings.Add("If your organization has a distribution group naming policy, Exchange applies it. The name it sets is shown afterwards.");
    }

    private async Task CheckAddressAsync(GroupRenameSubject s, string address, List<string> problems,
        List<string> warnings, List<GroupRenameChange> changes, CancellationToken ct)
    {
        if (GroupNaming.AddressProblem(address) is { } ap) { problems.Add(ap); return; }
        changes.Add(new("Primary email address", s.PrimaryAddress ?? string.Empty, address));
        changes.Add(new("Old address", s.PrimaryAddress ?? string.Empty, "kept as a secondary address, so mail sent to it still arrives"));

        var oldDomain = GroupNaming.Domain(s.PrimaryAddress ?? string.Empty);
        if (!string.Equals(GroupNaming.Domain(address), oldDomain, StringComparison.OrdinalIgnoreCase)
            && !s.Addresses.Any(a => a.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(GroupNaming.Domain(a[5..]), GroupNaming.Domain(address), StringComparison.OrdinalIgnoreCase)))
            warnings.Add($"“{GroupNaming.Domain(address)}” is not a domain this group uses now. It must be a domain your organization accepts, or the address is refused.");

        if (s.Kind == GroupRenameKind.OnPremAd)
        {
            var owners = await Try(() => _directory.FindAddressOwnersAsync(address, ct), warnings, "Active Directory for the new address");
            var other = owners?.FirstOrDefault(dn => !string.Equals(dn, s.Identity, StringComparison.OrdinalIgnoreCase));
            if (other is not null)
                problems.Add($"{address} already belongs to {NameResolver.RdnFallback(other)} in Active Directory.");
        }

        if (s.Kind == GroupRenameKind.ExchangeDistribution)
        {
            var held = await Try(() => _exchange.FindRecipientConflictsAsync(null, address, ct), warnings, "Exchange Online for the new address");
            var other = held?.FirstOrDefault(c => c.Field == "Address" && !IsSelf(s, c));
            if (other is not null) problems.Add($"{address} already belongs to {Describe(other)} in Exchange Online.");
            return;
        }

        // The group's own copy in the cloud holds every address the group already has, so making one of them the
        // primary must not read as a clash with itself. Graph identifies that copy: by its ID, or for a synced group
        // by its SID. Exchange's answer is then read against the same IDs.
        var self = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(s.EntraId)) self.Add(s.EntraId);

        // On-prem and Microsoft 365 groups are checked against Entra too: the cloud holds objects AD never sees, and
        // an address that clashes with one of them is quarantined by sync with no error anyone is shown.
        IReadOnlyList<CloudAddressOwner>? cloud = null;
        if (!_graph.IsSignedIn)
            warnings.Add("Microsoft 365 was not checked for the new address, because nobody is signed in to Entra ID.");
        else
        {
            cloud = await Try(() => _graph.FindAddressOwnersAsync(address, ct), warnings, "Microsoft 365 for the new address");
            foreach (var o in cloud ?? Array.Empty<CloudAddressOwner>())
                if (s.Sid is not null && o.Id.Length > 0 && string.Equals(o.OnPremSid, s.Sid, StringComparison.OrdinalIgnoreCase))
                    self.Add(o.Id);
            var cloudOther = cloud?.FirstOrDefault(o => !self.Contains(o.Id));
            if (cloudOther is not null)
            {
                problems.Add($"{address} already belongs to {cloudOther.Kind.ToLowerInvariant()} “{cloudOther.DisplayName}” in Microsoft 365.");
                return;
            }
        }

        // Exchange sees every kind of recipient, mail contacts included, which Graph is not allowed to read here.
        // Asked only when already connected: the first connect can take a while, and Graph covers the rest.
        if (!_exchange.IsConnected) return;
        var exchangeHolders = await Try(() => _exchange.FindRecipientConflictsAsync(null, address, ct), warnings, "Exchange Online for the new address");
        var holder = exchangeHolders?.FirstOrDefault(c => c.Field == "Address" && !(c.EntraId is { } id && self.Contains(id)));
        if (holder is null) return;
        // Microsoft 365 gave no answer, so nothing identifies the group's own copy, and the group already has this
        // address: the holder is most likely that copy. When Microsoft 365 did answer, its copy would be in `self`.
        if (cloud is null && self.Count == 0 && GroupNaming.Holds(s.Addresses, address))
            warnings.Add($"Exchange Online lists {Describe(holder)} with {address}. That is expected if it is this group's own synced copy, but without an answer from Microsoft 365 it could not be confirmed.");
        else
            problems.Add($"{address} already belongs to {Describe(holder)} in Exchange Online.");
    }

    private static bool IsSelf(GroupRenameSubject s, RecipientConflict c) =>
        string.Equals(c.Identity, s.Identity, StringComparison.OrdinalIgnoreCase);

    private static string Describe(RecipientConflict c) =>
        string.IsNullOrWhiteSpace(c.DisplayName) ? "another recipient" : $"“{c.DisplayName}”";

    private static string Blank(string? s) => string.IsNullOrWhiteSpace(s) ? "(none)" : s;

    /// <summary>Runs a check, turning a failure into a warning that says what could not be checked.</summary>
    private static async Task<T?> Try<T>(Func<Task<T>> check, List<string> warnings, string what)
    {
        try { return await check(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            warnings.Add($"Could not check {what}: {Friendly(ex)}");
            return default;
        }
    }

    /// <summary>A failure in words an operator can act on, whichever service raised it.</summary>
    public static string Friendly(Exception ex) =>
        ex is ODataError ? GraphErrors.Friendly(ex) : DirectoryService.Friendly(ex);

    // --- Writing ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Renames the group. Call <see cref="CheckAsync"/> first: this writes what it is given. Never throws for a
    /// refused write; the outcome says what was and was not done.
    /// </summary>
    public Task<GroupRenameOutcome> ApplyAsync(GroupRenameSubject s, GroupRenameRequest r, CancellationToken cancellationToken = default) =>
        s.Kind switch
        {
            GroupRenameKind.OnPremAd => ApplyOnPremAsync(s, r, cancellationToken),
            GroupRenameKind.ExchangeDistribution => ApplyExchangeAsync(s, r, cancellationToken),
            _ => ApplyEntraAsync(s, r, cancellationToken),
        };

    private async Task<GroupRenameOutcome> ApplyOnPremAsync(GroupRenameSubject s, GroupRenameRequest r, CancellationToken ct)
    {
        var dn = s.Identity;
        var name = r.Name.Trim();
        var display = (r.DisplayName ?? string.Empty).Trim();
        var sam = (r.SamAccountName ?? string.Empty).Trim();
        var address = NewAddress(s, r);
        var notes = new List<string>();

        // Everything but the name goes in ONE modify: LDAP applies a modify as a single operation, and the DC
        // enforces sAMAccountName uniqueness inside it, so a refusal leaves the group exactly as it was.
        var writes = new List<PendingChange>();
        if (sam.Length > 0 && !string.Equals(sam, s.SamAccountName, StringComparison.Ordinal))
            writes.Add(new PendingChange { Op = ChangeOp.Set, LdapName = "sAMAccountName", FriendlyName = "Pre-Windows 2000 name", Values = { sam } });
        if (!string.Equals(display, s.DisplayName, StringComparison.Ordinal))
            writes.Add(display.Length == 0
                ? new PendingChange { Op = ChangeOp.Clear, LdapName = "displayName", FriendlyName = "Display name" }
                : new PendingChange { Op = ChangeOp.Set, LdapName = "displayName", FriendlyName = "Display name", Values = { display } });
        if (address is not null)
        {
            writes.Add(new PendingChange { Op = ChangeOp.Set, LdapName = "mail", FriendlyName = "Email address", Values = { address } });
            writes.Add(new PendingChange
            {
                Op = ChangeOp.Set, LdapName = "proxyAddresses", FriendlyName = "Proxy addresses",
                Values = GroupNaming.PlanProxyAddresses(s.Addresses, address).ToList(),
            });
        }

        if (writes.Count > 0)
        {
            try { await _directory.ApplyChangesAsync(dn, writes, ct); }
            catch (Exception ex)
            {
                return new GroupRenameOutcome(false, dn, s.Name, s.DisplayName, s.PrimaryAddress, notes,
                    "Nothing was changed: " + Friendly(ex), NamePending: false);
            }
        }

        // The name in the tree is a separate operation: AD changes cn only through a modify-DN.
        var newDn = dn;
        string? error = null;
        var namePending = false;
        if (!string.Equals(name, s.Name, StringComparison.Ordinal))
        {
            try { newDn = await _directory.RenameObjectAsync(dn, name, ct); }
            catch (Exception ex)
            {
                namePending = writes.Count > 0;
                error = (writes.Count > 0
                    ? "The other names were changed, but the group's name in Active Directory was not: "
                    : "Nothing was changed: ") + Friendly(ex);
            }
        }

        if (address is not null && s.PrimaryAddress is { } old)
            notes.Add($"{old} still delivers to this group: it was kept as a secondary address.");
        if (!string.Equals(newDn, dn, StringComparison.Ordinal) && s.ObjectGuid is { } guid && _references is not null)
        {
            // The group is already renamed: a file that cannot be saved is logged, never reported as a failed rename.
            try
            {
                var updated = await _references.RetargetGroupAsync(guid, dn, newDn);
                if (updated > 0) notes.Add($"Updated {updated} template(s) or scenario(s) that use this group.");
            }
            catch (Exception ex) { AppLog.Instance.Warn($"Could not point templates and scenarios at {newDn}: {ex.Message}"); }
        }

        // Read back rather than echo the request: what AD holds is what the operator should see.
        GroupRenameSubject? after = null;
        try { after = await LoadOnPremAsync(newDn, s.RedirectedFromCloud, ct); }
        catch (Exception ex) { notes.Add("The group could not be read back to confirm: " + Friendly(ex)); }

        AppLog.Instance.Info($"Renamed group {dn} -> {newDn}: name “{s.Name}” → “{after?.Name ?? name}”, "
            + $"pre-Windows 2000 “{s.SamAccountName}” → “{after?.SamAccountName ?? sam}”, display “{s.DisplayName}” → “{after?.DisplayName ?? display}”"
            + (address is not null ? $", primary address {s.PrimaryAddress} → {after?.PrimaryAddress ?? address}" : string.Empty)
            + (error is not null ? $". NOT FINISHED: {error}" : "."));

        return new GroupRenameOutcome(error is null, newDn,
            after?.Name ?? (error is null ? name : s.Name), after?.DisplayName ?? display,
            after?.PrimaryAddress ?? address ?? s.PrimaryAddress, notes, error, namePending);
    }

    private async Task<GroupRenameOutcome> ApplyEntraAsync(GroupRenameSubject s, GroupRenameRequest r, CancellationToken ct)
    {
        var id = s.Identity;
        var display = (r.DisplayName ?? string.Empty).Trim();
        var address = s.Kind == GroupRenameKind.Microsoft365 ? NewAddress(s, r) : null;
        var notes = new List<string>();
        var renamed = !string.Equals(display, s.DisplayName, StringComparison.Ordinal);

        if (renamed)
        {
            try { await _graph.UpdateGroupAsync(id, new Dictionary<string, string?> { ["displayName"] = display }, ct); }
            catch (Exception ex)
            {
                return new GroupRenameOutcome(false, id, s.Name, s.DisplayName, s.PrimaryAddress, notes,
                    "Nothing was changed: " + Friendly(ex), NamePending: false);
            }
        }

        // Graph treats a group's mail properties as read-only, so the address goes through Exchange.
        string? error = null;
        string? actualPrimary = s.PrimaryAddress;
        if (address is not null)
        {
            try
            {
                // Found by its current address, which Get-UnifiedGroup documents as an identity; the write itself
                // then addresses the group by its Exchange GUID, since the address is what changes.
                var addresses = await _exchange.SetUnifiedGroupPrimaryAddressAsync(s.PrimaryAddress ?? id, address, ct);
                actualPrimary = GroupNaming.PrimaryOf(addresses) ?? address;
                if (s.PrimaryAddress is { } old)
                    notes.Add(GroupNaming.Holds(addresses, old)
                        ? $"{old} still delivers to this group: it was kept as a secondary address."
                        : $"{old} is not among the group's addresses any more. Add it back in Exchange if mail is still sent to it.");
            }
            catch (Exception ex)
            {
                error = (renamed ? "The name was changed, but the email address was not: " : "Nothing was changed: ") + ex.Message;
            }
        }

        var actualName = display;
        try
        {
            if (await _graph.GetGroupNamingAsync(id, ct) is { } after)
            {
                actualName = after.DisplayName;
                if (renamed && !string.Equals(after.DisplayName, display, StringComparison.Ordinal))
                    notes.Add($"Entra ID applied your organization's group naming policy: the group is now called “{after.DisplayName}”.");
            }
        }
        catch (Exception ex) { notes.Add("The group could not be read back to confirm: " + Friendly(ex)); }

        if (renamed && s.IsTeam)
            notes.Add("The Team shows the new name once Teams catches up. Its SharePoint site address is unchanged.");
        await RefreshReferencesAsync(s, actualName, address is not null ? actualPrimary : null, notes);

        AppLog.Instance.Info($"Renamed {s.KindLabel} {id}: “{s.DisplayName}” → “{actualName}”"
            + (address is not null ? $", primary address {s.PrimaryAddress} → {actualPrimary}" : string.Empty)
            + (error is not null ? $". NOT FINISHED: {error}" : "."));

        return new GroupRenameOutcome(error is null, id, actualName, actualName, actualPrimary, notes, error, NamePending: false);
    }

    private async Task<GroupRenameOutcome> ApplyExchangeAsync(GroupRenameSubject s, GroupRenameRequest r, CancellationToken ct)
    {
        var name = r.Name.Trim();
        var display = (r.DisplayName ?? string.Empty).Trim();
        var address = NewAddress(s, r);
        var notes = new List<string>();

        DistributionGroupNaming after;
        try { after = await _exchange.RenameDistributionGroupAsync(s.Identity, name, display, address, ct); }
        catch (Exception ex)
        {
            return new GroupRenameOutcome(false, s.Identity, s.Name, s.DisplayName, s.PrimaryAddress, notes,
                "Nothing was changed: " + ex.Message, NamePending: false);
        }

        if (!string.Equals(after.DisplayName, display, StringComparison.Ordinal) || !string.Equals(after.Name, name, StringComparison.Ordinal))
            notes.Add($"Exchange applied your organization's distribution group naming policy: the group is now called “{after.DisplayName}”.");
        if (address is not null && s.PrimaryAddress is { } old)
            notes.Add(GroupNaming.Holds(after.EmailAddresses, old)
                ? $"{old} still delivers to this group: it was kept as a secondary address."
                : $"{old} is not among the group's addresses any more. Add it back if mail is still sent to it.");
        await RefreshReferencesAsync(s, after.DisplayName, address is not null ? after.PrimarySmtpAddress : null, notes);

        AppLog.Instance.Info($"Renamed {s.KindLabel} {s.Identity}: name “{s.Name}” → “{after.Name}”, display “{s.DisplayName}” → “{after.DisplayName}”"
            + (address is not null ? $", primary address {s.PrimaryAddress} → {after.PrimarySmtpAddress}." : "."));

        return new GroupRenameOutcome(true, s.Identity, after.Name, after.DisplayName,
            string.IsNullOrWhiteSpace(after.PrimarySmtpAddress) ? s.PrimaryAddress : after.PrimarySmtpAddress,
            notes, null, NamePending: false);
    }

    /// <summary>Keeps the name templates and scenarios show for a cloud group current; never fails the rename.</summary>
    private async Task RefreshReferencesAsync(GroupRenameSubject s, string newName, string? newSmtp, List<string> notes)
    {
        if (_references is null) return;
        try
        {
            var updated = await _references.RefreshCloudGroupAsync(s.EntraId ?? string.Empty, newName, s.PrimaryAddress, newSmtp);
            if (updated > 0) notes.Add($"Updated {updated} template(s) or scenario(s) that use this group.");
        }
        catch (Exception ex) { AppLog.Instance.Warn($"Could not refresh templates and scenarios for “{newName}”: {ex.Message}"); }
    }
}
