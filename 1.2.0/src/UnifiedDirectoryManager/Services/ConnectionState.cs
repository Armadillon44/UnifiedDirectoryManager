using System.DirectoryServices;
using System.Net;
using Protocols = System.DirectoryServices.Protocols;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Holds the live, authenticated session: which DC we bound to, the domain naming context, and the
/// credentials used to mint <see cref="DirectoryEntry"/> objects for any DN. Credentials are the
/// explicitly-entered ones only — never the machine/current-user context (clients may be Entra-only).
/// </summary>
public sealed class ConnectionState
{
    public required string Server { get; init; }
    public required string DomainFqdn { get; init; }

    /// <summary>defaultNamingContext read from RootDSE, e.g. "DC=corp,DC=example,DC=com".</summary>
    public required string DefaultNamingContext { get; init; }

    public required string Username { get; init; }
    public required string Password { get; init; }
    public bool UseLdaps { get; init; }
    public bool IgnoreCertificateErrors { get; init; }

    private AuthenticationTypes AuthTypes => UseLdaps
        ? AuthenticationTypes.Secure | AuthenticationTypes.SecureSocketsLayer | AuthenticationTypes.Sealing | AuthenticationTypes.Signing
        : AuthenticationTypes.Secure | AuthenticationTypes.Sealing | AuthenticationTypes.Signing;

    private string Port => UseLdaps ? ":636" : string.Empty;

    /// <summary>Builds an "LDAP://server[:port]/DN" path, or the server root when dn is null.</summary>
    public string LdapPath(string? dn)
    {
        var tail = string.IsNullOrEmpty(dn) ? string.Empty : "/" + dn;
        return $"LDAP://{Server}{Port}{tail}";
    }

    /// <summary>Creates a credentialed DirectoryEntry bound to the given DN (root NC if null).</summary>
    public DirectoryEntry CreateEntry(string? dn = null)
    {
        dn ??= DefaultNamingContext;
        return new DirectoryEntry(LdapPath(dn), Username, Password, AuthTypes);
    }

    /// <summary>Creates a DirectoryEntry for the server's RootDSE (no naming context).</summary>
    public DirectoryEntry CreateRootDse() =>
        new($"LDAP://{Server}{Port}/RootDSE", Username, Password, AuthTypes);

    /// <summary>
    /// Creates a bound LdapConnection using the same DC/credentials. Used for modifications that are
    /// awkward via DirectoryEntry (e.g. INTEGER8/LargeInteger attributes like accountExpires).
    /// </summary>
    public Protocols.LdapConnection CreateLdapConnection()
    {
        var port = UseLdaps ? 636 : 389;
        var conn = new Protocols.LdapConnection(
            new Protocols.LdapDirectoryIdentifier(Server, port),
            new NetworkCredential(Username, Password))
        {
            AuthType = Protocols.AuthType.Negotiate,
            Timeout = TimeSpan.FromSeconds(30),
        };
        conn.SessionOptions.ProtocolVersion = 3;
        if (UseLdaps)
        {
            conn.SessionOptions.SecureSocketLayer = true;
            // Only bypass certificate validation when the user has explicitly opted in.
            if (IgnoreCertificateErrors)
                conn.SessionOptions.VerifyServerCertificate = (_, _) => true;
        }
        else
        {
            conn.SessionOptions.Signing = true;
            conn.SessionOptions.Sealing = true;
        }
        conn.Bind();
        return conn;
    }
}
