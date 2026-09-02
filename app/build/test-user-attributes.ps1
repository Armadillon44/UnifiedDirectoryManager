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

Write-Host "`n== Copy User offers the field but never inherits it ==" -ForegroundColor Cyan
# An employee ID identifies a person. Copying one to a new user hands two people the same identifier, and
# the copy dialog is exactly where that would happen by accident. The field is offered so the real value can
# be typed at creation, and left blank so nothing is inherited.
$Copy = [UnifiedDirectoryManager.ViewModels.CopyUserViewModel]
$store = New-Object UnifiedDirectoryManager.Services.TemplateStore ([System.IO.Path]::GetTempPath())
$settings = New-Object UnifiedDirectoryManager.Services.AppSettings
# Only BuildAttributes is exercised; nothing here reaches the directory or Graph.
$cu = $Copy::new([UnifiedDirectoryManager.Services.IDirectoryService]$null, $store,
                 [UnifiedDirectoryManager.Services.IDialogService]$null,
                 [UnifiedDirectoryManager.Services.IGraphService]$null, $null, $settings,
                 'CN=Source,OU=Staff,DC=contoso,DC=net')

Check 'the field starts empty' '' $cu.EmployeeId

$build = $Copy.GetMethod('BuildAttributes', [System.Reflection.BindingFlags]'NonPublic,Instance')
function CopyAttrs { $build.Invoke($cu, @()) }

$cu.FirstName = 'Grace'; $cu.LastName = 'Hopper'
$a = CopyAttrs
Check 'nothing is written while it is blank' $false ($a.ContainsKey('employeeID'))

$cu.EmployeeId = '  67890  '
$a = CopyAttrs
Check 'a typed value is written'  '67890' $a['employeeID']
# Same rule as everywhere else: the copy path has its own attribute builder, so trimming has to hold here too.
Check 'and trimmed'               $true   (-not $a['employeeID'].StartsWith(' '))

$cu.EmployeeId = '   '
$a = CopyAttrs
Check 'whitespace writes nothing' $false ($a.ContainsKey('employeeID'))

Write-Host "`n== copying a user to a TEMPLATE must not bake one in ==" -ForegroundColor Cyan
# Worse than the copy case and quieter: a template applies to everyone created from it, and its attribute
# rows are ticked by default, so a captured employee ID would be handed to every future user without anyone
# choosing it. Guarded by an exclusion list that is easy to add a catalog attribute alongside and forget.
$excluded = [UnifiedDirectoryManager.ViewModels.CopyToTemplateViewModel].GetField(
    'Excluded', [System.Reflection.BindingFlags]'NonPublic,Static').GetValue($null)
Check 'employeeID is excluded'    $true $excluded.Contains('employeeID')
Check 'whatever the casing'       $true $excluded.Contains('EMPLOYEEid')
# The identity attributes that were already excluded must stay excluded.
foreach ($k in 'sAMAccountName', 'userPrincipalName', 'mail', 'givenName', 'sn', 'displayName') {
    Check "  and $k still is"      $true $excluded.Contains($k)
}
# The negative control: things a template SHOULD carry must not have been swept up.
foreach ($k in 'title', 'department', 'physicalDeliveryOfficeName', 'co') {
    Check "  but $k is not"        $false $excluded.Contains($k)
}

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
