<#
.SYNOPSIS
  Behavioural tests for the edit pane's busy indicator: the moving bar and step message shown while the pane
  reads or writes, the pane disabled meanwhile, and "Loading cloud groups…" on Member Of.

.DESCRIPTION
  Reported as "while removing cloud groups, it's like the app hangs". The pane always KNEW it was busy (IsBusy
  was set around every write); nothing on screen was bound to it. So half of this suite renders the real
  EditPaneView, with the app's own resources, and looks at what an operator would see. A check that the XAML
  merely mentions IsBusy would have passed before the fix.

  The other half drives the view model: the step messages a save goes through, and that leaving the object
  while a save is still writing waits for it instead of offering to save the same changes a second time.

  STA because the view models touch WPF collection types and the view is real.

  Run with:  pwsh -NoProfile -STA -File ./app/build/test-busy-indicator.ps1
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

# In the app, code after an await resumes on the UI thread. pwsh's thread has no synchronization context, so
# without this a held read, once released, resumes on a pool thread. The view model then runs off the UI
# thread, and the PowerShell handlers this suite hangs on it throw there for want of a runspace. The first
# draft of this suite passed a check over a save that had in fact faulted that way.
[System.Threading.SynchronizationContext]::SetSynchronizationContext(
    [System.Windows.Threading.DispatcherSynchronizationContext]::new(
        [System.Windows.Threading.Dispatcher]::CurrentDispatcher))

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
$SALES = 'CN=Sales,OU=Groups,DC=contoso,DC=net'
$STAFF = 'CN=All Staff,OU=Groups,DC=contoso,DC=net'
$MKTG  = 'CN=Marketing,OU=Groups,DC=contoso,DC=net'
$Fake  = [UnifiedDirectoryManager.TestSupport.FakeDirectoryService]
$User  = [UnifiedDirectoryManager.Models.AdObjectType]::User
$Chan  = [UnifiedDirectoryManager.Models.GroupChannel]
$Vis   = [System.Windows.Visibility]

# Drains queued continuations so an awaited step's remainder actually runs before we assert.
function Pump {
    $frame = New-Object System.Windows.Threading.DispatcherFrame
    [System.Windows.Threading.Dispatcher]::CurrentDispatcher.BeginInvoke(
        [System.Windows.Threading.DispatcherPriority]::ApplicationIdle,
        [System.Action] { $frame.Continue = $false }) | Out-Null
    [System.Windows.Threading.Dispatcher]::PushFrame($frame)
}
# Lets real time pass with the dispatcher running, so animations tick.
function PumpFor([int]$ms) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $ms) { Pump; Start-Sleep -Milliseconds 15 }
}
# A broken build of the code under test must FAIL a named check, not crash the suite before the tally: a crash
# reads as "inconclusive" to a mutation run rather than as the guard doing its job.
function Safe([scriptblock]$s) { try { & $s } catch { "<error: $($_.Exception.Message)>" } }
# Never blocks on a task that is still running: its remainder needs this same thread's dispatcher, so
# GetResult() would wait forever. A mutation that left a leave or save hanging froze this suite for an hour
# that way instead of failing a check.
function Run($task) {
    $n = 0; while (-not $task.IsCompleted -and $n -lt 200) { Pump; $n++ }
    if (-not $task.IsCompleted) { return '<still running>' }
    try { return $task.GetAwaiter().GetResult() } catch { return "<threw: $($_.Exception.Message)>" }
}
function List([object[]]$items) { $l = New-Object System.Collections.ArrayList; foreach ($i in $items) { [void]$l.Add($i) }; return ,$l }
function Multi([string]$ldap, [string[]]$dns, [string[]]$names) {
    $a = $Fake::Attr($ldap, $dns[0])
    $a.DisplayValues[0] = $names[0]
    for ($i = 1; $i -lt $dns.Count; $i++) { $a.RawValues.Add($dns[$i]); $a.DisplayValues.Add($names[$i]) }
    return $a
}
function GroupPick([object]$channel, [string]$name, [string]$dn, [string]$cloudId, [string]$smtp) {
    # [string] parameters turn $null into ""; the record wants real nulls where a value is absent.
    [UnifiedDirectoryManager.Models.GroupRef]::new($channel, $name,
        $(if ($dn) { $dn } else { $null }), $(if ($cloudId) { $cloudId } else { $null }),
        $(if ($smtp) { $smtp } else { $null }), '')
}
function Field($vm, [string]$ldap) {
    foreach ($c in $vm.General, $vm.Account, $vm.Address, $vm.Organization, $vm.Email) {
        foreach ($f in $c) { if ($f.LdapName -eq $ldap) { return $f } }
    }
}
function Row($vm, [string]$name) { $vm.MemberOf | Where-Object { $_.Name -eq $name } | Select-Object -First 1 }

# Alice: two on-prem groups, one direct report, and a UPN so the cloud and Exchange paths have an identity.
# $graph: an InertGraphService (signed out) unless a test needs the cloud.
function NewPane($graph) {
    $fake = New-Object UnifiedDirectoryManager.TestSupport.FakeDirectoryService
    $fake.Objects[$ALICE] = [UnifiedDirectoryManager.Models.AdAttribute[]]@(
        $Fake::Attr('displayName', 'Alice'), $Fake::Attr('description', 'alice-original'),
        $Fake::Attr('userPrincipalName', 'alice@contoso.net'),
        (Multi 'memberOf' @($SALES, $STAFF) @('Sales', 'All Staff')),
        (Multi 'directReports' @($BOB) @('Bob')))
    if (-not $graph) { $graph = New-Object UnifiedDirectoryManager.TestSupport.InertGraphService }
    $dlg = New-Object UnifiedDirectoryManager.TestSupport.InertDialogService
    $dlg.RecordAlerts = $true
    $errors = New-Object System.Collections.Generic.List[string]
    $vm = [UnifiedDirectoryManager.ViewModels.EditPaneViewModel]::new(
        $fake, $dlg, [Action[string]] ({ param($m) $errors.Add($m) }.GetNewClosure()),
        $graph, (New-Object UnifiedDirectoryManager.TestSupport.InertExchangeService))
    # Every value BusyMessage takes, in order: what the operator would have read under the bar.
    $said = New-Object System.Collections.Generic.List[string]
    $vm.add_PropertyChanged({ param($s, $e) if ($e.PropertyName -eq 'BusyMessage') { $said.Add($s.BusyMessage) } }.GetNewClosure())
    return @{ Vm = $vm; Fake = $fake; Dlg = $dlg; Errors = $errors; Said = $said }
}
function SignedInGraph {
    $g = New-Object UnifiedDirectoryManager.TestSupport.HoldableGraphService
    $g.UserInfo = [UnifiedDirectoryManager.Models.CloudUserInfo]::new('alice-entra-id', 'Alice', 'alice@contoso.net',
        $true, $true, 'Member', 'US', $null,
        [UnifiedDirectoryManager.Models.CloudLicense[]]@(), [UnifiedDirectoryManager.Models.CloudGroup[]]@())
    return $g
}
# The counted steps of a save, in order: the messages that say "n of N".
function Counted($t) { @($t.Said | Where-Object { $_ -match '\(\d+ of \d+\)' }) -join ' | ' }

Write-Host "`n--- a save says what it is doing ---"
$t = NewPane
[void](Run ($t.Vm.LoadAsync($ALICE, $User)))
(Field $t.Vm 'description').EditText = 'edited'
$t.Fake.HoldWrites = $true
$save = $t.Vm.SaveCommand.ExecuteAsync($null); Pump
Check 'while the property write is out, the pane is busy'  $true $t.Vm.IsBusy
Check '  and says so, with no count for a single change'   'Saving properties…' $t.Vm.BusyMessage
[void]$t.Fake.ReleaseWrite()
Check 'the held save then completes, reporting success'  $true (Run $save)
Check '  with nothing reported'                            0 $t.Errors.Count
Check 'once written, the pane is not busy'                 $false $t.Vm.IsBusy

# A refused property write is reported, and must still let go of the pane.
$t = NewPane
[void](Run ($t.Vm.LoadAsync($ALICE, $User)))
(Field $t.Vm 'description').EditText = 'edited'
[void]$t.Fake.FailWritesFor.Add($ALICE)
[void](Run ($t.Vm.SaveCommand.ExecuteAsync($null)))
Check 'a refused save is reported'                         1 $t.Errors.Count
Check '  and leaves the pane usable'                       $false $t.Vm.IsBusy

Write-Host "`n--- each change of a save, counted ---"
$t = NewPane
[void](Run ($t.Vm.LoadAsync($ALICE, $User)))
(Field $t.Vm 'description').EditText = 'edited'
$t.Dlg.PickedGroups = [UnifiedDirectoryManager.Models.GroupRef[]]@((GroupPick $Chan::OnPremAd 'Marketing' $MKTG))
$t.Vm.AddToGroupsCommand.Execute($null)
$t.Vm.RemoveFromGroupCommand.Execute((List @(Row $t.Vm 'Sales')))
$t.Vm.RemoveDirectReportsCommand.Execute((List @($t.Vm.DirectReports[0])))
$t.Said.Clear()
[void](Run ($t.Vm.SaveCommand.ExecuteAsync($null)))
Check 'four changes, named and counted in the order written' (
    'Saving properties… (1 of 4) | Adding to group Marketing… (2 of 4) | ' +
    'Removing from group Sales… (3 of 4) | Removing direct report Bob… (4 of 4)') (Counted $t)
Check '  then the reload says it is loading'               'Loading…' $(if ($t.Said.Count) { $t.Said[$t.Said.Count - 1] })
Check '  and every change was written'                     4 $t.Fake.Writes.Count

Write-Host "`n--- the cloud and Exchange steps (the slow ones) ---"
$g = SignedInGraph
$t = NewPane $g
[void](Run ($t.Vm.LoadAsync($ALICE, $User)))
$t.Dlg.PickedGroups = [UnifiedDirectoryManager.Models.GroupRef[]]@(
    (GroupPick $Chan::EntraGraph 'Sales-Team' $null 'cloud-group-1'),
    (GroupPick $Chan::ExchangeOnline 'All-Staff-DL' $null 'dl-1' 'allstaff@contoso.net'))
$t.Vm.AddToGroupsCommand.Execute($null)
$t.Vm.RemoveFromGroupCommand.Execute((List @(Row $t.Vm 'Sales')))
$t.Said.Clear()
[void](Run ($t.Vm.SaveCommand.ExecuteAsync($null)))
Check 'on-prem, then cloud, then Exchange, counted across all three' (
    'Removing from group Sales… (1 of 3) | Adding to cloud group Sales-Team… (2 of 3) | ' +
    'Adding to Exchange group All-Staff-DL… (3 of 3)') (Counted $t)
Check '  the Entra lookup before the cloud write is said too' $true ($t.Said -contains 'Looking up “Alice” in Entra ID…')
Check '  and the cloud write happened'                     1 $g.GroupWrites.Count

# No Entra twin: the cloud changes are skipped, and what follows must still show its real place in the count.
$g = SignedInGraph; $g.UserInfo = $null
$t = NewPane $g
[void](Run ($t.Vm.LoadAsync($ALICE, $User)))
$t.Dlg.PickedGroups = [UnifiedDirectoryManager.Models.GroupRef[]]@(
    (GroupPick $Chan::EntraGraph 'Cloud-A' $null 'cloud-a'), (GroupPick $Chan::EntraGraph 'Cloud-B' $null 'cloud-b'))
$t.Vm.AddToGroupsCommand.Execute($null)
$t.Vm.RemoveDirectReportsCommand.Execute((List @($t.Vm.DirectReports[0])))
$t.Said.Clear()
[void](Run ($t.Vm.SaveCommand.ExecuteAsync($null)))
Check 'skipped cloud changes still count'                  'Removing direct report Bob… (3 of 3)' (Counted $t)
Check '  and the skip is reported'                         1 @($t.Errors | Where-Object { $_ -match 'NOT made' }).Count

Write-Host "`n--- the immediate actions say what they are doing ---"
$t = NewPane
[void](Run ($t.Vm.LoadAsync($ALICE, $User)))
$t.Said.Clear()
[void](Run ($t.Vm.UnlockCommand.ExecuteAsync($null)))
Check 'Unlock'                                             'Unlocking the account…' $(if ($t.Said.Count) { $t.Said[0] })
Check '  then refreshes'                                   $true ($t.Said -contains 'Refreshing…')
$t.Said.Clear()
[void](Run ($t.Vm.EnableCommand.ExecuteAsync($null)))
Check 'Enable'                                             'Enabling the account…' $(if ($t.Said.Count) { $t.Said[0] })
$t.Said.Clear()
[void](Run ($t.Vm.DisableCommand.ExecuteAsync($null)))
Check 'Disable'                                            'Disabling the account…' $(if ($t.Said.Count) { $t.Said[0] })
$t.Said.Clear()
$t.Dlg.PickedContainer = 'OU=New Hires\, East,DC=contoso,DC=net'
[void](Run ($t.Vm.MoveCommand.ExecuteAsync($null)))
Check 'Move names the OU, unescaped'                       'Moving to New Hires, East…' $(if ($t.Said.Count) { $t.Said[0] })
Check '  and the pane is free afterwards'                  $false $t.Vm.IsBusy

Write-Host "`n--- a load that does not finish must not leave the pane busy ---"
# Both of these bump the load token, and a load's own finally lets go of IsBusy only while it is current.
$t = NewPane
$t.Fake.HoldLoads = $true
$load = $t.Vm.LoadAsync($ALICE, $User); Pump
Check 'loading'                                            $true $t.Vm.IsBusy
[void]$t.Fake.FailLoad($ALICE); [void](Run $load)
Check 'a failed load is reported'                          1 $t.Errors.Count
Check '  and lets go of the pane'                          $false $t.Vm.IsBusy

$t = NewPane
$t.Fake.HoldLoads = $true
$load = $t.Vm.LoadAsync($ALICE, $User); Pump
$t.Vm.Clear()   # the selection emptied mid-load
Check 'a load cut short by Clear lets go of the pane'      $false $t.Vm.IsBusy
[void]$t.Fake.ReleaseLoad($ALICE); [void](Run $load)
Check '  and stays let go when the abandoned read answers' $false $t.Vm.IsBusy
Check '  without filling the pane'                         $false $t.Vm.HasObject

Write-Host "`n--- leaving while a save is still writing ---"
# The rows stay marked pending until the save's reload. Asking "save your changes?" at this point would list
# changes already on their way, and Save would write them twice.
$t = NewPane
[void](Run ($t.Vm.LoadAsync($ALICE, $User)))
(Field $t.Vm 'description').EditText = 'edited'
$t.Fake.HoldWrites = $true
$save = $t.Vm.SaveCommand.ExecuteAsync($null); Pump
$t.Dlg.UnsavedAnswer = [UnifiedDirectoryManager.Services.UnsavedChoice]::Save
$leave = $t.Vm.ConfirmLeaveAsync(); Pump
Check 'leaving mid-save waits for the save'                $false $leave.IsCompleted
Check '  without asking about changes being written'       0 $t.Dlg.UnsavedPrompts.Count
[void]$t.Fake.ReleaseWrite()
Check 'once it is written, leaving goes ahead'             $true (Run $leave)
Check '  still without asking'                             0 $t.Dlg.UnsavedPrompts.Count
Check '  and the change was written once, not twice'       1 $t.Fake.Writes.Count
Check '  by a save that reported success'                 $true (Run $save)
Check '  with nothing reported'                            0 $t.Errors.Count

# The wait must not swallow the prompt when there IS something unsaved and no save running.
$t = NewPane
[void](Run ($t.Vm.LoadAsync($ALICE, $User)))
(Field $t.Vm 'description').EditText = 'edited'
$t.Dlg.UnsavedAnswer = [UnifiedDirectoryManager.Services.UnsavedChoice]::Cancel
Check 'with no save running, unsaved edits still ask'      $false (Run ($t.Vm.ConfirmLeaveAsync()))
Check '  once'                                             1 $t.Dlg.UnsavedPrompts.Count

# A failed save leaves its changes unsaved, so the wait is followed by the prompt.
$t = NewPane
[void](Run ($t.Vm.LoadAsync($ALICE, $User)))
(Field $t.Vm 'description').EditText = 'edited'
[void]$t.Fake.FailWritesFor.Add($ALICE)
$t.Fake.HoldWrites = $true
$save = $t.Vm.SaveCommand.ExecuteAsync($null); Pump
$t.Dlg.UnsavedAnswer = [UnifiedDirectoryManager.Services.UnsavedChoice]::Cancel
$leave = $t.Vm.ConfirmLeaveAsync(); Pump
[void]$t.Fake.ReleaseWrite()
Check 'after a FAILED save, leaving asks'                  $false (Run $leave)
Check '  once the save is over'                            1 $t.Dlg.UnsavedPrompts.Count
[void](Run $save)

Write-Host "`n--- Member Of says when cloud groups are still coming ---"
$g = SignedInGraph; $g.HoldGroups = $true
$t = NewPane $g
[void](Run ($t.Vm.LoadAsync($ALICE, $User)))
Check 'after the pane loads, the cloud read is still out'  1 $g.PendingGroups
Check '  and Member Of says so'                            $true $t.Vm.IsLoadingCloudGroups
[void]$g.ReleaseGroups(); Pump
Check 'once it answers, it stops saying so'                $false $t.Vm.IsLoadingCloudGroups

# A reload starts a second read. The first one finishing must not report the second one done.
[void](Run ($t.Vm.ReloadCommand.ExecuteAsync($null)))
[void](Run ($t.Vm.ReloadCommand.ExecuteAsync($null)))
Check 'two reloads, two reads out'                         2 $g.PendingGroups
[void]$g.ReleaseGroups(); Pump
Check 'the superseded read finishing changes nothing'      $true $t.Vm.IsLoadingCloudGroups
[void]$g.ReleaseGroups(); Pump
Check 'the current read finishing clears it'               $false $t.Vm.IsLoadingCloudGroups

# A read the pane gave up on (Clear) must not leave it showing.
[void](Run ($t.Vm.ReloadCommand.ExecuteAsync($null)))
Check 'reading again'                                      $true $t.Vm.IsLoadingCloudGroups
$t.Vm.Clear()
Check 'clearing the pane clears it'                        $false $t.Vm.IsLoadingCloudGroups
[void]$g.ReleaseGroups(); Pump
Check '  and the abandoned read does not bring it back'    $false $t.Vm.IsLoadingCloudGroups

# Moving on to an object with no cloud read (a group) while a user's is still out must not carry it over.
$TEAM = 'CN=Team,OU=Groups,DC=contoso,DC=net'
$t.Fake.Objects[$TEAM] = [UnifiedDirectoryManager.Models.AdAttribute[]]@($Fake::Attr('displayName', 'Team'))
[void](Run ($t.Vm.LoadAsync($ALICE, $User)))
Check 'a user, cloud groups loading'                       $true $t.Vm.IsLoadingCloudGroups
[void](Run ($t.Vm.LoadAsync($TEAM, [UnifiedDirectoryManager.Models.AdObjectType]::Group)))
Check '  then a group: nothing loading'                    $false $t.Vm.IsLoadingCloudGroups
while ($g.ReleaseGroups()) { Pump }

# Signed out there is no cloud read, so nothing to wait for.
$t = NewPane
[void](Run ($t.Vm.LoadAsync($ALICE, $User)))
Check 'signed out, it never shows'                         $false $t.Vm.IsLoadingCloudGroups

Write-Host "`n--- what the operator sees: the real pane, with the app's resources ---"
$app = [UnifiedDirectoryManager.App]::new()
# Closing the test window must not shut the dispatcher down under the rest of the suite.
$app.ShutdownMode = [System.Windows.ShutdownMode]::OnExplicitShutdown
$app.InitializeComponent()

$g = SignedInGraph; $g.HoldGroups = $true
$t = NewPane $g
$view = [UnifiedDirectoryManager.Views.Controls.EditPaneView]::new()
$view.DataContext = $t.Vm
$win = [System.Windows.Window]::new()
$win.Content = $view; $win.Width = 800; $win.Height = 600
$win.ShowActivated = $false; $win.ShowInTaskbar = $false; $win.Left = -4000
$win.Show(); Pump
function El([string]$name) { $view.FindName($name) }

Check 'nothing selected: the hint shows'                   $Vis::Visible (Safe { (El 'EmptyState').Visibility })
Check '  and says to select something'                     'Select an object to view and edit its attributes.' (Safe { (El 'EmptyStateText').Text })

$t.Fake.HoldLoads = $true
$load = $t.Vm.LoadAsync($ALICE, $User); Pump
$t.Fake.HoldLoads = $false   # only this read is held: the save below reloads the pane, and that read must just answer
Check 'while loading: the empty pane says Loading'         'Loading…' (Safe { (El 'EmptyStateText').Text })
Check '  with its bar showing'                             $Vis::Visible (Safe { (El 'EmptyState').Children[1].Visibility })
[void]$t.Fake.ReleaseLoad($ALICE)
[void](Run $load); Pump
Check 'loaded: the hint is gone'                           $Vis::Collapsed (Safe { (El 'EmptyState').Visibility })
Check '  no busy bar'                                      $Vis::Collapsed (Safe { (El 'BusyPanel').Visibility })
Check '  buttons usable'                                   $true (Safe { (El 'HeaderButtons').IsEnabled })
Check '  tabs usable'                                      $true (Safe { (El 'ObjectTabs').IsEnabled })
Check '  normal cursor'                                    $null (Safe { (El 'PaneRoot').Cursor })

foreach ($tab in (El 'ObjectTabs').Items) { if ($tab.Header -eq 'Member Of') { $tab.IsSelected = $true } }
Pump
Check 'Member Of shows the cloud groups are loading'       $Vis::Visible (Safe { (El 'CloudGroupsLoading').Visibility })
[void]$g.ReleaseGroups(); Pump
Check '  and stops once they are in'                       $Vis::Collapsed (Safe { (El 'CloudGroupsLoading').Visibility })

# Busy, through a real save held mid-write.
(Field $t.Vm 'description').EditText = 'edited'
$t.Fake.HoldWrites = $true
$save = $t.Vm.SaveCommand.ExecuteAsync($null); Pump
Check 'saving: the busy bar shows'                         $Vis::Visible (Safe { (El 'BusyPanel').Visibility })
Check '  saying what it is doing'                          'Saving properties…' (Safe { (El 'BusyText').Text })
Check '  the buttons are disabled'                         $false (Safe { (El 'HeaderButtons').IsEnabled })
Check '  the tabs are disabled, so nothing typed is lost'  $false (Safe { (El 'ObjectTabs').IsEnabled })
Check '  the cursor is busy'                               ([System.Windows.Input.Cursors]::Wait) (Safe { (El 'PaneRoot').Cursor })
Check '  across the whole pane'                            $true (Safe { (El 'PaneRoot').ForceCursor })

# The bar has to MOVE: the app's own ProgressBar template would show a solid, still bar here.
$bar = (El 'BusyPanel').Children[0]
$sweep = Safe { $bar.Template.FindName('Sweep', $bar) }
PumpFor 400
$x1 = Safe { $sweep.OpacityMask.RelativeTransform.X }
PumpFor 250
$x2 = Safe { $sweep.OpacityMask.RelativeTransform.X }
Check 'the busy bar is animating'                          $true ($x1 -is [double] -and $x2 -is [double] -and $x1 -ne $x2)
Check '  across the bar, not off its ends'                 $true ($x1 -is [double] -and $x1 -gt -1 -and $x1 -lt 1)

[void]$t.Fake.ReleaseWrite()
[void](Run $save); Pump
Check 'saved: the busy bar is gone'                        $Vis::Collapsed (Safe { (El 'BusyPanel').Visibility })
Check '  the pane is usable again'                         $true (Safe { (El 'ObjectTabs').IsEnabled })
Check '  the cursor is back'                               $null (Safe { (El 'PaneRoot').Cursor })
# A running animation makes WPF redraw every frame, even behind a hidden bar. So it must stand still now.
PumpFor 150
$x3 = Safe { $sweep.OpacityMask.RelativeTransform.X }
PumpFor 250
$x4 = Safe { $sweep.OpacityMask.RelativeTransform.X }
Write-Host "    (sweep while busy: $x1 -> $x2; once hidden: $x3 -> $x4)"
Check '  and the hidden bar has stopped animating'         $true ($x3 -is [double] -and $x3 -eq $x4)

$win.Close(); Pump

Write-Host ""
Write-Host "pass=$pass fail=$fail"
if ($fail -gt 0) { exit 1 }
