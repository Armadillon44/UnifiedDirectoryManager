<#
.SYNOPSIS
  Regression tests for the cloud properties pane's two staleness races: F18 (a superseded load doubles every
  list) and F19 (a superseded post-save re-read stamps the new selection's values onto the old row).

.DESCRIPTION
  Both are the same missing pattern in two places. The pane loads in stages and each stage resumes after an
  await, so a second selection can start while an earlier stage is still in flight. The guard has to answer
  "is this still my load", and a row reference alone cannot: re-selecting the SAME row is an ordinary event
  because Save, Revert and enable/disable all call SetTarget(row) to force a re-read.

  Neither is reachable against a live tenant on any schedule a test can rely on -- the whole failure is
  about ordering. So the graph and Exchange doubles park each read until the test releases it, which makes
  "the operator clicked another row mid-load" something a test can perform exactly.

  Run with:  pwsh -NoProfile -STA -File ./app/build/test-cloudpane-race.ps1
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

$Row       = [UnifiedDirectoryManager.Models.CloudObjectRow]
$Kind      = [UnifiedDirectoryManager.Models.CloudObjectKind]
$Source    = [UnifiedDirectoryManager.Models.CloudObjectSource]
$Prop      = [UnifiedDirectoryManager.Models.CloudProperty]
$Section   = [UnifiedDirectoryManager.Models.CloudPropertySection]
$Editable  = [UnifiedDirectoryManager.Models.CloudPropertyEditability]
$Editor    = [UnifiedDirectoryManager.Models.CloudPropertyEditor]
$Group     = [UnifiedDirectoryManager.Models.CloudGroup]
$License   = [UnifiedDirectoryManager.Models.CloudLicense]
$Member    = [UnifiedDirectoryManager.Models.CloudMember]
$UserInfo  = [UnifiedDirectoryManager.Models.CloudUserInfo]
$Detail    = [UnifiedDirectoryManager.ViewModels.CloudObjectDetailViewModel]
$dialogs   = [UnifiedDirectoryManager.TestSupport.InertDialogService]::new()

function New-Row([string]$id, [string]$name, $objectKind, [hashtable]$values = @{}, $objectSource = $null) {
    $r = $Row::new()
    # Id and DisplayName are required init-only properties; PowerShell reaches them the same way the
    # deserialiser does.
    $Row.GetProperty('Id').SetValue($r, $id)
    $Row.GetProperty('DisplayName').SetValue($r, $name)
    $Row.GetProperty('Kind').SetValue($r, $objectKind)
    if ($null -ne $objectSource) { $Row.GetProperty('Source').SetValue($r, $objectSource) }
    foreach ($k in $values.Keys) { $r.Values[$k] = [string]$values[$k] }
    return $r
}

function New-Section([string]$title, [hashtable]$props, [bool]$writable = $false) {
    $list = [System.Collections.Generic.List[UnifiedDirectoryManager.Models.CloudProperty]]::new()
    $ed = if ($writable) { $Editable::Editable } else { $Editable::SystemReadOnly }
    # ::new does not fill optional parameters, so every one is passed.
    foreach ($k in $props.Keys) { $list.Add($Prop::new($k, $k, [string]$props[$k], $ed, $null, $Editor::Text, $null, $null)) }
    return $Section::new($title, $list)
}

# The pane starts its loads with `_ = LoadDetailAsync(row)`, so nothing hands back a task to wait on. Pump
# until the parked read the test is about to release has actually been reached.
function Wait-For([scriptblock]$condition, [string]$what) {
    for ($i = 0; $i -lt 200; $i++) {
        if (& $condition) { return }
        Start-Sleep -Milliseconds 10
    }
    throw "Timed out waiting for $what"
}

# Lets a released continuation run to its next await before the test asserts on the pane.
function Settle { for ($i = 0; $i -lt 20; $i++) { Start-Sleep -Milliseconds 5 } }

Write-Host "`n== F18: re-selecting the same row must not double every list ==" -ForegroundColor Cyan
# Save, Revert and enable/disable all call SetTarget(row) on the SAME row to force a re-read. That bumps the
# token, clears the lists and starts a second load -- and the first load's continuation used to pass a check
# that only compared the row reference, which still matched, and append its results beside the new ones.
$graph = [UnifiedDirectoryManager.TestSupport.HoldableGraphService]::new()
$exchange = [UnifiedDirectoryManager.TestSupport.HoldableExchangeService]::new()
$vm = $Detail::new($graph, $exchange, $dialogs)

$graph.Sections.Add((New-Section 'Identity' @{ objectId = 'u-1' }))
$licenses = [System.Collections.Generic.List[UnifiedDirectoryManager.Models.CloudLicense]]::new()
$licenses.Add($License::new([guid]::NewGuid(), 'Microsoft 365 E3', 'SPE_E3', 'Direct', $true, $false))
$groups = [System.Collections.Generic.List[UnifiedDirectoryManager.Models.CloudGroup]]::new()
$groups.Add($Group::new('g-1', 'All Staff', $null, 'allstaff@contoso.com', 'Security', 'Assigned', 'Cloud', $false))
$graph.UserInfo = $UserInfo::new('u-1', 'Jane Doe', 'jane@contoso.com', $true, $false, 'Member', 'US', $null, $licenses, $groups)

$graph.Hold = $true
$user = New-Row 'u-1' 'Jane Doe' $Kind::User @{ userPrincipalName = 'jane@contoso.com' }

$vm.SetTarget($user)
Wait-For { $graph.PendingDetails -ge 1 } 'the first detail read'

# The operator saves, which re-targets the same row. Load #2 starts; load #1 is still parked.
$vm.SetTarget($user)
Wait-For { $graph.PendingDetails -ge 2 } 'the second detail read'
Check 'both loads are in flight'          2 $graph.PendingDetails

# Release the SUPERSEDED one first. This is the whole bug: its continuation resumes into a pane that now
# belongs to load #2.
[void]$graph.ReleaseDetail()
Settle
Check 'the superseded load adds no licences'   0 $vm.Licenses.Count
Check 'and no memberships'                     0 $vm.Memberships.Count
Check 'and it did not even reach the user read' 0 $graph.PendingUsers

# Now the current load finishes properly. Release EVERY parked user read, not just one: under the bug the
# superseded load has queued one of its own, and letting both land is what produced the doubled lists the
# operator actually saw.
[void]$graph.ReleaseDetail()
Wait-For { $graph.PendingUsers -ge 1 } 'the current load to reach the user read'
while ($graph.PendingUsers -gt 0) { [void]$graph.ReleaseUser(); Settle }
Settle
Check 'the licence is listed once'             1 $vm.Licenses.Count
Check 'and the membership once'                1 $vm.Memberships.Count
Check 'naming the right group'                 'All Staff' $vm.Memberships[0].DisplayName

Write-Host "`n== F18: the same hazard on a group's members ==" -ForegroundColor Cyan
$graph2 = [UnifiedDirectoryManager.TestSupport.HoldableGraphService]::new()
$vm2 = $Detail::new($graph2, $exchange, $dialogs)
$graph2.Sections.Add((New-Section 'Identity' @{ origin = 'Cloud' }))
$graph2.GroupMembers.Add($Member::new('m-1', 'Jane Doe', 'jane@contoso.com', 'user'))
$graph2.GroupMembers.Add($Member::new('m-2', 'Bob Roe', 'bob@contoso.com', 'user'))
$graph2.Hold = $true

$grp = New-Row 'g-1' 'All Staff' $Kind::Group @{}
$vm2.SetTarget($grp)
Wait-For { $graph2.PendingDetails -ge 1 } 'the first group read'
$vm2.SetTarget($grp)
Wait-For { $graph2.PendingDetails -ge 2 } 'the second group read'

[void]$graph2.ReleaseDetail()   # the superseded one
Settle
Check 'the superseded load adds no members'   0 $vm2.Members.Count
# And it must not have STARTED the members read either. Members are appended after a second await, so a
# guard that only stops the append still spends the round trip and still races the current load to it.
Check 'nor starts a members read'             0 $graph2.PendingMembers

[void]$graph2.ReleaseDetail()   # the current one
Wait-For { $graph2.PendingMembers -ge 1 } 'the members read'
while ($graph2.PendingMembers -gt 0) { [void]$graph2.ReleaseMembers(); Settle }
Settle
Check 'the current load adds them once'       2 $vm2.Members.Count

Write-Host "`n== F18: a load that is never superseded still works ==" -ForegroundColor Cyan
# The negative control. A guard that rejects everything passes every test above and breaks the pane.
$graph3 = [UnifiedDirectoryManager.TestSupport.HoldableGraphService]::new()
$vm3 = $Detail::new($graph3, $exchange, $dialogs)
$graph3.Sections.Add((New-Section 'Identity' @{ objectId = 'u-9'; displayName = 'Solo' }))
$graph3.UserInfo = $UserInfo::new('u-9', 'Solo', 'solo@contoso.com', $true, $false, 'Member', 'US', $null, $licenses, $groups)
$vm3.SetTarget((New-Row 'u-9' 'Solo' $Kind::User @{ userPrincipalName = 'solo@contoso.com' }))
Settle
Check 'the sections are on screen'   $true ($vm3.Sections.Count -ge 1)
Check 'the licence is listed'        1 $vm3.Licenses.Count
Check 'the membership is listed'     1 $vm3.Memberships.Count

Write-Host "`n== F19: a superseded post-save re-read must not touch the saved row ==" -ForegroundColor Cyan
# Save a distribution-list edit, then click another row while the re-read is still queued behind it on the
# serialised Exchange channel. Sections now describe the OTHER group, and the save's continuation used to
# treat "superseded" as success and copy them into the row it had saved -- giving that group the other
# group's SMTP address, which is the identifier every later resolution addresses it by.
$ex = [UnifiedDirectoryManager.TestSupport.HoldableExchangeService]::new()
$g = [UnifiedDirectoryManager.TestSupport.HoldableGraphService]::new()
$vm4 = $Detail::new($g, $ex, $dialogs)

$sales = New-Row 'dl-sales' 'Sales' $Kind::Group @{ primarySmtpAddress = 'sales@contoso.com'; alias = 'sales' } $Source::Exchange
$hr    = New-Row 'dl-hr'    'HR'    $Kind::Group @{ primarySmtpAddress = 'hr@contoso.com';    alias = 'hr'    } $Source::Exchange

# Open Sales and let its detail read land, so there is something editable on screen.
$ex.Sections.Add((New-Section 'General' @{ primaryAddress = 'sales@contoso.com'; alias = 'sales'; mailTip = 'Sales team' } $true))
$vm4.SetTarget($sales)
Settle
Check 'the group detail loaded'  $true ($vm4.Sections.Count -ge 1)

# Edit a row so there is something to save.
$mailTip = @($vm4.Sections | ForEach-Object { $_.Properties } | Where-Object { $_.Key -eq 'mailTip' })[0]
$mailTip.Value = 'Sales team (UK)'
Check 'the edit is pending'      $true $vm4.HasChanges

# From here on the channel is held, so the save's re-read parks.
$ex.Hold = $true
$saveTask = $vm4.SaveCommand.ExecuteAsync($null)
Wait-For { $ex.PendingDetails -ge 1 } 'the post-save re-read'
Check 'the write was sent'       1 $ex.Writes.Count

# The operator clicks HR while that re-read is still parked. HR's own read is NOT held, so the pane fills
# with HR in full -- which is the state the save's continuation wakes up into, and the whole point: Sections
# describe HR, and the row it is about to write into is Sales.
$ex.Hold = $false
$ex.Sections.Clear()
$ex.Sections.Add((New-Section 'General' @{ primaryAddress = 'hr@contoso.com'; alias = 'hr' } $true))
$vm4.SetTarget($hr)
Settle
Check 'the pane now shows HR' 'hr@contoso.com' (@($vm4.Sections | ForEach-Object { $_.Properties } | Where-Object { $_.Key -eq 'primaryAddress' })[0].Value)

# Only now does the superseded re-read resume.
[void]$ex.ReleaseDetail()
Settle

Check 'Sales keeps its own address' 'sales@contoso.com' $sales.Get('primarySmtpAddress')
Check 'and its own alias'           'sales' $sales.Get('alias')
Check 'HR is untouched too'         'hr@contoso.com' $hr.Get('primarySmtpAddress')

Write-Host "`n== F19: a re-read that is NOT superseded still updates the row ==" -ForegroundColor Cyan
# The negative control, and the reason the guard cannot simply be "never sync". An alias change rewrites the
# primary address, and the row is what the next read addresses -- so a live re-read has to land.
$ex2 = [UnifiedDirectoryManager.TestSupport.HoldableExchangeService]::new()
$vm5 = $Detail::new($g, $ex2, $dialogs)
$mktg = New-Row 'dl-mktg' 'Marketing' $Kind::Group @{ primarySmtpAddress = 'mktg@contoso.com'; alias = 'mktg' } $Source::Exchange
$ex2.Sections.Add((New-Section 'General' @{ primaryAddress = 'mktg@contoso.com'; alias = 'mktg' } $true))
$vm5.SetTarget($mktg)
Settle
$aliasRow = @($vm5.Sections | ForEach-Object { $_.Properties } | Where-Object { $_.Key -eq 'alias' })[0]
$aliasRow.Value = 'marketing'

# Exchange applies the alias and the primary address follows it.
$ex2.Sections.Clear()
$ex2.Sections.Add((New-Section 'General' @{ primaryAddress = 'marketing@contoso.com'; alias = 'marketing' } $true))
$saveTask2 = $vm5.SaveCommand.ExecuteAsync($null)
Settle
Check 'the row takes the new address' 'marketing@contoso.com' $mktg.Get('primarySmtpAddress')
Check 'and the new alias'             'marketing' $mktg.Get('alias')

Write-Host "`n== F19: a failed re-read is told apart from a superseded one ==" -ForegroundColor Cyan
# Three outcomes, not two. A failure means the write landed but the read did not, and the pane has to fall
# back to the list-row summary and say so -- if that were folded in with "superseded", a real failure would
# leave the edited rows on screen with the Save bar still armed over changes already applied.
$ex3 = [UnifiedDirectoryManager.TestSupport.HoldableExchangeService]::new()
$vm6 = $Detail::new($g, $ex3, $dialogs)
$ops = New-Row 'dl-ops' 'Operations' $Kind::Group @{ primarySmtpAddress = 'ops@contoso.com'; alias = 'ops' } $Source::Exchange
$ex3.Sections.Add((New-Section 'General' @{ primaryAddress = 'ops@contoso.com'; alias = 'ops'; mailTip = 'Ops' } $true))
$vm6.SetTarget($ops)
Settle
$tip = @($vm6.Sections | ForEach-Object { $_.Properties } | Where-Object { $_.Key -eq 'mailTip' })[0]
$tip.Value = 'Ops (24x7)'
$ex3.Hold = $true
$saveTask3 = $vm6.SaveCommand.ExecuteAsync($null)
Wait-For { $ex3.PendingDetails -ge 1 } 'the post-save re-read'
[void]$ex3.FailDetail('the mailbox server is busy')
Settle
Check 'the save is still reported'    $true ($vm6.Status -like '*Saved*')
Check 'and so is the read failure'    $true ($vm6.Status -like '*busy*')
Check 'the row keeps its address'     'ops@contoso.com' $ops.Get('primarySmtpAddress')
Check 'and the Save bar is disarmed'  $false $vm6.HasChanges

Write-Host "`n== the outcome is a three-state, not a bool ==" -ForegroundColor Cyan
# The mutation check for the shape the two fixes rest on. Folding Superseded back into a bool flips this.
$src = Get-Content -Raw (Join-Path $repoRoot 'app\src\UnifiedDirectoryManager\ViewModels\CloudObjectDetailViewModel.cs')
Check 'the read returns the enum' $true ($src -match 'private async Task<ExchangeRead> LoadExchangeDetailAsync')
foreach ($state in 'Loaded', 'Failed', 'Superseded') {
    Check "  and names $state" $true ($src -match ("ExchangeRead\." + $state))
}
# And the Graph load must compare the token, not just the row reference.
$load = [regex]::Match($src, '(?s)private async Task LoadDetailAsync\(CloudObjectRow row\).*?\r?\n    \}').Value
Check 'LoadDetailAsync captures the token' $true ($load -match 'var token = _detailToken')
Check 'and compares it'                    $true ($load -match 'token == _detailToken')
Check 'not the row reference alone'        $false ($load -match 'if \(!ReferenceEquals\(_currentTarget, row\)\) return;')

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
