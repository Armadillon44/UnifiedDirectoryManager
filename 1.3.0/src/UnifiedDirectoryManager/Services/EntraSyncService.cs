using System.Diagnostics;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Triggers an Entra Connect (Azure AD Connect) delta sync on a remote server by invoking
/// <c>Start-ADSyncSyncCycle -PolicyType Delta</c> through PowerShell remoting (WinRM).
/// The script is fed via stdin (off the command line) and the credential is passed through the child
/// process's environment, not the script text — so the password never appears in PowerShell script-block
/// or transcription logs (Event 4104). Only the server hostname is placed in the script.
/// </summary>
public sealed class EntraSyncService
{
    public sealed record SyncResult(bool Success, string Output);

    private readonly ICredentialStore? _credentials;

    /// <param name="credentials">Optional credential store. When the caller supplies no explicit account,
    /// a sync credential saved for the target server (in Settings ▸ Entra Connect) is used as a fallback,
    /// before defaulting to the current Windows user.</param>
    public EntraSyncService(ICredentialStore? credentials = null) => _credentials = credentials;

    /// <param name="allowSavedFallback">When true (the default) and no explicit account is supplied, a sync
    /// credential saved for the server is used before defaulting to the current Windows user. Callers that let
    /// the operator explicitly choose "current user" pass false to keep that choice honest.</param>
    public async Task<SyncResult> RunDeltaSyncAsync(string server, string? username, string? password,
        bool allowSavedFallback = true, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(server))
            return new SyncResult(false, "Enter the Entra Connect server name.");

        // No explicit account supplied? Fall back to a saved sync credential for this server, if one exists.
        if (allowSavedFallback && string.IsNullOrWhiteSpace(username) && _credentials?.TryLoadSyncCredential(server.Trim()) is { } saved)
        {
            username = saved.Username;
            password = saved.Password;
            AppLog.Instance.Info($"Using the saved Entra Connect sync account '{saved.Username}' for '{server.Trim()}'.");
        }

        var useCurrent = string.IsNullOrWhiteSpace(username);

        // The credential is read from environment variables the child process inherits — NOT interpolated into
        // the script — so it is never captured by script-block/transcription logging. Only the server name (a
        // hostname) goes into the script text, inside a single-quoted literal that Esc() keeps un-escapable.
        var script = useCurrent
            ? $@"Invoke-Command -ComputerName '{Esc(server)}' -ScriptBlock {{ Start-ADSyncSyncCycle -PolicyType Delta }} -ErrorAction Stop | Out-String"
            : $@"$sec = ConvertTo-SecureString $env:UDM_SYNC_PW -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential($env:UDM_SYNC_USER, $sec)
$env:UDM_SYNC_PW = $null
Invoke-Command -ComputerName '{Esc(server)}' -Credential $cred -ScriptBlock {{ Start-ADSyncSyncCycle -PolicyType Delta }} -ErrorAction Stop | Out-String";

        AppLog.Instance.Info($"Entra Connect delta sync requested on '{server}' ({(useCurrent ? "current user" : "supplied credentials")}).");

        try
        {
            var (exitCode, output) = await RunPowerShellAsync(
                script, useCurrent ? null : username, useCurrent ? null : password, cancellationToken);
            var ok = exitCode == 0;
            // Log the actual PowerShell/WinRM output (no secret is ever echoed — the password lives only in the
            // child's environment and is cleared before Invoke-Command) so a failure is diagnosable after the fact.
            var detail = string.IsNullOrWhiteSpace(output) ? "(no output)" : output.Trim().Replace(Environment.NewLine, " ⏎ ");
            if (ok)
                AppLog.Instance.Info($"Entra Connect delta sync succeeded on '{server}' (exit {exitCode}). Output: {detail}");
            else
                AppLog.Instance.Error($"Entra Connect delta sync failed on '{server}' (exit {exitCode}). Output: {detail}");
            return new SyncResult(ok, string.IsNullOrWhiteSpace(output)
                ? (ok ? "Delta sync started." : "Sync command failed (no output).")
                : output.Trim());
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("Entra Connect delta sync error.", ex);
            return new SyncResult(false, "Could not run PowerShell: " + ex.Message);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunPowerShellAsync(
        string script, string? username, string? password, CancellationToken cancellationToken)
    {
        // No -ExecutionPolicy Bypass: execution policy only gates .ps1 files, not the inline `-Command -` stream,
        // so it added risk-signal without doing anything here.
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command -")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Hand the credential to the child via its environment (UseShellExecute=false keeps it private to this
        // process tree, off the command line, and out of any script-block log). The script reads $env:UDM_SYNC_*.
        if (username is not null) psi.Environment["UDM_SYNC_USER"] = username;
        if (password is not null) psi.Environment["UDM_SYNC_PW"] = password;

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // Feed the script over stdin (keeps any password out of the process command line).
        await proc.StandardInput.WriteAsync(script);
        proc.StandardInput.Close();

        // Read both streams concurrently to avoid a pipe-buffer deadlock.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        await proc.WaitForExitAsync(cancellationToken);

        var combined = stdout;
        if (!string.IsNullOrWhiteSpace(stderr))
            combined += (combined.Length > 0 ? Environment.NewLine : string.Empty) + stderr;
        return (proc.ExitCode, combined);
    }

    /// <summary>Escapes a value for safe inclusion inside a single-quoted PowerShell literal.</summary>
    private static string Esc(string? value) => (value ?? string.Empty).Replace("'", "''");
}
