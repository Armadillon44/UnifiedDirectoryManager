using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Opens the app's dialogs/windows on behalf of view models, keeping window creation out of the VMs.
/// </summary>
public interface IDialogService
{
    /// <summary>Searchable picker. Returns selected rows, or null if cancelled.</summary>
    IReadOnlyList<AdObjectRow>? PickObjects(string title, AdObjectType type, bool multiSelect);

    /// <summary>Hybrid group picker spanning on-prem AD + Entra ID groups. Returns the picked groups, or null if cancelled.</summary>
    IReadOnlyList<GroupRef>? PickGroupsHybrid(string title);

    /// <summary>Picks Entra ID members (users + devices) to add to a cloud group. Returns the picked rows, or null if cancelled.</summary>
    IReadOnlyList<CloudObjectRow>? PickCloudMembers(string title);

    /// <summary>Picks license SKUs to assign directly to a user (from the supplied candidates). Returns the picked SKUs, or null if cancelled.</summary>
    IReadOnlyList<CloudSku>? PickLicenses(string title, IReadOnlyList<CloudSku> candidates);

    /// <summary>Picks Entra ID groups to add an object to. Returns the picked groups, or null if cancelled.</summary>
    IReadOnlyList<CloudGroup>? PickCloudGroups(string title);

    /// <summary>Picks a single internal Exchange recipient (user/shared mailbox/distribution group) to forward a mailbox to. Null if cancelled.</summary>
    MailboxRecipient? PickMailboxRecipient(string title);

    /// <summary>Confirmation dialog listing the changes about to happen. Returns true to proceed.</summary>
    bool Confirm(string title, string heading, IEnumerable<string> lines);

    /// <summary>Browses the directory tree to pick an OU/container. Returns its DN, or null if cancelled.</summary>
    string? PickContainer(string? initialDn);

    /// <summary>Browses the directory tree to tick several OUs/containers. Returns their DNs, or null if cancelled.</summary>
    IReadOnlyList<string>? PickContainers(IEnumerable<string> initialDns);

    void Alert(string title, string message);

    void ShowBulkResult(BulkResult result);

    /// <summary>Runs a scenario in a modal window that streams each step live (and a done-of-total counter),
    /// then shows the summary. <paramref name="operationLog"/>, when non-null, is filled with the detailed
    /// per-target log for the caller to save. Returns the run's result.</summary>
    BulkResult ShowScenarioRun(Scenario scenario, IReadOnlyList<AdObjectRow> targets, IList<string>? operationLog);

    /// <summary>Opens the (non-modal) new-user wizard; <paramref name="onCreated"/> fires after each create.</summary>
    void ShowNewUser(string? defaultOuDn, Action onCreated);

    /// <summary>Opens the (non-modal) bulk create-users window; <paramref name="onCreated"/> fires after the batch runs.</summary>
    void ShowBulkCreateUsers(string? defaultOuDn, Action onCreated);

    /// <summary>Shows the (modal) post-run report for a bulk create (generated passwords / TAPs, copy + export).</summary>
    void ShowBulkCreateReport(BulkCreateReport report);

    /// <summary>Opens the standard New User window in batch-capture mode (non-modal, so the operator can
    /// switch to other windows while configuring a row) to configure one batch row (no I/O). Pass
    /// <paramref name="existing"/> to edit (the window is prefilled). <paramref name="onCaptured"/> is
    /// invoked with the configured row when the operator commits; it is not called if they cancel.</summary>
    void CaptureBatchUser(UserTemplate? defaultTemplate, string? defaultOuDn, string? upnSuffix, BulkCreateRowViewModel? existing, Action<BulkCreateRowViewModel> onCaptured);

    /// <summary>Opens the (non-modal) template editor.</summary>
    void ShowTemplateEditor();

    /// <summary>Opens the (modal) "save user as template" dialog for the given user DN.</summary>
    void ShowCopyUserToTemplate(string userDistinguishedName);

    /// <summary>Opens the (non-modal) "copy user" wizard seeded from the given user DN; <paramref name="onCreated"/> fires after each create.</summary>
    void ShowCopyUser(string sourceUserDistinguishedName, Action onCreated);

    /// <summary>Opens the (modal) "copy groups to user" dialog: copies the source user's group memberships
    /// (operator picks which) onto a chosen target user. Returns true if any membership was written.</summary>
    bool ShowCopyGroupsToUser(string sourceUserDistinguishedName);

    /// <summary>Opens the (non-modal) scenario editor; <paramref name="onChanged"/> fires after each save/delete/import.</summary>
    void ShowScenarioEditor(Action onChanged);

    /// <summary>Shows the embedded README.</summary>
    void ShowReadme();

    /// <summary>Opens the Entra Connect delta-sync dialog.</summary>
    void ShowEntraSync();

    /// <summary>Opens the (modal) Settings dialog (on-prem AD connection + cloud sign-in);
    /// <paramref name="onReconnected"/> fires when the AD connection is successfully rebound.</summary>
    void ShowSettings(Action onReconnected);

    /// <summary>Opens a (non-modal) read-only properties window for a cloud (Entra ID) object.</summary>
    void ShowCloudObjectProperties(CloudObjectRow row);

    /// <summary>Advanced search builder. Returns the query to run, or null if cancelled.</summary>
    SearchQuery? ShowAdvancedSearch(string defaultBaseDn);

    /// <summary>Bulk-edit dialog over the given targets. Returns true if changes were applied.</summary>
    bool ShowBulkEdit(IReadOnlyList<AdObjectRow> rows);

    /// <summary>Edits a multi-valued attribute's values. Returns the new list, or null if cancelled.</summary>
    IReadOnlyList<string>? EditMultiValue(string friendlyName, IEnumerable<string> values);

    /// <summary>Opens an object in its own editor window (non-modal); <paramref name="onChanged"/> fires after each write.</summary>
    void OpenObjectEditor(string distinguishedName, AdObjectType type, string title, Action onChanged);

    /// <summary>Confirmation that requires the user to type an exact phrase before OK enables. Returns true to proceed.</summary>
    bool ConfirmWithPhrase(string title, string heading, IEnumerable<string> lines, string requiredPhrase);

    /// <summary>Prompts for a new password (with confirm, generate, must-change and unlock options). Null if cancelled.</summary>
    PasswordResetRequest? PromptPasswordReset(string accountTitle);

    /// <summary>Shows a Save-file dialog. Returns the chosen path, or null if cancelled.</summary>
    string? PromptSaveFile(string filter, string defaultFileName, string? initialDirectory = null);

    /// <summary>Shows an Open-file dialog. Returns the chosen path, or null if cancelled.</summary>
    string? PromptOpenFile(string filter);

    /// <summary>Opens the in-app log viewer (newest log file).</summary>
    void ShowLogViewer();

    /// <summary>Opens the About dialog.</summary>
    void ShowAbout();
}
