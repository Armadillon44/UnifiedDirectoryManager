using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.TestSupport;

/// <summary>
/// An <see cref="InertGraphService"/> whose cloud-detail reads park until the test releases them.
///
/// The cloud properties pane loads in stages — the grouped sections, then licences and memberships, or
/// members — and every stage resumes after an await. The bugs live in what happens when a second selection
/// starts while an earlier stage is still in flight, and a real tenant answers whenever it likes. Here the
/// test decides when each stage completes, so "the operator clicked another row mid-load" is something a
/// test can actually perform.
///
/// Everything not overridden still throws by name (see <see cref="InertGraphService"/>).
/// </summary>
public sealed class HoldableGraphService : InertGraphService
{
    private readonly List<TaskCompletionSource<IReadOnlyList<CloudPropertySection>>> _detailWaiters = new();
    private readonly List<TaskCompletionSource<CloudUserInfo?>> _userWaiters = new();
    private readonly List<TaskCompletionSource<IReadOnlyList<CloudMember>>> _memberWaiters = new();

    public override bool IsConfigured => true;
    public override bool IsSignedIn => true;

    /// <summary>When true, every read below parks until its Release call. Off by default so a test opts in.</summary>
    public bool Hold { get; set; }

    /// <summary>Object ids passed to <see cref="GetObjectDetailAsync"/>, in order.</summary>
    public List<string> DetailReads { get; } = new();

    /// <summary>What a released detail read hands back.</summary>
    public List<CloudPropertySection> Sections { get; } = new();

    /// <summary>What a released user read hands back.</summary>
    public CloudUserInfo? UserInfo { get; set; }

    /// <summary>What a released group-members read hands back.</summary>
    public List<CloudMember> GroupMembers { get; } = new();

    /// <summary>How many reads of each kind are parked right now.</summary>
    public int PendingDetails => _detailWaiters.Count;
    public int PendingUsers => _userWaiters.Count;
    public int PendingMembers => _memberWaiters.Count;

    /// <summary>Completes the OLDEST parked read of each kind — the superseded one, which is the point.</summary>
    public bool ReleaseDetail() => Release(_detailWaiters, (IReadOnlyList<CloudPropertySection>)Sections.ToList());
    public bool ReleaseUser() => Release(_userWaiters, UserInfo);
    public bool ReleaseMembers() => Release(_memberWaiters, (IReadOnlyList<CloudMember>)GroupMembers.ToList());

    private static bool Release<T>(List<TaskCompletionSource<T>> waiters, T value)
    {
        if (waiters.Count == 0) return false;
        var tcs = waiters[0];
        waiters.RemoveAt(0);
        tcs.SetResult(value);
        return true;
    }

    private Task<T> Park<T>(List<TaskCompletionSource<T>> waiters, T ready)
    {
        if (!Hold) return Task.FromResult(ready);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiters.Add(tcs);
        return tcs.Task;
    }

    public override Task<IReadOnlyList<CloudPropertySection>> GetObjectDetailAsync(
        string id, CloudObjectKind kind, CancellationToken cancellationToken = default)
    {
        DetailReads.Add(id);
        return Park(_detailWaiters, (IReadOnlyList<CloudPropertySection>)Sections.ToList());
    }

    public override Task<CloudUserInfo?> GetUserByUpnAsync(string upn, CancellationToken cancellationToken = default) =>
        Park(_userWaiters, UserInfo);

    public override Task<IReadOnlyList<CloudMember>> GetGroupMembersAsync(string groupId, CancellationToken cancellationToken = default) =>
        Park(_memberWaiters, (IReadOnlyList<CloudMember>)GroupMembers.ToList());

    // --- a user's own cloud groups, as the edit pane's Member Of reads them -----------------------------

    private readonly List<TaskCompletionSource<IReadOnlyList<CloudGroup>>> _groupWaiters = new();

    /// <summary>
    /// When true, <see cref="GetUserGroupsByUpnAsync"/> parks until <see cref="ReleaseGroups"/>. This is separate
    /// from <see cref="Hold"/>, so the pane's cloud-group read can be held while its other reads answer.
    /// </summary>
    public bool HoldGroups { get; set; }

    /// <summary>What a released group read hands back.</summary>
    public List<CloudGroup> UserGroups { get; } = new();

    public int PendingGroups => _groupWaiters.Count;

    /// <summary>Completes the OLDEST parked group read.</summary>
    public bool ReleaseGroups() => Release(_groupWaiters, (IReadOnlyList<CloudGroup>)UserGroups.ToList());

    public override Task<IReadOnlyList<CloudGroup>> GetUserGroupsByUpnAsync(string upnOrId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CloudGroup> ready = UserGroups.ToList();
        if (!HoldGroups) return Task.FromResult(ready);
        var tcs = new TaskCompletionSource<IReadOnlyList<CloudGroup>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _groupWaiters.Add(tcs);
        return tcs.Task;
    }

    /// <summary>Every cloud group add or remove, in order: (add?, group id, member object id).</summary>
    public List<(bool Add, string GroupId, string MemberId)> GroupWrites { get; } = new();

    public override Task AddMemberToGroupAsync(string groupId, string memberObjectId, CancellationToken cancellationToken = default)
    {
        GroupWrites.Add((true, groupId, memberObjectId));
        return Task.CompletedTask;
    }

    public override Task RemoveMemberFromGroupAsync(string groupId, string memberObjectId, CancellationToken cancellationToken = default)
    {
        GroupWrites.Add((false, groupId, memberObjectId));
        return Task.CompletedTask;
    }
}
