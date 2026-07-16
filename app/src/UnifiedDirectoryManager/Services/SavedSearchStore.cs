using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.DirectoryServices;
using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// JSON-file store for saved advanced searches / LDAP queries. Defaults to
/// %APPDATA%\UnifiedDirectoryManager\Searches but accepts a custom directory (e.g. a shared network folder)
/// so a team can share searches. Seeds a few ready-made example searches the first time the directory is
/// created. Mirrors <see cref="ScenarioStore"/> / <see cref="TemplateStore"/>.
/// </summary>
public sealed class SavedSearchStore : ISavedSearchStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true, // tolerate PascalCase / hand-edited / foreign-authored import files
        Converters = { new JsonStringEnumConverter() }, // store operator/scope/type names, not numbers
    };

    public string SearchesDirectory { get; }

    public SavedSearchStore(string? directory = null)
    {
        SearchesDirectory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnifiedDirectoryManager", "Searches");

        var fresh = !Directory.Exists(SearchesDirectory);
        Directory.CreateDirectory(SearchesDirectory);
        if (fresh) SeedDefaults();
    }

    public IReadOnlyList<SavedSearch> LoadAll()
    {
        var list = new List<SavedSearch>();
        string[] files;
        // Materialize the listing INSIDE the try so a directory-listing failure (e.g. the folder was
        // removed/became inaccessible mid-session) degrades to an empty list instead of throwing — otherwise
        // it would abort the Advanced Search dialog's construction, not just the load.
        try { files = Directory.GetFiles(SearchesDirectory, "*.json"); }
        catch { return list; }

        foreach (var file in files)
        {
            try
            {
                var search = JsonSerializer.Deserialize<SavedSearch>(File.ReadAllText(file), JsonOptions);
                if (search is not null && !string.IsNullOrWhiteSpace(search.Name)) list.Add(Normalize(search));
            }
            catch
            {
                // Skip corrupt/unreadable files rather than failing the whole load.
            }
        }
        return list.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Guarantees the query and its collections are non-null (JSON can set them to explicit null,
    /// which would NRE the consumers), so a hand-edited/foreign file can't crash the loader or the dialog.</summary>
    private static SavedSearch Normalize(SavedSearch s)
    {
        s.Query ??= new SearchQuery();
        s.Query.Conditions ??= new();
        s.Query.BaseDns ??= new();
        return s;
    }

    public void Save(SavedSearch search, string? originalName = null)
    {
        if (string.IsNullOrWhiteSpace(search.Name))
            throw new ArgumentException("Search name is required.");

        if (!string.IsNullOrWhiteSpace(originalName) &&
            !string.Equals(originalName, search.Name, StringComparison.OrdinalIgnoreCase))
        {
            Delete(originalName);
        }

        File.WriteAllText(PathFor(search.Name), JsonSerializer.Serialize(search, JsonOptions));
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    public void ExportTo(SavedSearch search, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(search, JsonOptions));

    public SavedSearch ImportFrom(string path)
    {
        var search = JsonSerializer.Deserialize<SavedSearch>(File.ReadAllText(path), JsonOptions);
        if (search is null || string.IsNullOrWhiteSpace(search.Name))
            throw new InvalidOperationException("The file is not a valid saved search.");
        return Normalize(search);
    }

    private string PathFor(string name) => Path.Combine(SearchesDirectory, Sanitize(name) + ".json");

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "search" : cleaned;
    }

    /// <summary>Writes a few useful example searches on first run (also demonstrates saved raw LDAP queries).</summary>
    private void SeedDefaults()
    {
        try
        {
            // Bitwise LDAP matching rule 1.2.840.113556.1.4.803 (AND) against userAccountControl:
            //   2 = ACCOUNTDISABLE, 65536 = DONT_EXPIRE_PASSWORD.
            Save(new SavedSearch
            {
                Name = "Disabled users",
                Description = "All disabled user accounts (userAccountControl bit 2).",
                Query = new SearchQuery
                {
                    ObjectType = AdObjectType.User,
                    Scope = SearchScope.Subtree,
                    RawFilter = "(userAccountControl:1.2.840.113556.1.4.803:=2)",
                },
            });

            Save(new SavedSearch
            {
                Name = "Users without a manager",
                Description = "User accounts that have no manager set.",
                Query = new SearchQuery
                {
                    ObjectType = AdObjectType.User,
                    Scope = SearchScope.Subtree,
                    MatchAll = true,
                    Conditions = { new SearchCondition { LdapName = "manager", Operator = ConditionOperator.NotPresent } },
                },
            });

            Save(new SavedSearch
            {
                Name = "Users with password set to never expire",
                Description = "User accounts flagged DONT_EXPIRE_PASSWORD (userAccountControl bit 65536).",
                Query = new SearchQuery
                {
                    ObjectType = AdObjectType.User,
                    Scope = SearchScope.Subtree,
                    RawFilter = "(userAccountControl:1.2.840.113556.1.4.803:=65536)",
                },
            });
        }
        catch
        {
            // A failed seed is non-fatal — the user can still create saved searches manually.
        }
    }
}
