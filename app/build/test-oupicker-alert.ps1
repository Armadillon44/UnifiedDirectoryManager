<#
.SYNOPSIS
  Regression tests for F28 (the multi-select OU picker ignored its seeded selection) and F27 (Alert threw
  instead of showing when no window existed).

.DESCRIPTION
  F28. Advanced Search's "pick OUs" hands the picker the search bases it already has. The picker consumed
  initialDns only in its single-select branch, so the multi-select window opened with nothing ticked and OK
  returned an empty list: the operator's existing scoping silently evaporated.

  Seeding the visible ticks alone would not have been enough. The tree loads LAZILY, and OK derived its
  answer by walking whatever happened to be loaded -- so a seeded OU three levels down, whose node had never
  been created, would have been dropped just the same. The selection is now held by the window and edited by
  ticking, and the seed is pushed into the tree so nodes arrive ticked whenever they do load.

  F27. Alert passed Owner! -- a null-forgiveness on a genuinely nullable value -- and MessageBox.Show throws
  ArgumentNullException on a null owner. Every other method there assigns `Owner = Owner`, which accepts
  null. Only Alert dereferenced, and the alerts that arrive with no window are the late async ones
  ("Password not set", an import failure, a sync problem) -- the messages an operator most needs.

  Alert itself cannot be driven from a test without showing a modal dialog, so what is exercised here is the
  Owner lookup it dereferenced, which is reachable and does throw under the old code. The branch inside
  Alert is asserted on the source; that limit is stated rather than implied.

  Run with:  pwsh -NoProfile -STA -File ./app/build/test-oupicker-alert.ps1
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

$Picker = [UnifiedDirectoryManager.Views.Dialogs.OuPickerWindow]
$NonPublic = [System.Reflection.BindingFlags]'NonPublic,Instance'
$onOk = $Picker.GetMethod('OnOk', $NonPublic)
$selectedField = $Picker.GetField('_selected', $NonPublic)
if ($null -eq $onOk) { throw 'OuPickerWindow has no OnOk — has it been refactored?' }
if ($null -eq $selectedField) { throw 'OuPickerWindow has no _selected — has it been refactored?' }

$ROOT = 'DC=contoso,DC=net'
$SALES = 'OU=Sales,DC=contoso,DC=net'
$WEST  = 'OU=West,OU=Sales,DC=contoso,DC=net'
$HR    = 'OU=HR,DC=contoso,DC=net'
$GONE  = 'OU=Archived,OU=Nowhere,DC=contoso,DC=net'  # a DN the tree will never produce

# A two-level domain: Sales (with West beneath it) and HR.
function New-Directory {
    $fake = [UnifiedDirectoryManager.TestSupport.FakeDirectoryService]::new()
    $fake.RootNode = $fake.AddOu($ROOT, 'contoso.net', $null, $true)
    [void]$fake.AddOu($SALES, 'Sales', $ROOT, $true)
    [void]$fake.AddOu($HR, 'HR', $ROOT, $false)
    [void]$fake.AddOu($WEST, 'West', $SALES, $false)
    return $fake
}

# The arguments are cast to the constructor's exact parameter types. PowerShell wraps a value that came
# through a function parameter in a PSObject, and overload resolution then cannot see the three-argument
# constructor at all -- it reports "cannot find an overload for the argument count 3" while looking
# straight at one.
function New-Picker($dirSvc, [string[]]$seed, [bool]$multi = $true) {
    $svc  = [UnifiedDirectoryManager.Services.IDirectoryService]$dirSvc
    $list = [System.Collections.Generic.IEnumerable[string]]$seed
    return $Picker::new($svc, $list, $multi)
}

# The tree loads children on expand, and the load is async. Pump the dispatcher until it settles.
function Settle {
    for ($i = 0; $i -lt 40; $i++) {
        [System.Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke(
            [action]{}, [System.Windows.Threading.DispatcherPriority]::ApplicationIdle)
        Start-Sleep -Milliseconds 15
    }
}

# OnOk assigns SelectedDns and THEN sets DialogResult, which WPF permits only on a window shown via
# ShowDialog. The assignment has already happened by the time that throws, so the exception is expected.
function Invoke-Ok($window) {
    try { $onOk.Invoke($window, @($null, $null)) } catch { }
    # The leading comma stops PowerShell unrolling a one-element array back into a bare string, which then
    # indexes by CHARACTER: $picked[0] came back as "O" rather than the distinguished name.
    return ,@($window.SelectedDns)
}

function Selection($window) { return ,@(($selectedField.GetValue($window)) | Sort-Object) }

function Find-Node($window, [string]$dn) {
    $stack = [System.Collections.Generic.Stack[object]]::new()
    foreach ($r in $window.RootNodes) { $stack.Push($r) }
    while ($stack.Count -gt 0) {
        $node = $stack.Pop()
        if ($node.DistinguishedName -eq $dn) { return $node }
        foreach ($c in $node.Children) { $stack.Push($c) }
    }
    return $null
}

Write-Host "`n== the seeded selection survives to OK (F28) ==" -ForegroundColor Cyan
# THE BUG: this used to come back empty, and Advanced Search's search bases went with it.
$fake = New-Directory
$win = New-Picker $fake @($SALES, $HR)
Settle
$picked = Invoke-Ok $win
Check 'OK returns the seeded OUs'      2 $picked.Count
Check 'including Sales'                $true ($picked -contains $SALES)
Check 'and HR'                         $true ($picked -contains $HR)

Write-Host "`n== and the tree shows them as ticked ==" -ForegroundColor Cyan
# Returning them without showing them would be its own lie: the operator could not tell what was scoped.
$salesNode = Find-Node $win $SALES
$hrNode = Find-Node $win $HR
Check 'the Sales node exists'          $true ($null -ne $salesNode)
Check 'and is ticked'                  $true $salesNode.IsChecked
Check 'the HR node is ticked too'      $true $hrNode.IsChecked

Write-Host "`n== a seed deeper than the loaded tree is still returned ==" -ForegroundColor Cyan
# The half that seeding the visible ticks alone would have missed. OU=West is two levels down and its node
# does not exist until Sales is expanded, so a walk of the loaded tree could never have found it.
$fake = New-Directory
$win = New-Picker $fake @($WEST)
Settle
Check 'West was never loaded'          $null (Find-Node $win $WEST)
$picked = Invoke-Ok $win
Check 'but OK still returns it'        1 $picked.Count
Check 'naming the right OU'            $WEST $picked[0]

Write-Host "`n== expanding to it finds it already ticked ==" -ForegroundColor Cyan
$salesNode = Find-Node $win $SALES
$salesNode.IsExpanded = $true
Settle
$westNode = Find-Node $win $WEST
Check 'West loads on expand'           $true ($null -ne $westNode)
Check 'and arrives already ticked'     $true $westNode.IsChecked
Check 'and is still the only selection' 1 (Selection $win).Count

Write-Host "`n== an OU the directory no longer has is not silently dropped ==" -ForegroundColor Cyan
# A saved search can name an OU that has since been renamed or deleted. Quietly discarding it would widen
# the search to the whole domain without saying so, which is the failure this picker keeps having.
$fake = New-Directory
$win = New-Picker $fake @($GONE)
Settle
$picked = Invoke-Ok $win
Check 'it comes back unchanged'        1 $picked.Count
Check 'exactly as it went in'          $GONE $picked[0]

Write-Host "`n== ticking and unticking still work ==" -ForegroundColor Cyan
# The negative control. A selection that only ever grows is not a picker.
$fake = New-Directory
$win = New-Picker $fake @($SALES)
Settle
$hrNode = Find-Node $win $HR
$hrNode.IsChecked = $true
Check 'ticking adds to the selection'  2 (Selection $win).Count
$salesNode = Find-Node $win $SALES
$salesNode.IsChecked = $false
Check 'unticking removes from it'      1 (Selection $win).Count
$picked = Invoke-Ok $win
Check 'and OK agrees'                  $HR $picked[0]

Write-Host "`n== an unloaded node is not treated as an untick ==" -ForegroundColor Cyan
# The trap in reconciling: a node that has not loaded says nothing about the operator's intent, and reading
# its absence as "cleared" is exactly how the seed used to disappear.
$fake = New-Directory
$win = New-Picker $fake @($WEST, $SALES)
Settle
$hrNode = Find-Node $win $HR
$hrNode.IsChecked = $true          # a tick anywhere triggers reconciliation
$hrNode.IsChecked = $false
Check 'the unloaded seed survives'     $true ((Selection $win) -contains $WEST)
Check 'along with the loaded one'      $true ((Selection $win) -contains $SALES)

Write-Host "`n== no seed means no selection ==" -ForegroundColor Cyan
$fake = New-Directory
$win = New-Picker $fake $null
Settle
Check 'nothing is selected'            0 (Selection $win).Count
Check 'and OK returns nothing'         0 (Invoke-Ok $win).Count

Write-Host "`n== single-select mode is unchanged ==" -ForegroundColor Cyan
# It always honoured its seed; the fix must not disturb that.
$fake = New-Directory
$win = New-Picker $fake @($SALES) $false
Settle
Check 'the seeded DN is preselected'   $SALES $win.SelectedDn
Check 'and multi-select is off'        $false $win.MultiSelect

Write-Host "`n== F27: a dialog owner is optional ==" -ForegroundColor Cyan
# There is no Application in this process, which is the state during teardown -- and the state in which
# Alert's `Owner!` threw before MessageBox ever saw the message.
$DialogService = [UnifiedDirectoryManager.Views.DialogService]
$ownerProp = $DialogService.GetProperty('Owner', [System.Reflection.BindingFlags]'NonPublic,Static')
if ($null -eq $ownerProp) { throw 'DialogService has no Owner — has it been refactored?' }
$threw = $null
$owner = $null
try { $owner = $ownerProp.GetValue($null) } catch { $threw = $_.Exception.GetBaseException().GetType().Name }
Check 'looking up an owner does not throw' $null $threw
Check 'it just reports there is none'      $null $owner

# Alert cannot be called here without putting a modal MessageBox on screen, so its branch is read instead.
$dsSrc = Get-Content -Raw (Join-Path $repoRoot 'app\src\UnifiedDirectoryManager\Views\DialogService.cs')
$alert = [regex]::Match($dsSrc, '(?s)public void Alert\(string title, string message\).*?\r?\n    \}').Value
Check 'Alert was found'                    $true  ($alert.Length -gt 0)
Check 'it no longer forgives a null owner' $false ($alert -match 'Owner!')
Check 'it shows a message either way'      2      ([regex]::Matches($alert, 'MessageBox\.Show')).Count
Check 'and branches on having one'         $true  ($alert -match 'Owner is \{ \} owner')

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
