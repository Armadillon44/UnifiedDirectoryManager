using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>Persists reusable action scenarios (CRUD). Backed by JSON files.</summary>
public interface IScenarioStore
{
    /// <summary>Directory scenarios are read from / written to.</summary>
    string ScenariosDirectory { get; }

    IReadOnlyList<Scenario> LoadAll();

    /// <summary>Saves a scenario. If <paramref name="originalName"/> differs, the old file is removed (rename).</summary>
    void Save(Scenario scenario, string? originalName = null);

    void Delete(string name);

    /// <summary>Writes a scenario to an arbitrary path (for sharing).</summary>
    void ExportTo(Scenario scenario, string path);

    /// <summary>Reads a scenario from an arbitrary path (does not save it to the store).</summary>
    Scenario ImportFrom(string path);
}
