using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.TestSupport;

/// <summary>
/// A deliberately inert IGraphService. Every call throws, and the signed-in/configured flags read false.
///
/// It exists so a view model can be CONSTRUCTED in a test without a tenant. The view models build child
/// tabs in their constructors, and those tabs read the sign-in flags immediately — passing null crashes
/// before the test starts. Nothing here pretends to be Microsoft 365; a test that needs cloud behaviour
/// should stub the specific call it needs rather than lean on this.
/// </summary>
public sealed class InertGraphService : IGraphService
{
    private static Exception Unused([System.Runtime.CompilerServices.CallerMemberName] string? member = null) =>
        new NotSupportedException($"InertGraphService.{member} was called but is not stubbed.");

    public bool IsConfigured => false;
    public bool IsSignedIn => false;
    public string? SignedInAccount => null;
    public void Configure(string tenantId, string clientId) => throw Unused();
    public Task SignInAsync(CancellationToken cancellationToken = default) => throw Unused();
    public void SignOut() => throw Unused();
    public Task<string> GetAccessTokenAsync(string[] scopes, CancellationToken cancellationToken = default) => throw Unused();
    public Task<CloudUserInfo?> GetUserByUpnAsync(string upn, CancellationToken cancellationToken = default) => throw Unused();
    public Task<IReadOnlyList<CloudGroup>> SearchGroupsAsync(string text, CancellationToken cancellationToken = default) => throw Unused();
    public Task<IReadOnlyList<CloudMember>> GetGroupMembersAsync(string groupId, CancellationToken cancellationToken = default) => throw Unused();
    public Task<IReadOnlyList<CloudGroup>> GetUserGroupsByUpnAsync(string upnOrId, CancellationToken cancellationToken = default) => throw Unused();
    public Task<IReadOnlyList<CloudGroup>> GetObjectMemberOfAsync(string objectId, CloudObjectKind kind, CancellationToken cancellationToken = default) => throw Unused();
    public Task<bool> GroupExistsAsync(string groupId, CancellationToken cancellationToken = default) => throw Unused();
    public Task<IReadOnlyList<CloudSku>> GetSubscribedSkusAsync(CancellationToken cancellationToken = default) => throw Unused();
    public Task<CloudPage> ListUsersAsync(string? search, string? nextLink, CancellationToken cancellationToken = default) => throw Unused();
    public Task<CloudPage> ListGroupsAsync(string? search, string? nextLink, CancellationToken cancellationToken = default) => throw Unused();
    public Task<CloudPage> ListDevicesAsync(string? search, string? nextLink, CancellationToken cancellationToken = default) => throw Unused();
    public Task<CloudPage> GetGroupMembersPageAsync(string groupId, string? nextLink, CancellationToken cancellationToken = default) => throw Unused();
    public Task<CloudGroup?> GetGroupByOnPremSidAsync(string onPremSid, CancellationToken cancellationToken = default) => throw Unused();
    public Task<IReadOnlyList<CloudDevice>> GetDevicesByComputerAsync(string computerName, string? onPremSid, CancellationToken cancellationToken = default) => throw Unused();
    public Task<IReadOnlyList<CloudPropertySection>> GetObjectDetailAsync(string id, CloudObjectKind kind, CancellationToken cancellationToken = default) => throw Unused();
    public Task<CloudGroupCreateResult> CreateGroupAsync(CloudGroupCreateRequest request, CancellationToken cancellationToken = default) => throw Unused();
    public Task AddMemberToGroupAsync(string groupId, string memberObjectId, CancellationToken cancellationToken = default) => throw Unused();
    public Task RemoveMemberFromGroupAsync(string groupId, string memberObjectId, CancellationToken cancellationToken = default) => throw Unused();
    public Task SetUserAccountEnabledAsync(string userId, bool enabled, CancellationToken cancellationToken = default) => throw Unused();
    public Task RevokeSignInSessionsAsync(string userId, CancellationToken cancellationToken = default) => throw Unused();
    public Task UpdateUserAsync(string userId, IReadOnlyDictionary<string, string?> changes, CancellationToken cancellationToken = default) => throw Unused();
    public Task UpdateGroupAsync(string groupId, IReadOnlyDictionary<string, string?> changes, CancellationToken cancellationToken = default) => throw Unused();
    public Task AssignLicenseToUserAsync(string userId, Guid skuId, CancellationToken cancellationToken = default) => throw Unused();
    public Task RemoveLicenseFromUserAsync(string userId, Guid skuId, CancellationToken cancellationToken = default) => throw Unused();
    public Task<TemporaryAccessPassResult> CreateTemporaryAccessPassAsync(string userId, int lifetimeMinutes, bool isUsableOnce, CancellationToken cancellationToken = default) => throw Unused();
}
