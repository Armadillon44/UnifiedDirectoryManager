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


Write-Host "`n== membership changes wait for Save ==" -ForegroundColor Cyan
# Reported: change a user's properties, remove them from a group before saving, and the property change is
# gone -- the group removal wrote at once and reloaded the whole pane. Membership changes now wait for Save
# like every other edit. These drive the real view model against the fake directory and record what is
# written, so each assertion is about writes, not about the source. (The fakes log writes as C# value
# tuples, whose field names do not survive into PowerShell: Item1 is the DN, Item2 the changes.)
$SALES = 'CN=Sales,OU=Groups,DC=contoso,DC=net'
$STAFF = 'CN=All Staff,OU=Groups,DC=contoso,DC=net'
$MKTG  = 'CN=Marketing,OU=Groups,DC=contoso,DC=net'
$CAROL = 'CN=Carol,OU=Staff,DC=contoso,DC=net'
$TEAM  = 'CN=Team,OU=Groups,DC=contoso,DC=net'
$Group = [UnifiedDirectoryManager.Models.AdObjectType]::Group
$Pend  = [UnifiedDirectoryManager.Models.PendingMembership]

function Multi([string]$ldap, [string[]]$dns, [string[]]$names) {
    $a = $Fake::Attr($ldap, $dns[0])
    $a.DisplayValues[0] = $names[0]
    for ($i = 1; $i -lt $dns.Count; $i++) { $a.RawValues.Add($dns[$i]); $a.DisplayValues.Add($names[$i]) }
    return $a
}
function NewEditablePane {
    $fake = New-Object UnifiedDirectoryManager.TestSupport.FakeDirectoryService
    $fake.Objects[$ALICE] = [UnifiedDirectoryManager.Models.AdAttribute[]]@(
        $Fake::Attr('displayName', 'Alice'), $Fake::Attr('description', 'alice-original'),
        (Multi 'memberOf' @($SALES, $STAFF) @('Sales', 'All Staff')),
        (Multi 'directReports' @($BOB) @('Bob')))
    $fake.Objects[$TEAM] = [UnifiedDirectoryManager.Models.AdAttribute[]]@($Fake::Attr('displayName', 'Team'))
    $fake.GroupMembers[$TEAM] = [UnifiedDirectoryManager.Services.GroupMember[]]@(
        [UnifiedDirectoryManager.Services.GroupMember]::new('Bob', $BOB))
    $dlg = New-Object UnifiedDirectoryManager.TestSupport.InertDialogService
    $dlg.RecordAlerts = $true   # a Save with nothing to save alerts; that must not throw mid-suite
    $errors = New-Object System.Collections.Generic.List[string]
    $vm = [UnifiedDirectoryManager.ViewModels.EditPaneViewModel]::new(
        # GetNewClosure: the callback fires after this function has returned, when $errors would otherwise be gone.
        $fake, $dlg, [Action[string]] ({ param($m) $errors.Add($m) }.GetNewClosure()),
        (New-Object UnifiedDirectoryManager.TestSupport.InertGraphService),
        (New-Object UnifiedDirectoryManager.TestSupport.InertExchangeService))
    return @{ Vm = $vm; Fake = $fake; Dlg = $dlg; Errors = $errors }
}
function Field($vm, [string]$ldap) {
    foreach ($c in $vm.General, $vm.Account, $vm.Address, $vm.Organization, $vm.Email) {
        foreach ($f in $c) { if ($f.LdapName -eq $ldap) { return $f } }
    }
}
function Row($vm, [string]$name) { $vm.MemberOf | Where-Object { $_.Name -eq $name } | Select-Object -First 1 }
# A broken build of the code under test must FAIL a named check, not crash the suite before the tally: a crash
# reads as "inconclusive" to the mutation harness rather than as the guard doing its job. So results are read
# through Safe, and a throwing operation becomes a value a check can disagree with.
function Safe([scriptblock]$s) { try { & $s } catch { "<error: $($_.Exception.Message)>" } }
function Run($task) {
    $n = 0; while (-not $task.IsCompleted -and $n -lt 200) { Pump; $n++ }
    try { return $task.GetAwaiter().GetResult() } catch { return "<threw: $($_.Exception.Message)>" }
}
function List([object[]]$items) { $l = New-Object System.Collections.ArrayList; foreach ($i in $items) { [void]$l.Add($i) }; return ,$l }
function OnPremGroup([string]$name, [string]$dn) {
    [UnifiedDirectoryManager.Models.GroupRef]::new([UnifiedDirectoryManager.Models.GroupChannel]::OnPremAd, $name, $dn, $null, $null, '')
}
function PickRow([string]$name, [string]$dn) {
    $r = New-Object UnifiedDirectoryManager.Models.AdObjectRow -Property @{ Name = $name; DistinguishedName = $dn }
    return ,([UnifiedDirectoryManager.Models.AdObjectRow[]]@($r))
}

# --- the reported sequence ---
$t = NewEditablePane
[void](Await ($t.Vm.LoadAsync($ALICE, $User))); Pump
Check 'Alice is loaded with two groups'  2 $t.Vm.MemberOf.Count
(Field $t.Vm 'description').EditText = 'edited'
$t.Vm.RemoveFromGroupCommand.Execute((List @(Row $t.Vm 'Sales'))); Pump
Check 'removing a group writes nothing'  0 $t.Fake.Writes.Count
Check '  and keeps the property edit'    'edited' (Field $t.Vm 'description').EditText
Check '  the group is marked removing'   $Pend::Removing (Row $t.Vm 'Sales').Pending
Check '  and the row says so'            'Removing (unsaved)' (Row $t.Vm 'Sales').PendingLabel
Check '  so there are unsaved changes'   $true $t.Vm.HasUnsavedChanges()
[void](Run ($t.Vm.SaveCommand.ExecuteAsync($null)))
Check 'Save writes both'                 2 $t.Fake.Writes.Count
Check '  the property first'             'description' (Safe { $t.Fake.Writes[0].Item2[0].LdapName })
Check '  then the group removal'         'RemoveFromGroups' (Safe { [string]$t.Fake.Writes[1].Item2[0].Op })
Check '  from Sales'                     $SALES (Safe { $t.Fake.Writes[1].Item2[0].Values[0] })
Check '  all against Alice'              2 @($t.Fake.Writes | Where-Object { $_.Item1 -eq $ALICE }).Count
Check '  in one confirmation'            1 $t.Dlg.Confirmations.Count

# --- adding, and taking changes back ---
$t = NewEditablePane
[void](Await ($t.Vm.LoadAsync($ALICE, $User))); Pump
$t.Dlg.PickedGroups = [UnifiedDirectoryManager.Models.GroupRef[]]@((OnPremGroup 'Marketing' $MKTG))
$t.Vm.AddToGroupsCommand.Execute($null)
Check 'adding writes nothing'            0 $t.Fake.Writes.Count
Check '  the group appears as adding'    $Pend::Adding (Row $t.Vm 'Marketing').Pending
# On its own -- no property edited -- a pending membership change is unsaved work. (The earlier check had a
# description edit alongside, which made it true regardless; mutation testing found the gap.)
Check '  which on its own is unsaved'    $true $t.Vm.HasUnsavedChanges()
$t.Vm.AddToGroupsCommand.Execute($null)
Check '  adding it twice adds one row'   1 @($t.Vm.MemberOf | Where-Object { $_.Name -eq 'Marketing' }).Count
$t.Vm.RemoveFromGroupCommand.Execute((List @(Row $t.Vm 'Marketing')))
Check 'removing a pending add drops it'  $null (Row $t.Vm 'Marketing')
Check '  leaving nothing unsaved'        $false $t.Vm.HasUnsavedChanges()
$t.Vm.RemoveFromGroupCommand.Execute((List @(Row $t.Vm 'All Staff')))
$t.Dlg.PickedGroups = [UnifiedDirectoryManager.Models.GroupRef[]]@((OnPremGroup 'All Staff' $STAFF))
$t.Vm.AddToGroupsCommand.Execute($null)
Check 're-adding takes a removal back'   $Pend::None (Row $t.Vm 'All Staff').Pending
Check '  without a second row'           1 @($t.Vm.MemberOf | Where-Object { $_.Name -eq 'All Staff' }).Count

# --- declining, and reloading ---
$t.Vm.RemoveFromGroupCommand.Execute((List @(Row $t.Vm 'Sales')))
$t.Dlg.ConfirmAnswer = $false
[void](Run ($t.Vm.SaveCommand.ExecuteAsync($null)))
Check 'declining the save writes nothing' 0 $t.Fake.Writes.Count
Check '  and keeps the change pending'   $Pend::Removing (Row $t.Vm 'Sales').Pending
[void](Run ($t.Vm.ReloadCommand.ExecuteAsync($null)))
Check 'Reload discards pending changes'  $false $t.Vm.HasUnsavedChanges()
Check '  Sales is back as it was'        $Pend::None (Row $t.Vm 'Sales').Pending

# --- failures ---
# One group refusing must not stop the others -- as when each was written at once.
$t = NewEditablePane
[void](Await ($t.Vm.LoadAsync($ALICE, $User))); Pump
$t.Vm.RemoveFromGroupCommand.Execute((List @((Row $t.Vm 'Sales'), (Row $t.Vm 'All Staff'))))
[void]$t.Fake.FailWritesFor.Add($SALES)
$ok = Run ($t.Vm.SaveCommand.ExecuteAsync($null))
Check 'a refused group does not stop the rest' 2 $t.Fake.Writes.Count
Check '  All Staff was still written'    $STAFF (Safe { $t.Fake.Writes[1].Item2[0].Values[0] })
Check '  and the refusal is reported'    $true (($t.Errors -join ' ') -match 'Sales')

# A failed PROPERTY write stops before any group, and keeps every change on screen to try again.
$t = NewEditablePane
[void](Await ($t.Vm.LoadAsync($ALICE, $User))); Pump
(Field $t.Vm 'description').EditText = 'edited'
$t.Vm.RemoveFromGroupCommand.Execute((List @(Row $t.Vm 'Sales')))
[void]$t.Fake.FailWritesFor.Add($ALICE)
[void](Run ($t.Vm.SaveCommand.ExecuteAsync($null)))
Check 'a failed property write stops there' 1 $t.Fake.Writes.Count
Check '  the edit is kept'               'edited' (Field $t.Vm 'description').EditText
Check '  and so is the group change'     $Pend::Removing (Row $t.Vm 'Sales').Pending

Write-Host "`n== direct reports and a group's members wait too ==" -ForegroundColor Cyan
$t = NewEditablePane
[void](Await ($t.Vm.LoadAsync($ALICE, $User))); Pump
$t.Vm.RemoveDirectReportsCommand.Execute((List @(Safe { $t.Vm.DirectReports[0] })))
$t.Dlg.PickedObjects = PickRow 'Carol' $CAROL
$t.Vm.AddDirectReportsCommand.Execute($null)
Check 'direct report changes write nothing' 0 $t.Fake.Writes.Count
Check '  Bob is marked removing'         $Pend::Removing ($t.Vm.DirectReports | Where-Object { $_.Name -eq 'Bob' }).Pending
Check '  Carol is marked adding'         $Pend::Adding ($t.Vm.DirectReports | Where-Object { $_.Name -eq 'Carol' }).Pending
[void](Run ($t.Vm.SaveCommand.ExecuteAsync($null)))
$carolWrite = $t.Fake.Writes | Where-Object { $_.Item1 -eq $CAROL } | Select-Object -First 1
$bobWrite   = $t.Fake.Writes | Where-Object { $_.Item1 -eq $BOB } | Select-Object -First 1
Check 'Save sets Carol''s manager'       $ALICE (Safe { $carolWrite.Item2[0].Values[0] })
Check '  and clears Bob''s'              'Clear' (Safe { [string]$bobWrite.Item2[0].Op })
# Nobody can be their own manager.
$t.Dlg.RecordAlerts = $true
$t.Dlg.PickedObjects = PickRow 'Alice' $ALICE
$t.Vm.AddDirectReportsCommand.Execute($null)
Check 'adding yourself is refused'       1 $t.Dlg.Alerts.Count

$t = NewEditablePane
[void](Await ($t.Vm.LoadAsync($TEAM, $Group))); Pump
Check 'the group shows its member'       1 $t.Vm.Members.Count
$t.Dlg.PickedObjects = PickRow 'Carol' $CAROL
$t.Vm.AddMembersCommand.Execute($null)
$t.Vm.RemoveMemberCommand.Execute((List @($t.Vm.Members | Where-Object { $_.Name -eq 'Bob' })))
Check 'member changes write nothing'     0 $t.Fake.MemberWrites.Count
[void](Run ($t.Vm.SaveCommand.ExecuteAsync($null)))
Check 'Save adds Carol to the group'     $CAROL (Safe { ($t.Fake.MemberWrites | Where-Object { $_.Item1 }).Item3[0] })
Check '  and removes Bob'                $BOB (Safe { ($t.Fake.MemberWrites | Where-Object { -not $_.Item1 }).Item3[0] })

Write-Host "`n== leaving with unsaved changes asks first ==" -ForegroundColor Cyan
$t = NewEditablePane
[void](Await ($t.Vm.LoadAsync($ALICE, $User))); Pump
Check 'nothing unsaved: leave, no prompt' $true (Run ($t.Vm.ConfirmLeaveAsync()))
Check '  and nothing was asked'          0 $t.Dlg.UnsavedPrompts.Count
(Field $t.Vm 'description').EditText = 'edited'
$t.Vm.RemoveFromGroupCommand.Execute((List @(Row $t.Vm 'Sales')))
$t.Dlg.UnsavedAnswer = [UnifiedDirectoryManager.Services.UnsavedChoice]::Cancel
Check 'Cancel stays'                     $false (Run ($t.Vm.ConfirmLeaveAsync()))
Check '  with the edit intact'           'edited' (Field $t.Vm 'description').EditText
Check '  the prompt listed the group'    $true (Safe { $t.Dlg.UnsavedPrompts[0].Item2 -contains 'Remove from group: Sales (On-prem)' })
$t.Dlg.UnsavedAnswer = [UnifiedDirectoryManager.Services.UnsavedChoice]::Save
$asked = $t.Dlg.Confirmations.Count
Check 'Save leaves'                      $true (Run ($t.Vm.ConfirmLeaveAsync()))
Check '  having written both'            2 $t.Fake.Writes.Count
Check '  without asking a second time'   $asked $t.Dlg.Confirmations.Count

$t = NewEditablePane
[void](Await ($t.Vm.LoadAsync($ALICE, $User))); Pump
(Field $t.Vm 'description').EditText = 'edited'
$t.Dlg.UnsavedAnswer = [UnifiedDirectoryManager.Services.UnsavedChoice]::Discard
Check 'Discard leaves'                   $true (Run ($t.Vm.ConfirmLeaveAsync()))
Check '  writing nothing'                0 $t.Fake.Writes.Count
Check '  and nothing is left unsaved'    $false $t.Vm.HasUnsavedChanges()

# A save that fails from the prompt keeps the operator on the object, where the failure is.
$t = NewEditablePane
[void](Await ($t.Vm.LoadAsync($ALICE, $User))); Pump
$t.Vm.RemoveFromGroupCommand.Execute((List @(Row $t.Vm 'Sales')))
[void]$t.Fake.FailWritesFor.Add($SALES)
$t.Dlg.UnsavedAnswer = [UnifiedDirectoryManager.Services.UnsavedChoice]::Save
Check 'a failed save from the prompt stays' $false (Run ($t.Vm.ConfirmLeaveAsync()))

Write-Host "`n== immediate actions no longer wipe unsaved edits ==" -ForegroundColor Cyan
$t = NewEditablePane
[void](Await ($t.Vm.LoadAsync($ALICE, $User))); Pump
(Field $t.Vm 'description').EditText = 'edited'
$t.Vm.RemoveFromGroupCommand.Execute((List @(Row $t.Vm 'Sales')))
[void](Run ($t.Vm.UnlockCommand.ExecuteAsync($null)))
Check 'Unlock is written at once'        $ALICE (Safe { $t.Fake.Unlocks[0] })
Check '  and the edit survives it'       'edited' (Field $t.Vm 'description').EditText
Check '  so does the pending group'      $Pend::Removing (Row $t.Vm 'Sales').Pending

# Move used to reload and discard, with a warning saying so. The object is the same object afterwards.
$t.Dlg.PickedContainer = 'OU=Elsewhere,DC=contoso,DC=net'
[void](Run ($t.Vm.MoveCommand.ExecuteAsync($null)))
Check 'Move keeps the edit'              'edited' (Field $t.Vm 'description').EditText
Check '  and follows the object'         'CN=Alice,OU=Elsewhere,DC=contoso,DC=net' $t.Vm.CurrentDn
[void](Run ($t.Vm.SaveCommand.ExecuteAsync($null)))
Check '  so Save writes to its new home' 'CN=Alice,OU=Elsewhere,DC=contoso,DC=net' (Safe { $t.Fake.Writes[0].Item1 })

Write-Host "`n== the main window follows the list without losing edits ==" -ForegroundColor Cyan
# The decision is pure so it can be tested here; MainViewModel cannot be built in a test.
$decide = [UnifiedDirectoryManager.ViewModels.MainViewModel].GetMethod('DecideFollow', [System.Reflection.BindingFlags]'NonPublic,Static')
function Follow($unsaved, $editing, $selected) { [string]$decide.Invoke($null, @($unsaved, $editing, $selected)) }
Check 'nothing unsaved: load the row'    'Load'  (Follow $false $ALICE $BOB)
Check '  or clear when none is selected' 'Clear' (Follow $false $ALICE $null)
# A list refresh re-selects the same object as a new row; every pane write refreshes the list.
Check 'same object re-selected: keep'    'Keep'  (Follow $true $ALICE $ALICE.ToUpperInvariant())
Check 'selection emptied: keep the pane' 'Keep'  (Follow $true $ALICE $null)
Check 'a different object: ask'          'Ask'   (Follow $true $ALICE $BOB)

# Closing a window over unsaved edits asks first. A real window, closed for real.
$t = NewEditablePane
[void](Await ($t.Vm.LoadAsync($ALICE, $User))); Pump
(Field $t.Vm 'description').EditText = 'edited'
$win = [System.Windows.Window]::new(); $win.DataContext = $t.Vm
$win.ShowActivated = $false; $win.Left = -4000
$script:closed = $false; $win.add_Closed({ $script:closed = $true })
[UnifiedDirectoryManager.Views.UnsavedChangesGuard]::Attach($win)
$win.Show()
$t.Dlg.UnsavedAnswer = [UnifiedDirectoryManager.Services.UnsavedChoice]::Cancel
$win.Close(); Pump; Pump
Check 'Cancel keeps the window open'     $false $script:closed
$t.Dlg.UnsavedAnswer = [UnifiedDirectoryManager.Services.UnsavedChoice]::Discard
$win.Close(); Pump; Pump
Check 'Discard lets it close'            $true $script:closed

# And every window that hosts an edit pane is guarded.
$srcRoot = Join-Path (Split-Path -Parent $root) 'app\src\UnifiedDirectoryManager'
foreach ($w in 'Views\Dialogs\ObjectEditorWindow.xaml.cs', 'Views\Dialogs\AdObjectPropertiesWindow.xaml.cs') {
    Check "  $(Split-Path -Leaf $w) is guarded" $true ((Get-Content -Raw (Join-Path $srcRoot $w)) -match 'UnsavedChangesGuard\.Attach\(this\)')
}
Check '  and so is the main window'      $true ((Get-Content -Raw (Join-Path $srcRoot 'Views\MainWindow.xaml.cs')) -match 'UnsavedChangesGuard\.AttachFor\(this')
Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
