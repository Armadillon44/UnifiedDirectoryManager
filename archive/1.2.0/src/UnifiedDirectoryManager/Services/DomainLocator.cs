using UnifiedDirectoryManager.Native;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Discovers domain controllers via the DNS SRV record "_ldap._tcp.dc._msdcs.&lt;domain&gt;".
/// Strictly best-effort: if DNS can't be reached (typical on Entra-only clients), it returns an
/// empty result with an explanatory status and the workflow continues with manually-entered DCs.
/// </summary>
public sealed class DomainLocator : IDomainLocator
{
    public Task<DiscoveryResult> LocateAsync(string domainFqdn, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var domain = domainFqdn?.Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(domain) || !domain.Contains('.'))
                return new DiscoveryResult(Array.Empty<string>(), "Enter a fully-qualified domain (e.g. corp.example.com) to discover DCs.");

            try
            {
                var query = $"_ldap._tcp.dc._msdcs.{domain}";
                var records = DnsSrv.Query(query);
                if (records.Count == 0)
                {
                    return new DiscoveryResult(
                        Array.Empty<string>(),
                        $"No DCs found via DNS for '{domain}'. This is expected if the client can't reach the domain's DNS — enter a DC hostname or IP manually.");
                }

                var hosts = records
                    .Select(r => r.Target)
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new DiscoveryResult(hosts, $"Discovered {hosts.Count} domain controller(s) via DNS.");
            }
            catch (Exception ex)
            {
                return new DiscoveryResult(
                    Array.Empty<string>(),
                    $"DC discovery failed ({ex.Message}). Enter a DC hostname or IP manually.");
            }
        }, cancellationToken);
    }
}
