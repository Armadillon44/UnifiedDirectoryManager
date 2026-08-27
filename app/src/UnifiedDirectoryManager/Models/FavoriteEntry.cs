namespace UnifiedDirectoryManager.Models;

/// <summary>What a pinned favourite points at.</summary>
public enum FavoriteKind
{
    /// <summary>An OU, a container, or the domain root, addressed by its distinguished name.</summary>
    Container,
    /// <summary>A saved advanced search or raw LDAP query, addressed by its name in the search store.</summary>
    SavedSearch,
}

/// <summary>
/// One pinned favourite. It stores WHAT IT POINTS AT and nothing else — no display name, no copy of the
/// query, no column set. Every one of those would be a second source of truth that goes quietly stale when
/// the real thing changes, and a favourite that disagrees with the tree is worse than no favourite.
///
/// Mutable properties with a parameterless constructor because this round-trips through the settings JSON.
/// </summary>
public sealed class FavoriteEntry
{
    public FavoriteKind Kind { get; set; }

    /// <summary>The distinguished name for a container; the search's name for a saved search.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// True when two entries name the same thing. Both a distinguished name and a saved search's name are
    /// matched without regard to case: AD treats a DN that way, and pinning something twice because the
    /// casing differed would put two rows in the list for one target.
    /// </summary>
    public bool SameAs(FavoriteEntry? other) =>
        other is not null
        && other.Kind == Kind
        && string.Equals((other.Value ?? string.Empty).Trim(), (Value ?? string.Empty).Trim(),
                         StringComparison.OrdinalIgnoreCase);
}
