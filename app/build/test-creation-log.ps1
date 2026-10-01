<#
.SYNOPSIS
  Tests the creation record — the steps New User and Copy User can now copy or save — and the rule that
  keeps a password or a Temporary Access Pass out of it.

.DESCRIPTION
  The progress pane lists each step as its own read-only TextBox, so a line can be selected but a drag
  cannot cross lines. Copying the whole thing therefore needs a button, and saving it needs a writer. Both
  come from one shared builder so New User and Copy User cannot drift apart, which has already happened
  once in this codebase to the naming-token resolver.

  The assertion that matters most here is a negative one. This feature writes what the operator saw to a
  file on disk, and what they saw is one step away from a freshly generated password and a Temporary Access
  Pass. Neither is in the steps today -- the password step says only that it was set, and the TAP reporter
  says only that one was issued -- and there is a check below that keeps it that way, because the day
  somebody adds Step($"Password: {Password}") nothing else would notice.

  Run with:  pwsh -NoProfile -File ./app/build/test-creation-log.ps1
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

$Log = [UnifiedDirectoryManager.Services.OperationLog]
$stamp = [datetime]::new(2026, 9, 30, 14, 22, 11)

function Build([string]$operation, [string]$subject, $by, [string[]]$steps, $outcome) {
    $list = [System.Collections.Generic.List[string]]::new()
    foreach ($s in $steps) { $list.Add($s) }
    return $Log::BuildCreationRecord($operation, $subject, $by, $stamp, $list, $outcome)
}

Write-Host "`n== the record names what was done, to whom, by whom, and when ==" -ForegroundColor Cyan
$record = Build 'New user' 'CN=Jane Doe,OU=Sales,DC=contoso,DC=net' 'CONTOSO\svc-admin' `
    @('✓ Created CN=Jane Doe,OU=Sales,DC=contoso,DC=net', '✓ Password set.', 'Done.') `
    'User created; added to 2 cloud group(s).'

Check 'it says what it is'          $true ($record -like '*Unified Directory Manager*creation record*')
Check 'the operation'               $true ($record -like '*Operation*New user*')
Check 'the account'                 $true ($record -like '*Account*CN=Jane Doe,OU=Sales,DC=contoso,DC=net*')
Check 'who did it'                  $true ($record -like '*Performed by*CONTOSO\svc-admin*')
Check 'and the outcome'             $true ($record -like '*Outcome*added to 2 cloud group(s)*')
# Every step, in order, exactly as the operator saw it -- the point of the feature.
Check 'the first step is there'     $true ($record -like '*✓ Created CN=Jane Doe*')
Check 'the middle one'              $true ($record -like '*✓ Password set.*')
Check 'and the last'                $true ($record -like '*Done.*')
$lines = $record -split "`r?`n"
$createdAt = [array]::IndexOf($lines, '✓ Password set.')
$doneAt = [array]::IndexOf($lines, 'Done.')
Check 'and they keep their order'   $true ($createdAt -lt $doneAt -and $createdAt -gt 0)

Write-Host "`n== the timestamp does not depend on the workstation's calendar ==" -ForegroundColor Cyan
# The same trap F23 records in ScenarioRunner: a workstation whose default calendar is Buddhist stamps
# 2569 rather than 2026, and this is a record that gets filed against a ticket and read elsewhere.
$was = [System.Threading.Thread]::CurrentThread.CurrentCulture
try {
    [System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::new('th-TH')
    $thai = Build 'New user' 'jdoe' 'admin' @('step') 'done'
    Check 'the date is Gregorian'   $true ($thai -like '*2026-09-30 14:22:11*')
    Check 'and not the Buddhist year' $false ($thai -like '*2569*')
    $name = $Log::SuggestFileName('new-user', 'jdoe', $stamp)
    Check 'and so is the file name' 'new-user-jdoe-20260930-142211.log' $name
}
finally { [System.Threading.Thread]::CurrentThread.CurrentCulture = $was }

Write-Host "`n== nothing is silently left blank ==" -ForegroundColor Cyan
# A record with an empty middle reads like a truncated file. Say what is missing instead.
$empty = Build 'New user' '' $null @() $null
Check 'no steps says so'            $true ($empty -like '*no steps were recorded*')
Check 'an unnamed account says so'  $true ($empty -like '*Account*(not named)*')
Check 'an unknown operator too'     $true ($empty -like '*Performed by*(unknown)*')
Check 'and a missing outcome'       $true ($empty -like '*Outcome*(none recorded)*')

Write-Host "`n== the file name is safe to write ==" -ForegroundColor Cyan
# A display name can hold anything; a logon name usually cannot, but the fallback is a typed full name.
$awkward = $Log::SuggestFileName('copy-user', 'Doe, Jane / R&D:test', $stamp)
Check 'invalid characters are gone' $false ($awkward -match '[\\/:*?"<>|]')
Check 'it keeps the prefix'         $true  ($awkward -like 'copy-user-*')
Check 'and the extension'           $true  ($awkward -like '*.log')
Check 'an empty subject still works' $true (($Log::SuggestFileName('new-user', '', $stamp)).Length -gt 0)

Write-Host "`n== New User: copy and save are driven by the same text ==" -ForegroundColor Cyan
$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("udm-log-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $sandbox | Out-Null
try {
    $NewUserVm = [UnifiedDirectoryManager.ViewModels.NewUserViewModel]
    $NonPublic = [System.Reflection.BindingFlags]'NonPublic,Instance'

    function New-Window {
        $directory = [UnifiedDirectoryManager.TestSupport.FakeDirectoryService]::new()
        $templates = [UnifiedDirectoryManager.Services.TemplateStore]::new((Join-Path $sandbox 'templates'))
        $dialogs   = [UnifiedDirectoryManager.TestSupport.InertDialogService]::new()
        $graph     = [UnifiedDirectoryManager.TestSupport.InertGraphService]::new()
        $exchange  = [UnifiedDirectoryManager.TestSupport.InertExchangeService]::new()
        $entraSync = [UnifiedDirectoryManager.Services.EntraSyncService]::new($null)
        $store     = [UnifiedDirectoryManager.Services.SettingsStore]::new((Join-Path $sandbox 'settings'))
        $settings  = [UnifiedDirectoryManager.Services.AppSettings]::new()
        $settings.OperationLogDirectory = (Join-Path $sandbox 'oplogs')
        $cloud = [UnifiedDirectoryManager.Services.CloudProvisioningService]::new($graph, $exchange, $entraSync, $store)
        $vm = $NewUserVm::new($directory, $templates, $dialogs, $graph, $cloud, $settings)
        return [pscustomobject]@{ Vm = $vm; Dialogs = $dialogs }
    }

    $step = $NewUserVm.GetMethod('Step', $NonPublic)
    $w = New-Window
    Check 'nothing to log before anything runs' $false $w.Vm.HasLog
    Check 'and the save command is disabled'    $false $w.Vm.SaveLogCommand.CanExecute($null)

    $w.Vm.FirstName = 'Jane'
    $w.Vm.LastName = 'Doe'
    $step.Invoke($w.Vm, @('✓ Created CN=Jane Doe,OU=Sales,DC=contoso,DC=net'))
    $step.Invoke($w.Vm, @('✓ Password set.'))
    $w.Vm.Status = 'Created CN=Jane Doe,OU=Sales,DC=contoso,DC=net.'

    Check 'a step enables the log'              $true $w.Vm.HasLog
    Check 'and the save command'                $true $w.Vm.SaveLogCommand.CanExecute($null)
    Check 'the text carries the steps'          $true ($w.Vm.LogText -like '*✓ Password set.*')
    Check 'and names the operation'             $true ($w.Vm.LogText -like '*New user*')

    Write-Host "`n== Save log writes the file the operator chose ==" -ForegroundColor Cyan
    $target = Join-Path $sandbox 'chosen.log'
    $w.Dialogs.SaveFilePath = $target
    $w.Vm.SaveLogCommand.Execute($null)

    Check 'the file exists'                     $true (Test-Path $target)
    $written = Get-Content -LiteralPath $target -Raw
    Check 'holding the same text'               $true ($written -like '*✓ Password set.*')
    Check 'and the status says where'           $true ($w.Vm.Status -like "*$target*")

    # What was OFFERED matters as much as what was written: the default lands beside scenario logs and
    # deleted-group records, and is named after the person rather than their distinguished name.
    $prompt = $w.Dialogs.SaveFilePrompts[0]
    Check 'one prompt was shown'                1 $w.Dialogs.SaveFilePrompts.Count
    # A C# tuple's field names are compile-time only; at runtime PowerShell sees Item1/Item2/Item3.
    Check 'defaulting to the log folder'        (Join-Path $sandbox 'oplogs') $prompt.Item3
    Check 'named for the account'               $true ($prompt.Item2 -like 'new-user-Jane Doe-*')
    Check 'not for its distinguished name'      $false ($prompt.Item2 -like '*OU=Sales*')

    Write-Host "`n== cancelling the save writes nothing, and says nothing ==" -ForegroundColor Cyan
    $w2 = New-Window
    $step.Invoke($w2.Vm, @('✓ Created CN=Bob,OU=X,DC=y,DC=z'))
    $w2.Vm.Status = 'Created.'
    # PowerShell turns $null into an empty string on a [string] property, which is a happy accident here:
    # it is exactly the shape a cancel arrives in, and the first version of the guard only checked for null.
    $w2.Dialogs.SaveFilePath = $null          # the operator closed the dialog
    $w2.Vm.SaveLogCommand.Execute($null)
    Check 'a prompt was still offered'          1 $w2.Dialogs.SaveFilePrompts.Count
    Check 'the status is untouched'             'Created.' $w2.Vm.Status
    Check 'and no stray file appeared'          1 (@(Get-ChildItem $sandbox -Filter '*.log' -File).Count)
}
finally { Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host "`n== no secret ever reaches a progress step ==" -ForegroundColor Cyan
# THE ASSERTION THIS FILE EXISTS FOR. These steps are now written to disk. The password step says only
# that the password was set, and CloudProvisioningService's TAP reporter says only that a pass was issued
# -- neither value is interpolated anywhere. A future Step($"...{Password}") would be a silent, serious
# leak, so the shape is pinned here rather than trusted.
$secrets = 'Password', 'GeneratedPassword', 'TapCode', 'SyncPassword'
foreach ($file in 'ViewModels\NewUserViewModel.cs', 'ViewModels\CopyUserViewModel.cs', 'Services\CloudProvisioningService.cs') {
    $src = Get-Content -Raw (Join-Path $repoRoot (Join-Path 'app\src\UnifiedDirectoryManager' $file))
    $short = Split-Path -Leaf $file
    # Every Step(...) / report(...) argument on one line, which is how all of them are written.
    $emits = [regex]::Matches($src, '(?m)^\s*(?:Step|report)\((.*)$')
    Check "  $short has progress steps to check" $true ($emits.Count -gt 0)
    foreach ($secret in $secrets) {
        $leaks = @($emits | Where-Object { $_.Groups[1].Value -match ('\{[^}]*\b' + $secret + '\b') })
        Check "  $short never emits $secret" 0 $leaks.Count
    }
}

Write-Host "`n== both windows share one builder (mutation check) ==" -ForegroundColor Cyan
# The drift this codebase has already suffered once. Two copies of the record format would diverge the same
# way Copy User's naming-token resolver did.
foreach ($file in 'NewUserViewModel.cs', 'CopyUserViewModel.cs') {
    $src = Get-Content -Raw (Join-Path $repoRoot (Join-Path 'app\src\UnifiedDirectoryManager\ViewModels' $file))
    Check "  $file uses the shared builder" $true ($src -match 'OperationLog\.BuildCreationRecord\(')
    Check "  and the shared file namer"     $true ($src -match 'OperationLog\.SuggestFileName\(')
    Check "  and the shared log folder"     $true ($src -match 'OperationLog\.ResolveDirectory\(')
}

Write-Host "`n== the record says what the account was created with ==" -ForegroundColor Cyan
# The record used to say an account was created and not WHAT it was created with, so checking a mistake
# against it meant opening the account in another tool.
$describe = $Log.GetMethod('DescribeAttributes')
function Describe([hashtable]$pairs) {
    $d = [System.Collections.Generic.Dictionary[string, string]]::new()
    foreach ($k in $pairs.Keys) { $d[$k] = $pairs[$k] }
    $box = [object[]]::new(1); $box[0] = $d
    # The leading comma is load-bearing. Without it PowerShell unrolls a ONE-line result back to a
    # bare string, whose .Count is also 1 and whose [0] is its first CHARACTER -- so the no-attributes
    # case silently compares a bullet against the message and the count assertion passes anyway.
    return ,@($describe.Invoke($null, $box))
}

$lines = Describe @{ sAMAccountName = 'jdoe'; displayName = 'Jane Doe'; mail = 'jane.doe@contoso.net' }
Check 'it counts them'                  $true ($lines[0] -like '*Attributes set (3)*')
Check '  and lists every one'           4 $lines.Count
Check '  with the lDAP name'            $true (($lines -join "`n") -like '*sAMAccountName*')
Check '  and the value'                 $true (($lines -join "`n") -like '*jdoe*')
# lDAPDisplayNames, not friendly labels: this gets filed against a ticket, and sAMAccountName is the name
# whoever picks it up can act on.
Check '  not the friendly label'        $false (($lines -join "`n") -like '*Logon name*')
# Sorted, so two records of the same account can be compared line by line.
Check '  sorted by name'                $true ($lines[1] -like '*displayName*')
Check '  values line up'                $true ($lines[1] -match 'displayName\s{4,}Jane Doe')

Check 'nothing set says so'             $true ((Describe @{})[0] -like '*No attributes were set*')
Check '  rather than printing a header' 1 (Describe @{}).Count
$empty = Describe @{ title = '' }
Check 'an empty value is marked'        $true (($empty -join "`n") -like '*(empty)*')

Write-Host "`n== and never says what the password was ==" -ForegroundColor Cyan
# THE REASON THIS FEATURE NEEDED A GUARD OF ITS OWN. The rule keeping secrets out of the record is
# enforced elsewhere by GREPPING source for Step($"...{Password}..."). Writing out a whole dictionary
# walks straight past that check: the attribute names live in data, not in source, so a grep sees
# nothing. The guard has to be where the values are.
#
# Nothing can put a password in this dictionary today -- CreateUserAsync takes it as a separate argument
# -- so this is a guard against a future change, which is exactly the kind that arrives unnoticed.
$isSecret = $Log.GetMethod('IsSecretAttribute')
function Secret([string]$name) { $box = [object[]]::new(1); $box[0] = $name; return $isSecret.Invoke($null, $box) }
foreach ($name in 'unicodePwd', 'userPassword', 'dBCSPwd', 'lmPwdHistory', 'ntPwdHistory',
                  'supplementalCredentials', 'msDS-ManagedPassword') {
    Check "  $name is a secret"          $true (Secret $name)
}
# The net under the list, so an attribute nobody thought of is redacted rather than printed.
foreach ($name in 'myCustomPassword', 'legacyPwdField', 'clientSecret', 'storedCredential') {
    Check "  so is $name"                $true (Secret $name)
}
Check '  and it is case-insensitive'    $true (Secret 'UNICODEPWD')
foreach ($name in 'sAMAccountName', 'displayName', 'mail', 'department', 'title') {
    Check "  $name is not"               $false (Secret $name)
}
Check '  nor is a blank name'           $false (Secret '')

$leaky = Describe @{ sAMAccountName = 'jdoe'; unicodePwd = 'Brave-Tiger_Maple-7kR2m'; myCustomPassword = 'hunter2' }
$text = $leaky -join "`n"
Check 'the secret VALUE never appears'  $false ($text -like '*Brave-Tiger_Maple-7kR2m*')
Check '  nor the improvised one'        $false ($text -like '*hunter2*')
# The attribute is still listed, so the record shows that something was set rather than hiding it.
Check '  but the attribute is listed'   $true ($text -like '*unicodePwd*')
Check '  marked as withheld'            $true ($text -like '*(not recorded)*')
Check '  and the rest is intact'        $true ($text -like '*jdoe*')

Write-Host "`n== both creation windows record them ==" -ForegroundColor Cyan
# Same reason the two share the record builder: a record that lists attributes for one window and not the
# other is the drift that assertion exists to prevent, one level up.
foreach ($file in 'NewUserViewModel.cs', 'CopyUserViewModel.cs') {
    $src = Get-Content -Raw (Join-Path $repoRoot (Join-Path 'app\src\UnifiedDirectoryManager\ViewModels' $file))
    Check "  $file describes them"       $true ($src -match 'OperationLog\.DescribeAttributes\(')
    # After the create, not before: until it succeeds these are a proposal, not a record.
    $created = $src.IndexOf('Created {result.DistinguishedName}')
    $described = $src.IndexOf('OperationLog.DescribeAttributes(')
    Check "  $file records them after"   $true ($described -gt $created -and $created -gt 0)
}

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
