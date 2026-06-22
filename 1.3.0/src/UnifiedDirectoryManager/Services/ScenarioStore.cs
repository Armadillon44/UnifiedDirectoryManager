using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// JSON-file scenario store. Defaults to %APPDATA%\UnifiedDirectoryManager\Scenarios but accepts a custom directory
/// (e.g. a shared network folder) so a team can share scenarios. Seeds a ready-made "Terminate User"
/// scenario the first time the directory is created.
/// </summary>
public sealed class ScenarioStore : IScenarioStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }, // store action names, not numbers
    };

    public string ScenariosDirectory { get; }

    public ScenarioStore(string? directory = null)
    {
        ScenariosDirectory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnifiedDirectoryManager", "Scenarios");

        var fresh = !Directory.Exists(ScenariosDirectory);
        Directory.CreateDirectory(ScenariosDirectory);
        if (fresh) SeedDefaults();
    }

    public IReadOnlyList<Scenario> LoadAll()
    {
        var list = new List<Scenario>();
        foreach (var file in Directory.EnumerateFiles(ScenariosDirectory, "*.json"))
        {
            try
            {
                var scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(file), JsonOptions);
                if (scenario is not null) list.Add(scenario);
            }
            catch
            {
                // Skip corrupt/unreadable files rather than failing the whole load.
            }
        }
        return list.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void Save(Scenario scenario, string? originalName = null)
    {
        if (string.IsNullOrWhiteSpace(scenario.Name))
            throw new ArgumentException("Scenario name is required.");

        if (!string.IsNullOrWhiteSpace(originalName) &&
            !string.Equals(originalName, scenario.Name, StringComparison.OrdinalIgnoreCase))
        {
            Delete(originalName);
        }

        File.WriteAllText(PathFor(scenario.Name), JsonSerializer.Serialize(scenario, JsonOptions));
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    public void ExportTo(Scenario scenario, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(scenario, JsonOptions));

    public Scenario ImportFrom(string path)
    {
        var scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(path), JsonOptions);
        if (scenario is null || string.IsNullOrWhiteSpace(scenario.Name))
            throw new InvalidOperationException("The file is not a valid scenario.");
        return scenario;
    }

    private string PathFor(string name) => Path.Combine(ScenariosDirectory, Sanitize(name) + ".json");

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "scenario" : cleaned;
    }

    /// <summary>Writes the built-in example scenario(s) on first run.</summary>
    private void SeedDefaults()
    {
        try
        {
            Save(new Scenario
            {
                Name = "Terminate User",
                Description = "Disables the account, strips group memberships, clears the manager and stamps "
                            + "the description. Set a Disabled-OU target on the “Move to OU” step in the scenario "
                            + "editor before first use (it is blank by default — leaving it blank skips the move).",
                Steps =
                {
                    new ScenarioStep { Action = ScenarioActionType.Disable },
                    new ScenarioStep { Action = ScenarioActionType.RemoveAllGroups },
                    new ScenarioStep { Action = ScenarioActionType.ClearAttribute, Attribute = "manager" },
                    // TargetOu intentionally blank: the disabled-OU DN is tenant-specific. The runner treats a blank
                    // MoveToOu as a no-op, so the seed stays portable; the admin fills in their OU via the editor.
                    new ScenarioStep { Action = ScenarioActionType.MoveToOu, TargetOu = "" },
                    new ScenarioStep { Action = ScenarioActionType.SetDescription, Value = "Terminated {date} by {admin}" },
                },
            });
        }
        catch
        {
            // A failed seed is non-fatal — the user can still create scenarios manually.
        }
    }
}
