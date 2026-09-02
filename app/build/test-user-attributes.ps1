<#
.SYNOPSIS
  Checks UserAttributeBuilder — the shared resolver behind New User, Copy User and Bulk Create.

.DESCRIPTION
  Everything three creation paths write to a new account comes out of this one pure function, and until now
  none of it was covered. The rules that matter are about what does NOT get written: a blank field must not
  write an empty attribute, and an explicitly entered value must beat the template default rather than the
  other way round.

  Run with:  pwsh -NoProfile -File ./app/build/test-user-attributes.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dll = Join-Path (Split-Path -Parent $root) 'debug\UnifiedDirectoryManager.dll'
if (-not (Test-Path $dll)) { throw "Build first — could not find $dll" }
[System.Reflection.Assembly]::LoadFrom($dll) | Out-Null

$B = [UnifiedDirectoryManager.Services.UserAttributeBuilder]

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

# The record's properties are init-only, which blocks assignment in C# but not through reflection, which is
# what PowerShell uses. Handy here, and the reason this suite needs no test double.
function Input([hashtable]$fields) {
    $t = New-Object UnifiedDirectoryManager.Models.UserTemplate
    $t.Name = 'Test template'
    if ($fields.ContainsKey('Defaults')) {
        foreach ($k in $fields.Defaults.Keys) { $t.AttributeDefaults[$k] = $fields.Defaults[$k] }
    }
    $i = New-Object UnifiedDirectoryManager.Services.UserAttributeBuilder+Input
    $i.Template = $t
    $i.FirstName = if ($fields.ContainsKey('FirstName')) { $fields.FirstName } else { 'Ada' }
    $i.LastName  = if ($fields.ContainsKey('LastName'))  { $fields.LastName }  else { 'Lovelace' }
    foreach ($k in 'MiddleName', 'Initials', 'SamOverride', 'UpnSuffix', 'Email', 'Upn', 'ManagerDn', 'EmployeeId') {
        if ($fields.ContainsKey($k)) { $i.$k = $fields[$k] }
    }
    return $i
}
function Attrs([hashtable]$fields) { $B::Build((Input $fields)).Attributes }
function Val($attrs, [string]$ldap) { if ($attrs.ContainsKey($ldap)) { $attrs[$ldap] } else { $null } }

Write-Host "`n== employee ID is written when given ==" -ForegroundColor Cyan
$a = Attrs @{ EmployeeId = '12345' }
Check 'it lands on employeeID'   '12345' (Val $a 'employeeID')
# The lDAPDisplayName matters: employeeId (lowercase d) is the Entra/Graph spelling, employeeID the AD one.
Check 'under the AD spelling'    $true   $a.ContainsKey('employeeID')

$a = Attrs @{ EmployeeId = '  12345  ' }
Check 'and is trimmed'           '12345' (Val $a 'employeeID')

Write-Host "`n== a blank employee ID writes nothing at all ==" -ForegroundColor Cyan
# Writing an empty attribute is not the same as not writing one. An employee ID is optional, and a blank
# box must leave the attribute absent rather than setting it to "".
$a = Attrs @{ }
Check 'omitted means absent'          $false ($a.ContainsKey('employeeID'))
$a = Attrs @{ EmployeeId = '' }
Check 'empty means absent'            $false ($a.ContainsKey('employeeID'))
$a = Attrs @{ EmployeeId = '   ' }
Check 'whitespace-only means absent'  $false ($a.ContainsKey('employeeID'))

Write-Host "`n== what is typed beats the template ==" -ForegroundColor Cyan
# A template CAN carry employeeID, but it can only carry one value for everyone it creates. Whatever the
# operator typed for this person has to win, the same way it does for mail and userPrincipalName.
$a = Attrs @{ Defaults = @{ 'employeeID' = 'FROM-TEMPLATE' } }
Check 'a template default is used when nothing is typed' 'FROM-TEMPLATE' (Val $a 'employeeID')

$a = Attrs @{ Defaults = @{ 'employeeID' = 'FROM-TEMPLATE' }; EmployeeId = 'TYPED' }
Check 'and is overridden by what was typed'              'TYPED'         (Val $a 'employeeID')

# A blank box must not wipe a template default — that would be a silent behaviour change for anyone whose
# template already sets it.
$a = Attrs @{ Defaults = @{ 'employeeID' = 'FROM-TEMPLATE' }; EmployeeId = '' }
Check 'but a blank box does not clear it'                'FROM-TEMPLATE' (Val $a 'employeeID')

Write-Host "`n== the surrounding rules still hold ==" -ForegroundColor Cyan
# Guards against the new field disturbing what was already there.
$a = Attrs @{ FirstName = 'Ada'; LastName = 'Lovelace'; EmployeeId = '12345' }
Check 'givenName survives'   'Ada'           (Val $a 'givenName')
Check 'sn survives'          'Lovelace'      (Val $a 'sn')
Check 'displayName survives' 'Ada Lovelace'  (Val $a 'displayName')
Check 'cn survives'          'Ada Lovelace'  (Val $a 'cn')
Check 'sAMAccountName survives' 'ada.lovelace' (Val $a 'sAMAccountName')

# Every value in the result must be non-blank; the builder filters blanks out at the end and the new field
# must not sneak past that.
$a = Attrs @{ EmployeeId = '   '; Defaults = @{ 'department' = '' } }
$blank = @($a.GetEnumerator() | Where-Object { [string]::IsNullOrWhiteSpace($_.Value) })
Check 'no attribute is written blank' 0 $blank.Count

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
