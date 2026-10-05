<#
.SYNOPSIS
  Tests the Deleted Objects view (AD Recycle Bin).

.DESCRIPTION
  What is NOT tested here, and cannot be: the LDAP read itself. It needs a live domain with a Deleted
  Objects container, a Show Deleted Objects control, and an account holding a right that is Domain Admin
  by default. Nothing in this repository can stand that up, and a fake that answers the way I expect
  would only confirm that I expect it.

  So the seam is drawn so that everything ABOVE the LDAP call is primitives: the filter string, the
  Recycle Bin decision, the lifetime fallback, the name a deleted object displays under, the parent path,
  and the sentence that tells "nothing deleted" apart from "not allowed to look". Those are what this
  file covers, and they are where the quiet mistakes live -- a deleted object's cn is NOT its name, and
  an unset lifetime attribute means 180 days rather than zero.

  Run with:  pwsh -NoProfile -File ./app/build/test-deleted-objects.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dll = Join-Path $repoRoot 'debug\UnifiedDirectoryManager.dll'
if (-not (Test-Path $dll)) { throw "Build first - could not find $dll" }
[System.Reflection.Assembly]::LoadFrom($dll) | Out-Null

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

$DO = [UnifiedDirectoryManager.Services.DeletedObjects]
$Status = [UnifiedDirectoryManager.Services.DeletedObjectsStatus]

function Call1($method, $arg) { $b = [object[]]::new(1); $b[0] = $arg; return $method.Invoke($null, $b) }
function Call2($method, $a1, $a2) { $b = [object[]]::new(2); $b[0] = $a1; $b[1] = $a2; return $method.Invoke($null, $b) }

Write-Host "`n== is the Recycle Bin on? ==" -ForegroundColor Cyan
# Enabling it writes the feature's DN into msDS-EnabledFeature on the Partitions container. The rest of
# that DN contains the FOREST ROOT's naming context, which differs from the domain's in any multi-domain
# forest, so only the leading RDN can be matched.
$isOn = $DO.GetMethod('IsRecycleBinEnabled')
$realDn = 'CN=Recycle Bin Feature,CN=Optional Features,CN=Directory Service,CN=Windows NT,CN=Services,CN=Configuration,DC=contoso,DC=net'
Check 'the real feature DN counts'      $true  (Call1 $isOn ([string[]]@($realDn)))
Check '  even beside other features'    $true  (Call1 $isOn ([string[]]@('CN=Privileged Access Management Feature,CN=Optional Features,DC=x', $realDn)))
Check '  and in a child domain'         $true  (Call1 $isOn ([string[]]@($realDn.Replace('DC=contoso,DC=net', 'DC=corp,DC=contoso,DC=net'))))
Check '  case does not matter'          $true  (Call1 $isOn ([string[]]@('cn=recycle bin feature,CN=Optional Features,DC=x')))
Check 'no features means off'           $false (Call1 $isOn ([string[]]@()))
Check '  and so does null'              $false (Call1 $isOn $null)
Check '  a different feature is not it' $false (Call1 $isOn ([string[]]@('CN=Privileged Access Management Feature,CN=Optional Features,DC=x')))
# A blank entry must not be read as a match, or an empty attribute would report the bin as enabled.
Check '  nor is a blank entry'          $false (Call1 $isOn ([string[]]@('', '   ')))

Write-Host "`n== how long deletions stay recoverable ==" -ForegroundColor Cyan
# The trap: an UNSET lifetime attribute means "use the default", not zero. Reading it as zero would tell
# the operator everything had already expired.
$lifetime = $DO.GetMethod('ResolveLifetimeDays')
Check 'the deleted-object lifetime wins' 365 (Call2 $lifetime 365 180)
Check '  tombstone lifetime is the fallback' 60 (Call2 $lifetime $null 60)
Check '  unset means 180, not 0'        180 (Call2 $lifetime $null $null)
Check '  and so does zero'              180 (Call2 $lifetime 0 0)
Check '  a negative is not believed'    180 (Call2 $lifetime ([int]-1) $null)

Write-Host "`n== the search filter ==" -ForegroundColor Cyan
# isRecycled does not exist on any object in a domain that never had the Recycle Bin on. Absent is not
# TRUE, so the negated clause is right there too and needs no special case.
$filter = $DO.GetMethod('Filter')
Check 'recoverable only'                '(&(isDeleted=TRUE)(!(isRecycled=TRUE)))' (Call1 $filter $false)
Check 'everything'                      '(isDeleted=TRUE)' (Call1 $filter $true)

Write-Host "`n== a deleted object's cn is not its name ==" -ForegroundColor Cyan
# AD rewrites the RDN to <name>\0ADEL:<guid> so that two objects deleted from different places cannot
# collide inside one flat container. Showing that raw puts "Jane Doe\0ADEL:4f2c..." in front of someone.
$strip = $DO.GetMethod('StripDeletedSuffix')
Check 'the escaped form is cut'         'Jane Doe' (Call1 $strip 'Jane Doe\0ADEL:4f2c9e21-1111-2222-3333-444455556666')
Check '  and the decoded one'           'Jane Doe' (Call1 $strip "Jane Doe`nDEL:4f2c9e21-1111-2222-3333-444455556666")
Check '  a normal name is untouched'    'Jane Doe' (Call1 $strip 'Jane Doe')
Check '  a name containing DEL survives' 'DELTA Team' (Call1 $strip 'DELTA Team')
Check '  blank stays blank'             '' (Call1 $strip '')
Check '  and null does too'             '' (Call1 $strip $null)

$display = $DO.GetMethod('DisplayName')
Check 'msDS-LastKnownRDN is preferred'  'Jane Doe' (Call2 $display 'Jane Doe' 'Jane Doe\0ADEL:4f2c')
# Tombstones from before the Recycle Bin was enabled have no msDS-LastKnownRDN, so cn has to be salvaged.
Check '  cn is salvaged without it'     'Jane Doe' (Call2 $display $null 'Jane Doe\0ADEL:4f2c')
Check '  and a blank one is skipped'    'Jane Doe' (Call2 $display '   ' 'Jane Doe\0ADEL:4f2c')
Check '  nothing at all is blank'       '' (Call2 $display $null $null)

Write-Host "`n== where it used to live ==" -ForegroundColor Cyan
# A DN is too wide for a column, and every row shares the domain components, so they go.
$short = $DO.GetMethod('ShortenParent')
Check 'a nested OU reads top-down'      'Sales/Users' (Call1 $short 'OU=Users,OU=Sales,DC=contoso,DC=net')
Check '  one level'                     'Sales' (Call1 $short 'OU=Sales,DC=contoso,DC=net')
Check '  the domain root says so'       '(domain root)' (Call1 $short 'DC=contoso,DC=net')
Check '  blank stays blank'             '' (Call1 $short '')
# A parent that was itself deleted carries the same uniquifier.
Check '  a deleted parent is cleaned'   'Sales' (Call1 $short 'OU=Sales\0ADEL:4f2c9e21-1111-2222-3333-444455556666,DC=contoso,DC=net')
# An escaped comma is part of the name, not a separator -- "OU=Sales, EMEA" is one component.
Check '  an escaped comma holds'        'Sales\, EMEA' (Call1 $short 'OU=Sales\, EMEA,DC=contoso,DC=net')
Check '  CN containers work too'        'Users' (Call1 $short 'CN=Users,DC=contoso,DC=net')

$split = $DO.GetMethod('SplitDn')
Check 'splitting keeps escaped commas'  2 (@(Call1 $split 'OU=Sales\, EMEA,DC=net').Count)
Check '  and splits ordinary ones'      3 (@(Call1 $split 'OU=Users,OU=Sales,DC=net').Count)

Write-Host "`n== the container's DN ==" -ForegroundColor Cyan
Check 'it hangs off the domain NC'      'CN=Deleted Objects,DC=contoso,DC=net' (Call1 $DO.GetMethod('ContainerDn') 'DC=contoso,DC=net')

Write-Host "`n== telling the failures apart ==" -ForegroundColor Cyan
# Matched on Win32 codes, not on message text: AD's prose is localised and this has to work on a German
# workstation, but the numeric codes in its extended errors are not.
$classify = $DO.GetMethod('Classify')
Check 'insufficient access rights'      $Status::AccessDenied (Call2 $classify ([int]0x80072098) '')
Check '  plain access denied'           $Status::AccessDenied (Call2 $classify ([int]0x80070005) '')
Check '  no such object'                $Status::NotFound (Call2 $classify ([int]0x80072030) '')
Check '  anything else is Failed'       $Status::Failed (Call2 $classify ([int]0x8007000E) '')
# The extended error carries the code in text when the COM layer did not surface a distinct HRESULT.
Check 'the extended error is read'      $Status::AccessDenied (Call2 $classify 0 '00002098: SecErr: DSID-031521D0, problem 4003 (INSUFF_ACCESS_RIGHTS)')
Check '  and for a missing container'   $Status::NotFound (Call2 $classify 0 '00002030: NameErr: DSID-03100238, problem 2001 (NO_OBJECT)')
Check '  unrelated text is not'         $Status::Failed (Call2 $classify 0 'The server is not operational.')

Write-Host "`n== the sentence above the list ==" -ForegroundColor Cyan
# THE ASSERTION THIS FILE EXISTS FOR. "Nothing has been deleted", "you are not allowed to look" and "the
# Recycle Bin is off" all produce an empty grid. This app has already shipped a bug where an empty group
# was reported as unreadable, so the three are pulled apart here and not at three call sites.
$RowType = [UnifiedDirectoryManager.Services.DeletedObjectRow]
$ResultType = [UnifiedDirectoryManager.Services.DeletedObjectsResult]
$empty = [System.Collections.Generic.List[UnifiedDirectoryManager.Services.DeletedObjectRow]]::new()
function Result($status, $rows, $enabled, $days, $msg) { return $ResultType::new($status, $rows, $enabled, $days, $msg) }
function Describe($result, [bool]$includeRecycled) {
    $b = [object[]]::new(3); $b[0] = $result; $b[1] = 'contoso.net'; $b[2] = $includeRecycled
    return $DO.GetMethod('Describe').Invoke($null, $b)
}

$denied = Describe (Result $Status::AccessDenied $empty $false 180 'x') $false
Check 'access denied says so'           $true ($denied -like '*do not have permission*')
Check '  and names why it is likely'    $true ($denied -like '*Domain Admin*')
Check '  without claiming it is empty'  $false ($denied -like '*Nothing*')

$missing = Describe (Result $Status::NotFound $empty $false 180 $null) $false
Check 'a missing container says so'     $true ($missing -like '*No Deleted Objects container*contoso.net*')

$failed = Describe (Result $Status::Failed $empty $false 180 'The server is not operational.') $false
Check 'a failure quotes the server'     $true ($failed -like '*could not be read*server is not operational*')

$none = Describe (Result $Status::Ok $empty $true 180 $null) $false
Check 'an empty container is not an error' $true ($none -like '*Recycle Bin is enabled*')
Check '  and says nothing was deleted'  $true ($none -like '*Nothing recoverable has been deleted*180 days*')
Check '  pointing at the tick box'      $true ($none -like '*Include recycled*')
$noneAll = Describe (Result $Status::Ok $empty $true 180 $null) $true
Check '  which it drops once ticked'    $false ($noneAll -like '*Include recycled*')

$off = Describe (Result $Status::Ok $empty $false 60 $null) $false
Check 'a disabled bin is called out'    $true ($off -like '*NOT enabled*')
Check '  explaining what tombstones are' $true ($off -like '*tombstones*cannot be restored*')
Check '  with the right lifetime'       $true ($off -like '*60 days*')

$row = $RowType::new('Jane Doe', 'CN=Jane Doe\0ADEL:4f2c,CN=Deleted Objects,DC=contoso,DC=net',
                     [UnifiedDirectoryManager.Models.AdObjectType]::User, 'OU=Sales,DC=contoso,DC=net',
                     [datetime]'2026-09-28T14:02:11', 'jdoe', $false, '4f2c9e21-1111-2222-3333-444455556666')
$oneRow = [System.Collections.Generic.List[UnifiedDirectoryManager.Services.DeletedObjectRow]]::new()
$oneRow.Add($row)
$some = Describe (Result $Status::Ok $oneRow $true 180 $null) $false
Check 'a populated list just describes' $true ($some -like '*Recycle Bin is enabled*')
Check '  and does not say nothing went' $false ($some -like '*Nothing*')

Write-Host "`n== the row itself ==" -ForegroundColor Cyan
Check 'the date is fixed-format'        '2026-09-28 14:02' $row.DeletedOnText
Check '  and blank when AD had none'    '' ($RowType::new('x', 'y', [UnifiedDirectoryManager.Models.AdObjectType]::User, 'z', $null, '', $false, '')).DeletedOnText
Check 'the parent is shortened'         'Sales' $row.LastKnownParentText
Check 'the state reads plainly'         'Deleted' $row.StateText
Check '  and so does a recycled one'    'Recycled' ($RowType::new('x', 'y', [UnifiedDirectoryManager.Models.AdObjectType]::User, 'z', $null, '', $true, '')).StateText

Write-Host "`n== the window asks for what it is showing ==" -ForegroundColor Cyan
$VmType = [UnifiedDirectoryManager.ViewModels.DeletedObjectsViewModel]
$matches = $VmType.GetMethod('Matches', [System.Reflection.BindingFlags]'NonPublic,Static')
Check 'no filter matches everything'    $true  (Call2 $matches $row '')
Check '  the name matches'              $true  (Call2 $matches $row 'jane')
Check '  the logon name matches'        $true  (Call2 $matches $row 'JDOE')
Check '  where it lived matches'        $true  (Call2 $matches $row 'sales')
Check '  and something absent does not' $false (Call2 $matches $row 'zzz')
# The filter must not match the raw deleted DN, or typing "DEL" would return the whole container.
Check '  the mangled DN is not searched' $false (Call2 $matches $row 'ADEL')

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
