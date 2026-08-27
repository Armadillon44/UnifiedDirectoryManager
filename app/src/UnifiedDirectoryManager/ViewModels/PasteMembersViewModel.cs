using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// One pasted line in the review grid. Carries what was pasted alongside what it resolved to, because the
/// operator's question is always "is that the person I meant?" and they can only answer it by seeing both.
/// </summary>
public sealed partial class PastedMemberRow : ObservableObject
{
    public int Line { get; }
    public string Pasted { get; }
    public IReadOnlyList<MemberCandidate> Candidates { get; }

    /// <summary>Null until an ambiguous row is settled. Nothing is written for a row without one.</summary>
    [ObservableProperty] private MemberCandidate? _chosen;

    /// <summary>True when this row still needs the operator to say which person is meant.</summary>
    [ObservableProperty] private bool _needsChoice;

    /// <summary>Why this row will not be added, when it will not be.</summary>
    [ObservableProperty] private string _state = string.Empty;

    /// <summary>True when the row will be written. Everything else is shown and skipped.</summary>
    public bool WillAdd => Chosen is not null && !Skip;

    /// <summary>
    /// The other half of <see cref="NeedsChoice"/>, so a settled value can take the cell instead of the
    /// drop-down. A computed inverse rather than a converter, because it has to notify the moment the
    /// operator settles a row and the cell has to swap there and then.
    /// </summary>
    public bool ShowChosen => !NeedsChoice;

    /// <summary>Already a member, the group itself, or nothing found — shown, never written.</summary>
    public bool Skip { get; private init; }

    public PastedMemberRow(MemberResolution resolution, ISet<string> alreadyMembers, string? selfIdentity)
    {
        Line = resolution.Term.LineNumber;
        Pasted = resolution.Term.Raw.Trim();
        Candidates = resolution.Candidates;

        switch (resolution.Match)
        {
            case MemberMatch.Resolved:
                Chosen = resolution.Chosen;
                break;
            case MemberMatch.Choose:
                NeedsChoice = true;
                State = resolution.Candidates.Count == 1
                    // A single fuzzy hit is still a guess about a half-specified name.
                    ? "Close match — confirm"
                    : $"{resolution.Candidates.Count} matches — choose";
                break;
            default:
                Skip = true;
                State = "Not found";
                break;
        }

        // A group cannot be its own member, and Exchange's refusal names neither the group nor the reason.
        if (Chosen is not null && selfIdentity is not null
            && string.Equals(Chosen.Identity, selfIdentity, StringComparison.OrdinalIgnoreCase))
        {
            Skip = true;
            State = "This is the group itself";
            NeedsChoice = false;
        }
        else if (Chosen is not null && alreadyMembers.Contains(Chosen.Identity))
        {
            Skip = true;
            State = "Already a member";
        }
        else if (Chosen is not null)
        {
            State = "Ready";
        }
    }

    partial void OnNeedsChoiceChanged(bool value) => OnPropertyChanged(nameof(ShowChosen));

    partial void OnChosenChanged(MemberCandidate? value)
    {
        if (value is null) return;
        NeedsChoice = false;
        State = "Ready";
        OnPropertyChanged(nameof(WillAdd));
    }
}

/// <summary>
/// Paste a list of people, resolve each line, settle whatever is ambiguous, and hand back the recipients.
/// Deliberately does NOT write: the membership editor that opened it already dedupes, confirms and reports
/// per member, and a second write path is a second set of bugs.
/// </summary>
public partial class PasteMembersViewModel : ObservableObject
{
    private readonly IExchangeService _exchange;
    private readonly ISet<string> _alreadyMembers;
    private readonly string? _selfIdentity;

    [ObservableProperty] private string _pastedText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status =
        "Paste one person per line — an address, a logon name, or a name. Then Resolve.";
    [ObservableProperty] private bool _hasResolved;

    public ObservableCollection<PastedMemberRow> Rows { get; } = new();

    /// <summary>The recipients to add, once the operator accepts. Empty until then.</summary>
    public List<MailboxRecipient> Accepted { get; } = new();

    public PasteMembersViewModel(IExchangeService exchange, IEnumerable<string>? alreadyMembers, string? selfIdentity)
    {
        _exchange = exchange;
        _alreadyMembers = new HashSet<string>(alreadyMembers ?? [], StringComparer.OrdinalIgnoreCase);
        _selfIdentity = string.IsNullOrWhiteSpace(selfIdentity) ? null : selfIdentity;
    }

    private bool CanResolve() => !IsBusy && !string.IsNullOrWhiteSpace(PastedText);

    partial void OnPastedTextChanged(string value) => ResolveCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value) => ResolveCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanResolve))]
    private async Task ResolveAsync()
    {
        var parsed = PastedMemberParser.Parse(PastedText);
        if (parsed.Terms.Count == 0) { Status = "Nothing to resolve."; return; }

        Rows.Clear();
        IsBusy = true;
        Status = $"Resolving {parsed.Terms.Count}…";
        try
        {
            var progress = new Progress<int>(done => Status = $"Resolving {done} of {parsed.Terms.Count}…");
            var resolved = await _exchange.ResolveMembersAsync(parsed.Terms, progress);
            foreach (var r in resolved) Rows.Add(new PastedMemberRow(r, _alreadyMembers, _selfIdentity));
            HasResolved = true;
            Status = Describe(parsed);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("Could not resolve the pasted list: " + ex.Message);
            Status = "Could not resolve the list: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Says what the paste came to, including what was dropped before any lookup happened. A line silently
    /// discarded for being over the cap or a repeat would otherwise look like a line that found nobody.
    /// </summary>
    private string Describe(PasteParseResult parsed)
    {
        var ready = Rows.Count(r => r.WillAdd);
        var parts = new List<string> { $"{ready} ready" };
        var choose = Rows.Count(r => r.NeedsChoice);
        if (choose > 0) parts.Add($"{choose} to confirm");
        var skipped = Rows.Count(r => r.Skip);
        if (skipped > 0) parts.Add($"{skipped} skipped");
        if (parsed.Duplicates > 0) parts.Add($"{parsed.Duplicates} repeated line(s) collapsed");
        if (parsed.Dropped > 0)
            parts.Add($"{parsed.Dropped} line(s) beyond the {PastedMemberParser.MaxLines}-line limit were not read");
        return string.Join(", ", parts) + ".";
    }

    /// <summary>Gathers the rows that will be written. Anything unsettled or skipped is left out.</summary>
    public bool Commit()
    {
        Accepted.Clear();
        foreach (var row in Rows.Where(r => r.WillAdd && r.Chosen is not null))
        {
            var c = row.Chosen!;
            Accepted.Add(new MailboxRecipient
            {
                Identity = c.Identity,
                DisplayName = c.DisplayName,
                PrimarySmtpAddress = c.Identity,
                RecipientType = c.Kind ?? string.Empty,
            });
        }
        return Accepted.Count > 0;
    }
}
