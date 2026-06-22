using UnifiedDirectoryManager.Native;

namespace UnifiedDirectoryManager.Services;

/// <summary>Stores credentials in the Windows Credential Manager under "UnifiedDirectoryManager:&lt;domain&gt;".</summary>
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const string Prefix = "UnifiedDirectoryManager:";

    private static string Target(string domainFqdn) => Prefix + domainFqdn.Trim().ToLowerInvariant();

    // Distinct sub-namespace so a sync account keyed by server can't collide with an on-prem bind credential
    // keyed by an identically-named domain FQDN.
    private static string SyncTarget(string server) => Prefix + "entra-sync:" + server.Trim().ToLowerInvariant();

    public void Save(string domainFqdn, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(domainFqdn))
            throw new ArgumentException("Domain is required to save credentials.", nameof(domainFqdn));
        CredentialManager.Write(Target(domainFqdn), username, password);
    }

    public SavedCredential? TryLoad(string domainFqdn)
    {
        if (string.IsNullOrWhiteSpace(domainFqdn))
            return null;
        return CredentialManager.TryRead(Target(domainFqdn), out var user, out var secret)
            ? new SavedCredential(user, secret)
            : null;
    }

    public void Delete(string domainFqdn)
    {
        if (!string.IsNullOrWhiteSpace(domainFqdn))
            CredentialManager.Delete(Target(domainFqdn));
    }

    public void SaveSyncCredential(string server, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(server))
            throw new ArgumentException("Server is required to save sync credentials.", nameof(server));
        CredentialManager.Write(SyncTarget(server), username, password);
    }

    public SavedCredential? TryLoadSyncCredential(string server)
    {
        if (string.IsNullOrWhiteSpace(server))
            return null;
        return CredentialManager.TryRead(SyncTarget(server), out var user, out var secret)
            ? new SavedCredential(user, secret)
            : null;
    }

    public void DeleteSyncCredential(string server)
    {
        if (!string.IsNullOrWhiteSpace(server))
            CredentialManager.Delete(SyncTarget(server));
    }
}
