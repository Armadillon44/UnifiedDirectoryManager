using System.IO;
using System.Text.Json;

namespace UnifiedDirectoryManager.Services;

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);

    /// <summary>
    /// Set when the last <see cref="Load"/> found an unreadable file and set it aside. The caller is
    /// expected to tell the operator: settings coming back as defaults is otherwise indistinguishable from
    /// a first run, and the first save then overwrites what was salvaged.
    /// </summary>
    string? RecoveredFrom { get; }
}

/// <summary>Persists <see cref="AppSettings"/> as JSON in %APPDATA%\UnifiedDirectoryManager\settings.json.</summary>
public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;

    public string? RecoveredFrom { get; private set; }

    public SettingsStore(string? directory = null)
    {
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnifiedDirectoryManager");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        RecoveredFrom = null;
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("Failed to read settings: " + ex.Message);
            // Do NOT leave the unreadable file in place to be overwritten by the next Save. Since 2.3.0 this
            // file holds operator data — pinned favourites — not just window sizes, and a half-written file
            // may still contain most of them. Moving it aside keeps that recoverable by hand and makes the
            // failure visible; silently returning defaults reads exactly like a first run.
            RecoveredFrom = SetAsideCorruptFile();
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            // Write-then-rename, because File.WriteAllText truncates first: a crash, power loss or full disk
            // between the truncate and the write leaves a file that parses as nothing, and every favourite
            // and saved connection goes with it. A rename over the top is atomic enough that the reader sees
            // either the old file or the new one, never a half of either.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));

            if (File.Exists(_path)) File.Replace(temp, _path, destinationBackupFileName: null);
            else File.Move(temp, _path);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("Failed to save settings: " + ex.Message);
            // Leave nothing half-finished lying next to the real file.
            try { if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp"); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Renames an unreadable settings file to settings.bad-&lt;n&gt;.json and returns the new name, or null if
    /// it could not be moved. Numbered rather than overwritten so a second bad start cannot destroy the
    /// evidence from the first.
    /// </summary>
    private string? SetAsideCorruptFile()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            for (var i = 1; i < 100; i++)
            {
                var candidate = Path.Combine(dir, $"settings.bad-{i}.json");
                if (File.Exists(candidate)) continue;
                File.Move(_path, candidate);
                AppLog.Instance.Warn($"Unreadable settings file kept as {Path.GetFileName(candidate)}.");
                return Path.GetFileName(candidate);
            }
        }
        catch (Exception ex) { AppLog.Instance.Warn("Could not set the unreadable settings file aside: " + ex.Message); }
        return null;
    }
}
