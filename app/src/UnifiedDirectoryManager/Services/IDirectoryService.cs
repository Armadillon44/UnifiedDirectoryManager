using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>Per-object outcome of a bulk operation.</summary>
public sealed record BulkItemResult(string DistinguishedName, string Name, bool Success, string? Error);

/// <summary>Aggregate result of a bulk operation.</summary>
public sealed record BulkResult(IReadOnlyList<BulkItemResult> Items)
{
    public int SuccessCount => Items.Count(i => i.Success);
    public int FailureCount => Items.Count(i => !i.Success);
}

/// <summary>
/// Outcome of creating a user: the new object's DN, whether the requested password was actually set,
/// and whether the account ended up enabled. When a password was requested but <see cref="PasswordSet"/>
/// is false (e.g. the bind isn't LDAPS/Kerberos-secured), the account is left disabled.
/// </summary>
public sealed record UserCreateResult(string DistinguishedName, bool PasswordSet, bool Enabled);

/// <summary>A member of a group: a display name plus the distinguished name (the DN is what makes a recorded
/// membership re-addable, since the pickers act on DNs).</summary>
public sealed record GroupMember(string Name, string DistinguishedName);

/// <summary>
/// A group's membership together with how far it can be trusted — necessary because LDAP cannot distinguish
/// "this group has no members" from "you may not read this group's members": in both cases the server simply
/// omits the attribute. Callers that are about to destroy the group must not treat those as the same thing.
/// </summary>
/// <param name="Members">The members read.</param>
/// <param name="Truncated">True when the read is KNOWN to be incomplete (the range walk stopped early).</param>
/// <param name="Unconfirmed">True when nothing came back and the emptiness could not be confirmed — an empty
/// group and an unreadable <c>member</c> attribute look identical on the wire.</param>
public sealed record GroupMembersResult(IReadOnlyList<GroupMember> Members, bool Truncated, bool Unconfirmed);

/// <summary>
/// Outcome of creating a group. The group EXISTS whenever this is returned — the optional error strings are
/// non-fatal failures of the separate follow-up writes (each is its own LDAP operation that can fail on its own,
/// e.g. Create-Child granted but WriteDacl denied), so callers must report the group as created and surface these
/// as warnings rather than treating them as a failed create.
/// </summary>
public sealed record GroupCreateResult(
    string DistinguishedName,
    string? ProtectionError = null,
    string? ManagedByError = null,
    string? MembersError = null)
{
    /// <summary>The follow-up steps that didn't complete, phrased for the operator; empty when all succeeded.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var list = new List<string>();
            if (ManagedByError is not null) list.Add("“Managed by” was not set: " + ManagedByError);
            if (MembersError is not null) list.Add("These members were not added: " + MembersError);
            if (ProtectionError is not null) list.Add("Accidental-deletion protection was not applied: " + ProtectionError);
            return list;
        }
    }
}

/// <summary>
/// All Active Directory I/O. Binds with explicitly-supplied credentials only (never the machine
/// context). Reads return display-ready data with friendly names; writes are committed as requested
/// by callers that have already confirmed with the user.
/// </summary>
public interface IDirectoryService
{
    ConnectionState? Current { get; }
    NameResolver? Resolver { get; }
    bool IsConnected { get; }

    Task ConnectAsync(ConnectionProfile profile, string password, CancellationToken cancellationToken = default);
    void Disconnect();

    /// <summary>Root node for the connected domain.</summary>
    AdNode GetRootNode();

    /// <summary>Container/OU children of a DN for the navigation tree (one level).</summary>
    Task<IReadOnlyList<AdNode>> GetChildrenAsync(string distinguishedName, CancellationToken cancellationToken = default);

    /// <summary>Leaf objects directly under a container, projected onto the requested columns.</summary>
    Task<IReadOnlyList<AdObjectRow>> ListObjectsAsync(
        string baseDn, AdObjectType filter, IReadOnlyList<string> columns, bool subtree,
        CancellationToken cancellationToken = default);

    /// <summary>Runs an advanced query, projecting results onto the requested columns.</summary>
    Task<IReadOnlyList<AdObjectRow>> SearchAsync(
        SearchQuery query, IReadOnlyList<string> columns, CancellationToken cancellationToken = default);

    /// <summary>Loads every populated attribute of one object (for the edit pane / attribute editor).</summary>
    Task<IReadOnlyList<AdAttribute>> LoadObjectAsync(string distinguishedName, CancellationToken cancellationToken = default);

    /// <summary>Reads an object's basic identity for a lightweight properties view: name, the LDAP DN, the
    /// canonical name (both naming formats), and description. Requests <c>canonicalName</c> explicitly since
    /// it's a constructed attribute not returned by a wildcard load.</summary>
    Task<ObjectBasicInfo> GetBasicInfoAsync(string distinguishedName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a group named <paramref name="name"/> under <paramref name="parentDn"/> (an OU or container) with the
    /// given scope + category, then applies the optional follow-ups (managed-by, initial members, accidental-deletion
    /// protection). The group is created by the first commit, so a follow-up failure is reported through
    /// <see cref="GroupCreateResult"/> rather than thrown — see that type's remarks.
    /// </summary>
    Task<GroupCreateResult> CreateGroupAsync(
        string parentDn, string name, string samAccountName, GroupScope scope, GroupCategory category,
        string? description, string? managedByDn, bool protectFromDeletion,
        IReadOnlyList<string>? initialMemberDns = null, CancellationToken cancellationToken = default);

    /// <summary>Creates an organizational unit named <paramref name="name"/> under <paramref name="parentDn"/>
    /// (a domain root or another OU), optionally setting a description and the accidental-deletion protection
    /// flag. Returns the new OU's distinguished name and, when protection was requested but couldn't be applied
    /// (a separate DACL write that can fail on its own), a non-fatal <c>ProtectionError</c> message — the OU is
    /// still created in that case.</summary>
    Task<(string Dn, string? ProtectionError)> CreateOrganizationalUnitAsync(string parentDn, string name, bool protectFromDeletion, string? description, CancellationToken cancellationToken = default);

    /// <summary>Reads whether an object is protected from accidental deletion (Everyone:Deny Delete/DeleteTree).</summary>
    Task<bool> GetDeletionProtectionAsync(string distinguishedName, CancellationToken cancellationToken = default);

    /// <summary>Searches objects of a given type by name (Unknown = users, groups and computers).</summary>
    Task<IReadOnlyList<AdObjectRow>> SearchByNameAsync(string text, AdObjectType type, CancellationToken cancellationToken = default);

    /// <summary>True if an object with the given DN currently exists (used to validate template group DNs before use).</summary>
    Task<bool> ExistsAsync(string distinguishedName, CancellationToken cancellationToken = default);

    /// <summary>Returns the subset of the given sAMAccountNames that already exist in the directory (matched
    /// case-insensitively, across any object type since the logon-name namespace is domain-wide), so callers
    /// can reject duplicate logon names before attempting to create. Runs as one chunked query.</summary>
    Task<IReadOnlySet<string>> FindExistingSamAccountNamesAsync(IEnumerable<string> samAccountNames, CancellationToken cancellationToken = default);

    /// <summary>Resolves a set of group DNs to a friendly classification (e.g. "Security · Global",
    /// "Distribution · Universal") read from each group's <c>groupType</c> bitmask. Returns a DN→kind map;
    /// DNs that aren't found / aren't groups are simply absent. Used for the Member Of "Type" column.</summary>
    Task<IReadOnlyDictionary<string, string>> GetGroupTypesAsync(IReadOnlyList<string> distinguishedNames, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a group's member list, following AD's range retrieval. A single attribute read returns at most ~1500
    /// values and renames the property to <c>member;range=0-1499</c>, so reading <c>member</c> from
    /// <see cref="LoadObjectAsync"/> silently yields NOTHING for a group larger than that — this walks the ranges
    /// instead and is correct for a group of any size. Names are taken from each DN's RDN rather than resolved
    /// individually, so reading a large group costs one search per ~1500 members instead of a bind per member.
    /// The result reports whether it is complete — see <see cref="GroupMembersResult"/>.
    /// </summary>
    Task<GroupMembersResult> GetGroupMembersAsync(string groupDn, CancellationToken cancellationToken = default);

    /// <summary>Adds members (any object DNs) to a group's <c>member</c> attribute.</summary>
    Task AddMembersAsync(string groupDn, IReadOnlyList<string> memberDns, CancellationToken cancellationToken = default);

    /// <summary>Removes members from a group's <c>member</c> attribute.</summary>
    Task RemoveMembersAsync(string groupDn, IReadOnlyList<string> memberDns, CancellationToken cancellationToken = default);

    // --- Writes (callers confirm first) ---

    Task ApplyChangesAsync(string distinguishedName, IReadOnlyList<PendingChange> changes, CancellationToken cancellationToken = default);

    /// <summary>Permanently deletes an object (and any descendants).</summary>
    Task DeleteObjectAsync(string distinguishedName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves an object to another OU/container (keeps its RDN). Returns the object's new DN.
    /// </summary>
    Task<string> MoveObjectAsync(string distinguishedName, string newParentDn, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a new password for a user via ADSI <c>SetPassword</c>, sets <c>pwdLastSet</c> to require (or
    /// not) a change at next logon, and optionally unlocks the account. The password is never logged.
    /// </summary>
    Task ResetPasswordAsync(
        string distinguishedName, string newPassword, bool mustChangeAtNextLogon, bool unlock,
        CancellationToken cancellationToken = default);

    /// <summary>Unlocks a locked-out account by writing <c>lockoutTime = 0</c>.</summary>
    Task UnlockAccountAsync(string distinguishedName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a user and returns the outcome. If <paramref name="password"/> is supplied but can't be set
    /// (typically because the connection isn't LDAPS/Kerberos-secured), the account is created disabled and the
    /// returned <see cref="UserCreateResult.PasswordSet"/> is false — callers must surface this, not assume success.
    /// </summary>
    Task<UserCreateResult> CreateUserAsync(
        string ouDn, IReadOnlyDictionary<string, string> attributes, IEnumerable<string> groupDns,
        string? password, bool enabled, bool mustChangePassword,
        IReadOnlyList<string>? proxyAddresses = null, CancellationToken cancellationToken = default);

    Task<BulkResult> BulkApplyAsync(
        IReadOnlyList<AdObjectRow> targets, IReadOnlyList<PendingChange> changes,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default);
}
