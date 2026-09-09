<#
.SYNOPSIS
  Regression tests for F22: the Entra Connect delta sync had no timeout, and cancelling it left the helper
  process running.

.DESCRIPTION
  Two defects, one symptom. RunPowerShellAsync awaited a child powershell.exe with no time budget at all, so
  a WinRM call that never answered stalled New User, Copy User and Bulk Create for as long as the app ran --
  and the only escape was killing the app. And CloudProvisioningService.RunDeltaSyncAsync had no
  CancellationToken parameter, so Bulk Create's own Cancel could not reach the sync even though the token
  existed on both sides of it.

  A correction to the finding, which said EntraSyncService.RunDeltaSyncAsync takes no token: it always has,
  at ac09e77 too. The layer that dropped it was CloudProvisioningService.

  Even with the token threaded, cancelling only abandoned the awaits. The child kept running, holding its
  WinRM session, and `using var proc` then disposed the wrapper around a live process. So the fix also kills
  the helper -- and that is what these tests mostly check, because it is the part that leaks.

  No network here. The budget and the kill are exercised through the private RunPowerShellAsync with a
  script that just sleeps, which is exactly the shape of a WinRM call that never answers.

  Run with:  pwsh -NoProfile -File ./app/build/test-entra-sync.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dll = Join-Path $repoRoot 'debug\UnifiedDirectoryManager.dll'
if (-not (Test-Path $dll)) { throw "Build first — could not find $dll" }
[System.Reflection.Assembly]::LoadFrom($dll) | Out-Null

$pass = 0; $fail = 0
function Check([string]$name, $expected, $actual) {
    if ($expected -eq $actual) { $script:pass++; Write-Host "  PASS  $name" -ForegroundColor Green }
    else {
        $script:fail++
        Write-Host "  FAIL  $name" -ForegroundColor Red
        Write-Host "          expected: [$expected]"
        Write-Host "          actual:   [$actual]"
    }
}

$Sync = [UnifiedDirectoryManager.Services.EntraSyncService]
$Provisioning = [UnifiedDirectoryManager.Services.CloudProvisioningService]
$NonPublicStatic = [System.Reflection.BindingFlags]'NonPublic,Static'

$runPs = $Sync.GetMethod('RunPowerShellAsync', $NonPublicStatic)
if ($null -eq $runPs) { throw 'EntraSyncService has no RunPowerShellAsync — has it been refactored?' }

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("udm-sync-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $scratch | Out-Null

# A script that announces its own process id and then hangs. That is what a WinRM call to a host which never
# answers looks like from this side, and the pid file is how the test can prove the child was killed.
function Hang-Script([string]$pidFile) {
    return "`$PID | Set-Content -LiteralPath '$pidFile'`nStart-Sleep -Seconds 120"
}

function Wait-ForPid([string]$pidFile) {
    for ($i = 0; $i -lt 200; $i++) {
        if (Test-Path $pidFile) {
            $raw = (Get-Content -LiteralPath $pidFile -Raw).Trim()
            if ($raw) { return [int]$raw }
        }
        Start-Sleep -Milliseconds 50
    }
    throw "The helper process never wrote its pid to $pidFile"
}

# The budget under test is seconds; anything approaching this means the call is not coming back, which is
# the regression itself. Kept well under the CI job timeout so the failure is reported rather than reaped.
$GiveUpMs = 45000

# Waits for a reflected Task and returns the exception it ended with, the string 'never-returned' if it did
# not end, or $null if it succeeded. Never throws, so one regression cannot abort the whole suite.
function Wait-Outcome($task) {
    try { if ($task.Wait($GiveUpMs)) { return $null } else { return 'never-returned' } }
    catch { return $_.Exception.GetBaseException() }
}

function TypeName($outcome) {
    if ($null -eq $outcome) { return '(completed)' }
    if ($outcome -is [string]) { return $outcome }
    return $outcome.GetType().FullName
}

function Test-Gone([int]$processId) {
    for ($i = 0; $i -lt 100; $i++) {
        if (-not (Get-Process -Id $processId -ErrorAction SilentlyContinue)) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

$strays = @()

try {

Write-Host "`n== a sync that never answers is abandoned, not waited on forever ==" -ForegroundColor Cyan
$pidFile = Join-Path $scratch 'timeout.pid'
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$err = $null
$task = $runPs.Invoke($null, @(
    (Hang-Script $pidFile), $null, $null,
    [timespan]::FromSeconds(3), [System.Threading.CancellationToken]::None))
$childPid = Wait-ForPid $pidFile
$strays += $childPid
$err = Wait-Outcome $task
$sw.Stop()

Check 'the call ends'                       $true ($err -isnot [string])
Check 'as a timeout'                        'System.TimeoutException' (TypeName $err)
Check 'naming the budget'                   $true ($err -is [System.TimeoutException] -and $err.Message -like '*minute(s)*')
Check 'well before the script would finish' $true ($sw.Elapsed.TotalSeconds -lt 30)
# The part that leaked: the helper owns a WinRM session, and abandoning the awaits left it running.
Check 'and the helper process is killed'    $true (Test-Gone $childPid)

Write-Host "`n== cancelling does the same, but says it was cancelled ==" -ForegroundColor Cyan
# Bulk Create's Cancel arrives this way. It must be told apart from the budget expiring, because the caller
# reports them differently.
$pidFile = Join-Path $scratch 'cancel.pid'
$cts = [System.Threading.CancellationTokenSource]::new()
$task = $runPs.Invoke($null, @(
    (Hang-Script $pidFile), $null, $null,
    [timespan]::FromMinutes(10), $cts.Token))
$childPid = Wait-ForPid $pidFile
$strays += $childPid
$cts.Cancel()
$err = Wait-Outcome $task

Check 'the call ends'                    $true ($err -isnot [string])
Check 'as cancellation, not a timeout'   $true ($err -is [System.OperationCanceledException])
Check 'and the helper is killed too'     $true (Test-Gone $childPid)

Write-Host "`n== a script that finishes is untouched ==" -ForegroundColor Cyan
# The negative control. A budget that also breaks the ordinary path is not a fix.
$task = $runPs.Invoke($null, @(
    "Write-Output 'sync started'", $null, $null,
    [timespan]::FromMinutes(5), [System.Threading.CancellationToken]::None))
Check 'it completes'            $true ($task.Wait(60000))
$result = $task.Result
Check 'with a success exit code' 0 $result.Item1
Check 'and its output'           $true ($result.Item2 -like '*sync started*')

Write-Host "`n== the outcome an operator is shown ==" -ForegroundColor Cyan
# Start-ADSyncSyncCycle QUEUES a cycle and returns, so by the time the helper is killed the sync may well be
# running on the server. Reporting a flat failure would send the operator to start a second one. This is the
# same rule the cancelled-scenario fix (F9) established: say what is actually known.
$svc = $Sync::new($null)
$blank = $svc.RunDeltaSyncAsync('', $null, $null, $true, [System.Threading.CancellationToken]::None, $null).GetAwaiter().GetResult()
Check 'a blank server is refused up front' $false $blank.Success
Check 'and says what to enter'             $true ($blank.Output -like '*Entra Connect server name*')

$src = Get-Content -Raw (Join-Path $repoRoot 'app\src\UnifiedDirectoryManager\Services\EntraSyncService.cs')
$run = [regex]::Match($src, '(?s)public async Task<SyncResult> RunDeltaSyncAsync.*?\r?\n    \}').Value
Check 'cancellation is handled separately'  $true ($run -match 'catch \(OperationCanceledException\)')
Check 'and a timeout separately from that'  $true ($run -match 'catch \(TimeoutException')
# Both must say the sync might already be running, or the operator starts a second one.
$cancelArm = [regex]::Match($run, '(?s)catch \(OperationCanceledException\).*?\n        \}').Value
$timeoutArm = [regex]::Match($run, '(?s)catch \(TimeoutException.*?\n        \}').Value
Check 'the cancel message does not claim failure' $true ($cancelArm -match 'may already have started')
Check 'nor does the timeout message'              $true ($timeoutArm -match 'may already have started')

Write-Host "`n== Cancel actually reaches the sync now ==" -ForegroundColor Cyan
# The token existed on EntraSyncService all along -- the finding was wrong about that. What dropped it was
# CloudProvisioningService, which sat between Bulk Create's ct and the call that needed it.
$provisionRun = $Provisioning.GetMethod('RunDeltaSyncAsync')
if ($null -eq $provisionRun) { throw 'CloudProvisioningService has no RunDeltaSyncAsync — has it been refactored?' }
$params = @($provisionRun.GetParameters() | ForEach-Object { $_.ParameterType.Name })
Check 'the wrapper takes a token' $true ($params -contains 'CancellationToken')

# And passes it on rather than accepting it and ignoring it.
$provSrc = Get-Content -Raw (Join-Path $repoRoot 'app\src\UnifiedDirectoryManager\Services\CloudProvisioningService.cs')
$provRun = [regex]::Match($provSrc, '(?s)public async Task<EntraSyncService\.SyncResult> RunDeltaSyncAsync.*?\r?\n    \}').Value
Check 'and forwards it to the sync'  $true ($provRun -match 'cancellationToken: cancellationToken')

# Bulk Create is the caller whose Cancel button this restores.
$bulkSrc = Get-Content -Raw (Join-Path $repoRoot 'app\src\UnifiedDirectoryManager\Services\BulkUserCreator.cs')
Check 'Bulk Create hands over its token' $true ($bulkSrc -match '_settings, Report, ct\)')

Write-Host "`n== the budget exists at all (mutation check) ==" -ForegroundColor Cyan
$runPsSrc = [regex]::Match($src, '(?s)private static async Task<\(int ExitCode, string Output\)> RunPowerShellAsync.*?\r?\n    \}').Value
Check 'RunPowerShellAsync was found'  $true ($runPsSrc.Length -gt 0)
Check 'it bounds the call'            $true ($runPsSrc -match 'CancelAfter\(timeout\)')
Check 'and kills the child'           $true ($runPsSrc -match 'TryKill\(proc')
$kill = [regex]::Match($src, '(?s)private static void TryKill\(Process proc, string reason\).*?\r?\n    \}').Value
Check 'taking the process tree with it' $true ($kill -match 'entireProcessTree: true')

}
finally {
    foreach ($processId in ($strays | Select-Object -Unique)) {
        try { Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue } catch { }
    }
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
