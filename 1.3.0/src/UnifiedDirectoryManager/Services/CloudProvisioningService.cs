using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Shared post-create cloud provisioning steps used by both the New User and Copy User flows:
/// trigger an Entra Connect delta sync, wait for the freshly-created on-prem user to sync into
/// Entra ID, then add it to cloud (Entra) groups. Progress is surfaced through a <c>report</c>
/// callback so each caller renders it however it likes (today both append to a ProgressSteps log).
/// Orchestration (whether a sync failure is fatal, retry offers, etc.) stays with the callers,
/// since the New User and Copy User flows deliberately differ there.
/// </summary>
public sealed class CloudProvisioningService
{
    private readonly IGraphService _graph;
    private readonly EntraSyncService _entraSync;
    private readonly ISettingsStore _settingsStore;

    public CloudProvisioningService(IGraphService graph, EntraSyncService entraSync, ISettingsStore settingsStore)
    {
        _graph = graph;
        _entraSync = entraSync;
        _settingsStore = settingsStore;
    }

    /// <summary>
    /// Triggers an Entra Connect delta sync on <paramref name="server"/> over WinRM — as the current Windows
    /// user when <paramref name="username"/> is blank, else as the supplied account — and persists the server
    /// to <paramref name="settings"/>. Returns the raw sync outcome; the caller decides whether a failure is fatal.
    /// </summary>
    public async Task<EntraSyncService.SyncResult> RunDeltaSyncAsync(
        string server, string? username, string? password, AppSettings settings, Action<string> report)
    {
        server = server.Trim();
        var hasUser = !string.IsNullOrWhiteSpace(username);
        report($"• Starting Entra Connect delta sync on {server} ({(hasUser ? username!.Trim() : "current user")})…");
        var sync = await _entraSync.RunDeltaSyncAsync(server, hasUser ? username!.Trim() : null, hasUser ? password : null);
        settings.EntraConnectServer = server;
        _settingsStore.Save(settings);
        return sync;
    }

    /// <summary>Polls Entra ID for the user by UPN after an initial settle; null if it never appears in time.</summary>
    public async Task<CloudUserInfo?> PollForCloudUserAsync(string upn, Action<string> report)
    {
        await Task.Delay(TimeSpan.FromSeconds(12)); // let the delta sync get going (10–15s)
        for (int attempt = 1; attempt <= 12; attempt++) // ~12 × 6s ≈ 72s more
        {
            report($"   • Checking Entra ID (attempt {attempt})…");
            try
            {
                var user = await _graph.GetUserByUpnAsync(upn);
                if (user is not null) return user;
            }
            catch (Exception ex) { report("     …check failed: " + GraphErrors.Friendly(ex)); }
            await Task.Delay(TimeSpan.FromSeconds(6));
        }
        return null;
    }

    /// <summary>
    /// Batch-aware variant of <see cref="PollForCloudUserAsync"/>: settles once for the whole batch, then
    /// polls all still-missing UPNs against a shared deadline (≈2 min). Returns a UPN→user map of those
    /// that appeared; UPNs that never showed up are simply absent from the map.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, CloudUserInfo>> PollForCloudUsersAsync(
        IEnumerable<string> upns, Action<string> report)
    {
        var found = new Dictionary<string, CloudUserInfo>(StringComparer.OrdinalIgnoreCase);
        var pending = new HashSet<string>(upns.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()),
            StringComparer.OrdinalIgnoreCase);
        if (pending.Count == 0) return found;

        await Task.Delay(TimeSpan.FromSeconds(12)); // one settle for the whole batch (10–15s)
        for (int attempt = 1; attempt <= 20 && pending.Count > 0; attempt++) // ~20 × 6s ≈ 2 min
        {
            report($"   • Checking Entra ID for {pending.Count} user(s) (attempt {attempt})…");
            foreach (var upn in pending.ToList())
            {
                try
                {
                    var user = await _graph.GetUserByUpnAsync(upn);
                    if (user is not null) { found[upn] = user; pending.Remove(upn); }
                }
                catch (Exception ex) { report($"     …check failed for {upn}: {GraphErrors.Friendly(ex)}"); }
            }
            if (pending.Count > 0) await Task.Delay(TimeSpan.FromSeconds(6));
        }
        return found;
    }

    /// <summary>Adds the (already-synced) user to each cloud group, reporting per-group success/failure.</summary>
    public async Task<(int Ok, int Failed)> AddUserToGroupsAsync(
        string userId, IEnumerable<CloudGroupRef> groups, Action<string> report)
    {
        int ok = 0, failed = 0;
        foreach (var g in groups)
        {
            try { await _graph.AddMemberToGroupAsync(g.Id, userId); ok++; report($"   ✓ {g.Name}"); }
            catch (Exception ex) { failed++; report($"   ✗ {g.Name}: {GraphErrors.Friendly(ex)}"); }
        }
        return (ok, failed);
    }

    /// <summary>
    /// Issues a Temporary Access Pass for the (already-synced) cloud user. Returns the pass — visible only once —
    /// or null if issuing failed (the failure is reported via <paramref name="report"/>, not thrown, so it never
    /// derails the rest of provisioning). The pass code is never echoed to <paramref name="report"/>.
    /// </summary>
    public async Task<TemporaryAccessPassResult?> IssueTemporaryAccessPassAsync(
        string userId, int lifetimeMinutes, bool isUsableOnce, Action<string> report)
    {
        try
        {
            var tap = await _graph.CreateTemporaryAccessPassAsync(userId, lifetimeMinutes, isUsableOnce);
            report($"   ✓ Temporary Access Pass issued (valid {lifetimeMinutes} min, {(isUsableOnce ? "one-time use" : "multi-use")}).");
            return tap;
        }
        catch (Exception ex) { report("   ✗ Temporary Access Pass failed: " + GraphErrors.Friendly(ex)); return null; }
    }
}
