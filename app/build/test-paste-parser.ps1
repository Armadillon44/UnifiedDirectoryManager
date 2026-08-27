<#
.SYNOPSIS
  Checks PastedMemberParser — the parsing and the ladder rules behind "add members from a list".

.DESCRIPTION
  This is the part of the batch add that fails QUIETLY. A mis-parsed line does not throw; it resolves to a
  real person who is not the one that was pasted, and lands them in a group. So the parse and the rung rules
  are tested on their own, against the built assembly, with no directory in the way.

  Run with:  pwsh -NoProfile -File ./app/build/test-paste-parser.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dll = Join-Path (Split-Path -Parent $root) 'debug\UnifiedDirectoryManager.dll'
if (-not (Test-Path $dll)) { throw "Build first — could not find $dll" }
[System.Reflection.Assembly]::LoadFrom($dll) | Out-Null

$P = [UnifiedDirectoryManager.Services.PastedMemberParser]
$Shape = [UnifiedDirectoryManager.Models.PastedTermShape]
$Kind = [UnifiedDirectoryManager.Services.MemberLookupKind]
$Match = [UnifiedDirectoryManager.Models.MemberMatch]

$pass = 0; $fail = 0
function Check([string]$name, $expected, $actual) {
    if ($expected -eq $actual) { $script:pass++; Write-Host "  PASS  $name" -ForegroundColor Green }
    else {
        $script:fail++
        Write-Host "  FAIL  $name" -ForegroundColor Red
        Write-Host "          expected: $expected"
        Write-Host "          actual:   $actual"
    }
}
function Cand([string]$id, [string]$name) {
    [UnifiedDirectoryManager.Models.MemberCandidate]::new($id, $name, $null, 'User')
}
function Term([string]$text) { $P::Classify(1, $text, $P::Clean($text)) }

Write-Host "`n== Clean: a paste arrives decorated ==" -ForegroundColor Cyan
# Every one of these is something a real paste carries and none of it is part of the name. Left in place,
# each one turns an exact match into a search, and a search never resolves a row on its own.
Check 'plain name is untouched'      'Jane Doe' $P::Clean('Jane Doe')
Check 'surrounding space'            'Jane Doe' $P::Clean('   Jane Doe   ')
Check 'tabs from a spreadsheet'      'Jane Doe' $P::Clean("Jane`tDoe")
Check 'runs of spaces collapse'      'Jane Doe' $P::Clean('Jane    Doe')
Check 'bulleted list'                'Jane Doe' $P::Clean('- Jane Doe')
Check 'bullet character'             'Jane Doe' $P::Clean('• Jane Doe')
Check 'numbered list'                'Jane Doe' $P::Clean('1. Jane Doe')
Check 'trailing semicolon (Outlook)' 'Jane Doe' $P::Clean('Jane Doe;')
Check 'quoted CSV cell'              'Doe, Jane' $P::Clean('"Doe, Jane"')
Check 'address in angle brackets'    'jane.doe@contoso.com' $P::Clean('<jane.doe@contoso.com>')
Check 'name and address together'    'jane.doe@contoso.com' $P::Clean('Jane Doe <jane.doe@contoso.com>')
Check 'blank stays blank'            '' $P::Clean('   ')

Write-Host "`n== Classify: which rung to start on ==" -ForegroundColor Cyan
Check 'an address'        $Shape::UpnOrSmtp   (Term 'jane.doe@contoso.com').Shape
Check 'a logon name'      $Shape::AccountName (Term 'jdoe').Shape
Check 'a display name'    $Shape::DisplayName (Term 'Jane Doe').Shape
Check 'last, first'       $Shape::LastFirst   (Term 'Doe, Jane').Shape
# The flip matters: the directory stores "Jane Doe", so searching the pasted order finds nobody.
Check 'last, first is flipped for searching' 'Jane Doe' (Term 'Doe, Jane').SearchText
Check 'and keeps the pasted term'            'Doe, Jane' (Term 'Doe, Jane').Term
Check 'a middle initial survives the flip'   'Jane A. Doe' (Term 'Doe, Jane A.').SearchText
# An address containing a comma is still an address; the comma rule only applies once @ is ruled out.
Check 'an address wins over the comma rule'  $Shape::UpnOrSmtp (Term 'doe,jane@contoso.com').Shape
Check 'a trailing comma is not Last, First'  $Shape::DisplayName (Term 'Jane Doe,').Shape
Check 'other text searches on itself'        'Jane Doe' (Term 'Jane Doe').SearchText

Write-Host "`n== Parse: a whole paste ==" -ForegroundColor Cyan
$r = $P::Parse("Jane Doe`njdoe`n`nDoe, John`n")
Check 'blank lines are skipped, not errors' 3 $r.Terms.Count
Check 'line numbers trace back to the paste' 4 $r.Terms[2].LineNumber
$r = $P::Parse("Jane Doe`nJANE DOE`n  Jane Doe  ")
Check 'the same person once, whatever the casing' 1 $r.Terms.Count
Check 'and the repeats are counted'               2 $r.Duplicates
$r = $P::Parse((1..120 | ForEach-Object { "User $_" }) -join "`n", 100)
Check 'the cap is enforced'      100 $r.Terms.Count
Check 'and the overflow counted' 20  $r.Dropped
Check 'an empty paste is empty, not an error' 0 $P::Parse('').Terms.Count
Check 'a null paste is empty too'             0 $P::Parse($null).Terms.Count

Write-Host "`n== Ladder: detection starts, it does not decide ==" -ForegroundColor Cyan
# A term that looked like a logon name has to fall through to a display-name lookup and then a search,
# or someone whose alias does not match what was pasted is reported as missing when they are not.
$l = $P::Ladder($Shape::AccountName)
Check 'a logon name falls through to a name' $true ($l -contains $Kind::DisplayName)
Check 'and then to a search'                 $true ($l -contains $Kind::Search)
Check 'every shape ends in a search' $true (@($Shape::UpnOrSmtp, $Shape::LastFirst, $Shape::AccountName, $Shape::DisplayName) |
    ForEach-Object { $P::Ladder($_)[-1] -eq $Kind::Search } | Where-Object { -not $_ } | Measure-Object).Count.Equals(0)
# There is no identifier inside "Doe, Jane", so trying one is a wasted round trip on a serialised channel.
$l = $P::Ladder($Shape::LastFirst)
Check 'last, first does not try an address'    $false ($l -contains $Kind::Address)
Check 'nor a logon name'                       $false ($l -contains $Kind::AccountName)

Write-Host "`n== FromRung: only an exact match resolves itself ==" -ForegroundColor Cyan
$t = Term 'Jane Doe'
$one = [UnifiedDirectoryManager.Models.MemberCandidate[]]@((Cand 'jane@contoso.com' 'Jane Doe'))
$two = [UnifiedDirectoryManager.Models.MemberCandidate[]]@((Cand 'jane@contoso.com' 'Jane Doe'), (Cand 'jane2@contoso.com' 'Jane Doe'))
$none = [UnifiedDirectoryManager.Models.MemberCandidate[]]@()

Check 'one exact hit resolves'            $Match::Resolved ($P::FromRung($t, $Kind::Address, $one)).Match
Check 'and carries the person'            'jane@contoso.com' ($P::FromRung($t, $Kind::Address, $one)).Chosen.Identity
# The whole point: two people really do share a display name, and picking the first adds the wrong one.
Check 'two exact hits ask'                $Match::Choose ($P::FromRung($t, $Kind::DisplayName, $two)).Match
Check 'and choose nobody by itself'       $null ($P::FromRung($t, $Kind::DisplayName, $two)).Chosen
Check 'both are offered'                  2 ($P::FromRung($t, $Kind::DisplayName, $two)).Candidates.Count
# A single fuzzy hit is still a guess about a half-specified name.
Check 'one SEARCH hit still asks'         $Match::Choose ($P::FromRung($t, $Kind::Search, $one)).Match
Check 'and chooses nobody'                $null ($P::FromRung($t, $Kind::Search, $one)).Chosen
Check 'an empty rung falls through'       $null ($P::FromRung($t, $Kind::Address, $none))
Check 'a null rung falls through too'     $null ($P::FromRung($t, $Kind::Address, $null))
Check 'nothing anywhere is Not found'     $Match::NotFound ($P::NotFound($t)).Match

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
