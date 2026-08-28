<#
.SYNOPSIS
  Checks the Exchange Online PowerShell host script that is embedded in ExchangeService.cs.

.DESCRIPTION
  The HostScript literal is compiled into the app as a raw string, so nothing checks it: no syntax
  checking, no IntelliSense, no compiler. A typo in it only surfaces at runtime, against a live tenant,
  as a failed operation. This script closes part of that gap without needing a tenant:

    1. Extracts the literal and parses it with the PowerShell parser (catches syntax errors).
    2. Unit-tests the recipient projection (__ownerNames, __ownerReset, __recipNames, __ownerDiag)
       against a stubbed Get-Recipient.
    3. Unit-tests __newDgParams, the New-DistributionGroup splat. Every value it decides is create-time
       only, so a mistake there cannot be corrected on the group afterwards.
    4. Checks the distribution group WRITE path: that the C# translation and the host allow-list name the
       same parameters, that every editable row has a write mapping, and that the op refuses a synced
       group, a mismatched identity and an unlisted parameter.

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

# --- 2. the recipient projection ------------------------------------------------------------------
# Load just the helpers: the rest of the host script connects to Exchange when it runs. The block is
# delimited by sentinels rather than by the first closing brace, so it can hold more than one function.
$all = $hostScript -split "`n"
$fnStart = ($all | Select-String -Pattern '^\$script:__ownerCache' | Select-Object -First 1).LineNumber
if (-not $fnStart) { throw 'Could not locate the recipient-projection block (has HostScript been reformatted?).' }
$fnEnd = ($all | Select-String -Pattern '^# --- end recipient projection' | Select-Object -First 1).LineNumber
if (-not $fnEnd) { throw 'Could not locate the end of the recipient-projection block.' }
if ($fnEnd -le $fnStart) { throw 'The recipient-projection sentinels are out of order.' }
$fnText = ($all[($fnStart - 1)..($fnEnd - 1)]) -join "`n"
# Fail loudly if the extraction silently grabbed the wrong text: these tests are worthless if they run
# against a fragment that happens to parse.
$RECIP_FNS = @('__ownerNames', '__ownerReset', '__recipNames', '__ownerDiag')
foreach ($f in $RECIP_FNS) { if ($fnText -notmatch "function $f") { throw "Extracted block does not contain $f." } }
if ($fnText -match 'Import-Module|Connect-ExchangeOnline|__emit|<<<UDM-') {
    throw 'Extraction of the recipient projection over-ran into the host-script body.'
}
Invoke-Expression $fnText
foreach ($f in $RECIP_FNS) {
    if (-not (Get-Command $f -ErrorAction SilentlyContinue)) { throw "$f was not defined by the extracted block." }
}

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

Write-Host "`n== __ownerReset: one place that cannot be half-done ==" -ForegroundColor Cyan
# The counters are script-scoped in a host process that outlives every op. Resetting only some of them
# leaves the next op reporting the previous one's degradation as its own.
$script:__ownerCache = @{ 'x' = 'y' }; $script:__ownerBudget = 3
$script:__ownerSkipped = 9; $script:__ownerErrors = @('boom')
__ownerReset 25
Check 'cache cleared'  0  $script:__ownerCache.Count
Check 'budget set'     25 $script:__ownerBudget
Check 'skipped zeroed' 0  $script:__ownerSkipped
Check 'errors cleared' 0  $script:__ownerErrors.Count

Write-Host "`n== __ownerDiag: silent unless something actually degraded ==" -ForegroundColor Cyan
# A stale count is worse than no count: it reports a degradation that did not happen on this read.
__ownerReset 25
Check 'clean read reports nothing' '' (__ownerDiag)
__ownerReset 0
$null = __ownerNames @('44444444-4444-4444-4444-444444444444')
Check 'budget exhaustion reported' $true ((__ownerDiag) -like 'recipient lookups skipped*: 1')
__ownerReset 25
$script:__ownerErrors = @('throttled', 'throttled', 'denied')
Check 'errors reported once each' $true ((__ownerDiag) -like '*throttled | denied')
__ownerReset 25
Check 'a reset clears what the last op left' '' (__ownerDiag)

Write-Host "`n== __recipNames: prefers what Exchange already resolved ==" -ForegroundColor Cyan
# Exchange returns these fields as bare GUIDs unless the matching -Include*WithDisplayNames switch is
# passed. When it is, resolving them again here would be a round trip per recipient for the same answer.
__ownerReset 25; $script:lookups = 0
Check 'display-name variant wins' 'Jane Doe' `
    ((__recipNames @('Jane Doe') @('11111111-1111-1111-1111-111111111111')) -join '; ')
Check 'and costs no lookup' 0 $script:lookups
Check 'falls back to the raw property' 'Dana Scully' `
    ((__recipNames @() @('11111111-1111-1111-1111-111111111111')) -join '; ')
Check 'the fallback did cost a lookup' 1 $script:lookups
Check 'both empty -> nothing' '' ((__recipNames @() @()) -join '; ')
Check 'both null -> nothing'  '' ((__recipNames $null $null) -join '; ')
# A display-name variant is not automatically human-readable: Exchange leaves entries it could not
# render as GUIDs, so whichever array is chosen still goes through the same resolution.
__ownerReset 25
Check 'a GUID inside the variant still resolves' 'Dana Scully' `
    ((__recipNames @('11111111-1111-1111-1111-111111111111') @()) -join '; ')

Write-Host "`n== every op that projects recipients resets the counters first ==" -ForegroundColor Cyan
# The regression this guards: an op that projects recipients without resetting reports the previous
# op's skips as its own. Finding the ops by parsing the switch means a new one is covered on arrival.
$opName = $null; $ops = [ordered]@{}
foreach ($line in $all) {
    if ($line -match "^\s{8,}'([a-z0-9-]+)' \{\s*`$") { $opName = $Matches[1]; $ops[$opName] = @() }
    elseif ($opName) { $ops[$opName] += $line }
}
$projecting = @($ops.Keys | Where-Object { ($ops[$_] -join "`n") -match '__ownerNames|__recipNames' })
Check 'the projecting ops were located' $true ($projecting.Count -ge 3)
foreach ($op in $projecting) {
    Check "$op resets the counters" $true (($ops[$op] -join "`n") -match '__ownerReset')
}

Write-Host "`n== get-dl-detail asks for every display-name property it reads ==" -ForegroundColor Cyan
# A *WithDisplayNames property comes back EMPTY unless its -Include* switch was passed on the same call,
# and __recipNames then silently falls back to the raw property — which Exchange fills with bare GUIDs.
# The pane would show object ids where it promises names, with nothing reporting a problem.
if (-not $ops.Contains('get-dl-detail')) { throw 'get-dl-detail was not found in the host script.' }
$dlBody = ($ops['get-dl-detail'] -join "`n")
$dlCalls = [regex]::Matches($dlBody, '__recipNames \$g\.([A-Za-z]+)WithDisplayNames \$g\.([A-Za-z]+)\)')
Check 'the recipient fields use the shared projection' $true ($dlCalls.Count -ge 5)
foreach ($c in $dlCalls) {
    $prop = $c.Groups[1].Value
    Check "${prop}: the fallback names the same property" $prop $c.Groups[2].Value
    Check "${prop}: the -Include switch is passed" $true `
        ($dlBody -match "Include${prop}WithDisplayNames\s*=\s*\`$true")
}
# Declaring the switches is not passing them: without the splat the hashtable is dead code and every
# recipient field quietly falls back to GUIDs.
$dlSplat = [regex]::Match($dlBody, '\$(\w+) = @{[^}]*IncludeManagedByWithDisplayNames')
Check 'the switches live in one splat hashtable' $true $dlSplat.Success
Check 'and that hashtable is splatted onto the read' $true `
    ($dlBody -match "Get-DistributionGroup[^`n]*@$($dlSplat.Groups[1].Value)\b")

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

# --- 5. the distribution group write path ------------------------------------------------------------
# Set-DistributionGroup is the only cmdlet in this host script that changes a group, and every guard around
# it (identity verification, the synced refusal, the parameter allow-list) is the difference between a
# refused edit and one applied to the wrong object.
$csText = $src -join "`n"

Write-Host "`n== the C# translation and the host allow-list agree ==" -ForegroundColor Cyan
# The parameter names are decided in C# and permitted in PowerShell. If the two lists drift, either a save
# fails for a reason unrelated to the edit, or the host permits something nothing produces.
$csParams = @()
$csParams += [regex]::Matches($csText, 'new\("([A-Za-z]+)", DlValue') | ForEach-Object { $_.Groups[1].Value }
$csParams += [regex]::Matches($csText, 'p\["([A-Za-z]+)"\] =') | ForEach-Object { $_.Groups[1].Value }
$csParams = @($csParams | Select-Object -Unique | Sort-Object)
Check 'the C# translation produces parameters' $true ($csParams.Count -ge 13)

$alStart = ($all | Select-String -Pattern '^\s*\$wAllowed = @\($' | Select-Object -First 1).LineNumber
if (-not $alStart) { throw 'Could not locate the host allow-list.' }
$alEnd = $null
for ($i = $alStart; $i -lt $all.Count; $i++) { if ($all[$i] -match '^\s*\)\s*$') { $alEnd = $i; break } }
if (-not $alEnd) { throw 'Could not locate the end of the host allow-list.' }
$allowText = ($all[$alStart..($alEnd - 1)]) -join "`n"
$hostAllowed = @([regex]::Matches($allowText, "'([A-Za-z]+)'") | ForEach-Object { $_.Groups[1].Value } | Sort-Object)
Check 'the host allow-list was read' $true ($hostAllowed.Count -ge 13)
foreach ($cp in $csParams) { Check "host permits $cp" $true ($hostAllowed -contains $cp) }
foreach ($hp in $hostAllowed) { Check "$hp is actually produced" $true ($csParams -contains $hp) }

Write-Host "`n== every editable row has a write mapping ==" -ForegroundColor Cyan
# An editable row with no entry in the translation table throws on save. A row that is editable and MISSING
# from the table would otherwise be the worst outcome available: a save that reports success and changes
# nothing. Rows blocked for a permanent reason are view-only by design and need no mapping.
$dlKeys = @([regex]::Matches($csText, '\["([a-zA-Z]+)"\] = new\("') | ForEach-Object { $_.Groups[1].Value })
Check 'the translation table was read' $true ($dlKeys.Count -ge 13)
$permanentTips = @('AddressBlockedTip', 'SizeBlockedTip', 'RecipientPendingTip',
                   'PrimaryAddressTip', 'ServiceAddressTip', 'AliasBlockedTip')
$checkedRows = 0
foreach ($m in [regex]::Matches($csText, 'E\("([a-zA-Z]+)",')) {
    $rowKey = $m.Groups[1].Value
    $slice = $csText.Substring($m.Index, [Math]::Min(500, $csText.Length - $m.Index))
    # Stop at the next row, so one row's reason is never attributed to the row above it.
    $nextRow = [regex]::Match($slice.Substring(1), '\n\s+[EP]\("')
    if ($nextRow.Success) { $slice = $slice.Substring(0, $nextRow.Index + 1) }
    $forever = $false
    foreach ($tip in $permanentTips) { if ($slice -match $tip) { $forever = $true } }
    if ($forever) { continue }
    $checkedRows++
    Check "editable row $rowKey has a write mapping" $true ($dlKeys -contains $rowKey)
}
Check 'editable rows were found to check' $true ($checkedRows -ge 10)

Write-Host "`n== set-dl-properties: the guards around the write ==" -ForegroundColor Cyan
# Extract the op body and run it against stubs. Everything it protects against is unrecoverable or wrong:
# writing to a group Exchange returned instead of the one asked for, writing to a synced group, or handing
# Set-DistributionGroup a parameter this app never meant to expose.
$sdStart = ($all | Select-String -Pattern "^\s*'set-dl-properties' \{\s*$" | Select-Object -First 1).LineNumber
if (-not $sdStart) { throw 'Could not locate the set-dl-properties op.' }
$depth = 0; $sdEnd = $null
for ($i = $sdStart - 1; $i -lt $all.Count; $i++) {
    $depth += ([regex]::Matches($all[$i], '\{')).Count
    $depth -= ([regex]::Matches($all[$i], '\}')).Count
    if ($depth -le 0) { $sdEnd = $i; break }
}
if (-not $sdEnd) { throw 'Could not locate the end of the set-dl-properties op.' }
$sdBody = ($all[$sdStart..($sdEnd - 1)]) -join "`n"
if ($sdBody -notmatch 'Set-DistributionGroup') { throw 'The extracted op does not contain the write.' }
if ($sdBody -match '__emit @\{ ok = \$false') { throw 'The extracted op swallows its own errors.' }

$script:setCalls = @()
$script:emitted = @()
function __emit($obj) { $script:emitted += $obj }
function Set-DistributionGroup {
    [CmdletBinding()]
    param(
        $Identity, $Alias, $Description, $MailTip, [switch]$RoomList, $EmailAddresses, $HiddenFromAddressListsEnabled,
        $MemberJoinRestriction, $MemberDepartRestriction, $RequireSenderAuthenticationEnabled,
        $BccBlocked, $ReportToOriginatorEnabled, $ReportToManagerEnabled,
        $SendOofMessageToOriginatorEnabled, $ModerationEnabled, $SendModerationNotifications,
        $ManagedBy, $GrantSendOnBehalfTo, $ModeratedBy, $AcceptMessagesOnlyFromSendersOrMembers,
        $RejectMessagesFromSendersOrMembers, $BypassModerationFromSendersOrMembers,
        [switch]$BypassSecurityGroupManagerCheck
    )
    $script:setCalls += , $PSBoundParameters
}
$script:theGroup = $null
function Get-DistributionGroup {
    [CmdletBinding()]
    param([string]$Identity)
    return $script:theGroup
}
function NewGroup([bool]$synced) {
    [pscustomobject]@{
        DisplayName = 'All Staff'; Name = 'All Staff'; Alias = 'allstaff'
        PrimarySmtpAddress = 'allstaff@contoso.com'
        Guid = '77777777-7777-7777-7777-777777777777'
        ExternalDirectoryObjectId = '66666666-6666-6666-6666-666666666666'
        Identity = 'contoso.onmicrosoft.com/Groups/All Staff'
        ManagedBy = @('11111111-1111-1111-1111-111111111111')
        IsDirSynced = $(if ($synced) { 'True' } else { 'False' })
    }
}
# Runs the op body and returns the error message, or $null when it succeeded.
function RunSet($identity, $changes) {
    $script:setCalls = @(); $script:emitted = @()
    $r = [pscustomobject]@{ identity = $identity; changes = $changes }
    try { Invoke-Expression $sdBody; return $null } catch { return $_.Exception.Message }
}

$script:theGroup = NewGroup $false
$err = RunSet 'allstaff@contoso.com' ([pscustomobject]@{ MailTip = 'Team list'; BccBlocked = $true })
Check 'a clean edit succeeds'        $null $err
Check 'and calls the cmdlet once'    1     $script:setCalls.Count
Check 'and emits success'            $true ($script:emitted.Count -eq 1 -and $script:emitted[0].ok -eq $true)
# Never index blind: when a guard regresses the write does not happen, and an exception here would stop
# the run instead of reporting which check failed.
function Sent($i) { if ($script:setCalls.Count -gt $i) { return $script:setCalls[$i] } return @{} }
function Ea($i) { $v = (Sent $i)['EmailAddresses']; if ($null -eq $v) { return @{} } return $v }
$sent = Sent 0
Check 'addressed by the Exchange GUID' '77777777-7777-7777-7777-777777777777' $sent['Identity']
Check 'the manager check is bypassed'  $true ([bool]$sent['BypassSecurityGroupManagerCheck'])
Check 'the supplied values are passed' 'Team list' $sent['MailTip']
Check 'booleans survive the trip'      $true  $sent['BccBlocked']

# A synced group: Exchange rejects the write anyway, but names neither the group nor the reason.
$script:theGroup = NewGroup $true
$err = RunSet 'allstaff@contoso.com' ([pscustomobject]@{ MailTip = 'Team list' })
Check 'a synced group is refused'          $true ($err -like '*synchronized from on-premises*')
Check 'and the refusal names the group'    $true ($err -like '*All Staff*')
Check 'and nothing is written'             0     $script:setCalls.Count

# The returns-everything trap, on the write side: -First 1 on a wrong answer would edit a stranger.
$script:theGroup = NewGroup $false
$err = RunSet 'someone-else@contoso.com' ([pscustomobject]@{ MailTip = 'Team list' })
Check 'a different group is refused'  $true ($err -like '*did not return the group*')
Check 'and nothing is written'        0     $script:setCalls.Count

# The allow-list is the last point at which a parameter can be stopped.
$script:theGroup = NewGroup $false
$err = RunSet 'allstaff@contoso.com' ([pscustomobject]@{ PrimarySmtpAddress = 'hijack@contoso.com' })
Check 'an unlisted parameter is refused' $true ($err -like '*not a distribution group setting this app may change*')
Check 'and nothing is written'           0    $script:setCalls.Count

$err = RunSet '' ([pscustomobject]@{ MailTip = 'Team list' })
Check 'a blank identity is refused' $true ($err -like '*group is required*')
Check 'and nothing is written'      0    $script:setCalls.Count

$err = RunSet 'allstaff@contoso.com' ([pscustomobject]@{})
Check 'an empty change set is refused' $true ($err -like '*No changes were supplied*')
Check 'and nothing is written'         0    $script:setCalls.Count

Write-Host "`n== set-dl-properties: secondary addresses ==" -ForegroundColor Cyan
# The one parameter here that can silently redirect a group's mail. Exchange promotes an uppercase SMTP:
# entry to the reply address, and replacing the whole collection promotes the first lowercase one, so the op
# accepts an add/remove of lowercase entries and nothing else.
function Addr($add, $remove) {
    $o = @{}
    if ($add) { $o['Add'] = $add }
    if ($remove) { $o['Remove'] = $remove }
    [pscustomobject]@{ EmailAddresses = [pscustomobject]$o }
}

$script:theGroup = NewGroup $false
$err = RunSet 'allstaff@contoso.com' (Addr @('smtp:staff@contoso.com') @('smtp:old@contoso.com'))
Check 'an add and remove succeeds' $null $err
$ea = Ea 0
Check 'the parameter is a hashtable' $true ($ea -is [hashtable])
Check 'the addition is carried'      'smtp:staff@contoso.com' ($ea['Add'] -join ';')
Check 'the removal is carried'       'smtp:old@contoso.com'   ($ea['Remove'] -join ';')

$err = RunSet 'allstaff@contoso.com' (Addr @('SMTP:hijack@contoso.com') $null)
Check 'an uppercase SMTP entry is refused' $true ($err -like '*must be a secondary address*')
Check 'and nothing is written'             0    $script:setCalls.Count

$err = RunSet 'allstaff@contoso.com' (Addr $null @('smtp:allstaff@contoso.com'))
Check 'removing the primary is refused' $true ($err -like '*primary address*')
Check 'and nothing is written'          0    $script:setCalls.Count

$err = RunSet 'allstaff@contoso.com' (Addr $null @('smtp:allstaff@contoso.onmicrosoft.com'))
Check 'removing a routing address is refused' $true ($err -like '*routing address*')
Check 'and nothing is written'                0    $script:setCalls.Count

$err = RunSet 'allstaff@contoso.com' (Addr @('smtp:new@contoso.com') $null)
Check 'an add alone is fine'   $null $err
Check 'and Remove is omitted'  $false ((Ea 0).ContainsKey('Remove'))


# --- 6. the recipient resolution behind the pickers ---------------------------------------------------
# __ownerNames answers "what do I show". __recipList answers "what can I write back", which needs an
# address. The distinction is the whole safety property: an entry resolved to a name but no address cannot
# be written, and saving the list without it would remove somebody the operator never saw.
#
# Its own stub, deliberately: three Get-Recipient stubs are defined earlier in this file for other sections,
# and depending on which one happened to be declared last is how a test ends up asserting nothing.
function Get-Recipient {
    [CmdletBinding()]
    param([string]$Identity)
    $script:lookups++
    if ($KNOWN.ContainsKey($Identity)) {
        return [pscustomobject]@{
            DisplayName = $KNOWN[$Identity]; Guid = $Identity; ExternalDirectoryObjectId = $Identity
            PrimarySmtpAddress = "$($KNOWN[$Identity] -replace ' ', '.')@contoso.com"
            RecipientTypeDetails = 'UserMailbox'
        }
    }
    if ($script:stubReturnsEverything) {
        return @(
            [pscustomobject]@{ DisplayName = 'Totally Unrelated Person'; Guid = '99999999-9999-9999-9999-999999999999'
                               ExternalDirectoryObjectId = '99999999-9999-9999-9999-999999999999'
                               PrimarySmtpAddress = 'stranger@contoso.com'; RecipientTypeDetails = 'UserMailbox' }
        )
    }
    throw "The operation couldn't be performed because object '$Identity' couldn't be found."
}

Write-Host "`n== __recipList: a name AND an address, or neither ==" -ForegroundColor Cyan
__ownerReset 25; $script:lookups = 0
$rl = @(__recipList @('11111111-1111-1111-1111-111111111111'))
Check 'a GUID resolves to a name' 'Dana Scully'             $rl[0].Name
Check 'and to an address'         'Dana.Scully@contoso.com' $rl[0].Smtp
Check 'and to a type'             'UserMailbox'             $rl[0].Type
__ownerReset 25
$rl = @(__recipList @('contoso.onmicrosoft.com/Users/11111111-1111-1111-1111-111111111111'))
Check 'a canonical name is reduced, then resolved' 'Dana Scully' $rl[0].Name
__ownerReset 25
$rl = @(__recipList @('22222222-2222-2222-2222-222222222222'))
Check 'an entry that does not resolve keeps its raw value' '22222222-2222-2222-2222-222222222222' $rl[0].Name
Check 'and carries no address'                             ''                                     $rl[0].Smtp
__ownerReset 0
$rl = @(__recipList @('11111111-1111-1111-1111-111111111111'))
Check 'a skipped entry carries no address' '' $rl[0].Smtp
Check 'and is counted as skipped'          1  $script:__ownerSkipped
# The documented returns-everything trap, on the resolution path: a deleted owner's GUID is exactly the
# non-existent identity that makes an Exchange Get- cmdlet hand back the whole directory.
$script:stubReturnsEverything = $true
__ownerReset 25
$rl = @(__recipList @('55555555-5555-5555-5555-555555555555'))
Check 'a stranger is not accepted as the answer' '55555555-5555-5555-5555-555555555555' $rl[0].Name
Check 'and contributes no address'               ''                                     $rl[0].Smtp
$script:stubReturnsEverything = $false

Write-Host "`n== list-dl-recipients: the guards ==" -ForegroundColor Cyan
$lrStart = ($all | Select-String -Pattern "^\s*'list-dl-recipients' \{\s*$" | Select-Object -First 1).LineNumber
if (-not $lrStart) { throw 'Could not locate the list-dl-recipients op.' }
$depth = 0; $lrEnd = $null
for ($i = $lrStart - 1; $i -lt $all.Count; $i++) {
    $depth += ([regex]::Matches($all[$i], '\{')).Count
    $depth -= ([regex]::Matches($all[$i], '\}')).Count
    if ($depth -le 0) { $lrEnd = $i; break }
}
if (-not $lrEnd) { throw 'Could not locate the end of the list-dl-recipients op.' }
$lrBody = ($all[$lrStart..($lrEnd - 1)]) -join "`n"
if ($lrBody -notmatch '__recipList') { throw 'The extracted op does not resolve anything.' }
function RunList($identity, $field) {
    $script:emitted = @()
    $r = [pscustomobject]@{ identity = $identity; field = $field }
    try { Invoke-Expression $lrBody; return $null } catch { return $_.Exception.Message }
}
function Emitted() { if ($script:emitted.Count -gt 0) { return $script:emitted[0] } return @{} }

$script:theGroup = NewGroup $false
$err = RunList 'allstaff@contoso.com' 'ManagedBy'
Check 'a permitted field resolves' $null                    $err
Check 'and the owners come back'   'Dana Scully'            (@((Emitted).data)[0].Name)
Check 'with an address to write'   'Dana.Scully@contoso.com' (@((Emitted).data)[0].Smtp)

# The field name is chosen in C#, but a property this app never meant to read must still stop here.
$err = RunList 'allstaff@contoso.com' 'PrimarySmtpAddress'
Check 'an unlisted field is refused' $true ($err -like '*not a recipient property*')

$err = RunList 'someone-else@contoso.com' 'ManagedBy'
Check 'a different group is refused' $true ($err -like '*did not return the group*')

$err = RunList '' 'ManagedBy'
Check 'a blank identity is refused'  $true ($err -like '*group is required*')


Write-Host "`n== every recipient list is edited with the picker ==" -ForegroundColor Cyan
# A recipient row that lost its picker becomes an ordinary text box. The save then looks for the addresses
# the picker would have supplied, finds none, and reports the row as unchanged — a typed edit that vanishes
# while the pane says the save succeeded.
$recipKeys = @([regex]::Matches($csText, '\["([a-zA-Z]+)"\] = new\("[A-Za-z]+", DlValue\.Recipients') |
               ForEach-Object { $_.Groups[1].Value })
Check 'the recipient mappings were read' $true ($recipKeys.Count -ge 6)
foreach ($rk in $recipKeys) {
    $rm = [regex]::Match($csText, 'E\("' + [regex]::Escape($rk) + '"[^\n]*')
    Check "$rk is edited with the picker" $true ($rm.Success -and $rm.Value -match 'recipients: true')
}

# The same property has to be read and written, or the editor seeds itself from one list and saves another.
$fieldsStart = $csText.IndexOf('DlRecipientFields =')
if ($fieldsStart -lt 0) { throw 'Could not locate DlRecipientFields.' }
$fieldsBlock = $csText.Substring($fieldsStart, [Math]::Min(900, $csText.Length - $fieldsStart))
$fieldsBlock = $fieldsBlock.Substring(0, $fieldsBlock.IndexOf('};') + 2)
foreach ($rk in $recipKeys) {
    $wm = [regex]::Match($csText, '\["' + [regex]::Escape($rk) + '"\] = new\("([A-Za-z]+)", DlValue\.Recipients')
    $rmField = [regex]::Match($fieldsBlock, '\["' + [regex]::Escape($rk) + '"\] = "([A-Za-z]+)"')
    Check "$rk is read and written through the same property" $wm.Groups[1].Value $rmField.Groups[1].Value
}


Write-Host "`n== set-dl-properties: a recipient list is a delta, never a replacement ==" -ForegroundColor Cyan
# These lists can hold entries this app cannot resolve — a role group such as Organization Management is not
# a mail recipient and Set-DistributionGroup will not take one back. Replacing the list would delete it, so
# only the difference is ever sent and everything else is left exactly as it was.
function Recip($param, $add, $remove) {
    $o = @{}
    if ($add) { $o['Add'] = $add }
    if ($remove) { $o['Remove'] = $remove }
    $outer = @{}; $outer[$param] = [pscustomobject]$o
    return [pscustomobject]$outer
}
function Param($i, $name) { $v = (Sent $i)[$name]; if ($null -eq $v) { return @{} } return $v }

$script:theGroup = NewGroup $false
$err = RunSet 'allstaff@contoso.com' (Recip 'ManagedBy' @('jane@contoso.com') @('bob@contoso.com'))
Check 'a recipient delta succeeds'     $null              $err
Check 'and arrives as a hashtable'     $true              ((Param 0 'ManagedBy') -is [hashtable])
Check 'carrying the addition'          'jane@contoso.com' ((Param 0 'ManagedBy')['Add'] -join ';')
Check 'and the removal'                'bob@contoso.com'  ((Param 0 'ManagedBy')['Remove'] -join ';')

$err = RunSet 'allstaff@contoso.com' (Recip 'ModeratedBy' @('jane@contoso.com') $null)
Check 'an add alone omits Remove'      $false ((Param 0 'ModeratedBy').ContainsKey('Remove'))
$err = RunSet 'allstaff@contoso.com' (Recip 'ModeratedBy' $null @('bob@contoso.com'))
Check 'a remove alone omits Add'       $false ((Param 0 'ModeratedBy').ContainsKey('Add'))

# Nothing on either side means nothing to send — and nothing is what a replacement would have deleted.
$err = RunSet 'allstaff@contoso.com' (Recip 'ManagedBy' $null $null)
Check 'an empty delta sends nothing'   $true ($err -like '*No changes were supplied*')
Check 'and nothing is written'         0     $script:setCalls.Count

Write-Host "`n== the host and the C# agree on which lists are deltas ==" -ForegroundColor Cyan
# A list C# treats as a delta but the host does not would be splatted as an object into a MultiValuedProperty
# parameter, which fails at the cmdlet for a reason that names nothing useful.
$wrStart = ($all | Select-String -Pattern '^\s*\$wRecipient = @\($' | Select-Object -First 1).LineNumber
if (-not $wrStart) { throw 'Could not locate the host recipient list.' }
$wrEnd = $null
for ($i = $wrStart; $i -lt $all.Count; $i++) { if ($all[$i] -match '^\s*\)\s*$') { $wrEnd = $i; break } }
if (-not $wrEnd) { throw 'Could not locate the end of the host recipient list.' }
$hostRecip = @([regex]::Matches((($all[$wrStart..($wrEnd - 1)]) -join "`n"), "'([A-Za-z]+)'") |
              ForEach-Object { $_.Groups[1].Value })
Check 'the host recipient list was read' $true ($hostRecip.Count -ge 6)
foreach ($rk in $recipKeys) {
    $wm = [regex]::Match($csText, '\["' + [regex]::Escape($rk) + '"\] = new\("([A-Za-z]+)", DlValue\.Recipients')
    Check "the host treats $($wm.Groups[1].Value) as a delta" $true ($hostRecip -contains $wm.Groups[1].Value)
}


# --- 7. resolving a pasted batch -----------------------------------------------------------------------
Write-Host "`n== resolve-recipients: exact before ambiguous ==" -ForegroundColor Cyan
# The rung matters more than the hit: an exact match may resolve a row on its own, a search hit never does.
# If this op reported a search hit as exact, a half-typed name would put a real person into a group unasked.
$rrStart = ($all | Select-String -Pattern "^\s*'resolve-recipients' \{\s*`$" | Select-Object -First 1).LineNumber
if (-not $rrStart) { throw 'Could not locate the resolve-recipients op.' }
$depth = 0; $rrEnd = $null
for ($i = $rrStart - 1; $i -lt $all.Count; $i++) {
    $depth += ([regex]::Matches($all[$i], '\{')).Count
    $depth -= ([regex]::Matches($all[$i], '\}')).Count
    if ($depth -le 0) { $rrEnd = $i; break }
}
if (-not $rrEnd) { throw 'Could not locate the end of the resolve-recipients op.' }
$rrBody = ($all[$rrStart..($rrEnd - 1)]) -join "`n"
if ($rrBody -notmatch 'Anr') { throw 'The extracted op does not fall back to an ambiguous search.' }

# A stub that records how it was asked, so the ORDER of the rungs can be asserted.
$script:calls = @()
$script:byFilter = @{}
$script:byAnr = @{}
function Get-Recipient {
    [CmdletBinding()]
    param([string]$Identity, [string]$Filter, [string]$Anr, $ResultSize)
    if ($Filter) {
        $script:calls += "filter:$Filter"
        foreach ($k in $script:byFilter.Keys) { if ($Filter -like "*'$k'*") { return $script:byFilter[$k] } }
        return @()
    }
    if ($Anr) { $script:calls += "anr:$Anr"; if ($script:byAnr.ContainsKey($Anr)) { return $script:byAnr[$Anr] }; return @() }
    return @()
}
function Rec([string]$smtp, [string]$name, [string]$alias) {
    [pscustomobject]@{ PrimarySmtpAddress = $smtp; DisplayName = $name; Alias = $alias; RecipientTypeDetails = 'UserMailbox' }
}
function RunResolve($terms) {
    $script:calls = @(); $script:emitted = @()
    $r = [pscustomobject]@{ terms = $terms }
    try { Invoke-Expression $rrBody; return $null } catch { return $_.Exception.Message }
}
function Term2($line, $term, $search) { [pscustomobject]@{ line = $line; term = $term; search = $search } }

$script:byFilter = @{ 'jane@contoso.com' = @(Rec 'jane@contoso.com' 'Jane Doe' 'jdoe') }
$err = RunResolve @((Term2 1 'jane@contoso.com' 'jane@contoso.com'))
Check 'an exact address resolves'   $null $err
Check 'and is reported as exact'    $true (@((Emitted).data)[0].Exact)
Check 'carrying the recipient'      'jane@contoso.com' (@((Emitted).data)[0].Candidates[0].Identity)
Check 'without an ambiguous search' 0 (@($script:calls | Where-Object { $_ -like 'anr:*' })).Count

# Nothing exact: it must fall through to ANR and say the result was NOT exact.
$script:byFilter = @{}
$script:byAnr = @{ 'Jane' = @((Rec 'jane@contoso.com' 'Jane Doe' 'jdoe'), (Rec 'jane2@contoso.com' 'Jane Roe' 'jroe')) }
$err = RunResolve @((Term2 1 'Jane' 'Jane'))
Check 'a partial name falls through to a search' $true (@($script:calls | Where-Object { $_ -like 'anr:*' })).Count.Equals(1)
Check 'and is NOT reported as exact'            $false (@((Emitted).data)[0].Exact)
Check 'offering every candidate'                2 (@((Emitted).data)[0].Candidates).Count

# "Doe, Jane" is stored the other way round, so the flipped form is what gets searched.
$script:byFilter = @{ 'Jane Doe' = @(Rec 'jane@contoso.com' 'Jane Doe' 'jdoe') }
$script:byAnr = @{}
$err = RunResolve @((Term2 1 'Doe, Jane' 'Jane Doe'))
Check 'last, first is looked up flipped' $true (@((Emitted).data)[0].Exact)
Check 'and finds the person'             'Jane Doe' (@((Emitted).data)[0].Candidates[0].DisplayName)

# A whole chunk in one op is the point: one round trip, not one per line.
$script:byFilter = @{ 'a@x.com' = @(Rec 'a@x.com' 'A One' 'a'); 'b@x.com' = @(Rec 'b@x.com' 'B Two' 'b') }
$script:byAnr = @{}
$err = RunResolve @((Term2 1 'a@x.com' 'a@x.com'), (Term2 2 'b@x.com' 'b@x.com'), (Term2 3 'nobody@x.com' 'nobody@x.com'))
Check 'every term comes back'        3 (@((Emitted).data)).Count
Check 'lines are preserved'          3 (@((Emitted).data)[2].Line)
Check 'a miss returns no candidates' 0 (@(@((Emitted).data)[2].Candidates)).Count

# No -Identity anywhere: that is the parameter that makes a Get- cmdlet return the whole directory.
# Comments mention it, so match the CALL: -Identity on a Get- cmdlet is what returns the whole directory.
$rrCode = ($rrBody -split "`n" | Where-Object { $_ -notmatch '^\s*#' }) -join " "
Check 'the op never addresses by -Identity' $false ($rrCode -match 'Get-\w+[^|]*-Identity')
# A leading wildcard is documented as not allowed in Exchange Online, and slow wherever it is tolerated.
Check 'and uses no leading wildcard'        $false ($rrBody -match "like '\*")

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
