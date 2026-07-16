using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>Persists reusable advanced searches / LDAP queries (CRUD). Backed by JSON files.</summary>
public interface ISavedSearchStore
{
    /// <summary>Directory saved searches are read from / written to.</summary>
    string SearchesDirectory { get; }

    IReadOnlyList<SavedSearch> LoadAll();

    /// <summary>Saves a search. If <paramref name="originalName"/> differs, the old file is removed (rename).</summary>
    void Save(SavedSearch search, string? originalName = null);

    void Delete(string name);

    /// <summary>Writes a saved search to an arbitrary path (for sharing).</summary>
    void ExportTo(SavedSearch search, string path);

    /// <summary>Reads a saved search from an arbitrary path (does not save it to the store).</summary>
    SavedSearch ImportFrom(string path);
}
