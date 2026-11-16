using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.TestSupport;

/// <summary>
/// The Graph half of a group rename, held in memory: groups by ID, the display-name writes made to them, and the
/// address and name lookups a rename checks with. A naming policy can be set to show that a rename reports what
/// Entra actually stored rather than what was asked for.
/// </summary>
public sealed class RenameGraphService : InertGraphService
{
    public override bool IsConfigured => true;

    /// <summary>Signed in by default; set false to see the checks a rename cannot make without Graph.</summary>
    public bool SignedIn { get; set; } = true;
    public override bool IsSignedIn => SignedIn;

    public Dictionary<string, CloudGroupNaming> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every UpdateGroupAsync, in order.</summary>
    public List<(string Id, IReadOnlyDictionary<string, string?> Changes)> GroupUpdates { get; } = new();

    /// <summary>What Entra stores for a display name it is given, as a group naming policy would. Unchanged by default.</summary>
    public Func<string, string> NamingPolicy { get; set; } = s => s;

    /// <summary>Group IDs whose update is refused.</summary>
    public HashSet<string> FailUpdatesFor { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Owners per address (no prefix), as FindAddressOwnersAsync reports them.</summary>
    public Dictionary<string, List<CloudAddressOwner>> AddressOwners { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every address looked up, in order.</summary>
    public List<string> AddressLookups { get; } = new();

    /// <summary>Group IDs per display name, as FindGroupIdsByDisplayNameAsync reports them.</summary>
    public Dictionary<string, List<string>> GroupIdsByName { get; } = new(StringComparer.OrdinalIgnoreCase);

    public override Task<CloudGroupNaming?> GetGroupNamingAsync(string groupId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Groups.TryGetValue(groupId, out var g) ? g : null);

    public override Task UpdateGroupAsync(string groupId, IReadOnlyDictionary<string, string?> changes, CancellationToken cancellationToken = default)
    {
        GroupUpdates.Add((groupId, changes));
        if (FailUpdatesFor.Contains(groupId))
            return Task.FromException(new InvalidOperationException($"Entra refused the change to {groupId}"));
        if (Groups.TryGetValue(groupId, out var g) && changes.TryGetValue("displayName", out var name) && name is not null)
            Groups[groupId] = g with { DisplayName = NamingPolicy(name) };
        return Task.CompletedTask;
    }

    public override Task<IReadOnlyList<CloudAddressOwner>> FindAddressOwnersAsync(string address, CancellationToken cancellationToken = default)
    {
        AddressLookups.Add(address);
        return Task.FromResult<IReadOnlyList<CloudAddressOwner>>(
            AddressOwners.TryGetValue(address, out var owners) ? owners.ToList() : new List<CloudAddressOwner>());
    }

    public override Task<IReadOnlyList<string>> FindGroupIdsByDisplayNameAsync(string displayName, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(
            GroupIdsByName.TryGetValue(displayName, out var ids) ? ids.ToList() : new List<string>());
}
