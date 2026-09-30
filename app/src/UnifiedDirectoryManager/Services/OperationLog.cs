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
