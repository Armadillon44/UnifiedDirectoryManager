using System.IO;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Thread-safe rolling file logger. Writes one file per day to
/// %APPDATA%\UnifiedDirectoryManager\Logs\UnifiedDirectoryManager-YYYYMMDD.log and prunes files older than the retention window.
/// </summary>
public sealed class FileLogger : IAppLogger
{
    private readonly object _gate = new();
    private readonly LogLevel _minLevel;

    public string Directory { get; }

    public FileLogger(string? directory = null, LogLevel minLevel = LogLevel.Info, int retentionDays = 30)
    {
        Directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnifiedDirectoryManager", "Logs");
        System.IO.Directory.CreateDirectory(Directory);
        _minLevel = minLevel;
        PruneOlderThan(retentionDays);
    }

    private string CurrentFile => Path.Combine(Directory, $"UnifiedDirectoryManager-{DateTime.Now:yyyyMMdd}.log");

    public void Info(string message) => Write(LogLevel.Info, message, null);
    public void Warn(string message) => Write(LogLevel.Warn, message, null);
    public void Error(string message, Exception? exception = null) => Write(LogLevel.Error, message, exception);

    private void Write(LogLevel level, string message, Exception? exception)
    {
        if (level < _minLevel) return;

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] {message}";
        if (exception is not null)
            line += Environment.NewLine + exception;

        lock (_gate)
        {
            try { File.AppendAllText(CurrentFile, line + Environment.NewLine); }
            catch { /* logging must never throw into the app */ }
        }
    }

    private void PruneOlderThan(int days)
    {
        if (days <= 0) return;
        try
        {
            var cutoff = DateTime.Now.AddDays(-days);
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "UnifiedDirectoryManager-*.log"))
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
        }
        catch { /* best-effort cleanup */ }
    }
}
