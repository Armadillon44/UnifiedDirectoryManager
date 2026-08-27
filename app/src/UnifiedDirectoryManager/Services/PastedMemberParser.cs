using System.Text.RegularExpressions;
using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Turns a pasted block of people into lines worth looking up, and decides what a rung of the resolution
/// ladder found. Deliberately pure: no directory, no UI, no async. This is where a batch add goes wrong
/// quietly — a mis-parsed line resolves to a real person who is not the one that was pasted — so it is the
/// part that has to be testable on its own.
/// </summary>
public static class PastedMemberParser
{
    /// <summary>The most lines one paste will take. Beyond this the caller says so rather than truncating quietly.</summary>
    public const int MaxLines = 100;

    // A spreadsheet column, a bulleted list and an Outlook address line all arrive with decoration. None of it
    // is part of the name, and all of it stops an exact match dead.
    private static readonly Regex LeadingBullet = new(@"^\s*(?:[•\-\*–•]|\d+[\.\)])\s+", RegexOptions.Compiled);
    private static readonly Regex CollapseSpace = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Splits a paste into terms. Blank lines are skipped rather than reported: a trailing newline is not an
    /// operator error. Lines naming someone an earlier line already named are counted and dropped, so the
    /// grid does not show the same person twice and the write does not add them twice.
    /// </summary>
    public static PasteParseResult Parse(string? pasted, int maxLines = MaxLines)
    {
        var terms = new List<PastedTerm>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int duplicates = 0, dropped = 0, line = 0;

        foreach (var raw in (pasted ?? string.Empty).Split('\n'))
        {
            line++;
            var cleaned = Clean(raw);
            if (cleaned.Length == 0) continue;

            if (terms.Count >= maxLines) { dropped++; continue; }
            if (!seen.Add(cleaned)) { duplicates++; continue; }

            terms.Add(Classify(line, raw.TrimEnd('\r', '\n'), cleaned));
        }
        return new PasteParseResult(terms, duplicates, dropped);
    }

    /// <summary>Strips the decoration a paste arrives with, without touching the name inside it.</summary>
    public static string Clean(string? rawLine)
    {
        var s = (rawLine ?? string.Empty).Replace('\t', ' ').Trim();
        if (s.Length == 0) return string.Empty;

        s = LeadingBullet.Replace(s, string.Empty);
        // Outlook and CSV both hand over trailing separators; a quoted cell keeps its quotes.
        s = s.Trim().Trim(';', ',').Trim();
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            s = s[1..^1].Trim();
        // "<jane.doe@contoso.com>" and "Jane Doe <jane.doe@contoso.com>" both mean the address.
        var open = s.LastIndexOf('<');
        var close = s.LastIndexOf('>');
        if (open >= 0 && close > open) s = s[(open + 1)..close].Trim();

        return CollapseSpace.Replace(s, " ");
    }

    /// <summary>
    /// Picks the starting rung. Order matters: an address wins over everything because it is unambiguous, and
    /// a comma only means "Last, First" once an address has been ruled out.
    /// </summary>
    public static PastedTerm Classify(int lineNumber, string raw, string cleaned)
    {
        if (cleaned.Contains('@'))
            return new PastedTerm(lineNumber, raw, cleaned, PastedTermShape.UpnOrSmtp);

        var comma = cleaned.IndexOf(',');
        if (comma > 0 && comma < cleaned.Length - 1)
        {
            var last = cleaned[..comma].Trim();
            var first = cleaned[(comma + 1)..].Trim();
            if (last.Length > 0 && first.Length > 0)
                return new PastedTerm(lineNumber, raw, cleaned, PastedTermShape.LastFirst, $"{first} {last}");
        }

        return cleaned.Contains(' ')
            ? new PastedTerm(lineNumber, raw, cleaned, PastedTermShape.DisplayName)
            : new PastedTerm(lineNumber, raw, cleaned, PastedTermShape.AccountName);
    }

    /// <summary>
    /// The rungs to try for a shape, in order. Detection chooses where to start, not where to stop — a name
    /// that looked like an alias is still tried as a display name, and everything falls through to a search.
    /// </summary>
    public static IReadOnlyList<MemberLookupKind> Ladder(PastedTermShape shape) => shape switch
    {
        // An address that does not resolve exactly is worth searching for: it may be an old address, or the
        // local part may be the alias.
        PastedTermShape.UpnOrSmtp => [MemberLookupKind.Address, MemberLookupKind.Search],
        PastedTermShape.AccountName => [MemberLookupKind.AccountName, MemberLookupKind.Address, MemberLookupKind.DisplayName, MemberLookupKind.Search],
        // "Doe, Jane" is a name; there is no identifier in it to try.
        PastedTermShape.LastFirst => [MemberLookupKind.DisplayName, MemberLookupKind.Search],
        _ => [MemberLookupKind.DisplayName, MemberLookupKind.AccountName, MemberLookupKind.Search],
    };

    /// <summary>
    /// Decides what a rung's candidates mean. The rule the whole feature turns on: only an EXACT match
    /// resolves a row by itself. A search that happens to return one candidate is still a guess about a
    /// half-specified name, so it asks — and an exact match that returns several always asks, because two
    /// people really can share a display name and picking the first is how the wrong one gets added.
    /// </summary>
    /// <returns>The outcome, or null when this rung found nothing and the next one should be tried.</returns>
    public static MemberResolution? FromRung(PastedTerm term, MemberLookupKind rung, IReadOnlyList<MemberCandidate> candidates)
    {
        var found = candidates ?? [];
        if (found.Count == 0) return null;

        if (rung == MemberLookupKind.Search || found.Count > 1)
            return new MemberResolution(term, MemberMatch.Choose, null, found);

        return new MemberResolution(term, MemberMatch.Resolved, found[0], found);
    }

    /// <summary>What every rung came to when none of them found anything.</summary>
    public static MemberResolution NotFound(PastedTerm term) =>
        new(term, MemberMatch.NotFound, null, [], "No match in this directory.");
}

/// <summary>Which way a backend is being asked to look something up.</summary>
public enum MemberLookupKind
{
    /// <summary>Exact UPN, primary SMTP or proxy address.</summary>
    Address,
    /// <summary>Exact sAMAccountName (AD) or alias (Exchange).</summary>
    AccountName,
    /// <summary>Exact, whole display name.</summary>
    DisplayName,
    /// <summary>Ambiguous search — ANR, or Graph $search. Its hits never resolve a row on their own.</summary>
    Search,
}
