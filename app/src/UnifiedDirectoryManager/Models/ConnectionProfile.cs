namespace UnifiedDirectoryManager.Models;

/// <summary>
/// User-supplied connection settings. No password is stored here; credentials are kept
/// separately (Credential Manager / SecureString) and never serialized with the profile.
/// </summary>
public sealed class ConnectionProfile
{
    /// <summary>Fully-qualified domain name, e.g. "corp.example.com". Always user-supplied.</summary>
    public string DomainFqdn { get; set; } = string.Empty;

    /// <summary>Primary DC hostname or IP. Primary, reliable path for non-domain-joined clients.</summary>
    public string PrimaryDc { get; set; } = string.Empty;

    /// <summary>Ordered fall-back DC hostnames/IPs, tried after the primary.</summary>
    public List<string> FallbackDcs { get; set; } = new();

    /// <summary>Use LDAPS (636) instead of signed/sealed LDAP (389).</summary>
    public bool UseLdaps { get; set; }

    /// <summary>When true, accept an untrusted/mismatched LDAPS server certificate (insecure — opt-in only).</summary>
    public bool IgnoreCertificateErrors { get; set; }

    /// <summary>Username in DOMAIN\user or UPN (user@domain) form.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Whether to persist credentials in Windows Credential Manager.</summary>
    public bool SaveCredentials { get; set; }

    /// <summary>Candidate DCs in connection order: primary first, then fall-backs (deduplicated, non-empty).</summary>
    public IEnumerable<string> OrderedCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dc in new[] { PrimaryDc }.Concat(FallbackDcs))
        {
            var trimmed = dc?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
                yield return trimmed;
        }
    }
}
