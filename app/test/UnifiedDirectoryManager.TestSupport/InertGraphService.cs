using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.TestSupport;

/// <summary>
/// A deliberately inert IGraphService. Every call throws, and the signed-in/configured flags read false.
///
/// It exists so a view model can be CONSTRUCTED in a test without a tenant. The view models build child
/// tabs in their constructors, and those tabs read the sign-in flags immediately — passing null crashes
/// before the test starts. Nothing here pretends to be Microsoft 365. Every member is
/// virtual, so a test that needs cloud behaviour derives from this and overrides the two or three calls it
/// actually exercises — anything it forgets still throws by name rather than handing back a default.
/// </summary>
public class InertGraphService : IGraphService
{
    private static Exception Unused([System.Runtime.CompilerServices.CallerMemberName] string? member = null) =>
        new NotSupportedException($"InertGraphService.{member} was called but is not stubbed.");

    public virtual bool IsConfigured => false;
    public virtual bool IsSignedIn => false;

    /// <summary>
    /// Settable, unlike everything else here, because the Exchange channel keys its live session on WHO is
    /// signed in — a test has to be able to change the admin without a tenant. Null by default, which is
    /// what the view-model tests that use this class already expect.
    /// </summary>
    public string? SignedInAccount { get; set; }
    public virtual void Configure(string tenantId, string clientId) => throw Unused();
    public virtual Task SignInAsync(CancellationToken cancellationToken = default) => throw Unused();
    public virtual void SignOut() => throw Unused();
    public virtual Task<string> GetAccessTokenAsync(string[] scopes, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<CloudUserInfo?> GetUserByUpnAsync(string upn, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<IReadOnlyList<CloudGroup>> SearchGroupsAsync(string text, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<IReadOnlyList<CloudMember>> GetGroupMembersAsync(string groupId, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<IReadOnlyList<CloudGroup>> GetUserGroupsByUpnAsync(string upnOrId, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<IReadOnlyList<CloudGroup>> GetObjectMemberOfAsync(string objectId, CloudObjectKind kind, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<bool> GroupExistsAsync(string groupId, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<IReadOnlyList<CloudSku>> GetSubscribedSkusAsync(CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<CloudPage> ListUsersAsync(string? search, string? nextLink, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<CloudPage> ListGroupsAsync(string? search, string? nextLink, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<CloudPage> ListDevicesAsync(string? search, string? nextLink, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<CloudPage> GetGroupMembersPageAsync(string groupId, string? nextLink, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<CloudGroup?> GetGroupByOnPremSidAsync(string onPremSid, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<IReadOnlyList<CloudDevice>> GetDevicesByComputerAsync(string computerName, string? onPremSid, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<IReadOnlyList<CloudPropertySection>> GetObjectDetailAsync(string id, CloudObjectKind kind, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<CloudGroupCreateResult> CreateGroupAsync(CloudGroupCreateRequest request, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task AddMemberToGroupAsync(string groupId, string memberObjectId, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task RemoveMemberFromGroupAsync(string groupId, string memberObjectId, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task SetUserAccountEnabledAsync(string userId, bool enabled, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task RevokeSignInSessionsAsync(string userId, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task UpdateUserAsync(string userId, IReadOnlyDictionary<string, string?> changes, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task UpdateGroupAsync(string groupId, IReadOnlyDictionary<string, string?> changes, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task AssignLicenseToUserAsync(string userId, Guid skuId, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task RemoveLicenseFromUserAsync(string userId, Guid skuId, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<TemporaryAccessPassResult> CreateTemporaryAccessPassAsync(string userId, int lifetimeMinutes, bool isUsableOnce, CancellationToken cancellationToken = default) => throw Unused();
}
