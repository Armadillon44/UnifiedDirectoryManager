using System.IO;
using System.Text.Json;

namespace UnifiedDirectoryManager.Services;

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
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

    public SettingsStore(string? directory = null)
    {
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnifiedDirectoryManager");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) { AppLog.Instance.Warn("Failed to read settings: " + ex.Message); }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions)); }
        catch (Exception ex) { AppLog.Instance.Warn("Failed to save settings: " + ex.Message); }
    }
}
