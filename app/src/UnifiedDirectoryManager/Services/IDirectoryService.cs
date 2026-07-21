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
