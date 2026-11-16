using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// The Rename Group dialog, for every kind of group. It shows the names the group's kind has (on-prem: name,
/// pre-Windows 2000 name and display name; Exchange: name and display name; Entra: the one name), keeps the
/// derived ones following the name until the operator edits them, and leaves every check and write to
/// <see cref="GroupRenameService"/>.
/// </summary>
public partial class RenameGroupViewModel : ObservableObject
{
    private readonly GroupRenameService _service;
    private readonly IDialogService _dialogs;
    private readonly GroupRenameSource _source;
    private readonly Func<CancellationToken, Task<string>>? _syncNow;

    /// <summary>Set while fields are filled from the group, so filling them is not mistaken for typing.</summary>
    private bool _filling;

    // Each derived field follows the name only while it still holds what was last derived for it.
    private string? _samSuggestion;
    private string? _displaySuggestion;
    private string? _addressSuggestion;

    public RenameGroupViewModel(GroupRenameService service, IDialogService dialogs, GroupRenameSource source,
        Func<CancellationToken, Task<string>>? syncNow)
    {
        _service = service;
        _dialogs = dialogs;
        _source = source;
        _syncNow = syncNow;
        _title = string.IsNullOrWhiteSpace(source.DisplayHint) ? "Rename group" : $"Rename “{source.DisplayHint}”";
    }

    [ObservableProperty] private string _title;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _loadFailed;
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRenameButton), nameof(CloseLabel), nameof(ShowNotes), nameof(IsEditable))]
    private bool _isDone;

    public bool ShowRenameButton => !IsDone;
    /// <summary>Cancel before anything is written; Close after, because by then there is nothing to cancel.</summary>
    public string CloseLabel => IsDone ? "Close" : "Cancel";
    public bool ShowNotes => !IsDone;
    public bool IsEditable => !IsDone;
    public bool HasResult => ResultLines.Count > 0;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _kindLabel = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowName), nameof(ShowSam), nameof(CanChangeAddress), nameof(CurrentAddress),
                              nameof(NameLabel), nameof(ShowForm))]
    private GroupRenameSubject? _subject;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _samAccountName = string.Empty;
    [ObservableProperty] private bool _changeAddress;
    [ObservableProperty] private string _newAddress = string.Empty;
    [ObservableProperty] private bool _showSyncNow;

    /// <summary>What the operator should know about this kind of group before renaming it.</summary>
    public ObservableCollection<string> Notes { get; } = new();

    /// <summary>What the rename did, read back from the group, once it has run.</summary>
    public ObservableCollection<string> ResultLines { get; } = new();

    /// <summary>The last rename's outcome; null until something was written. The caller refreshes from it.</summary>
    public GroupRenameOutcome? Outcome { get; private set; }

    public bool ShowForm => Subject is not null;
    /// <summary>On-prem and Exchange groups have a name of their own besides the display name.</summary>
    public bool ShowName => Subject?.HasSeparateName == true;
    public bool ShowSam => Subject?.Kind == GroupRenameKind.OnPremAd;
    public bool CanChangeAddress => Subject?.IsMailEnabled == true;
    public string CurrentAddress => Subject?.PrimaryAddress ?? string.Empty;
    public string NameLabel => Subject?.Kind == GroupRenameKind.OnPremAd ? "Group name" : "Name";

    /// <summary>Reads the group and fills the form. The window calls this once it is shown.</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try { Fill(await _service.LoadAsync(_source)); }
        catch (Exception ex)
        {
            LoadFailed = true;
            Status = ex is InvalidOperationException ? ex.Message : "The group could not be read: " + GroupRenameService.Friendly(ex);
        }
        finally { IsLoading = false; }
    }

    private void Fill(GroupRenameSubject s)
    {
        _filling = true;
        try
        {
            Subject = s;
            KindLabel = s.KindLabel;
            Name = s.HasSeparateName ? s.Name : s.DisplayName;
            DisplayName = s.DisplayName;
            SamAccountName = s.SamAccountName ?? string.Empty;
            ChangeAddress = false;
            NewAddress = s.PrimaryAddress ?? string.Empty;

            // Derived names follow the group name only if they followed it before: a pre-Windows 2000 name that
            // was made from the name the way ADUC makes it, and a display name that is empty or the same as the name.
            _samSuggestion = s.Kind == GroupRenameKind.OnPremAd
                             && string.Equals(s.SamAccountName, GroupNaming.DeriveGroupSam(s.Name), StringComparison.Ordinal)
                ? s.SamAccountName : null;
            _displaySuggestion = s.HasSeparateName
                                 && (string.IsNullOrEmpty(s.DisplayName) || string.Equals(s.DisplayName, s.Name, StringComparison.Ordinal))
                ? s.DisplayName : null;
            _addressSuggestion = null;
        }
        finally { _filling = false; }

        Notes.Clear();
        if (s.RedirectedFromCloud)
            Notes.Add("This group is synchronized from Active Directory, so it is renamed there. Microsoft 365 shows the new name after the next Entra Connect sync.");
        switch (s.Kind)
        {
            case GroupRenameKind.OnPremAd when !s.RedirectedFromCloud:
                Notes.Add("Members, permissions and the link to Microsoft 365 are unaffected: they don't depend on the name.");
                break;
            case GroupRenameKind.Microsoft365:
                Notes.Add(s.IsTeam
                    ? "The Team takes the new name. Its SharePoint site address does not change."
                    : "The group's SharePoint site address does not change.");
                Notes.Add("A naming policy for Microsoft 365 groups may add a prefix or suffix; the name it sets is shown afterwards.");
                break;
            case GroupRenameKind.ExchangeDistribution:
                Notes.Add("The name and the display name are changed together, so the address book shows the new name.");
                break;
        }
    }

    partial void OnNameChanged(string value)
    {
        if (_filling || Subject is null) return;
        if (_samSuggestion is not null && string.Equals(SamAccountName, _samSuggestion, StringComparison.Ordinal))
        {
            _samSuggestion = GroupNaming.DeriveGroupSam(value);
            SamAccountName = _samSuggestion;
        }
        if (_displaySuggestion is not null && string.Equals(DisplayName, _displaySuggestion, StringComparison.Ordinal))
        {
            _displaySuggestion = value;
            DisplayName = value;
        }
        if (Subject.HasSeparateName) FollowAddress(value);
    }

    partial void OnDisplayNameChanged(string value)
    {
        // An Entra group has only the display name, so the address follows that.
        if (_filling || Subject is null || Subject.HasSeparateName) return;
        FollowAddress(value);
    }

    partial void OnChangeAddressChanged(bool value)
    {
        if (_filling || Subject is null) return;
        if (!value) { NewAddress = Subject.PrimaryAddress ?? string.Empty; _addressSuggestion = null; return; }
        // Suggest one only if the old address was built from the old name in a way that can be repeated.
        var oldName = Subject.HasSeparateName ? Subject.Name : Subject.DisplayName;
        var suggestion = GroupNaming.SuggestAddress(oldName, Subject.PrimaryAddress, Subject.HasSeparateName ? Name : DisplayName);
        if (suggestion is not null) { _addressSuggestion = suggestion; NewAddress = suggestion; }
    }

    private void FollowAddress(string newName)
    {
        if (!ChangeAddress || _addressSuggestion is null || !string.Equals(NewAddress, _addressSuggestion, StringComparison.Ordinal)) return;
        var oldName = Subject!.HasSeparateName ? Subject.Name : Subject.DisplayName;
        var next = GroupNaming.SuggestAddress(oldName, Subject.PrimaryAddress, newName);
        if (next is null) return;
        _addressSuggestion = next;
        NewAddress = next;
    }

    /// <summary>The request the form describes.</summary>
    public GroupRenameRequest BuildRequest() => new(
        Name: Subject?.HasSeparateName == true ? Name : DisplayName,
        DisplayName: DisplayName,
        SamAccountName: ShowSam ? SamAccountName : null,
        NewPrimaryAddress: ChangeAddress ? NewAddress : null);

    private bool CanRename() => Subject is not null && !IsBusy && !IsDone;

    partial void OnIsBusyChanged(bool value) => RenameCommand.NotifyCanExecuteChanged();
    partial void OnIsDoneChanged(bool value) => RenameCommand.NotifyCanExecuteChanged();
    partial void OnSubjectChanged(GroupRenameSubject? value) => RenameCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanRename))]
    private async Task RenameAsync()
    {
        if (Subject is not { } subject || IsBusy) return;
        var request = BuildRequest();
        IsBusy = true;
        Status = "Checking…";
        try
        {
            var check = await _service.CheckAsync(subject, request);
            if (check.Problems.Count > 0) { Status = string.Join(Environment.NewLine, check.Problems); return; }
            if (check.Changes.Count == 0) { Status = "Nothing has changed."; return; }

            var lines = check.Changes.Select(c => $"{c.What}: {Shown(c.Before)}  →  {c.After}").ToList();
            if (check.Warnings.Count > 0)
            {
                lines.Add(string.Empty);
                lines.AddRange(check.Warnings.Select(w => "• " + w));
            }
            if (!_dialogs.Confirm("Rename group", $"Rename “{(subject.HasSeparateName ? subject.Name : subject.DisplayName)}”?", lines))
            {
                Status = string.Empty;
                return;
            }

            Status = "Renaming…";
            var outcome = await _service.ApplyAsync(subject, request);
            // Kept even when it failed: the caller still refreshes, because part of it may have been written.
            Outcome = outcome;
            ShowResult(subject, outcome);
            if (outcome.Succeeded)
            {
                IsDone = true;
                Status = "Renamed.";
                ShowSyncNow = subject.Kind == GroupRenameKind.OnPremAd && _syncNow is not null;
            }
            else
            {
                Status = outcome.Error ?? "The rename did not finish.";
                // Some of it was written. Read the group again so a second try asks only for what is left.
                if (outcome.NamePending)
                {
                    // The names typed are kept. The address is not: it was written in the same modify as the other
                    // names, so after the read it is already the group's primary and there is nothing left to ask.
                    var keep = (Name, DisplayName, SamAccountName);
                    try
                    {
                        Fill(await _service.LoadAsync(new GroupRenameSource(GroupRenameOrigin.OnPremDn, outcome.Identity)));
                        _filling = true;
                        (Name, DisplayName, SamAccountName) = (keep.Name, keep.DisplayName, keep.SamAccountName);
                        _filling = false;
                    }
                    catch (Exception ex) { Status += Environment.NewLine + "It could not be read again: " + GroupRenameService.Friendly(ex); }
                }
            }
        }
        catch (Exception ex) { Status = "The rename failed: " + GroupRenameService.Friendly(ex); }
        finally { IsBusy = false; }

        static string Shown(string s) => string.IsNullOrWhiteSpace(s) ? "(none)" : s;
    }

    private void ShowResult(GroupRenameSubject before, GroupRenameOutcome outcome)
    {
        ResultLines.Clear();
        if (before.HasSeparateName) ResultLines.Add($"Name: {outcome.ActualName}");
        ResultLines.Add($"Display name: {(string.IsNullOrWhiteSpace(outcome.ActualDisplayName) ? "(none)" : outcome.ActualDisplayName)}");
        if (before.Kind == GroupRenameKind.OnPremAd && outcome.Succeeded)
        {
            var sam = BuildRequest().SamAccountName;
            if (!string.IsNullOrWhiteSpace(sam)) ResultLines.Add($"Pre-Windows 2000 name: {sam}");
        }
        if (!string.IsNullOrWhiteSpace(outcome.ActualPrimaryAddress)) ResultLines.Add($"Email address: {outcome.ActualPrimaryAddress}");
        foreach (var note in outcome.Notes) ResultLines.Add("• " + note);
        OnPropertyChanged(nameof(HasResult));
    }

    private bool CanSyncNow() => ShowSyncNow && !IsBusy;

    partial void OnShowSyncNowChanged(bool value) => SyncNowCommand.NotifyCanExecuteChanged();

    /// <summary>Starts an Entra Connect delta sync, so a synced group's new name reaches the cloud without waiting.</summary>
    [RelayCommand(CanExecute = nameof(CanSyncNow))]
    private async Task SyncNowAsync()
    {
        if (_syncNow is null) return;
        IsBusy = true;
        SyncNowCommand.NotifyCanExecuteChanged();
        Status = "Starting a delta sync…";
        try { Status = await _syncNow(CancellationToken.None); }
        catch (Exception ex) { Status = "The sync could not be started: " + ex.Message; }
        finally
        {
            IsBusy = false;
            SyncNowCommand.NotifyCanExecuteChanged();
        }
    }
}
