<#
.SYNOPSIS
  Regression tests for F12: re-importing the app's own export failed every row.

.DESCRIPTION
  The importer accepted any column whose header the attribute catalog recognised, with no check that the
  attribute could actually be written. The on-prem list export's first column is "Name", which the catalog
  maps to the system-owned RDN attribute `name`. Export a list, edit it, import it back -- the obvious thing
  to try -- and that column went into the create request, the DC rejected the write, and EVERY row failed
  with one opaque LDAP error. "Distinguished name", "Member of" and "Created" are all in that export too.

  The fix classifies such a column as NotWritable rather than as an attribute, and says so BEFORE the import
  runs. It is deliberately a different message from the unrecognised-column one: the header was understood,
  and telling an operator it "isn't a recognized field" sends them off to fix a spelling that is already
  right.

  ValidateFormat is static, so the pre-import check is exercised directly. The row path needs a directory to
  resolve managers against, so it is driven with FakeDirectoryService.

  Run with:  pwsh -NoProfile -File ./app/build/test-csv-import.ps1
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

$Importer = [UnifiedDirectoryManager.Services.BulkUserCsvImporter]
$Catalog  = [UnifiedDirectoryManager.Services.AttributeCatalog]

# The app writes typographic quotes and apostrophes in its messages, and PowerShell 7 treats those as string
# DELIMITERS -- a pattern containing one is a parse error, not a match. Flatten them to ASCII so the
# assertions below can be written plainly.
function Plain([string]$s) {
    $s -replace ([char]0x2019), "'" -replace ([char]0x201C), '"' -replace ([char]0x201D), '"'
}

function Warnings([string]$csv) { @($Importer::ValidateFormat($csv).Warnings) | ForEach-Object { Plain $_ } }
function Errors([string]$csv)   { @($Importer::ValidateFormat($csv).Errors) }

# Matches within ONE warning. Joining them first let a pattern span two, so '*"Type"*cant be set*' passed
# on a file where "Type" was merely unrecognised and something else entirely was unwritable.
function Warned([string]$csv, [string]$pattern) {
    foreach ($line in (Warnings $csv)) { if ($line -like $pattern) { return $true } }
    return $false
}

Write-Host "`n== the app's own export, imported back ==" -ForegroundColor Cyan
# Exactly what ObjectListViewModel.BuildCsv writes: Name/Type/Status/Protected, then the visible columns by
# their friendly headers. This is the file an operator actually has in their Downloads folder.
# "First name" and "Last name" are the friendly headers of givenName and sn, so an export with those columns
# visible -- an entirely ordinary thing -- passes the name-column check and gets as far as creating users.
# A bare export without them is refused earlier, by a pre-existing and correct check, which is why the
# finding's "every row fails" needs this shape to reproduce.
$export = @'
Name,Type,Status,Protected,Distinguished name,Member of,Created,First name,Last name,Department,Job title
Jane Doe,User,Enabled,No,"CN=Jane Doe,OU=Sales,DC=contoso,DC=net",All Staff,2024-01-05,Jane,Doe,Sales,Rep
Bob Roe,User,Enabled,No,"CN=Bob Roe,OU=Sales,DC=contoso,DC=net",All Staff,2024-02-11,Bob,Roe,Sales,Rep
'@

Check 'this export gets past the format check' 0 (@(Errors $export)).Count
# The finding's exact column. It used to reach the create request and fail the entire batch.
Check 'Name is refused'                   $true (Warned $export '*"Name"*can''t be set*')
Check 'and the reason is the directory'   $true (Warned $export '*"Name"*directory owns this value*')
Check 'Distinguished name is refused'     $true (Warned $export '*"Distinguished name"*can''t be set*')
Check 'Member of is refused'              $true (Warned $export '*"Member of"*can''t be set*')
Check 'Created is refused'                $true (Warned $export '*"Created"*can''t be set*')

# The columns that ARE writable must still be accepted, or the fix has traded one broken import for another.
Check 'Department is not refused'         $false (Warned $export '*"Department"*can''t be set*')
Check 'Job title is not refused'          $false (Warned $export '*"Job title"*can''t be set*')
Check 'First name is not refused'         $false (Warned $export '*"First name"*can''t be set*')

# Type/Status/Protected are not AD attributes at all, and keep the older, different message.
Check 'Type is merely unrecognised'       $true (Warned $export '*aren''t recognized*"Type"*')
Check 'and is not called unwritable'      $false (Warned $export '*"Type"*can''t be set*')

Write-Host "`n== a recognised column is not the same as an unrecognised one ==" -ForegroundColor Cyan
# The whole point of the separate message. "Name" is spelled correctly and means something; telling the
# operator it isn't recognised would send them to fix a spelling that is already right.
$named = "Name,First name`nJane Doe,Jane"
Check 'Name is not reported as unrecognised' $false (Warned $named '*aren''t recognized*Name*')
Check 'it is reported as unwritable'         $true  (Warned $named '*"Name"*can''t be set*')
Check 'and the reason is given'              $true  (Warned $named '*will not accept a write*')

Write-Host "`n== the other two reasons a column is refused ==" -ForegroundColor Cyan
# A multi-valued attribute cannot come out of one cell without being written as a single literal value,
# which is silently wrong data rather than a visible failure.
Check 'proxyAddresses is multi-valued'    $true $Catalog::Meta('proxyAddresses').IsMultiValued
$multi = "First name,Proxy addresses`nJane,smtp:jane@x.com;smtp:j.doe@x.com"
Check 'so the column is refused'          $true (Warned $multi '*"Proxy addresses"*can''t be set*')
Check 'saying it holds several values'    $true (Warned $multi '*"Proxy addresses"*holds several values*')

# A DN-valued attribute needs a distinguished name, and nothing here resolves one.
Check 'managedBy is DN-valued'            $true $Catalog::Meta('managedBy').IsDnValued
$dnCol = "First name,Managed by`nJane,Some Person"
Check 'so that column is refused too'     $true (Warned $dnCol '*"Managed by"*can''t be set*')
Check 'saying it needs a DN'              $true (Warned $dnCol '*"Managed by"*distinguished name*')

Write-Host "`n== Manager keeps its own handling ==" -ForegroundColor Cyan
# manager is DN-valued, but it is claimed by a named field BEFORE the catalog path and resolved by name
# against the directory. Refusing it would remove a documented feature.
Check 'manager is DN-valued in the catalog' $true $Catalog::Meta('manager').IsDnValued
$mgr = "First name,Last name,Manager`nJane,Doe,bsmith"
Check 'but the Manager column is accepted'  $false (Warned $mgr '*"Manager"*can''t be set*')
Check 'and not called unrecognised either'  $false (Warned $mgr '*aren''t recognized*Manager*')

Write-Host "`n== the template still imports cleanly ==" -ForegroundColor Cyan
# The negative control that matters most: the file the app hands out must produce no complaints at all.
$template = $Importer::TemplateCsv()
Check 'the template has no errors'   0 (@(Errors $template)).Count
Check 'and nothing unwritable'       $false (Warned $template '*can''t be set*')
Check 'and nothing unrecognised'     $false (Warned $template '*aren''t recognized*')

Write-Host "`n== the refusal reaches each row too ==" -ForegroundColor Cyan
# ValidateFormat is advisory and an operator can import anyway, so the row report has to carry it as well.
$fake = [UnifiedDirectoryManager.TestSupport.FakeDirectoryService]::new()
$graphSvc = [UnifiedDirectoryManager.TestSupport.InertGraphService]::new()
$imp = $Importer::new($fake, $graphSvc)
$rows = @($imp.ImportAsync("Name,First name,Department`nJane Doe,Jane,Sales", [System.Threading.CancellationToken]::None).GetAwaiter().GetResult())
Check 'one row came back'                 1 $rows.Count
$row = $rows[0]
$rw = Plain (($row.Warnings) -join ' || ')
Check 'the row warns about Name'          $true ($rw -like '*"Name"*can''t be set*')
# And, the actual bug: it must not be handed to the create request.
Check 'and name is NOT an override'       $false $row.AttributeOverrides.ContainsKey('name')
Check 'while department still is'         $true  $row.AttributeOverrides.ContainsKey('department')
Check 'carrying its value'                'Sales' $row.AttributeOverrides['department']
Check 'and the real name column landed'   'Jane' $row.FirstName

Write-Host "`n== the guard is in Classify, not in the caller (mutation check) ==" -ForegroundColor Cyan
$src = Get-Content -Raw (Join-Path $repoRoot 'app\src\UnifiedDirectoryManager\Services\BulkUserCsvImporter.cs')
$classify = [regex]::Match($src, '(?s)private static \(Field Field, string\? Ldap\) Classify\(string header\).*?\r?\n    \}').Value
Check 'Classify was found'                   $true ($classify.Length -gt 0)
Check 'it consults the writability rule'     $true ($classify -match 'WhyNotWritable')
Check 'and no longer accepts anything known' $false ($classify -match 'IsKnown\(ldap\) \? \(Field\.Attribute, ldap\)')
$why = [regex]::Match($src, '(?s)private static string\? WhyNotWritable\(string ldapName\).*?\r?\n    \}').Value
foreach ($flag in 'IsReadOnly', 'IsMultiValued', 'IsDnValued') {
    Check "  and checks $flag" $true ($why -match [regex]::Escape($flag))
}

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
