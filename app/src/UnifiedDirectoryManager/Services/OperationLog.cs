using System.Globalization;
using System.IO;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Resolves where operation logs (plain-text records of app operations such as scenario runs) are
/// written, honouring the global <see cref="AppSettings.OperationLogDirectory"/> override and falling
/// back to a per-user default under %APPDATA%.
/// </summary>
public static class OperationLog
{
    /// <summary>Default operation-log folder when the user hasn't set one.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UnifiedDirectoryManager", "OperationLogs");

    /// <summary>The effective folder for the given settings (the override if set, else the default).</summary>
    public static string ResolveDirectory(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.OperationLogDirectory)
            ? DefaultDirectory
            : settings.OperationLogDirectory!.Trim();

    /// <summary>
    /// Builds the plain-text record of a single creation — a new user, or a user copied from another.
    ///
    /// The same shape as a scenario's operation log, because an operator filing one of these alongside a
    /// ticket should not have to read two formats. Shared between New User and Copy User so the two cannot
    /// drift; that has already happened once in this codebase, to the naming-token resolver.
    ///
    /// <paramref name="timestamp"/> is passed in rather than read here so the result is reproducible, and
    /// every date is formatted with the invariant culture: this is a record that may be read on another
    /// machine, and a workstation whose default calendar is not Gregorian would otherwise stamp it with a
    /// year five centuries out.
    /// </summary>
    /// <param name="operation">What was done, e.g. "New user".</param>
    /// <param name="subject">Who it was done to — the logon name, or the distinguished name once known.</param>
    /// <param name="performedBy">The bound directory account, or null when it is not known.</param>
    /// <param name="steps">The progress lines exactly as the operator saw them.</param>
    /// <param name="outcome">The closing status line, if there is one.</param>
    public static string BuildCreationRecord(
        string operation,
        string subject,
        string? performedBy,
        DateTime timestamp,
        IEnumerable<string> steps,
        string? outcome)
    {
        var rule = new string('=', 64);
        var thin = new string('-', 64);
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("Unified Directory Manager — creation record");
        sb.AppendLine(rule);
        sb.AppendLine($"Operation    : {Blank(operation, "(unknown)")}");
        sb.AppendLine($"Account      : {Blank(subject, "(not named)")}");
        sb.AppendLine($"Performed by : {Blank(performedBy, "(unknown)")}");
        sb.AppendLine($"Recorded at  : {timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
        sb.AppendLine(thin);

        var any = false;
        foreach (var step in steps ?? Enumerable.Empty<string>()) { sb.AppendLine(step); any = true; }
        // Say so rather than leaving a blank gap that reads like a truncated file.
        if (!any) sb.AppendLine("(no steps were recorded)");

        sb.AppendLine(thin);
        sb.AppendLine($"Outcome      : {Blank(outcome, "(none recorded)")}");
        return sb.ToString();

        static string Blank(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value!.Trim();
    }

    /// <summary>
    /// Fragments that make an attribute name one whose value must never be written down.
    /// </summary>
    /// <remarks>
    /// Nothing can put a secret in this dictionary today — the password is a separate argument to
    /// <c>CreateUserAsync</c> and never travels with the attributes. This exists because the rule that
    /// keeps secrets out of the record is otherwise enforced by GREPPING the source for
    /// <c>Step($"…{Password}…")</c>, and writing out a whole dictionary walks straight past that: the
    /// names are in data, not in source. So the guard has to live where the values are.
    ///
    /// This started as an explicit list of AD's secret attributes with these fragments as a net beneath
    /// it. A mutation check showed the list could be broken with no test noticing, and the reason is that
    /// it was redundant: every one of them — <c>unicodePwd</c>, <c>userPassword</c>, <c>dBCSPwd</c>,
    /// <c>lmPwdHistory</c>, <c>ntPwdHistory</c>, <c>supplementalCredentials</c>,
    /// <c>msDS-ManagedPassword</c>, LAPS's <c>ms-Mcs-AdmPwd</c> and <c>msLAPS-Password</c> — contains one
    /// of these four. A redundant list that looks load-bearing is worse than none, because it invites
    /// maintaining the list instead of the thing that works.
    ///
    /// Matching wide is deliberate. A false positive withholds a value that was safe to print; a false
    /// negative puts a credential in a file that gets attached to tickets. Only one of those is
    /// recoverable, and no attribute a template sets goes anywhere near these words.
    /// </remarks>
    private static readonly string[] SecretFragments = { "password", "pwd", "secret", "credential" };

    /// <summary>Whether an attribute's VALUE must never be written to a record.</summary>
    public static bool IsSecretAttribute(string? ldapName) =>
        !string.IsNullOrWhiteSpace(ldapName) &&
        SecretFragments.Any(f => ldapName.Contains(f, StringComparison.OrdinalIgnoreCase));

    /// <summary>Stands in for a value that must not be recorded. Says it was withheld, not that it was absent.</summary>
    public const string Redacted = "(not recorded)";

    /// <summary>
    /// Formats the attributes an account was created with, one per line, for the progress pane and the
    /// record it is saved to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// lDAPDisplayNames rather than friendly labels: this record gets filed against a ticket and read by
    /// whoever picks it up, and <c>sAMAccountName</c> is the name they can act on. Values are printed as
    /// they were sent, except that a secret-named attribute is replaced with <see cref="Redacted"/>.
    /// </para>
    /// <para>
    /// Returns one line per attribute rather than a single block, so each is its own row in the progress
    /// pane and can be selected and copied on its own.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> DescribeAttributes(IEnumerable<KeyValuePair<string, string>>? attributes)
    {
        var pairs = (attributes ?? Enumerable.Empty<KeyValuePair<string, string>>())
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pairs.Count == 0) return new[] { "• No attributes were set." };

        // Pad to the longest name so the values line up, but not past a width that would push the value
        // off the edge of the pane when one attribute has an unusually long name.
        var width = Math.Min(pairs.Max(kv => kv.Key.Trim().Length), 28);

        var lines = new List<string> { $"• Attributes set ({pairs.Count}):" };
        lines.AddRange(pairs.Select(kv =>
        {
            var name = kv.Key.Trim();
            var value = IsSecretAttribute(name) ? Redacted
                : string.IsNullOrEmpty(kv.Value) ? "(empty)"
                : kv.Value;
            return $"    {name.PadRight(width)}  {value}";
        }));
        return lines;
    }

    /// <summary>
    /// A default file name for a creation record, e.g. <c>new-user-jdoe-20260930-142211.log</c>. Shaped like
    /// the deleted-group records so one folder of operation logs sorts and reads consistently.
    /// </summary>
    public static string SuggestFileName(string prefix, string subject, DateTime timestamp) =>
        $"{prefix}-{SafeFileNamePart(subject)}-{timestamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log";

    /// <summary>Turns an arbitrary label (e.g. a scenario name) into a safe file-name fragment.</summary>
    public static string SafeFileNamePart(string name)
    {
        var cleaned = new string((name ?? string.Empty)
            .Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "log" : cleaned;
    }
}
