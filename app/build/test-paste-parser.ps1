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
function Term([string]$text) { $P::Classify(0, 1, $text, $P::Clean($text)) }

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


Write-Host "`n== SplitRecipients: an Outlook address line holds several people ==" -ForegroundColor Cyan
# The worst failure this feature can have: copying a To:/Cc: field and keeping only ONE of the recipients.
# The surviving row looks perfectly correct, so nothing about the screen says anyone was lost.
$multi = 'Jane Doe <jane@contoso.com>; Bob Smith <bob@contoso.com>; Amy Lee <amy@contoso.com>'
Check 'three recipients on one line become three' 3 (@($P::SplitRecipients($multi))).Count
$r = $P::Parse($multi)
Check 'and three terms come out of Parse'         3 $r.Terms.Count
Check 'the first is not lost'                     'jane@contoso.com' $r.Terms[0].Term
Check 'nor the middle one'                        'bob@contoso.com'  $r.Terms[1].Term
Check 'nor the last'                              'amy@contoso.com'  $r.Terms[2].Term
Check 'they all trace back to the one line'       1 $r.Terms[2].LineNumber
# A comma separates only when every piece carries an address, or "Doe, Jane" would be torn in half.
Check 'a comma between addresses separates' 2 (@($P::SplitRecipients('a@x.com, b@x.com'))).Count
Check 'but Last, First is left whole'       1 (@($P::SplitRecipients('Doe, Jane'))).Count
Check 'and so is a lone name'               1 (@($P::SplitRecipients('Jane Doe'))).Count
$r = $P::Parse('Doe, Jane')
Check 'so Last, First still parses as one term' 1 $r.Terms.Count
Check 'and still flips for searching'           'Jane Doe' $r.Terms[0].SearchText

Write-Host "`n== a term is identified by index, not by line ==" -ForegroundColor Cyan
# One line can now yield several terms, so a backend's answers cannot be matched back by line number.
$r = $P::Parse($multi)
Check 'indexes are distinct' 3 (@($r.Terms | ForEach-Object { $_.Index } | Select-Object -Unique)).Count
Check 'and start at zero'    0 $r.Terms[0].Index

Write-Host "`n== nothing vanishes without being counted ==" -ForegroundColor Cyan
# A line that held something but cleaned to nothing must be reported, or it looks like a line that found
# nobody — and the operator goes looking for a person who was never searched for.
# Decoration that leaves NOTHING behind: a stray separator, an empty bracket pair, an empty quoted cell.
# Text like "---" is left as a term on purpose — it becomes a visible "Not found" row, which is honest.
$r = $P::Parse("Jane Doe`n;`n<>`n`"`"")
Check 'lines that clean to nothing are counted' 3 $r.Unreadable
Check 'and do not become terms'                 1 $r.Terms.Count
# "Jane Doe <>" means Jane Doe. Taking the empty brackets as the answer would erase the line.
Check 'an empty bracket pair keeps the name' 'Jane Doe' $P::Clean('Jane Doe <>')
Check 'and still resolves to a term'         1 ($P::Parse('Jane Doe <>')).Terms.Count

Write-Host "`n== Label: the discriminator, not the identifier ==" -ForegroundColor Cyan
# Telling two people called John Smith apart is the whole purpose of the Choose step, and an Entra object
# id cannot do it. The sign-in name can.
$guid = '296f01f3-04de-49a1-ae6c-279d147b2487'
$c = [UnifiedDirectoryManager.Models.MemberCandidate]::new($guid, 'John Smith', 'jsmith@contoso.com', 'User')
Check 'the label shows the sign-in name' $true ($c.Label -like '*jsmith@contoso.com*')
Check 'and not the object id'            $false ($c.Label -like "*$guid*")
$c = [UnifiedDirectoryManager.Models.MemberCandidate]::new('x', 'John Smith', $null, 'User')
Check 'falling back to the identity when there is nothing else' $true ($c.Label -like '*x*')
# A group in a list of people is nearly always a mistake, and nesting one is invisible afterwards.
$g = [UnifiedDirectoryManager.Models.MemberCandidate]::new('sales@contoso.com', 'Sales Team', 'sales', 'MailUniversalDistributionGroup')
Check 'a group is flagged as not a person' $false $g.IsPerson
Check 'and its kind is shown'              $true ($g.Label -like '*MailUniversalDistributionGroup*')
$u = [UnifiedDirectoryManager.Models.MemberCandidate]::new('a@x.com', 'A User', 'a@x.com', 'UserMailbox')
Check 'a mailbox is a person'              $true $u.IsPerson
Check 'and carries no kind suffix'         $false ($u.Label -like '*[[]*')

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
