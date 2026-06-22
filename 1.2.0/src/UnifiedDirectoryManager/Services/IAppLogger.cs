namespace UnifiedDirectoryManager.Services;

public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

/// <summary>Minimal application logger abstraction.</summary>
public interface IAppLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}

/// <summary>
/// App-wide logger access point. Defaults to a no-op so code can log unconditionally; the real
/// <see cref="FileLogger"/> is installed at startup. (Static rather than DI to avoid threading a
/// logger through every view model in this single-composition-root desktop app.)
/// </summary>
public static class AppLog
{
    public static IAppLogger Instance { get; set; } = new NullLogger();

    /// <summary>Directory the active logger writes to (empty until a file logger is installed).</summary>
    public static string LogDirectory { get; set; } = string.Empty;

    private sealed class NullLogger : IAppLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
