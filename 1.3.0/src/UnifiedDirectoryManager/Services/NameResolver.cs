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

    /// <summary>Extracts the first RDN value, e.g. "CN=Jane Doe,OU=..." → "Jane Doe".</summary>
    internal static string RdnFallback(string dn)
    {
        var first = dn.Split(',')[0];
        var eq = first.IndexOf('=');
        return eq >= 0 ? first[(eq + 1)..].Trim() : first.Trim();
    }
}
