<#
.SYNOPSIS
  Regression tests for the Exchange Online channel: F1 (an inert operation timeout), F15 (app exit blocking
  the UI thread on the gate), F14 (every disconnect ending in a kill) and F6 (a session outliving the admin
  it belongs to).

.DESCRIPTION
  These four share one property: nothing about them is visible from the outside until the day they matter,
  and then the app is hung, or writing to Exchange as the wrong administrator.

  They are exercised through a real child process rather than a mock. The channel's whole job is to own a
  pwsh process over a pipe, and the timeout bug was invisible precisely because the C# read APIs behave
  differently against a pipe than against anything a mock can produce: StreamReader.ReadLine blocks in a
  native read that no CancellationToken can interrupt. A test that does not use a real pipe cannot tell the
  fixed version from the broken one.

  The child here is a plain pwsh that echoes nothing, NOT the app's host script -- standing that up needs
  the ExchangeOnlineManagement module and a tenant. Each child announces itself on stderr before the test
  starts timing anything, so a slow process launch cannot be mistaken for a slow disconnect. Reflection
  reaches the private members, which is the price of testing a class whose entire surface needs
  Microsoft 365 to run.

  Run with:  pwsh -NoProfile -File ./app/build/test-exchange-channel.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dll = Join-Path $repoRoot 'debug\UnifiedDirectoryManager.dll'
$support = Join-Path $repoRoot 'debug\UnifiedDirectoryManager.TestSupport.dll'
if (-not (Test-Path $dll)) { throw "Build first — could not find $dll" }
if (-not (Test-Path $support)) { throw "Build the test-support project first — could not find $support" }
[System.Reflection.Assembly]::LoadFrom($dll) | Out-Null
[System.Reflection.Assembly]::LoadFrom($support) | Out-Null

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

$Exchange = [UnifiedDirectoryManager.Services.ExchangeService]
$NonPublic = [System.Reflection.BindingFlags]'NonPublic,Instance'

function Field([string]$name) { $Exchange.GetField($name, $NonPublic) }
function Method([string]$name) { $Exchange.GetMethod($name, $NonPublic) }

# Reflection against private members breaks silently when a member is renamed, and a suite that cannot find
# what it is testing has to say so rather than quietly pass.
foreach ($f in '_pwsh', '_connected', '_sessionAccount', '_gate') {
    if ($null -eq (Field $f)) { throw "ExchangeService has no field '$f' — has it been refactored?" }
}
foreach ($m in 'ReadLineLockedAsync', 'KillLocked', 'DropSessionIfAccountChangedLocked') {
    if ($null -eq (Method $m)) { throw "ExchangeService has no method '$m' — has it been refactored?" }
}

$pwshExe = (Get-Process -Id $PID).Path
if (-not $pwshExe) { $pwshExe = 'pwsh' }

$script:spawned = @()

# Starts a child holding the pipe open, and does not return until it is actually in its read loop -- pwsh
# takes the better part of a second to start, and a disconnect must not be timed against that.
#
# $say is written to stdout once the loop is running; leave it empty for a host that never speaks, which is
# what a hung EXO cmdlet looks like from the C# side. Readiness is announced on stderr either way, so an
# empty $say really does leave stdout silent. The loop reads stdin so that CLOSING stdin is what ends it,
# exactly like the real host loop's null-line escape.
function Start-Child([string]$say = '') {
    $body = if ($say) { "[Console]::Out.WriteLine('$say'); [Console]::Out.Flush(); " } else { '' }
    $body += "[Console]::Error.WriteLine('READY'); [Console]::Error.Flush(); "
    $body += 'while ($true) { $l = [Console]::In.ReadLine(); if ($null -eq $l) { exit 0 } }'

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $pwshExe
    foreach ($a in '-NoProfile', '-NoLogo', '-NonInteractive', '-Command', $body) { $psi.ArgumentList.Add($a) }
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::new()
    $p.StartInfo = $psi
    [void]$p.Start()
    $script:spawned += $p.Id
    $hello = $p.StandardError.ReadLine()
    if ($hello -ne 'READY') { throw "The test child did not start (stderr said '$hello')." }
    return $p
}

# Builds a channel with a live child already attached, bypassing Configure/Connect (both need a tenant).
function New-AttachedChannel([string]$account, [string]$say = '') {
    $graph = [UnifiedDirectoryManager.TestSupport.InertGraphService]::new()
    $graph.SignedInAccount = $account
    $svc = $Exchange::new($graph)
    $child = Start-Child $say
    (Field '_pwsh').SetValue($svc, $child)
    (Field '_connected').SetValue($svc, $true)
    (Field '_sessionAccount').SetValue($svc, $account)
    return [pscustomobject]@{ Service = $svc; Graph = $graph; Pid = $child.Id }
}

function Test-ProcessGone([int]$processId) {
    for ($i = 0; $i -lt 60; $i++) {
        if (-not (Get-Process -Id $processId -ErrorAction SilentlyContinue)) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

$readLine = Method 'ReadLineLockedAsync'
$NoToken = [System.Threading.CancellationToken]::None

try {

Write-Host "`n== F1: the operation timeout actually fires ==" -ForegroundColor Cyan
# The old code passed a token to Task.Run, which only stops the delegate STARTING. Once ReadLine was blocked
# in the pipe the token did nothing: the task never completed, so the await never returned, so the catch
# that killed the host was unreachable. A hung EXO cmdlet blocked forever WHILE HOLDING THE GATE -- every
# Exchange feature in the app queued behind it, and the Cancel buttons on Exchange operations were dead.
$c = New-AttachedChannel 'admin.a@contoso.com'
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$err = $null
try {
    $task = $readLine.Invoke($c.Service, @([timespan]::FromSeconds(2), $NoToken))
    # Wait well past the budget. If the timeout is inert this returns false and the assertions below fail,
    # rather than the suite hanging the way the app used to.
    if (-not $task.Wait(20000)) { $err = 'the read never completed' }
}
catch { $err = $_.Exception.GetBaseException().Message }
$sw.Stop()

Check 'a silent host times out'            $true ($err -like '*timed out*')
Check 'and does so within the budget'      $true ($sw.Elapsed.TotalSeconds -lt 10)
Check 'and the host process is killed'     $true (Test-ProcessGone $c.Pid)
Check 'and the channel drops the handle'   $null ((Field '_pwsh').GetValue($c.Service))
Check 'and no longer reports connected'    $false $c.Service.IsConnected

Write-Host "`n== F1: a line that arrives is still returned ==" -ForegroundColor Cyan
# The negative control. Racing the read against a delay must not disturb the ordinary path -- a timeout that
# fires correctly is worthless if it also eats successful reads.
$c2 = New-AttachedChannel 'admin.a@contoso.com' 'hello'
$task = $readLine.Invoke($c2.Service, @([timespan]::FromSeconds(30), $NoToken))
Check 'the read completes'              $true    ($task.Wait(20000))
Check 'with its content intact'         'hello'  $task.Result
Check 'and the host is left running'    $false   ($null -eq (Field '_pwsh').GetValue($c2.Service))
$c2.Service.Disconnect()

Write-Host "`n== F14: a disconnect ends politely instead of always killing ==" -ForegroundColor Cyan
# KillLocked wrote QUIT and then waited 1500 ms for an exit that could not happen: the host's QUIT arm used
# `break`, which in PowerShell leaves the SWITCH and not the enclosing while loop, and stdin was never
# closed, so the loop's null-line escape was unreachable too. Every disconnect stalled the full grace period
# and ended in Kill(entireProcessTree) -- which could land while Disconnect-ExchangeOnline was still running
# and leak the server-side session. Closing stdin is what makes the polite exit reachable.
$c3 = New-AttachedChannel 'admin.a@contoso.com'
$sw = [System.Diagnostics.Stopwatch]::StartNew()
(Method 'KillLocked').Invoke($c3.Service, @())
$sw.Stop()
Check 'the child is gone'                  $true (Test-ProcessGone $c3.Pid)
Check 'and it exited well inside 1500 ms'  $true ($sw.Elapsed.TotalMilliseconds -lt 1400)

Write-Host "`n== F14: the host script leaves its read loop on QUIT ==" -ForegroundColor Cyan
# The C# half above only proves stdin now closes. The host's own QUIT arm has to leave the loop as well: a
# QUIT that arrives before stdin closes must not drop the host straight back into ReadLine.
$serviceSrc = Get-Content -Raw (Join-Path $repoRoot 'app\src\UnifiedDirectoryManager\Services\ExchangeService.cs')
$quitArm = [regex]::Match($serviceSrc, "'QUIT' \{[^\r\n]*").Value
Check 'the QUIT arm exists'                $true ($quitArm.Length -gt 0)
Check 'and it exits the host'              $true ($quitArm -match '\bexit\b')
Check 'rather than breaking the switch'    $false ($quitArm -match '\bbreak\b')
# Why that matters, demonstrated rather than asserted about, because it is the kind of thing a later tidy-up
# reverses: break inside a switch leaves the switch, and the loop goes round again.
$rounds = 0
while ($true) { $rounds++; switch ('QUIT') { 'QUIT' { break } }; if ($rounds -ge 3) { break } }
Check 'break in a switch does not end the loop' 3 $rounds

Write-Host "`n== F15: disconnecting never waits on an in-flight operation ==" -ForegroundColor Cyan
# Disconnect() ran _gate.Wait() with no timeout, on the UI thread: App.OnExit -> Dispose() -> Disconnect().
# Close the window during a mailbox listing and shutdown froze a dead window for up to the 180-second list
# budget -- and with the inert timeout above, forever.
$c4 = New-AttachedChannel 'admin.a@contoso.com'
$gate = (Field '_gate').GetValue($c4.Service)
$gate.Wait()   # stands in for an operation holding the channel
try {
    # A real delegate, not a script block: the calling thread is about to block on Wait(), and a script
    # block would need this runspace to run on.
    $action = $Exchange.GetMethod('Disconnect').CreateDelegate([Action], $c4.Service)
    $disconnect = [System.Threading.Tasks.Task]::Run($action)
    Check 'Disconnect returns without the gate' $true ($disconnect.Wait(10000))
    Check 'and the host is stopped anyway'      $true (Test-ProcessGone $c4.Pid)
    Check 'and the handle is dropped'           $null ((Field '_pwsh').GetValue($c4.Service))
}
finally { [void]$gate.Release() }

Write-Host "`n== F15: with the gate free, the polite path is still taken ==" -ForegroundColor Cyan
# The fast path must not become the only path: an ordinary disconnect should take the gate, so the QUIT
# happens with nothing in flight, and must hand it back afterwards.
$c5 = New-AttachedChannel 'admin.a@contoso.com'
$gate5 = (Field '_gate').GetValue($c5.Service)
$c5.Service.Disconnect()
Check 'the gate is released afterwards' 1 $gate5.CurrentCount
Check 'and the host is stopped'         $true (Test-ProcessGone $c5.Pid)

Write-Host "`n== F6: the session dies with the admin it belongs to ==" -ForegroundColor Cyan
# The live pwsh session holds ONE admin's delegated token. Every mailbox and distribution-list write runs
# with that admin's permissions and lands in the audit log under their name. The session was keyed on the
# organization alone, so signing out and back in as a different admin in the same tenant kept the first
# admin's session: Configure() early-returns on an unchanged organization, and nothing else dropped it.
$drop = Method 'DropSessionIfAccountChangedLocked'

$c6 = New-AttachedChannel 'admin.a@contoso.com'
Check 'the same admin keeps the session' $false ($drop.Invoke($c6.Service, @()))
Check 'and the host keeps running'       $true  ($c6.Service.IsConnected)

# Admin B signs in. Same tenant, same organization string, different person.
$c6.Graph.SignedInAccount = 'admin.b@contoso.com'
Check 'a different admin drops it'       $true  ($drop.Invoke($c6.Service, @()))
Check 'the host process is stopped'      $true  (Test-ProcessGone $c6.Pid)
Check 'and the channel is disconnected'  $false ($c6.Service.IsConnected)

# Signing out entirely is the same hazard: the host would go on serving with a token nobody is signed in for.
$c7 = New-AttachedChannel 'admin.a@contoso.com'
$c7.Graph.SignedInAccount = $null
Check 'signing out drops it too'         $true ($drop.Invoke($c7.Service, @()))
Check 'and stops the host'               $true (Test-ProcessGone $c7.Pid)

# Casing is not a change of admin: a UPN comes back in whatever case it was typed.
$c8 = New-AttachedChannel 'admin.a@contoso.com'
$c8.Graph.SignedInAccount = 'Admin.A@Contoso.com'
Check 'casing alone is not a new admin'  $false ($drop.Invoke($c8.Service, @()))
Check 'and the session survives'         $true  ($c8.Service.IsConnected)
$c8.Service.Disconnect()

# With no session there is nothing to drop, and this runs before every operation.
$c9 = New-AttachedChannel 'admin.a@contoso.com'
$c9.Service.Disconnect()
$c9.Graph.SignedInAccount = 'someone.else@contoso.com'
Check 'a dead session drops nothing'     $false ($drop.Invoke($c9.Service, @()))

Write-Host "`n== F6: and the connect path consults it ==" -ForegroundColor Cyan
# The rule is worthless if nothing calls it, and the call site needs a tenant to reach.
$ensure = [regex]::Match($serviceSrc, '(?s)private async Task EnsureConnectedLockedAsync\(CancellationToken ct\).*?\r?\n    \}').Value
Check 'EnsureConnected checks the admin'   $true ($ensure -match 'DropSessionIfAccountChangedLocked\(\)')
Check 'before its already-connected exit'  $true (
    $ensure.IndexOf('DropSessionIfAccountChangedLocked()') -lt $ensure.IndexOf('if (IsConnected) return;'))
# And a new session has to record who owns it, or the check compares against nothing.
Check 'and a new session records its owner' $true ($ensure -match '_sessionAccount = _graph\.SignedInAccount')

}
finally {
    foreach ($processId in ($script:spawned | Select-Object -Unique)) {
        try { Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue } catch { }
    }
}

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
