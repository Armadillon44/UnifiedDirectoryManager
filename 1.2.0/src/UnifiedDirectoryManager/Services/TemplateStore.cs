using System.IO;
using System.Text.Json;
using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// JSON-file template store. Defaults to %APPDATA%\UnifiedDirectoryManager\Templates but accepts a custom
/// directory (e.g. a shared network folder) so a team can share templates.
/// </summary>
public sealed class TemplateStore : ITemplateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string TemplatesDirectory { get; }

    public TemplateStore(string? directory = null)
    {
        TemplatesDirectory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnifiedDirectoryManager", "Templates");
        Directory.CreateDirectory(TemplatesDirectory);
    }

    public IReadOnlyList<UserTemplate> LoadAll()
    {
        var list = new List<UserTemplate>();
        foreach (var file in Directory.EnumerateFiles(TemplatesDirectory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var template = JsonSerializer.Deserialize<UserTemplate>(json, JsonOptions);
                if (template is not null)
                    list.Add(template);
            }
            catch
            {
                // Skip corrupt/unreadable template files rather than failing the whole load.
            }
        }
        return list.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void Save(UserTemplate template, string? originalName = null)
    {
        if (string.IsNullOrWhiteSpace(template.Name))
            throw new ArgumentException("Template name is required.");

        if (!string.IsNullOrWhiteSpace(originalName) &&
            !string.Equals(originalName, template.Name, StringComparison.OrdinalIgnoreCase))
        {
            Delete(originalName);
        }

        var path = PathFor(template.Name);
        File.WriteAllText(path, JsonSerializer.Serialize(template, JsonOptions));
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path))
            File.Delete(path);
    }

    public void ExportTo(UserTemplate template, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(template, JsonOptions));

    public UserTemplate ImportFrom(string path)
    {
        var template = JsonSerializer.Deserialize<UserTemplate>(File.ReadAllText(path), JsonOptions);
        if (template is null || string.IsNullOrWhiteSpace(template.Name))
            throw new InvalidOperationException("The file is not a valid template.");
        return template;
    }

    private string PathFor(string name) => Path.Combine(TemplatesDirectory, Sanitize(name) + ".json");

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "template" : cleaned;
    }
}
