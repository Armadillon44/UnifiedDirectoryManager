using System.Collections.Concurrent;
using System.DirectoryServices;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Resolves distinguished names to friendly display names (displayName ▸ cn ▸ RDN), with a
/// session cache so repeated lookups (group lists, managers) stay cheap. DNs are never shown raw.
/// </summary>
public sealed class NameResolver
{
    private readonly ConnectionState _connection;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    public NameResolver(ConnectionState connection) => _connection = connection;

    /// <summary>Resolves a single DN to a friendly name (cached). Falls back to the RDN on failure.</summary>
    public string Resolve(string distinguishedName)
    {
        if (string.IsNullOrWhiteSpace(distinguishedName))
            return string.Empty;

        if (_cache.TryGetValue(distinguishedName, out var cached))
            return cached;

        string resolved;
        try
        {
            using var entry = _connection.CreateEntry(distinguishedName);
            entry.RefreshCache(new[] { "displayName", "cn", "name" });
            resolved = FirstValue(entry, "displayName")
                       ?? FirstValue(entry, "cn")
                       ?? FirstValue(entry, "name")
                       ?? RdnFallback(distinguishedName);
        }
        catch
        {
            resolved = RdnFallback(distinguishedName);
        }

        _cache[distinguishedName] = resolved;
        return resolved;
    }

    /// <summary>Resolves many DNs, returning friendly names in input order.</summary>
    public IReadOnlyList<string> ResolveMany(IEnumerable<string> dns) =>
        dns.Select(Resolve).ToList();

    private static string? FirstValue(DirectoryEntry entry, string prop)
    {
        var values = entry.Properties[prop];
        return values.Count > 0 ? values[0]?.ToString() : null;
    }

    /// <summary>
    /// Extracts the first RDN value, e.g. "CN=Jane Doe,OU=…" → "Jane Doe". Splits on an UNESCAPED comma and
    /// unescapes the RFC 4514 sequences, so a name that legitimately contains a comma
    /// ("CN=Doe\, Jane,OU=…") comes back as "Doe, Jane" instead of being cut at the backslash.
    /// </summary>
    internal static string RdnFallback(string dn)
    {
        if (string.IsNullOrWhiteSpace(dn)) return string.Empty;
        var first = System.Text.RegularExpressions.Regex.Split(dn.Trim(), @"(?<!\\),")[0];
        var eq = first.IndexOf('=');
        var value = (eq >= 0 ? first[(eq + 1)..] : first).Trim();
        // Unescape the DN specials (\\ last so it can't re-consume an escape we just produced).
        foreach (var c in new[] { ',', '+', '"', '<', '>', ';', '=', '#', '/' })
            value = value.Replace("\\" + c, c.ToString());
        return value.Replace("\\\\", "\\");
    }
}
