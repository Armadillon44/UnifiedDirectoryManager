namespace UnifiedDirectoryManager.Models;

/// <summary>
/// A named, reusable advanced search — the full <see cref="SearchQuery"/> (object type, conditions, scope,
/// base OUs, match mode, and any raw LDAP filter) plus a name so it can be recalled later. Persisted as JSON
/// by the saved-search store, exactly like templates and scenarios.
/// </summary>
public sealed class SavedSearch
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional free-text note about what the search finds (shown as a tooltip).</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The query to run when this saved search is loaded.</summary>
    public SearchQuery Query { get; set; } = new();
}
