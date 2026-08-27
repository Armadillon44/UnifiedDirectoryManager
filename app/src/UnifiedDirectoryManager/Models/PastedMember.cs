namespace UnifiedDirectoryManager.Models;

/// <summary>
/// What a pasted line looks like. This picks the resolution ladder's STARTING rung, never the answer: a line
/// that looks like an account name is still tried as a display name once the account-name lookup misses.
/// Guessing the shape wrong must cost a retry, not the wrong person.
/// </summary>
public enum PastedTermShape
{
    /// <summary>Contains an @ — a UPN or an SMTP address.</summary>
    UpnOrSmtp,
    /// <summary>"Doe, Jane" — the shape a spreadsheet column arrives in.</summary>
    LastFirst,
    /// <summary>A single token with no spaces — a sAMAccountName or an Exchange alias.</summary>
    AccountName,
    /// <summary>Anything else — treated as a display name.</summary>
    DisplayName,
}

/// <summary>One line of a paste, cleaned up and classified.</summary>
/// <param name="Index">Unique across the paste. ONE LINE CAN YIELD SEVERAL TERMS, so the line number does
/// not identify a term and cannot be used to match a backend's answers back to what was asked.</param>
/// <param name="LineNumber">1-based position in the paste, so a row can be traced back to what was pasted.</param>
/// <param name="Raw">Exactly what was on the line, for showing the operator what this row came from.</param>
/// <param name="Term">The cleaned value the lookup uses.</param>
/// <param name="Shape">Which rung to start on.</param>
/// <param name="NameSearch">
/// For "Doe, Jane", the flipped "Jane Doe" — how the directory actually stores the display name. Null for
/// every other shape.
/// </param>
public sealed record PastedTerm(int Index, int LineNumber, string Raw, string Term, PastedTermShape Shape, string? NameSearch = null)
{
    /// <summary>What to search on: the flipped name where there is one, otherwise the term itself.</summary>
    public string SearchText => string.IsNullOrWhiteSpace(NameSearch) ? Term : NameSearch!;
}

/// <summary>A directory principal a pasted line resolved to.</summary>
/// <param name="Identity">What the write uses — an SMTP address, a UPN, or a distinguished name.</param>
/// <param name="DisplayName">What the operator reads.</param>
/// <param name="Secondary">A second identifier worth showing to tell two same-named people apart.</param>
/// <param name="Kind">User, group, contact — for the rows a group cannot accept.</param>
public sealed record MemberCandidate(string Identity, string DisplayName, string? Secondary, string? Kind)
{
    /// <summary>
    /// How this candidate is shown wherever the operator decides something. The DISCRIMINATOR matters more
    /// than the name: telling two people called John Smith apart is the entire purpose of the Choose step,
    /// and an Entra object id does not do it — Identity is a GUID there, so Secondary (the sign-in name) is
    /// what a person can actually recognise.
    ///
    /// The kind is appended when it is not a person, because a group in a list of people is nearly always a
    /// mistake and nesting one is invisible once it is done.
    /// </summary>
    public string Label
    {
        get
        {
            var who = string.IsNullOrWhiteSpace(Secondary) ? Identity : Secondary!;
            var label = string.IsNullOrWhiteSpace(DisplayName) ? who
                      : string.Equals(DisplayName, who, StringComparison.OrdinalIgnoreCase) ? DisplayName
                      : $"{DisplayName} — {who}";
            return IsPerson ? label : $"{label}  [{Kind}]";
        }
    }

    /// <summary>False for a group, which can legitimately be nested but is rarely what a paste of people meant.</summary>
    public bool IsPerson =>
        string.IsNullOrWhiteSpace(Kind)
        || Kind!.Contains("User", StringComparison.OrdinalIgnoreCase)
        || Kind!.Contains("Mailbox", StringComparison.OrdinalIgnoreCase)
        || Kind!.Contains("Contact", StringComparison.OrdinalIgnoreCase);
}

/// <summary>How a pasted line came out of the ladder.</summary>
public enum MemberMatch
{
    /// <summary>Exactly one exact match. The only state that resolves itself.</summary>
    Resolved,
    /// <summary>Candidates found, but the operator has to say which — including a single fuzzy hit.</summary>
    Choose,
    /// <summary>Nothing found. The operator can edit the term and try again, or drop the row.</summary>
    NotFound,
}

/// <summary>The outcome for one pasted line.</summary>
public sealed record MemberResolution(
    PastedTerm Term,
    MemberMatch Match,
    MemberCandidate? Chosen,
    IReadOnlyList<MemberCandidate> Candidates,
    string? Note = null);

/// <summary>The result of parsing a whole paste.</summary>
/// <param name="Terms">The lines worth resolving, in order, with duplicates already collapsed.</param>
/// <param name="Duplicates">How many lines named something an earlier line already named.</param>
/// <param name="Dropped">How many entries were beyond the cap and not taken.</param>
/// <param name="Unreadable">
/// How many lines held something, but nothing usable once the decoration was stripped. Counted rather than
/// skipped: a line silently discarded here looks exactly like a line that found nobody.
/// </param>
public sealed record PasteParseResult(IReadOnlyList<PastedTerm> Terms, int Duplicates, int Dropped, int Unreadable);
