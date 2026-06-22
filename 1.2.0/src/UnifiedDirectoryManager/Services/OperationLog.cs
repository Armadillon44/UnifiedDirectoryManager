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

    /// <summary>Turns an arbitrary label (e.g. a scenario name) into a safe file-name fragment.</summary>
    public static string SafeFileNamePart(string name)
    {
        var cleaned = new string((name ?? string.Empty)
            .Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "log" : cleaned;
    }
}
