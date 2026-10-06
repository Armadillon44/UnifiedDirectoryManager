<#
.SYNOPSIS
  Checks UserAttributeBuilder — the shared resolver behind New User, Copy User and Bulk Create.

.DESCRIPTION
  Everything three creation paths write to a new account comes out of this one pure function -- which was
  only true of two of them until F26. Copy User kept a near-verbatim private copy of the resolver, and that
  copy had drifted. The rules that matter are about what does NOT get written: a blank field must not
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
# This suite names the app folder $root; the repository root is its parent.
$repoRoot = Split-Path -Parent $root
$srcDir = Join-Path $repoRoot 'app\src\UnifiedDirectoryManager'

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

Write-Host "`n== Bulk Create round-trips it, and keeps overrides it has no field for ==" -ForegroundColor Cyan
# Bulk Create's "Add user..." reuses the New User form in capture mode, so it shows the Employee ID
# box. Without wiring, a typed value was accepted and thrown away -- the row had nowhere to put it. And
# reopening a CSV-imported row rebuilt it from the form alone, dropping every override the form has no
# field for (Job title, Department).
$DS = [UnifiedDirectoryManager.Views.DialogService]
$map = $DS.GetMethod('MapRowFromNewUser', [System.Reflection.BindingFlags]'NonPublic,Static')
$seed = $DS.GetMethod('SeedNewUserFromRow', [System.Reflection.BindingFlags]'NonPublic,Static')
$store2 = New-Object UnifiedDirectoryManager.Services.TemplateStore ([System.IO.Path]::GetTempPath())
function NewUserVm {
    [UnifiedDirectoryManager.ViewModels.NewUserViewModel]::new(
        [UnifiedDirectoryManager.Services.IDirectoryService]$null, $store2,
        [UnifiedDirectoryManager.Services.IDialogService]$null,
        [UnifiedDirectoryManager.Services.IGraphService]$null, $null,
        (New-Object UnifiedDirectoryManager.Services.AppSettings))
}

$vm = NewUserVm
$vm.FirstName = 'Alan'; $vm.LastName = 'Turing'; $vm.EmployeeId = '  31415  '
$row = $map.Invoke($null, @($vm, $null))
Check 'a captured row carries it'  '31415' $row.AttributeOverrides['employeeID']

$vm2 = NewUserVm
$vm2.FirstName = 'Alan'; $vm2.LastName = 'Turing'
$row2 = $map.Invoke($null, @($vm2, $null))
Check 'and a blank box carries none' $false $row2.AttributeOverrides.ContainsKey('employeeID')

# Round trip: seed the form from a row, then map it back.
$imported = [UnifiedDirectoryManager.ViewModels.BulkCreateRowViewModel](New-Object UnifiedDirectoryManager.ViewModels.BulkCreateRowViewModel)
$imported.FirstName = 'Alan'; $imported.LastName = 'Turing'
$imported.AttributeOverrides['employeeID'] = '27182'
$imported.AttributeOverrides['title'] = 'Cryptanalyst'
$imported.AttributeOverrides['department'] = 'Hut 8'
$vm3 = NewUserVm
[void]$seed.Invoke($null, @($vm3, $imported))
Check 'editing a row shows its employee ID' '27182' $vm3.EmployeeId
$back = $map.Invoke($null, @($vm3, $imported))
Check 'and it survives the round trip'      '27182' $back.AttributeOverrides['employeeID']
# The pre-existing half: the form has no Job title or Department box, so those only survive by being
# carried forward from the row being edited.
Check 'as does Job title'                   'Cryptanalyst' $back.AttributeOverrides['title']
Check 'and Department'                      'Hut 8'        $back.AttributeOverrides['department']

# Clearing the box must actually clear it, not fall back to the carried-forward value.
$vm3.EmployeeId = ''
$cleared = $map.Invoke($null, @($vm3, $imported))
Check 'clearing the box removes it'         $false $cleared.AttributeOverrides.ContainsKey('employeeID')
Check 'without disturbing the others'       'Hut 8' $cleared.AttributeOverrides['department']
Write-Host "`n== one resolver, not one per window (F26) ==" -ForegroundColor Cyan
# Copy User carried a private copy of Resolve: same regex, same nine tokens, same switch. It had already
# drifted -- the copy resolved {upnSuffix} WITHOUT trimming -- so the same template produced a different
# UPN depending on which window created the user, and the difference went into AD.
$Tokens = [UnifiedDirectoryManager.Services.UserAttributeBuilder+NameTokens]

# The exact drift. A template suffix with a trailing space is easy to save and impossible to see.
$untrimmed = $Tokens::new('Jane', '', 'Doe', '', 'contoso.com ')
Check 'the suffix is trimmed'          'jdoe@contoso.com' ($B::Resolve($untrimmed, '{sam}@{upnSuffix}', 'jdoe'))
# And not just the suffix: a name pasted from a spreadsheet carries whitespace too.
$spacey = $Tokens::new('  Jane ', ' Q ', ' Doe  ', ' JQD ', ' contoso.com ')
Check 'first is trimmed'               'Jane' ($B::Resolve($spacey, '{first}', 'x'))
Check 'last is trimmed'                'Doe' ($B::Resolve($spacey, '{last}', 'x'))
Check 'middle is trimmed'              'Q' ($B::Resolve($spacey, '{middle}', 'x'))
Check 'initials are trimmed'           'JQD' ($B::Resolve($spacey, '{initials}', 'x'))
Check 'and initials come off the trimmed name' 'J' ($B::Resolve($spacey, '{firstInitial}', 'x'))

# Every token the pattern language documents, so a switch arm cannot be dropped unnoticed.
$jane = $Tokens::new('Jane', 'Quinn', 'Doe', 'JQD', 'contoso.com')
Check '{first}'                        'Jane' ($B::Resolve($jane, '{first}', 'jdoe'))
Check '{middle}'                       'Quinn' ($B::Resolve($jane, '{middle}', 'jdoe'))
Check '{last}'                         'Doe' ($B::Resolve($jane, '{last}', 'jdoe'))
Check '{firstInitial}'                 'J' ($B::Resolve($jane, '{firstInitial}', 'jdoe'))
Check '{middleInitial}'                'Q' ($B::Resolve($jane, '{middleInitial}', 'jdoe'))
Check '{lastInitial}'                  'D' ($B::Resolve($jane, '{lastInitial}', 'jdoe'))
Check '{initials}'                     'JQD' ($B::Resolve($jane, '{initials}', 'jdoe'))
Check '{sam}'                          'jdoe' ($B::Resolve($jane, '{sam}', 'jdoe'))
Check '{upnSuffix}'                    'contoso.com' ($B::Resolve($jane, '{upnSuffix}', 'jdoe'))
Check 'tokens are case-insensitive'    'Jane' ($B::Resolve($jane, '{FIRST}', 'jdoe'))
Check 'an unknown token is left alone' '{nickname}' ($B::Resolve($jane, '{nickname}', 'jdoe'))
Check 'an empty pattern stays empty'   '' ($B::Resolve($jane, '', 'jdoe'))
Check 'a missing middle name vanishes' 'Jane .Doe' ($B::Resolve(($Tokens::new('Jane', '', 'Doe', '', 'x')), '{first} {middleInitial}.{last}', 'jdoe'))

# The point of the whole exercise: Copy User must now go through this, not around it -- and the way to
# prove that is to run ITS resolver, not the shared one, and watch the drift be gone.
#
# CopyUserViewModel's constructor wants the whole service graph (directory, template store, dialogs,
# graph, cloud provisioning) for a method that reads five string fields. GetUninitializedObject skips it
# entirely; the fields are then set directly, so no property-changed machinery runs either.
$CopyVm = [UnifiedDirectoryManager.ViewModels.CopyUserViewModel]
$NonPublic = [System.Reflection.BindingFlags]'NonPublic,Instance'
$copyResolve = $CopyVm.GetMethod('Resolve', $NonPublic)
if ($null -eq $copyResolve) { throw 'CopyUserViewModel has no Resolve — has it been refactored?' }

function New-CopyVm([string]$first, [string]$middle, [string]$last, [string]$initials, [string]$suffix) {
    $vm = [System.Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject($CopyVm)
    foreach ($pair in @(@('_firstName', $first), @('_middleName', $middle), @('_lastName', $last),
                        @('_initials', $initials), @('_upnSuffix', $suffix))) {
        $f = $CopyVm.GetField($pair[0], $NonPublic)
        if ($null -eq $f) { throw "CopyUserViewModel has no field $($pair[0])" }
        $f.SetValue($vm, $pair[1])
    }
    return $vm
}

# THE DRIFT, through Copy User's own method. The fork resolved {upnSuffix} without trimming, so this
# produced "jdoe@contoso.com " -- a UPN with a trailing space, written into AD.
$vmCopy = New-CopyVm 'Jane' '' 'Doe' '' 'contoso.com '
Check 'Copy User trims the suffix too' 'jdoe@contoso.com' ($copyResolve.Invoke($vmCopy, @('{sam}@{upnSuffix}', 'jdoe')))

# And the two windows now agree, token for token, on the same messy input. That is the whole claim.
$messy = @{ First = '  Jane '; Middle = ' Q '; Last = ' Doe  '; Initials = ' JQD '; Suffix = ' contoso.com ' }
$vmCopy = New-CopyVm $messy.First $messy.Middle $messy.Last $messy.Initials $messy.Suffix
$sharedTokens = $Tokens::new($messy.First, $messy.Middle, $messy.Last, $messy.Initials, $messy.Suffix)
foreach ($pattern in '{first}', '{middle}', '{last}', '{firstInitial}', '{middleInitial}', '{lastInitial}',
                     '{initials}', '{sam}', '{upnSuffix}', '{first}.{last}@{upnSuffix}') {
    $viaCopy = $copyResolve.Invoke($vmCopy, @($pattern, 'jdoe'))
    $viaShared = $B::Resolve($sharedTokens, $pattern, 'jdoe')
    Check "  both windows agree on $pattern" $viaShared $viaCopy
}
# The point of the whole exercise: Copy User must now go through this, not around it.
$copySrc = Get-Content -Raw (Join-Path (Split-Path -Parent $root) 'app\src\UnifiedDirectoryManager\ViewModels\CopyUserViewModel.cs')
Check 'Copy User calls the shared resolver' $true ($copySrc -match 'UserAttributeBuilder\.Resolve\(')
# The fork's fingerprints: its own regex, and the private initial helper only it used.
Check 'and keeps no regex of its own'       $false ($copySrc -match 'firstInitial\|lastInitial\|middleInitial')
Check 'nor its own initial helper'          $false ($copySrc -match 'private static string Ini\(')

# Bulk Create reaches the same resolver through Suggest/Build, so all three creation paths agree. This is
# what the header of this file claims, and until F26 it was only true of two of them.
$builderSrc = Get-Content -Raw (Join-Path (Split-Path -Parent $root) 'app\src\UnifiedDirectoryManager\Services\UserAttributeBuilder.cs')
$resolvers = ([regex]::Matches($builderSrc, 'firstInitial\|lastInitial\|middleInitial')).Count
Check 'exactly one resolver exists'         1 $resolvers
Write-Host "`n== a user built from scratch, with no template ==" -ForegroundColor Cyan
# New User used to REQUIRE a template: with none selected BuildAttributes returned an empty dictionary and
# the create failed at "could not derive a common name". Everything below the template loop already
# derived cn, sAMAccountName, displayName and the UPN from the typed names, so no-template needed the loop
# skipped and nothing else.
$Builder = [UnifiedDirectoryManager.Services.UserAttributeBuilder]

# The record is init-only, so it is built through its initialiser the way C# would.
# Dictionary<,> lives in System.Collections; a using directive does not reference the assembly.
Add-Type -ReferencedAssemblies @(
    (Join-Path $repoRoot 'debug\UnifiedDirectoryManager.dll'),
    'System.Collections', 'System.Runtime', 'netstandard') @'
using System.Collections.Generic;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;
public static class ScratchProbe {
    public static UserAttributeBuilder.Built Build(
        UserTemplate template, string first, string last, string upnSuffix,
        string[] extraKeys, string[] extraValues) {
        var extras = new Dictionary<string, string>();
        for (var i = 0; i < extraKeys.Length; i++) extras[extraKeys[i]] = extraValues[i];
        return UserAttributeBuilder.Build(new UserAttributeBuilder.Input {
            Template = template, FirstName = first, LastName = last,
            UpnSuffix = upnSuffix, ExtraAttributes = extras,
        });
    }
    public static string Sam(UserTemplate template, string first, string last) =>
        UserAttributeBuilder.ComputeSam(new UserAttributeBuilder.Input {
            Template = template, FirstName = first, LastName = last });
    public static UserAttributeBuilder.Suggestions Suggest(UserTemplate template, string first, string last, string suffix) =>
        UserAttributeBuilder.Suggest(new UserAttributeBuilder.Input {
            Template = template, FirstName = first, LastName = last, UpnSuffix = suffix });
}
'@
if (-not ('ScratchProbe' -as [type])) { throw 'the probe did not compile -- everything below would be a false pass' }

$none = [string[]]@()
$built = [ScratchProbe]::Build($null, 'Jane', 'Doe', 'contoso.net', $none, $none)
Check 'a cn is derived with no template' 'Jane Doe' $built.Attributes['cn']
Check '  and a logon name'               'jane.doe' $built.Attributes['sAMAccountName']
Check '  and a display name'             'Jane Doe' $built.Attributes['displayName']
Check '  and a UPN from the suffix'      'jane.doe@contoso.net' $built.Attributes['userPrincipalName']
Check '  givenName and sn too'           'Jane' $built.Attributes['givenName']
Check '  '                               'Doe' $built.Attributes['sn']
# The sam pattern lived in the template; without one it falls back rather than producing nothing.
Check 'the sam falls back to first.last' 'jane.doe' ([ScratchProbe]::Sam($null, 'Jane', 'Doe'))

# Suggest used to be skipped entirely with no template, so the UPN box stayed empty.
$sug = [ScratchProbe]::Suggest($null, 'Jane', 'Doe', 'contoso.net')
Check 'the UPN is still suggested'       'jane.doe@contoso.net' $sug.Upn
Check '  mail has nothing to suggest'    '' $sug.Email
Check '  nor do proxies'                 '' $sug.ProxyText

Write-Host "`n== the Details fields reach the attribute set ==" -ForegroundColor Cyan
$keys = [string[]]@('title', 'department', 'company')
$vals = [string[]]@('Buyer', 'Merchandising', 'LaCrosse')
$withExtras = [ScratchProbe]::Build($null, 'Jane', 'Doe', 'contoso.net', $keys, $vals)
Check 'a job title is written'          'Buyer' $withExtras.Attributes['title']
Check '  a department'                  'Merchandising' $withExtras.Attributes['department']
Check '  a company'                     'LaCrosse' $withExtras.Attributes['company']
Check '  and the derived ones survive'  'jane.doe' $withExtras.Attributes['sAMAccountName']

# An explicit value beats a template default -- the same rule mail, UPN and employee ID already follow.
$tpl = [UnifiedDirectoryManager.Models.UserTemplate]::new()
$tpl.AttributeDefaults['department'] = 'Warehouse'
$tpl.AttributeDefaults['company'] = 'LaCrosse'
$over = [ScratchProbe]::Build($tpl, 'Jane', 'Doe', 'contoso.net', [string[]]@('department'), [string[]]@('Merchandising'))
Check 'an explicit value overrides'     'Merchandising' $over.Attributes['department']
Check '  and leaves the rest alone'     'LaCrosse' $over.Attributes['company']

# sAMAccountName is computed from the names; letting it be set here would let the logon name disagree
# with the cn and UPN derived from it.
$samAttempt = [ScratchProbe]::Build($null, 'Jane', 'Doe', 'contoso.net', [string[]]@('sAMAccountName'), [string[]]@('hijacked'))
Check 'sAMAccountName cannot be hijacked' 'jane.doe' $samAttempt.Attributes['sAMAccountName']

# A blank field writes nothing rather than an empty attribute.
$blank = [ScratchProbe]::Build($null, 'Jane', 'Doe', 'contoso.net', [string[]]@('title'), [string[]]@('   '))
Check 'a blank field is not written'    $false ($blank.Attributes.ContainsKey('title'))

Write-Host "`n== the window exposes it ==" -ForegroundColor Cyan
$nuVm = Get-Content -Raw (Join-Path $srcDir 'ViewModels\NewUserViewModel.cs')
$nuXaml = Get-Content -Raw (Join-Path $srcDir 'Views\Dialogs\NewUserWindow.xaml')
# A null in the list rather than a sentinel template object, so nothing can be saved or exported by accident.
Check 'the entry is a choice, not null' $true ($nuVm -match 'Templates\.Add\(new TemplateChoice\(null\)\)')
Check '  the list is typed for it'      $true ($nuVm -match 'ObservableCollection<TemplateChoice> Templates')
Check '  the combo binds the choice'    $true ($nuXaml -match 'SelectedItem="\{Binding SelectedChoice\}"')
Check '  and shows its own label'       $true ($nuXaml -match 'DisplayMemberPath="Label"')
# The fallback is gone: the item carrying its own label is what makes the CLOSED box readable.
Check '  no TargetNullValue hack left'  $false ($nuXaml -match 'TargetNullValue=\(No template')
# The short circuit that made from-scratch build no attributes at all, so the create failed at
# "could not derive a common name" -- the very gate this feature was supposed to remove.
Check 'BuildAttributes has no gate'     $false ($nuVm -match 'SelectedTemplate is null[\s\S]{0,40}new Dictionary')
# The Details block appears only from scratch: a hidden field overriding a template default would be worse
# than not offering it.
Check 'Details are from-scratch only'   $true ($nuXaml -match 'IsFromScratch, Converter=\{StaticResource BoolToVis\}')
Check '  and so is the attribute set'   $true ($nuVm -match 'if \(!IsFromScratch\) return extras;')
foreach ($f in 'JobTitle', 'Department', 'Company', 'Office', 'Telephone', 'UserDescription') {
    Check "  the form binds $f"         $true ($nuXaml -match ("Binding " + $f + ","))
}
Check 'anything else is reachable'      $true ($nuXaml -match 'AddExtraAttributeCommand')
# Validation no longer demands a template; the cn and sam checks are the real requirement.
Check 'a template is not required'      $false ($nuVm -match 'Select a template first')

Write-Host "`n== the password and the pass are where you are looking ==" -ForegroundColor Cyan
# Both were in the LEFT scrollable column -- the password beside Generate, the pass inside the TAP
# settings. After a create the operator watches the progress log, and the two values they have to capture
# were off screen.
foreach ($w in 'NewUserWindow.xaml', 'CopyUserWindow.xaml') {
    $x = Get-Content -Raw (Join-Path $srcDir (Join-Path 'Views\Dialogs' $w))
    Check "  $w has the panel"           $true ($x -match 'Shown once — copy these now')
    Check "  spanning both columns"      $true ($x -match '<Border Grid\.Row="1" Grid\.ColumnSpan="2"')
    Check "  with both values"           $true (($x -match 'Binding GeneratedPassword, Mode=OneWay') -and ($x -match 'Binding TapCode, Mode=OneWay'))
    # Four in the window, not two: the originals stay beside the Generate button and the TAP settings,
    # and the panel adds its own. Scoped to the panel so it counts the ones being asserted about.
    $panel = [regex]::Match($x, '(?s)<Border Grid\.Row="1" Grid\.ColumnSpan="2".*?</Border>').Value
    Check "  the panel was found"        $true ($panel.Length -gt 0)
    Check "  with a Copy for each"       2 ([regex]::Matches($panel, 'Content="Copy"').Count)
    # It appears only once there is something to copy.
    Check "  hidden until there is one"  $true ($x -match 'HasSecrets, Converter=\{StaticResource BoolToVis\}')
    # The progress pane spans the window rather than a 340px column.
    Check "  progress spans the window"  $true ($x -match '<DockPanel Grid\.Row="2" Grid\.ColumnSpan="2"')
}
foreach ($vm in 'NewUserViewModel.cs', 'CopyUserViewModel.cs') {
    $v = Get-Content -Raw (Join-Path $srcDir (Join-Path 'ViewModels' $vm))
    Check "  $vm computes HasSecrets"    $true ($v -match 'public bool HasSecrets =>')
    # Computed, so it has to be told -- without these the panel never appears.
    Check "  and notifies on both"       $true (($v -match 'OnGeneratedPasswordChanged\(string value\) => OnPropertyChanged\(nameof\(HasSecrets\)\)') -and ($v -match 'OnTapCodeChanged\(string value\) => OnPropertyChanged\(nameof\(HasSecrets\)\)'))
}
# The panel shows them; it must not be what WRITES them. The creation-log suite owns that rule, and this
# checks the new markup did not quietly break it.
$nuXaml2 = Get-Content -Raw (Join-Path $srcDir 'Views\Dialogs\NewUserWindow.xaml')
Check 'the panel says they are not stored' $true ($nuXaml2 -match 'Neither is written to the log')


Write-Host "`n== which entry survives a list rebuild ==" -ForegroundColor Cyan
# This is what broke in the field: choosing "No template" appeared to do nothing, because the window
# reloads the template list whenever it is activated and the reload put the first template back.
Add-Type -ReferencedAssemblies @(
    (Join-Path $repoRoot 'debug\UnifiedDirectoryManager.dll'),
    'System.Collections', 'System.Runtime', 'netstandard') @'
using System.Collections.Generic;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;
public static class ChoiceProbe {
    // PowerShell cannot build the generic list of choices, so the names come in as strings, and
    // the previous selection as a mode: it binds $null to a string parameter as "", which would
    // read as a template named nothing rather than as "no previous selection".
    //   mode "none"    -> the window is opening, nothing selected yet
    //   mode "scratch" -> the operator chose "no template"
    //   anything else  -> the name of the template that was selected
    public static string Resolve(string[] names, string mode) {
        return Pick(names, mode, null, false);
    }
    // defaultMode: "unset" -> no default has ever been chosen (null in settings)
    //              "scratch" -> the stored default is "start from scratch" (the empty string)
    //              anything else -> a template name
    public static string WithDefault(string[] names, string mode, string defaultMode) {
        return Pick(names, mode, defaultMode, true);
    }
    private static string Pick(string[] names, string mode, string defaultMode, bool useDefault) {
        var choices = new List<TemplateChoice> { new TemplateChoice(null) };
        foreach (var n in names) choices.Add(new TemplateChoice(new UserTemplate { Name = n }));
        TemplateChoice previous =
            mode == "none"    ? null :
            mode == "scratch" ? new TemplateChoice(null)
                              : new TemplateChoice(new UserTemplate { Name = mode });
        string def = !useDefault || defaultMode == "unset" ? null
                   : defaultMode == "scratch" ? TemplateChoice.FromScratchSetting
                   : defaultMode;
        var picked = TemplateChoice.Resolve(choices, previous, def);
        return picked.IsFromScratch ? "<scratch>" : picked.Template.Name;
    }
    // What gets written to settings for a choice -- "" for from-scratch, the name otherwise.
    public static string StoredFor(string name) =>
        new TemplateChoice(name == null ? null : new UserTemplate { Name = name }).SettingValue;
    public static string StoredForScratch() => new TemplateChoice(null).SettingValue;
    public static string ScratchLabel() => new TemplateChoice(null).Label;
    public static string LabelOf(string name) => new TemplateChoice(new UserTemplate { Name = name }).Label;
}
'@
if (-not ('ChoiceProbe' -as [type])) { throw 'the choice probe did not compile -- everything below would be a false pass' }

$all = [string[]]@('CRC Consumer Sales Specialist', 'Seasonal CRC', 'Standard HQ User')

# The bug, stated as a test. From scratch was CHOSEN, so a reload must leave it chosen.
Check 'from scratch survives a reload'  '<scratch>' ([ChoiceProbe]::Resolve($all, 'scratch'))
# ...and keeps surviving. A reload fires on every activation, so once is not enough.
Check '  and a second reload'           '<scratch>' ([ChoiceProbe]::Resolve($all, 'scratch'))

# The usual flow is unchanged: opening the window lands on a real template, so from-scratch stays
# something chosen rather than something defaulted into.
Check 'the first load picks a template' 'CRC Consumer Sales Specialist' ([ChoiceProbe]::Resolve($all, 'none'))

# A selected template is kept across the rebuild, which is the whole point of the reload.
Check 'a chosen template is kept'       'Seasonal CRC' ([ChoiceProbe]::Resolve($all, 'Seasonal CRC'))
Check '  matched without case'          'Seasonal CRC' ([ChoiceProbe]::Resolve($all, 'SEASONAL crc'))

# Deleted in the template editor while New User was open: fall back rather than selecting nothing.
Check 'a deleted template falls back'   'CRC Consumer Sales Specialist' ([ChoiceProbe]::Resolve($all, 'Gone'))

# A tenant with no templates at all still has to select something, and from scratch is all there is.
$none2 = [string[]]@()
Check 'no templates -> from scratch'    '<scratch>' ([ChoiceProbe]::Resolve($none2, 'none'))
Check '  even chasing a deleted one'    '<scratch>' ([ChoiceProbe]::Resolve($none2, 'Gone'))

# The label is on the item, which is what makes the CLOSED combo readable -- a null item would show
# blank there however the item template is written.
Check 'the scratch entry has a label'   '(No template - start from scratch)' ([ChoiceProbe]::ScratchLabel().Replace([char]0x2014, '-'))
Check '  a template shows its name'     'Seasonal CRC' ([ChoiceProbe]::LabelOf('Seasonal CRC'))

Write-Host "`n== the default template the windows open on ==" -ForegroundColor Cyan
# Settings holds three states and they are NOT interchangeable: null means no default has been
# chosen (land on the first template, as before the setting existed), the empty string means start
# from scratch, anything else is a template name.

Check 'no default -> first by name'     'CRC Consumer Sales Specialist' ([ChoiceProbe]::WithDefault($all, 'none', 'unset'))
Check 'a default is honoured'           'Standard HQ User'              ([ChoiceProbe]::WithDefault($all, 'none', 'Standard HQ User'))
Check '  matched without case'          'Seasonal CRC'                  ([ChoiceProbe]::WithDefault($all, 'none', 'seasonal crc'))
Check '  from scratch can be default'   '<scratch>'                     ([ChoiceProbe]::WithDefault($all, 'none', 'scratch'))

# A template can be deleted or renamed after being made the default. Falling back beats refusing to
# open the window over a stale setting.
Check 'a deleted default falls back'    'CRC Consumer Sales Specialist' ([ChoiceProbe]::WithDefault($all, 'none', 'Gone'))
$none3 = [string[]]@()
Check '  with no templates at all'      '<scratch>'                     ([ChoiceProbe]::WithDefault($none3, 'none', 'Gone'))

# The default says where a window OPENS. It must not re-impose itself on every reload, or choosing
# anything else would be undone the next time the window was activated -- the bug that was just fixed.
Check 'a reload keeps the choice'       'Seasonal CRC' ([ChoiceProbe]::WithDefault($all, 'Seasonal CRC', 'Standard HQ User'))
Check '  from scratch over a default'   '<scratch>'    ([ChoiceProbe]::WithDefault($all, 'scratch', 'Standard HQ User'))

# What gets written to settings. The empty string is only safe as the from-scratch marker because the
# store refuses to save a template with a blank name -- asserted below, since the marker rests on it.
Check 'a template stores its name'      'Seasonal CRC' ([ChoiceProbe]::StoredFor('Seasonal CRC'))
Check '  from scratch stores empty'     ''             ([ChoiceProbe]::StoredForScratch())

# The empty string is only usable as the from-scratch marker because no real template can carry it.
# That rests on the store, so assert the store rather than assuming it.
$blankStore = New-Object UnifiedDirectoryManager.Services.TemplateStore ([System.IO.Path]::GetTempPath())
$blankTpl = New-Object UnifiedDirectoryManager.Models.UserTemplate
$refused = $false
foreach ($bad in '', '   ') {
    $blankTpl.Name = $bad
    try { $blankStore.Save($blankTpl); $refused = $false; break }
    catch { $refused = $true }
}
Check 'the store refuses a blank name'  $true $refused

Write-Host "`n== all three windows are wired to it ==" -ForegroundColor Cyan
$bcVm = Get-Content -Raw (Join-Path $srcDir 'ViewModels\BulkCreateUsersViewModel.cs')
$cuVm = Get-Content -Raw (Join-Path $srcDir 'ViewModels\CopyUserViewModel.cs')
$teVm = Get-Content -Raw (Join-Path $srcDir 'ViewModels\TemplateEditorViewModel.cs')
$teXaml = Get-Content -Raw (Join-Path $srcDir 'Views\Dialogs\TemplateEditorWindow.xaml')
$cuXaml = Get-Content -Raw (Join-Path $srcDir 'Views\Dialogs\CopyUserWindow.xaml')

Check 'New User consults the default'   $true ($nuVm -match 'Resolve\(Templates, previous, _settings\.DefaultTemplateName\)')
Check 'Bulk Create consults it'         $true ($bcVm -match 'DefaultTemplateName')
Check 'Copy user consults it'           $true ($cuVm -match 'MatchDefault\(NamingTemplates, settings\.DefaultTemplateName\)')
# Copy user keeps "Standard User" as the landing place for anyone who has not set a default.
Check '  still falls back to Standard User' $true ($cuVm -match 'Standard User')

# The editor is where it is set, and it writes on change rather than behind a Save button.
Check 'the editor offers the picker'    $true ($teXaml -match 'ItemsSource="\{Binding DefaultChoices\}"')
Check '  showing each label'            $true ($teXaml -match 'SelectedDefaultChoice[\s\S]{0,120}DisplayMemberPath="Label"')
Check '  and saves on change'           $true ($teVm -match 'OnSelectedDefaultChoiceChanged[\s\S]{0,600}_settingsStore\.Save')
Check '  storing the choice, not label' $true ($teVm -match 'DefaultTemplateName = value\.SettingValue')
# The picker must show what the windows will DO, not merely what is stored. Resolve is the call New
# User makes as it opens, so using it here means the two cannot disagree -- it matters when the stored
# default names a template that has since been renamed, where a bare MatchDefault yields nothing and
# the box would claim "no template" while New User opened on the first one.
Check '  the picker shows the truth'    $true ($teVm -match 'SelectedDefaultChoice = TemplateChoice\.Resolve\(DefaultChoices, null, _settings\.DefaultTemplateName\)')

# Copy user must show the choice label too, or its "no template" row renders blank -- the same WPF
# trap that made New User look like it had ignored the selection.
Check 'Copy user binds the choice'      $true ($cuXaml -match 'SelectedItem="\{Binding SelectedNamingChoice\}"')
Check '  and shows its label'           $true ($cuXaml -match 'NamingTemplates[\s\S]{0,160}DisplayMemberPath="Label"')
Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
