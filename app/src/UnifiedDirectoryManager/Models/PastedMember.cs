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
/// <param name="LineNumber">1-based position in the paste, so a row can be traced back to what was pasted.</param>
/// <param name="Raw">Exactly what was on the line, for showing the operator what this row came from.</param>
/// <param name="Term">The cleaned value the lookup uses.</param>
/// <param name="Shape">Which rung to start on.</param>
/// <param name="NameSearch">
/// For "Doe, Jane", the flipped "Jane Doe" — how the directory actually stores the display name. Null for
/// every other shape.
/// </param>
public sealed record PastedTerm(int LineNumber, string Raw, string Term, PastedTermShape Shape, string? NameSearch = null)
{
    /// <summary>What to search on: the flipped name where there is one, otherwise the term itself.</summary>
    public string SearchText => string.IsNullOrWhiteSpace(NameSearch) ? Term : NameSearch!;
}

/// <summary>A directory principal a pasted line resolved to.</summary>
/// <param name="Identity">What the write uses — an SMTP address, a UPN, or a distinguished name.</param>
/// <param name="DisplayName">What the operator reads.</param>
/// <param name="Secondary">A second identifier worth showing to tell two same-named people apart.</param>
/// <param name="Kind">User, group, contact — for the rows a group cannot accept.</param>
public sealed record MemberCandidate(string Identity, string DisplayName, string? Secondary, string? Kind);

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
/// <param name="Dropped">How many lines were beyond the cap and not taken.</param>
public sealed record PasteParseResult(IReadOnlyList<PastedTerm> Terms, int Duplicates, int Dropped);
