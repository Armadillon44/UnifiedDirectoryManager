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

Write-Host "`npass=$pass fail=$fail" -ForegroundColor $(if ($fail -gt 0) { 'Red' } else { 'Green' })
if ($fail -gt 0) { exit 1 }
