<#
.SYNOPSIS
  Regression tests for the 2.3.2 silent-failure fixes: F9 (a cancelled scenario must not report Success),
  F2 (the edit pane's stale-load guard) and F3/F4 (Graph membership reads must page and must not swallow).

.DESCRIPTION
  These three defects share a shape: something went wrong or went unfinished, and the app said it was fine.
  What can be asserted here without a live directory or tenant is recorded below; what cannot is listed at
  the end of this file so the gap is visible rather than implied.

  Run with:  pwsh -NoProfile -File ./app/build/test-silent-failures.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dll = Join-Path (Split-Path -Parent $root) 'debug\UnifiedDirectoryManager.dll'
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

Write-Host "`n== F9: a cancelled scenario must not be recorded as Success ==" -ForegroundColor Cyan
# Steps are isolated and AD has no transactions, so everything before a cancel stays committed. Reporting
# Success would tell the operator a termination finished when the account can be disabled but still in every
# group and still licensed. The rule lives in a pure static so this boundary is testable at all.
$Runner = [UnifiedDirectoryManager.Services.ScenarioRunner]
$note = $Runner.GetMethod('CancelNote', [System.Reflection.BindingFlags]'NonPublic,Static')
if ($null -eq $note) { $note = $Runner.GetMethod('CancelNote', [System.Reflection.BindingFlags]'Public,Static') }
Check 'CancelNote exists' $true ($null -ne $note)
function Note([bool]$cancelled, [int]$interrupted, [int]$run, [int]$total) {
    # Missing method: report a distinct value rather than throwing, so the rest of the file still runs and a
    # partial revert can still be triaged from one run.
    if ($null -eq $note) { return '(CancelNote missing)' }
    $note.Invoke($null, [object[]]@($cancelled, $interrupted, $run, $total))
}

# Nothing was cancelled: a clean run has nothing to report.
Check 'a complete run has no note'        $null (Note $false 0 5 5)

# The cancelled flag is load-bearing on its own. Today a run that was not cancelled always finishes every
# step, so disagreeing counts are unreachable -- but the note must key off the cancel, not off the counts,
# or a future skip-a-step path would start reporting healthy runs as cancelled.
Check 'no cancel means no note, whatever the counts' $null (Note $false 0 2 5)
# ...and on the interrupted branch too, which had no cancelled check at all.
Check 'nor when a step index is set without a cancel'  $null (Note $false 3 3 5)

# The boundary that matters most: cancelling AFTER the last step left nothing undone, so it stays a success.
Check 'cancelling after the last step is still success' $null (Note $true 0 5 5)

# Cancelled between steps, with steps left.
$n = Note $true 0 2 5
Check 'cancelling between steps produces a note' $true ($null -ne $n)
Check 'it names where it stopped'               $true ($n -like '*after step 2 of 5*')
Check 'and how much did not run'                $true ($n -like '*3 step(s) did not run*')

# Cancelled INSIDE a step — worse, because that step may have half-applied, and the wording must say so.
$d = Note $true 3 3 5
Check 'cancelling inside a step produces a note' $true ($null -ne $d)
Check 'it says during, not after'               $true ($d -like '*during step 3 of 5*')
Check 'and warns it may have partly applied'    $true ($d -like '*partly applied*')

# An interrupted step reports as interrupted even on the last step: it did not finish.
$last = Note $true 5 5 5
Check 'an interrupted last step is not a success' $true ($null -ne $last)
Check 'and reads as during'                       $true ($last -like '*during step 5 of 5*')

# A one-step scenario cancelled before that step ran.
$one = Note $true 0 0 1
Check 'nothing ran at all still reports'          $true ($one -like '*after step 0 of 1*')

Write-Host "`n== F2: the edit pane guards stale loads with a token, not a DN ==" -ForegroundColor Cyan
# A DN comparison cannot tell a superseded load from the current one, because the pane reloads the SAME
# object after a save. Without a token, an overlapping load repopulates every field from the object it read
# while _dn names a different one — and the next Save writes those values to the wrong object.
# These assert BEHAVIOUR-BEARING source, not shape. The previous version checked that a field existed and
# two methods took an int — all of which stay true if every guard in the class is deleted, so it passed
# against the unfixed code. Reflection cannot see method bodies; the source can.
$paneSrc = Get-Content -Raw (Join-Path (Split-Path -Parent $root) 'app\src\UnifiedDirectoryManager\ViewModels\EditPaneViewModel.cs')

# The token has to be BUMPED, or every comparison against it is trivially true and the guard is inert.
Check 'the load token is incremented per load' $true ($paneSrc -match '\+\+_loadToken')
# Clearing the pane must invalidate an in-flight load, or it repopulates the pane it just cleared.
Check 'and clearing the pane invalidates one'  $true ($paneSrc -match '_loadToken\+\+')

# One guard per await in LoadAsync, plus the two helpers, plus catch and finally.
$guards = ([regex]::Matches($paneSrc, 'token != _loadToken')).Count
Check 'every await is followed by a staleness check' $true ($guards -ge 6)

# The defect itself: _dn is what every write targets, so it must NOT be published before the object loads.
$publishedEarly = $paneSrc -match '(?m)var token = \+\+_loadToken;\s*\r?\n\s*_dn = distinguishedName;'
Check '_dn is not assigned before the first await' $false $publishedEarly
$blanked = $paneSrc -match '(?m)_dn = null;\s*\r?\n\s*HasObject = false;'
Check 'and is cleared while a load is in flight'    $true $blanked

# A write must target the object it was STARTED for, not whatever is selected when it finishes.
Check 'group writes capture their target'      $true ($paneSrc -match 'CaptureTarget\(\) is not')
Check 'and address the captured DN'            $true ($paneSrc -match 'ApplyChangesAsync\(who\.Dn')
Check 'and the captured mailbox identity'      $true ($paneSrc -match 'who\.CloudUpn!')
Check 'no group write reads the live _cloudUpn' $false ($paneSrc -match 'DistributionGroupMemberAsync\(groupId, _cloudUpn!')

# "Not synced" and "the lookup failed" must not share a message.
Check 'a failed Entra lookup is not called unsynced' $true ($paneSrc -match "Couldn't check Entra ID")

Write-Host "`n== F3/F4: Graph membership reads page, and do not swallow failures ==" -ForegroundColor Cyan
# Source-level assertions. Driving these for real needs a Graph fake the app does not have yet (see the note
# at the foot of this file), but the two properties that were wrong are both visible in the source: a
# hard-coded single page, and a catch that turned a failure into an empty list.
$svc = Join-Path (Split-Path -Parent $root) 'app\src\UnifiedDirectoryManager\Services\GraphService.cs'
$src = Get-Content -Raw $svc

# The swallow that let "remove all cloud groups" report Success on a failed read.
Check 'no empty-list swallow remains' 0 `
    ([regex]::Matches($src, 'Could not read (object|cloud) group memberships')).Count

# Every membership read must follow the continuation link.
Check 'membership reads drain their pages' $true ($src -match 'DrainGroupPagesAsync')
Check 'group members follow OdataNextLink' $true ($src -match 'Members\s*\r?\n?\s*\.WithUrl\(page\.OdataNextLink\)')

# Running past the guard must THROW, not return what was read so far — returning a partial list silently is
# the exact defect being fixed.
Check 'the paging guard throws rather than truncating' $true `
    ($src -match 'refusing to return a partial list')
Check 'and no read is still pinned to one 200-row page' $false ($src -match 'QueryParameters\.Top = 200')

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })

Write-Host @'

NOT covered here, and why:
  * F2's actual race (two overlapping LoadAsync calls resolving out of order) and F3/F4's paging behaviour
    both need fakes for IDirectoryService (31 members) and IGraphService. The app has no service-fake
    infrastructure, so these are asserted structurally above rather than behaviourally. Building a recording
    fake would make this suite real, and would also unlock the F18/F19 cloud-pane races.
  * The scenario runner's cancel path is covered at the rule (CancelNote) but not end to end, for the same
    reason.
'@ -ForegroundColor DarkGray

if ($fail -gt 0) { exit 1 }
