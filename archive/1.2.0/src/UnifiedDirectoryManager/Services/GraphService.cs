using System.IO;
using Azure.Core;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Microsoft Graph implementation of <see cref="IGraphService"/>. Authenticates the admin
/// interactively via <see cref="InteractiveBrowserCredential"/> (system browser), with a
/// DPAPI-backed token cache and a persisted <see cref="AuthenticationRecord"/> so the admin
/// isn't re-prompted every launch. Reads only; never logs tokens.
/// </summary>
public sealed class GraphService : IGraphService
{
    // Fully-qualified delegated scopes (unambiguous for both AuthenticateAsync and the Graph client).
    private static readonly string[] Scopes =
    {
        "https://graph.microsoft.com/User.ReadWrite.All",        // read + edit + disable/enable + revoke sessions
        "https://graph.microsoft.com/Organization.Read.All",
        "https://graph.microsoft.com/Group.ReadWrite.All",       // read + edit cloud groups + manage membership
        "https://graph.microsoft.com/Device.Read.All",
        "https://graph.microsoft.com/UserAuthenticationMethod.ReadWrite.All", // issue a Temporary Access Pass
    };

    private const int PageSize = 100;

    // Named, DPAPI-encrypted MSAL token cache shared across launches.
    private const string TokenCacheName = "UnifiedDirectoryManager.Graph";

    private static readonly string AuthRecordPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UnifiedDirectoryManager", "graph-auth.bin");

    private string? _tenantId;
    private string? _clientId;
    private InteractiveBrowserCredential? _credential;
    private GraphServiceClient? _graph;
    private AuthenticationRecord? _record;

    // skuId -> friendly SKU part number (e.g. ENTERPRISEPACK), loaded once per session.
    private Dictionary<Guid, string>? _skuMap;
    private readonly SemaphoreSlim _skuGate = new(1, 1);

    // groupId -> display name, resolved on demand (for group-based license sources). Cleared on sign-out/reconfig.
    private readonly Dictionary<string, string?> _groupNameCache = new();

    // skuId -> display names of groups that assign that SKU (group-based licensing), loaded once per session.
    private Dictionary<Guid, List<string>>? _licenseGroupMap;
    private readonly SemaphoreSlim _licenseGroupGate = new(1, 1);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_tenantId) && !string.IsNullOrWhiteSpace(_clientId);
    public bool IsSignedIn => _record is not null;
    public string? SignedInAccount => _record?.Username;

    public void Configure(string tenantId, string clientId)
    {
        _tenantId = tenantId?.Trim();
        _clientId = clientId?.Trim();
        _skuMap = null; // a different tenant has a different SKU catalogue
        _licenseGroupMap = null;
        _groupNameCache.Clear();

        if (!IsConfigured)
        {
            _credential = null;
            _graph = null;
            return;
        }

        // Reuse any persisted authentication record so a cached token can be used silently.
        _record ??= TryLoadAuthRecord();

        var options = new InteractiveBrowserCredentialOptions
        {
            TenantId = _tenantId,
            ClientId = _clientId,
            RedirectUri = new Uri("http://localhost"),
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = TokenCacheName },
            AuthenticationRecord = _record,
        };

        _credential = new InteractiveBrowserCredential(options);
        _graph = new GraphServiceClient(_credential, Scopes);
        AppLog.Instance.Info($"Graph client configured for tenant '{_tenantId}'.");
    }

    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || _credential is null)
            throw new InvalidOperationException("Enter a tenant ID and client ID before signing in.");

        // Opens the system browser; returns the account record once consent/sign-in completes.
        _record = await _credential.AuthenticateAsync(new TokenRequestContext(Scopes), cancellationToken);
        TrySaveAuthRecord(_record);
        AppLog.Instance.Info($"Signed in to Entra ID as '{_record.Username}'.");
    }

    public void SignOut()
    {
        _record = null;
        try { if (File.Exists(AuthRecordPath)) File.Delete(AuthRecordPath); }
        catch (Exception ex) { AppLog.Instance.Warn("Could not clear the saved Graph sign-in: " + ex.Message); }

        // Rebuild the credential without an authentication record so cached tokens aren't reused.
        if (IsConfigured) Configure(_tenantId!, _clientId!);
        AppLog.Instance.Info("Signed out of Entra ID.");
    }

    public async Task<CloudUserInfo?> GetUserByUpnAsync(string upn, CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        if (string.IsNullOrWhiteSpace(upn)) return null;

        User? user;
        try
        {
            user = await _graph.Users[upn].GetAsync(rc =>
            {
                rc.QueryParameters.Select = new[]
                {
                    "id", "displayName", "userPrincipalName", "accountEnabled",
                    "onPremisesSyncEnabled", "userType", "usageLocation",
                    "createdDateTime", "assignedLicenses", "licenseAssignmentStates",
                };
            }, cancellationToken);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            return null; // no cloud account for this UPN (e.g. not synced)
        }

        if (user is null) return null;

        var licenses = await ResolveLicensesAsync(user, cancellationToken);
        var groups = await GetUserGroupsAsync(user.Id ?? upn, cancellationToken);
        return new CloudUserInfo(
            user.Id ?? string.Empty,
            user.DisplayName,
            user.UserPrincipalName,
            user.AccountEnabled,
            user.OnPremisesSyncEnabled,
            user.UserType,
            user.UsageLocation,
            user.CreatedDateTime,
            licenses,
            groups);
    }

    public async Task<IReadOnlyList<CloudGroup>> SearchGroupsAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        var search = EscapeSearchPhrase(text);

        var resp = await _graph.Groups.GetAsync(rc =>
        {
            rc.QueryParameters.Select = GroupSelect;
            rc.QueryParameters.Top = 50;
            if (!string.IsNullOrEmpty(search))
            {
                rc.QueryParameters.Search = $"\"displayName:{search}\"";
                rc.QueryParameters.Count = true;
                rc.Headers.Add("ConsistencyLevel", "eventual"); // required for $search on directory objects
            }
        }, cancellationToken);

        return (resp?.Value ?? new List<Group>())
            .Select(ToCloudGroup)
            .OrderBy(g => g.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<CloudMember>> GetGroupMembersAsync(string groupId, CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        if (string.IsNullOrWhiteSpace(groupId)) return Array.Empty<CloudMember>();

        var resp = await _graph.Groups[groupId].Members.GetAsync(rc => rc.QueryParameters.Top = 200, cancellationToken);
        return (resp?.Value ?? new List<DirectoryObject>())
            .Select(ToMember)
            .OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // --- Paged browsing (tree-driven cloud lists) ---

    public async Task<CloudPage> ListUsersAsync(string? search, string? nextLink, CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        UserCollectionResponse? resp;
        if (!string.IsNullOrEmpty(nextLink))
            resp = await _graph.Users.WithUrl(nextLink).GetAsync(rc => rc.Headers.Add("ConsistencyLevel", "eventual"), cancellationToken);
        else
            resp = await _graph.Users.GetAsync(rc =>
            {
                rc.QueryParameters.Select = UserListSelect;
                rc.QueryParameters.Top = PageSize;
                rc.QueryParameters.Orderby = new[] { "displayName" };
                rc.QueryParameters.Count = true;
                rc.Headers.Add("ConsistencyLevel", "eventual");
                var s = EscapeSearchPhrase(search);
                if (s.Length > 0)
                    rc.QueryParameters.Search = $"\"displayName:{s}\" OR \"userPrincipalName:{s}\" OR \"mail:{s}\"";
            }, cancellationToken);

        return new CloudPage((resp?.Value ?? new List<User>()).Select(ToUserRow).ToList(), resp?.OdataNextLink);
    }

    public async Task<CloudPage> ListGroupsAsync(string? search, string? nextLink, CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        GroupCollectionResponse? resp;
        if (!string.IsNullOrEmpty(nextLink))
            resp = await _graph.Groups.WithUrl(nextLink).GetAsync(rc => rc.Headers.Add("ConsistencyLevel", "eventual"), cancellationToken);
        else
            resp = await _graph.Groups.GetAsync(rc =>
            {
                rc.QueryParameters.Select = GroupSelect;
                rc.QueryParameters.Top = PageSize;
                rc.QueryParameters.Orderby = new[] { "displayName" };
                rc.QueryParameters.Count = true;
                rc.Headers.Add("ConsistencyLevel", "eventual");
                var s = EscapeSearchPhrase(search);
                if (s.Length > 0)
                    rc.QueryParameters.Search = $"\"displayName:{s}\" OR \"mail:{s}\"";
            }, cancellationToken);

        return new CloudPage((resp?.Value ?? new List<Group>()).Select(ToGroupRow).ToList(), resp?.OdataNextLink);
    }

    public async Task<CloudPage> ListDevicesAsync(string? search, string? nextLink, CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        DeviceCollectionResponse? resp;
        if (!string.IsNullOrEmpty(nextLink))
            resp = await _graph.Devices.WithUrl(nextLink).GetAsync(rc => rc.Headers.Add("ConsistencyLevel", "eventual"), cancellationToken);
        else
            resp = await _graph.Devices.GetAsync(rc =>
            {
                rc.QueryParameters.Select = DeviceSelect;
                rc.QueryParameters.Top = PageSize;
                rc.QueryParameters.Orderby = new[] { "displayName" };
                rc.QueryParameters.Count = true;
                rc.Headers.Add("ConsistencyLevel", "eventual");
                var s = EscapeSearchPhrase(search);
                if (s.Length > 0)
                    rc.QueryParameters.Search = $"\"displayName:{s}\"";
            }, cancellationToken);

        return new CloudPage((resp?.Value ?? new List<Device>()).Select(ToDeviceRow).ToList(), resp?.OdataNextLink);
    }

    public async Task<CloudPage> GetGroupMembersPageAsync(string groupId, string? nextLink, CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        if (string.IsNullOrWhiteSpace(groupId)) return new CloudPage(Array.Empty<CloudObjectRow>(), null);

        DirectoryObjectCollectionResponse? resp;
        if (!string.IsNullOrEmpty(nextLink))
            resp = await _graph.Groups[groupId].Members.WithUrl(nextLink).GetAsync(cancellationToken: cancellationToken);
        else
            resp = await _graph.Groups[groupId].Members.GetAsync(rc => rc.QueryParameters.Top = PageSize, cancellationToken);

        return new CloudPage((resp?.Value ?? new List<DirectoryObject>()).Select(ToMemberRow).ToList(), resp?.OdataNextLink);
    }

    // --- On-prem ↔ cloud correlation (synced-object Cloud tab) ---

    public async Task<CloudGroup?> GetGroupByOnPremSidAsync(string onPremSid, CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        if (string.IsNullOrWhiteSpace(onPremSid)) return null;

        var resp = await _graph.Groups.GetAsync(rc =>
        {
            rc.QueryParameters.Select = GroupSelect;
            rc.QueryParameters.Filter = $"onPremisesSecurityIdentifier eq '{EscapeFilter(onPremSid.Trim())}'";
            rc.QueryParameters.Top = 1;
        }, cancellationToken);

        var g = resp?.Value?.FirstOrDefault();
        return g is null ? null : ToCloudGroup(g);
    }

    public async Task<IReadOnlyList<CloudDevice>> GetDevicesByComputerAsync(string computerName, string? onPremSid, CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        var name = (computerName ?? string.Empty).Trim();
        if (name.Length == 0) return Array.Empty<CloudDevice>();

        // Hybrid-joined devices usually aren't Entra-Connect-synced, so the practical correlation is by
        // display name (the device displayName equals the computer name). May match >1 (stale records).
        var resp = await _graph.Devices.GetAsync(rc =>
        {
            rc.QueryParameters.Select = DeviceSelect;
            rc.QueryParameters.Filter = $"displayName eq '{EscapeFilter(name)}'";
            rc.QueryParameters.Top = 25;
        }, cancellationToken);

        return (resp?.Value ?? new List<Device>())
            .Select(ToCloudDevice)
            .OrderByDescending(d => d.ApproximateLastSignInDateTime ?? DateTimeOffset.MinValue)
            .ToList();
    }

    // --- Comprehensive object details (grouped) for the properties pane ---

    public async Task<IReadOnlyList<CloudPropertySection>> GetObjectDetailAsync(string id, CloudObjectKind kind, CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        if (string.IsNullOrWhiteSpace(id)) return Array.Empty<CloudPropertySection>();
        return kind switch
        {
            CloudObjectKind.User => await BuildUserDetailAsync(id, cancellationToken),
            CloudObjectKind.Group => await BuildGroupDetailAsync(id, cancellationToken),
            CloudObjectKind.Device => await BuildDeviceDetailAsync(id, cancellationToken),
            _ => Array.Empty<CloudPropertySection>(),
        };
    }

    // --- Writes (callers confirm first) ---

    public async Task AddMemberToGroupAsync(string groupId, string memberObjectId, CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        var body = new ReferenceCreate { OdataId = $"https://graph.microsoft.com/v1.0/directoryObjects/{memberObjectId}" };
        await _graph.Groups[groupId].Members.Ref.PostAsync(body, cancellationToken: cancellationToken);
        AppLog.Instance.Info($"Added member {memberObjectId} to cloud group {groupId}.");
    }

    public async Task RemoveMemberFromGroupAsync(string groupId, string memberObjectId, CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        await _graph.Groups[groupId].Members[memberObjectId].Ref.DeleteAsync(cancellationToken: cancellationToken);
        AppLog.Instance.Info($"Removed member {memberObjectId} from cloud group {groupId}.");
    }

    public async Task SetUserAccountEnabledAsync(string userId, bool enabled, CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        await _graph.Users[userId].PatchAsync(new User { AccountEnabled = enabled }, cancellationToken: cancellationToken);
        AppLog.Instance.Info($"Set accountEnabled={enabled} on cloud user {userId}.");
    }

    public async Task RevokeSignInSessionsAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        await _graph.Users[userId].RevokeSignInSessions.PostAsRevokeSignInSessionsPostResponseAsync(cancellationToken: cancellationToken);
        AppLog.Instance.Info($"Revoked sign-in sessions for cloud user {userId}.");
    }

    public async Task UpdateUserAsync(string userId, IReadOnlyDictionary<string, string?> changes, CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        var body = new User();
        foreach (var (key, raw) in changes)
        {
            var v = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
            switch (key)
            {
                case "displayName": body.DisplayName = v; break;
                case "givenName": body.GivenName = v; break;
                case "surname": body.Surname = v; break;
                case "jobTitle": body.JobTitle = v; break;
                case "department": body.Department = v; break;
                case "companyName": body.CompanyName = v; break;
                case "employeeId": body.EmployeeId = v; break;
                case "employeeType": body.EmployeeType = v; break;
                case "officeLocation": body.OfficeLocation = v; break;
                case "streetAddress": body.StreetAddress = v; break;
                case "city": body.City = v; break;
                case "state": body.State = v; break;
                case "postalCode": body.PostalCode = v; break;
                case "country": body.Country = v; break;
                case "mobilePhone": body.MobilePhone = v; break;
                case "faxNumber": body.FaxNumber = v; break;
                case "usageLocation": body.UsageLocation = v; break;
                // unknown / non-writable keys are ignored (defense in depth — the UI only sends editable ones)
            }
        }
        await _graph.Users[userId].PatchAsync(body, cancellationToken: cancellationToken);
        AppLog.Instance.Info($"Updated cloud user {userId}: {string.Join(", ", changes.Keys)}.");
    }

    public async Task UpdateGroupAsync(string groupId, IReadOnlyDictionary<string, string?> changes, CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        var body = new Group();
        foreach (var (key, raw) in changes)
        {
            var v = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
            switch (key)
            {
                case "displayName": body.DisplayName = v; break;
                case "description": body.Description = v; break;
                case "mailNickname": body.MailNickname = v; break;
            }
        }
        await _graph.Groups[groupId].PatchAsync(body, cancellationToken: cancellationToken);
        AppLog.Instance.Info($"Updated cloud group {groupId}: {string.Join(", ", changes.Keys)}.");
    }

    public async Task AssignLicenseToUserAsync(string userId, Guid skuId, CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        var body = new Microsoft.Graph.Users.Item.AssignLicense.AssignLicensePostRequestBody
        {
            AddLicenses = new List<AssignedLicense> { new() { SkuId = skuId, DisabledPlans = new List<Guid?>() } },
            RemoveLicenses = new List<Guid?>(),
        };
        await _graph.Users[userId].AssignLicense.PostAsync(body, cancellationToken: cancellationToken);
        AppLog.Instance.Info($"Assigned license {skuId} to cloud user {userId}.");
    }

    public async Task RemoveLicenseFromUserAsync(string userId, Guid skuId, CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        var body = new Microsoft.Graph.Users.Item.AssignLicense.AssignLicensePostRequestBody
        {
            AddLicenses = new List<AssignedLicense>(),
            RemoveLicenses = new List<Guid?> { skuId },
        };
        await _graph.Users[userId].AssignLicense.PostAsync(body, cancellationToken: cancellationToken);
        AppLog.Instance.Info($"Removed license {skuId} from cloud user {userId}.");
    }

    public async Task<TemporaryAccessPassResult> CreateTemporaryAccessPassAsync(string userId, int lifetimeMinutes, bool isUsableOnce, CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        // Graph requires lifetimeInMinutes between 10 and 43200 (30 days); a user can hold only one usable TAP,
        // so this replaces any existing one. The pass code comes back populated ONLY on this create call.
        var body = new TemporaryAccessPassAuthenticationMethod
        {
            LifetimeInMinutes = lifetimeMinutes,
            IsUsableOnce = isUsableOnce,
        };
        var created = await _graph.Users[userId].Authentication.TemporaryAccessPassMethods.PostAsync(body, cancellationToken: cancellationToken);
        // Never log the pass itself — only the (non-secret) parameters.
        AppLog.Instance.Info($"Issued a Temporary Access Pass for cloud user {userId} (lifetime {lifetimeMinutes}m, one-time={isUsableOnce}).");
        return new TemporaryAccessPassResult(
            created?.TemporaryAccessPass ?? string.Empty,
            created?.StartDateTime,
            created?.LifetimeInMinutes,
            created?.IsUsableOnce ?? isUsableOnce);
    }

    private static readonly string[] UserDetailSelect =
    {
        "id", "userPrincipalName", "displayName", "givenName", "surname", "mailNickname", "userType",
        "accountEnabled", "createdDateTime", "lastPasswordChangeDateTime", "creationType", "externalUserState", "ageGroup",
        "onPremisesSyncEnabled", "onPremisesSamAccountName", "onPremisesUserPrincipalName", "onPremisesSecurityIdentifier",
        "onPremisesImmutableId", "onPremisesDomainName", "onPremisesDistinguishedName", "onPremisesLastSyncDateTime",
        "jobTitle", "department", "companyName", "employeeId", "employeeType", "employeeHireDate",
        "mail", "otherMails", "proxyAddresses", "businessPhones", "mobilePhone", "faxNumber", "imAddresses",
        "streetAddress", "city", "state", "postalCode", "country", "officeLocation", "usageLocation", "preferredLanguage",
    };

    private async Task<IReadOnlyList<CloudPropertySection>> BuildUserDetailAsync(string id, CancellationToken ct)
    {
        var u = await _graph!.Users[id].GetAsync(rc => rc.QueryParameters.Select = UserDetailSelect, ct);
        if (u is null) return Array.Empty<CloudPropertySection>();

        string? manager = null;
        try
        {
            var m = await _graph.Users[id].Manager.GetAsync(rc =>
                rc.QueryParameters.Select = new[] { "displayName", "userPrincipalName" }, ct);
            if (m is User mu)
                manager = mu.DisplayName + (string.IsNullOrEmpty(mu.UserPrincipalName) ? string.Empty : $" ({mu.UserPrincipalName})");
        }
        catch { /* no manager / not readable */ }

        var isSynced = u.OnPremisesSyncEnabled == true;
        CloudProperty P(string key, string label, string? value) => CloudPropertyRules.Make(CloudObjectKind.User, isSynced, key, label, value);

        return new List<CloudPropertySection>
        {
            Section("Identity",
                P("id", "Object ID", u.Id), P("userPrincipalName", "User principal name", u.UserPrincipalName),
                P("displayName", "Display name", u.DisplayName), P("givenName", "First name", u.GivenName),
                P("surname", "Last name", u.Surname), P("mailNickname", "Mail nickname", u.MailNickname),
                P("userType", "User type", u.UserType)),
            Section("Account",
                P("accountEnabled", "Enabled", YesNo(u.AccountEnabled)), P("createdDateTime", "Created", LocalDate(u.CreatedDateTime)),
                P("lastPasswordChangeDateTime", "Last password change", LocalDate(u.LastPasswordChangeDateTime)),
                P("creationType", "Creation type", u.CreationType), P("externalUserState", "External state", u.ExternalUserState),
                P("ageGroup", "Age group", u.AgeGroup)),
            Section("On-premises sync",
                P("onPremisesSyncEnabled", "Synced from on-prem", u.OnPremisesSyncEnabled == true ? "Yes" : "No"),
                P("onPremisesSamAccountName", "SAM account name", u.OnPremisesSamAccountName),
                P("onPremisesUserPrincipalName", "On-prem UPN", u.OnPremisesUserPrincipalName),
                P("onPremisesSecurityIdentifier", "On-prem SID", u.OnPremisesSecurityIdentifier),
                P("onPremisesImmutableId", "Immutable ID", u.OnPremisesImmutableId),
                P("onPremisesDomainName", "On-prem domain", u.OnPremisesDomainName),
                P("onPremisesDistinguishedName", "On-prem DN", u.OnPremisesDistinguishedName),
                P("onPremisesLastSyncDateTime", "Last on-prem sync", LocalDate(u.OnPremisesLastSyncDateTime))),
            Section("Organization",
                P("jobTitle", "Job title", u.JobTitle), P("department", "Department", u.Department),
                P("companyName", "Company", u.CompanyName), P("employeeId", "Employee ID", u.EmployeeId),
                P("employeeType", "Employee type", u.EmployeeType), P("employeeHireDate", "Hire date", LocalDate(u.EmployeeHireDate)),
                P("manager", "Manager", manager)),
            Section("Contact",
                P("mail", "Mail", u.Mail), P("otherMails", "Other mails", Join(u.OtherMails)),
                P("proxyAddresses", "Proxy addresses", Join(u.ProxyAddresses)),
                P("businessPhones", "Business phones", Join(u.BusinessPhones)), P("mobilePhone", "Mobile", u.MobilePhone),
                P("faxNumber", "Fax", u.FaxNumber), P("imAddresses", "IM addresses", Join(u.ImAddresses)),
                P("streetAddress", "Street", u.StreetAddress), P("city", "City", u.City), P("state", "State", u.State),
                P("postalCode", "Postal code", u.PostalCode), P("country", "Country", u.Country),
                P("officeLocation", "Office", u.OfficeLocation), P("usageLocation", "Usage location", u.UsageLocation),
                P("preferredLanguage", "Preferred language", u.PreferredLanguage)),
        };
    }

    private static readonly string[] GroupDetailSelect =
    {
        "id", "displayName", "description", "mailNickname", "visibility", "classification",
        "securityEnabled", "mailEnabled", "groupTypes", "isAssignableToRole", "membershipRule", "membershipRuleProcessingState",
        "resourceProvisioningOptions", "mail", "proxyAddresses",
        "onPremisesSyncEnabled", "onPremisesSamAccountName", "onPremisesSecurityIdentifier", "onPremisesDomainName",
        "onPremisesNetBiosName", "onPremisesLastSyncDateTime", "createdDateTime", "renewedDateTime", "expirationDateTime",
    };

    private async Task<IReadOnlyList<CloudPropertySection>> BuildGroupDetailAsync(string id, CancellationToken ct)
    {
        var g = await _graph!.Groups[id].GetAsync(rc => rc.QueryParameters.Select = GroupDetailSelect, ct);
        if (g is null) return Array.Empty<CloudPropertySection>();
        var cg = ToCloudGroup(g);

        var isSynced = g.OnPremisesSyncEnabled == true;
        CloudProperty P(string key, string label, string? value) => CloudPropertyRules.Make(CloudObjectKind.Group, isSynced, key, label, value);

        return new List<CloudPropertySection>
        {
            Section("Identity",
                P("id", "Object ID", g.Id), P("displayName", "Display name", g.DisplayName),
                P("description", "Description", g.Description), P("mailNickname", "Mail nickname", g.MailNickname),
                P("visibility", "Visibility", g.Visibility), P("classification", "Classification", g.Classification)),
            Section("Type",
                P("groupType", "Group type", cg.GroupKind), P("teams", "Teams-enabled", cg.IsTeam ? "Yes" : "No"),
                P("securityEnabled", "Security-enabled", YesNo(g.SecurityEnabled)), P("mailEnabled", "Mail-enabled", YesNo(g.MailEnabled)),
                P("groupTypes", "Group types", Join(g.GroupTypes)), P("isAssignableToRole", "Assignable to role", YesNo(g.IsAssignableToRole)),
                P("membershipRule", "Membership rule", g.MembershipRule), P("membershipRuleProcessingState", "Rule processing", g.MembershipRuleProcessingState)),
            Section("Mail",
                P("mail", "Mail", g.Mail), P("proxyAddresses", "Proxy addresses", Join(g.ProxyAddresses))),
            Section("On-premises",
                P("origin", "Origin", cg.Origin), P("onPremisesSamAccountName", "SAM account name", g.OnPremisesSamAccountName),
                P("onPremisesSecurityIdentifier", "On-prem SID", g.OnPremisesSecurityIdentifier),
                P("onPremisesDomainName", "On-prem domain", g.OnPremisesDomainName),
                P("onPremisesNetBiosName", "NetBIOS name", g.OnPremisesNetBiosName),
                P("onPremisesLastSyncDateTime", "Last on-prem sync", LocalDate(g.OnPremisesLastSyncDateTime))),
            Section("Dates",
                P("createdDateTime", "Created", LocalDate(g.CreatedDateTime)), P("renewedDateTime", "Renewed", LocalDate(g.RenewedDateTime)),
                P("expirationDateTime", "Expiration", LocalDate(g.ExpirationDateTime))),
        };
    }

    private static readonly string[] DeviceDetailSelect =
    {
        "id", "deviceId", "displayName", "operatingSystem", "operatingSystemVersion", "trustType", "isManaged",
        "managementType", "isCompliant", "isRooted", "deviceOwnership", "enrollmentType", "registrationDateTime",
        "accountEnabled", "approximateLastSignInDateTime", "onPremisesSyncEnabled", "onPremisesLastSyncDateTime",
        "manufacturer", "model",
    };

    private async Task<IReadOnlyList<CloudPropertySection>> BuildDeviceDetailAsync(string id, CancellationToken ct)
    {
        var d = await _graph!.Devices[id].GetAsync(rc => rc.QueryParameters.Select = DeviceDetailSelect, ct);
        if (d is null) return Array.Empty<CloudPropertySection>();

        // Devices have nothing cloud-editable in this SDK — every row classifies as read-only.
        CloudProperty P(string key, string label, string? value) => CloudPropertyRules.Make(CloudObjectKind.Device, isSynced: true, key, label, value);

        return new List<CloudPropertySection>
        {
            Section("Identity", P("id", "Object ID", d.Id), P("deviceId", "Device ID", d.DeviceId), P("displayName", "Display name", d.DisplayName)),
            Section("Operating system", P("operatingSystem", "OS", d.OperatingSystem), P("operatingSystemVersion", "OS version", d.OperatingSystemVersion)),
            Section("Join / management",
                P("trustType", "Join type", TrustTypeFriendly(d.TrustType)), P("isManaged", "Managed", YesNo(d.IsManaged)),
                P("managementType", "Management type", d.ManagementType), P("isCompliant", "Compliant", YesNo(d.IsCompliant)),
                P("isRooted", "Rooted / jailbroken", YesNo(d.IsRooted)), P("deviceOwnership", "Ownership", d.DeviceOwnership),
                P("enrollmentType", "Enrollment type", d.EnrollmentType), P("registrationDateTime", "Registered", LocalDate(d.RegistrationDateTime))),
            Section("State",
                P("accountEnabled", "Enabled", YesNo(d.AccountEnabled)), P("approximateLastSignInDateTime", "Approx. last sign-in", LocalDate(d.ApproximateLastSignInDateTime)),
                P("onPremisesSyncEnabled", "Synced from on-prem", d.OnPremisesSyncEnabled == true ? "Yes" : "No"),
                P("onPremisesLastSyncDateTime", "Last on-prem sync", LocalDate(d.OnPremisesLastSyncDateTime))),
            Section("Hardware", P("manufacturer", "Manufacturer", d.Manufacturer), P("model", "Model", d.Model)),
        };
    }

    private static CloudPropertySection Section(string title, params CloudProperty[] props) => new(title, props);
    private static string Join(IEnumerable<string>? values) => values is null ? string.Empty : string.Join(", ", values);

    public Task<IReadOnlyList<CloudGroup>> GetUserGroupsByUpnAsync(string upnOrId, CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        return GetUserGroupsAsync(upnOrId, cancellationToken);
    }

    public async Task<IReadOnlyList<CloudGroup>> GetObjectMemberOfAsync(string objectId, CloudObjectKind kind, CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        if (string.IsNullOrWhiteSpace(objectId)) return Array.Empty<CloudGroup>();
        try
        {
            // Cast memberOf to groups so the group-only $select (Teams/Dynamic/Synced flags) applies — the
            // untyped directoryObject collection omits resourceProvisioningOptions/membershipRule.
            var resp = kind switch
            {
                CloudObjectKind.Device => await _graph.Devices[objectId].MemberOf.GraphGroup.GetAsync(rc => { rc.QueryParameters.Select = GroupSelect; rc.QueryParameters.Top = 200; }, cancellationToken),
                CloudObjectKind.Group => await _graph.Groups[objectId].MemberOf.GraphGroup.GetAsync(rc => { rc.QueryParameters.Select = GroupSelect; rc.QueryParameters.Top = 200; }, cancellationToken),
                _ => await _graph.Users[objectId].MemberOf.GraphGroup.GetAsync(rc => { rc.QueryParameters.Select = GroupSelect; rc.QueryParameters.Top = 200; }, cancellationToken),
            };
            return (resp?.Value ?? new List<Group>())
                .Select(ToCloudGroup)
                .OrderBy(g => g.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("Could not read object group memberships: " + ex.Message);
            return Array.Empty<CloudGroup>();
        }
    }

    public async Task<bool> GroupExistsAsync(string groupId, CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        if (string.IsNullOrWhiteSpace(groupId)) return false;
        try
        {
            var g = await _graph.Groups[groupId].GetAsync(rc => rc.QueryParameters.Select = new[] { "id" }, cancellationToken);
            return g is not null;
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            return false; // the group has been deleted
        }
    }

    /// <summary>Reads the user's cloud group memberships (direct memberOf, groups only).</summary>
    private async Task<IReadOnlyList<CloudGroup>> GetUserGroupsAsync(string idOrUpn, CancellationToken cancellationToken)
    {
        try
        {
            // Cast memberOf to groups so we can $select group-only properties — resourceProvisioningOptions
            // (Teams), membershipRule (Dynamic), onPremisesSyncEnabled (Synced) aren't returned on the
            // untyped directoryObject collection, which would leave Teams groups unidentified.
            var resp = await _graph!.Users[idOrUpn].MemberOf.GraphGroup.GetAsync(rc =>
            {
                rc.QueryParameters.Select = GroupSelect;
                rc.QueryParameters.Top = 200;
            }, cancellationToken);
            return (resp?.Value ?? new List<Group>())
                .Select(ToCloudGroup)
                .OrderBy(g => g.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("Could not read cloud group memberships: " + ex.Message);
            return Array.Empty<CloudGroup>();
        }
    }

    /// <summary>Builds the user's license list with friendly + native names and the assignment source.</summary>
    private async Task<IReadOnlyList<CloudLicense>> ResolveLicensesAsync(User user, CancellationToken cancellationToken)
    {
        var map = await EnsureSkuMapAsync(cancellationToken);

        string Part(Guid? skuId) =>
            skuId is { } id && map.TryGetValue(id, out var part) ? part : skuId?.ToString() ?? "(unknown SKU)";

        // Prefer licenseAssignmentStates (carries the assignment source); fall back to assignedLicenses.
        var states = user.LicenseAssignmentStates;
        if (states is { Count: > 0 })
        {
            var bySku = states.Where(s => s.SkuId is not null).GroupBy(s => s.SkuId!.Value);
            var list = new List<CloudLicense>();
            foreach (var grp in bySku)
            {
                var sources = new List<string>();
                bool hasDirect = false, hasGroup = false;
                foreach (var s in grp)
                {
                    if (string.IsNullOrEmpty(s.AssignedByGroup))
                    {
                        hasDirect = true;
                        if (!sources.Contains("Direct")) sources.Add("Direct");
                    }
                    else
                    {
                        hasGroup = true;
                        var name = await GetGroupNameAsync(s.AssignedByGroup!, cancellationToken) ?? s.AssignedByGroup!;
                        var label = $"Group: {name}";
                        if (!sources.Contains(label)) sources.Add(label);
                    }
                }
                var part = Part(grp.Key);
                list.Add(new CloudLicense(grp.Key, LicenseCatalog.Friendly(part), part,
                    sources.Count > 0 ? string.Join(", ", sources) : "Direct",
                    HasDirect: hasDirect || sources.Count == 0, HasGroup: hasGroup));
            }
            return list.OrderBy(l => l.FriendlyName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        var assigned = user.AssignedLicenses;
        if (assigned is { Count: > 0 })
        {
            return assigned
                .Select(a => (Id: a.SkuId ?? Guid.Empty, Part: Part(a.SkuId)))
                .Select(x => new CloudLicense(x.Id, LicenseCatalog.Friendly(x.Part), x.Part, "Direct", HasDirect: true, HasGroup: false))
                .OrderBy(l => l.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        return Array.Empty<CloudLicense>();
    }

    /// <summary>Resolves a group's display name (cached); null if it can't be read.</summary>
    private async Task<string?> GetGroupNameAsync(string groupId, CancellationToken cancellationToken)
    {
        if (_groupNameCache.TryGetValue(groupId, out var cached)) return cached;
        string? name = null;
        try
        {
            var g = await _graph!.Groups[groupId].GetAsync(rc => rc.QueryParameters.Select = new[] { "displayName" }, cancellationToken);
            name = g?.DisplayName;
        }
        catch (Exception ex) { AppLog.Instance.Warn($"Could not resolve group name for {groupId}: {ex.Message}"); }
        _groupNameCache[groupId] = name;
        return name;
    }

    private static readonly string[] GroupSelect =
    {
        "id", "displayName", "description", "mail", "groupTypes", "securityEnabled", "mailEnabled",
        "membershipRule", "resourceProvisioningOptions", "onPremisesSyncEnabled", "onPremisesSecurityIdentifier", "visibility",
    };

    private static CloudGroup ToCloudGroup(Group g) => new(
        g.Id ?? string.Empty,
        g.DisplayName ?? "(no name)",
        g.Description,
        g.Mail,
        ClassifyGroupKind(g),
        !string.IsNullOrEmpty(g.MembershipRule) ? "Dynamic" : "Assigned",
        g.OnPremisesSyncEnabled == true ? "Synced" : "Cloud-only",
        g.ResourceProvisioningOptions?.Any(o => string.Equals(o, "Team", StringComparison.OrdinalIgnoreCase)) == true);

    private static string ClassifyGroupKind(Group g)
    {
        if (g.GroupTypes?.Any(t => string.Equals(t, "Unified", StringComparison.OrdinalIgnoreCase)) == true)
            return "Microsoft 365";
        var sec = g.SecurityEnabled == true;
        var mail = g.MailEnabled == true;
        if (sec && mail) return "Mail-enabled security";
        if (sec) return "Security";
        if (mail) return "Distribution";
        return "Group";
    }

    private static CloudMember ToMember(DirectoryObject o) => o switch
    {
        User u => new(u.Id ?? string.Empty, u.DisplayName ?? u.UserPrincipalName ?? "(user)", u.UserPrincipalName, "User"),
        Group g => new(g.Id ?? string.Empty, g.DisplayName ?? "(group)", null, "Group"),
        Device d => new(d.Id ?? string.Empty, d.DisplayName ?? "(device)", null, "Device"),
        ServicePrincipal sp => new(sp.Id ?? string.Empty, sp.DisplayName ?? "(service principal)", null, "Service principal"),
        OrgContact c => new(c.Id ?? string.Empty, c.DisplayName ?? "(contact)", c.Mail, "Contact"),
        _ => new(o.Id ?? string.Empty, (o.OdataType ?? "object").Replace("#microsoft.graph.", string.Empty), null, "Other"),
    };

    // --- List-row projections + formatting (keys match CloudColumnCatalog) ---

    private static readonly string[] UserListSelect =
    {
        "id", "displayName", "userPrincipalName", "mail", "accountEnabled", "onPremisesSyncEnabled",
        "jobTitle", "department", "usageLocation", "userType", "createdDateTime",
    };

    private static readonly string[] DeviceSelect =
    {
        "id", "deviceId", "displayName", "operatingSystem", "operatingSystemVersion", "trustType",
        "isCompliant", "isManaged", "accountEnabled", "approximateLastSignInDateTime", "onPremisesSyncEnabled",
    };

    private static CloudObjectRow ToUserRow(User u)
    {
        var row = new CloudObjectRow
        {
            Id = u.Id ?? string.Empty,
            DisplayName = u.DisplayName ?? u.UserPrincipalName ?? "(user)",
            Kind = CloudObjectKind.User,
        };
        row.Values["userPrincipalName"] = u.UserPrincipalName ?? string.Empty;
        row.Values["id"] = u.Id ?? string.Empty;
        row.Values["mail"] = u.Mail ?? string.Empty;
        row.Values["accountEnabled"] = YesNo(u.AccountEnabled);
        row.Values["onPremisesSyncEnabled"] = u.OnPremisesSyncEnabled == true ? "Synced" : "Cloud-only";
        row.Values["jobTitle"] = u.JobTitle ?? string.Empty;
        row.Values["department"] = u.Department ?? string.Empty;
        row.Values["usageLocation"] = u.UsageLocation ?? string.Empty;
        row.Values["userType"] = u.UserType ?? string.Empty;
        row.Values["createdDateTime"] = LocalDate(u.CreatedDateTime);
        return row;
    }

    private static CloudObjectRow ToGroupRow(Group g)
    {
        var cg = ToCloudGroup(g);
        var row = new CloudObjectRow { Id = cg.Id, DisplayName = cg.DisplayName, Kind = CloudObjectKind.Group };
        row.Values["id"] = cg.Id;
        row.Values["groupType"] = cg.GroupKind;
        row.Values["origin"] = cg.Origin;
        row.Values["membership"] = cg.MembershipKind;
        row.Values["teams"] = cg.IsTeam ? "Yes" : "No";
        row.Values["mail"] = cg.Mail ?? string.Empty;
        row.Values["visibility"] = g.Visibility ?? string.Empty;
        row.Values["description"] = cg.Description ?? string.Empty;
        return row;
    }

    private static CloudObjectRow ToDeviceRow(Device d)
    {
        var row = new CloudObjectRow
        {
            Id = d.Id ?? string.Empty,
            DisplayName = d.DisplayName ?? "(device)",
            Kind = CloudObjectKind.Device,
        };
        row.Values["id"] = d.Id ?? string.Empty;
        row.Values["operatingSystem"] = d.OperatingSystem ?? string.Empty;
        row.Values["operatingSystemVersion"] = d.OperatingSystemVersion ?? string.Empty;
        row.Values["trustType"] = TrustTypeFriendly(d.TrustType);
        row.Values["isCompliant"] = YesNo(d.IsCompliant);
        row.Values["isManaged"] = YesNo(d.IsManaged);
        row.Values["accountEnabled"] = YesNo(d.AccountEnabled);
        row.Values["approximateLastSignInDateTime"] = LocalDate(d.ApproximateLastSignInDateTime);
        return row;
    }

    private static CloudObjectRow ToMemberRow(DirectoryObject o)
    {
        var m = ToMember(o);
        var kind = m.ObjectType switch
        {
            "User" => CloudObjectKind.User,
            "Group" => CloudObjectKind.Group,
            "Device" => CloudObjectKind.Device,
            _ => CloudObjectKind.Other,
        };
        var row = new CloudObjectRow { Id = m.Id, DisplayName = m.DisplayName, Kind = kind };
        row.Values["id"] = m.Id;
        row.Values["type"] = m.ObjectType;
        row.Values["userPrincipalName"] = m.Upn ?? string.Empty;
        return row;
    }

    private static CloudDevice ToCloudDevice(Device d) => new(
        d.Id ?? string.Empty, d.DeviceId, d.DisplayName, d.OperatingSystem, d.OperatingSystemVersion,
        d.TrustType, d.IsCompliant, d.IsManaged, d.AccountEnabled, d.ApproximateLastSignInDateTime, d.OnPremisesSyncEnabled);

    /// <summary>
    /// Escapes a user-supplied value for safe inclusion INSIDE a quoted Graph <c>$search</c> phrase.
    /// Per the $search docs, a clause is wrapped in double quotes and any embedded backslash or double-quote
    /// must be backslash-escaped (backslash first, then quote). The field prefix (e.g. <c>displayName:</c>) is
    /// always a code-controlled literal — never user input — so the escaped value cannot change which property is
    /// searched or break out of the quoted phrase to inject AND/OR operators.
    /// </summary>
    private static string EscapeSearchPhrase(string? search) =>
        (search ?? string.Empty).Trim().Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string EscapeFilter(string value) => value.Replace("'", "''");
    private static string YesNo(bool? b) => b == true ? "Yes" : b == false ? "No" : string.Empty;
    private static string LocalDate(DateTimeOffset? d) => d is { } v ? v.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : string.Empty;

    private static string TrustTypeFriendly(string? t) => t switch
    {
        "ServerAd" => "Hybrid (ServerAd)",
        "AzureAd" => "Entra joined",
        "Workplace" => "Registered",
        null or "" => string.Empty,
        _ => t,
    };

    /// <summary>Loads (once) the tenant's subscribed SKUs as a skuId→partNumber map; empty on failure.</summary>
    private async Task<Dictionary<Guid, string>> EnsureSkuMapAsync(CancellationToken cancellationToken)
    {
        if (_skuMap is not null) return _skuMap;
        await _skuGate.WaitAsync(cancellationToken);
        try
        {
            if (_skuMap is not null) return _skuMap;
            var map = new Dictionary<Guid, string>();
            try
            {
                var skus = await _graph!.SubscribedSkus.GetAsync(cancellationToken: cancellationToken);
                foreach (var sku in skus?.Value ?? new List<SubscribedSku>())
                {
                    if (sku.SkuId is { } id && !string.IsNullOrEmpty(sku.SkuPartNumber))
                        map[id] = sku.SkuPartNumber!;
                }
            }
            catch (Exception ex)
            {
                // Degrade gracefully: licenses will show raw GUIDs rather than failing the lookup.
                AppLog.Instance.Warn("Could not read subscribed SKUs (licenses will show GUIDs): " + ex.Message);
            }
            return _skuMap = map;
        }
        finally { _skuGate.Release(); }
    }

    public async Task<IReadOnlyList<CloudSku>> GetSubscribedSkusAsync(CancellationToken cancellationToken = default)
    {
        if (_graph is null) throw new InvalidOperationException("Sign in to Entra ID first.");
        var byGroup = await EnsureLicenseGroupMapAsync(cancellationToken);

        var resp = await _graph.SubscribedSkus.GetAsync(cancellationToken: cancellationToken);
        var list = new List<CloudSku>();
        foreach (var sku in resp?.Value ?? new List<SubscribedSku>())
        {
            if (sku.SkuId is not { } id || string.IsNullOrEmpty(sku.SkuPartNumber)) continue;
            // Only count tenant-available SKUs (status "Enabled" / "Warning"); skip "Suspended"/"Deleted".
            var capability = sku.CapabilityStatus ?? "Enabled";
            if (string.Equals(capability, "Suspended", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(capability, "Deleted", StringComparison.OrdinalIgnoreCase))
                continue;

            var part = sku.SkuPartNumber!;
            var groups = byGroup.TryGetValue(id, out var g) ? (IReadOnlyList<string>)g : Array.Empty<string>();
            list.Add(new CloudSku(id, part, LicenseCatalog.Friendly(part),
                sku.PrepaidUnits?.Enabled ?? 0, sku.ConsumedUnits ?? 0, groups));
        }
        return list.OrderBy(s => s.FriendlyName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Builds (once per session) a skuId→assigning-group-names map by scanning groups that carry assigned
    /// licenses. There's no server-side filter for this, so it pages all groups selecting only
    /// id/displayName/assignedLicenses; the set of license-bearing groups is normally small.
    /// </summary>
    private async Task<Dictionary<Guid, List<string>>> EnsureLicenseGroupMapAsync(CancellationToken cancellationToken)
    {
        if (_licenseGroupMap is not null) return _licenseGroupMap;
        await _licenseGroupGate.WaitAsync(cancellationToken);
        try
        {
            if (_licenseGroupMap is not null) return _licenseGroupMap;
            var map = new Dictionary<Guid, List<string>>();
            try
            {
                int scanned = 0;
                var page = await _graph!.Groups.GetAsync(rc =>
                {
                    rc.QueryParameters.Select = new[] { "id", "displayName", "assignedLicenses" };
                    rc.QueryParameters.Top = 999;
                }, cancellationToken);

                while (page is not null)
                {
                    foreach (var grp in page.Value ?? new List<Group>())
                    {
                        scanned++;
                        if (grp.AssignedLicenses is not { Count: > 0 }) continue;
                        var name = grp.DisplayName ?? grp.Id ?? "(group)";
                        foreach (var al in grp.AssignedLicenses)
                            if (al.SkuId is { } sku)
                            {
                                if (!map.TryGetValue(sku, out var names)) map[sku] = names = new List<string>();
                                if (!names.Contains(name)) names.Add(name);
                            }
                    }
                    if (string.IsNullOrEmpty(page.OdataNextLink)) break;
                    page = await _graph.Groups.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
                }
                AppLog.Instance.Info($"Scanned {scanned} group(s); {map.Count} SKU(s) are group-assigned.");
            }
            catch (Exception ex)
            {
                // Degrade gracefully: without this map the group-first nudge is simply skipped.
                AppLog.Instance.Warn("Could not scan groups for license assignments: " + ex.Message);
            }
            return _licenseGroupMap = map;
        }
        finally { _licenseGroupGate.Release(); }
    }

    private static AuthenticationRecord? TryLoadAuthRecord()
    {
        try
        {
            if (!File.Exists(AuthRecordPath)) return null;
            using var stream = File.OpenRead(AuthRecordPath);
            return AuthenticationRecord.Deserialize(stream);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("Could not read the saved Graph sign-in: " + ex.Message);
            return null;
        }
    }

    private static void TrySaveAuthRecord(AuthenticationRecord record)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AuthRecordPath)!);
            using var stream = File.Create(AuthRecordPath);
            record.Serialize(stream);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("Could not save the Graph sign-in for next time: " + ex.Message);
        }
    }
}
