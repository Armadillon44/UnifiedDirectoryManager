using System.Management.Automation;
using System.Management.Automation.Runspaces;
using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Hosted-PowerShell implementation of <see cref="IExchangeService"/>. Runs the ExchangeOnlineManagement
/// module inside an in-process runspace and connects with an access token borrowed from the Entra sign-in
/// (<see cref="IGraphService.GetAccessTokenAsync"/>) for the outlook.office365.com resource — so the admin
/// is not prompted a second time.
///
/// The runspace and its commands are not reentrant, so every operation is serialized behind <see cref="_gate"/>
/// and the (blocking) PowerShell pipeline runs off the UI thread. The connect access token is static and expires
/// after ~1h; operations that fail because the session lapsed reconnect once with a fresh token and retry.
/// </summary>
public sealed class ExchangeService : IExchangeService, IDisposable
{
    // Exchange Online admin resource (NOT Graph). The .default scope yields the consented EXO permissions.
    private static readonly string[] ExoScopes = { "https://outlook.office365.com/.default" };

    private const string ModuleName = "ExchangeOnlineManagement";

    private readonly IGraphService _graph;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _organization;
    private Runspace? _runspace;
    private bool _connected;

    public ExchangeService(IGraphService graph) => _graph = graph;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_organization);
    public bool IsConnected => _connected;
    public string? Organization => _organization;

    public void Configure(string organization)
    {
        var org = organization?.Trim();
        if (string.Equals(org, _organization, StringComparison.OrdinalIgnoreCase)) return;
        // A different org means any existing session is stale.
        Disconnect();
        _organization = org;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await EnsureConnectedLockedAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public void Disconnect()
    {
        _gate.Wait();
        try
        {
            if (_connected && _runspace is { RunspaceStateInfo.State: RunspaceState.Opened })
            {
                try
                {
                    using var ps = NewPowerShell();
                    ps.AddCommand("Disconnect-ExchangeOnline")
                      .AddParameter("Confirm", false)
                      .AddParameter("ErrorAction", "SilentlyContinue");
                    ps.Invoke();
                }
                catch (Exception ex) { AppLog.Instance.Warn("Disconnect-ExchangeOnline failed: " + ex.Message); }
            }
            _connected = false;
            try { _runspace?.Dispose(); } catch { /* best effort */ }
            _runspace = null;
        }
        finally { _gate.Release(); }
    }

    public async Task<MailboxInfo?> GetMailboxAsync(string identity, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identity)) return null;
        // SilentlyContinue + empty-result check so a missing mailbox returns null rather than throwing.
        var results = await RunAsync(cancellationToken, throwOnError: false, ps =>
            ps.AddCommand("Get-Mailbox")
              .AddParameter("Identity", identity)
              .AddParameter("ErrorAction", "SilentlyContinue")).ConfigureAwait(false);
        return results.Count == 0 ? null : MapMailbox(results[0]);
    }

    public async Task<IReadOnlyList<MailboxRecipient>> SearchRecipientsAsync(string text, CancellationToken cancellationToken = default)
    {
        var results = await RunAsync(cancellationToken, throwOnError: false, ps =>
        {
            var cmd = ps.AddCommand("Get-Recipient")
                        .AddParameter("ResultSize", 50)
                        .AddParameter("ErrorAction", "SilentlyContinue");
            if (!string.IsNullOrWhiteSpace(text))
            {
                // Escape single quotes to keep the OPATH filter literal (no injection).
                var esc = text.Trim().Replace("'", "''");
                cmd.AddParameter("Filter", $"DisplayName -like '*{esc}*' -or PrimarySmtpAddress -like '*{esc}*'");
            }
        }).ConfigureAwait(false);

        return results.Select(MapRecipient).ToList();
    }

    public Task ConvertMailboxAsync(string identity, MailboxType type, CancellationToken cancellationToken = default)
    {
        if (type == MailboxType.Unknown)
            throw new ArgumentException("Only Regular and Shared are convertible.", nameof(type));

        var typeArg = type == MailboxType.Shared ? "Shared" : "Regular";
        return RunAsync(cancellationToken, throwOnError: true, ps =>
            ps.AddCommand("Set-Mailbox")
              .AddParameter("Identity", identity)
              .AddParameter("Type", typeArg)
              .AddParameter("ErrorAction", "Stop"));
    }

    public Task SetForwardingAsync(string identity, string forwardingTargetIdentity, bool deliverToMailboxAndForward, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(forwardingTargetIdentity))
            throw new ArgumentException("A forwarding target is required.", nameof(forwardingTargetIdentity));

        return RunAsync(cancellationToken, throwOnError: true, ps =>
            ps.AddCommand("Set-Mailbox")
              .AddParameter("Identity", identity)
              .AddParameter("ForwardingAddress", forwardingTargetIdentity)
              .AddParameter("DeliverToMailboxAndForward", deliverToMailboxAndForward)
              .AddParameter("ErrorAction", "Stop"));
    }

    public Task ClearForwardingAsync(string identity, CancellationToken cancellationToken = default)
    {
        return RunAsync(cancellationToken, throwOnError: true, ps =>
            ps.AddCommand("Set-Mailbox")
              .AddParameter("Identity", identity)
              .AddParameter("ForwardingAddress", null)            // $null clears the internal forwarding target
              .AddParameter("DeliverToMailboxAndForward", false)
              .AddParameter("ErrorAction", "Stop"));
    }

    // --- connection / execution plumbing (all callers hold _gate) ---

    private async Task EnsureConnectedLockedAsync(CancellationToken ct)
    {
        if (_connected && _runspace is { RunspaceStateInfo.State: RunspaceState.Opened }) return;
        if (!IsConfigured) throw new InvalidOperationException("Set the Exchange organization before connecting.");

        // Borrow an Exchange-resource token from the existing Entra sign-in (throws if not signed in). This runs
        // before the cmdlet-invoke wrapper, so translate its failures here — the most common first-run failure is
        // the Exchange Online permission not being consented for the app registration.
        string token;
        try
        {
            token = await _graph.GetAccessTokenAsync(ExoScopes, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new ExchangeException(
                "Couldn't get an Exchange Online access token. Grant the app registration the Office 365 Exchange "
                + "Online permission with admin consent, then retry. (" + ExchangeErrors.Friendly(ex) + ")", ex);
        }

        try { OpenRunspaceLocked(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new ExchangeException("Couldn't start the Exchange Online PowerShell session: " + ExchangeErrors.Friendly(ex), ex);
        }

        try
        {
            await InvokeOnceLockedAsync(ct, throwOnError: true, ps =>
                ps.AddCommand("Connect-ExchangeOnline")
                  .AddParameter("AccessToken", token)
                  .AddParameter("Organization", _organization)
                  .AddParameter("ShowBanner", false)
                  .AddParameter("SkipLoadingFormatData")   // avoids the AuthorizationManager format-data failure when hosting in-proc
                  .AddParameter("ErrorAction", "Stop")).ConfigureAwait(false);
        }
        catch (ExchangeException ex) when (ex.Message.Contains("Connect-ExchangeOnline", StringComparison.OrdinalIgnoreCase)
                                           && ex.Message.Contains("not recognized", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExchangeException("The ExchangeOnlineManagement PowerShell module was not found on this machine.", ex);
        }

        _connected = true;
        AppLog.Instance.Info($"Connected to Exchange Online ({_organization}).");
    }

    private void OpenRunspaceLocked()
    {
        if (_runspace is { RunspaceStateInfo.State: RunspaceState.Opened }) return;

        try { _runspace?.Dispose(); } catch { /* replacing a faulted runspace */ }

        // Use the FULL default session (NOT CreateDefault2). Connect-ExchangeOnline downloads a cloud-side
        // "WebModule" of the mailbox cmdlets (Get-Mailbox, Set-Mailbox, …) and forms it using the standard
        // engine modules plus PackageManagement/PowerShellGet. The minimal CreateDefault2 session starves that
        // step, so the connect fails with "Module could not be correctly formed. Please run Connect-ExchangeOnline
        // again." ExecutionPolicy.Bypass is what stops the AuthorizationManager from rejecting the (unsigned)
        // module format data when hosting in-proc — it works with the full session too.
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        iss.ImportPSModule(new[] { ModuleName });

        var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        _runspace = runspace;
    }

    private async Task<List<PSObject>> RunAsync(CancellationToken ct, bool throwOnError, Action<PowerShell> build)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedLockedAsync(ct).ConfigureAwait(false);
            try
            {
                return await InvokeOnceLockedAsync(ct, throwOnError, build).ConfigureAwait(false);
            }
            catch (ExchangeException ex) when (throwOnError && IsSessionExpired(ex))
            {
                AppLog.Instance.Warn("Exchange Online session looks expired; reconnecting once and retrying.");
                _connected = false;
                await EnsureConnectedLockedAsync(ct).ConfigureAwait(false);
                return await InvokeOnceLockedAsync(ct, throwOnError, build).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<List<PSObject>> InvokeOnceLockedAsync(CancellationToken ct, bool throwOnError, Action<PowerShell> build)
    {
        using var ps = NewPowerShell();
        build(ps);

        using var reg = ct.Register(() => { try { ps.Stop(); } catch { /* best effort */ } });

        List<PSObject> results;
        try
        {
            var output = await Task.Run(() => ps.Invoke(), ct).ConfigureAwait(false);
            results = output.ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new ExchangeException(ExchangeErrors.Friendly(ex), ex);
        }

        if (throwOnError && ps.HadErrors && ps.Streams.Error.Count > 0)
            throw new ExchangeException(ExchangeErrors.Friendly(ps.Streams.Error[0]));

        return results;
    }

    private PowerShell NewPowerShell()
    {
        var ps = PowerShell.Create();
        ps.Runspace = _runspace!;
        return ps;
    }

    // Heuristic: did the command fail because the EXO session/token lapsed (so a reconnect is worth one retry)?
    private static bool IsSessionExpired(Exception ex)
    {
        var m = ex.Message;
        return m.Contains("session", StringComparison.OrdinalIgnoreCase)
            || m.Contains("expired", StringComparison.OrdinalIgnoreCase)
            || m.Contains("token", StringComparison.OrdinalIgnoreCase)
            || m.Contains("reconnect", StringComparison.OrdinalIgnoreCase)
            || m.Contains("not recognized", StringComparison.OrdinalIgnoreCase); // cmdlet vanished after the session dropped
    }

    private static MailboxInfo MapMailbox(PSObject o)
    {
        string S(string p) => o.Properties[p]?.Value?.ToString() ?? string.Empty;

        var rtd = S("RecipientTypeDetails");
        var type = rtd.Equals("SharedMailbox", StringComparison.OrdinalIgnoreCase) ? MailboxType.Shared
                 : rtd.Equals("UserMailbox", StringComparison.OrdinalIgnoreCase) ? MailboxType.Regular
                 : MailboxType.Unknown;

        var upn = S("UserPrincipalName");
        var fwd = S("ForwardingAddress");

        return new MailboxInfo
        {
            Identity = upn.Length > 0 ? upn : S("PrimarySmtpAddress"),
            DisplayName = S("DisplayName"),
            PrimarySmtpAddress = S("PrimarySmtpAddress"),
            Type = type,
            ForwardingAddress = fwd.Length > 0 ? fwd : null,
            DeliverToMailboxAndForward = o.Properties["DeliverToMailboxAndForward"]?.Value is bool b && b,
        };
    }

    private static MailboxRecipient MapRecipient(PSObject o)
    {
        string S(string p) => o.Properties[p]?.Value?.ToString() ?? string.Empty;
        var smtp = S("PrimarySmtpAddress");
        return new MailboxRecipient
        {
            Identity = smtp.Length > 0 ? smtp : S("Identity"),
            DisplayName = S("DisplayName"),
            PrimarySmtpAddress = smtp,
            RecipientType = S("RecipientTypeDetails"),
        };
    }

    public void Dispose()
    {
        try { Disconnect(); } catch { /* best effort */ }
        _gate.Dispose();
    }
}
