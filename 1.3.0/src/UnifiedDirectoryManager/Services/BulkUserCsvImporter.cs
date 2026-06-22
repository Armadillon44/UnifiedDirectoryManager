using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Maps a CSV (parsed by <see cref="CsvReader"/>) into <see cref="ImportedUserRow"/>s for the bulk-create
/// grid. Recognized headers map to identity fields; any other header is treated as an attribute override
/// via <see cref="AttributeCatalog.Ldap"/>. Manager and cloud-group columns are resolved best-effort
/// against the live directory/Entra — failures (including "not connected") become per-row warnings, so an
/// offline import still loads the rest of the data.
/// </summary>
public sealed class BulkUserCsvImporter
{
    private readonly IDirectoryService _directory;
    private readonly IGraphService _graph;

    public BulkUserCsvImporter(IDirectoryService directory, IGraphService graph)
    {
        _directory = directory;
        _graph = graph;
    }

    private enum Field { First, Middle, Last, Initials, Sam, Upn, Email, Manager, CloudGroups, IssueTap, Attribute, Unknown }

    /// <summary>Outcome of a pre-import format check: <see cref="Errors"/> block the import; <see cref="Warnings"/> are advisory.</summary>
    public sealed record CsvFormatCheck(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
    {
        public bool IsImportable => Errors.Count == 0;
    }

    /// <summary>
    /// A ready-to-edit sample CSV showing the recognized columns and how to fill them. Encoded with the shared
    /// (formula-injection-safe) <see cref="CsvText"/>. Offered to the operator so an import is structured correctly.
    /// </summary>
    public static string TemplateCsv()
    {
        var headers = new[]
        {
            "First name", "Last name", "Logon name (sAMAccountName)", "User logon name (UPN)",
            "Email", "Manager", "Department", "Job title", "Cloud groups", "Issue TAP",
        };
        var rows = new[]
        {
            new[] { "Ada", "Lovelace", "ada.lovelace", "ada.lovelace@contoso.com",
                    "ada.lovelace@contoso.com", "grace.hopper", "Engineering", "Software Engineer",
                    "All Staff; Engineering Team", "yes" },
            new[] { "Alan", "Turing", "alan.turing", "alan.turing@contoso.com",
                    "alan.turing@contoso.com", "CN=Grace Hopper,OU=Users,DC=contoso,DC=com", "Research", "Researcher",
                    "All Staff", "no" },
        };
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(CsvText.Row(headers));
        foreach (var r in rows) sb.AppendLine(CsvText.Row(r));
        return sb.ToString();
    }

    /// <summary>
    /// Pre-import format check: confirms the text parses as CSV, has a header row with at least one column that
    /// yields a name/logon, and has data rows. Unrecognized columns and ragged rows become warnings (not errors).
    /// </summary>
    public static CsvFormatCheck ValidateFormat(string csvText)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(csvText))
        {
            errors.Add("The file is empty.");
            return new CsvFormatCheck(errors, warnings);
        }

        var (headers, rows) = CsvReader.Parse(csvText);
        if (headers.Count == 0)
        {
            errors.Add("No header row found. The first row must name the columns (see the template).");
            return new CsvFormatCheck(errors, warnings);
        }

        var map = headers.Select(Classify).ToArray();

        if (!map.Any(m => m.Field is Field.First or Field.Last or Field.Sam))
            errors.Add("No name column found. Include at least one of: First name, Last name, or Logon name (sAMAccountName).");

        if (rows.Count == 0)
            errors.Add("The file has a header row but no data rows.");

        var unknown = headers.Where((_, i) => map[i].Field == Field.Unknown).Select(h => $"“{h}”").ToList();
        if (unknown.Count > 0)
            warnings.Add("These columns aren’t recognized and will be ignored: " + string.Join(", ", unknown) + ".");

        var ragged = rows.Count(r => r.Count > headers.Count);
        if (ragged > 0)
            warnings.Add($"{ragged} row(s) have more values than columns — check for unquoted commas. Extra values are ignored.");

        return new CsvFormatCheck(errors, warnings);
    }

    public async Task<IReadOnlyList<ImportedUserRow>> ImportAsync(string csvText, CancellationToken ct = default)
    {
        var (headers, rows) = CsvReader.Parse(csvText);
        if (headers.Count == 0) return Array.Empty<ImportedUserRow>();

        // Classify each column once from its header.
        var map = headers.Select(Classify).ToArray();

        var result = new List<ImportedUserRow>(rows.Count);
        var line = 1; // header is line 1
        foreach (var cells in rows)
        {
            ct.ThrowIfCancellationRequested();
            line++;
            var row = new ImportedUserRow();
            for (var c = 0; c < headers.Count; c++)
            {
                var value = c < cells.Count ? cells[c].Trim() : string.Empty;
                if (value.Length == 0 && map[c].Field != Field.IssueTap) continue;
                switch (map[c].Field)
                {
                    case Field.First: row.FirstName = value; break;
                    case Field.Middle: row.MiddleName = value; break;
                    case Field.Last: row.LastName = value; break;
                    case Field.Initials: row.Initials = value; break;
                    case Field.Sam: row.SamOverride = value; break;
                    case Field.Upn: row.Upn = value; break;
                    case Field.Email: row.Email = value; break;
                    case Field.IssueTap: row.IssueTap = ParseBool(value); break;
                    case Field.Manager: await ResolveManagerAsync(row, value, ct); break;
                    case Field.CloudGroups: await ResolveCloudGroupsAsync(row, value, ct); break;
                    case Field.Attribute: row.AttributeOverrides[map[c].Ldap!] = value; break;
                    case Field.Unknown:
                        row.Warnings.Add($"Column “{headers[c]}” isn’t a recognized field — ignored.");
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(row.FirstName) && string.IsNullOrWhiteSpace(row.LastName)
                && string.IsNullOrWhiteSpace(row.SamOverride))
                row.Warnings.Add($"Row {line}: no first/last name or logon name — fill one in before creating.");

            result.Add(row);
        }
        return result;
    }

    private static (Field Field, string? Ldap) Classify(string header)
    {
        var h = header.Trim().ToLowerInvariant();
        switch (h)
        {
            case "first name": case "first": case "firstname": case "given name": case "givenname":
                return (Field.First, null);
            case "middle name": case "middle": case "middlename":
                return (Field.Middle, null);
            case "last name": case "last": case "lastname": case "surname": case "sn":
                return (Field.Last, null);
            case "initials":
                return (Field.Initials, null);
            case "logon name": case "logon name (samaccountname)": case "samaccountname":
            case "sam": case "sam account name": case "username": case "user name":
                return (Field.Sam, null);
            case "user logon name": case "user logon name (upn)": case "upn":
            case "userprincipalname": case "user principal name":
                return (Field.Upn, null);
            case "email": case "e-mail": case "mail": case "email address":
                return (Field.Email, null);
            case "manager":
                return (Field.Manager, null);
            case "cloud groups": case "entra groups": case "entra id groups": case "cloud group":
                return (Field.CloudGroups, null);
            case "issue tap": case "tap": case "temporary access pass": case "issue temporary access pass":
                return (Field.IssueTap, null);
        }

        // Anything else: try to treat the header as an attribute (friendly name or raw lDAPDisplayName).
        var ldap = AttributeCatalog.Ldap(header.Trim()); // returns the input unchanged if unknown
        return AttributeCatalog.IsKnown(ldap) ? (Field.Attribute, ldap) : (Field.Unknown, null);
    }

    private static bool ParseBool(string value) =>
        value.Trim().ToLowerInvariant() is "yes" or "true" or "1" or "y" or "x" or "✓";

    private async Task ResolveManagerAsync(ImportedUserRow row, string value, CancellationToken ct)
    {
        if (value.Contains('=')) { row.ManagerDn = value; row.ManagerDisplay = NameResolver.RdnFallback(value); return; } // already a DN
        try
        {
            var matches = await _directory.SearchByNameAsync(value, AdObjectType.User, ct);
            var match = matches.FirstOrDefault(m =>
                string.Equals(m.Get("sAMAccountName"), value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.Get("userPrincipalName"), value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.Name, value, StringComparison.OrdinalIgnoreCase))
                ?? matches.FirstOrDefault();
            if (match is not null) { row.ManagerDn = match.DistinguishedName; row.ManagerDisplay = match.Name; }
            else row.Warnings.Add($"Manager “{value}” not found — left unset.");
        }
        catch (Exception ex)
        {
            row.Warnings.Add($"Couldn’t resolve manager “{value}”: {DirectoryService.Friendly(ex)}");
        }
    }

    private async Task ResolveCloudGroupsAsync(ImportedUserRow row, string value, CancellationToken ct)
    {
        foreach (var name in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var hits = await _graph.SearchGroupsAsync(name, ct);
                var match = hits.FirstOrDefault(g => string.Equals(g.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                            ?? hits.FirstOrDefault();
                if (match is not null) row.CloudGroups.Add(new CloudGroupRef { Id = match.Id, Name = match.DisplayName });
                else row.Warnings.Add($"Cloud group “{name}” not found — skipped.");
            }
            catch (Exception ex)
            {
                row.Warnings.Add($"Couldn’t resolve cloud group “{name}”: {GraphErrors.Friendly(ex)}");
            }
        }
    }
}
