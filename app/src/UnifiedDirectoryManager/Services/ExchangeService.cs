using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Out-of-process implementation of <see cref="IExchangeService"/>. Runs the ExchangeOnlineManagement module
/// in a separate <c>pwsh</c> process rather than in-process, because the EXO module bundles its own
/// MSAL/Azure.Core that clash with the Graph SDK's newer versions inside a single process (the connect fails
/// with "Module could not be correctly formed"). A child process gets its own clean assemblies and a correct
/// PowerShell module environment, so the module works reliably.
///
/// A single long-lived pwsh session is kept and reused across operations (connecting per-operation would be far
/// too slow for bulk scenario runs). Communication is a line protocol over stdin/stdout: the C# side sends
/// <c>VERB &lt;base64-json&gt;</c> lines; the host script replies with a framed JSON envelope. The EXO access token
/// is borrowed from the Entra sign-in (<see cref="IGraphService.GetAccessTokenAsync"/>) and passed via stdin
/// (never on the command line). Requires PowerShell 7 + the ExchangeOnlineManagement module on the machine.
/// </summary>
public sealed class ExchangeService : IExchangeService, IDisposable
{
    private static readonly string[] ExoScopes = { "https://outlook.office365.com/.default" };
    private const string Begin = "<<<UDM-BEGIN>>>";
    private const string End = "<<<UDM-END>>>";
    private const string Ready = "<<<UDM-READY>>>";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan OpTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

    private readonly IGraphService _graph;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _organization;
    private Process? _pwsh;
    private readonly StringBuilder _stderr = new();
    private bool _connected;

    public ExchangeService(IGraphService graph) => _graph = graph;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_organization);
    public bool IsConnected => _connected && _pwsh is { HasExited: false };
    public string? Organization => _organization;

    public void Configure(string organization)
    {
        var org = organization?.Trim();
        if (string.Equals(org, _organization, StringComparison.OrdinalIgnoreCase)) return;
        Disconnect(); // a different org invalidates any existing session
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
        try { KillLocked(); }
        finally { _gate.Release(); }
    }

    public async Task<MailboxInfo?> GetMailboxAsync(string identity, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identity)) return null;
        var data = await RunOpAsync(new { op = "get-mailbox", identity }, cancellationToken).ConfigureAwait(false);
        return data is { ValueKind: JsonValueKind.Object } d ? MapMailbox(d) : null; // null data = mailbox not found
    }

    public async Task<IReadOnlyList<MailboxRecipient>> SearchRecipientsAsync(string text, CancellationToken cancellationToken = default)
    {
        var data = await RunOpAsync(new { op = "search-recipients", text = text ?? string.Empty }, cancellationToken).ConfigureAwait(false);
        var list = new List<MailboxRecipient>();
        if (data is { ValueKind: JsonValueKind.Array } arr)
            foreach (var e in arr.EnumerateArray()) list.Add(MapRecipient(e));
        else if (data is { ValueKind: JsonValueKind.Object } one) // ConvertTo-Json renders a single result as an object
            list.Add(MapRecipient(one));
        return list;
    }

    public Task ConvertMailboxAsync(string identity, MailboxType type, CancellationToken cancellationToken = default)
    {
        if (type == MailboxType.Unknown)
            throw new ArgumentException("Only Regular and Shared are convertible.", nameof(type));
        return RunOpAsync(new { op = "convert", identity, type = type == MailboxType.Shared ? "Shared" : "Regular" }, cancellationToken);
    }

    public Task SetForwardingAsync(string identity, string forwardingTargetIdentity, bool deliverToMailboxAndForward, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(forwardingTargetIdentity))
            throw new ArgumentException("A forwarding target is required.", nameof(forwardingTargetIdentity));
        return RunOpAsync(new { op = "set-forwarding", identity, target = forwardingTargetIdentity, deliver = deliverToMailboxAndForward }, cancellationToken);
    }

    public Task ClearForwardingAsync(string identity, CancellationToken cancellationToken = default)
        => RunOpAsync(new { op = "clear-forwarding", identity }, cancellationToken);

    public async Task<IReadOnlyList<MailboxDelegate>> GetDelegatesAsync(string identity, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identity)) return Array.Empty<MailboxDelegate>();
        var data = await RunOpAsync(new { op = "list-delegates", identity }, cancellationToken).ConfigureAwait(false);
        var list = new List<MailboxDelegate>();
        if (data is { ValueKind: JsonValueKind.Array } arr)
            foreach (var e in arr.EnumerateArray()) list.Add(MapDelegate(e));
        else if (data is { ValueKind: JsonValueKind.Object } one)
            list.Add(MapDelegate(one));
        return list;
    }

    public Task AddDelegateAsync(string identity, string delegateIdentity, DelegateAccess access, bool autoMapping, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(delegateIdentity)) throw new ArgumentException("A delegate is required.", nameof(delegateIdentity));
        if (access == DelegateAccess.None) throw new ArgumentException("At least one permission is required.", nameof(access));
        return RunOpAsync(new
        {
            op = "add-delegate",
            identity,
            delegateId = delegateIdentity,
            fullAccess = access.HasFlag(DelegateAccess.FullAccess),
            sendAs = access.HasFlag(DelegateAccess.SendAs),
            sendOnBehalf = access.HasFlag(DelegateAccess.SendOnBehalf),
            autoMapping,
        }, cancellationToken);
    }

    public Task RemoveDelegateAsync(string identity, string delegateIdentity, DelegateAccess access, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(delegateIdentity)) throw new ArgumentException("A delegate is required.", nameof(delegateIdentity));
        if (access == DelegateAccess.None) throw new ArgumentException("At least one permission is required.", nameof(access));
        return RunOpAsync(new
        {
            op = "remove-delegate",
            identity,
            delegateId = delegateIdentity,
            fullAccess = access.HasFlag(DelegateAccess.FullAccess),
            sendAs = access.HasFlag(DelegateAccess.SendAs),
            sendOnBehalf = access.HasFlag(DelegateAccess.SendOnBehalf),
        }, cancellationToken);
    }

    // --- session plumbing (all callers hold _gate) ---

    /// <summary>Runs one operation against the reused session, reconnecting once if the session lapsed. Throws
    /// <see cref="ExchangeException"/> on failure; returns the response's <c>data</c> element (may be null).</summary>
    private async Task<JsonElement?> RunOpAsync(object request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedLockedAsync(ct).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(request);
            var resp = await SendAndReadLockedAsync("OP", json, OpTimeout, ct).ConfigureAwait(false);
            if (!resp.Ok && IsSessionExpired(resp.Error))
            {
                AppLog.Instance.Warn("Exchange Online session looks expired; restarting the host and reconnecting once.");
                KillLocked();
                await EnsureConnectedLockedAsync(ct).ConfigureAwait(false);
                resp = await SendAndReadLockedAsync("OP", json, OpTimeout, ct).ConfigureAwait(false);
            }
            if (!resp.Ok)
            {
                if (!string.IsNullOrWhiteSpace(resp.Detail))
                    AppLog.Instance.Error("Exchange Online operation failed. Full PowerShell error record:" + Environment.NewLine + resp.Detail);
                throw new ExchangeException(ExchangeErrors.Friendly(resp.Error));
            }
            if (!string.IsNullOrWhiteSpace(resp.Detail))
                AppLog.Instance.Info("Exchange op diagnostics: " + resp.Detail);
            return resp.Data;
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureConnectedLockedAsync(CancellationToken ct)
    {
        if (IsConnected) return;
        if (!IsConfigured) throw new ExchangeException("Exchange Online isn't configured — set the tenant/organization first.");

        StartProcessLocked();
        await ReadUntilReadyLockedAsync(ct).ConfigureAwait(false);

        string token;
        try
        {
            token = await _graph.GetAccessTokenAsync(ExoScopes, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            KillLocked();
            throw new ExchangeException(
                "Couldn't get an Exchange Online access token. Grant the app registration the Office 365 Exchange "
                + "Online permission with admin consent, then retry. (" + ExchangeErrors.Friendly(ex) + ")", ex);
        }

        LogTokenClaims(token); // diagnostic: which delegated scopes did the token actually get? (never logs the token)

        // Delegated token → connect with -UserPrincipalName (the signed-in admin); -Organization is the
        // app-only pattern and leaves a delegated connect malformed. Pass both; the host prefers the UPN.
        var resp = await SendAndReadLockedAsync("CONNECT",
                JsonSerializer.Serialize(new { token, org = _organization, upn = _graph.SignedInAccount }), ConnectTimeout, ct)
            .ConfigureAwait(false);
        if (!resp.Ok)
        {
            KillLocked();
            if (!string.IsNullOrWhiteSpace(resp.Detail))
                AppLog.Instance.Error("Exchange Online connect failed. Full PowerShell error record:" + Environment.NewLine + resp.Detail);
            throw new ExchangeException("Connect to Exchange Online failed: " + ExchangeErrors.Friendly(resp.Error));
        }
        _connected = true;
        AppLog.Instance.Info($"Connected to Exchange Online ({_organization}) via pwsh host.");
    }

    private void StartProcessLocked()
    {
        KillLocked();
        var scriptPath = WriteHostScript();

        var psi = new ProcessStartInfo
        {
            FileName = ResolvePwsh(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in new[] { "-NoProfile", "-NoLogo", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath })
            psi.ArgumentList.Add(a);

        var p = new Process { StartInfo = psi };
        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            try { p.Dispose(); } catch { /* ignore */ }
            throw new ExchangeException(
                "Couldn't start PowerShell 7 (pwsh). Exchange Online features require PowerShell 7 and the "
                + "ExchangeOnlineManagement module installed on this machine (see the README ▸ Exchange Online "
                + "prerequisites). (" + ex.Message + ")", ex);
        }
        // stdin encoding can't be set via ProcessStartInfo reliably on all hosts; wrap it as UTF-8 (no BOM).
        // (Base64 payloads are ASCII anyway, so this only matters for safety.)
        _stderr.Clear();
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_stderr) { if (_stderr.Length < 8192) _stderr.AppendLine(e.Data); } };
        p.BeginErrorReadLine();
        _pwsh = p;
    }

    private void KillLocked()
    {
        _connected = false;
        var p = _pwsh;
        _pwsh = null;
        if (p is null) return;
        try
        {
            if (!p.HasExited)
            {
                try { p.StandardInput.WriteLine("QUIT"); p.StandardInput.Flush(); } catch { /* ignore */ }
                if (!p.WaitForExit(1500)) p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) { AppLog.Instance.Warn("Stopping the Exchange host process failed: " + ex.Message); }
        finally { try { p.Dispose(); } catch { /* ignore */ } }
    }

    private async Task ReadUntilReadyLockedAsync(CancellationToken ct)
    {
        while (true)
        {
            var line = await ReadLineLockedAsync(StartupTimeout, ct).ConfigureAwait(false);
            if (line is null)
                throw new ExchangeException("The Exchange Online host (pwsh) exited during startup. " + DrainStderr());
            if (line == Ready) return;
            if (line == Begin) // an error envelope before ready (e.g. the module failed to import)
            {
                var payload = await ReadLineLockedAsync(StartupTimeout, ct).ConfigureAwait(false);
                await ReadLineLockedAsync(StartupTimeout, ct).ConfigureAwait(false); // consume END
                var r = Parse(payload);
                throw new ExchangeException(ExchangeErrors.Friendly(r.Error ?? "The ExchangeOnlineManagement module could not be loaded."));
            }
            // ignore any other stray startup output
        }
    }

    private async Task<Resp> SendAndReadLockedAsync(string verb, string json, TimeSpan timeout, CancellationToken ct)
    {
        if (_pwsh is null) throw new ExchangeException("The Exchange Online host is not running.");
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        try
        {
            await _pwsh.StandardInput.WriteLineAsync((verb + " " + b64).AsMemory(), ct).ConfigureAwait(false);
            await _pwsh.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ExchangeException("The Exchange Online host stopped accepting input. " + DrainStderr(), ex);
        }

        // Read until the framed envelope; tolerate stray lines before BEGIN.
        while (true)
        {
            var line = await ReadLineLockedAsync(timeout, ct).ConfigureAwait(false);
            if (line is null) throw new ExchangeException("The Exchange Online host (pwsh) exited unexpectedly. " + DrainStderr());
            if (line != Begin) continue;
            var payload = await ReadLineLockedAsync(timeout, ct).ConfigureAwait(false);
            await ReadLineLockedAsync(timeout, ct).ConfigureAwait(false); // consume END
            return Parse(payload);
        }
    }

    /// <summary>Reads one stdout line with a timeout; kills the host and throws on timeout so a hung EXO call
    /// can't block the app forever.</summary>
    private async Task<string?> ReadLineLockedAsync(TimeSpan timeout, CancellationToken ct)
    {
        var reader = _pwsh?.StandardOutput ?? throw new ExchangeException("The Exchange Online host is not running.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await Task.Run(() => reader.ReadLine(), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Kill the host either way — an EXO cmdlet can't be interrupted mid-run, so abandon the process
            // (the next operation restarts it). This unblocks the orphaned blocking ReadLine too.
            KillLocked();
            if (ct.IsCancellationRequested) throw;                 // user cancellation → propagate
            throw new ExchangeException("The Exchange Online operation timed out.");
        }
    }

    private string DrainStderr()
    {
        lock (_stderr)
        {
            var s = _stderr.ToString().Trim();
            return s.Length == 0 ? string.Empty : "Details: " + s;
        }
    }

    // Heuristic: did the op fail because the EXO session/token lapsed (worth one reconnect+retry)?
    private static bool IsSessionExpired(string? error)
    {
        if (string.IsNullOrEmpty(error)) return false;
        var m = error;
        return m.Contains("session", StringComparison.OrdinalIgnoreCase)
            || m.Contains("expired", StringComparison.OrdinalIgnoreCase)
            || m.Contains("reconnect", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Connect-ExchangeOnline", StringComparison.OrdinalIgnoreCase)
            || m.Contains("not recognized", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct Resp(bool Ok, string? Error, JsonElement? Data, string? Detail);

    private static Resp Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return new Resp(false, "The Exchange Online host returned no response.", null, null);
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            string? error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            string? detail = root.TryGetProperty("detail", out var dt) && dt.ValueKind == JsonValueKind.String ? dt.GetString() : null;
            JsonElement? data = root.TryGetProperty("data", out var d) && d.ValueKind != JsonValueKind.Null ? d.Clone() : null;
            return new Resp(ok, error, data, detail);
        }
        catch (Exception ex)
        {
            return new Resp(false, "Unparseable response from the Exchange Online host: " + ex.Message, null, null);
        }
    }

    private static MailboxInfo MapMailbox(JsonElement d)
    {
        string S(string p) => d.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        bool B(string p) => d.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;

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
            DeliverToMailboxAndForward = B("DeliverToMailboxAndForward"),
        };
    }

    private static MailboxRecipient MapRecipient(JsonElement d)
    {
        string S(string p) => d.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        var smtp = S("PrimarySmtpAddress");
        return new MailboxRecipient
        {
            Identity = smtp.Length > 0 ? smtp : S("Identity"),
            DisplayName = S("DisplayName"),
            PrimarySmtpAddress = smtp,
            RecipientType = S("RecipientType"),
        };
    }

    private static MailboxDelegate MapDelegate(JsonElement d)
    {
        string S(string p) => d.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        bool B(string p) => d.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;
        return new MailboxDelegate
        {
            Identity = S("Identity"),
            DisplayName = S("DisplayName"),
            FullAccess = B("FullAccess"),
            SendAs = B("SendAs"),
            SendOnBehalf = B("SendOnBehalf"),
        };
    }

    /// <summary>Logs the audience + delegated scopes (scp) + app roles from the EXO access token, to confirm
    /// which Exchange permission the token actually carries. Never logs the token itself.</summary>
    private static void LogTokenClaims(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4) { case 2: payload += "=="; break; case 3: payload += "="; break; }
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            var root = doc.RootElement;
            string Claim(string n) => root.TryGetProperty(n, out var v) ? v.ToString() : "(none)";
            AppLog.Instance.Info($"EXO token claims — aud={Claim("aud")}; scp={Claim("scp")}; roles={Claim("roles")}; appid={Claim("appid")}");
        }
        catch (Exception ex) { AppLog.Instance.Warn("Could not decode EXO token claims: " + ex.Message); }
    }

    private static string ResolvePwsh()
    {
        var known = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        return File.Exists(known) ? known : "pwsh.exe"; // else rely on PATH
    }

    private static string WriteHostScript()
    {
        var dir = Path.Combine(Path.GetTempPath(), "UnifiedDirectoryManager");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "exo-host.ps1");
        File.WriteAllText(path, HostScript, new UTF8Encoding(false));
        return path;
    }

    public void Dispose()
    {
        try { Disconnect(); } catch { /* best effort */ }
        _gate.Dispose();
    }

    // The pwsh host loop. Started via -File; reads command lines from stdin and replies with a framed JSON
    // envelope on stdout. Only __emit writes to stdout, so the framing stays clean (cmdlet output is captured
    // into variables; warnings/info are suppressed or go to stderr).
    private const string HostScript = """
        $ErrorActionPreference = 'Stop'
        try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch {}

        function __emit($obj) {
            [Console]::Out.WriteLine('<<<UDM-BEGIN>>>')
            [Console]::Out.WriteLine(($obj | ConvertTo-Json -Depth 8 -Compress))
            [Console]::Out.WriteLine('<<<UDM-END>>>')
            [Console]::Out.Flush()
        }
        function __arg($b64) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b64)) | ConvertFrom-Json }
        function __delAdd($map, $who, $perm) {
            if ([string]::IsNullOrWhiteSpace([string]$who)) { return }
            $rec = Get-Recipient -Identity $who -ErrorAction SilentlyContinue | Select-Object -First 1
            $key = if ($rec) { [string]$rec.PrimarySmtpAddress } else { [string]$who }
            $name = if ($rec) { [string]$rec.DisplayName } else { [string]$who }
            if (-not $map.ContainsKey($key)) { $map[$key] = [ordered]@{ Identity = $key; DisplayName = $name; FullAccess = $false; SendAs = $false; SendOnBehalf = $false } }
            $map[$key][$perm] = $true
        }

        try {
            Import-Module ExchangeOnlineManagement -ErrorAction Stop
        } catch {
            __emit @{ ok = $false; error = ("The ExchangeOnlineManagement module could not be loaded. Install it, e.g. Install-Module ExchangeOnlineManagement -Scope CurrentUser. Details: " + $_.Exception.Message) }
            exit 1
        }
        [Console]::Out.WriteLine('<<<UDM-READY>>>'); [Console]::Out.Flush()

        while ($true) {
            $line = [Console]::In.ReadLine()
            if ($null -eq $line) { break }
            $sp = $line.IndexOf(' ')
            $verb = if ($sp -lt 0) { $line } else { $line.Substring(0, $sp) }
            $payload = if ($sp -lt 0) { '' } else { $line.Substring($sp + 1) }
            switch ($verb) {
                'QUIT' { try { Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue | Out-Null } catch {}; break }
                'CONNECT' {
                    try {
                        $p = __arg $payload
                        if ($p.upn) {
                            Connect-ExchangeOnline -AccessToken $p.token -UserPrincipalName $p.upn -ShowBanner:$false -WarningAction SilentlyContinue -ErrorAction Stop 6>$null
                        } else {
                            Connect-ExchangeOnline -AccessToken $p.token -Organization $p.org -ShowBanner:$false -WarningAction SilentlyContinue -ErrorAction Stop 6>$null
                        }
                        __emit @{ ok = $true }
                    } catch { __emit @{ ok = $false; error = $_.Exception.Message; detail = ($_ | Out-String) } }
                }
                'OP' {
                    try {
                        $r = __arg $payload
                        switch ($r.op) {
                            'get-mailbox' {
                                $m = Get-Mailbox -Identity $r.identity -ErrorAction SilentlyContinue
                                if ($null -eq $m) { __emit @{ ok = $true; data = $null } }
                                else {
                                    __emit @{ ok = $true; data = @{
                                        DisplayName = [string]$m.DisplayName
                                        PrimarySmtpAddress = [string]$m.PrimarySmtpAddress
                                        RecipientTypeDetails = [string]$m.RecipientTypeDetails
                                        ForwardingAddress = [string]$m.ForwardingAddress
                                        DeliverToMailboxAndForward = ([string]$m.DeliverToMailboxAndForward -eq 'True')
                                        UserPrincipalName = [string]$m.UserPrincipalName
                                    } }
                                }
                            }
                            'convert' { Set-Mailbox -Identity $r.identity -Type $r.type -ErrorAction Stop 6>$null; __emit @{ ok = $true } }
                            'set-forwarding' { Set-Mailbox -Identity $r.identity -ForwardingAddress $r.target -DeliverToMailboxAndForward ([bool]$r.deliver) -ErrorAction Stop 6>$null; __emit @{ ok = $true } }
                            'clear-forwarding' { Set-Mailbox -Identity $r.identity -ForwardingAddress $null -DeliverToMailboxAndForward $false -ErrorAction Stop 6>$null; __emit @{ ok = $true } }
                            'search-recipients' {
                                if ([string]::IsNullOrWhiteSpace([string]$r.text)) {
                                    $rc = Get-Recipient -ResultSize 50 -ErrorAction SilentlyContinue
                                } else {
                                    $esc = ([string]$r.text).Replace("'", "''")
                                    $rc = Get-Recipient -ResultSize 50 -Filter "DisplayName -like '*$esc*' -or PrimarySmtpAddress -like '*$esc*'" -ErrorAction SilentlyContinue
                                }
                                $data = @($rc | ForEach-Object { @{
                                    Identity = [string]$_.PrimarySmtpAddress
                                    DisplayName = [string]$_.DisplayName
                                    PrimarySmtpAddress = [string]$_.PrimarySmtpAddress
                                    RecipientType = [string]$_.RecipientTypeDetails
                                } })
                                __emit @{ ok = $true; data = $data }
                            }
                            'list-delegates' {
                                # NOTE: EXO v3 returns IsInherited/Deny as STRINGS ("False"/"True"), and in PowerShell
                                # -not "False" is $false (any non-empty string is truthy). Compare stringified values
                                # with -ne 'True' (correct whether the property is a bool or a string). AccessRights is a
                                # single flags value (e.g. "FullAccess, ReadPermission"), so match it as a substring.
                                $map = @{}
                                $diag = @()
                                try {
                                    $raw = @(Get-MailboxPermission -Identity $r.identity -ErrorAction Stop)
                                    $fp = @($raw | Where-Object { (([string]$_.AccessRights) -like '*FullAccess*') -and ([string]$_.IsInherited -ne 'True') -and ([string]$_.Deny -ne 'True') -and ([string]$_.User -notlike 'NT AUTHORITY\*') })
                                    $diag += "FullAccess raw=$($raw.Count) matched=$($fp.Count)"
                                    $fp | ForEach-Object { __delAdd $map $_.User 'FullAccess' }
                                } catch { $diag += "MbxPerm ERR: $($_.Exception.Message)" }
                                try {
                                    $rawS = @(Get-RecipientPermission -Identity $r.identity -ErrorAction Stop)
                                    $sp = @($rawS | Where-Object { (([string]$_.AccessRights) -like '*SendAs*') -and ([string]$_.Trustee -notlike 'NT AUTHORITY\*') })
                                    $diag += "SendAs raw=$($rawS.Count) matched=$($sp.Count)"
                                    $sp | ForEach-Object { __delAdd $map $_.Trustee 'SendAs' }
                                } catch { $diag += "RcptPerm ERR: $($_.Exception.Message)" }
                                try {
                                    $mbx = Get-Mailbox -Identity $r.identity -ErrorAction Stop
                                    $sob = @($mbx.GrantSendOnBehalfTo)
                                    $diag += "SendOnBehalf=$($sob.Count)"
                                    foreach ($g in $sob) { __delAdd $map $g 'SendOnBehalf' }
                                } catch { $diag += "Mbx(SoB) ERR: $($_.Exception.Message)" }
                                __emit @{ ok = $true; data = @($map.Values | Sort-Object { $_.DisplayName }); detail = ($diag -join '; ') }
                            }
                            'add-delegate' {
                                # Each permission is applied independently and idempotently: 'already exists' is treated
                                # as success, and one sub-op failing doesn't skip the others (errors are aggregated).
                                $errs = @()
                                if ($r.fullAccess) { try { Add-MailboxPermission -Identity $r.identity -User $r.delegateId -AccessRights FullAccess -InheritanceType All -AutoMapping ([bool]$r.autoMapping) -Confirm:$false -ErrorAction Stop | Out-Null } catch { if ($_.Exception.Message -notmatch 'already') { $errs += 'Full Access: ' + $_.Exception.Message } } }
                                if ($r.sendAs) { try { Add-RecipientPermission -Identity $r.identity -Trustee $r.delegateId -AccessRights SendAs -Confirm:$false -ErrorAction Stop | Out-Null } catch { if ($_.Exception.Message -notmatch 'already') { $errs += 'Send As: ' + $_.Exception.Message } } }
                                if ($r.sendOnBehalf) { try { Set-Mailbox -Identity $r.identity -GrantSendOnBehalfTo @{ Add = $r.delegateId } -ErrorAction Stop 6>$null } catch { if ($_.Exception.Message -notmatch 'already') { $errs += 'Send on Behalf: ' + $_.Exception.Message } } }
                                if ($errs.Count -gt 0) { __emit @{ ok = $false; error = ($errs -join '; ') } } else { __emit @{ ok = $true } }
                            }
                            'remove-delegate' {
                                # Idempotent removal: 'not present' is success; sub-ops are independent + aggregated.
                                $errs = @()
                                if ($r.fullAccess) { try { Remove-MailboxPermission -Identity $r.identity -User $r.delegateId -AccessRights FullAccess -Confirm:$false -ErrorAction Stop | Out-Null } catch { if ($_.Exception.Message -notmatch 'not found|does not exist|doesn''t|isn''t|cannot be found|not have') { $errs += 'Full Access: ' + $_.Exception.Message } } }
                                if ($r.sendAs) { try { Remove-RecipientPermission -Identity $r.identity -Trustee $r.delegateId -AccessRights SendAs -Confirm:$false -ErrorAction Stop | Out-Null } catch { if ($_.Exception.Message -notmatch 'not found|does not exist|doesn''t|isn''t|cannot be found|not have') { $errs += 'Send As: ' + $_.Exception.Message } } }
                                if ($r.sendOnBehalf) { try { Set-Mailbox -Identity $r.identity -GrantSendOnBehalfTo @{ Remove = $r.delegateId } -ErrorAction Stop 6>$null } catch { if ($_.Exception.Message -notmatch 'not found|does not exist|doesn''t|isn''t|cannot be found|not have') { $errs += 'Send on Behalf: ' + $_.Exception.Message } } }
                                if ($errs.Count -gt 0) { __emit @{ ok = $false; error = ($errs -join '; ') } } else { __emit @{ ok = $true } }
                            }
                            default { __emit @{ ok = $false; error = ("Unknown op: " + [string]$r.op) } }
                        }
                    } catch { __emit @{ ok = $false; error = $_.Exception.Message; detail = ($_ | Out-String) } }
                }
                default { __emit @{ ok = $false; error = ("Unknown verb: " + $verb) } }
            }
        }
        """;
}
