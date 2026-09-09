using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.TestSupport;

/// <summary>
/// A deliberately inert IExchangeService. Every call throws, and the signed-in/configured flags read false.
///
/// It exists so a view model can be CONSTRUCTED in a test without a tenant. The view models build child
/// tabs in their constructors, and those tabs read the sign-in flags immediately — passing null crashes
/// before the test starts. Nothing here pretends to be Microsoft 365. Every member is
/// virtual, so a test that needs cloud behaviour derives from this and overrides the two or three calls it
/// actually exercises — anything it forgets still throws by name rather than handing back a default.
/// </summary>
public class InertExchangeService : IExchangeService
{
    private static Exception Unused([System.Runtime.CompilerServices.CallerMemberName] string? member = null) =>
        new NotSupportedException($"InertExchangeService.{member} was called but is not stubbed.");

    public virtual bool IsConfigured => false;
    public virtual bool IsConnected => false;
    public virtual string? Organization => null;
    public virtual void Configure(string organization) => throw Unused();
    public virtual Task ConnectAsync(CancellationToken cancellationToken = default) => throw Unused();
    public virtual void Disconnect() => throw Unused();
    public virtual Task<MailboxInfo?> GetMailboxAsync(string identity, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<IReadOnlyList<MailboxRecipient>> SearchRecipientsAsync(string text, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<IReadOnlyList<MemberResolution>> ResolveMembersAsync(IReadOnlyList<PastedTerm> terms, IProgress<int>? progress, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<ExchangePage> ListMailboxesAsync(string? search, int max, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<ExchangePage> ListDistributionGroupsAsync(string? search, int max, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<CloudGroupCreateResult> CreateDistributionGroupAsync(CloudGroupCreateRequest request, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task ConvertMailboxAsync(string identity, MailboxType type, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task SetForwardingAsync(string identity, string forwardingTargetIdentity, bool deliverToMailboxAndForward, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task ClearForwardingAsync(string identity, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<IReadOnlyList<MailboxDelegate>> GetDelegatesAsync(string identity, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task AddDelegateAsync(string identity, string delegateIdentity, DelegateAccess access, bool autoMapping, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task RemoveDelegateAsync(string identity, string delegateIdentity, DelegateAccess access, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<IReadOnlyList<CloudPropertySection>> GetMailboxDetailAsync(string identity, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<CloudPropertySection> GetMailboxUsageAsync(string identity, string? exchangeGuid = null, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<IReadOnlyList<CloudPropertySection>> GetDistributionGroupDetailAsync(string identity, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<DlRecipientList> GetDistributionGroupRecipientsAsync(string identity, string rowKey, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<IReadOnlyList<string>> SetDistributionGroupPropertiesAsync(string identity, IReadOnlyList<CloudProperty> changes, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task<IReadOnlyList<MailboxRecipient>> GetDistributionGroupMembersAsync(string groupIdentity, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task RemoveDistributionGroupMemberAsync(string groupIdentity, string memberIdentity, CancellationToken cancellationToken = default) => throw Unused();
    public virtual Task AddDistributionGroupMemberAsync(string groupIdentity, string memberIdentity, CancellationToken cancellationToken = default) => throw Unused();
}
