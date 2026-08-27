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
    [ObservableProperty] private bool _skip;

    private readonly ISet<string> _alreadyMembers;
    private readonly string? _selfIdentity;
    private readonly bool _notFound;

    public PastedMemberRow(MemberResolution resolution, ISet<string> alreadyMembers, string? selfIdentity)
    {
        Line = resolution.Term.LineNumber;
        Pasted = resolution.Term.Raw.Trim();
        Candidates = resolution.Candidates;
        _alreadyMembers = alreadyMembers;
        _selfIdentity = string.IsNullOrWhiteSpace(selfIdentity) ? null : selfIdentity;
        _notFound = resolution.Match == MemberMatch.NotFound;

        if (resolution.Match == MemberMatch.Choose)
        {
            NeedsChoice = true;
            // A single fuzzy hit is still a guess about a half-specified name.
            State = resolution.Candidates.Count == 1
                ? "Close match — confirm"
                : $"{resolution.Candidates.Count} matches — choose";
        }
        else
        {
            Chosen = resolution.Chosen;   // Settle() runs from the setter
            if (_notFound) Settle();
        }
    }

    /// <summary>
    /// Works out what this row is now. Called both when resolution settles a row and when the OPERATOR does:
    /// the gates below have to apply to a candidate picked from the drop-down just as much as to one the
    /// ladder resolved, or choosing from the list is a way around them.
    /// </summary>
    private void Settle()
    {
        if (Chosen is null)
        {
            Skip = _notFound;
            if (_notFound) State = "Not found";
            return;
        }

        // A group cannot be its own member, and Exchange's refusal names neither the group nor the reason.
        if (_selfIdentity is not null
            && string.Equals(Chosen.Identity, _selfIdentity, StringComparison.OrdinalIgnoreCase))
        {
            Skip = true;
            NeedsChoice = false;
            State = "This is the group itself";
        }
        else if (_alreadyMembers.Contains(Chosen.Identity))
        {
            Skip = true;
            NeedsChoice = false;
            State = "Already a member";
        }
        else
        {
            Skip = false;
            NeedsChoice = false;
            State = "Ready";
        }
        OnPropertyChanged(nameof(WillAdd));
    }

    /// <summary>Marks this row as naming somebody an earlier row already names.</summary>
    public void MarkDuplicateOf(int line)
    {
        Skip = true;
        NeedsChoice = false;
        State = $"Same person as line {line}";
        OnPropertyChanged(nameof(WillAdd));
    }

    partial void OnNeedsChoiceChanged(bool value) => OnPropertyChanged(nameof(ShowChosen));
    partial void OnSkipChanged(bool value) => OnPropertyChanged(nameof(WillAdd));
    partial void OnChosenChanged(MemberCandidate? value) => Settle();
}

/// <summary>
/// Paste a list of people, resolve each line, settle whatever is ambiguous, and hand back the recipients.
/// Deliberately does NOT write: the membership editor that opened it already dedupes, confirms and reports
/// per member, and a second write path is a second set of bugs.
/// </summary>
public partial class PasteMembersViewModel : ObservableObject
{
    private readonly IMemberResolver _resolver;
    private readonly ISet<string> _alreadyMembers;
    private readonly string? _selfIdentity;

    [ObservableProperty] private string _pastedText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status =
        "Paste one person per line — an address, a logon name, or a name. Then Resolve.";
    [ObservableProperty] private bool _hasResolved;

    public ObservableCollection<PastedMemberRow> Rows { get; } = new();

    /// <summary>
    /// The people to add, once the operator accepts. Backend-neutral: Identity is whatever that directory
    /// writes a membership with — an SMTP address, an object id, or a distinguished name.
    /// </summary>
    public List<MemberCandidate> Accepted { get; } = new();

    public PasteMembersViewModel(IMemberResolver resolver, IEnumerable<string>? alreadyMembers, string? selfIdentity)
    {
        _resolver = resolver;
        _alreadyMembers = new HashSet<string>(alreadyMembers ?? [], StringComparer.OrdinalIgnoreCase);
        _selfIdentity = string.IsNullOrWhiteSpace(selfIdentity) ? null : selfIdentity;
    }

    private bool CanResolve() => !IsBusy && !string.IsNullOrWhiteSpace(PastedText);

    /// <summary>What was resolved, so an edit to the box can be told from a re-render of the same text.</summary>
    private string _resolvedText = string.Empty;

    partial void OnPastedTextChanged(string value)
    {
        ResolveCommand.NotifyCanExecuteChanged();
        // The grid describes text that is no longer in the box. Leaving it on screen with Add enabled is an
        // invitation to write the previous list.
        if (HasResolved && !string.Equals(value, _resolvedText, StringComparison.Ordinal))
        {
            HasResolved = false;
            Rows.Clear();
            Status = "The list changed — press Resolve again.";
        }
    }
    partial void OnIsBusyChanged(bool value)
    {
        ResolveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCommit));
    }

    partial void OnHasResolvedChanged(bool value) => OnPropertyChanged(nameof(CanCommit));

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
            var resolved = await _resolver.ResolveAsync(parsed.Terms, progress);
            foreach (var r in resolved) Rows.Add(new PastedMemberRow(r, _alreadyMembers, _selfIdentity));
            // Two lines can name one person without being the same text — "jdoe" and "Jane Doe" both do. The
            // parser collapses repeated TEXT; only now is it known who each line actually meant.
            CollapseSamePerson();
            HasResolved = true;
            _resolvedText = PastedText;
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
    /// Marks any row naming someone an earlier row already names. Without this the confirmation counts a
    /// person twice and claims to add more people than it does.
    /// </summary>
    private void CollapseSamePerson()
    {
        var first = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows)
        {
            if (!row.WillAdd || row.Chosen is null) continue;
            if (first.TryGetValue(row.Chosen.Identity, out var line)) row.MarkDuplicateOf(line);
            else first[row.Chosen.Identity] = row.Line;
        }
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
        if (parsed.Unreadable > 0) parts.Add($"{parsed.Unreadable} line(s) could not be read");
        if (parsed.Dropped > 0)
            parts.Add($"{parsed.Dropped} entr(ies) beyond the {PastedMemberParser.MaxLines} limit were not read");
        return string.Join(", ", parts) + ".";
    }

    /// <summary>True when there is something to add and nothing in flight.</summary>
    public bool CanCommit => HasResolved && !IsBusy;

    /// <summary>Gathers the rows that will be written. Anything unsettled or skipped is left out.</summary>
    public bool Commit()
    {
        Accepted.Clear();
        // A last guard on the identity, not just the row state: whatever reaches here is what gets written.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows.Where(r => r.WillAdd && r.Chosen is not null))
            if (seen.Add(row.Chosen!.Identity)) Accepted.Add(row.Chosen!);
        return Accepted.Count > 0;
    }
}
