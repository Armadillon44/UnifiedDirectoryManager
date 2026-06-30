using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Read access to Entra ID (Microsoft 365) via Microsoft Graph, using interactive (delegated)
/// admin sign-in. The first cloud capability is read-only; writes are deliberately out of scope.
/// Configuration (tenant + client id) is supplied by the user and persisted in app settings;
/// no secret is used (public client + PKCE).
/// </summary>
public interface IGraphService
{
    /// <summary>True once a tenant id and client id have been supplied (sign-in is possible).</summary>
    bool IsConfigured { get; }

    /// <summary>True once an admin has signed in (an authentication record is held).</summary>
    bool IsSignedIn { get; }

    /// <summary>UPN of the signed-in admin, for display; null when not signed in.</summary>
    string? SignedInAccount { get; }

    /// <summary>Sets the tenant/client id and (re)builds the credential + Graph client.</summary>
    void Configure(string tenantId, string clientId);

    /// <summary>Interactively signs the admin in (opens the system browser) and records the account.</summary>
    Task SignInAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets the signed-in admin (clears the persisted authentication record).</summary>
    void SignOut();

    /// <summary>
    /// Reads a user's cloud identity by UPN. Returns null if no such user exists in the tenant
    /// (e.g. an on-prem account that isn't synced). Throws on auth/permission/transport errors.
    /// </summary>
    Task<CloudUserInfo?> GetUserByUpnAsync(string upn, CancellationToken cancellationToken = default);

    /// <summary>Searches Entra ID groups by display name (empty text returns the first page of groups).</summary>
    Task<IReadOnlyList<CloudGroup>> SearchGroupsAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Lists the direct members of a group (users, groups, devices, etc.).</summary>
    Task<IReadOnlyList<CloudMember>> GetGroupMembersAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>Lists a user's cloud group memberships (direct memberOf), by UPN or object id.</summary>
    Task<IReadOnlyList<CloudGroup>> GetUserGroupsByUpnAsync(string upnOrId, CancellationToken cancellationToken = default);

    /// <summary>Lists a directory object's group memberships (direct memberOf) — works for users, devices, and groups.</summary>
    Task<IReadOnlyList<CloudGroup>> GetObjectMemberOfAsync(string objectId, CloudObjectKind kind, CancellationToken cancellationToken = default);

    /// <summary>True if a group with the given Entra object id still exists (used to validate template cloud groups).</summary>
    Task<bool> GroupExistsAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the tenant's subscribed license SKUs (with prepaid/consumed counts), each tagged with any
    /// groups that grant it via group-based licensing — so the UI can steer admins toward group membership.
    /// </summary>
    Task<IReadOnlyList<CloudSku>> GetSubscribedSkusAsync(CancellationToken cancellationToken = default);

    // --- Paged browsing (tree-driven cloud lists) ---

    /// <summary>One page of users (optionally name-filtered). Pass a prior page's NextLink for the next page.</summary>
    Task<CloudPage> ListUsersAsync(string? search, string? nextLink, CancellationToken cancellationToken = default);

    /// <summary>One page of groups (optionally name-filtered).</summary>
    Task<CloudPage> ListGroupsAsync(string? search, string? nextLink, CancellationToken cancellationToken = default);

    /// <summary>One page of devices (optionally name-filtered).</summary>
    Task<CloudPage> ListDevicesAsync(string? search, string? nextLink, CancellationToken cancellationToken = default);

    /// <summary>One page of a group's direct members (users, groups, devices, …) as list rows.</summary>
    Task<CloudPage> GetGroupMembersPageAsync(string groupId, string? nextLink, CancellationToken cancellationToken = default);

    // --- On-prem ↔ cloud correlation (synced-object Cloud tab) ---

    /// <summary>Finds the synced cloud group for an on-prem group by its objectSid; null if not synced/found.</summary>
    Task<CloudGroup?> GetGroupByOnPremSidAsync(string onPremSid, CancellationToken cancellationToken = default);

    /// <summary>Finds Entra device(s) matching an on-prem computer (by display name; may return several).</summary>
    Task<IReadOnlyList<CloudDevice>> GetDevicesByComputerAsync(string computerName, string? onPremSid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the full, grouped property set of a cloud object (user/group/device) for the details pane —
    /// far more than the list columns, to surface fields normally only visible via PowerShell.
    /// </summary>
    Task<IReadOnlyList<CloudPropertySection>> GetObjectDetailAsync(string id, CloudObjectKind kind, CancellationToken cancellationToken = default);

    // --- Writes (callers confirm first) ---

    /// <summary>Adds a directory object (by its Entra object id) as a member of a cloud group.</summary>
    Task AddMemberToGroupAsync(string groupId, string memberObjectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a directory object (by its Entra object id) from a cloud group. Note: membership of a
    /// group synced from on-prem AD is mastered on-prem — Graph rejects removing such a member.
    /// </summary>
    Task RemoveMemberFromGroupAsync(string groupId, string memberObjectId, CancellationToken cancellationToken = default);

    /// <summary>Enables or disables a cloud user account (accountEnabled).</summary>
    Task SetUserAccountEnabledAsync(string userId, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Revokes a cloud user's sign-in sessions (invalidates refresh tokens; forces re-auth).</summary>
    Task RevokeSignInSessionsAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>PATCHes the given editable user properties (key→value; empty value clears the property).</summary>
    Task UpdateUserAsync(string userId, IReadOnlyDictionary<string, string?> changes, CancellationToken cancellationToken = default);

    /// <summary>PATCHes the given editable group properties (key→value; empty value clears the property).</summary>
    Task UpdateGroupAsync(string groupId, IReadOnlyDictionary<string, string?> changes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Directly assigns a license SKU to a user. Prefer group-based licensing where it exists; this is the
    /// direct-assignment path. Fails if the user has no usageLocation or no units are available.
    /// </summary>
    Task AssignLicenseToUserAsync(string userId, Guid skuId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a directly-assigned license SKU from a user. A group-inherited license can't be removed this
    /// way — Graph rejects it; the user must be removed from the assigning group instead.
    /// </summary>
    Task RemoveLicenseFromUserAsync(string userId, Guid skuId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues a Temporary Access Pass (a time-limited passcode) on a cloud user — used to bootstrap passwordless
    /// onboarding (registering Authenticator / Windows Hello). The returned pass code is visible only here and can
    /// never be read back, so the caller must surface it immediately. Replaces any existing usable TAP on the user.
    /// Requires the TAP method to be enabled for the user in the tenant's Authentication Methods policy.
    /// </summary>
    /// <param name="lifetimeMinutes">Validity in minutes; Graph requires 10–43200 (up to 30 days).</param>
    /// <param name="isUsableOnce">If true, the pass works once; if false, repeatedly within its lifetime (multi-use must be enabled in policy).</param>
    Task<TemporaryAccessPassResult> CreateTemporaryAccessPassAsync(string userId, int lifetimeMinutes, bool isUsableOnce, CancellationToken cancellationToken = default);
}
