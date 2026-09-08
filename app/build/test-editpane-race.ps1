<#
.SYNOPSIS
  Behavioural tests for the edit pane's overlapping-load race (F2) — the bug where a Save could write one
  object's values onto another.

.DESCRIPTION
  This is the test that the first attempt at the fix needed and did not have. That attempt guarded the
  wrong half of the race, passed a suite of structural checks, and left the defect fully reachable; only a
  reviewer reasoning through the runtime behaviour caught it.

  The whole thing turns on TIMING, so it uses FakeDirectoryService, which parks a load until the test
  releases it. That is the one capability a real directory cannot offer: it answers when it likes.

  STA because the view models touch WPF collection types.

  Run with:  pwsh -NoProfile -STA -File ./app/build/test-editpane-race.ps1
#>
[CmdletBinding()]
param()

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$debug = Join-Path (Split-Path -Parent $root) 'debug'
foreach ($a in 'UnifiedDirectoryManager.dll', 'UnifiedDirectoryManager.TestSupport.dll') {
    $path = Join-Path $debug $a
    if (-not (Test-Path $path)) { throw "Build first — could not find $path" }
    [System.Reflection.Assembly]::LoadFrom($path) | Out-Null
}

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

$ALICE = 'CN=Alice,OU=Staff,DC=contoso,DC=net'
$BOB   = 'CN=Bob,OU=Staff,DC=contoso,DC=net'
$Fake  = [UnifiedDirectoryManager.TestSupport.FakeDirectoryService]
$User  = [UnifiedDirectoryManager.Models.AdObjectType]::User

function Await($task) {
    try { $task.GetAwaiter().GetResult() | Out-Null; return $false }
    catch { return $true }   # see the note in test-favorites.ps1: the LDAP assembly mismatch in pwsh
}

# Drains queued continuations so an awaited load's remainder actually runs before we assert.
function Pump {
    $frame = New-Object System.Windows.Threading.DispatcherFrame
    [System.Windows.Threading.Dispatcher]::CurrentDispatcher.BeginInvoke(
        [System.Windows.Threading.DispatcherPriority]::ApplicationIdle,
        [System.Action] { $frame.Continue = $false }) | Out-Null
    [System.Windows.Threading.Dispatcher]::PushFrame($frame)
}

function NewPane {
    $fake = New-Object UnifiedDirectoryManager.TestSupport.FakeDirectoryService
    $fake.Objects[$ALICE] = [UnifiedDirectoryManager.Models.AdAttribute[]]@(
        $Fake::Attr('displayName', 'Alice'), $Fake::Attr('description', 'alice-original'))
    $fake.Objects[$BOB] = [UnifiedDirectoryManager.Models.AdAttribute[]]@(
        $Fake::Attr('displayName', 'Bob'), $Fake::Attr('description', 'bob-original'))
    $errors = New-Object System.Collections.Generic.List[string]
    $vm = [UnifiedDirectoryManager.ViewModels.EditPaneViewModel]::new(
        $fake,
        [UnifiedDirectoryManager.Services.IDialogService]$null,
        [Action[string]] { param($m) $errors.Add($m) },
        (New-Object UnifiedDirectoryManager.TestSupport.InertGraphService),
        (New-Object UnifiedDirectoryManager.TestSupport.InertExchangeService))
    return @{ Vm = $vm; Fake = $fake; Errors = $errors }
}
# _dn is what every write targets, so it is what these tests inspect.
$dnField = [UnifiedDirectoryManager.ViewModels.EditPaneViewModel].GetField('_dn', [System.Reflection.BindingFlags]'NonPublic,Instance')
function Dn($vm) { $dnField.GetValue($vm) }

Write-Host "`n== a load in flight does not claim the pane ==" -ForegroundColor Cyan
# The defect: _dn was assigned synchronously at the top of LoadAsync, so from the instant a row was clicked
# every write targeted the NEW object while the pane still displayed — and could still save — the old one.
$t = NewPane
$t.Fake.HoldLoads = $true
$loadAlice = $t.Vm.LoadAsync($ALICE, $User)
Pump
Check 'nothing is targeted while loading' $null (Dn $t.Vm)
Check 'and the pane offers no object'     $false $t.Vm.HasObject
[void]$t.Fake.ReleaseLoad($ALICE); [void](Await $loadAlice); Pump
Check 'once loaded it targets Alice'      $ALICE (Dn $t.Vm)
Check 'and the pane has an object'        $true  $t.Vm.HasObject

Write-Host "`n== the wrong-object write ==" -ForegroundColor Cyan
# Load Alice, start loading Bob, and check that nothing can be written to Bob's DN using Alice's state
# while Bob is still loading. This is the exact sequence from the finding.
$t = NewPane
$t.Fake.HoldLoads = $false
[void](Await ($t.Vm.LoadAsync($ALICE, $User))); Pump
Check 'Alice is loaded'                 $ALICE (Dn $t.Vm)

$t.Fake.HoldLoads = $true
$loadBob = $t.Vm.LoadAsync($BOB, $User)
Pump
# THE assertion. Before the fix this was Bob's DN — with Alice still on screen and Save live.
Check 'starting Bob does not retarget'  $null  (Dn $t.Vm)
Check 'and the pane is not offering Alice to save' $false $t.Vm.HasObject
$t.Vm.SaveCommand.Execute($null); Pump
Check 'so a Save mid-load writes nothing' 0 $t.Fake.Writes.Count

[void]$t.Fake.ReleaseLoad($BOB); [void](Await $loadBob); Pump
Check 'after loading, Bob is the target' $BOB (Dn $t.Vm)

Write-Host "`n== a superseded load never repaints ==" -ForegroundColor Cyan
# Alice's slow load resolving AFTER Bob's must not repopulate the pane Bob now owns.
$t = NewPane
$t.Fake.HoldLoads = $true
$loadAlice = $t.Vm.LoadAsync($ALICE, $User)
Pump
$loadBob = $t.Vm.LoadAsync($BOB, $User)
Pump
[void]$t.Fake.ReleaseLoad($BOB);   [void](Await $loadBob);   Pump
Check 'Bob owns the pane'            $BOB (Dn $t.Vm)
Check 'and is shown'                 'Bob' $t.Vm.Title
[void]$t.Fake.ReleaseLoad($ALICE); [void](Await $loadAlice); Pump
Check 'Alice arriving late does not steal it' $BOB  (Dn $t.Vm)
Check 'nor repaint the title'                 'Bob' $t.Vm.Title
Check 'and the pane is still usable'          $true $t.Vm.HasObject

Write-Host "`n== clearing the pane cancels what is in flight ==" -ForegroundColor Cyan
# Clear() wiped the pane but left the load running, so it repopulated the pane it had just cleared —
# showing a deselected (or deleted) object whose buttons all silently did nothing.
$t = NewPane
$t.Fake.HoldLoads = $true
$loadAlice = $t.Vm.LoadAsync($ALICE, $User)
Pump
$t.Vm.Clear()
[void]$t.Fake.ReleaseLoad($ALICE); [void](Await $loadAlice); Pump
Check 'the cleared pane stays cleared' $false $t.Vm.HasObject
Check 'and targets nothing'            $null  (Dn $t.Vm)

Write-Host "`n== a superseded failure is not reported against the wrong object ==" -ForegroundColor Cyan
# A stale load that throws must not clear the pane the current load populated, nor raise an error about an
# object the operator has already moved away from.
$t = NewPane
$t.Fake.HoldLoads = $true
$loadAlice = $t.Vm.LoadAsync($ALICE, $User)
Pump
$loadBob = $t.Vm.LoadAsync($BOB, $User)
Pump
[void]$t.Fake.ReleaseLoad($BOB); [void](Await $loadBob); Pump
$t.Errors.Clear()
[void]$t.Fake.FailLoad($ALICE); [void](Await $loadAlice); Pump
Check 'the stale failure is not surfaced' 0     $t.Errors.Count
Check 'and Bob survives it'               $BOB  (Dn $t.Vm)
Check 'with the pane intact'              $true $t.Vm.HasObject

Write-Host "`n== a real failure IS surfaced ==" -ForegroundColor Cyan
# The negative control: silencing superseded failures must not silence current ones.
$t = NewPane
$t.Fake.HoldLoads = $true
$loadAlice = $t.Vm.LoadAsync($ALICE, $User)
Pump
[void]$t.Fake.FailLoad($ALICE)
$threw = Await $loadAlice
Pump
Check 'the current load reports its failure' $true ($threw -or $t.Errors.Count -ge 1)
Check 'and the pane offers nothing'          $false $t.Vm.HasObject

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
