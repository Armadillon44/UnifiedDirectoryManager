namespace UnifiedDirectoryManager.Services;

/// <summary>Outcome of a best-effort DC discovery attempt.</summary>
public sealed record DiscoveryResult(IReadOnlyList<string> DomainControllers, string Status)
{
    public bool Found => DomainControllers.Count > 0;
}

/// <summary>Best-effort domain-controller discovery. Never throws; reports status instead.</summary>
public interface IDomainLocator
{
    Task<DiscoveryResult> LocateAsync(string domainFqdn, CancellationToken cancellationToken = default);
}
