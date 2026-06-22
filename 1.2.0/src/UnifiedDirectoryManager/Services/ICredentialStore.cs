namespace UnifiedDirectoryManager.Services;

/// <summary>Saved credential pair retrieved from the store.</summary>
public sealed record SavedCredential(string Username, string Password);

/// <summary>Securely persists domain credentials, keyed by domain FQDN.</summary>
public interface ICredentialStore
{
    void Save(string domainFqdn, string username, string password);
    SavedCredential? TryLoad(string domainFqdn);
    void Delete(string domainFqdn);

    // --- Entra Connect delta-sync account (used to run Start-ADSyncSyncCycle over WinRM), keyed by the
    // Entra Connect server name. Stored separately from the on-prem bind credential above. ---
    void SaveSyncCredential(string server, string username, string password);
    SavedCredential? TryLoadSyncCredential(string server);
    void DeleteSyncCredential(string server);
}
