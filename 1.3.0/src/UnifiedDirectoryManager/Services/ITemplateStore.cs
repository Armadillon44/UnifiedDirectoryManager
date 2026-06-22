using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>Persists new-user creation templates (CRUD). Backed by JSON files.</summary>
public interface ITemplateStore
{
    /// <summary>Directory templates are read from / written to.</summary>
    string TemplatesDirectory { get; }

    IReadOnlyList<UserTemplate> LoadAll();

    /// <summary>Saves a template. If <paramref name="originalName"/> differs, the old file is removed (rename).</summary>
    void Save(UserTemplate template, string? originalName = null);

    void Delete(string name);

    /// <summary>Writes a template to an arbitrary path (for sharing).</summary>
    void ExportTo(UserTemplate template, string path);

    /// <summary>Reads a template from an arbitrary path (does not save it to the store).</summary>
    UserTemplate ImportFrom(string path);
}
