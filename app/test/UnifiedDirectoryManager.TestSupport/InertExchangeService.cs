using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.TestSupport;

/// <summary>
/// A deliberately inert IExchangeService. Every call throws, and the signed-in/configured flags read false.
///
/// It exists so a view model can be CONSTRUCTED in a test without a tenant. The view models build child
/// tabs in their constructors, and those tabs read the sign-in flags immediately — passing null crashes
/// before the test starts. Nothing here pretends to be Microsoft 365; a test that needs cloud behaviour
/// should stub the specific call it needs rather than lean on this.
/// </summary>
public sealed class InertExchangeService : IExchangeService
{
    private static Exception Unused([System.Runtime.CompilerServices.CallerMemberName] string? member = null) =>
        new NotSupportedException($"InertExchangeService.{member} was called but is not stubbed.");

    public bool IsConfigured => false;
    public bool IsConnected => false;
    public string? Organization => null;
    public void Configure(string organization) => throw Unused();
    public Task ConnectAsync(CancellationToken cancellationToken = default) => throw Unused();
    public void Disconnect() => throw Unused();
    public Task<MailboxInfo?> GetMailboxAsync(string identity, CancellationToken cancellationToken = default) => throw Unused();
    public Task<IReadOnlyList<MailboxRecipient>> SearchRecipientsAsync(string text, CancellationToken cancellationToken = default) => throw Unused();
    public Task<IReadOnlyList<MemberResolution>> ResolveMembersAsync(IReadOnlyList<PastedTerm> terms, IProgress<int>? progress, CancellationToken cancellationToken = default) => throw Unused();
    public Task<ExchangePage> ListMailboxesAsync(string? search, int max, CancellationToken cancellationToken = default) => throw Unused();
    public Task<ExchangePage> ListDistributionGroupsAsync(string? search, int max, CancellationToken cancellationToken = default) => throw Unused();
    public Task<CloudGroupCreateResult> CreateDistributionGroupAsync(CloudGroupCreateRequest request, CancellationToken cancellationToken = default) => throw Unused();
    public Task ConvertMailboxAsync(string identity, MailboxType type, CancellationToken cancellationToken = default) => throw Unused();
    public Task SetForwardingAsync(string identity, string forwardingTargetIdentity, bool deliverToMailboxAndForward, CancellationToken cancellationToken = default) => throw Unused();
    public Task ClearForwardingAsync(string identity, CancellationToken cancellationToken = default) => throw Unused();
    public Task<IReadOnlyList<MailboxDelegate>> GetDelegatesAsync(string identity, CancellationToken cancellationToken = default) => throw Unused();
    public Task AddDelegateAsync(string identity, string delegateIdentity, DelegateAccess access, bool autoMapping, CancellationToken cancellationToken = default) => throw Unused();
    public Task RemoveDelegateAsync(string identity, string delegateIdentity, DelegateAccess access, CancellationToken cancellationToken = default) => throw Unused();
    public Task<IReadOnlyList<CloudPropertySection>> GetMailboxDetailAsync(string identity, CancellationToken cancellationToken = default) => throw Unused();
    public Task<CloudPropertySection> GetMailboxUsageAsync(string identity, string? exchangeGuid = null, CancellationToken cancellationToken = default) => throw Unused();
    public Task<IReadOnlyList<CloudPropertySection>> GetDistributionGroupDetailAsync(string identity, CancellationToken cancellationToken = default) => throw Unused();
    public Task<DlRecipientList> GetDistributionGroupRecipientsAsync(string identity, string rowKey, CancellationToken cancellationToken = default) => throw Unused();
    public Task<IReadOnlyList<string>> SetDistributionGroupPropertiesAsync(string identity, IReadOnlyList<CloudProperty> changes, CancellationToken cancellationToken = default) => throw Unused();
    public Task<IReadOnlyList<MailboxRecipient>> GetDistributionGroupMembersAsync(string groupIdentity, CancellationToken cancellationToken = default) => throw Unused();
    public Task RemoveDistributionGroupMemberAsync(string groupIdentity, string memberIdentity, CancellationToken cancellationToken = default) => throw Unused();
    public Task AddDistributionGroupMemberAsync(string groupIdentity, string memberIdentity, CancellationToken cancellationToken = default) => throw Unused();
}
