using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Executes a <see cref="Scenario"/> against a set of target objects. Each target runs every step in
/// order; if a step throws, that target is marked failed and the runner moves on to the next target.
/// </summary>
public sealed class ScenarioRunner
{
    private readonly IDirectoryService _directory;
    private readonly IGraphService _graph;
    private readonly IExchangeService _exchange;

    public ScenarioRunner(IDirectoryService directory, IGraphService graph, IExchangeService exchange)
    {
        _directory = directory;
        _graph = graph;
        _exchange = exchange;
    }

    public async Task<BulkResult> RunAsync(
        Scenario scenario, IReadOnlyList<AdObjectRow> targets,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default,
        IList<string>? operationLog = null, IProgress<string>? live = null)
    {
        var results = new List<BulkItemResult>(targets.Count);
        var done = 0;
        foreach (var target in targets)
        {
            if (cancellationToken.IsCancellationRequested) break; // stop early, returning the partial results so far
            var dn = target.DistinguishedName;
            ScenarioStep? current = null;
            operationLog?.Add($"=== {target.Type}: {target.Name} ===");
            operationLog?.Add($"    {target.DistinguishedName}");
            live?.Report($"▶ {target.Type}: {target.Name}");
            try
            {
                var n = 0;
                foreach (var step in scenario.Steps)
                {
                    current = step;
                    // The Save-operation-log step is a meta-step: it records nothing per object and makes no change.
                    if (step.Action == ScenarioActionType.SaveOperationLog) continue;
                    n++;
                    var (newDn, detail) = await RunStepAsync(step, dn, target, cancellationToken);
                    dn = newDn;
                    operationLog?.Add($"    {n}. {detail}");
                    live?.Report($"    ✓ {detail}");
                }
                results.Add(new BulkItemResult(target.DistinguishedName, target.Name, true, null));
                operationLog?.Add("    RESULT: Success");
            }
            catch (Exception ex)
            {
                // Name the failing step: earlier steps may already be committed (AD has no transactions),
                // so the operator needs to know how far the scenario got.
                var where = current is null ? string.Empty : $"step “{current.Action}”: ";
                var reason = ex is Microsoft.Graph.Models.ODataErrors.ODataError ? GraphErrors.Friendly(ex) : DirectoryService.Friendly(ex);
                results.Add(new BulkItemResult(target.DistinguishedName, target.Name, false, where + reason));
                operationLog?.Add($"    RESULT: FAILED — {where}{reason}");
                live?.Report($"    ✗ {where}{reason}");
            }
            operationLog?.Add(string.Empty);
            progress?.Report(++done);
        }
        AppLog.Instance.Info($"Ran scenario “{scenario.Name}” on {targets.Count} object(s): "
                           + $"{results.Count(r => r.Success)} ok, {results.Count(r => !r.Success)} failed.");
        return new BulkResult(results);
    }

    /// <summary>
    /// Runs one step and returns the object's (possibly changed) DN plus a human-readable description of
    /// the concrete change made (for the operation log — e.g. the names of the groups removed).
    /// </summary>
    private async Task<(string Dn, string Detail)> RunStepAsync(ScenarioStep step, string dn, AdObjectRow target, CancellationToken ct)
    {
        string detail;
        switch (step.Action)
        {
            case ScenarioActionType.Disable:
                await _directory.ApplyChangesAsync(dn, new[] { new PendingChange { Op = ChangeOp.Disable } }, ct);
                detail = "Disabled account";
                break;

            case ScenarioActionType.Enable:
                await _directory.ApplyChangesAsync(dn, new[] { new PendingChange { Op = ChangeOp.Enable } }, ct);
                detail = "Enabled account";
                break;

            case ScenarioActionType.Unlock:
                await _directory.UnlockAccountAsync(dn, ct);
                detail = "Unlocked account";
                break;

            case ScenarioActionType.RemoveAllGroups:
            {
                var attrs = await _directory.LoadObjectAsync(dn, ct);
                var memberOf = attrs.FirstOrDefault(a => a.LdapName.Equals("memberOf", StringComparison.OrdinalIgnoreCase));
                var groupDns = memberOf?.RawValues.ToList() ?? new List<string>();
                if (groupDns.Count > 0)
                    await _directory.ApplyChangesAsync(dn,
                        new[] { new PendingChange { Op = ChangeOp.RemoveFromGroups, Values = groupDns } }, ct);
                detail = groupDns.Count == 0
                    ? "Removed all on-prem group memberships (none to remove)"
                    : $"Removed from {groupDns.Count} on-prem group(s):" + Bullets(groupDns.Select(NameResolver.RdnFallback));
                break;
            }

            case ScenarioActionType.AddToGroups:
                if (step.GroupDns.Count > 0)
                    await _directory.ApplyChangesAsync(dn,
                        new[] { new PendingChange { Op = ChangeOp.AddToGroups, Values = step.GroupDns.ToList() } }, ct);
                detail = step.GroupDns.Count == 0
                    ? "Add to on-prem groups (no groups configured)"
                    : $"Added to {step.GroupDns.Count} on-prem group(s):" + Bullets(step.GroupDns.Select(NameResolver.RdnFallback));
                break;

            case ScenarioActionType.RemoveFromGroups:
                if (step.GroupDns.Count > 0)
                    await _directory.ApplyChangesAsync(dn,
                        new[] { new PendingChange { Op = ChangeOp.RemoveFromGroups, Values = step.GroupDns.ToList() } }, ct);
                detail = step.GroupDns.Count == 0
                    ? "Remove from on-prem groups (no groups configured)"
                    : $"Removed from {step.GroupDns.Count} on-prem group(s):" + Bullets(step.GroupDns.Select(NameResolver.RdnFallback));
                break;

            case ScenarioActionType.SetAttribute:
            {
                if (string.IsNullOrWhiteSpace(step.Attribute)) { detail = "Set attribute (no attribute configured)"; break; }
                var value = ResolveTokens(step.Value, target);
                var change = string.IsNullOrWhiteSpace(value)
                    ? new PendingChange { Op = ChangeOp.Clear, LdapName = step.Attribute, FriendlyName = step.Attribute }
                    : new PendingChange { Op = ChangeOp.Set, LdapName = step.Attribute, FriendlyName = step.Attribute, Values = { value } };
                await _directory.ApplyChangesAsync(dn, new[] { change }, ct);
                detail = string.IsNullOrWhiteSpace(value) ? $"Cleared {step.Attribute}" : $"Set {step.Attribute} = {value}";
                break;
            }

            case ScenarioActionType.ClearAttribute:
                if (!string.IsNullOrWhiteSpace(step.Attribute))
                    await _directory.ApplyChangesAsync(dn,
                        new[] { new PendingChange { Op = ChangeOp.Clear, LdapName = step.Attribute, FriendlyName = step.Attribute } }, ct);
                detail = string.IsNullOrWhiteSpace(step.Attribute) ? "Clear attribute (no attribute configured)" : $"Cleared {step.Attribute}";
                break;

            case ScenarioActionType.SetDescription:
            {
                var value = ResolveTokens(step.Value, target);
                await _directory.ApplyChangesAsync(dn, new[]
                {
                    new PendingChange { Op = ChangeOp.Set, LdapName = "description", FriendlyName = "Description", Values = { value } },
                }, ct);
                detail = $"Set description = {value}";
                break;
            }

            case ScenarioActionType.MoveToOu:
                if (!string.IsNullOrWhiteSpace(step.TargetOu))
                    dn = await _directory.MoveObjectAsync(dn, step.TargetOu, ct); // DN changes after a move
                detail = string.IsNullOrWhiteSpace(step.TargetOu) ? "Move to OU (no target configured)" : $"Moved to {step.TargetOu}";
                break;

            // --- Cloud (Entra ID) actions on the object's synced twin ---

            case ScenarioActionType.CloudDisableAccount:
                await _graph.SetUserAccountEnabledAsync(await ResolveCloudUserIdAsync(dn, target, ct), false, ct);
                detail = "Cloud: disabled Entra ID account";
                break;

            case ScenarioActionType.CloudEnableAccount:
                await _graph.SetUserAccountEnabledAsync(await ResolveCloudUserIdAsync(dn, target, ct), true, ct);
                detail = "Cloud: enabled Entra ID account";
                break;

            case ScenarioActionType.CloudRevokeSessions:
                await _graph.RevokeSignInSessionsAsync(await ResolveCloudUserIdAsync(dn, target, ct), ct);
                detail = "Cloud: revoked sign-in sessions";
                break;

            case ScenarioActionType.CloudAddToGroups:
                await CloudGroupOpAsync(dn, target, step, add: true, ct);
                detail = step.CloudGroups.Count == 0
                    ? "Cloud: add to groups (no groups configured)"
                    : $"Cloud: added to {step.CloudGroups.Count} Entra ID group(s):" + Bullets(step.CloudGroups.Select(g => g.Name));
                break;

            case ScenarioActionType.CloudRemoveFromGroups:
                await CloudGroupOpAsync(dn, target, step, add: false, ct);
                detail = step.CloudGroups.Count == 0
                    ? "Cloud: remove from groups (no groups configured)"
                    : $"Cloud: removed from {step.CloudGroups.Count} Entra ID group(s):" + Bullets(step.CloudGroups.Select(g => g.Name));
                break;

            case ScenarioActionType.CloudRemoveAllGroups:
                detail = await CloudRemoveAllGroupsAsync(dn, target, ct);
                break;

            // --- Exchange Online mailbox actions (pure-cloud): convert type + internal forwarding ---

            case ScenarioActionType.ExchangeConvertToShared:
                await _exchange.ConvertMailboxAsync(await ResolveMailboxIdentityAsync(dn, target, ct), MailboxType.Shared, ct);
                detail = "Exchange: converted mailbox to Shared";
                break;

            case ScenarioActionType.ExchangeConvertToRegular:
                await _exchange.ConvertMailboxAsync(await ResolveMailboxIdentityAsync(dn, target, ct), MailboxType.Regular, ct);
                detail = "Exchange: converted mailbox to Regular (user)";
                break;

            case ScenarioActionType.ExchangeSetForwarding:
                // No target configured is a graceful no-op (mirrors MoveToOu with a blank OU), since the
                // editor can currently save this action before its target picker lands (Phase 3).
                if (step.ForwardingTarget is null || string.IsNullOrWhiteSpace(step.ForwardingTarget.Identity))
                {
                    detail = "Exchange: set forwarding (no target configured)";
                    break;
                }
                await _exchange.SetForwardingAsync(
                    await ResolveMailboxIdentityAsync(dn, target, ct),
                    step.ForwardingTarget.Identity, step.DeliverAndForward, ct);
                // Fall back to the identity when no display name was captured, so the log never shows a blank recipient.
                var fwdLabel = string.IsNullOrWhiteSpace(step.ForwardingTarget.Name) ? step.ForwardingTarget.Identity : step.ForwardingTarget.Name;
                detail = $"Exchange: set forwarding to {fwdLabel}"
                       + (step.DeliverAndForward ? " (deliver + forward)" : " (forward only)");
                break;

            case ScenarioActionType.ExchangeClearForwarding:
                await _exchange.ClearForwardingAsync(await ResolveMailboxIdentityAsync(dn, target, ct), ct);
                detail = "Exchange: cleared mailbox forwarding";
                break;

            case ScenarioActionType.ExchangeDelegateToManager:
            {
                var mailboxId = await ResolveMailboxIdentityAsync(dn, target, ct); // guards user + exchange available
                var managerDn = await CorrelationAsync(dn, "manager", formatted: false, ct);
                if (string.IsNullOrWhiteSpace(managerDn)) { detail = "Exchange: delegate to manager (no manager set — skipped)"; break; }
                var managerUpn = await CorrelationAsync(managerDn, "userPrincipalName", formatted: false, ct);
                if (string.IsNullOrWhiteSpace(managerUpn)) { detail = "Exchange: delegate to manager (manager has no userPrincipalName — skipped)"; break; }
                await _exchange.AddDelegateAsync(mailboxId, managerUpn, DelegateAccess.FullAccess, autoMapping: true, ct);
                detail = $"Exchange: delegated mailbox to manager {managerUpn} (Full Access)";
                break;
            }

            default:
                detail = step.Action.ToString();
                break;
        }
        return (dn, detail);
    }

    /// <summary>Removes the target's cloud twin from every Entra group it belongs to (skips dynamic + synced);
    /// returns a description of which groups were removed (and which were skipped).</summary>
    private async Task<string> CloudRemoveAllGroupsAsync(string dn, AdObjectRow target, CancellationToken ct)
    {
        var objectId = await ResolveCloudObjectIdAsync(dn, target, ct);
        var groups = await _graph.GetObjectMemberOfAsync(objectId, CloudKindFor(target.Type), ct);
        var removed = new List<string>();
        var skippedDynamic = new List<string>();
        var errors = new List<string>();
        foreach (var g in groups)
        {
            ct.ThrowIfCancellationRequested();
            // Dynamic (rule-managed) memberships can't be removed in the cloud — note them.
            if (string.Equals(g.MembershipKind, "Dynamic", StringComparison.OrdinalIgnoreCase)) { skippedDynamic.Add(g.DisplayName); continue; }
            // On-prem-synced memberships are on-prem-mastered (handled on the AD side), so they're skipped here
            // and intentionally not logged — there's nothing actionable to report about them.
            if (string.Equals(g.Origin, "Synced", StringComparison.OrdinalIgnoreCase)) continue;
            try { await _graph.RemoveMemberFromGroupAsync(g.Id, objectId, ct); removed.Add(g.DisplayName); }
            catch (Exception ex) { errors.Add($"{g.DisplayName}: {GraphErrors.Friendly(ex)}"); }
        }
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
        var detail = removed.Count == 0
            ? "Cloud: removed from all Entra ID groups (none removable)"
            : $"Cloud: removed from {removed.Count} Entra ID group(s):" + Bullets(removed);
        if (skippedDynamic.Count > 0)
            detail += Environment.NewLine + $"    skipped {skippedDynamic.Count} dynamic group(s):" + Bullets(skippedDynamic);
        return detail;
    }

    private static CloudObjectKind CloudKindFor(AdObjectType type) => type switch
    {
        AdObjectType.Computer => CloudObjectKind.Device,
        AdObjectType.Group => CloudObjectKind.Group,
        _ => CloudObjectKind.User,
    };

    private void EnsureSignedIn()
    {
        if (!_graph.IsSignedIn)
            throw new InvalidOperationException("Not signed in to Entra ID — sign in (File ▸ Settings ▸ Cloud) before running cloud steps.");
    }

    /// <summary>Guards Exchange steps: the service must be configured, and — since its token is borrowed from the
    /// Entra sign-in — the admin must be signed in to Entra ID.</summary>
    private void EnsureExchange()
    {
        if (!_exchange.IsConfigured)
            throw new InvalidOperationException("Exchange Online isn't configured — set the tenant/organization (File ▸ Settings ▸ Cloud) first.");
        if (!_graph.IsSignedIn)
            throw new InvalidOperationException("Not signed in to Entra ID — sign in (File ▸ Settings ▸ Cloud) before running Exchange steps (the Exchange token is obtained from the Entra sign-in).");
    }

    /// <summary>Resolves the target's mailbox identity (its on-prem userPrincipalName, which equals the cloud
    /// mailbox's UPN/primary SMTP for synced users). Mailbox actions apply to users only. Takes the live
    /// <paramref name="dn"/> (not target.DistinguishedName) so it still resolves after an earlier MoveToOu step.</summary>
    private async Task<string> ResolveMailboxIdentityAsync(string dn, AdObjectRow target, CancellationToken ct)
    {
        EnsureExchange();
        if (target.Type != AdObjectType.User)
            throw new InvalidOperationException("This Exchange action applies to user mailboxes only.");
        var upn = await CorrelationAsync(dn, "userPrincipalName", formatted: false, ct);
        return string.IsNullOrWhiteSpace(upn)
            ? throw new InvalidOperationException("No mailbox identity found (the account has no userPrincipalName).")
            : upn;
    }

    /// <summary>Adds/removes the target's cloud twin to/from the step's Entra groups; aggregates per-group errors.</summary>
    private async Task CloudGroupOpAsync(string dn, AdObjectRow target, ScenarioStep step, bool add, CancellationToken ct)
    {
        if (step.CloudGroups.Count == 0) return;
        var objectId = await ResolveCloudObjectIdAsync(dn, target, ct);
        var errors = new List<string>();
        foreach (var g in step.CloudGroups)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (add) await _graph.AddMemberToGroupAsync(g.Id, objectId, ct);
                else await _graph.RemoveMemberFromGroupAsync(g.Id, objectId, ct);
            }
            catch (Exception ex) { errors.Add($"{g.Name}: {GraphErrors.Friendly(ex)}"); }
        }
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
    }

    /// <summary>Resolves the target's Entra user id (disable/revoke apply to users only). Takes the live
    /// <paramref name="dn"/> so it still resolves after an earlier MoveToOu step re-parented the object.</summary>
    private async Task<string> ResolveCloudUserIdAsync(string dn, AdObjectRow target, CancellationToken ct)
    {
        EnsureSignedIn();
        if (target.Type != AdObjectType.User)
            throw new InvalidOperationException("This cloud action applies to user accounts only.");
        var upn = await CorrelationAsync(dn, "userPrincipalName", formatted: false, ct);
        var id = string.IsNullOrWhiteSpace(upn) ? null : (await _graph.GetUserByUpnAsync(upn, ct))?.Id;
        return id ?? throw new InvalidOperationException("No matching Entra ID user (the account may not be synced yet).");
    }

    /// <summary>Resolves the target's Entra object id (user by UPN, computer by name, group by on-prem SID).
    /// Takes the live <paramref name="dn"/> so it still resolves after an earlier MoveToOu step.</summary>
    private async Task<string> ResolveCloudObjectIdAsync(string dn, AdObjectRow target, CancellationToken ct)
    {
        EnsureSignedIn();
        string? id = null;
        switch (target.Type)
        {
            case AdObjectType.User:
                var upn = await CorrelationAsync(dn, "userPrincipalName", formatted: false, ct);
                id = string.IsNullOrWhiteSpace(upn) ? null : (await _graph.GetUserByUpnAsync(upn, ct))?.Id;
                break;
            case AdObjectType.Computer:
                id = string.IsNullOrWhiteSpace(target.Name) ? null : (await _graph.GetDevicesByComputerAsync(target.Name, null, ct)).FirstOrDefault()?.Id;
                break;
            case AdObjectType.Group:
                var sid = await CorrelationAsync(dn, "objectSid", formatted: true, ct); // formatted S-1-5-…
                id = string.IsNullOrWhiteSpace(sid) ? null : (await _graph.GetGroupByOnPremSidAsync(sid, ct))?.Id;
                break;
        }
        return id ?? throw new InvalidOperationException("This object has no matching Entra ID object (it may not be synced yet).");
    }

    /// <summary>Loads one correlation attribute (raw or display-formatted) from the on-prem object; null if absent.</summary>
    private async Task<string?> CorrelationAsync(string dn, string ldapName, bool formatted, CancellationToken ct)
    {
        var attrs = await _directory.LoadObjectAsync(dn, ct);
        var a = attrs.FirstOrDefault(x => x.LdapName.Equals(ldapName, StringComparison.OrdinalIgnoreCase));
        if (a is null) return null;
        var vals = formatted ? a.DisplayValues : a.RawValues;
        return vals.Count > 0 ? vals[0] : null;
    }

    /// <summary>Formats a list of group names as an indented, one-per-line block (for the operation log + live
    /// progress), so a multi-group change is readable instead of a long comma-separated run.</summary>
    private static string Bullets(IEnumerable<string> names) =>
        string.Concat(names.Select(n => Environment.NewLine + "        • " + n));

    /// <summary>Resolves the tokens supported in scenario text values.</summary>
    private string ResolveTokens(string template, AdObjectRow target)
    {
        if (string.IsNullOrEmpty(template)) return template;
        var now = DateTime.Now;
        return template
            .Replace("{datetime}", now.ToString("yyyy-MM-dd HH:mm"))
            .Replace("{date}", now.ToString("yyyy-MM-dd"))
            .Replace("{time}", now.ToString("HH:mm"))
            .Replace("{admin}", _directory.Current?.Username ?? string.Empty)
            .Replace("{name}", target.Name);
    }
}
