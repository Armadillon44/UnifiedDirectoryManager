using System.DirectoryServices;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using UnifiedDirectoryManager.Models;
using Protocols = System.DirectoryServices.Protocols;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Concrete AD access over System.DirectoryServices, with a lightweight LdapConnection probe for
/// connect/fail-over. Always binds with the explicitly-entered credentials (clients may be Entra-only).
/// </summary>
public sealed class DirectoryService : IDirectoryService
{
    private const int NormalAccount = 0x0200;
    private const int AccountDisable = 0x0002;

    // "Protected from accidental deletion" = a Deny ACE for Everyone (S-1-1-0) covering Delete and
    // DeleteTree on the object itself — the same ACE ADUC's checkbox and Set-ADObject add/remove.
    private const ActiveDirectoryRights DeleteRights =
        ActiveDirectoryRights.Delete | ActiveDirectoryRights.DeleteTree;
    private static readonly SecurityIdentifier EveryoneSid = new(WellKnownSidType.WorldSid, null);

    private ConnectionState? _current;
    private NameResolver? _resolver;

    public ConnectionState? Current => _current;
    public NameResolver? Resolver => _resolver;
    public bool IsConnected => _current is not null;

    private ConnectionState Required =>
        _current ?? throw new InvalidOperationException("Not connected to a domain controller.");

    // ---------------------------------------------------------------- Connect

    public async Task ConnectAsync(ConnectionProfile profile, string password, CancellationToken cancellationToken = default)
    {
        var candidates = profile.OrderedCandidates().ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException("Enter at least a primary domain controller hostname or IP.");

        // Bare usernames can't authenticate from a non-domain-joined (e.g. Entra-only) client — there
        // is no implicit domain to qualify them. Promote "user" to the UPN "user@domain"; pass
        // DOMAIN\user and existing UPNs through unchanged.
        var effectiveUser = NormalizeUsername(profile.Username, profile.DomainFqdn);
        AppLog.Instance.Info($"Connecting to domain '{profile.DomainFqdn}' as '{effectiveUser}' " +
                             $"({(profile.UseLdaps ? "LDAPS" : "LDAP+sign/seal")}); candidates: {string.Join(", ", candidates)}.");

        var errors = new List<string>();
        foreach (var host in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var nc = await Task.Run(() => ProbeAndReadNamingContext(host, profile, effectiveUser, password), cancellationToken);
                _current = new ConnectionState
                {
                    Server = host,
                    DomainFqdn = profile.DomainFqdn,
                    DefaultNamingContext = nc,
                    Username = effectiveUser,
                    Password = password,
                    UseLdaps = profile.UseLdaps,
                    IgnoreCertificateErrors = profile.IgnoreCertificateErrors,
                };
                _resolver = new NameResolver(_current);
                AppLog.Instance.Info($"Connected to DC '{host}' (naming context {nc}).");
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Instance.Warn($"Bind attempt to '{host}' failed: {Friendly(ex)}");
                errors.Add($"  • {host}: {Friendly(ex)}");
            }
        }

        AppLog.Instance.Error($"Connection to domain '{profile.DomainFqdn}' failed for all {candidates.Count} candidate DC(s).");
        throw new InvalidOperationException(
            "Could not connect to any domain controller with the supplied credentials:\n" + string.Join("\n", errors));
    }

    public void Disconnect()
    {
        _current = null;
        _resolver = null;
    }

    /// <summary>Promotes a bare username to a UPN using the domain; leaves DOMAIN\user and UPNs as-is.</summary>
    internal static string NormalizeUsername(string username, string domainFqdn)
    {
        var trimmed = (username ?? string.Empty).Trim();
        if (trimmed.Contains('\\') || trimmed.Contains('@')) return trimmed;
        return string.IsNullOrWhiteSpace(domainFqdn) ? trimmed : $"{trimmed}@{domainFqdn.Trim()}";
    }

    /// <summary>Binds to a single DC and returns its defaultNamingContext, or throws on failure.</summary>
    private static string ProbeAndReadNamingContext(string host, ConnectionProfile profile, string username, string password)
    {
        var port = profile.UseLdaps ? 636 : 389;
        var identifier = new Protocols.LdapDirectoryIdentifier(host, port);
        using var conn = new Protocols.LdapConnection(identifier, new NetworkCredential(username, password))
        {
            AuthType = Protocols.AuthType.Negotiate,
            Timeout = TimeSpan.FromSeconds(15),
        };
        conn.SessionOptions.ProtocolVersion = 3;
        if (profile.UseLdaps)
        {
            conn.SessionOptions.SecureSocketLayer = true;
            // Validate the server certificate by default; only bypass when the user opts in (insecure).
            if (profile.IgnoreCertificateErrors)
                conn.SessionOptions.VerifyServerCertificate = (_, _) => true;
        }
        else
        {
            conn.SessionOptions.Signing = true;
            conn.SessionOptions.Sealing = true;
        }

        conn.Bind();

        var request = new Protocols.SearchRequest(null, "(objectClass=*)", Protocols.SearchScope.Base, "defaultNamingContext");
        var response = (Protocols.SearchResponse)conn.SendRequest(request);
        if (response.Entries.Count == 0)
            throw new InvalidOperationException("Server did not return a RootDSE.");

        var values = response.Entries[0].Attributes["defaultNamingContext"]?.GetValues(typeof(string));
        if (values is null || values.Length == 0)
            throw new InvalidOperationException("Server did not expose defaultNamingContext.");
        return (string)values[0];
    }

    // ---------------------------------------------------------------- Tree

    public AdNode GetRootNode() => new()
    {
        DistinguishedName = Required.DefaultNamingContext,
        Name = string.IsNullOrWhiteSpace(Required.DomainFqdn) ? Required.DefaultNamingContext : Required.DomainFqdn,
        Type = AdObjectType.Domain,
        HasChildren = true,
    };

    public Task<IReadOnlyList<AdNode>> GetChildrenAsync(string distinguishedName, CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<AdNode>>(() =>
        {
            using var root = Required.CreateEntry(distinguishedName);
            using var searcher = new DirectorySearcher(root)
            {
                SearchScope = SearchScope.OneLevel,
                Filter = "(|(objectClass=organizationalUnit)(objectClass=container)(objectClass=builtinDomain))",
                PageSize = 500,
            };
            searcher.PropertiesToLoad.AddRange(new[] { "name", "distinguishedName", "objectClass" });
            searcher.Sort = new SortOption("name", SortDirection.Ascending);

            var nodes = new List<AdNode>();
            using var results = searcher.FindAll();
            foreach (SearchResult r in results)
            {
                var dn = GetString(r, "distinguishedName");
                if (string.IsNullOrEmpty(dn)) continue;
                nodes.Add(new AdNode
                {
                    DistinguishedName = dn,
                    Name = GetString(r, "name"),
                    Type = AdObjectTypeExtensions.FromClasses(GetStrings(r, "objectClass")),
                    HasChildren = true,
                });
            }
            return nodes;
        }, cancellationToken);
    }

    // ---------------------------------------------------------------- List / search

    public Task<IReadOnlyList<AdObjectRow>> ListObjectsAsync(
        string baseDn, AdObjectType filter, IReadOnlyList<string> columns, bool subtree,
        CancellationToken cancellationToken = default)
    {
        var query = new SearchQuery
        {
            ObjectType = filter,
            BaseDn = baseDn,
            Scope = subtree ? SearchScope.Subtree : SearchScope.OneLevel,
        };
        return SearchAsync(query, columns, cancellationToken);
    }

    public Task<IReadOnlyList<AdObjectRow>> SearchAsync(
        SearchQuery query, IReadOnlyList<string> columns, CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<AdObjectRow>>(() =>
        {
            // Search each selected base DN under the same filter/scope and merge the results. An object
            // can fall under more than one selected base (e.g. a parent OU plus one of its children with
            // subtree scope), so dedupe by DN while preserving first-seen order.
            var bases = query.EffectiveBaseDns();
            if (bases.Count == 0) bases = new[] { Required.DefaultNamingContext };

            var filter = query.BuildFilter();
            var props = RequiredProps(columns).ToArray();

            var rows = new List<AdObjectRow>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var b in bases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var baseDn = string.IsNullOrWhiteSpace(b) ? Required.DefaultNamingContext : b;
                using var root = Required.CreateEntry(baseDn);
                using var searcher = new DirectorySearcher(root)
                {
                    SearchScope = query.Scope,
                    Filter = filter,
                    PageSize = 1000,
                    // Read only the DACL of the security descriptor (avoids needing SACL/SeSecurity privilege)
                    // so each row can report whether it's protected from accidental deletion.
                    SecurityMasks = SecurityMasks.Dacl,
                };

                foreach (var prop in props)
                    searcher.PropertiesToLoad.Add(prop);
                searcher.PropertiesToLoad.Add("ntSecurityDescriptor");

                using var results = searcher.FindAll();
                foreach (SearchResult r in results)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var dn = GetString(r, "distinguishedName");
                    if (string.IsNullOrEmpty(dn) || !seen.Add(dn)) continue;

                    var row = new AdObjectRow
                    {
                        DistinguishedName = dn,
                        Name = GetString(r, "name"),
                        Type = AdObjectTypeExtensions.FromClasses(GetStrings(r, "objectClass")),
                    };
                    if (r.Properties.Contains("userAccountControl") && r.Properties["userAccountControl"].Count > 0
                        && r.Properties["userAccountControl"][0] is int uac)
                        row.IsDisabled = AdValueFormatter.IsAccountDisabled(uac);
                    row.IsProtected = ReadDeleteProtection(r);
                    foreach (var col in columns)
                        row.Values[col] = ProjectColumn(r, col);
                    rows.Add(row);
                }
            }
            return rows;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<AdObjectRow>> SearchByNameAsync(string text, AdObjectType type, CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<AdObjectRow>>(() =>
        {
            var categoryFilter = type switch
            {
                AdObjectType.Group => "(objectCategory=group)",
                AdObjectType.Computer => "(objectCategory=computer)",
                AdObjectType.Contact => "(objectCategory=contact)",
                AdObjectType.User => "(objectCategory=person)",
                // Unknown / Any: users, groups and computers (valid group members)
                _ => "(|(objectCategory=person)(objectCategory=group)(objectCategory=computer))",
            };
            var nameMatch = string.IsNullOrWhiteSpace(text)
                ? string.Empty
                : $"(|(cn=*{LdapFilter.EscapeValue(text)}*)(sAMAccountName=*{LdapFilter.EscapeValue(text)}*)(displayName=*{LdapFilter.EscapeValue(text)}*))";
            var filter = $"(&{categoryFilter}{nameMatch})";

            using var root = Required.CreateEntry();
            using var searcher = new DirectorySearcher(root)
            {
                SearchScope = SearchScope.Subtree,
                Filter = filter,
                PageSize = 200,
                SizeLimit = 200,
            };
            searcher.PropertiesToLoad.AddRange(new[] { "cn", "name", "distinguishedName", "description", "sAMAccountName", "objectClass" });
            searcher.Sort = new SortOption("name", SortDirection.Ascending);

            var rows = new List<AdObjectRow>();
            using var results = searcher.FindAll();
            foreach (SearchResult r in results)
            {
                var dn = GetString(r, "distinguishedName");
                if (string.IsNullOrEmpty(dn)) continue;
                var row = new AdObjectRow
                {
                    DistinguishedName = dn,
                    Name = GetString(r, "name"),
                    Type = AdObjectTypeExtensions.FromClasses(GetStrings(r, "objectClass")),
                };
                row.Values["description"] = GetString(r, "description");
                row.Values["sAMAccountName"] = GetString(r, "sAMAccountName");
                rows.Add(row);
            }
            return rows;
        }, cancellationToken);
    }

    // ---------------------------------------------------------------- Load one object

    public Task<IReadOnlyList<AdAttribute>> LoadObjectAsync(string distinguishedName, CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<AdAttribute>>(() =>
        {
            using var entry = Required.CreateEntry(distinguishedName);
            using var searcher = new DirectorySearcher(entry)
            {
                SearchScope = SearchScope.Base,
                Filter = "(objectClass=*)",
            };
            searcher.PropertiesToLoad.Add("*");
            // Constructed / back-link attributes not returned by "*":
            searcher.PropertiesToLoad.Add("directReports");
            searcher.PropertiesToLoad.Add("memberOf");

            var result = searcher.FindOne() ?? throw new InvalidOperationException("Object not found: " + distinguishedName);

            var attrs = new List<AdAttribute>();
            foreach (string prop in result.Properties.PropertyNames)
            {
                if (prop.Equals("adspath", StringComparison.OrdinalIgnoreCase)) continue;

                var meta = AttributeCatalog.Meta(prop);
                var values = result.Properties[prop];
                var attr = new AdAttribute
                {
                    LdapName = prop,
                    FriendlyName = meta.Friendly,
                    IsMultiValued = meta.IsMultiValued || values.Count > 1,
                    IsDnValued = meta.IsDnValued,
                    IsReadOnly = meta.IsReadOnly,
                };

                foreach (object? v in values)
                {
                    var raw = v?.ToString() ?? string.Empty;
                    attr.RawValues.Add(raw);
                    attr.DisplayValues.Add(meta.IsDnValued ? _resolver!.Resolve(raw) : AdValueFormatter.Format(prop, v));
                }

                attr.OriginalText = values.Count > 0 ? (values[0]?.ToString() ?? string.Empty) : string.Empty;
                attr.EditText = attr.OriginalText;
                attrs.Add(attr);
            }

            return attrs.OrderBy(a => a.FriendlyName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }, cancellationToken);
    }

    public Task<ObjectBasicInfo> GetBasicInfoAsync(string distinguishedName, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            using var entry = Required.CreateEntry(distinguishedName);
            using var searcher = new DirectorySearcher(entry)
            {
                SearchScope = SearchScope.Base,
                Filter = "(objectClass=*)",
            };
            // canonicalName is a constructed attribute — it must be requested by name (a "*" load omits it).
            foreach (var p in new[] { "name", "distinguishedName", "canonicalName", "description" })
                searcher.PropertiesToLoad.Add(p);

            var result = searcher.FindOne() ?? throw new InvalidOperationException("Object not found: " + distinguishedName);
            string P(string n) => result.Properties[n].Count > 0 ? result.Properties[n][0]?.ToString() ?? string.Empty : string.Empty;

            var dn = P("distinguishedName");
            // description is multi-valued in the schema (though edited as one line) — return every value so the
            // caller can preserve the unshown ones on save.
            var descValues = result.Properties["description"].Cast<object?>()
                .Select(v => v?.ToString() ?? string.Empty)
                .Where(s => s.Length > 0)
                .ToList();
            return new ObjectBasicInfo(
                Name: P("name"),
                DistinguishedName: string.IsNullOrEmpty(dn) ? distinguishedName : dn,
                CanonicalName: P("canonicalName"),
                Description: descValues.Count > 0 ? descValues[0] : null,
                DescriptionValues: descValues);
        }, cancellationToken);
    }

    // ---------------------------------------------------------------- Writes

    public Task ApplyChangesAsync(string distinguishedName, IReadOnlyList<PendingChange> changes, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => ApplyChangesCore(distinguishedName, changes), cancellationToken);
    }

    private void ApplyChangesCore(string distinguishedName, IReadOnlyList<PendingChange> changes)
    {
        using var entry = Required.CreateEntry(distinguishedName);

        // If any change touches the DACL (accidental-deletion protection), restrict the security
        // descriptor to the DACL up front — before the entry binds — so ObjectSecurity reads/writes it.
        if (changes.Any(c => c.Op is ChangeOp.Protect or ChangeOp.Unprotect))
            entry.Options!.SecurityMasks = SecurityMasks.Dacl;

        var needCommit = false;

        foreach (var change in changes)
        {
            switch (change.Op)
            {
                case ChangeOp.Set when IsLdapInteger(change.LdapName):
                    // Integer attributes (accountExpires INTEGER8, countryCode) don't marshal cleanly via
                    // DirectoryEntry; set them through an LDAP modify using the decimal-string representation.
                    ModifyViaLdap(distinguishedName, change.LdapName,
                        change.Values.Count > 0 ? change.Values[0] : null);
                    break;

                case ChangeOp.Clear when IsLdapInteger(change.LdapName):
                    // accountExpires "never" is 0; other integer attrs are removed entirely.
                    ModifyViaLdap(distinguishedName, change.LdapName,
                        change.LdapName.Equals("accountExpires", StringComparison.OrdinalIgnoreCase) ? "0" : null);
                    break;

                case ChangeOp.Set:
                    if (change.Values.Count == 0)
                    {
                        // Empty Set == clear; skip when already absent so we don't emit a delete for a
                        // non-existent attribute (which a DC can reject with noSuchAttribute).
                        if (entry.Properties[change.LdapName].Count > 0) { entry.Properties[change.LdapName].Clear(); needCommit = true; }
                    }
                    else if (change.Values.Count == 1)
                    {
                        entry.Properties[change.LdapName].Value = change.Values[0];
                        needCommit = true;
                    }
                    else
                    {
                        entry.Properties[change.LdapName].Value = change.Values.ToArray();
                        needCommit = true;
                    }
                    break;

                case ChangeOp.Clear:
                    // Idempotent: clearing an already-absent attribute is a no-op, not a modify-delete error.
                    if (entry.Properties[change.LdapName].Count > 0) { entry.Properties[change.LdapName].Clear(); needCommit = true; }
                    break;

                case ChangeOp.Enable:
                    SetEnabled(entry, true);
                    needCommit = true;
                    break;

                case ChangeOp.Disable:
                    SetEnabled(entry, false);
                    needCommit = true;
                    break;

                case ChangeOp.AddToGroups:
                    ModifyGroupMembership(distinguishedName, change.Values, add: true);
                    break;

                case ChangeOp.RemoveFromGroups:
                    ModifyGroupMembership(distinguishedName, change.Values, add: false);
                    break;

                case ChangeOp.Protect:
                    SetDeleteProtection(entry, true);
                    needCommit = true;
                    break;

                case ChangeOp.Unprotect:
                    SetDeleteProtection(entry, false);
                    needCommit = true;
                    break;
            }
        }

        if (needCommit)
            entry.CommitChanges();

        AppLog.Instance.Info($"Applied {changes.Count} change(s) to {distinguishedName}: " +
                             string.Join("; ", changes.Select(c => c.Describe())));
    }

    /// <summary>Reads whether the object is protected from accidental deletion (Everyone:Deny Delete/DeleteTree).</summary>
    public Task<bool> GetDeletionProtectionAsync(string distinguishedName, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            using var entry = Required.CreateEntry(distinguishedName);
            // Restrict to the DACL before first access so we don't need SACL read privileges.
            entry.Options!.SecurityMasks = SecurityMasks.Dacl;
            return HasDeleteProtection(entry.ObjectSecurity);
        }, cancellationToken);
    }

    public Task DeleteObjectAsync(string distinguishedName, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            using var entry = Required.CreateEntry(distinguishedName);
            entry.DeleteTree();
            entry.CommitChanges();
            AppLog.Instance.Info($"Deleted object {distinguishedName}.");
        }, cancellationToken);
    }

    public Task<string> MoveObjectAsync(string distinguishedName, string newParentDn, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            // No-op if the object is already directly under the target (e.g. re-running a scenario on an
            // already-terminated user) — issuing the move would otherwise fail with "already exists".
            if (string.Equals(ParentDn(distinguishedName), newParentDn, StringComparison.OrdinalIgnoreCase))
                return distinguishedName;

            // A move is a modify-DN that keeps the same RDN but re-parents the object. Done over the
            // LDAP connection (ModifyDNRequest) rather than DirectoryEntry.MoveTo for predictable errors.
            var rdn = FirstRdn(distinguishedName);
            using var conn = Required.CreateLdapConnection();
            conn.SendRequest(new Protocols.ModifyDNRequest(distinguishedName, newParentDn, rdn));
            var newDn = rdn + "," + newParentDn;
            AppLog.Instance.Info($"Moved {distinguishedName} -> {newDn}.");
            return newDn;
        }, cancellationToken);
    }

    public Task ResetPasswordAsync(
        string distinguishedName, string newPassword, bool mustChangeAtNextLogon, bool unlock,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            using var entry = Required.CreateEntry(distinguishedName);
            if (!TrySetPassword(entry, newPassword, distinguishedName))
                throw new InvalidOperationException(
                    "Could not set the password over this connection. The channel must be encrypted — connect over " +
                    "LDAPS, or ensure the bind uses Kerberos sign+seal (the default for a domain-joined client).");
            // pwdLastSet = 0 forces a change at next logon; -1 stamps "now" (no forced change).
            entry.Properties["pwdLastSet"].Value = mustChangeAtNextLogon ? 0 : -1;
            entry.CommitChanges();

            if (unlock)
                ModifyViaLdap(distinguishedName, "lockoutTime", "0");

            // Password is intentionally never written to the log.
            AppLog.Instance.Info(
                $"Reset password for {distinguishedName} (mustChangeAtNextLogon={mustChangeAtNextLogon}, unlock={unlock}).");
        }, cancellationToken);
    }

    public Task UnlockAccountAsync(string distinguishedName, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            // Unlock is simply lockoutTime = 0; written via LDAP since it's an INTEGER8/LargeInteger.
            ModifyViaLdap(distinguishedName, "lockoutTime", "0");
            AppLog.Instance.Info($"Unlocked account {distinguishedName}.");
        }, cancellationToken);
    }

    public Task<UserCreateResult> CreateUserAsync(
        string ouDn, IReadOnlyDictionary<string, string> attributes, IEnumerable<string> groupDns,
        string? password, bool enabled, bool mustChangePassword,
        IReadOnlyList<string>? proxyAddresses = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (!attributes.TryGetValue("cn", out var cn) || string.IsNullOrWhiteSpace(cn))
                throw new InvalidOperationException("A common name (cn) is required to create a user.");

            using var ou = Required.CreateEntry(ouDn);
            var user = ou.Children.Add($"CN={EscapeRdn(cn)}", "user");
            try
            {
                var integerAttrs = new List<(string Key, string Value)>();
                foreach (var (key, value) in attributes)
                {
                    if (key.Equals("cn", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    if (IsLdapInteger(key)) { integerAttrs.Add((key, value)); continue; } // set via LDAP after create
                    user.Properties[key].Value = value;
                }

                // Proxy addresses (multi-valued).
                var proxies = proxyAddresses?.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct().ToArray();
                if (proxies is { Length: > 0 })
                    user.Properties["proxyAddresses"].Value = proxies;

                user.CommitChanges(); // object is created disabled

                // Integer attributes (e.g. countryCode) don't marshal via DirectoryEntry — set them via LDAP.
                var newDnEarly = user.Properties["distinguishedName"].Value?.ToString() ?? $"CN={cn},{ouDn}";
                foreach (var (key, value) in integerAttrs)
                    ModifyViaLdap(newDnEarly, key, value);

                var passwordSet = false;
                if (!string.IsNullOrEmpty(password))
                    passwordSet = TrySetPassword(user, password, newDnEarly);

                var uac = NormalAccount;
                if (!(enabled && passwordSet)) uac |= AccountDisable;
                user.Properties["userAccountControl"].Value = uac;

                if (passwordSet)
                    user.Properties["pwdLastSet"].Value = mustChangePassword ? 0 : -1;

                user.CommitChanges();

                var newDn = user.Properties["distinguishedName"].Value?.ToString() ?? $"CN={cn},{ouDn}";
                ModifyGroupMembership(newDn, groupDns.ToList(), add: true);
                var finalEnabled = enabled && passwordSet;
                AppLog.Instance.Info($"Created user {newDn} (enabled={finalEnabled}, passwordSet={passwordSet}).");
                return new UserCreateResult(newDn, passwordSet, finalEnabled);
            }
            finally
            {
                user.Dispose();
            }
        }, cancellationToken);
    }

    public Task<(string Dn, string? ProtectionError)> CreateOrganizationalUnitAsync(
        string parentDn, string name, bool protectFromDeletion, string? description,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<(string, string?)>(() =>
        {
            var ouName = name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(ouName))
                throw new InvalidOperationException("An OU name is required.");

            string newDn;
            using (var parent = Required.CreateEntry(parentDn))
            {
                var ou = parent.Children.Add($"OU={EscapeRdn(ouName)}", "organizationalUnit");
                try
                {
                    var desc = description?.Trim();
                    if (!string.IsNullOrWhiteSpace(desc)) ou.Properties["description"].Value = desc;
                    ou.CommitChanges();
                    // Read back the canonical DN; the fallback uses the escaped RDN so it stays a valid DN.
                    newDn = ou.Properties["distinguishedName"].Value?.ToString() ?? $"OU={EscapeRdn(ouName)},{parentDn}";
                }
                finally { ou.Dispose(); }
            }

            // The OU now exists. Accidental-deletion protection is a SEPARATE DACL write (reusing the shared
            // apply path) that can fail on its own — e.g. Create-Child granted but WriteDacl denied. Treat that
            // as a non-fatal warning so we never report failure over an OU that was actually created.
            string? protectionError = null;
            if (protectFromDeletion)
            {
                try { ApplyChangesCore(newDn, new[] { new PendingChange { Op = ChangeOp.Protect } }); }
                catch (Exception ex)
                {
                    protectionError = Friendly(ex);
                    AppLog.Instance.Warn($"Created OU {newDn} but could not apply accidental-deletion protection: {protectionError}");
                }
            }
            AppLog.Instance.Info($"Created OU {newDn}"
                + (protectFromDeletion && protectionError is null ? " (protected from accidental deletion)." : "."));
            return (newDn, protectionError);
        }, cancellationToken);
    }

    public async Task<BulkResult> BulkApplyAsync(
        IReadOnlyList<AdObjectRow> targets, IReadOnlyList<PendingChange> changes,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        AppLog.Instance.Info($"Bulk operation starting on {targets.Count} object(s): " +
                             string.Join("; ", changes.Select(c => c.Describe())));

        var items = new List<BulkItemResult>(targets.Count);
        var done = 0;
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await Task.Run(() => ApplyChangesCore(target.DistinguishedName, changes), cancellationToken);
                items.Add(new BulkItemResult(target.DistinguishedName, target.Name, true, null));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Instance.Warn($"Bulk change failed for {target.DistinguishedName}: {Friendly(ex)}");
                items.Add(new BulkItemResult(target.DistinguishedName, target.Name, false, Friendly(ex)));
            }
            progress?.Report(++done);
        }

        var result = new BulkResult(items);
        AppLog.Instance.Info($"Bulk operation finished: {result.SuccessCount} succeeded, {result.FailureCount} failed.");
        return result;
    }

    public Task AddMembersAsync(string groupDn, IReadOnlyList<string> memberDns, CancellationToken cancellationToken = default) =>
        Task.Run(() => ModifyMembers(groupDn, memberDns, add: true), cancellationToken);

    public Task RemoveMembersAsync(string groupDn, IReadOnlyList<string> memberDns, CancellationToken cancellationToken = default) =>
        Task.Run(() => ModifyMembers(groupDn, memberDns, add: false), cancellationToken);

    /// <summary>Adds/removes members on a single group's <c>member</c> attribute in one commit.</summary>
    private void ModifyMembers(string groupDn, IReadOnlyList<string> memberDns, bool add)
    {
        using var group = Required.CreateEntry(groupDn);
        var members = group.Properties["member"];
        var changed = 0;
        foreach (var memberDn in memberDns)
        {
            if (string.IsNullOrWhiteSpace(memberDn)) continue;
            if (add)
            {
                if (!members.Contains(memberDn)) { members.Add(memberDn); changed++; }
            }
            else
            {
                if (members.Contains(memberDn)) { members.Remove(memberDn); changed++; }
            }
        }
        if (changed > 0)
        {
            group.CommitChanges();
            AppLog.Instance.Info($"{(add ? "Added" : "Removed")} {changed} member(s) {(add ? "to" : "from")} group {groupDn}.");
        }
    }

    /// <summary>
    /// Sets a user's password. Tries ADSI <c>SetPassword</c> first (it negotiates LDAPS or the Kerberos
    /// set-password protocol). If that fails — typically because there's no LDAPS listener — it falls back to
    /// writing <c>unicodePwd</c> directly over the current connection. AD accepts a unicodePwd write on any
    /// ENCRYPTED channel, and the non-LDAPS bind here uses LDAP sign+seal (Sealing), so this succeeds without
    /// LDAPS. Returns true if either method set the password (it is never written to the log).
    /// </summary>
    private static bool TrySetPassword(DirectoryEntry user, string password, string dnForLog)
    {
        try
        {
            user.Invoke("SetPassword", new object[] { password });
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn($"ADSI SetPassword failed for {dnForLog}; falling back to unicodePwd over the sealed channel. {Friendly(ex)}");
        }

        try
        {
            // unicodePwd = the password as a UTF-16LE string wrapped in double quotes.
            var quoted = System.Text.Encoding.Unicode.GetBytes("\"" + password + "\"");
            user.Properties["unicodePwd"].Value = quoted;
            user.CommitChanges();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn($"Setting unicodePwd failed for {dnForLog} (the connection may not be encrypted). {Friendly(ex)}");
            return false;
        }
    }

    public Task<bool> ExistsAsync(string distinguishedName, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(distinguishedName)) return false;
            try
            {
                using var entry = Required.CreateEntry(distinguishedName);
                _ = entry.NativeObject; // forces the bind; throws if the object is gone
                return true;
            }
            catch { return false; }
        }, cancellationToken);

    public Task<IReadOnlySet<string>> FindExistingSamAccountNamesAsync(
        IEnumerable<string> samAccountNames, CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlySet<string>>(() =>
        {
            var wanted = samAccountNames
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == 0) return found;

            // sAMAccountName is unique across all security principals (users, computers, groups), so we don't
            // restrict by objectClass. Chunk the OR clause so a large batch can't blow past the filter-size limit.
            foreach (var chunk in wanted.Chunk(100))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var clauses = string.Concat(chunk.Select(s => $"(sAMAccountName={LdapFilter.EscapeValue(s)})"));
                using var root = Required.CreateEntry();
                using var searcher = new DirectorySearcher(root)
                {
                    SearchScope = SearchScope.Subtree,
                    Filter = $"(|{clauses})",
                    PageSize = 500,
                };
                searcher.PropertiesToLoad.Add("sAMAccountName");

                using var results = searcher.FindAll();
                foreach (SearchResult r in results)
                {
                    var sam = GetString(r, "sAMAccountName");
                    if (!string.IsNullOrEmpty(sam)) found.Add(sam);
                }
            }
            return found;
        }, cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, string>> GetGroupTypesAsync(
        IReadOnlyList<string> distinguishedNames, CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyDictionary<string, string>>(() =>
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (distinguishedNames is null || distinguishedNames.Count == 0) return map;

            // Resolve all DNs in one query from the domain root, ORing them in the filter rather than
            // round-tripping per group. Chunk the OR clause so a very large memberOf can't blow past
            // the server's filter-size limit.
            foreach (var chunk in distinguishedNames.Chunk(100))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var clauses = string.Concat(chunk.Select(dn => $"(distinguishedName={LdapFilter.EscapeValue(dn)})"));
                using var root = Required.CreateEntry();
                using var searcher = new DirectorySearcher(root)
                {
                    SearchScope = SearchScope.Subtree,
                    Filter = $"(&(objectClass=group)(|{clauses}))",
                    PageSize = 500,
                };
                searcher.PropertiesToLoad.AddRange(new[] { "distinguishedName", "groupType" });

                using var results = searcher.FindAll();
                foreach (SearchResult r in results)
                {
                    var dn = GetString(r, "distinguishedName");
                    if (string.IsNullOrEmpty(dn)) continue;
                    map[dn] = GroupTypeClassifier.Describe(GetString(r, "groupType"));
                }
            }
            return map;
        }, cancellationToken);
    }

    private void ModifyGroupMembership(string memberDn, IReadOnlyList<string> groupDns, bool add)
    {
        foreach (var groupDn in groupDns)
        {
            if (string.IsNullOrWhiteSpace(groupDn)) continue;
            using var group = Required.CreateEntry(groupDn);
            var members = group.Properties["member"];
            if (add)
            {
                if (!members.Contains(memberDn)) { members.Add(memberDn); group.CommitChanges(); }
            }
            else
            {
                if (members.Contains(memberDn)) { members.Remove(memberDn); group.CommitChanges(); }
            }
        }
    }

    /// <summary>The first (leftmost) RDN of a DN, e.g. "CN=Jane Doe,OU=Sales,DC=x" → "CN=Jane Doe". Honors backslash-escaped commas.</summary>
    public static string FirstRdn(string dn)
    {
        for (var i = 0; i < dn.Length; i++)
            if (dn[i] == ',' && (i == 0 || dn[i - 1] != '\\'))
                return dn[..i];
        return dn;
    }

    /// <summary>The parent container DN, e.g. "CN=Jane Doe,OU=Sales,DC=x" → "OU=Sales,DC=x" (empty if none).</summary>
    public static string ParentDn(string dn)
    {
        var rdn = FirstRdn(dn);
        return rdn.Length >= dn.Length ? string.Empty : dn[(rdn.Length + 1)..];
    }

    /// <summary>
    /// Integer (INTEGER/INTEGER8) attributes are written via an LDAP modify using the decimal-string
    /// representation rather than through DirectoryEntry (which can't marshal them from a string). The
    /// two names are kept explicit as a defensive fallback; the catalog covers the rest (e.g. primaryGroupID).
    /// </summary>
    private static bool IsLdapInteger(string ldapName) =>
        AttributeCatalog.IsInteger(ldapName) ||
        ldapName.Equals("accountExpires", StringComparison.OrdinalIgnoreCase) ||
        ldapName.Equals("countryCode", StringComparison.OrdinalIgnoreCase);

    /// <summary>Replaces an attribute via an LDAP ModifyRequest; a null value deletes the attribute.</summary>
    private void ModifyViaLdap(string dn, string attribute, string? value)
    {
        using var conn = Required.CreateLdapConnection();
        var mod = new Protocols.DirectoryAttributeModification
        {
            Name = attribute,
            Operation = Protocols.DirectoryAttributeOperation.Replace,
        };
        if (value is not null)
            mod.Add(value);
        conn.SendRequest(new Protocols.ModifyRequest(dn, mod));
    }

    private static void SetEnabled(DirectoryEntry entry, bool enabled)
    {
        var current = entry.Properties["userAccountControl"].Value is int i ? i : NormalAccount;
        entry.Properties["userAccountControl"].Value = enabled ? current & ~AccountDisable : current | AccountDisable;
    }

    /// <summary>
    /// Adds or removes the Everyone:Deny Delete/DeleteTree ACE that marks accidental-deletion protection.
    /// Idempotent: it strips any existing matching deny ACE first, then re-adds it only when protecting.
    /// The caller commits (so it can be batched with other edits on the same entry).
    /// </summary>
    private static void SetDeleteProtection(DirectoryEntry entry, bool protect)
    {
        var security = entry.ObjectSecurity;
        foreach (ActiveDirectoryAccessRule rule in
                 security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Deny
                && rule.IdentityReference is SecurityIdentifier sid && sid == EveryoneSid
                && (rule.ActiveDirectoryRights & DeleteRights) != 0)
                security.RemoveAccessRuleSpecific(rule);
        }
        if (protect)
            security.AddAccessRule(new ActiveDirectoryAccessRule(EveryoneSid, DeleteRights, AccessControlType.Deny));
    }

    /// <summary>Parses a search result's ntSecurityDescriptor (DACL) and reports deletion protection.</summary>
    private static bool ReadDeleteProtection(SearchResult r)
    {
        if (!r.Properties.Contains("ntSecurityDescriptor") || r.Properties["ntSecurityDescriptor"].Count == 0
            || r.Properties["ntSecurityDescriptor"][0] is not byte[] bytes || bytes.Length == 0)
            return false;
        try
        {
            var security = new ActiveDirectorySecurity();
            security.SetSecurityDescriptorBinaryForm(bytes);
            return HasDeleteProtection(security);
        }
        catch { return false; } // unreadable/foreign SD: treat as not protected rather than failing the search
    }

    /// <summary>True when Everyone is denied both Delete and DeleteTree (accumulated across explicit deny ACEs).</summary>
    private static bool HasDeleteProtection(ActiveDirectorySecurity security)
    {
        ActiveDirectoryRights denied = 0;
        foreach (ActiveDirectoryAccessRule rule in
                 security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Deny
                && rule.IdentityReference is SecurityIdentifier sid && sid == EveryoneSid)
                denied |= rule.ActiveDirectoryRights;
        }
        return (denied & DeleteRights) == DeleteRights;
    }

    // ---------------------------------------------------------------- Helpers

    /// <summary>Column projection: resolves DN-valued columns and formats typed values.</summary>
    private string ProjectColumn(SearchResult result, string ldapName)
    {
        if (!result.Properties.Contains(ldapName)) return string.Empty;
        var values = result.Properties[ldapName];
        if (values.Count == 0) return string.Empty;

        var meta = AttributeCatalog.Meta(ldapName);
        if (meta.IsDnValued)
        {
            var dns = values.Cast<object?>().Select(o => o?.ToString() ?? string.Empty).Where(s => s.Length > 0);
            return string.Join("; ", _resolver!.ResolveMany(dns));
        }
        return values.Count > 1
            ? AdValueFormatter.FormatMulti(ldapName, values.Cast<object?>())
            : AdValueFormatter.Format(ldapName, values[0]);
    }

    private static IEnumerable<string> RequiredProps(IReadOnlyList<string> columns)
    {
        var props = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "distinguishedName", "name", "objectClass", "userAccountControl",
        };
        foreach (var c in columns) props.Add(c);
        return props;
    }

    private static string GetString(SearchResult r, string prop) =>
        r.Properties.Contains(prop) && r.Properties[prop].Count > 0
            ? r.Properties[prop][0]?.ToString() ?? string.Empty
            : string.Empty;

    private static IEnumerable<string> GetStrings(SearchResult r, string prop) =>
        r.Properties.Contains(prop)
            ? r.Properties[prop].Cast<object?>().Select(o => o?.ToString() ?? string.Empty)
            : Enumerable.Empty<string>();

    private static string EscapeRdn(string value) =>
        // '\' first so the escapes we add below aren't doubled. '/' isn't an RFC 4514 DN special, but it IS the
        // ADsPath component separator, so it must be escaped for the ADSI child bind (ADSI un-escapes it back,
        // so the object is still named literally, e.g. "Sales/Marketing").
        value.Replace("\\", "\\\\").Replace(",", "\\,").Replace("+", "\\+").Replace("\"", "\\\"")
             .Replace("<", "\\<").Replace(">", "\\>").Replace(";", "\\;").Replace("=", "\\=").Replace("#", "\\#")
             .Replace("/", "\\/");

    /// <summary>Turns directory exceptions into short, user-facing messages.</summary>
    internal static string Friendly(Exception ex)
    {
        switch (ex)
        {
            case DirectoryServicesCOMException com:
                var detail = string.IsNullOrWhiteSpace(com.ExtendedErrorMessage) ? com.Message : com.ExtendedErrorMessage;
                return detail.Trim();
            case Protocols.LdapException ldap:
                return string.IsNullOrWhiteSpace(ldap.ServerErrorMessage) ? ldap.Message : ldap.ServerErrorMessage;
            default:
                return ex.Message;
        }
    }
}
