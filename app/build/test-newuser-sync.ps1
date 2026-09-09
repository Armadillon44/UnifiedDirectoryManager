<#
.SYNOPSIS
  Regression tests for F24: unticking every cloud group left New User unable to create anybody.

.DESCRIPTION
  SyncMandatory counted the group ROWS present rather than the rows TICKED, and an Include toggle raises
  PropertyChanged on the row, not CollectionChanged on the list -- so nothing re-evaluated it either. A
  template seeds cloud or distribution groups, the operator unticks them all, and the post-create Entra
  Connect sync stays forced on with its checkbox disabled. CreateAsync then refuses to create anybody until
  an Entra Connect server is entered, for a sync nothing needs. The only escape was deleting the rows
  instead of unticking them.

  Validate() has always counted ticks -- its needsCloud line -- so the two halves of the same window
  disagreed about whether there was any cloud work to do.

  The window is built for real here rather than through GetUninitializedObject, because the constructor's
  event wiring is precisely what is under test. Every service it needs is an inert double; the template and
  settings stores are pointed at a temp directory so nothing touches the operator's real data.

  Run with:  pwsh -NoProfile -File ./app/build/test-newuser-sync.ps1
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

$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("udm-newuser-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $sandbox | Out-Null

$NewUserVm = [UnifiedDirectoryManager.ViewModels.NewUserViewModel]
$GroupRow  = [UnifiedDirectoryManager.ViewModels.TemplateCopyGroupRow]
$Channel   = [UnifiedDirectoryManager.Models.GroupChannel]

function New-Window {
    $directory = [UnifiedDirectoryManager.TestSupport.FakeDirectoryService]::new()
    $templates = [UnifiedDirectoryManager.Services.TemplateStore]::new((Join-Path $sandbox 'templates'))
    $dialogs   = [UnifiedDirectoryManager.TestSupport.InertDialogService]::new()
    $graph     = [UnifiedDirectoryManager.TestSupport.InertGraphService]::new()
    $exchange  = [UnifiedDirectoryManager.TestSupport.InertExchangeService]::new()
    $entraSync = [UnifiedDirectoryManager.Services.EntraSyncService]::new($null)
    $settingsStore = [UnifiedDirectoryManager.Services.SettingsStore]::new((Join-Path $sandbox 'settings'))
    $settings  = [UnifiedDirectoryManager.Services.AppSettings]::new()
    $cloud = [UnifiedDirectoryManager.Services.CloudProvisioningService]::new($graph, $exchange, $entraSync, $settingsStore)
    return $NewUserVm::new($directory, $templates, $dialogs, $graph, $cloud, $settings)
}

# A seeded row, exactly as ApplyTemplate builds one. Include defaults to true, which is the whole setup.
function New-GroupRow([string]$name, [string]$id, $channel) {
    $row = $GroupRow::new()
    $GroupRow.GetProperty('Name').SetValue($row, $name)
    $GroupRow.GetProperty('Id').SetValue($row, $id)
    $GroupRow.GetProperty('Channel').SetValue($row, $channel)
    return $row
}

try {

Write-Host "`n== a seeded cloud group makes the sync mandatory ==" -ForegroundColor Cyan
$vm = New-Window
Check 'nothing selected to start with'  $false $vm.SyncMandatory
Check 'and the checkbox is usable'      $true  $vm.SyncCheckboxEnabled
Check 'with the sync off'               $false $vm.RunEntraSync

$cloudRow = New-GroupRow 'All Staff' 'g-1' $Channel::EntraGraph
$vm.CloudGroups.Add($cloudRow)
Check 'adding one makes it mandatory'   $true  $vm.SyncMandatory
Check 'which forces the sync on'        $true  $vm.RunEntraSync
Check 'and locks the checkbox'          $false $vm.SyncCheckboxEnabled

Write-Host "`n== unticking it releases the sync again (F24) ==" -ForegroundColor Cyan
# THE BUG. Include raises PropertyChanged on the ROW, not CollectionChanged on the list, so nothing
# re-evaluated this: the sync stayed forced on, the checkbox stayed locked, and CreateAsync then demanded an
# Entra Connect server for a sync with no cloud work to do. Deleting the row was the only way out.
$cloudRow.Include = $false
Check 'unticking clears mandatory'      $false $vm.SyncMandatory
Check 'the checkbox is usable again'    $true  $vm.SyncCheckboxEnabled
Check 'and the forced sync is released' $false $vm.RunEntraSync
# The row is still there to re-tick -- the section must not vanish with its last tick.
Check 'the row is still listed'         1 $vm.CloudGroups.Count
Check 'and its section still shows'     $true $vm.HasCloudGroups

# Re-ticking puts it back, so this is a live rule rather than a one-way latch.
$cloudRow.Include = $true
Check 're-ticking makes it mandatory'   $true $vm.SyncMandatory
Check 'and forces the sync again'       $true $vm.RunEntraSync

Write-Host "`n== distribution groups behave the same ==" -ForegroundColor Cyan
$vm = New-Window
$dlRow = New-GroupRow 'Sales DL' 'dl-1' $Channel::ExchangeOnline
$vm.DistributionGroups.Add($dlRow)
Check 'a distribution group is enough'  $true  $vm.SyncMandatory
$dlRow.Include = $false
Check 'and unticking releases it'       $false $vm.SyncMandatory
Check 'with the sync off again'         $false $vm.RunEntraSync

Write-Host "`n== a Temporary Access Pass forces it too, and lets go ==" -ForegroundColor Cyan
# A TAP needs the user in Entra first, so it forces the sync. It used to force it on and never release it.
$vm = New-Window
$vm.IssueTap = $true
Check 'a TAP makes the sync mandatory'  $true  $vm.SyncMandatory
Check 'and forces it on'                $true  $vm.RunEntraSync
$vm.IssueTap = $false
Check 'clearing the TAP releases it'    $false $vm.SyncMandatory
Check 'and turns the sync back off'     $false $vm.RunEntraSync

Write-Host "`n== the operator's own choice is not trampled ==" -ForegroundColor Cyan
# Someone may want the sync WITHOUT any cloud groups — a plain on-prem user they intend to sync now. If the
# rule later applies and then stops, their tick has to survive: only a sync the RULE switched on is switched
# back off.
$vm = New-Window
$vm.RunEntraSync = $true                       # ticked by hand, with nothing selected
$row = New-GroupRow 'All Staff' 'g-1' $Channel::EntraGraph
$vm.CloudGroups.Add($row)
Check 'the rule now applies'            $true $vm.SyncMandatory
$row.Include = $false
Check 'and stops applying'              $false $vm.SyncMandatory
Check 'but their tick survives'         $true  $vm.RunEntraSync
Check 'and the checkbox is theirs again' $true $vm.SyncCheckboxEnabled

Write-Host "`n== several rows: the last tick is what matters ==" -ForegroundColor Cyan
$vm = New-Window
$a = New-GroupRow 'A' 'g-a' $Channel::EntraGraph
$b = New-GroupRow 'B' 'g-b' $Channel::EntraGraph
$vm.CloudGroups.Add($a); $vm.CloudGroups.Add($b)
$a.Include = $false
Check 'one of two still counts'         $true  $vm.SyncMandatory
$b.Include = $false
Check 'both unticked does not'          $false $vm.SyncMandatory
$a.Include = $true
Check 'and one is enough to bring back' $true  $vm.SyncMandatory

Write-Host "`n== removing a row stops it being watched ==" -ForegroundColor Cyan
# A row taken out of the list must not go on driving the window from outside it.
$vm = New-Window
$gone = New-GroupRow 'Removed' 'g-x' $Channel::EntraGraph
$vm.CloudGroups.Add($gone)
$vm.CloudGroups.Remove($gone) | Out-Null
Check 'an empty list is not mandatory'  $false $vm.SyncMandatory
$gone.Include = $false
$gone.Include = $true
Check 'and a detached row changes nothing' $false $vm.SyncMandatory

Write-Host "`n== the window agrees with itself (mutation check) ==" -ForegroundColor Cyan
# Validate()'s needsCloud has always counted ticks. SyncMandatory counting ROWS is what made the two halves
# of the same window disagree about whether there was any cloud work at all.
$src = Get-Content -Raw (Join-Path $repoRoot 'app\src\UnifiedDirectoryManager\ViewModels\NewUserViewModel.cs')
$mandatory = [regex]::Match($src, '(?s)public bool SyncMandatory =>.*?;').Value
Check 'SyncMandatory was found'          $true ($mandatory.Length -gt 0)
Check 'it counts ticked rows'            $true ($mandatory -match 'Any\(g => g\.Include\)')
Check 'not rows present'                 $false ($mandatory -match 'CloudGroups\.Count > 0')
Check 'and an Include toggle re-runs it' $true ($src -match 'nameof\(TemplateCopyGroupRow\.Include\)')

}
finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
