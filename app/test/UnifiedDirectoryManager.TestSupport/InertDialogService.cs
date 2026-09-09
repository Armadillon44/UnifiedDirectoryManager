using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.TestSupport;

/// <summary>
/// A deliberately inert IDialogService. Every call throws by name, except the two confirmations, which
/// answer with whatever the test set.
///
/// The confirmations are the exception because they sit in the middle of write paths: a save asks before it
/// sends, so a test of what a save does cannot get past the first line without one. Every other member
/// opens a window, which a test has no business doing — reaching one means the test wandered off its path,
/// and it should say so loudly rather than quietly receive a null.
///
/// Every member is virtual, so a test that needs one more answer derives and overrides just that.
/// </summary>
public class InertDialogService : IDialogService
{
    private static Exception Unused([System.Runtime.CompilerServices.CallerMemberName] string? member = null) =>
        new NotSupportedException($"InertDialogService.{member} was called but is not stubbed.");

    /// <summary>What <see cref="Confirm"/> answers. True by default: a test that reaches it usually wants
    /// the operation to proceed, and a test of the refusal path can say so explicitly.</summary>
    public bool ConfirmAnswer { get; set; } = true;

    /// <summary>Every confirmation asked for, as (title, heading), in order.</summary>
    public List<(string Title, string Heading)> Confirmations { get; } = new();

    public virtual bool Confirm(string title, string heading, IEnumerable<string> lines)
    {
        Confirmations.Add((title, heading));
        return ConfirmAnswer;
    }

    public virtual bool ConfirmWithPhrase(string title, string heading, IEnumerable<string> lines, string requiredPhrase)
    {
        Confirmations.Add((title, heading));
        return ConfirmAnswer;
    }

    public virtual IReadOnlyList<AdObjectRow>? PickObjects(string title, AdObjectType type, bool multiSelect) => throw Unused();
    public virtual IReadOnlyList<GroupRef>? PickGroupsHybrid(string title) => throw Unused();
    public virtual IReadOnlyList<CloudObjectRow>? PickCloudMembers(string title) => throw Unused();
    public virtual IReadOnlyList<CloudSku>? PickLicenses(string title, IReadOnlyList<CloudSku> candidates) => throw Unused();
    public virtual IReadOnlyList<CloudGroup>? PickCloudGroups(string title) => throw Unused();
    public virtual MailboxRecipient? PickMailboxRecipient(string title) => throw Unused();
    public virtual IReadOnlyList<MailboxRecipient>? PickMailboxRecipients(string title, IReadOnlyList<MailboxRecipient>? initial = null) => throw Unused();
    public virtual void ShowDistributionGroupMembers(string identity, string groupName, bool isSynced) => throw Unused();
    public virtual IReadOnlyList<MemberCandidate>? PasteMembers(string title, MemberBackend backend, IEnumerable<string> alreadyMembers, string? selfIdentity) => throw Unused();
    public virtual string? PickContainer(string? initialDn) => throw Unused();
    public virtual IReadOnlyList<string>? PickContainers(IEnumerable<string> initialDns) => throw Unused();
    public virtual void Alert(string title, string message) => throw Unused();
    public virtual void ShowBulkResult(BulkResult result) => throw Unused();
    public virtual BulkResult ShowScenarioRun(Scenario scenario, IReadOnlyList<AdObjectRow> targets, IList<string>? operationLog) => throw Unused();
    public virtual void ShowNewUser(string? defaultOuDn, Action onCreated) => throw Unused();
    public virtual void ShowBulkCreateUsers(string? defaultOuDn, Action onCreated) => throw Unused();
    public virtual void ShowBulkCreateReport(BulkCreateReport report) => throw Unused();
    public virtual void CaptureBatchUser(UserTemplate? defaultTemplate, string? defaultOuDn, string? upnSuffix, BulkCreateRowViewModel? existing, Action<BulkCreateRowViewModel> onCaptured) => throw Unused();
    public virtual void ShowTemplateEditor() => throw Unused();
    public virtual void ShowCopyUserToTemplate(string userDistinguishedName) => throw Unused();
    public virtual void ShowCopyUser(string sourceUserDistinguishedName, Action onCreated) => throw Unused();
    public virtual bool ShowCopyGroupsToUser(string sourceUserDistinguishedName) => throw Unused();
    public virtual void ShowScenarioEditor(Action onChanged) => throw Unused();
    public virtual void ShowReadme() => throw Unused();
    public virtual void ShowEntraSync() => throw Unused();
    public virtual void ShowSettings(Action onReconnected) => throw Unused();
    public virtual void ShowCloudObjectProperties(CloudObjectRow row) => throw Unused();
    public virtual void ShowOuProperties(string distinguishedName, string name) => throw Unused();
    public virtual void ShowAdObjectProperties(string distinguishedName, AdObjectType type) => throw Unused();
    public virtual string? ShowNewOu(string parentDn) => throw Unused();
    public virtual string? ShowNewGroup(string? parentDn) => throw Unused();
    public virtual SearchQuery? ShowAdvancedSearch(string defaultBaseDn, SavedSearchPinning? pinning = null) => throw Unused();
    public virtual bool ShowBulkEdit(IReadOnlyList<AdObjectRow> rows) => throw Unused();
    public virtual IReadOnlyList<string>? EditMultiValue(string friendlyName, IEnumerable<string> values) => throw Unused();
    public virtual void OpenObjectEditor(string distinguishedName, AdObjectType type, string title, Action onChanged) => throw Unused();
    public virtual PasswordResetRequest? PromptPasswordReset(string accountTitle) => throw Unused();
    public virtual string? PromptSaveFile(string filter, string defaultFileName, string? initialDirectory = null) => throw Unused();
    public virtual string? PromptOpenFile(string filter) => throw Unused();
    public virtual (DelegateAccess Access, bool AutoMapping)? EditDelegateAccess(string delegateName, DelegateAccess current) => throw Unused();
    public virtual (string Id, string Name, bool FromExchange)? ShowNewCloudGroup(CloudGroupType? initialType) => throw Unused();

    /// <summary>The delete confirmation. Answers ConfirmAnswer, and never asks to record the deletion.</summary>
    public virtual bool? ConfirmDelete(string title, string heading, IEnumerable<string> lines,
                                       string? requiredPhrase, bool offerRecord, bool recordDefault)
    {
        Confirmations.Add((title, heading));
        return ConfirmAnswer ? false : (bool?)null;
    }

    public virtual void ShowLogViewer() => throw Unused();
    public virtual void ShowAbout() => throw Unused();
}
