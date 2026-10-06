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
$srcDir = Join-Path $repoRoot 'app\src\UnifiedDirectoryManager'
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

Write-Host "`n== how patiently to wait for the cloud to catch up ==" -ForegroundColor Cyan
# Creating a user on-prem then using it in the cloud is a race against replication. Until Entra and
# Exchange catch up they answer "cannot find it", meaning NOT YET rather than never. These numbers decide
# how long the app keeps asking, and an operator can now change them.
$Policy = [UnifiedDirectoryManager.Services.RetryPolicy]
function Pol([int]$attempts, [int]$wait) { return $Policy::new($attempts, $wait) }

Check 'the default is 10 attempts'      10 $Policy::Default.Attempts
Check '  10 seconds apart'              10 $Policy::Default.WaitSeconds
Check '  which is 90s of waiting'       90 $Policy::Default.TotalWait.TotalSeconds

# One gap FEWER than the attempt count: the last attempt is allowed to fail rather than being followed by
# another pause. Getting this off by one adds a whole wait to every failure.
Check 'waits are attempts minus one'    40 ((Pol 5 10).TotalWait.TotalSeconds)
Check '  and the floor holds'           20 ((Pol 5 5).TotalWait.TotalSeconds)

Write-Host "`n== out-of-range values are corrected, not rejected ==" -ForegroundColor Cyan
# These come from a settings file a newer build, a text editor or a bad merge may have put anything into.
# Refusing to provision a user because a number is wrong would be a worse failure than using a sane one.
Check 'too few attempts clamps up'      $Policy::MinAttempts ((Pol 1 10).Clamped().Attempts)
Check 'too many clamps down'            $Policy::MaxAttempts ((Pol 9999 10).Clamped().Attempts)
Check 'too short a wait clamps up'      $Policy::MinWaitSeconds ((Pol 10 1).Clamped().WaitSeconds)
Check 'too long clamps down'            $Policy::MaxWaitSeconds ((Pol 10 9999).Clamped().WaitSeconds)
# Zero means "never set" -- a fresh settings.json has no value at all. It must not disable retries.
Check 'zero means unset, not none'      $Policy::MinAttempts ((Pol 0 0).Clamped().Attempts)
Check '  for the wait too'              $Policy::MinWaitSeconds ((Pol 0 0).Clamped().WaitSeconds)
Check 'a negative cannot get through'   $Policy::MinAttempts ((Pol ([int]-5) ([int]-5)).Clamped().Attempts)
Check 'a value in range is untouched'   12 ((Pol 12 20).Clamped().Attempts)

Write-Host "`n== the ceiling is a deliberate number ==" -ForegroundColor Cyan
# 50 attempts was asked for; a five-minute gap was not kept. At the maximum attempt count that would be
# over four hours of waiting on a single group, which is not a setting so much as a way to lose an
# afternoon. A minute keeps the worst case under an hour and still rides out the lag this exists for.
Check 'the wait tops out at a minute'   60 $Policy::MaxWaitSeconds
Check '  so the worst case is under an hour' $true (((Pol 50 60).TotalWait.TotalMinutes) -lt 60)
Check '  and is 49 minutes exactly'     49 ((Pol 50 60).TotalWait.TotalMinutes)

Write-Host "`n== the cost in words, not in arithmetic ==" -ForegroundColor Cyan
# "50 attempts, 60 seconds apart" means nothing to read. Shown live as the numbers are typed, so a choice
# is never made without its consequence visible.
$h = $Policy.GetMethod('Humanise')
function Say([int]$seconds) { $b = [object[]]::new(1); $b[0] = [timespan]::FromSeconds($seconds); return $h.Invoke($null, $b) }
Check 'seconds read as seconds'         '45 seconds' (Say 45)
Check '  and one is singular'           '1 second' (Say 1)
Check '  zero is not negative'          '0 seconds' (Say 0)
Check 'a round minute has no seconds'   '2 min' (Say 120)
Check '  a ragged one does'             '1 min 30 s' (Say 90)
Check 'hours read as hours'             '1 hr 5 min' (Say 3900)
Check '  and a round hour is bare'      '2 hr' (Say 7200)

Write-Host "`n== Exchange is not Graph, and the figures say so ==" -ForegroundColor Cyan
# A Graph failure returns in well under a second, so an attempt costs only the wait. An Exchange call that
# HANGS costs the full 90-second operation budget first. One number for both would be too impatient for
# Exchange or needlessly slow for Entra, which is why they are separate settings.
Check 'a fast service costs the waits'  90 ((Pol 10 10).WorstCase(0).TotalSeconds)
Check '  a hanging one costs far more'  990 ((Pol 10 10).WorstCase(90).TotalSeconds)
Check '  which is 16 and a half minutes' '16 min 30 s' (Say 990)

Write-Host "`n== the settings carry it, and the services read it ==" -ForegroundColor Cyan
$Settings = [UnifiedDirectoryManager.Services.AppSettings]
$fresh = $Settings::new()
# A settings.json from before this feature has no values at all, and must not come back as zero retries.
Check 'a fresh settings file is valid'  $Policy::MinAttempts $fresh.EntraRetry.Attempts
Check '  for Exchange too'              $Policy::MinAttempts $fresh.ExchangeRetry.Attempts
$fresh.EntraRetryAttempts = 10; $fresh.EntraRetryWaitSeconds = 10
$fresh.ExchangeRetryAttempts = 25; $fresh.ExchangeRetryWaitSeconds = 30
Check 'Entra reads its own values'      10 $fresh.EntraRetry.Attempts
Check '  and Exchange its own'          25 $fresh.ExchangeRetry.Attempts
Check '  they do not share a knob'      $false ($fresh.EntraRetry.WaitSeconds -eq $fresh.ExchangeRetry.WaitSeconds)

$provSrc = Get-Content -Raw (Join-Path $srcDir 'Services\CloudProvisioningService.cs')
Check 'Entra groups use the Entra policy' $true ($provSrc -match 'var policy = CurrentSettings\.EntraRetry')
Check 'distribution groups use Exchange' $true ($provSrc -match 'var policy = CurrentSettings\.ExchangeRetry')
Check 'the TAP is a Graph write'        $true ($provSrc -match 'CurrentSettings\.EntraRetry, cancellationToken')
# Re-read per call, so changing the setting affects the very next creation with no restart.
Check 'settings are read per call'      $true ($provSrc -match 'private AppSettings CurrentSettings => _settingsStore\.Load\(\)')
Check '  and not cached in a field'     $false ($provSrc -match 'private readonly AppSettings')

Write-Host "`n== and the operator can stop it ==" -ForegroundColor Cyan
# A longer retry with no exit is a trap, not a setting. This is the failure the Entra Connect sync timeout
# was added to prevent: the only escape from a hung call was killing the app.
$retryBlock = [regex]::Match($provSrc, '(?s)private static async Task RetryWhileCatchingUpAsync.*?\n    \}').Value
Check 'the retry takes a token'         $true ($retryBlock -match 'CancellationToken cancellationToken')
Check '  it checks before each attempt' $true ($retryBlock -match 'cancellationToken\.ThrowIfCancellationRequested\(\)')
# A token covering only the attempts would leave someone who pressed Cancel watching a minute of delay.
Check '  and the WAIT is cancellable'   $true ($retryBlock -match 'Task\.Delay\(policy\.Wait, cancellationToken\)')
Check '  cancellation is never swallowed' $true ($retryBlock -match 'catch \(OperationCanceledException\) \{ throw; \}')

foreach ($vm in 'NewUserViewModel.cs', 'CopyUserViewModel.cs') {
    $src = Get-Content -Raw (Join-Path $srcDir (Join-Path 'ViewModels' $vm))
    Check "  $vm offers Cancel"          $true ($src -match '\[RelayCommand\(CanExecute = nameof\(CanCancelCloud\)\)\]')
    # Cancelling must report what DID happen: the account exists and earlier groups are still added.
    Check "  $vm says the account exists" $true ($src -match 'Cancelled\. The user was created')
    Check "  and clears the flag after"  $true ($src -match 'finally\s*\{\s*\r?\n\s*CanCancelCloud = false;')
}
foreach ($w in 'NewUserWindow.xaml', 'CopyUserWindow.xaml') {
    $x = Get-Content -Raw (Join-Path $srcDir (Join-Path 'Views\Dialogs' $w))
    Check "  $w has the button"          $true ($x -match 'CancelCloudCommand')
    # Hidden when there is nothing to stop.
    Check "  which hides when idle"      $true ($x -match 'CanCancelCloud, Converter=\{StaticResource BoolToVis\}')
}

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
