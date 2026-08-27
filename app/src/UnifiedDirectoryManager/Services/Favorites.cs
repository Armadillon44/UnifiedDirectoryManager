using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Reads and writes the pinned favourites held in <see cref="AppSettings"/>. Pure: it takes the settings
/// object and changes it, leaving the caller to persist. That keeps the rules — scoping, de-duplication,
/// ordering — testable without a directory, a tenant or a settings file.
///
/// Favourites are scoped to a domain because a distinguished name only means something in the domain it came
/// from. Connecting elsewhere shows that domain's own list rather than a set of entries that fail on click.
/// </summary>
public static class Favorites
{
    /// <summary>
    /// The key a domain's favourites are filed under. Lower-cased deliberately: the dictionary is rebuilt by
    /// the JSON deserialiser with a DEFAULT comparer, so a case-insensitive one set here would be silently
    /// dropped on the next load and "CONTOSO.NET" would come back as a different domain from "contoso.net".
    /// Normalising the key is the only thing that survives the round trip.
    /// </summary>
    public static string? KeyFor(string? domain)
    {
        var trimmed = (domain ?? string.Empty).Trim();
        return trimmed.Length == 0 ? null : trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// This domain's favourites, in the order they were pinned. Empty when there is no domain — not connected
    /// yet, so there is nothing a distinguished name could be relative to.
    /// </summary>
    public static IReadOnlyList<FavoriteEntry> For(AppSettings settings, string? domain)
    {
        var key = KeyFor(domain);
        if (key is null || settings.Favorites is null) return [];
        return settings.Favorites.TryGetValue(key, out var list) ? list : [];
    }

    /// <summary>True when this thing is already pinned for this domain.</summary>
    public static bool Contains(AppSettings settings, string? domain, FavoriteEntry entry) =>
        For(settings, domain).Any(e => e.SameAs(entry));

    /// <summary>
    /// Pins something. Returns false when there is no domain to pin it against, or when it is already
    /// pinned — pinning twice is a no-op, not a second row for one target.
    /// </summary>
    public static bool Add(AppSettings settings, string? domain, FavoriteEntry entry)
    {
        var key = KeyFor(domain);
        if (key is null || string.IsNullOrWhiteSpace(entry?.Value)) return false;
        if (Contains(settings, domain, entry!)) return false;

        settings.Favorites ??= new Dictionary<string, List<FavoriteEntry>>();
        if (!settings.Favorites.TryGetValue(key, out var list))
            settings.Favorites[key] = list = new List<FavoriteEntry>();

        // Trimmed on the way in, so what is stored is what is compared against later.
        list.Add(new FavoriteEntry { Kind = entry!.Kind, Value = entry.Value.Trim() });
        return true;
    }

    /// <summary>
    /// Moves a favourite up or down the list. Returns false when it is not pinned, or when it is already at
    /// the end it is being moved towards — the caller then does nothing rather than reporting a move that did
    /// not happen. The order is the operator's, so it is stored, not derived.
    /// </summary>
    /// <param name="delta">-1 to move it earlier, +1 later. Any other value is refused.</param>
    public static bool Move(AppSettings settings, string? domain, FavoriteEntry entry, int delta)
    {
        if (delta != -1 && delta != 1) return false;

        var key = KeyFor(domain);
        if (key is null || settings.Favorites is null) return false;
        if (!settings.Favorites.TryGetValue(key, out var list)) return false;

        var from = list.FindIndex(e => e.SameAs(entry));
        if (from < 0) return false;

        var to = from + delta;
        if (to < 0 || to >= list.Count) return false;

        (list[from], list[to]) = (list[to], list[from]);
        return true;
    }

    /// <summary>Unpins something. Returns false when it was not pinned in the first place.</summary>
    public static bool Remove(AppSettings settings, string? domain, FavoriteEntry entry)
    {
        var key = KeyFor(domain);
        if (key is null || settings.Favorites is null) return false;
        if (!settings.Favorites.TryGetValue(key, out var list)) return false;

        var index = list.FindIndex(e => e.SameAs(entry));
        if (index < 0) return false;
        list.RemoveAt(index);

        // Don't leave an empty list behind for a domain with nothing pinned; it only makes the settings file
        // harder to read and would come back as an empty Favourites section.
        if (list.Count == 0) settings.Favorites.Remove(key);
        return true;
    }
}
