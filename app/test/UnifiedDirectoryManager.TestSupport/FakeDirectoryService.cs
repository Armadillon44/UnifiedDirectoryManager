using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.TestSupport;

/// <summary>
/// A controllable stand-in for <see cref="IDirectoryService"/>.
///
/// It exists for one reason: the interesting failures in this app are about TIMING — which object a write
/// lands on when two reads overlap — and timing cannot be tested against a real directory, because a real
/// directory answers whenever it likes. Here the test decides exactly when each read completes.
///
/// Almost every member throws. That is deliberate: a test that accidentally reaches an unstubbed call
/// should fail loudly rather than quietly receive a default and assert something meaningless.
/// </summary>
public sealed class FakeDirectoryService : IDirectoryService
{
    private readonly Dictionary<string, TaskCompletionSource<IReadOnlyList<AdAttribute>>> _pendingLoads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every <see cref="ApplyChangesAsync"/> call, in order — the record of what was actually written.</summary>
    public List<(string Dn, IReadOnlyList<PendingChange> Changes)> Writes { get; } = new();

    /// <summary>Distinguished names passed to <see cref="LoadObjectAsync"/>, in order.</summary>
    public List<string> Loads { get; } = new();

    /// <summary>Attributes handed back per DN once its load is released. Missing DN yields an empty set.</summary>
    public Dictionary<string, IReadOnlyList<AdAttribute>> Objects { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Deletion-protection answers per DN, for the DACL read at the end of a load.</summary>
    public Dictionary<string, bool> Protection { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When true, a load parks until <see cref="ReleaseLoad"/> is called for that DN. This is the whole
    /// point of the class — it is what lets a test hold one load open while another finishes.
    /// </summary>
    public bool HoldLoads { get; set; }

    /// <summary>Completes a parked load. Returns false when nothing was waiting on that DN.</summary>
    public bool ReleaseLoad(string distinguishedName)
    {
        if (!_pendingLoads.Remove(distinguishedName, out var tcs)) return false;
        Objects.TryGetValue(distinguishedName, out var attrs);
        tcs.SetResult(attrs ?? Array.Empty<AdAttribute>());
        return true;
    }

    /// <summary>Fails a parked load, so the error path can be exercised too.</summary>
    public bool FailLoad(string distinguishedName, string message = "the directory said no")
    {
        if (!_pendingLoads.Remove(distinguishedName, out var tcs)) return false;
        tcs.SetException(new InvalidOperationException(message));
        return true;
    }

    /// <summary>Convenience for building an attribute the pane will display.</summary>
    public static AdAttribute Attr(string ldapName, string value, string? friendly = null) =>
        new() { LdapName = ldapName, FriendlyName = friendly ?? ldapName, RawValues = { value }, DisplayValues = { value } };

    // --- the members a pane load actually touches -------------------------------------------------

    public Task<IReadOnlyList<AdAttribute>> LoadObjectAsync(string distinguishedName, CancellationToken cancellationToken = default)
    {
        Loads.Add(distinguishedName);
        if (!HoldLoads)
        {
            Objects.TryGetValue(distinguishedName, out var ready);
            return Task.FromResult(ready ?? (IReadOnlyList<AdAttribute>)Array.Empty<AdAttribute>());
        }
        var tcs = new TaskCompletionSource<IReadOnlyList<AdAttribute>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingLoads[distinguishedName] = tcs;
        return tcs.Task;
    }

    public Task<bool> GetDeletionProtectionAsync(string distinguishedName, CancellationToken cancellationToken = default) =>
        Task.FromResult(Protection.TryGetValue(distinguishedName, out var p) && p);

    public Task<GroupMembersResult> GetGroupMembersAsync(string groupDn, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GroupMembersResult(Array.Empty<GroupMember>(), Truncated: false, Unconfirmed: false));

    public Task ApplyChangesAsync(string distinguishedName, IReadOnlyList<PendingChange> changes, CancellationToken cancellationToken = default)
    {
        Writes.Add((distinguishedName, changes));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>> GetGroupTypesAsync(IReadOnlyList<string> distinguishedNames, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

    // --- everything else: reaching these means the test wandered off ------------------------------

    private static Exception Unused([System.Runtime.CompilerServices.CallerMemberName] string? member = null) =>
        new NotSupportedException($"FakeDirectoryService.{member} was called but is not stubbed. "
                                  + "Stub it deliberately rather than letting a test lean on a default.");

    public ConnectionState? Current => null;
    public NameResolver? Resolver => null;
    public bool IsConnected => true;

    public Task ConnectAsync(ConnectionProfile profile, string password, CancellationToken cancellationToken = default) => throw Unused();
    public void Disconnect() => throw Unused();
    public AdNode GetRootNode() => throw Unused();
    public Task<IReadOnlyList<AdNode>> GetChildrenAsync(string distinguishedName, CancellationToken cancellationToken = default) => throw Unused();
    public Task<IReadOnlyList<AdObjectRow>> ListObjectsAsync(string baseDn, AdObjectType filter, IReadOnlyList<string> columns, bool subtree, CancellationToken cancellationToken = default) => throw Unused();
    public Task<IReadOnlyList<AdObjectRow>> SearchAsync(SearchQuery query, IReadOnlyList<string> columns, CancellationToken cancellationToken = default) => throw Unused();
    public Task<ObjectBasicInfo> GetBasicInfoAsync(string distinguishedName, CancellationToken cancellationToken = default) => throw Unused();
    public Task<GroupCreateResult> CreateGroupAsync(string parentDn, string name, string samAccountName, GroupScope scope, GroupCategory category, string? description, string? managedByDn, bool protectFromDeletion, IReadOnlyList<string>? initialMemberDns = null, CancellationToken cancellationToken = default) => throw Unused();
    public Task<(string Dn, string? ProtectionError)> CreateOrganizationalUnitAsync(string parentDn, string name, bool protectFromDeletion, string? description, CancellationToken cancellationToken = default) => throw Unused();
    public Task<IReadOnlyList<AdObjectRow>> SearchByNameAsync(string text, AdObjectType type, CancellationToken cancellationToken = default) => throw Unused();
    public Task<bool> ExistsAsync(string distinguishedName, CancellationToken cancellationToken = default) => throw Unused();
    public Task<IReadOnlySet<string>> FindExistingSamAccountNamesAsync(IEnumerable<string> samAccountNames, CancellationToken cancellationToken = default) => throw Unused();
    public Task AddMembersAsync(string groupDn, IReadOnlyList<string> memberDns, CancellationToken cancellationToken = default) => throw Unused();
    public Task RemoveMembersAsync(string groupDn, IReadOnlyList<string> memberDns, CancellationToken cancellationToken = default) => throw Unused();
    public Task DeleteObjectAsync(string distinguishedName, CancellationToken cancellationToken = default) => throw Unused();
    public Task<string> MoveObjectAsync(string distinguishedName, string newParentDn, CancellationToken cancellationToken = default) => throw Unused();
    public Task ResetPasswordAsync(string distinguishedName, string newPassword, bool mustChangeAtNextLogon, bool unlock, CancellationToken cancellationToken = default) => throw Unused();
    public Task UnlockAccountAsync(string distinguishedName, CancellationToken cancellationToken = default) => throw Unused();
    public Task<UserCreateResult> CreateUserAsync(string ouDn, IReadOnlyDictionary<string, string> attributes, IEnumerable<string> groupDns, string? password, bool enabled, bool mustChangePassword, IReadOnlyList<string>? proxyAddresses = null, CancellationToken cancellationToken = default) => throw Unused();
    public Task<BulkResult> BulkApplyAsync(IReadOnlyList<AdObjectRow> targets, IReadOnlyList<PendingChange> changes, IProgress<int>? progress = null, CancellationToken cancellationToken = default) => throw Unused();
}
