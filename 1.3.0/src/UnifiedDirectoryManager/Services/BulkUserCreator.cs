using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Runs a bulk user-creation batch in phases (the key difference from the per-user New User wizard):
/// <list type="number">
/// <item>create <b>every</b> user on-prem (generating a passphrase each; the batch never aborts on a
/// single failure, mirroring <see cref="ScenarioRunner"/>);</item>
/// <item>if any created row needs cloud (groups / TAP), run <b>one</b> Entra Connect delta sync;</item>
/// <item>wait for the created users to appear in Entra (one settle, then batch poll);</item>
/// <item>add cloud groups and issue Temporary Access Passes for the rows that requested them.</item>
/// </list>
/// Generated passphrases and TAP codes live only on the returned <see cref="BulkCreateReport"/> — they
/// are never written to the application log.
/// </summary>
public sealed class BulkUserCreator
{
    private readonly IDirectoryService _directory;
    private readonly CloudProvisioningService _cloud;
    private readonly AppSettings _settings;

    public BulkUserCreator(IDirectoryService directory, CloudProvisioningService cloud, AppSettings settings)
    {
        _directory = directory;
        _cloud = cloud;
        _settings = settings;
    }

    /// <summary>Runs phase 1 (on-prem create) then the cloud phases. Returns the per-user report.</summary>
    public async Task<BulkCreateReport> RunAsync(
        IReadOnlyList<BulkCreateRequest> rows, BulkCloudOptions cloud,
        IProgress<int>? progress, IProgress<string>? live, CancellationToken ct = default)
    {
        var results = rows.Select(r => new BulkCreateUserResult
        {
            Label = r.Label,
            SamAccountName = r.Attributes.TryGetValue("sAMAccountName", out var s) ? s : string.Empty,
        }).ToList();

        // --- Phase 1: create every user on-prem ---
        var done = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var req = rows[i];
            var res = results[i];
            live?.Report($"▶ Creating {req.Label}…");
            var passphrase = PassphraseGenerator.Generate();
            res.GeneratedPassword = passphrase;
            try
            {
                var created = await _directory.CreateUserAsync(
                    req.TargetOu, req.Attributes, req.OnPremGroupDns,
                    passphrase, req.Enabled, mustChangePassword: false, req.Proxies, ct);
                res.Success = true;
                res.DistinguishedName = created.DistinguishedName;
                res.PasswordSet = created.PasswordSet;
                if (created.PasswordSet)
                    live?.Report($"   ✓ Created {created.DistinguishedName}");
                else
                {
                    res.CloudSummary = "Password not set (insecure channel) — account left disabled.";
                    live?.Report($"   ⚠ Created {created.DistinguishedName} but password NOT set — account disabled.");
                }
            }
            catch (Exception ex)
            {
                res.Success = false;
                res.Error = DirectoryService.Friendly(ex);
                live?.Report($"   ✗ {req.Label}: {res.Error}");
            }
            progress?.Report(++done);
        }

        // --- Phases 2–4: cloud (only if at least one created row needs it) ---
        if (rows.Where((r, i) => results[i].Success && r.NeedsCloud).Any())
            await RunCloudPhasesAsync(rows, results, cloud, live, ct);

        AppLog.Instance.Info($"Bulk create finished: {results.Count(r => r.Success)} created, "
                           + $"{results.Count(r => !r.Success)} failed (of {rows.Count}).");
        return new BulkCreateReport { Items = results };
    }

    /// <summary>
    /// Runs the cloud phases (single delta sync → batch poll → add groups / issue TAP) for the
    /// successfully-created rows that need cloud and haven't had it applied yet. Safe to call again to
    /// retry after a sync failure — the on-prem accounts already exist and are never recreated.
    /// Returns false when the delta sync itself failed (so the caller can keep offering a retry).
    /// </summary>
    public async Task<bool> RunCloudPhasesAsync(
        IReadOnlyList<BulkCreateRequest> rows, IReadOnlyList<BulkCreateUserResult> results,
        BulkCloudOptions cloud, IProgress<string>? live, CancellationToken ct = default)
    {
        void Report(string s) => live?.Report(s);

        var pending = Enumerable.Range(0, rows.Count)
            .Where(i => results[i].Success && rows[i].NeedsCloud && !results[i].CloudApplied)
            .ToList();
        if (pending.Count == 0) return true;

        // Phase 2: one delta sync for the whole batch.
        var sync = await _cloud.RunDeltaSyncAsync(
            cloud.EntraConnectServer,
            cloud.SpecifyCredentials ? cloud.Username : null,
            cloud.SpecifyCredentials ? cloud.Password : null,
            _settings, Report);
        if (!sync.Success)
        {
            Report("✗ Entra Connect sync failed: " + sync.Output.Replace(Environment.NewLine, " ").Trim());
            foreach (var i in pending)
                results[i].CloudSummary = "Created; cloud not applied (delta sync failed — retry available).";
            return false;
        }
        Report("✓ Delta sync started.");

        // Phase 3: wait for the created users to appear in Entra.
        Report("• Waiting for the new users to appear in Entra ID…");
        var found = await _cloud.PollForCloudUsersAsync(pending.Select(i => rows[i].Upn ?? string.Empty), Report);

        // Phase 4: add cloud groups + issue TAPs.
        foreach (var i in pending)
        {
            ct.ThrowIfCancellationRequested();
            var req = rows[i];
            var res = results[i];
            var upn = req.Upn?.Trim() ?? string.Empty;
            if (upn.Length == 0 || !found.TryGetValue(upn, out var cloudUser))
            {
                res.CloudSummary = "Created; user hadn’t synced to Entra in time — cloud groups / TAP NOT applied (retry available).";
                Report($"   ✗ {req.Label}: not found in Entra ID within the wait window.");
                continue; // leave CloudApplied false → retryable
            }

            var summary = new List<string>();
            if (req.CloudGroups.Count > 0)
            {
                Report($"• {req.Label}: adding cloud groups…");
                var (ok, failed) = await _cloud.AddUserToGroupsAsync(cloudUser.Id, req.CloudGroups, Report);
                summary.Add($"added to {ok} cloud group(s)" + (failed > 0 ? $", {failed} failed" : ""));
            }
            if (req.IssueTap)
            {
                Report($"• {req.Label}: issuing a Temporary Access Pass…");
                var tap = await _cloud.IssueTemporaryAccessPassAsync(cloudUser.Id, req.TapLifetimeMinutes, req.TapOneTimeUse, Report);
                if (tap is { Pass.Length: > 0 }) { res.TapCode = tap.Pass; summary.Add("TAP issued (copy it now)"); }
                else summary.Add("TAP failed");
            }
            res.CloudApplied = true;
            res.CloudSummary = summary.Count > 0 ? string.Join("; ", summary) : "Cloud: nothing to apply.";
        }
        Report("Done.");
        return true;
    }
}
