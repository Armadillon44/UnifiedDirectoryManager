using System.IO;
using System.Text;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Builds the CSV of one or more groups' memberships, and works out what appending to an existing file
/// would take.
///
/// The export is flat — one row per member, with the group repeated — because several groups go into one
/// file and a reader has to be able to tell whose member is whose. Sorting or filtering by the Group column
/// then recovers the grouping in any spreadsheet.
/// </summary>
public static class GroupMemberCsv
{
    /// <summary>
    /// The column row. Also the contract for appending: a file whose first line is not exactly this is not
    /// one of these exports, and appending to it would produce a file that cannot be read as either.
    /// </summary>
    public static readonly string Header = CsvText.Row(new[] { "Group", "Group DN", "Member", "Member DN", "Status" });

    /// <summary>Status text for a group whose membership came back incomplete.</summary>
    public const string IncompleteStatus = "INCOMPLETE - the member list was cut short; this group has more members";

    /// <summary>Status text for a group whose membership could not be read at all.</summary>
    public const string UnreadableStatus = "UNREADABLE - membership could not be read; this is NOT a confirmed empty group";

    /// <summary>Status text for a group confirmed to have no members.</summary>
    public const string EmptyStatus = "No members";

    /// <summary>
    /// The data rows for one group. No header — the caller decides whether one is needed.
    ///
    /// A group that produced no rows still gets one, saying which of the two reasons applies. Omitting it
    /// would drop the group out of the export entirely, and a reader comparing the file against the list of
    /// groups they exported would have no way to tell an empty group from one that was silently skipped.
    ///
    /// <see cref="GroupMembersResult.Truncated"/> and <see cref="GroupMembersResult.Unconfirmed"/> are
    /// carried into the rows rather than reported only on screen. This file gets mailed around and filed
    /// against tickets; the warning has to travel with it, not stay behind in a dialog nobody kept.
    /// </summary>
    public static IReadOnlyList<string> Rows(string groupName, string groupDn, GroupMembersResult members)
    {
        var name = groupName ?? string.Empty;
        var dn = groupDn ?? string.Empty;
        var rows = new List<string>();

        if (members is null || members.Members.Count == 0)
        {
            rows.Add(CsvText.Row(new[]
            {
                name, dn, string.Empty, string.Empty,
                members?.Unconfirmed != false ? UnreadableStatus : EmptyStatus,
            }));
            return rows;
        }

        // On a truncated read EVERY row carries the warning. A single marker row would be lost the moment
        // somebody sorts the sheet by name, which is the first thing anyone does with a member list.
        var status = members.Truncated ? IncompleteStatus : string.Empty;
        foreach (var member in members.Members)
            rows.Add(CsvText.Row(new[] { name, dn, member.Name ?? string.Empty, member.DistinguishedName ?? string.Empty, status }));

        return rows;
    }

    /// <summary>What writing to <paramref name="path"/> requires, or why it cannot be appended to.</summary>
    /// <param name="CanWrite">False only when the file exists but is not one of these exports.</param>
    /// <param name="WriteHeader">True when the file is new or empty and needs the column row.</param>
    /// <param name="PrefixNewline">True when the existing file does not end in a line break, so the first
    /// appended row would otherwise be glued onto the last existing one.</param>
    /// <param name="Problem">Why the append was refused, for the operator.</param>
    public sealed record AppendPlan(bool CanWrite, bool WriteHeader, bool PrefixNewline, string? Problem);

    /// <summary>A plan for writing a brand-new file, replacing anything already there.</summary>
    public static AppendPlan FreshFile => new(CanWrite: true, WriteHeader: true, PrefixNewline: false, Problem: null);

    /// <summary>
    /// Inspects an existing file and decides whether these rows can be appended to it.
    ///
    /// Refusing a file with different columns is the point. Appending five-column rows to somebody's
    /// three-column spreadsheet produces a file that no longer parses as either thing, and the damage is
    /// only noticed later by whoever opens it.
    /// </summary>
    public static AppendPlan PlanAppend(string path)
    {
        try
        {
            if (!File.Exists(path)) return FreshFile;

            var info = new FileInfo(path);
            if (info.Length == 0) return FreshFile;

            var first = File.ReadLines(path).FirstOrDefault();
            // A UTF-8 BOM is invisible to a person looking at the file and would otherwise fail the compare.
            if (first is not null) first = first.TrimStart('﻿');

            if (!string.Equals(first, Header, StringComparison.OrdinalIgnoreCase))
                return new AppendPlan(false, false, false,
                    "That file is not a group-member export from this app — its first line is not the expected "
                    + $"column row.{Environment.NewLine}{Environment.NewLine}Expected: {Header}"
                    + $"{Environment.NewLine}Found:    {(string.IsNullOrWhiteSpace(first) ? "(blank)" : first)}");

            return new AppendPlan(true, false, !EndsWithNewline(path), null);
        }
        catch (Exception ex)
        {
            return new AppendPlan(false, false, false, "That file could not be read: " + ex.Message);
        }
    }

    /// <summary>The exact text to write, given a plan and the rows.</summary>
    public static string Compose(AppendPlan plan, IEnumerable<string> rows)
    {
        var sb = new StringBuilder();
        if (plan.PrefixNewline) sb.Append(Environment.NewLine);
        if (plan.WriteHeader) sb.Append(Header).Append(Environment.NewLine);
        foreach (var row in rows) sb.Append(row).Append(Environment.NewLine);
        return sb.ToString();
    }

    /// <summary>True when the file's last byte is a line break.</summary>
    private static bool EndsWithNewline(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length == 0) return true; // nothing to glue onto
        stream.Seek(-1, SeekOrigin.End);
        var last = stream.ReadByte();
        return last == '\n' || last == '\r';
    }
}
