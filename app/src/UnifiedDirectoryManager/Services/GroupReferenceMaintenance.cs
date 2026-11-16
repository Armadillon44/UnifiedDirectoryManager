using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>What a sweep over the stored templates and scenarios did.</summary>
/// <param name="Ran">False when it could not run (not connected to AD), so nothing below means anything.</param>
/// <param name="Saved">How many templates and scenarios were rewritten.</param>
/// <param name="Missing">One line per stored group that could not be found, naming where it is stored.</param>
public sealed record GroupLinkSweep(bool Ran, int Saved, IReadOnlyList<string> Missing)
{
    public static GroupLinkSweep Skipped { get; } = new(false, 0, Array.Empty<string>());
}

/// <summary>
/// Keeps the group references stored in templates and scenarios current, across every file in the two stores.
/// The per-list logic is <see cref="GroupLinks"/>; this decides when it runs and saves what it changed.
/// </summary>
/// <remarks>
/// Runs at three moments. After connecting to AD, to give older files their GUIDs while their DNs still resolve
/// and to follow groups renamed or moved since the last session (in ADUC as much as here). After the template
/// or scenario editor closes, so a newly added group gets its GUID straight away. And after this app renames a
/// group, through <see cref="RetargetGroupAsync"/>, which needs no lookup because it already knows both DNs.
/// </remarks>
public sealed class GroupReferenceMaintenance
{
    private readonly IDirectoryService _directory;
    private readonly ITemplateStore _templates;
    private readonly IScenarioStore _scenarios;

    /// <summary>One pass at a time: two sweeps saving the same file would race.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GroupReferenceMaintenance(IDirectoryService directory, ITemplateStore templates, IScenarioStore scenarios)
    {
        _directory = directory;
        _templates = templates;
        _scenarios = scenarios;
    }

    /// <summary>
    /// Resolves every stored on-prem group reference, records missing GUIDs, moves DNs to where their groups are
    /// now, and saves the files that changed. Never throws: a sweep that fails leaves the files as they were.
    /// </summary>
    public async Task<GroupLinkSweep> UpgradeAllAsync(CancellationToken cancellationToken = default)
    {
        if (!_directory.IsConnected) return GroupLinkSweep.Skipped;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var saved = 0;
            var missing = new List<string>();

            foreach (var template in _templates.LoadAll())
            {
                if (template.GroupDns.Count == 0 && template.GroupGuids.Count == 0) continue;
                var r = await GroupLinks.ResolveAsync(_directory, template.GroupDns, template.GroupGuids, cancellationToken);
                missing.AddRange(r.Missing.Select(dn => $"Template “{template.Name}”: {NameResolver.RdnFallback(dn)} ({dn})"));
                if (!r.Changed) continue;
                _templates.Save(template, template.Name);
                saved++;
            }

            foreach (var scenario in _scenarios.LoadAll())
            {
                var changed = false;
                foreach (var step in scenario.Steps)
                {
                    if (step.GroupDns.Count == 0 && step.GroupGuids.Count == 0) continue;
                    var r = await GroupLinks.ResolveAsync(_directory, step.GroupDns, step.GroupGuids, cancellationToken);
                    missing.AddRange(r.Missing.Select(dn => $"Scenario “{scenario.Name}”: {NameResolver.RdnFallback(dn)} ({dn})"));
                    changed |= r.Changed;
                }
                if (!changed) continue;
                _scenarios.Save(scenario, scenario.Name);
                saved++;
            }

            if (saved > 0) AppLog.Instance.Info($"Group references: updated {saved} template(s)/scenario(s).");
            foreach (var line in missing) AppLog.Instance.Warn("Group reference not found — " + line);
            return new GroupLinkSweep(true, saved, missing);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("Could not update the group references in templates and scenarios: " + ex.Message);
            return GroupLinkSweep.Skipped;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// After this app renames or moves an on-prem group: points every template and scenario reference to it at
    /// its new DN. Returns how many files were rewritten.
    /// </summary>
    /// <remarks>
    /// Waits for the lock asynchronously, never by blocking. A sweep started from the UI thread holds the lock
    /// while its directory lookups come back to that same thread; blocking the thread for the lock would stop
    /// those from ever arriving, and the window would freeze for good.
    /// </remarks>
    public async Task<int> RetargetGroupAsync(string groupGuid, string oldDn, string newDn)
    {
        await _gate.WaitAsync();
        try
        {
            var saved = 0;
            foreach (var template in _templates.LoadAll())
            {
                if (!GroupLinks.Retarget(template.GroupDns, template.GroupGuids, groupGuid, oldDn, newDn)) continue;
                _templates.Save(template, template.Name);
                saved++;
            }
            foreach (var scenario in _scenarios.LoadAll())
            {
                var changed = false;
                foreach (var step in scenario.Steps)
                    changed |= GroupLinks.Retarget(step.GroupDns, step.GroupGuids, groupGuid, oldDn, newDn);
                if (!changed) continue;
                _scenarios.Save(scenario, scenario.Name);
                saved++;
            }
            if (saved > 0) AppLog.Instance.Info($"Pointed {saved} template(s)/scenario(s) at {newDn}.");
            return saved;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// After this app renames a cloud group: refreshes the name templates and scenarios show for it and, for an
    /// Exchange group whose primary address changed, the address a template adds members through. Cloud groups
    /// are stored by object ID, which a rename does not change, so this keeps the display honest rather than
    /// repairing a link. Returns how many files were rewritten.
    /// </summary>
    /// <param name="groupId">The Entra object ID (an Exchange group's ExternalDirectoryObjectId).</param>
    /// <param name="oldSmtp">The primary address before the rename, matched for references that hold no ID.</param>
    /// <param name="newSmtp">The primary address after the rename; null when it did not change.</param>
    public async Task<int> RefreshCloudGroupAsync(string groupId, string newName, string? oldSmtp, string? newSmtp)
    {
        bool IsThis(string? id, string? smtp) =>
            (!string.IsNullOrWhiteSpace(groupId) && string.Equals(id, groupId, StringComparison.OrdinalIgnoreCase))
            || (string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(oldSmtp)
                && string.Equals(smtp, oldSmtp, StringComparison.OrdinalIgnoreCase));

        await _gate.WaitAsync(); // asynchronously, for the reason given on RetargetGroupAsync
        try
        {
            var saved = 0;
            foreach (var template in _templates.LoadAll())
            {
                var changed = false;
                foreach (var g in template.CloudGroups.Where(g => IsThis(g.Id, null) && g.Name != newName))
                { g.Name = newName; changed = true; }
                foreach (var g in template.DistributionGroups.Where(g => IsThis(g.Id, g.Smtp)))
                {
                    if (g.Name != newName) { g.Name = newName; changed = true; }
                    if (!string.IsNullOrWhiteSpace(newSmtp) && !string.Equals(g.Smtp, newSmtp, StringComparison.OrdinalIgnoreCase))
                    { g.Smtp = newSmtp; changed = true; }
                }
                if (!changed) continue;
                _templates.Save(template, template.Name);
                saved++;
            }
            foreach (var scenario in _scenarios.LoadAll())
            {
                var changed = false;
                foreach (var step in scenario.Steps)
                {
                    foreach (var g in step.CloudGroups.Where(g => IsThis(g.Id, null) && g.Name != newName))
                    { g.Name = newName; changed = true; }
                    // A distribution list can be a forwarding target; it is stored by its primary address.
                    if (step.ForwardingTarget is { } fwd && !string.IsNullOrWhiteSpace(oldSmtp)
                        && string.Equals(fwd.Identity, oldSmtp, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(newSmtp)) fwd.Identity = newSmtp;
                        fwd.Name = newName;
                        changed = true;
                    }
                }
                if (!changed) continue;
                _scenarios.Save(scenario, scenario.Name);
                saved++;
            }
            if (saved > 0) AppLog.Instance.Info($"Refreshed “{newName}” in {saved} template(s)/scenario(s).");
            return saved;
        }
        finally { _gate.Release(); }
    }
}
