<#
.SYNOPSIS
  Checks the Exchange Online PowerShell host script that is embedded in ExchangeService.cs.

.DESCRIPTION
  The HostScript literal is compiled into the app as a raw string, so nothing checks it: no syntax
  checking, no IntelliSense, no compiler. A typo in it only surfaces at runtime, against a live tenant,
  as a failed operation. This script closes part of that gap without needing a tenant:

    1. Extracts the literal and parses it with the PowerShell parser (catches syntax errors).
    2. Unit-tests __ownerNames, the owner projection, against a stubbed Get-Recipient.
    3. Unit-tests __newDgParams, the New-DistributionGroup splat. Every value it decides is create-time
       only, so a mistake there cannot be corrected on the group afterwards.

  Run it after editing HostScript.

.EXAMPLE
  pwsh -NoProfile -File ./build/test-hostscript.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$serviceFile = Join-Path $root 'src/UnifiedDirectoryManager/Services/ExchangeService.cs'
if (-not (Test-Path $serviceFile)) { throw "Could not find $serviceFile" }

# --- extract the raw string literal ------------------------------------------------------------
# C# strips the closing delimiter's indentation from every line of a raw string literal; do the same
# so the extracted script matches what the app actually writes to disk.
$src = Get-Content $serviceFile
$startIdx = ($src | Select-String -Pattern 'private const string HostScript = """' | Select-Object -First 1).LineNumber
if (-not $startIdx) { throw 'Could not locate the HostScript literal.' }
$endIdx = $null
for ($i = $startIdx; $i -lt $src.Count; $i++) { if ($src[$i] -match '^\s*"""\s*;?\s*$') { $endIdx = $i + 1; break } }
if (-not $endIdx) { throw 'Could not locate the end of the HostScript literal.' }
$indent = ($src[$endIdx - 1] -replace '"""\s*;?\s*$', '')
$lines = $src[$startIdx..($endIdx - 2)] | ForEach-Object {
    if ($_.Length -ge $indent.Length) { $_.Substring($indent.Length) } else { $_.TrimStart() }
}
$hostScript = $lines -join "`n"

$pass = 0
$fail = 0
function Check([string]$name, $expected, $actual) {
    if ($expected -eq $actual) { $script:pass++; Write-Host "  PASS  $name" -ForegroundColor Green }
    else {
        $script:fail++
        Write-Host "  FAIL  $name" -ForegroundColor Red
        Write-Host "          expected: $expected"
        Write-Host "          actual:   $actual"
    }
}

# --- 1. syntax ------------------------------------------------------------------------------------
Write-Host "`n== HostScript syntax ($($lines.Count) lines) ==" -ForegroundColor Cyan
$errors = $null
$tokens = $null
[void][System.Management.Automation.Language.Parser]::ParseInput($hostScript, [ref]$tokens, [ref]$errors)
if ($errors -and $errors.Count -gt 0) {
    $fail++
    Write-Host "  FAIL  parses without errors ($($errors.Count) error(s))" -ForegroundColor Red
    $errors | Select-Object -First 10 | ForEach-Object {
        Write-Host "          line $($_.Extent.StartLineNumber): $($_.Message)"
    }
} else {
    $pass++
    Write-Host "  PASS  parses without errors" -ForegroundColor Green
}

# --- 2. __ownerNames ------------------------------------------------------------------------------
# Load just the helper: the rest of the host script connects to Exchange when it runs.
$fnStart = ($hostScript -split "`n" | Select-String -Pattern '^\$script:__ownerCache' | Select-Object -First 1).LineNumber
if (-not $fnStart) { throw 'Could not locate the owner-projection block (has HostScript been reformatted?).' }
$all = $hostScript -split "`n"
$fnEnd = $null
for ($i = $fnStart; $i -lt $all.Count; $i++) { if ($all[$i] -eq '}') { $fnEnd = $i; break } }
if (-not $fnEnd) { throw 'Could not locate the end of the __ownerNames function.' }
$fnText = ($all[($fnStart - 1)..$fnEnd]) -join "`n"
# Fail loudly if the extraction silently grabbed the wrong text: these tests are worthless if they run
# against a fragment that happens to parse.
if ($fnText -notmatch 'function __ownerNames') { throw 'Extracted block does not contain __ownerNames.' }
Invoke-Expression $fnText
if (-not (Get-Command __ownerNames -ErrorAction SilentlyContinue)) { throw '__ownerNames was not defined by the extracted block.' }

# Stubbed Exchange. [CmdletBinding()] supplies -ErrorAction; declaring it explicitly alongside a
# [Parameter()] attribute makes the binding fail, and the failure would be swallowed by the catch
# under test — producing false passes.
$script:lookups = 0
$KNOWN = @{ '11111111-1111-1111-1111-111111111111' = 'Dana Scully' }

# When $true, the stub reproduces the documented Exchange behaviour that a NON-EXISTENT -Identity returns
# every recipient instead of erroring — the trap that would otherwise label a deleted owner with an
# unrelated person's name.
$script:stubReturnsEverything = $false

function Get-Recipient {
    [CmdletBinding()]
    param([string]$Identity)
    $script:lookups++
    if ($KNOWN.ContainsKey($Identity)) {
        return [pscustomobject]@{ DisplayName = $KNOWN[$Identity]; Guid = $Identity; ExternalDirectoryObjectId = $Identity }
    }
    if ($script:stubReturnsEverything) {
        return @(
            [pscustomobject]@{ DisplayName = 'Totally Unrelated Person'; Guid = '99999999-9999-9999-9999-999999999999'; ExternalDirectoryObjectId = '99999999-9999-9999-9999-999999999999' }
            [pscustomobject]@{ DisplayName = 'Someone Else'; Guid = '88888888-8888-8888-8888-888888888888'; ExternalDirectoryObjectId = '88888888-8888-8888-8888-888888888888' }
        )
    }
    throw "The operation couldn't be performed because object '$Identity' couldn't be found."
}

Write-Host "`n== __ownerNames: owner shapes ==" -ForegroundColor Cyan
$script:__ownerCache = @{}; $script:__ownerBudget = 50; $script:lookups = 0
Check 'canonical name -> display name' 'Jane Doe' ((__ownerNames @('contoso.onmicrosoft.com/Users/Jane Doe')) -join '; ')
# Exchange Online replaces the Name of a synced recipient with its directory object id, so this is the
# COMMON shape in a real tenant — and the one an earlier version returned as a bare GUID.
Check 'canonical name ending in GUID resolved' 'Dana Scully' ((__ownerNames @('contoso.onmicrosoft.com/Users/11111111-1111-1111-1111-111111111111')) -join '; ')
Check 'display name containing / kept whole' 'Sales/Marketing Owners' ((__ownerNames @('Sales/Marketing Owners')) -join '; ')
Check 'role group passes through' 'Organization Management' ((__ownerNames @('Organization Management')) -join '; ')
Check 'resolvable GUID -> display name' 'Dana Scully' ((__ownerNames @('11111111-1111-1111-1111-111111111111')) -join '; ')
Check 'deleted GUID -> labelled' 'Unresolved owner (22222222-2222-2222-2222-222222222222)' ((__ownerNames @('22222222-2222-2222-2222-222222222222')) -join '; ')
Check 'braced GUID normalised' 'Dana Scully' ((__ownerNames @('{11111111-1111-1111-1111-111111111111}')) -join '; ')
Check 'multiple owners joined' 'Jane Doe; Organization Management' ((__ownerNames @('contoso.onmicrosoft.com/Users/Jane Doe', 'Organization Management')) -join '; ')
Check 'blank entries dropped' 'Organization Management' ((__ownerNames @('', '  ', 'Organization Management')) -join '; ')
Check 'no owners -> empty' '' ((__ownerNames @()) -join '; ')
Check 'null collection -> empty' '' ((__ownerNames $null) -join '; ')

Write-Host "`n== __ownerNames: non-existent identity returns everything ==" -ForegroundColor Cyan
# Exchange documents that a non-existent -Identity returns ALL objects rather than erroring. Taking the
# first result would attribute a group to whoever happens to sort first in the tenant.
$script:__ownerCache = @{}; $script:__ownerBudget = 50; $script:lookups = 0
$script:stubReturnsEverything = $true
Check 'wrong-identity result rejected' 'Unresolved owner (44444444-4444-4444-4444-444444444444)' ((__ownerNames @('44444444-4444-4444-4444-444444444444')) -join '; ')
Check 'matching identity still accepted' 'Dana Scully' ((__ownerNames @('11111111-1111-1111-1111-111111111111')) -join '; ')
$script:stubReturnsEverything = $false

Write-Host "`n== __ownerNames: transient failure vs deleted owner ==" -ForegroundColor Cyan
# A throttling or permission error is a FAILED lookup, not a deleted owner. It must read differently and
# must not be cached, or one blip would be recorded as fact for the rest of the load.
function Get-Recipient {
    [CmdletBinding()]
    param([string]$Identity)
    $script:lookups++
    throw 'The server is busy. Micro delay applied.'
}
$script:__ownerCache = @{}; $script:__ownerBudget = 50; $script:lookups = 0
Check 'transient error labelled distinctly' 'Owner lookup failed (55555555-5555-5555-5555-555555555555)' ((__ownerNames @('55555555-5555-5555-5555-555555555555')) -join '; ')
Check 'transient error not cached' 0 $script:__ownerCache.Count
Check 'transient error recorded' 1 $script:__ownerErrors.Count

# restore the resolving stub for the remaining cost tests
function Get-Recipient {
    [CmdletBinding()]
    param([string]$Identity)
    $script:lookups++
    if ($KNOWN.ContainsKey($Identity)) {
        return [pscustomobject]@{ DisplayName = $KNOWN[$Identity]; Guid = $Identity; ExternalDirectoryObjectId = $Identity }
    }
    throw "The operation couldn't be performed because object '$Identity' couldn't be found."
}

Write-Host "`n== __ownerNames: lookup cost ==" -ForegroundColor Cyan
$script:__ownerCache = @{}; $script:__ownerBudget = 50; $script:lookups = 0
1..3 | ForEach-Object { $null = __ownerNames @('11111111-1111-1111-1111-111111111111') }
Check 'repeated GUID looked up once' 1 $script:lookups

$script:__ownerCache = @{}; $script:__ownerBudget = 2; $script:__ownerSkipped = 0; $script:lookups = 0
$res = __ownerNames (1..5 | ForEach-Object { "3333333$_-3333-3333-3333-333333333333" })
Check 'lookups stop at the budget' 2 $script:lookups
Check 'over-budget owners show raw' '33333333-3333-3333-3333-333333333333' $res[2]
Check 'every owner still returned' 5 $res.Count
Check 'skipped owners counted for reporting' 3 $script:__ownerSkipped

# --- 3. value formatters --------------------------------------------------------------------------
# Exchange returns almost nothing in a shape that survives ConvertTo-Json, so these three flatten it. They
# feed the mailbox properties view, where a wrong answer is indistinguishable from a true one.
$fmtStart = ($all | Select-String -Pattern '^function __yn' | Select-Object -First 1).LineNumber
if (-not $fmtStart) { throw 'Could not locate the value formatters (has HostScript been reformatted?).' }
$lastStart = ($all | Select-String -Pattern '^function __isWanted' | Select-Object -First 1).LineNumber
if (-not $lastStart) { throw 'Could not locate __isWanted.' }
$fmtEnd = $null
for ($i = $lastStart; $i -lt $all.Count; $i++) { if ($all[$i] -eq '}') { $fmtEnd = $i; break } }
if (-not $fmtEnd) { throw 'Could not locate the end of __isWanted.' }
$fmtText = ($all[($fmtStart - 1)..$fmtEnd]) -join "`n"
if (($fmtText -split "`n")[-1] -ne '}') { throw 'Extraction of the formatters did not end on a closing brace.' }
if ($fmtText -match 'Import-Module|Connect-ExchangeOnline|__emit|<<<UDM-') {
    throw 'Extraction of the formatters over-ran into the host-script body.'
}
Invoke-Expression $fmtText
foreach ($fn in '__yn', '__dt', '__leaf', '__isWanted') {
    if (-not (Get-Command $fn -ErrorAction SilentlyContinue)) { throw "$fn was not defined by the extracted block." }
}

Write-Host "`n== value formatters ==" -ForegroundColor Cyan
# EXO V3 returns flags as the STRINGS 'True'/'False'; -not 'False' is $false, so the comparison must be on text.
Check '__yn: string True'  'Yes' (__yn 'True')
Check '__yn: string False' 'No'  (__yn 'False')
Check '__yn: real boolean' 'Yes' (__yn $true)
Check '__yn: empty'        'No'  (__yn '')
Check '__yn: null'         'No'  (__yn $null)
Check '__dt: empty stays empty' '' (__dt '')
Check '__dt: null stays empty'  '' (__dt $null)
Check '__dt: formats a date' '2026-03-04 09:07' (__dt ([datetime]'2026-03-04T09:07:00'))
# ADObjectId stringifies to a canonical name, which is not an address.
Check '__leaf: canonical name' 'Jane Doe' (__leaf 'contoso.onmicrosoft.com/Users/Jane Doe')
Check '__leaf: plain name kept' 'Default MRM Policy' (__leaf 'Default MRM Policy')
Check '__leaf: name with a slash kept' 'Sales/Marketing' (__leaf 'Sales/Marketing')
Check '__leaf: empty' '' (__leaf '')

Write-Host "`n== __isWanted: the returns-everything guard ==" -ForegroundColor Cyan
# A non-existent -Identity makes an Exchange Get- cmdlet return EVERY object, so "take the first" would show a
# stranger's mailbox under the selected person's name. Every identity form Exchange accepts must be recognised.
$mb = [pscustomobject]@{
    UserPrincipalName        = 'jane@contoso.com'
    PrimarySmtpAddress       = 'jane.doe@contoso.com'
    ExternalDirectoryObjectId = '11111111-1111-1111-1111-111111111111'
    Guid                     = '22222222-2222-2222-2222-222222222222'
    Alias                    = 'jdoe'
    Identity                 = 'contoso.com/Users/Jane Doe'
    Name                     = 'Jane Doe'
}
Check '__isWanted: matches UPN'        $true  (__isWanted $mb 'jane@contoso.com')
Check '__isWanted: matches SMTP'       $true  (__isWanted $mb 'jane.doe@contoso.com')
Check '__isWanted: matches object id'  $true  (__isWanted $mb '11111111-1111-1111-1111-111111111111')
Check '__isWanted: matches alias'      $true  (__isWanted $mb 'jdoe')
Check '__isWanted: braces normalised'  $true  (__isWanted $mb '{22222222-2222-2222-2222-222222222222}')
Check '__isWanted: rejects a stranger' $false (__isWanted $mb 'someone.else@contoso.com')
Check '__isWanted: rejects empty'      $false (__isWanted $mb '')
Check '__isWanted: rejects null'       $false (__isWanted $mb $null)

# --- 4. __newDgParams -----------------------------------------------------------------------------
# The New-DistributionGroup splat. Nothing it decides can be changed on the group afterwards, and none of
# it runs until it runs against a live tenant, so it is checked here instead.
$dgStart = ($all | Select-String -Pattern '^function __newDgParams' | Select-Object -First 1).LineNumber
if (-not $dgStart) { throw 'Could not locate __newDgParams (has HostScript been reformatted?).' }
$dgEnd = $null
for ($i = $dgStart; $i -lt $all.Count; $i++) { if ($all[$i] -eq '}') { $dgEnd = $i; break } }
if (-not $dgEnd) { throw 'Could not locate the end of __newDgParams.' }
$dgText = ($all[($dgStart - 1)..$dgEnd]) -join "`n"
# Guard the END of the block, not the start. A start check is tautological — the slice begins at the line the
# pattern just matched — and the failure that can actually happen is the opposite one: the closing-brace scan
# missing its target and over-running into the rest of the host script, which would still parse and still
# define the function, leaving the suite green while testing something else.
if (($dgText -split "`n")[-1] -ne '}') { throw 'Extraction of __newDgParams did not end on its closing brace.' }
if ($dgText -match 'Import-Module|Connect-ExchangeOnline|__emit|<<<UDM-') {
    throw 'Extraction of __newDgParams over-ran into the host-script body.'
}
Invoke-Expression $dgText
if (-not (Get-Command __newDgParams -ErrorAction SilentlyContinue)) { throw '__newDgParams was not defined by the extracted block.' }

# The host receives $r from ConvertFrom-Json, so build the same shape rather than a hashtable: a
# PSCustomObject answers a missing property with $null, which is what the function's guards are written for.
function NewR([hashtable]$over) {
    $base = @{
        name = 'Test Group'; type = 'Distribution'; alias = ''; description = ''
        owners = @(); members = @(); requireAuth = $true; hiddenMembership = $false
    }
    foreach ($k in $over.Keys) { $base[$k] = $over[$k] }
    return [pscustomobject]$base
}

Write-Host "`n== __newDgParams: required and omitted parameters ==" -ForegroundColor Cyan
$np = __newDgParams (NewR @{})
Check 'Name passed through'          'Test Group'   $np['Name']
Check 'Type passed through'          'Distribution' $np['Type']
Check 'ErrorAction is Stop'          'Stop'         $np['ErrorAction']
Check 'blank alias omitted'          $false         $np.ContainsKey('Alias')
Check 'blank description omitted'    $false         $np.ContainsKey('Description')
Check 'no owners -> no ManagedBy'    $false         $np.ContainsKey('ManagedBy')
Check 'no members -> no Members'     $false         $np.ContainsKey('Members')
Check 'hidden membership off -> key absent' $false  $np.ContainsKey('HiddenGroupMembershipEnabled')

$np = __newDgParams (NewR @{ alias = 'test-group'; description = 'A group' })
Check 'alias passed through'       'test-group' $np['Alias']
Check 'description passed through' 'A group'    $np['Description']
Check 'whitespace alias omitted'   $false       ((__newDgParams (NewR @{ alias = '   ' })).ContainsKey('Alias'))

Write-Host "`n== __newDgParams: external senders is a double negative ==" -ForegroundColor Cyan
# The operator-facing option is "allow mail from external senders"; Exchange's flag is its inverse and
# defaults to $true, i.e. a new group rejects ALL external mail. Flipping this would be invisible until
# someone outside the org mailed the list and got a bounce.
Check 'always sent, never left to the default' $true ($np.ContainsKey('RequireSenderAuthenticationEnabled'))
Check 'external senders OFF -> RequireSenderAuthenticationEnabled $true' `
    $true  (__newDgParams (NewR @{ requireAuth = $true }))['RequireSenderAuthenticationEnabled']
Check 'external senders ON  -> RequireSenderAuthenticationEnabled $false' `
    $false (__newDgParams (NewR @{ requireAuth = $false }))['RequireSenderAuthenticationEnabled']

Write-Host "`n== __newDgParams: principals ==" -ForegroundColor Cyan
$np = __newDgParams (NewR @{ owners = @('a@x.com', '', '  ', 'b@x.com'); members = @('c@x.com', '', 'd@x.com') })
Check 'owners -> ManagedBy, blanks dropped' 'a@x.com; b@x.com' (($np['ManagedBy']) -join '; ')
Check 'members -> Members, blanks dropped'  'c@x.com; d@x.com' (($np['Members']) -join '; ')
Check 'all-blank owners -> no ManagedBy'    $false ((__newDgParams (NewR @{ owners = @('', ' ') })).ContainsKey('ManagedBy'))

Write-Host "`n== __newDgParams: irreversible and type-specific settings ==" -ForegroundColor Cyan
Check 'hidden membership set when asked' $true (__newDgParams (NewR @{ hiddenMembership = $true }))['HiddenGroupMembershipEnabled']
# A mail-enabled security group is a security principal: Open join/depart is rejected for it, and the
# cmdlet documents 'Default value: None', meaning it sends nothing and the service decides. Pin it.
$np = __newDgParams (NewR @{ type = 'Security' })
Check 'security: MemberJoinRestriction pinned Closed'   'Closed' $np['MemberJoinRestriction']
Check 'security: MemberDepartRestriction pinned Closed' 'Closed' $np['MemberDepartRestriction']
$np = __newDgParams (NewR @{ type = 'Distribution' })
Check 'distribution: join restriction left alone'   $false $np.ContainsKey('MemberJoinRestriction')
Check 'distribution: depart restriction left alone' $false $np.ContainsKey('MemberDepartRestriction')

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
