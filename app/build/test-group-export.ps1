<#
.SYNOPSIS
  Tests exporting group members to CSV, and appending to an existing export.

.DESCRIPTION
  The export is flat -- one row per member, group repeated -- because several groups go into one file and a
  reader has to be able to tell whose member is whose.

  Two things carry most of the risk.

  First, a group's membership can come back INCOMPLETE (the range walk stopped early) or UNREADABLE (LDAP
  omits the `member` attribute identically for an empty group and one you may not read). A CSV that shows
  neither would be a confident-looking lie, and this file gets mailed around separately from any dialog. So
  the warning is written into the rows, and a group that produced nothing still gets a row saying which of
  the two happened rather than vanishing from the export.

  Second, appending. Adding five-column rows to somebody's three-column spreadsheet produces a file that
  parses as neither, and nobody finds out until they open it. So an append to a file that is not one of
  these exports is refused, and the join is checked for the missing-trailing-newline case that would
  otherwise glue the first new row onto the last old one.

  Run with:  pwsh -NoProfile -File ./app/build/test-group-export.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dll = Join-Path $repoRoot 'debug\UnifiedDirectoryManager.dll'
if (-not (Test-Path $dll)) { throw "Build first — could not find $dll" }
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

$Csv = [UnifiedDirectoryManager.Services.GroupMemberCsv]
$Member = [UnifiedDirectoryManager.Services.GroupMember]
$Result = [UnifiedDirectoryManager.Services.GroupMembersResult]

function Members([string[]]$names, [bool]$truncated = $false, [bool]$unconfirmed = $false) {
    $list = [System.Collections.Generic.List[UnifiedDirectoryManager.Services.GroupMember]]::new()
    foreach ($n in $names) { $list.Add($Member::new($n, "CN=$n,OU=Staff,DC=contoso,DC=net")) }
    return $Result::new($list, $truncated, $unconfirmed)
}

function Rows($groupName, $groupDn, $members) { return ,@($Csv::Rows($groupName, $groupDn, $members)) }

$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("udm-gx-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $sandbox | Out-Null

try {

Write-Host "`n== one row per member, with the group on every row ==" -ForegroundColor Cyan
# The group repeats because several groups share one file; sorting by that column recovers the grouping.
$rows = Rows 'All Staff' 'CN=All Staff,OU=Groups,DC=contoso,DC=net' (Members @('Jane Doe', 'Bob Roe'))
Check 'two members, two rows'        2 $rows.Count
Check 'the group is on the first'    $true ($rows[0] -like 'All Staff,*')
Check 'and on the second'            $true ($rows[1] -like 'All Staff,*')
Check 'the member name is there'     $true ($rows[0] -like '*Jane Doe*')
Check 'and their DN'                 $true ($rows[0] -like '*CN=Jane Doe,OU=Staff,DC=contoso,DC=net*')
# A clean read leaves the status column empty, so the common case reads cleanly in a spreadsheet.
Check 'a clean row has no status'    $true ($rows[0].EndsWith(','))

Write-Host "`n== a truncated read says so on EVERY row ==" -ForegroundColor Cyan
# Not one marker row: the first thing anyone does with a member list is sort it, and a single marker would
# end up somewhere unrelated to the group it belongs to.
$rows = Rows 'Everyone' 'CN=Everyone,DC=contoso,DC=net' (Members @('A', 'B', 'C') $true)
Check 'every member is still exported' 3 $rows.Count
$warned = @($rows | Where-Object { $_ -like "*$($Csv::IncompleteStatus)*" })
Check 'and every row carries the warning' 3 $warned.Count

Write-Host "`n== a group that produced nothing still appears ==" -ForegroundColor Cyan
# Dropping it would leave a reader unable to tell an empty group from one that was silently skipped.
$confirmedEmpty = Rows 'Empty Group' 'CN=Empty,DC=contoso,DC=net' (Members @())
Check 'a confirmed-empty group gets a row' 1 $confirmedEmpty.Count
Check 'saying it has no members'           $true ($confirmedEmpty[0] -like "*$($Csv::EmptyStatus)*")
Check 'and naming the group'               $true ($confirmedEmpty[0] -like 'Empty Group,*')
# The reported bug: an ordinary empty group was being called unreadable, because AD omits the member
# attribute for one exactly as it does for the other and the read took the cautious reading every time.
Check 'and NOT that it is unreadable'      $false ($confirmedEmpty[0] -like "*UNREADABLE*")

# THE DANGEROUS ONE, and now a genuinely rare case. The read settles empty-vs-unreadable with the memberOf
# back-link before setting this flag, so Unconfirmed reaching here means the group really HAS members the
# signed-in account cannot see. It must never be presented as "no members".
$cannotRead = Rows 'Locked Group' 'CN=Locked,DC=contoso,DC=net' (Members @() $false $true)
Check 'an unreadable group gets a row'     1 $cannotRead.Count
Check 'saying it could not be read'        $true ($cannotRead[0] -like "*$($Csv::UnreadableStatus)*")
Check 'and NOT that it is empty'           $false ($cannotRead[0] -like "*$($Csv::EmptyStatus)*")

Write-Host "`n== cells that would break a CSV are encoded ==" -ForegroundColor Cyan
# A group called "Doe, Jane" is ordinary, and a display name beginning with = is a formula injection.
$rows = Rows 'Sales, West' 'CN=Sales\, West,DC=contoso,DC=net' (Members @('=cmd|calc'))
Check 'a comma in the name is quoted'  $true ($rows[0] -like '"Sales, West"*')
Check 'and a formula is neutralised'   $true ($rows[0] -like "*'=cmd|calc*")

Write-Host "`n== a fresh file gets a header, once ==" -ForegroundColor Cyan
$fresh = Join-Path $sandbox 'fresh.csv'
$plan = $Csv::FreshFile
$text = $Csv::Compose($plan, [string[]]@('a,b,c,d,', 'e,f,g,h,'))
[System.IO.File]::WriteAllText($fresh, $text, [System.Text.UTF8Encoding]::new($true))
$lines = @(Get-Content -LiteralPath $fresh)
Check 'the header is first'          $Csv::Header $lines[0]
Check 'then the rows'                3 $lines.Count
Check 'and it ends with a newline'   $true ((Get-Content -LiteralPath $fresh -Raw).EndsWith([Environment]::NewLine))

Write-Host "`n== appending to one of these exports adds no second header ==" -ForegroundColor Cyan
$plan = $Csv::PlanAppend($fresh)
Check 'it can be appended to'        $true  $plan.CanWrite
Check 'with no header'               $false $plan.WriteHeader
Check 'and no joining newline'       $false $plan.PrefixNewline
[System.IO.File]::AppendAllText($fresh, $Csv::Compose($plan, [string[]]@('x,y,z,w,')))
$lines = @(Get-Content -LiteralPath $fresh)
Check 'the file grew by one row'     4 $lines.Count
Check 'the header is still once'     1 (@($lines | Where-Object { $_ -eq $Csv::Header }).Count)
Check 'and the new row is last'      'x,y,z,w,' $lines[3]

Write-Host "`n== a file with no trailing newline is joined properly ==" -ForegroundColor Cyan
# Without this the first appended row is glued onto the last existing one and BOTH are lost.
$ragged = Join-Path $sandbox 'ragged.csv'
[System.IO.File]::WriteAllText($ragged, $Csv::Header + [Environment]::NewLine + 'a,b,c,d,')  # no trailing break
$plan = $Csv::PlanAppend($ragged)
Check 'the missing break is noticed' $true $plan.PrefixNewline
[System.IO.File]::AppendAllText($ragged, $Csv::Compose($plan, [string[]]@('e,f,g,h,')))
$lines = @(Get-Content -LiteralPath $ragged)
Check 'the old last row survives'    'a,b,c,d,' $lines[1]
Check 'the new row is its own line'  'e,f,g,h,' $lines[2]
Check 'and nothing was glued'        3 $lines.Count

Write-Host "`n== appending to somebody else's spreadsheet is refused ==" -ForegroundColor Cyan
# The damage this prevents is silent: a file that parses as neither thing, found weeks later.
$foreign = Join-Path $sandbox 'budget.csv'
Set-Content -LiteralPath $foreign -Value "Account,Amount`n4000,1234" -NoNewline
$plan = $Csv::PlanAppend($foreign)
Check 'it is refused'                $false $plan.CanWrite
Check 'and says why'                 $true  ($plan.Problem -like '*not a group-member export*')
Check 'showing what it expected'     $true  ($plan.Problem -like '*Group,Group DN*')
Check 'and what it found'            $true  ($plan.Problem -like '*Account,Amount*')
Check 'the file is untouched'        "Account,Amount`n4000,1234" (Get-Content -LiteralPath $foreign -Raw)

Write-Host "`n== a missing or empty file is just a fresh one ==" -ForegroundColor Cyan
# Appending to a file that is not there should start it, not fail.
$plan = $Csv::PlanAppend((Join-Path $sandbox 'not-here.csv'))
Check 'a missing file can be written' $true $plan.CanWrite
Check 'and gets a header'             $true $plan.WriteHeader

$blank = Join-Path $sandbox 'blank.csv'
[System.IO.File]::WriteAllText($blank, '')
$plan = $Csv::PlanAppend($blank)
Check 'an empty file too'             $true $plan.CanWrite
Check 'and gets a header'             $true $plan.WriteHeader

Write-Host "`n== a header behind a byte-order mark still matches ==" -ForegroundColor Cyan
# The fresh export writes a BOM so Excel opens it as UTF-8. Appending has to recognise its own output.
$bom = Join-Path $sandbox 'bom.csv'
[System.IO.File]::WriteAllText($bom, $Csv::Header + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($true))
$plan = $Csv::PlanAppend($bom)
Check 'the BOM does not fool it'     $true  $plan.CanWrite
Check 'and no second header is added' $false $plan.WriteHeader

Write-Host "`n== the round trip reads back as a table ==" -ForegroundColor Cyan
# The end-to-end claim: what comes out opens as a spreadsheet with the columns promised.
$round = Join-Path $sandbox 'round.csv'
$all = @()
$all += Rows 'All Staff' 'CN=All Staff,DC=contoso,DC=net' (Members @('Jane Doe', 'Bob Roe'))
$all += Rows 'Sales, West' 'CN=Sales,DC=contoso,DC=net' (Members @('Amy Lee'))
[System.IO.File]::WriteAllText($round, $Csv::Compose($Csv::FreshFile, [string[]]$all), [System.Text.UTF8Encoding]::new($true))

$table = @(Import-Csv -LiteralPath $round)
Check 'three member rows'            3 $table.Count
Check 'the columns are named'        'Group Group DN Member Member DN Status' (($table[0].PSObject.Properties.Name) -join ' ')
Check 'the first group'              'All Staff' $table[0].Group
Check 'its member'                   'Jane Doe' $table[0].Member
Check 'the comma in a group name survived' 'Sales, West' $table[2].Group
Check 'and the status is blank when clean' '' $table[0].Status

}
finally { Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host "`n== the menu offers both, only for groups (mutation check) ==" -ForegroundColor Cyan
$menu = Get-Content -Raw (Join-Path $repoRoot 'app\src\UnifiedDirectoryManager\Views\Controls\ObjectListView.xaml')
Check 'export is offered'            $true ($menu -match 'ExportGroupMembersCommand')
Check 'append is offered'            $true ($menu -match 'AppendGroupMembersCommand')
$exportItem = [regex]::Match($menu, '(?s)<MenuItem Header="Export members to CSV[^/]*/>').Value
$appendItem = [regex]::Match($menu, '(?s)<MenuItem Header="Append members to a CSV[^/]*/>').Value
Check 'export is group-only'         $true ($exportItem -match 'SelectionHasGroups')
Check 'append is group-only'         $true ($appendItem -match 'SelectionHasGroups')

$vm = Get-Content -Raw (Join-Path $repoRoot 'app\src\UnifiedDirectoryManager\ViewModels\MainViewModel.cs')
Check 'the flag tracks the selection' $true ($vm -match 'SelectionHasGroups = rows\.Any\(r => r\.Type == AdObjectType\.Group\)')
# Append must open an EXISTING file, never go through the save dialog — whose own "replace?" prompt means
# the opposite of what the operator asked for.
$append = [regex]::Match($vm, '(?s)if \(append\)\s*\{.*?\}\s*else').Value
Check 'append picks an existing file' $true  ($append -match 'PromptOpenFile')
Check 'and never the save dialog'     $false ($append -match 'PromptSaveFile')
# An append must not write a BOM into the middle of the file.
Check 'append writes no encoding'     $true ($vm -match 'File\.AppendAllText\(path, text\);')

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
