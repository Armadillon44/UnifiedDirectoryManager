using System.IO;
using System.Text;
using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Writes the record kept when a group is deleted: a text file of every populated attribute and a companion CSV
/// of its members. The CSV carries each member's full DN, which is what makes the membership re-addable later
/// (the add-to-groups pickers act on DNs).
///
/// Failures are thrown, not swallowed — a deletion record has to exist BEFORE the delete, so the caller treats a
/// failed write as a reason to skip deleting that group.
/// </summary>
public static class GroupDeletionRecord
{
    /// <summary>Writes the pair for one group and returns the two paths (text first). <paramref name="membershipCaveat"/>
    /// is a non-null warning when the member list could not be confirmed complete — it is stamped prominently into
    /// both files so a future reader is never misled by an authoritative-looking zero.</summary>
    public static (string TextPath, string CsvPath) Write(
        string directory,
        ObjectBasicInfo info,
        string groupTypeLabel,
        IReadOnlyList<AdAttribute> attributes,
        IReadOnlyList<GroupMember> members,
        DateTime timestamp,
        string? membershipCaveat = null)
    {
        Directory.CreateDirectory(directory);

        var displayName = string.IsNullOrWhiteSpace(info.Name)
            ? NameResolver.RdnFallback(info.DistinguishedName)
            : info.Name;
        var stamp = timestamp.ToString("yyyyMMdd-HHmmss");
        // Uniquify: one batch shares a timestamp, so two same-named groups from different OUs would otherwise
        // collide and the second would silently overwrite the first record.
        var prefix = $"group-{OperationLog.SafeFileNamePart(displayName)}-{stamp}";
        var baseName = prefix;
        for (var n = 2; File.Exists(Path.Combine(directory, baseName + ".txt"))
                     || File.Exists(Path.Combine(directory, baseName + "-members.csv")); n++)
            baseName = $"{prefix}-{n}";
        var textPath = Path.Combine(directory, baseName + ".txt");
        var csvPath = Path.Combine(directory, baseName + "-members.csv");

        var sb = new StringBuilder();
        sb.AppendLine("Unified Directory Manager — deleted group record");
        sb.AppendLine("================================================================");
        sb.AppendLine($"Group        : {displayName}");
        sb.AppendLine($"Type         : {(string.IsNullOrWhiteSpace(groupTypeLabel) ? "(unknown)" : groupTypeLabel)}");
        sb.AppendLine($"DN           : {info.DistinguishedName}");
        if (!string.IsNullOrWhiteSpace(info.CanonicalName)) sb.AppendLine($"Canonical    : {info.CanonicalName}");
        sb.AppendLine($"Members      : {members.Count}{(membershipCaveat is null ? string.Empty : "  ** SEE WARNING **")}");
        // "Recorded at", not "Deleted at": this file is written BEFORE the delete, which may still fail.
        sb.AppendLine($"Recorded at  : {timestamp:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Members file : {Path.GetFileName(csvPath)}");
        if (membershipCaveat is not null)
        {
            sb.AppendLine();
            sb.AppendLine("*** WARNING: " + membershipCaveat + " ***");
        }
        sb.AppendLine();
        sb.AppendLine("Attributes");
        sb.AppendLine("----------------------------------------------------------------");
        foreach (var a in attributes.OrderBy(a => a.LdapName, StringComparer.OrdinalIgnoreCase))
        {
            // Skip the member attribute: it has its own section + CSV below, and for a large group the load
            // returns it renamed "member;range=0-1499" — dumping that would show a silently truncated list.
            if (a.LdapName.Equals("member", StringComparison.OrdinalIgnoreCase)
                || a.LdapName.StartsWith("member;range=", StringComparison.OrdinalIgnoreCase)) continue;

            // For DN-valued attributes (memberOf, managedBy, manager) write the DN alongside the friendly name —
            // the DN is what makes nesting and ownership restorable; a display name alone is not.
            var count = Math.Max(a.DisplayValues.Count, a.RawValues.Count);
            if (count == 0) continue;
            var rendered = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var display = i < a.DisplayValues.Count ? a.DisplayValues[i] : null;
                var raw = i < a.RawValues.Count ? a.RawValues[i] : null;
                rendered.Add(a.IsDnValued && !string.IsNullOrEmpty(raw) && !string.Equals(display, raw, StringComparison.Ordinal)
                    ? $"{display ?? raw}  —  {raw}"
                    : display ?? raw ?? string.Empty);
            }

            if (rendered.Count == 1)
            {
                sb.AppendLine($"{a.LdapName} ({a.FriendlyName}): {rendered[0]}");
            }
            else
            {
                sb.AppendLine($"{a.LdapName} ({a.FriendlyName}):");
                foreach (var v in rendered) sb.AppendLine("    • " + v);
            }
        }
        sb.AppendLine();
        sb.AppendLine($"Members ({members.Count}) — full list with DNs is in {Path.GetFileName(csvPath)}");
        sb.AppendLine("----------------------------------------------------------------");
        foreach (var m in members) sb.AppendLine($"{m.Name}  —  {m.DistinguishedName}");

        // No BOM for the human-readable record; the CSV gets one so Excel reads it as UTF-8.
        File.WriteAllText(textPath, sb.ToString());

        var csv = new StringBuilder();
        csv.AppendLine(CsvText.Row(new[] { "Group", "Member", "DistinguishedName", "Note" }));
        foreach (var m in members)
            csv.AppendLine(CsvText.Row(new[] { displayName, m.Name, m.DistinguishedName, string.Empty }));
        // A caveat has to travel with the CSV too — otherwise a header-only file reads as "this group was empty".
        if (membershipCaveat is not null)
            csv.AppendLine(CsvText.Row(new[] { displayName, string.Empty, string.Empty, "WARNING: " + membershipCaveat }));
        File.WriteAllText(csvPath, csv.ToString(), new UTF8Encoding(true));

        AppLog.Instance.Info($"Wrote deleted-group record for {info.DistinguishedName} ({members.Count} member(s)) to {textPath}.");
        return (textPath, csvPath);
    }
}
