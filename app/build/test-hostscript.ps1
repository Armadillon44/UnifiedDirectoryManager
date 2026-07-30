<#
.SYNOPSIS
  Checks the Exchange Online PowerShell host script that is embedded in ExchangeService.cs.

.DESCRIPTION
  The HostScript literal is compiled into the app as a raw string, so nothing checks it: no syntax
  checking, no IntelliSense, no compiler. A typo in it only surfaces at runtime, against a live tenant,
  as a failed operation. This script closes part of that gap without needing a tenant:

    1. Extracts the literal and parses it with the PowerShell parser (catches syntax errors).
    2. Unit-tests __ownerNames, the owner projection, against a stubbed Get-Recipient.

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

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
