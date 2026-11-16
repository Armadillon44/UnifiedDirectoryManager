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

    /// <summary>Per-DN password options; absent means neither option is set.</summary>
    public Dictionary<string, PasswordOptions> PasswordOptionsByDn { get; } = new(StringComparer.OrdinalIgnoreCase);

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

    public Task<PasswordOptions> GetPasswordOptionsAsync(string distinguishedName, CancellationToken cancellationToken = default) =>
        Task.FromResult(PasswordOptionsByDn.TryGetValue(distinguishedName, out var o) ? o : new PasswordOptions(false, false));

    public Task<GroupMembersResult> GetGroupMembersAsync(string groupDn, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GroupMembersResult(
            GroupMembers.TryGetValue(groupDn, out var members) ? members : Array.Empty<GroupMember>(),
            Truncated: false, Unconfirmed: false));

    public Task ApplyChangesAsync(string distinguishedName, IReadOnlyList<PendingChange> changes, CancellationToken cancellationToken = default)
    {
        Writes.Add((distinguishedName, changes));
        // A write naming a DN in FailWritesFor -- as its target, or as a group it adds to or removes from --
        // fails, so a test can check that one failed change does not stop the rest.
        Exception? refused = null;
        foreach (var dn in changes.SelectMany(c => c.Values).Prepend(distinguishedName))
            if (FailWritesFor.Contains(dn)) { refused = new InvalidOperationException($"the directory refused {dn}"); break; }

        if (HoldWrites)
        {
            var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingWrites.Add((parked, refused));
            return parked.Task;
        }
        if (refused is not null) throw refused;
        if (StoreWrites) Store(distinguishedName, changes);
        return Task.CompletedTask;
    }

    /// <summary>
    /// When true, a write that succeeds also changes the stored object in <see cref="Objects"/>, as AD would, so a
    /// later read sees it. Off by default: most suites check what was sent, not what a read-back would show.
    /// </summary>
    public bool StoreWrites { get; set; }

    /// <summary>Applies a write's Set and Clear changes to the stored object; other kinds of change are ignored.</summary>
    private void Store(string distinguishedName, IReadOnlyList<PendingChange> changes)
    {
        if (!Objects.TryGetValue(distinguishedName, out var stored)) return;
        var attrs = stored.ToList();
        foreach (var c in changes.Where(c => c.Op is ChangeOp.Set or ChangeOp.Clear))
        {
            var existing = attrs.FirstOrDefault(a => string.Equals(a.LdapName, c.LdapName, StringComparison.OrdinalIgnoreCase));
            if (c.Op == ChangeOp.Clear || c.Values.Count == 0)
            {
                if (existing is not null) attrs.Remove(existing);
                continue;
            }
            var target = existing ?? new AdAttribute
            {
                LdapName = c.LdapName,
                FriendlyName = string.IsNullOrEmpty(c.FriendlyName) ? c.LdapName : c.FriendlyName,
                IsMultiValued = c.Values.Count > 1,
            };
            target.RawValues.Clear();
            target.DisplayValues.Clear();
            foreach (var v in c.Values) { target.RawValues.Add(v); target.DisplayValues.Add(v); }
            if (existing is null) attrs.Add(target);
        }
        Objects[distinguishedName] = attrs.ToArray();
    }

    /// <summary>DNs whose writes fail. See <see cref="ApplyChangesAsync"/>.</summary>
    public HashSet<string> FailWritesFor { get; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<(TaskCompletionSource Done, Exception? Refused)> _pendingWrites = new();

    /// <summary>
    /// When true, an <see cref="ApplyChangesAsync"/> write parks until <see cref="ReleaseWrite"/>. That lets a test
    /// act while a Save is still writing, as an operator can. The write is still recorded at once, and on release
    /// it still fails if <see cref="FailWritesFor"/> names it.
    /// </summary>
    public bool HoldWrites { get; set; }

    /// <summary>How many writes are parked right now.</summary>
    public int PendingWrites => _pendingWrites.Count;

    /// <summary>Completes the oldest parked write. Returns false when none is waiting.</summary>
    public bool ReleaseWrite()
    {
        if (_pendingWrites.Count == 0) return false;
        var (done, refused) = _pendingWrites[0];
        _pendingWrites.RemoveAt(0);
        if (refused is null) done.SetResult();
        else done.SetException(refused);
        return true;
    }

    /// <summary>Every member add or remove on a group, in order: (add?, group DN, member DNs).</summary>
    public List<(bool Add, string GroupDn, IReadOnlyList<string> MemberDns)> MemberWrites { get; } = new();

    public Task AddMembersAsync(string groupDn, IReadOnlyList<string> memberDns, CancellationToken cancellationToken = default)
    {
        MemberWrites.Add((true, groupDn, memberDns));
        return Task.CompletedTask;
    }

    public Task RemoveMembersAsync(string groupDn, IReadOnlyList<string> memberDns, CancellationToken cancellationToken = default)
    {
        MemberWrites.Add((false, groupDn, memberDns));
        return Task.CompletedTask;
    }

    /// <summary>Accounts unlocked, in order.</summary>
    public List<string> Unlocks { get; } = new();

    public Task UnlockAccountAsync(string distinguishedName, CancellationToken cancellationToken = default)
    {
        Unlocks.Add(distinguishedName);
        return Task.CompletedTask;
    }

    /// <summary>Moves, in order. A move keeps the object's attributes under its new DN, as AD does.</summary>
    public List<(string From, string ToParent)> Moves { get; } = new();

    public Task<string> MoveObjectAsync(string distinguishedName, string newParentDn, CancellationToken cancellationToken = default)
    {
        Moves.Add((distinguishedName, newParentDn));
        var rdn = DirectoryService.FirstRdn(distinguishedName);
        var moved = rdn + "," + newParentDn;
        if (Objects.Remove(distinguishedName, out var attrs)) Objects[moved] = attrs;
        if (ObjectGuids.Remove(distinguishedName, out var guid)) ObjectGuids[moved] = guid;
        if (ExistingDns.Remove(distinguishedName)) ExistingDns.Add(moved);
        return Task.FromResult(moved);
    }

    /// <summary>Renames, in order: (old DN, the name asked for, the DN it became).</summary>
    public List<(string From, string NewName, string To)> Renames { get; } = new();

    /// <summary>DNs whose rename is refused, so the partial-failure path can be exercised.</summary>
    public HashSet<string> FailRenamesFor { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Renames as AD does: the new DN is built exactly as <see cref="DirectoryService.BuildRenamedDn"/> builds
    /// it, and the object keeps its attributes, GUID and SID under the new DN.
    /// </summary>
    public Task<string> RenameObjectAsync(string distinguishedName, string newName, CancellationToken cancellationToken = default)
    {
        if (FailRenamesFor.Contains(distinguishedName))
            return Task.FromException<string>(new InvalidOperationException($"the directory refused to rename {distinguishedName}"));
        var renamed = DirectoryService.BuildRenamedDn(distinguishedName, newName);
        Renames.Add((distinguishedName, newName, renamed));
        if (Objects.Remove(distinguishedName, out var attrs))
        {
            // AD changes cn and name with the DN; a read-back after a rename has to see the new name.
            foreach (var a in attrs.Where(a => a.LdapName is "cn" or "name"))
            {
                a.RawValues.Clear(); a.RawValues.Add(newName.Trim());
                a.DisplayValues.Clear(); a.DisplayValues.Add(newName.Trim());
            }
            Objects[renamed] = attrs;
        }
        if (ObjectGuids.Remove(distinguishedName, out var guid)) ObjectGuids[renamed] = guid;
        foreach (var sid in Sids.Where(kv => string.Equals(kv.Value, distinguishedName, StringComparison.OrdinalIgnoreCase))
                                .Select(kv => kv.Key).ToList())
            Sids[sid] = renamed;
        if (ExistingDns.Remove(distinguishedName)) ExistingDns.Add(renamed);
        return Task.FromResult(renamed);
    }

    /// <summary>objectGUID per DN. A rename or move carries the entry to the new DN, as AD keeps the GUID.</summary>
    public Dictionary<string, string> ObjectGuids { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every GUID and SID lookup, in order, so a test can see what was resolved and how.</summary>
    public List<string> IdentityLookups { get; } = new();

    /// <summary>
    /// While set, every GUID lookup waits for this task before it answers. A test uses it to keep a reference sweep
    /// running, and holding its lock, for as long as it needs.
    /// </summary>
    public Task? HoldGuidLookups { get; set; }

    public async Task<string?> GetObjectGuidAsync(string distinguishedName, CancellationToken cancellationToken = default)
    {
        IdentityLookups.Add("dn:" + distinguishedName);
        if (HoldGuidLookups is { } hold) await hold;
        return ObjectGuids.TryGetValue(distinguishedName, out var g) ? g : null;
    }

    public async Task<string?> FindDnByGuidAsync(string objectGuid, CancellationToken cancellationToken = default)
    {
        IdentityLookups.Add("guid:" + objectGuid);
        if (HoldGuidLookups is { } hold) await hold;
        var hit = ObjectGuids.FirstOrDefault(kv => string.Equals(kv.Value, objectGuid, StringComparison.OrdinalIgnoreCase));
        return hit.Key;
    }

    /// <summary>DN per SID, for finding a synced group's on-prem object.</summary>
    public Dictionary<string, string> Sids { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> FindDnBySidAsync(string sid, CancellationToken cancellationToken = default)
    {
        IdentityLookups.Add("sid:" + sid);
        return Task.FromResult(Sids.TryGetValue(sid, out var dn) ? dn : null);
    }

    /// <summary>Owners per address (without a prefix), as FindAddressOwnersAsync reports them.</summary>
    public Dictionary<string, List<string>> AddressOwners { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<string>> FindAddressOwnersAsync(string address, CancellationToken cancellationToken = default)
    {
        var bare = address.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase) ? address[5..] : address;
        return Task.FromResult<IReadOnlyList<string>>(
            AddressOwners.TryGetValue(bare, out var owners) ? owners.ToList() : new List<string>());
    }

    /// <summary>DNs that exist besides those in <see cref="Objects"/>, for ExistsAsync.</summary>
    public HashSet<string> ExistingDns { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<bool> ExistsAsync(string distinguishedName, CancellationToken cancellationToken = default) =>
        Task.FromResult(ExistingDns.Contains(distinguishedName) || Objects.ContainsKey(distinguishedName));

    /// <summary>sAMAccountNames already taken in the domain, for FindExistingSamAccountNamesAsync.</summary>
    public HashSet<string> ExistingSams { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlySet<string>> FindExistingSamAccountNamesAsync(IEnumerable<string> samAccountNames, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlySet<string>>(
            samAccountNames.Where(ExistingSams.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>Members returned per group DN by <see cref="GetGroupMembersAsync"/>. Absent means none.</summary>
    public Dictionary<string, IReadOnlyList<GroupMember>> GroupMembers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyDictionary<string, string>> GetGroupTypesAsync(IReadOnlyList<string> distinguishedNames, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

    // --- everything else: reaching these means the test wandered off ------------------------------

    private static Exception Unused([System.Runtime.CompilerServices.CallerMemberName] string? member = null) =>
        new NotSupportedException($"FakeDirectoryService.{member} was called but is not stubbed. "
                                  + "Stub it deliberately rather than letting a test lean on a default.");

    public ConnectionState? Current => null;
    public NameResolver? Resolver => null;
    public bool IsConnected => true;

    /// <summary>What the next ListDeletedObjectsAsync returns. Null means the call was not expected.</summary>
    public DeletedObjectsResult? DeletedObjects { get; set; }

    /// <summary>Every ListDeletedObjectsAsync call, recording whether recycled objects were asked for.</summary>
    public List<bool> DeletedObjectReads { get; } = new();

    public Task<DeletedObjectsResult> ListDeletedObjectsAsync(bool includeRecycled, CancellationToken cancellationToken = default)
    {
        DeletedObjectReads.Add(includeRecycled);
        return Task.FromResult(DeletedObjects ?? throw Unused());
    }

    public Task ConnectAsync(ConnectionProfile profile, string password, CancellationToken cancellationToken = default) => throw Unused();
    public void Disconnect() => throw Unused();
    /// <summary>The tree root handed to a picker. Null until a test builds one.</summary>
    public AdNode? RootNode { get; set; }

    /// <summary>Children per container DN. A DN with no entry has no children, which is not an error.</summary>
    public Dictionary<string, List<AdNode>> Tree { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Container DNs whose children were actually asked for, in order — the tree loads lazily, and
    /// which parts of it a test caused to load is usually the thing under test.</summary>
    public List<string> ChildReads { get; } = new();

    /// <summary>
    /// Builds a container node and registers it under <paramref name="parentDn"/> when given.
    /// <paramref name="hasChildren"/> is stated rather than inferred because AdNode.HasChildren is
    /// init-only, and it is what decides whether the tree offers to expand the node at all.
    /// </summary>
    public AdNode AddOu(string distinguishedName, string name, string? parentDn = null, bool hasChildren = false)
    {
        var node = new AdNode
        {
            DistinguishedName = distinguishedName,
            Name = name,
            Type = AdObjectType.OrganizationalUnit,
            HasChildren = hasChildren,
        };
        if (parentDn is not null)
        {
            if (!Tree.TryGetValue(parentDn, out var kids)) Tree[parentDn] = kids = new List<AdNode>();
            kids.Add(node);
        }
        return node;
    }

    public AdNode GetRootNode() => RootNode ?? throw Unused();

    public Task<IReadOnlyList<AdNode>> GetChildrenAsync(string distinguishedName, CancellationToken cancellationToken = default)
    {
        ChildReads.Add(distinguishedName);
        Tree.TryGetValue(distinguishedName, out var kids);
        return Task.FromResult((IReadOnlyList<AdNode>)(kids ?? new List<AdNode>()));
    }
    public Task<IReadOnlyList<AdObjectRow>> ListObjectsAsync(string baseDn, AdObjectType filter, IReadOnlyList<string> columns, bool subtree, CancellationToken cancellationToken = default) => throw Unused();
    public Task<IReadOnlyList<AdObjectRow>> SearchAsync(SearchQuery query, IReadOnlyList<string> columns, CancellationToken cancellationToken = default) => throw Unused();
    public Task<ObjectBasicInfo> GetBasicInfoAsync(string distinguishedName, CancellationToken cancellationToken = default) => throw Unused();
    public Task<GroupCreateResult> CreateGroupAsync(string parentDn, string name, string samAccountName, GroupScope scope, GroupCategory category, string? description, string? managedByDn, bool protectFromDeletion, IReadOnlyList<string>? initialMemberDns = null, CancellationToken cancellationToken = default) => throw Unused();
    public Task<(string Dn, string? ProtectionError)> CreateOrganizationalUnitAsync(string parentDn, string name, bool protectFromDeletion, string? description, CancellationToken cancellationToken = default) => throw Unused();
    public Task<IReadOnlyList<AdObjectRow>> SearchByNameAsync(string text, AdObjectType type, CancellationToken cancellationToken = default) => throw Unused();
    public Task DeleteObjectAsync(string distinguishedName, CancellationToken cancellationToken = default) => throw Unused();
    public Task ResetPasswordAsync(string distinguishedName, string newPassword, bool mustChangeAtNextLogon, bool unlock, CancellationToken cancellationToken = default) => throw Unused();
    public Task<UserCreateResult> CreateUserAsync(string ouDn, IReadOnlyDictionary<string, string> attributes, IEnumerable<string> groupDns, string? password, bool enabled, bool mustChangePassword, IReadOnlyList<string>? proxyAddresses = null, CancellationToken cancellationToken = default) => throw Unused();
    public Task<BulkResult> BulkApplyAsync(IReadOnlyList<AdObjectRow> targets, IReadOnlyList<PendingChange> changes, IProgress<int>? progress = null, CancellationToken cancellationToken = default) => throw Unused();
}
