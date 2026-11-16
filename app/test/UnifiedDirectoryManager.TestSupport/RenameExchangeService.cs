using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.TestSupport;

/// <summary>
/// The Exchange half of a group rename, held in memory: distribution groups found by any of their identities,
/// renames as Exchange would apply them (naming policy included), Microsoft 365 address changes, and the name and
/// address conflicts a rename checks for.
/// </summary>
public sealed class RenameExchangeService : InertExchangeService
{
    public override bool IsConfigured => true;
    public override bool IsConnected => true;

    /// <summary>Distribution groups, keyed by their Exchange GUID.</summary>
    public Dictionary<string, DistributionGroupNaming> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What Exchange stores for a display name it is given, as a naming policy would. Unchanged by default.</summary>
    public Func<string, string> NamingPolicy { get; set; } = s => s;

    /// <summary>Every rename, in order: (identity used, name, display name, new primary or null).</summary>
    public List<(string Identity, string Name, string DisplayName, string? Primary)> Renames { get; } = new();

    /// <summary>Identities whose rename is refused.</summary>
    public HashSet<string> FailRenamesFor { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When true, a changed primary address does not keep the old one, so the warning path can be seen.</summary>
    public bool DropsOldAddress { get; set; }

    /// <summary>Every Microsoft 365 group address change, in order: (identity used, new primary).</summary>
    public List<(string Identity, string Primary)> UnifiedPrimaryChanges { get; } = new();

    /// <summary>A Microsoft 365 group's addresses, keyed by the identity the change is addressed with.</summary>
    public Dictionary<string, List<string>> UnifiedAddresses { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Identities whose Microsoft 365 address change is refused.</summary>
    public HashSet<string> FailUnifiedFor { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Recipients that hold a name or an address; FindRecipientConflictsAsync returns the ones that match.</summary>
    public List<(RecipientConflict Conflict, string Value)> Held { get; } = new();

    private DistributionGroupNaming? Find(string identity) =>
        Groups.Values.FirstOrDefault(g =>
            new[] { g.Guid, g.Name, g.PrimarySmtpAddress, g.ExternalDirectoryObjectId ?? string.Empty, g.Alias }
                .Any(v => string.Equals(v, identity, StringComparison.OrdinalIgnoreCase)));

    public override Task<DistributionGroupNaming> GetDistributionGroupNamingAsync(string identity, CancellationToken cancellationToken = default) =>
        Find(identity) is { } g
            ? Task.FromResult(g)
            : Task.FromException<DistributionGroupNaming>(new InvalidOperationException($"Exchange did not return the group '{identity}'."));

    public override Task<DistributionGroupNaming> RenameDistributionGroupAsync(
        string identity, string name, string displayName, string? newPrimaryAddress, CancellationToken cancellationToken = default)
    {
        Renames.Add((identity, name, displayName, newPrimaryAddress));
        if (FailRenamesFor.Contains(identity) || Find(identity) is not { } g)
            return Task.FromException<DistributionGroupNaming>(new InvalidOperationException($"Exchange refused to rename '{identity}'."));
        var addresses = g.EmailAddresses.ToList();
        var primary = g.PrimarySmtpAddress;
        if (!string.IsNullOrWhiteSpace(newPrimaryAddress))
        {
            addresses = addresses.Where(a => !string.Equals(a[5..], newPrimaryAddress, StringComparison.OrdinalIgnoreCase))
                                 .Select(a => a.StartsWith("SMTP:", StringComparison.Ordinal)
                                     ? (DropsOldAddress ? null : "smtp:" + a[5..]) : a)
                                 .Where(a => a is not null).Select(a => a!).ToList();
            addresses.Insert(0, "SMTP:" + newPrimaryAddress);
            primary = newPrimaryAddress;
        }
        var renamed = g with { Name = name, DisplayName = NamingPolicy(displayName), PrimarySmtpAddress = primary, EmailAddresses = addresses };
        Groups[g.Guid] = renamed;
        return Task.FromResult(renamed);
    }

    public override Task<IReadOnlyList<string>> SetUnifiedGroupPrimaryAddressAsync(
        string identity, string newPrimaryAddress, CancellationToken cancellationToken = default)
    {
        UnifiedPrimaryChanges.Add((identity, newPrimaryAddress));
        if (FailUnifiedFor.Contains(identity))
            return Task.FromException<IReadOnlyList<string>>(new InvalidOperationException($"Exchange refused the address for '{identity}'."));
        var before = UnifiedAddresses.TryGetValue(identity, out var list) ? list : new List<string> { "SMTP:" + identity };
        var after = before.Where(a => !string.Equals(a[5..], newPrimaryAddress, StringComparison.OrdinalIgnoreCase))
                          .Select(a => a.StartsWith("SMTP:", StringComparison.Ordinal) ? (DropsOldAddress ? null : "smtp:" + a[5..]) : a)
                          .Where(a => a is not null).Select(a => a!).ToList();
        after.Insert(0, "SMTP:" + newPrimaryAddress);
        UnifiedAddresses[identity] = after;
        return Task.FromResult<IReadOnlyList<string>>(after);
    }

    public override Task<IReadOnlyList<RecipientConflict>> FindRecipientConflictsAsync(
        string? name, string? address, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RecipientConflict>>(Held
            .Where(h => (h.Conflict.Field == "Name" && string.Equals(h.Value, name, StringComparison.OrdinalIgnoreCase))
                     || (h.Conflict.Field == "Address" && string.Equals(h.Value, address, StringComparison.OrdinalIgnoreCase)))
            .Select(h => h.Conflict).ToList());
}
