<#
.SYNOPSIS
  Regression tests for F5 (membership writes silently skipped, or lost the whole batch) and F10 (the Contact
  filter matched nothing; the User filter also matched contacts).

.DESCRIPTION
  F5 is the most dangerous defect this review found. Membership writes decided BEFOREHAND whether a change
  was needed, using PropertyValueCollection.Contains over the group's cached member list. That comparison is
  ordinal case-sensitive; AD compares distinguished names case-insensitively. A DN that arrived in different
  casing -- from a CSV, from Entra's onPremisesDistinguishedName, from an older export -- made a REMOVE find
  nothing, skip, and report success while the person was still in the group. An ADD appended what the DC
  considered a duplicate, and because the old code committed once at the end, the DC's rejection threw away
  every other add in the same call. The cache also holds only the first ~1500 values, so both checks were
  stale past that regardless of casing.

  The fix asks the directory instead of guessing: a directed LDAP modify, with "already a member" and "not a
  member" treated as success. The retry-and-idempotency policy is separated from the LDAP transport
  precisely so it can be driven here -- DirectoryService itself needs a domain controller, and the suites
  run under a PowerShell whose System.DirectoryServices.Protocols is an older version than the app builds
  against, so no test can construct a real request or result code.

  Run with:  pwsh -NoProfile -File ./app/build/test-directory-writes.ps1
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

$Dir = [UnifiedDirectoryManager.Services.DirectoryService]
$NonPublicStatic = [System.Reflection.BindingFlags]'NonPublic,Static'

$noOp = $Dir.GetMethod('IsMembershipNoOp', $NonPublicStatic)
$policy = $Dir.GetMethod('ApplyMembershipPolicy', $NonPublicStatic)
if ($null -eq $noOp)   { throw 'DirectoryService has no IsMembershipNoOp — has it been refactored?' }
if ($null -eq $policy) { throw 'DirectoryService has no ApplyMembershipPolicy — has it been refactored?' }

# LDAP result codes (RFC 4511 section 4.1.9). Written out rather than imported, because importing them means
# loading System.DirectoryServices.Protocols, whose PowerShell-shipped version does not match the one this
# assembly was built against.
$NO_SUCH_ATTRIBUTE       = 16   # removing a value the attribute does not hold
$ATTRIBUTE_OR_VALUE_EXISTS = 20 # adding a value it already holds, per the RFC
$INSUFFICIENT_ACCESS     = 50   # a real failure, and the one an under-privileged helpdesk account hits
$UNWILLING_TO_PERFORM    = 53   # a real failure: constraint, e.g. removing the primary group
# What Active Directory ACTUALLY answers for a member already in the group. Not 20. This suite passed
# without it, because it asserted the RFC rather than the directory.
$ENTRY_ALREADY_EXISTS    = 68

# The DC's extended error, verbatim from the failure that exposed this. Protocol text, sent in English
# whatever the locale -- unlike the exception message, which .NET translates.
$AD_ALREADY_MEMBER = '00000562: UpdErr: DSID-031A11DA, problem 6005 (ENTRY_EXISTS), data 0'
$AD_NOT_MEMBER     = '00000561: UpdErr: DSID-031A11E2, problem 5001 (NO_ATTRIBUTE_OR_VAL), data 0'
$AD_ACCESS_DENIED  = '00002098: SecErr: DSID-03150F94, problem 4003 (INSUFF_ACCESS_RIGHTS), data 0'

function NoOp([int]$code, [bool]$add, [string]$serverError = $null) {
    return $noOp.Invoke($null, @([int]$code, [bool]$add, [string]$serverError))
}

Write-Host "`n== which LDAP refusals mean 'the directory already agrees' ==" -ForegroundColor Cyan
# These two, and only these two. Widening this set would swallow real failures; narrowing it brings back the
# original bug in a new place, because a re-run of a partly-applied scenario hits them constantly.
Check 'adding a duplicate value is a no-op'     $true  (NoOp $ATTRIBUTE_OR_VALUE_EXISTS $true)
# THE ONE THAT WAS MISSING, and the reason an operator saw "The object exists." as a failure after adding
# somebody to a group they were already in.
Check 'and so does AD, which answers 68'        $true  (NoOp $ENTRY_ALREADY_EXISTS $true)
Check 'removing an absent value is a no-op'     $true  (NoOp $NO_SUCH_ATTRIBUTE $false)
# Crossed over, they are NOT no-ops: "no such attribute" on an ADD means the schema has no such attribute,
# and "value exists" on a REMOVE should never happen at all.
Check 'but not crossed over (add/no-such)'      $false (NoOp $NO_SUCH_ATTRIBUTE $true)
Check 'nor crossed over (remove/exists)'        $false (NoOp $ATTRIBUTE_OR_VALUE_EXISTS $false)
Check 'nor entry-exists on a remove'            $false (NoOp $ENTRY_ALREADY_EXISTS $false)
Check 'access denied is a real failure (add)'   $false (NoOp $INSUFFICIENT_ACCESS $true)
Check 'and on remove'                           $false (NoOp $INSUFFICIENT_ACCESS $false)
Check 'so is a constraint violation'            $false (NoOp $UNWILLING_TO_PERFORM $false)
Check 'success is not a refusal at all'         $false (NoOp 0 $true)
Check 'nor is "no result code" (-1)'            $false (NoOp -1 $false)

Write-Host "`n== the directory's own words are read, not just its code ==" -ForegroundColor Cyan
# Betting on the result code alone is what went wrong. The extended error is matched too, so an unexpected
# code carrying an unmistakable explanation is still understood rather than shown to the operator as a
# failure. This is the exact string a domain controller sent when the defect was found.
Check 'an unfamiliar code saying ENTRY_EXISTS'  $true  (NoOp 1 $true $AD_ALREADY_MEMBER)
Check 'and one saying NO_ATTRIBUTE_OR_VAL'      $true  (NoOp 1 $false $AD_NOT_MEMBER)
# Still direction-sensitive: "already a member" must never excuse a failed REMOVE.
Check 'but not for the wrong direction'         $false (NoOp 1 $false $AD_ALREADY_MEMBER)
Check 'nor the other wrong direction'           $false (NoOp 1 $true $AD_NOT_MEMBER)
# A real refusal stays a refusal however much text it carries.
Check 'access denied is still a failure'        $false (NoOp $INSUFFICIENT_ACCESS $true $AD_ACCESS_DENIED)
Check 'and empty server text excuses nothing'   $false (NoOp 1 $true '')

Write-Host "`n== the write policy: what reaches the directory ==" -ForegroundColor Cyan

# A scripted send. Each entry says what the NEXT call should do: $null to succeed, or an LDAP result code to
# refuse with. Recorded so the test can assert the shape of the traffic, not just the outcome.
Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;

namespace UdmTest
{
    public class LdapRefusal : Exception
    {
        public int Code;
        public string Server;
        public LdapRefusal(int code) : this(code, null) { }
        public LdapRefusal(int code, string server) : base("LDAP refused with " + code) { Code = code; Server = server; }
    }

    /// <summary>Records every batch handed to it and refuses the ones the test told it to.</summary>
    public class ScriptedSender
    {
        // Keyed by the single DN in a one-value batch; "*" is the whole-set batch.
        public Dictionary<string, int> Refuse = new Dictionary<string, int>();
        // Optional extended error to send with a refusal, keyed the same way.
        public Dictionary<string, string> ServerText = new Dictionary<string, string>();
        public List<string> Sent = new List<string>();

        public void Send(IReadOnlyList<string> values)
        {
            var key = values.Count == 1 ? values[0] : "*";
            Sent.Add(string.Join("|", values));
            int code;
            if (!Refuse.TryGetValue(key, out code)) return;
            string server;
            ServerText.TryGetValue(key, out server);
            throw new LdapRefusal(code, server);
        }

        public static int? CodeOf(Exception ex)
        {
            var r = ex as LdapRefusal;
            return r == null ? (int?)null : r.Code;
        }

        public static string ServerErrorOf(Exception ex)
        {
            var r = ex as LdapRefusal;
            return r == null ? null : r.Server;
        }

        public static string Describe(Exception ex) { return ex.Message; }
    }
}
'@ -ReferencedAssemblies @('System.Collections', 'System.Runtime') | Out-Null

$notes = [System.Collections.Generic.List[string]]::new()
function Invoke-Policy([string[]]$dns, [bool]$add, $sender) {
    $script:notes = [System.Collections.Generic.List[string]]::new()
    $list = [System.Collections.Generic.List[string]]::new()
    foreach ($d in $dns) { $list.Add($d) }

    $send      = [Action[System.Collections.Generic.IReadOnlyList[string]]]$sender.Send
    $codeOf    = [Func[System.Exception, System.Nullable[int]]][UdmTest.ScriptedSender]::CodeOf
    $serverOf  = [Func[System.Exception, string]][UdmTest.ScriptedSender]::ServerErrorOf
    $describe  = [Func[System.Exception, string]][UdmTest.ScriptedSender]::Describe
    $note      = [Action[string]]{ param($m) $script:notes.Add($m) }

    try {
        [void]$policy.Invoke($null, @($list, $add, $send, $codeOf, $serverOf, $describe, $note))
        return $null
    }
    catch { return $_.Exception.GetBaseException().Message }
}

# --- the ordinary case: one round trip for the whole set -------------------------------------------------
$s = [UdmTest.ScriptedSender]::new()
$err = Invoke-Policy @('CN=A,DC=x', 'CN=B,DC=x', 'CN=C,DC=x') $true $s
Check 'a clean add succeeds'              $null $err
Check 'in exactly one request'            1     $s.Sent.Count
Check 'carrying every member'             'CN=A,DC=x|CN=B,DC=x|CN=C,DC=x' $s.Sent[0]
Check 'and says nothing about retrying'   0     $notes.Count

# --- THE BUG. One duplicate used to take the whole call down with it -------------------------------------
# A differently-cased DN is the ordinary way this happens: the DC sees a duplicate, rejects the modify, and
# under the old read-modify-write every other add in the call was discarded with it.
$s = [UdmTest.ScriptedSender]::new()
$s.Refuse['*'] = $ATTRIBUTE_OR_VALUE_EXISTS                     # the batch is refused...
$s.Refuse['CN=B,DC=x'] = $ATTRIBUTE_OR_VALUE_EXISTS             # ...because B is already a member
$err = Invoke-Policy @('CN=A,DC=x', 'CN=B,DC=x', 'CN=C,DC=x') $true $s
Check 'a duplicate does not fail the call' $null $err
Check 'the batch is retried per member'    4     $s.Sent.Count   # 1 batch + 3 singles
Check 'and A still lands'                  $true ($s.Sent -contains 'CN=A,DC=x')
Check 'and C still lands'                  $true ($s.Sent -contains 'CN=C,DC=x')
Check 'the retry is recorded'              1     $notes.Count
Check 'saying why'                         $true ($notes[0] -like '*retrying one member at a time*')

# --- THE REPORTED FAILURE, end to end -------------------------------------------------------------------
# One member, already in the group, refused by AD with code 68. This is what an operator did in the dev
# build and was shown "The object exists." for. The single-member path answers immediately, so there is not
# even a retry to hide behind.
$s = [UdmTest.ScriptedSender]::new()
$s.Refuse['CN=A,DC=x'] = $ENTRY_ALREADY_EXISTS
$s.ServerText['CN=A,DC=x'] = $AD_ALREADY_MEMBER
$err = Invoke-Policy @('CN=A,DC=x') $true $s
Check 'adding an existing member succeeds' $null $err
Check 'in one request, with no retry'      1     $s.Sent.Count

# And in a batch: the duplicate must not be reported as a failure alongside the ones that worked.
$s = [UdmTest.ScriptedSender]::new()
$s.Refuse['*'] = $ENTRY_ALREADY_EXISTS
$s.ServerText['*'] = $AD_ALREADY_MEMBER
$s.Refuse['CN=B,DC=x'] = $ENTRY_ALREADY_EXISTS
$s.ServerText['CN=B,DC=x'] = $AD_ALREADY_MEMBER
$err = Invoke-Policy @('CN=A,DC=x', 'CN=B,DC=x', 'CN=C,DC=x') $true $s
Check 'a batch with one existing member succeeds' $null $err
Check 'and the other two still land'              $true (($s.Sent -contains 'CN=A,DC=x') -and ($s.Sent -contains 'CN=C,DC=x'))

# --- a removal that was already done is success, not a failure -------------------------------------------
# Re-running a termination scenario after a partial failure hits this on every step that already ran.
$s = [UdmTest.ScriptedSender]::new()
$s.Refuse['*'] = $NO_SUCH_ATTRIBUTE
$s.Refuse['CN=A,DC=x'] = $NO_SUCH_ATTRIBUTE
$s.Refuse['CN=B,DC=x'] = $NO_SUCH_ATTRIBUTE
$err = Invoke-Policy @('CN=A,DC=x', 'CN=B,DC=x') $false $s
Check 'removing non-members is success'   $null $err

# --- a single member, already in the desired state -------------------------------------------------------
# The one-member path answers immediately: there is nothing to salvage by retrying a single value.
$s = [UdmTest.ScriptedSender]::new()
$s.Refuse['CN=A,DC=x'] = $ATTRIBUTE_OR_VALUE_EXISTS
$err = Invoke-Policy @('CN=A,DC=x') $true $s
Check 'one already-member add succeeds'   $null $err
Check 'without a pointless retry'         1     $s.Sent.Count

# --- a single member, real failure: the directory's own words reach the caller ---------------------------
$s = [UdmTest.ScriptedSender]::new()
$s.Refuse['CN=A,DC=x'] = $INSUFFICIENT_ACCESS
$err = Invoke-Policy @('CN=A,DC=x') $true $s
Check 'one denied add fails'              $true ($err -like '*LDAP refused with 50*')
Check 'without a pointless retry'         1     $s.Sent.Count

# --- a real failure among several: report it, but do not hide what worked --------------------------------
# The old code's silence is the thing being fixed. An operator about to tell someone "they are out of the
# group" needs to know that one of five did not go.
$s = [UdmTest.ScriptedSender]::new()
$s.Refuse['*'] = $INSUFFICIENT_ACCESS
$s.Refuse['CN=B,DC=x'] = $INSUFFICIENT_ACCESS
$err = Invoke-Policy @('CN=A,DC=x', 'CN=B,DC=x', 'CN=C,DC=x') $false $s
Check 'a real failure is reported'        $true ($err -like '*Could not remove 1 of 3 member(s)*')
Check 'and names what did work'           $true ($err -like '*2 succeeded*')
Check 'and names who did not'             $true ($err -like '*B: *')
Check 'and the other two still ran'       $true (($s.Sent -contains 'CN=A,DC=x') -and ($s.Sent -contains 'CN=C,DC=x'))

# --- everything failing is still reported honestly -------------------------------------------------------
$s = [UdmTest.ScriptedSender]::new()
$s.Refuse['*'] = $INSUFFICIENT_ACCESS
foreach ($d in 'CN=A,DC=x', 'CN=B,DC=x') { $s.Refuse[$d] = $INSUFFICIENT_ACCESS }
$err = Invoke-Policy @('CN=A,DC=x', 'CN=B,DC=x') $true $s
Check 'all-failed says 2 of 2'            $true ($err -like '*Could not add 2 of 2 member(s)*')
Check 'and 0 succeeded'                   $true ($err -like '*0 succeeded*')

# --- nothing asked for, nothing sent ---------------------------------------------------------------------
$s = [UdmTest.ScriptedSender]::new()
$err = Invoke-Policy @() $true $s
Check 'an empty set touches the directory 0 times' 0 $s.Sent.Count
Check 'and does not fail'                          $null $err

Write-Host "`n== the pre-check that caused it is gone (mutation check) ==" -ForegroundColor Cyan
# The defect was a client-side decision about whether the directory needed changing. Restoring any form of
# it -- Contains over the member cache -- flips these.
$src = Get-Content -Raw (Join-Path $repoRoot 'app\src\UnifiedDirectoryManager\Services\DirectoryService.cs')
$modifyMembers = [regex]::Match($src, '(?s)private void ModifyMembers\(.*?\r?\n    \}').Value
$modifyMembership = [regex]::Match($src, '(?s)private void ModifyGroupMembership\(.*?\r?\n    \}').Value
$policySrc = [regex]::Match($src, '(?s)internal static void ApplyMembershipPolicy\(.*?\r?\n    \}').Value
Check 'ModifyMembers was found'              $true ($modifyMembers.Length -gt 0)
Check 'ModifyGroupMembership was found'      $true ($modifyMembership.Length -gt 0)
Check 'the policy was found'                 $true ($policySrc.Length -gt 0)
$writes = $modifyMembers + $modifyMembership + $policySrc
Check 'no Contains over the member cache'    $false ($writes -match 'members\.Contains')
Check 'no read-modify-write commit'          $false ($writes -match 'CommitChanges')
Check 'and no DirectoryEntry at all'         $false ($writes -match 'CreateEntry')
Check 'the change is a directed LDAP modify' $true  ($src -match 'DirectoryAttributeOperation\.Add : Protocols\.DirectoryAttributeOperation\.Delete')

Write-Host "`n== F10: objectCategory is not the object's class ==" -ForegroundColor Cyan
# A contact's defaultObjectCategory is CN=Person. So "(objectCategory=contact)" matched NOTHING -- Advanced
# Search's Contacts option always came back empty -- and "(objectCategory=person)" matched mail contacts as
# well as user accounts, which is how a contact could be bound as somebody's manager.
$Query = [UnifiedDirectoryManager.Models.SearchQuery]
$classFilter = $Query.GetMethod('ObjectClassFilter', $NonPublicStatic)
if ($null -eq $classFilter) { throw 'SearchQuery has no ObjectClassFilter — has it been refactored?' }
$Type = [UnifiedDirectoryManager.Models.AdObjectType]

$contact = $classFilter.Invoke($null, @($Type::Contact))
Check 'the contact filter is not category-only' $false ($contact -eq '(objectCategory=contact)')
Check 'it narrows by objectClass'               $true  ($contact -match 'objectClass=contact')
Check 'and keeps the indexed category'          $true  ($contact -match 'objectCategory=person')

$user = $classFilter.Invoke($null, @($Type::User))
Check 'the user filter excludes contacts'       $true  ($user -match 'objectClass=user')
Check 'and still excludes computers'            $true  ($user -match 'objectCategory=person')

# The same two filters exist a second time, for the picker. They have to agree, or the same term finds
# different objects depending on which dialog asked.
$dirSrc = [regex]::Match($src, '(?s)var categoryFilter = type switch.*?\};').Value
Check 'the picker filters were found'           $true ($dirSrc.Length -gt 0)
Check 'the picker contact filter is narrowed'   $true ($dirSrc -match 'AdObjectType\.Contact => "\(&\(objectCategory=person\)\(objectClass=contact\)\)"')
Check 'the picker user filter is narrowed'      $true ($dirSrc -match 'AdObjectType\.User => "\(&\(objectCategory=person\)\(objectClass=user\)\)"')
# Any/Unknown deliberately still admits contacts: AD accepts one as a distribution-group member and as
# managedBy, and both of those pickers open in this mode. Narrowing it would remove a real capability.
Check 'the any filter still admits contacts'    $true ($dirSrc -match '_ => "\(\|\(objectCategory=person\)')

Write-Host "`n== empty is told apart from unreadable ==" -ForegroundColor Cyan
# Active Directory omits the member attribute identically for a group with no members and a group whose
# membership you may not read. Taking the cautious reading meant EVERY empty group was reported as
# possibly-unreadable -- a warning on the ordinary case, which is how the rare real one gets scrolled past.
# It was reported from the dev build exactly that way: an empty group said it could not be read.
#
# The back-link settles it. memberOf is kept on each MEMBER object, under that object's own ACL rather
# than the group's, so a group whose member attribute is hidden still has members pointing back at it.
#
# The probe itself needs a domain controller, so what is pinned here is the decision it encodes.
$dirSrc = Get-Content -Raw (Join-Path $repoRoot 'app\src\UnifiedDirectoryManager\Services\DirectoryService.cs')
$probe = [regex]::Match($dirSrc, '(?s)private bool ConfirmedEmpty\(string groupDn, CancellationToken cancellationToken\).*?\r?\n    \}').Value
Check 'the probe exists'                     $true ($probe.Length -gt 0)
Check 'it asks via the back-link'            $true ($probe -match 'memberOf=')
Check 'and escapes the DN into the filter'   $true ($probe -match 'LdapFilter\.EscapeValue\(groupDn\)')
# One result is all the question needs; loading the whole membership would cost what the guard is avoiding.
Check 'one result answers it'                $true ($probe -match 'FindOne\(\)')

# THE DIRECTION THAT MATTERS. If the probe cannot run, the answer must be "not confirmed" -- claiming a
# group is empty on the strength of a failed check is the one answer that could get a group deleted on a
# false premise, because the deletion record's caveat hangs off this same flag.
$catch = [regex]::Match($probe, '(?s)catch \(Exception ex\).*?\r?\n        \}').Value
Check 'a failed probe was found'             $true ($catch.Length -gt 0)
Check 'and does NOT claim empty'             $false ($catch -match 'return true;')
Check 'it says unconfirmed instead'          $true ($catch -match 'return false;')
# Cancellation is not a failed probe and must not be swallowed into "unconfirmed" either.
Check 'cancellation still propagates'        $true ($probe -match 'catch \(OperationCanceledException\) \{ throw; \}')

# And the read only pays for the probe in the one case that is ambiguous.
$read = [regex]::Match($dirSrc, '(?m)^\s*var unconfirmed = .*$').Value
Check 'the probe gates on nothing read'      $true ($read -match '!sawMemberProperty && members\.Count == 0')
Check 'and only then consults it'            $true ($read -match '&& !ConfirmedEmpty\(groupDn')

# A distinguished name is full of characters an LDAP filter treats specially; a group really can be
# called "Sales, West" or "R&D (EU)".
$Filter = [UnifiedDirectoryManager.Models.LdapFilter]
Check 'a backslash is escaped'  'CN=Sales\5c, West,DC=x' ($Filter::EscapeValue('CN=Sales\, West,DC=x'))
Check 'parentheses are escaped' 'CN=R&D \28EU\29,DC=x'  ($Filter::EscapeValue('CN=R&D (EU),DC=x'))
Check 'and an asterisk'         'CN=a\2ab,DC=x'          ($Filter::EscapeValue('CN=a*b,DC=x'))

Write-Host "`n== a deleted OU leaves the tree, and says so in English ==" -ForegroundColor Cyan
# Reported: delete an OU, the tree keeps showing it, and seconds later the status bar reads
#   0000208D: NameErr: DSID-0310028C, problem 2001 (NO_OBJECT), data 0, best match of: ...
# The log showed the delete at 09:54:19 and the error at 09:54:25 -- six seconds later, i.e. the
# operator clicking the node that should already have gone.

$explain = [UnifiedDirectoryManager.Services.DirectoryService].GetMethod(
    'Explain', [System.Reflection.BindingFlags]'NonPublic,Static')
Check 'Explain exists'                  $true ($null -ne $explain)

# The exact message from the report.
$noObject = "0000208D: NameErr: DSID-0310028C, problem 2001 (NO_OBJECT), data 0, best match of:`n   'OU=Users,OU=Disabled,DC=lacrossefootwear,DC=net'"
$explained = $explain.Invoke($null, @([object]$noObject))
Check 'it leads with plain English'      $true ($explained -like 'That object no longer exists*')
Check '  and says what to do'            $true ($explained -like '*F5*')
# The server text is what gets pasted into a ticket, and the DSID is how Microsoft locates a failure.
# It must be kept, not replaced.
Check '  while keeping the server text'  $true ($explained -like '*DSID-0310028C*')
Check '  including the best match'       $true ($explained -like '*OU=Users,OU=Disabled*')

# Matched on the numeric problem code, not the English: a non-English DC localises the words.
$localised = 'problem 2001 (irgendwas), data 0'
Check 'the code is what is matched'      $true ($explain.Invoke($null, @([object]$localised)) -like 'That object no longer exists*')

# Anything unrecognised passes through untouched rather than being guessed at.
$unknown = 'something the directory said that we have no sentence for'
Check 'an unknown error is untouched'    $unknown ($explain.Invoke($null, @([object]$unknown)))
Check '  and empty stays empty'          '' ($explain.Invoke($null, @([object]'')))

Write-Host "`n== and the node itself is removed, wherever it is held ==" -ForegroundColor Cyan
# The delete used to do:   FindNodeByDn(ParentDn(dn))?.Children.Remove(node)
# which removes from the parent whose DN matches -- not from whatever is actually HOLDING the node.
# When those differ the call silently does nothing and the deleted OU stays in the tree.

$mv = [UnifiedDirectoryManager.ViewModels.MainViewModel]
$removeByDn = $mv.GetMethod(
    'RemoveNodeByDn',
    [System.Reflection.BindingFlags]'NonPublic,Static',
    $null,
    @([UnifiedDirectoryManager.ViewModels.TreeNodeViewModel], [string]),
    $null)
Check 'RemoveNodeByDn(node, dn) exists'  $true ($null -ne $removeByDn)

$onErr = [Action[string]]{ param($m) }
function New-Node([string]$dn, [string]$name) {
    $n = New-Object UnifiedDirectoryManager.Models.AdNode
    $n.DistinguishedName = $dn
    $n.Name = $name
    $n.HasChildren = $false
    return [UnifiedDirectoryManager.ViewModels.TreeNodeViewModel]::new($n, $null, $onErr, $null, $false, $null)
}

$dnTest  = 'OU=TEST,OU=Users,OU=Disabled,DC=lacrossefootwear,DC=net'
$dnChild = 'CN=Someone,OU=TEST,OU=Users,OU=Disabled,DC=lacrossefootwear,DC=net'

# The ordinary shape: the node sits under the parent its DN names.
$root   = New-Node 'DC=lacrossefootwear,DC=net' 'lacrossefootwear.net'
$users  = New-Node 'OU=Users,OU=Disabled,DC=lacrossefootwear,DC=net' 'Users'
$test   = New-Node $dnTest 'TEST'
$inside = New-Node $dnChild 'Someone'
$test.Children.Add($inside)
$users.Children.Add($test)
$root.Children.Add($users)
Check 'the OU is in the tree'            1 $users.Children.Count
Check "  removing it reports true"       $true $removeByDn.Invoke($null, @([object]$root, [object]$dnTest))
Check '  and it is gone'                 0 $users.Children.Count

# The case that broke: the node is held by something whose DN is NOT ParentDn(dn). A pinned
# favourite is exactly this -- it is its own root. Removing from the DN-matched parent would miss it.
$favRoot = New-Node 'fav:root' 'Favourites'
$test2   = New-Node $dnTest 'TEST'
$favRoot.Children.Add($test2)
Check 'held by a non-DN parent'          1 $favRoot.Children.Count
Check "  it is still removed"            $true $removeByDn.Invoke($null, @([object]$favRoot, [object]$dnTest))
Check '  and that one is gone too'       0 $favRoot.Children.Count

# Two instances of the same DN under one parent: BOTH must go. Mutation testing found this hole --
# stopping at the first match passed every test above, because each tree there held exactly one.
$dupParent = New-Node 'OU=Holder,DC=lacrossefootwear,DC=net' 'Holder'
$dupParent.Children.Add((New-Node $dnTest 'TEST'))
$dupParent.Children.Add((New-Node $dnTest 'TEST'))
$dupParent.Children.Add((New-Node 'OU=Other,DC=lacrossefootwear,DC=net' 'Other'))
Check 'two instances of one DN'          3 $dupParent.Children.Count
Check "  removing takes both"            $true $removeByDn.Invoke($null, @([object]$dupParent, [object]$dnTest))
Check '  leaving only the other'         1 $dupParent.Children.Count
Check '  which is untouched'             'OU=Other,DC=lacrossefootwear,DC=net' $dupParent.Children[0].DistinguishedName

# A DN nothing holds changes nothing, and says so.
$root2 = New-Node 'DC=lacrossefootwear,DC=net' 'lacrossefootwear.net'
$keep  = New-Node 'OU=Keep,DC=lacrossefootwear,DC=net' 'Keep'
$root2.Children.Add($keep)
Check "an absent DN reports false"       $false $removeByDn.Invoke($null, @([object]$root2, [object]$dnTest))
Check '  and removes nothing'            1 $root2.Children.Count

# Both delete paths must call it. Source checks: MainViewModel cannot be constructed from here, and
# mutation testing showed that without these, reverting either call site passed the whole suite.
$mvSrc = Get-Content -Raw (Join-Path $repoRoot 'app\src\UnifiedDirectoryManager\ViewModels\MainViewModel.cs')
$ouBody = [regex]::Match($mvSrc, '(?s)public async Task DeleteOuAsync.*?\r?\n    \}').Value
Check 'the OU delete body was found'     $true ($ouBody.Length -gt 0)
Check '  it removes the node by DN'      $true ($ouBody -match 'RemoveNodeByDn\(dn\)')
# The line this replaced, which silently did nothing whenever the holder was not the DN-matched parent.
# Comments stripped first: the body carries a comment NAMING the line this replaced, and matching
# that would fail on the explanation rather than on the code. Same trap as the drag-selection fix.
$ouCode = [regex]::Replace($ouBody, '//.*?(\r?\n|$)', '')
Check '  not via the DN-matched parent'  $false ($ouCode -match 'parent\?\.Children\.Remove')

$rowBody = [regex]::Match($mvSrc, '(?s)private async Task DeleteSelectedAsync.*?\r?\n    \}').Value
Check 'the row delete body was found'    $true ($rowBody.Length -gt 0)
# An OU reaches this path from advanced-search results, and it used to reload the list and stop.
Check '  containers leave the tree too'  $true ($rowBody -match 'IsContainerLike\(\)\) RemoveNodeByDn')

Write-Host "`n== password never expires: one flag, and nothing else ==" -ForegroundColor Cyan
# userAccountControl carries a dozen unrelated flags in one integer. Setting this checkbox must change
# exactly one bit -- a disable or a smartcard requirement set elsewhere has to survive it.
$bfStatic = [System.Reflection.BindingFlags]'NonPublic,Static'
$DS = [UnifiedDirectoryManager.Services.DirectoryService]
$applyNe = $DS.GetMethod('ApplyPasswordNeverExpires', $bfStatic)
Check 'the UAC seam exists'              $true ($null -ne $applyNe)

$normal = 0x200; $disabled = 0x2; $smartcard = 0x40000; $neverExp = 0x10000
Check 'on sets 0x10000'                  ($normal -bor $neverExp) ($applyNe.Invoke($null, @([int]$normal, $true)))
Check '  and off clears it'              $normal ($applyNe.Invoke($null, @([int]($normal -bor $neverExp), $false)))
$busy = $normal -bor $disabled -bor $smartcard
$on = $applyNe.Invoke($null, @([int]$busy, $true))
Check 'a disabled flag survives'         $disabled ($on -band $disabled)
Check '  and a smartcard flag'           $smartcard ($on -band $smartcard)
Check '  with ours added'                $neverExp ($on -band $neverExp)
$off = $applyNe.Invoke($null, @([int]$on, $false))
Check 'clearing leaves them too'         $busy $off
Check 'setting twice is setting once'    $on ($applyNe.Invoke($null, @([int]$on, $true)))

Write-Host "`n== user cannot change password: a permission, not a flag ==" -ForegroundColor Cyan
# Microsoft: ADS_UF_PASSWD_CANT_CHANGE "cannot be assigned by directly modifying the UserAccountControl
# attribute" -- it reflects a permission on the user object. So this is tested on a security descriptor,
# built in memory, which needs no domain controller.
$applyCcp = $DS.GetMethod('ApplyCannotChangePassword', $bfStatic)
$hasCcp   = $DS.GetMethod('HasCannotChangePassword', $bfStatic)
Check 'the DACL seams exist'             $true (($null -ne $applyCcp) -and ($null -ne $hasCcp))

$sidType  = [System.Security.Principal.SecurityIdentifier]
$everyone = [System.Security.Principal.SecurityIdentifier]::new('S-1-1-0')
$self     = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-10')
$changePw = [guid]'AB721A53-1E2F-11D0-9819-00AA0040529B'
$resetPw  = [guid]'00299570-246D-11D0-A768-00AA006E0529'
$extRight = [System.DirectoryServices.ActiveDirectoryRights]::ExtendedRight
$deny     = [System.Security.AccessControl.AccessControlType]::Deny
$allow    = [System.Security.AccessControl.AccessControlType]::Allow

# Every explicit ACE for a given control-access right. Wrapped in @() so one result stays a collection.
function Get-RightAces($sec, [guid]$right) {
    return ,@($sec.GetAccessRules($true, $false, $sidType) | Where-Object {
        $_.ObjectType -eq $right -and (($_.ActiveDirectoryRights -band $extRight) -ne 0) })
}

$sec = [System.DirectoryServices.ActiveDirectorySecurity]::new()
Check 'a fresh descriptor is not denied' $false ($hasCcp.Invoke($null, @($sec)))

$applyCcp.Invoke($null, @($sec, $true)) | Out-Null
Check 'denying reads back as denied'     $true ($hasCcp.Invoke($null, @($sec)))
$aces = Get-RightAces $sec $changePw
Check '  as exactly two ACEs'            2 $aces.Count
Check '  both of them Deny'              2 @($aces | Where-Object { $_.AccessControlType -eq $deny }).Count
# Both trustees, matched by SID: the names "Everyone" and "SELF" are localised on a non-English DC.
Check '  one for Everyone'               1 @($aces | Where-Object { $_.IdentityReference -eq $everyone }).Count
Check '  and one for SELF'               1 @($aces | Where-Object { $_.IdentityReference -eq $self }).Count

# A second save must not stack duplicate ACEs onto the account.
$applyCcp.Invoke($null, @($sec, $true)) | Out-Null
Check 'denying twice leaves two, not 4'  2 (Get-RightAces $sec $changePw).Count

$applyCcp.Invoke($null, @($sec, $false)) | Out-Null
Check 'allowing reads back as allowed'   $false ($hasCcp.Invoke($null, @($sec)))
Check '  with no deny left behind'       0 @((Get-RightAces $sec $changePw) | Where-Object { $_.AccessControlType -eq $deny }).Count

# Denying only one of the two trustees leaves the right reachable through the other, so it is NOT
# "cannot change". Reading it as ticked would show a state the account does not have.
$onlySelf = [System.DirectoryServices.ActiveDirectorySecurity]::new()
$onlySelf.AddAccessRule([System.DirectoryServices.ActiveDirectoryAccessRule]::new($self, $extRight, $deny, $changePw))
Check 'only SELF denied is not "cannot"' $false ($hasCcp.Invoke($null, @($onlySelf)))
$onlyEveryone = [System.DirectoryServices.ActiveDirectorySecurity]::new()
$onlyEveryone.AddAccessRule([System.DirectoryServices.ActiveDirectoryAccessRule]::new($everyone, $extRight, $deny, $changePw))
Check '  nor only Everyone'              $false ($hasCcp.Invoke($null, @($onlyEveryone)))

# The neighbouring right. Change Password is what the user does with their OLD password; Reset Password
# is what an administrator does. Touching the wrong one would take away the ability to reset the account.
$mixed = [System.DirectoryServices.ActiveDirectorySecurity]::new()
$mixed.AddAccessRule([System.DirectoryServices.ActiveDirectoryAccessRule]::new($everyone, $extRight, $deny, $resetPw))
$applyCcp.Invoke($null, @($mixed, $false)) | Out-Null
Check 'a Reset Password ACE survives'    1 (Get-RightAces $mixed $resetPw).Count
$applyCcp.Invoke($null, @($mixed, $true)) | Out-Null
Check '  either way'                     1 (Get-RightAces $mixed $resetPw).Count
Check '  and the deny is on CHANGE'      $true ($hasCcp.Invoke($null, @($mixed)))

# And deletion protection, which is also an Everyone deny, on a different right entirely.
$guarded = [System.DirectoryServices.ActiveDirectorySecurity]::new()
$delRights = [System.DirectoryServices.ActiveDirectoryRights]::Delete -bor [System.DirectoryServices.ActiveDirectoryRights]::DeleteTree
$guarded.AddAccessRule([System.DirectoryServices.ActiveDirectoryAccessRule]::new($everyone, $delRights, $deny))
$applyCcp.Invoke($null, @($guarded, $true)) | Out-Null
$applyCcp.Invoke($null, @($guarded, $false)) | Out-Null
$stillGuarded = @($guarded.GetAccessRules($true, $false, $sidType) | Where-Object {
    $_.AccessControlType -eq $deny -and (($_.ActiveDirectoryRights -band $delRights) -eq $delRights) }).Count
Check 'deletion protection survives'     1 $stillGuarded

Write-Host "`n== and the pane only saves what it actually read ==" -ForegroundColor Cyan
# The two checkboxes start unticked, and unticked is a legitimate value. If the read FAILS they must
# stay out of the save -- or saving would clear a never-expires flag or a deny the account really has.
$epSrc = Get-Content -Raw (Join-Path $repoRoot 'app\src\UnifiedDirectoryManager\ViewModels\EditPaneViewModel.cs')
$builder = [regex]::Match($epSrc, '(?s)private IEnumerable<PendingChange> BuildPasswordOptionChanges\(\).*?\r?\n    \}').Value
Check 'the change builder was found'     $true ($builder.Length -gt 0)
Check '  it yields nothing unread'       $true ($builder -match 'if \(!IsUser \|\| !PasswordOptionsKnown\) yield break;')
# Synthetic keys, so a raw userAccountControl edit in the Attribute Editor cannot silently replace this.
Check '  keyed apart from the attribute' $true ($builder -match '"__passwordNeverExpires__"')
Check '  both of them'                   $true ($builder -match '"__cannotChangePassword__"')

# The DACL mask must cover the new ops, or ObjectSecurity binds without the DACL and the write is lost.
$dsSrc = Get-Content -Raw (Join-Path $repoRoot 'app\src\UnifiedDirectoryManager\Services\DirectoryService.cs')
$maskLine = [regex]::Match($dsSrc, '(?s)if \(changes\.Any\(c => c\.Op is ChangeOp\.Protect.*?SecurityMasks\.Dacl;').Value
Check 'the DACL mask was found'          $true ($maskLine.Length -gt 0)
Check '  and covers both ops'            $true (($maskLine -match 'SetCannotChangePassword') -and ($maskLine -match 'UnsetCannotChangePassword'))
Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
