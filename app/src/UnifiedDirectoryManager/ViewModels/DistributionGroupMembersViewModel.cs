using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Views and edits the membership of a distribution list or mail-enabled security group. These groups can only
/// be managed through Exchange Online — Microsoft Graph returns 403 for their membership — so every operation
/// here goes through the hosted PowerShell channel.
///
/// Writes are blocked outright for a group synced from on-premises Active Directory. In a hybrid organisation
/// that is the ordinary case rather than an edge case: Exchange rejects every write against a synced object, so
/// the controls are disabled with the reason rather than left to fail one member at a time.
/// </summary>
public partial class DistributionGroupMembersViewModel : ObservableObject
{
    private readonly IExchangeService _exchange;
    private readonly IDialogService _dialogs;

    /// <summary>The identity Exchange addresses the group by (primary SMTP).</summary>
    private readonly string _identity;

    [ObservableProperty] private string _groupName = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = string.Empty;

    /// <summary>
    /// True only while a WRITE batch is running, as distinct from <see cref="IsBusy"/>, which is also set by an
    /// ordinary read. Closing during a read is harmless; closing mid-batch would leave the remaining members
    /// running against a dead view model and the operator with no report of what landed.
    /// </summary>
    [ObservableProperty] private bool _isWriting;

    /// <summary>Bound to the write buttons so the gate is visible, not just enforced. A live-looking button that
    /// silently does nothing is worse than a disabled one.</summary>
    public bool CanWriteNow => CanEdit && !IsBusy;

    public ObservableCollection<MailboxRecipient> Members { get; } = new();

    /// <summary>True when the group is mastered on-premises, which makes every write here impossible.</summary>
    public bool IsSynced { get; }

    public bool CanEdit => !IsSynced;

    /// <summary>Shown in place of the buttons when the group can't be edited here.</summary>
    public string SyncedNotice =>
        "This group is synchronized from on-premises Active Directory. Its membership is mastered there and "
        + "can't be changed in Exchange Online — edit it in Active Directory instead.";

    public DistributionGroupMembersViewModel(
        IExchangeService exchange, IDialogService dialogs, string identity, string groupName, bool isSynced)
    {
        _exchange = exchange;
        _dialogs = dialogs;
        _identity = identity;
        _groupName = groupName;
        IsSynced = isSynced;
    }

    /// <summary>Loads the membership. Called by the window on open.</summary>
    public async Task LoadAsync()
    {
        IsBusy = true;
        Status = "Loading members…";
        Members.Clear();
        try
        {
            var members = await _exchange.GetDistributionGroupMembersAsync(_identity);
            foreach (var m in members) Members.Add(m);
            Status = Members.Count == 0 ? "This group has no members." : $"{Members.Count} member(s).";
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error($"Listing members of Exchange group '{_identity}' failed.", ex);
            // ExchangeService humanizes before throwing; re-running it duplicates the guidance sentence.
            Status = "Couldn't list members: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRunWrite))]
    private async Task AddMembersAsync()
    {
        var picked = _dialogs.PickMailboxRecipients($"Add members to “{GroupName}”");
        if (picked is null || picked.Count == 0) return;

        // Skip anyone already in the group rather than spending a round trip to be told so. The add itself is
        // idempotent, so this is about time, not correctness.
        var already = Members.Select(m => m.Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toAdd = picked.Where(p => !already.Contains(p.Identity)).ToList();
        if (toAdd.Count == 0) { Status = "Those recipients are already members."; return; }

        if (!_dialogs.Confirm("Add members", $"Add {toAdd.Count} member(s) to “{GroupName}”?",
                toAdd.Select(m => $"{m.DisplayName} — {m.PrimarySmtpAddress}")))
            return;

        await RunPerMemberAsync("Adding", toAdd,
            m => _exchange.AddDistributionGroupMemberAsync(_identity, m.Identity));
    }

    [RelayCommand(CanExecute = nameof(CanRunWrite))]
    private async Task RemoveMembersAsync(System.Collections.IList? selected)
    {
        var rows = selected?.Cast<MailboxRecipient>().ToList() ?? new List<MailboxRecipient>();
        if (rows.Count == 0) { Status = "Select one or more members to remove."; return; }

        if (!_dialogs.Confirm("Remove members", $"Remove {rows.Count} member(s) from “{GroupName}”?",
                rows.Select(m => $"{m.DisplayName} — {m.PrimarySmtpAddress}")))
            return;

        await RunPerMemberAsync("Removing", rows,
            m => _exchange.RemoveDistributionGroupMemberAsync(_identity, m.Identity));
    }

    /// <summary>
    /// Runs one Exchange call per member and reports the outcome of each. Exchange takes a single member per
    /// call and the channel serialises everything, so a batch is N round trips: one failure must not discard
    /// the rest, and the operator needs to know exactly which ones didn't land.
    /// </summary>
    private async Task RunPerMemberAsync(string verb, IReadOnlyList<MailboxRecipient> members, Func<MailboxRecipient, Task> action)
    {
        IsBusy = true;
        IsWriting = true;
        var items = new List<BulkItemResult>();
        try
        {
            var done = 0;
            foreach (var m in members)
            {
                Status = $"{verb} {++done} of {members.Count}…";
                try
                {
                    await action(m);
                    items.Add(new BulkItemResult(m.Identity, m.DisplayName, true, null));
                }
                catch (Exception ex)
                {
                    AppLog.Instance.Warn($"{verb} '{m.Identity}' on Exchange group '{_identity}' failed: {ex.Message}");
                    items.Add(new BulkItemResult(m.Identity, m.DisplayName, false, ex.Message));
                }
            }
        }
        finally { IsBusy = false; IsWriting = false; }

        _dialogs.ShowBulkResult(new BulkResult(items));
        await LoadAsync(); // re-read rather than patching the list: the server is the authority on what stuck
    }

    private bool CanRunWrite() => CanEdit && !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanWriteNow));
        AddMembersCommand.NotifyCanExecuteChanged();
        RemoveMembersCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Explains a refused close, so a dead Close button isn't silent.</summary>
    public void NoteCloseBlocked() =>
        Status = "Wait for the current change to finish — it's applying one member at a time and the results haven't been reported yet.";
}
