# Code review findings — 2.3.1 baseline

Status: **findings recorded, no fixes made.** The working tree these were found in is `master` at
**`ac09e77` (tag `v2.3.1`)**. Every `file:line` below is pinned to that commit — verify against
`git show ac09e77:<path>` if lines have drifted by the time this is worked.

This document is written to be picked up cold, by a session with no memory of the review. It contains:
how the findings were produced and what "confirmed" means; practical notes on building and testing this
codebase that are not written down anywhere else; the 28 confirmed findings in full detail; a second tier
of finder-reported items that were **not** independently verified; and consolidation targets. Suggested
work packages are at the end.

---

## How these were found, and what "confirmed" means

A ten-angle review swept `app/src/UnifiedDirectoryManager/` — per-file line scans of services and
view models, a cross-file contract tracer, a recent-change auditor over the 2.3.0/2.3.1 diffs, C#/WPF
pitfall and wrapper-correctness angles, and reuse/simplification/efficiency/altitude angles. Roughly 75
candidates came back.

**Every Tier 1 finding was then verified by hand**: the quoted code was re-read at `ac09e77` and the
failure mechanism traced. Where a finding's mechanism rests on platform behaviour (PowerShell `break`
semantics, `Task.Run` cancellation, AD `objectCategory` for contacts, LDAP DN case-insensitivity), that
behaviour is stated in the finding so the fixer does not have to re-derive it. **Tier 2 items were
reported by a finder with quoted evidence but were not independently re-verified** — treat each as a
strong lead, not a settled fact, and verify before fixing.

Severity: **S1** silently corrupts data, misreports a destructive operation, or acts as the wrong
identity. **S2** loses user data/work or hard-degrades a feature. **S3** wrong-but-recoverable behaviour,
dead ends, misleading UI.

---

## Working on this codebase — read before starting

Things a fixing session will otherwise rediscover the hard way. None of this is in a CLAUDE.md; there
isn't one.

- **Build for tests:** the suites load `./debug/UnifiedDirectoryManager.dll` (repo root). Build with
  `dotnet build app/src/UnifiedDirectoryManager/UnifiedDirectoryManager.csproj -c Debug -p:OutputPath=<repo>/debug/`.
- **The BG1002 ritual.** WPF's temp project intermittently corrupts `obj/` (BG1002 "…baml cannot be
  found", or wpftmp CS2001). It is not your change. Fix: `dotnet build-server shutdown`, delete
  `app/src/UnifiedDirectoryManager/obj` and `bin`, **build twice**. This bites most after switching
  configurations (a Release publish clobbers state for the next Debug build).
- **A running app locks the deployed exe/dll.** Close it before building for deploy, or build to a
  scratch `-p:OutputPath`.
- **Test suites** (all under `app/build/`, run from the **repo root**):
  `test-favorites` (100), `test-paste-parser` (69), `test-hostscript` (247), `test-user-attributes` (40)
  with `pwsh -NoProfile -File`; `test-tabselection` (12), `test-paste-dialog` (7), `test-search-dialog`
  (11) need `-STA`. 486 assertions total at this baseline, all green.
- **House convention: mutation-test every new guard.** Apply the inverse mutation (or drop a clause),
  rebuild, confirm the suite fails, restore. Several past guards survived mutation because a test read a
  computed property instead of watching `PropertyChanged`, or exercised only one direction of a bound
  check — watch for both.
- **PowerShell 7 ships `System.DirectoryServices.Protocols` 10.0.0.5; the app builds against 10.0.0.9.**
  The loader binds its own copy no matter what, so any test that drives a catch path through
  `DirectoryService.Friendly()` throws a manifest mismatch instead of returning. `test-favorites` handles
  this with an `Attempted` helper that treats "handler fired" and "call threw" as one observation — reuse
  that pattern rather than fighting the loader.
- **Init-only record properties are settable through reflection**, which is what PowerShell uses —
  `test-user-attributes` builds `UserAttributeBuilder.Input` this way with no test double. A null
  interface argument (`[IDirectoryService]$null`) is the house pattern for "must not be reached".
- **Exchange channel facts** (relevant to several findings): one child `pwsh` hosting
  `ExchangeOnlineManagement`, base64-JSON line protocol, a single `SemaphoreSlim` (`_gate`) serialising
  everything; OpTimeout 90 s, ListTimeout 180 s, and the *design intent* is that timeout kills the host
  process and every session with it. EXO returns booleans as the strings `"True"`/`"False"`; a `Get-`
  cmdlet with a non-existent `-Identity` returns **every** object; leading wildcards are not allowed in
  EXO filters.
- **Existing plan docs** overlap some findings: `docs/issues-4-5-plan.md` (locked; quick-filter rework
  D1–D5 and removal records D6–D9) and `docs/onedrive-offboarding-plan.md` (gated on a spike). Fixes here
  should not contradict those locked decisions; where a finding touches one, it is cross-referenced.

---

## Tier 1 — confirmed findings

### The Exchange channel

**F1 — The operation timeout is inert; a hung call deadlocks every Exchange feature. (S1)**
`Services/ExchangeService.cs:1053`
```csharp
cts.CancelAfter(timeout);
...
return await Task.Run(() => reader.ReadLine(), cts.Token).ConfigureAwait(false);
```
A `CancellationToken` passed to `Task.Run` only prevents the delegate from **starting**. Once
`ReadLine()` blocks, cancellation has no effect, the task never completes, and the
`catch (OperationCanceledException)` that calls `KillLocked()` (line 1059) is unreachable. A hung EXO
cmdlet therefore blocks forever **while holding `_gate`** — every Exchange feature in the app queues
behind it, and the Cancel buttons on Exchange operations are dead. The documented kill-on-timeout
behaviour never happens.
*Fix shape:* race the read against the timeout rather than cancelling it —
`Task.WhenAny(readTask, Task.Delay(timeout, ct))`, and on timeout kill the host (which unblocks
`ReadLine` with EOF) before throwing. The orphaned read task then completes harmlessly.
*Test shape:* a fake host process that never answers; assert the op throws within the budget and the
process was killed. (The host script is a single string — a test can stand up the real protocol with a
`pwsh` that sleeps.)

**F14 — The host's `QUIT` handler breaks the wrong scope; every disconnect ends in a kill. (S3)**
`Services/ExchangeService.cs:1512`
```powershell
'QUIT' { try { Disconnect-ExchangeOnline ... } catch {}; break }
```
In PowerShell, `break` inside a `switch` exits the **switch**, not the enclosing `while ($true)` read
loop, so after QUIT the host re-blocks on `ReadLine`. `KillLocked` (line 981) writes QUIT but never
closes stdin, so the loop's `if ($null -eq $line) { break }` escape is unreachable too.
`WaitForExit(1500)` therefore always fails and `p.Kill(entireProcessTree: true)` always fires — a
guaranteed 1.5 s stall per disconnect, and the kill can land while `Disconnect-ExchangeOnline` is still
running, leaking the server-side session.
*Fix shape:* either `exit` in the QUIT arm, or a `:read` loop label with `break read` — and/or have
`KillLocked` close `StandardInput` after writing QUIT so the null-line escape works.

**F15 — App exit blocks the UI thread on the gate — up to 180 s, or forever with F1. (S2)**
`Services/ExchangeService.cs:69`
```csharp
public void Disconnect() { _gate.Wait(); try { KillLocked(); } finally { _gate.Release(); } }
```
`App.OnExit → _exchange.Dispose() → Disconnect()` runs on the UI thread. Close the window during an
in-flight mailbox listing and shutdown hangs a dead window for up to the 180 s list budget — and with F1,
forever. `MainViewModel.ReconfigureExchange` documents and works around this exact hazard
("blocks on the channel's gate with no timeout … it would freeze the window"); the exit path still hits
it.
*Fix shape:* on the Dispose path, skip the polite QUIT: try `_gate.Wait(0)`; if the gate is held, kill
the process directly without waiting for the in-flight op. On exit, abandoning the op is correct.

**F6 — The Exchange session survives a same-tenant admin switch. (S1, security)**
`Services/ExchangeService.cs:55`
```csharp
if (string.Equals(org, _organization, StringComparison.OrdinalIgnoreCase)) return;
Disconnect(); // a different org invalidates any existing session
```
The session is keyed only on the organization string, and **no code outside Configure/Dispose ever calls
`Disconnect()`** (verified by grep). Sign out and sign in as a different admin in the same tenant:
`Configure(sameTenant)` early-returns, the live pwsh session keeps the previous admin's token, and every
subsequent mailbox/DL write executes with **admin A's permissions and audit identity** until the token
expires.
*Fix shape:* the sign-out / sign-in path (GraphService or its caller) must tell Exchange to drop the
session — e.g. `IExchangeService.InvalidateSession()` called from `CloudSignInViewModel` on sign-out,
or key the session on organization **and** signed-in UPN.

**F13 — An ambiguous mailbox identity returns a merged, fabricated mailbox. (S1)**
`Services/ExchangeService.cs:1529`
```powershell
$m = Get-Mailbox -Identity $r.identity -ErrorAction SilentlyContinue
if ($null -eq $m) { __emit @{ ok = $true; data = $null } }
else { ... DisplayName = [string]$m.DisplayName ... }
```
The only `Get-` op in the host script without a single-result guard. An identity resolving to several
recipients returns an array; `$null -eq $m` is false and `[string]$m.DisplayName` space-joins **every
match**, so `GetMailboxAsync` returns a blended mailbox (wrong primary address, wrong type, wrong
forwarding state) that the convert/forwarding UI then displays and acts on. Every sibling op guards this
(e.g. line ~1730).
*Fix shape:* mirror the sibling ops — `Select-Object -First 1` plus the `__isWanted` identity check, or
refuse with "identity is ambiguous" when `@($m).Count -gt 1`. Add a host-script test to
`test-hostscript.ps1` (the suite already fakes cmdlets).

**F20 — The paste-resolver's exact rung can never flag truncation (20 vs > 20). (S3)**
`Services/ExchangeService.cs:1995` (and the flipped-name query at 2002)
```powershell
$rrHits = @(Get-Recipient -ResultSize 20 -Filter $rrFilter ...)
...
Truncated = ($rrHits.Count -gt 20)
```
With `-ResultSize 20`, the count can never exceed 20, so an exact term with more than 20 matches is
reported complete and trustworthy-exact — the C# side (`found.Exact && !found.Truncated`) then never
shows "More matches than can be shown — narrow the term." The ANR rung deliberately fetches **21** for
exactly this reason (line 2013). Introduced with the paste feature in 2.3.0.
*Fix shape:* fetch 21 in both exact branches, emit 20. One-line each; add the case to
`test-hostscript.ps1`.

### Graph truncation and swallowed failures

**F3 — "Remove all cloud groups" reports Success when the membership read failed or truncated. (S1)**
`Services/GraphService.cs:789`, consumer `Services/ScenarioRunner.cs:279`
```csharp
catch (Exception ex)
{
    AppLog.Instance.Warn("Could not read object group memberships: " + ex.Message);
    return Array.Empty<CloudGroup>();
}
```
`GetObjectMemberOfAsync` swallows every exception into an empty list **and** reads one `Top = 200` page
with no `OdataNextLink` walk (lines 777–779). The destructive `CloudRemoveAllGroups` scenario step
iterates zero (or only 200) groups and records `✓` / `RESULT: Success` — a terminated user keeps their
access while the compliance log says memberships were handled. The on-prem twin (`RemoveAllGroups`,
ScenarioRunner.cs:116) correctly **fails** the step when its read throws; the cloud twin must match.
*Fix shape:* let the exception propagate (or return a result type with a `Failed` flag) and page to
exhaustion. The scenario step must fail loudly on a failed or truncated read — same contract the
group-delete record enforces on-prem ("an unconfirmed list is stamped as such").

**F4 — Cloud member/membership lists are silently capped at 200 and shown as complete. (S2)**
`Services/GraphService.cs:191` (`GetGroupMembersAsync`) and `:819` (`GetUserGroupsAsync`)
```csharp
var resp = await _graph.Groups[groupId].Members.GetAsync(rc => rc.QueryParameters.Top = 200, cancellationToken);
return (resp?.Value ?? new List<DirectoryObject>()).Select(ToMember)...
```
One page, `OdataNextLink` ignored. Consumers: the cloud group **Members** pane
(CloudObjectDetailViewModel:603), **Copy User**, **Copy Groups to User**, and **Save as template** — all
silently treat the first 200 rows as the whole set. This is the exact trap the AD range walk (with its
`Truncated` flag) and the Exchange `-ResultSize Unlimited` read exist to avoid, and
`GetGroupMembersPageAsync` in the same file pages correctly.
*Fix shape:* follow `OdataNextLink` to exhaustion (the file already does this at 1141–1142 for groups),
or return a truncation flag the UI must surface. Prefer paging; these are bounded lists.
*Related:* `GetUserGroupsAsync` also swallows exceptions into `[]` at 829 — same silent-empty problem as
F3 for Copy Groups / Copy User / Save-as-template: a transient Graph failure presents a user as having
zero cloud memberships, and the operator copies an incomplete set with no warning.

**F7 — Reconfiguring the tenant keeps the old tenant's AuthenticationRecord. (S1, security)**
`Services/GraphService.cs:75`
```csharp
_record ??= TryLoadAuthRecord();
...
AuthenticationRecord = _record,
```
`??=` never replaces a non-null record, so `Configure(tenantB)` builds the new credential bound to
**tenant A's account**, and `IsSignedIn`/`SignedInAccount` keep reporting the old identity with no
sign-in to the new tenant. If the subsequent browser sign-in is cancelled, the app carries on with a
credential whose authority is B but whose bound account is A — silent wrong-tenant requests or opaque
token failures instead of "not signed in". The Exchange CONNECT also receives this stale UPN.
*Fix shape:* when tenant or client id actually changes, null `_record`, delete/ignore the persisted
record (it encodes the old authority), and drop `IsSignedIn` until a real sign-in completes.

### Data-integrity races

**F2 — Overlapping loads in the edit pane can apply object A's edits to object B's DN. (S1)**
`ViewModels/EditPaneViewModel.cs:198`
```csharp
public async Task LoadAsync(string distinguishedName, AdObjectType type)
{
    _dn = distinguishedName;
    ...
    var attrs = await _directory.LoadObjectAsync(distinguishedName);   // no _dn re-check after ANY await
```
`LoadAsync` is fired-and-forgotten on every selection change and has three awaits with **no staleness
re-check** — the class guards exactly this in its two fire-and-forget helpers
(`if (!string.Equals(_dn, forDn, ...)) return; // selection changed mid-load`, lines 643/665) but not in
its own body. Select slow-loading A, arrow-key to B: B's load completes, then A's continuation
repopulates every field — but `_dn` stays B. **Save writes A's values onto B.**
*Fix shape:* capture `forDn = distinguishedName` and bail after each await when `_dn` moved on (matching
lines 643/665), or a generation token like `CloudObjectDetailViewModel._detailToken`. The staleness guard
belongs after *every* await, including the group-members and protection reads.

**F18 — Re-targeting the SAME cloud row lets a superseded load double every list. (S3)**
`ViewModels/CloudObjectDetailViewModel.cs:567`
```csharp
if (!ReferenceEquals(_currentTarget, row)) return; // selection moved on
```
`LoadDetailAsync` checks only the row reference, never `_detailToken` — but Save, Revert and
enable/disable all call `SetTarget(row)` on the **same row**, which bumps the token, clears the lists and
starts a second load. The first load's continuation passes the `ReferenceEquals` check and appends its
results alongside: every license, membership and member rendered twice. The file's own header
(lines 22–24) states the invariant, and lines 655/728 implement it correctly; 567 is the one that
doesn't.
*Fix shape:* capture the token at entry and check `token == _detailToken` alongside the reference — the
same two-line guard as 655.

**F19 — A superseded post-save re-read stamps the NEW selection's values onto the OLD row. (S1)**
`ViewModels/CloudObjectDetailViewModel.cs:266`
```csharp
if (await LoadExchangeDetailAsync(row, mailbox: false, identityOverride: identity))
{
    SyncRowFromSections(row);
```
`LoadExchangeDetailAsync` documents that a **superseded** read "returns true … caller must not act"
(line 632) — but `SaveAsync` treats true as success and runs `SyncRowFromSections(row)`. Save a DL edit,
click another row while the re-read is queued on the serialised Exchange channel: `Sections` now
describe the new selection, and the old group's list row takes the **other group's** SMTP address — and
is later addressed by it. `RefreshAfterMailboxActionAsync` does the missing re-check; `SaveAsync`
doesn't.
*Fix shape:* make the superseded case distinguishable (tri-state result, or an out flag) and skip the
row sync unless the read really refreshed *this* row.

### The scenario runner

**F9 — A scenario cancelled mid-target records the half-processed target as Success. (S1)**
`Services/ScenarioRunner.cs:73`
```csharp
if (stepFailures.Count == 0)
{
    results.Add(new BulkItemResult(target.DistinguishedName, target.Name, true, null));
    operationLog?.Add(cancelled ? "    RESULT: Cancelled" : "    RESULT: Success");
```
The inner step loop breaks on cancellation with `stepFailures` empty, so the `BulkItemResult` is
`Success = true` — only the text operation log says Cancelled. Cancel a 5-step termination between steps
1 and 2: the account is disabled but keeps every group and licence, and the results window counts it a
success.
*Fix shape:* when `cancelled && steps remain`, record `Success = false` with "cancelled after step N of
M". The results dialog and the log must agree.

**F23 — `{date}` tokens are culture-sensitive; wrong-calendar dates get written into AD. (S3)**
`Services/ScenarioRunner.cs:470`
```csharp
.Replace("{datetime}", now.ToString("yyyy-MM-dd HH:mm"))
.Replace("{date}", now.ToString("yyyy-MM-dd"))
```
No `CultureInfo.InvariantCulture`, and the output is committed to AD attributes via
SetAttribute/SetDescription. On a workstation with a non-Gregorian default calendar (th-TH Buddhist,
ar-SA Um Al Qura), "Disabled {date}" writes `2569-09-02` — 500+ years off for every other reader.
*Fix shape:* pass `CultureInfo.InvariantCulture` to every `ToString` here. Trivial; test by formatting
under `new CultureInfo("th-TH")`.

### The on-prem directory service

**F5 — Membership writes use a case-sensitive Contains over a truncated value cache. (S1)**
`Services/DirectoryService.cs:889/893` (`ModifyMembers`) and `1034/1038` (`ModifyGroupMembership`)
```csharp
if (add) { if (!members.Contains(memberDn)) { members.Add(memberDn); changed++; } }
else     { if (members.Contains(memberDn)) { members.Remove(memberDn); changed++; } }
```
Two independent defects in one gate. (a) `PropertyValueCollection.Contains` is ordinal
**case-sensitive**; AD compares DNs case-insensitively. A DN sourced with different casing (CSV, Entra's
`onPremisesDistinguishedName`, an old record): remove finds nothing, silently skips, **reports success
while the user stays in the group**; add appends what the DC considers a duplicate and `CommitChanges`
rejects the batch — and because `ModifyMembers` commits once at the end, every other add in the call is
lost with it. (b) The property cache only holds the first ~1500 values of a large group — the same range
truncation `GetGroupMembersAsync` explicitly walks (lines 809–811) — so both checks give stale answers
past that point regardless of case.
*Fix shape:* stop pre-checking `Contains` as the correctness gate. Attempt the directed change and treat
the DC's "already exists" / "no such member" responses as idempotent success (the Exchange remove path
already follows this pattern); or compare case-insensitively **and** handle the constraint violations
anyway, since the >1500 case can't be pre-checked cheaply.

**F10 — The Contact filter matches nothing; the User filter also matches contacts. (S2)**
`Services/DirectoryService.cs:262`
```csharp
AdObjectType.Contact => "(objectCategory=contact)",
AdObjectType.User    => "(objectCategory=person)",
```
AD contact objects carry `objectCategory=person` (the contact class's `defaultObjectCategory` is
CN=Person), so the picker in Contact mode always returns zero rows; conversely the User filter matches
mail contacts, so a bulk-import manager lookup can bind a contact where a user account was intended.
*Fix shape:* `(&(objectClass=contact)(objectCategory=person))` for contacts;
`(&(objectCategory=person)(objectClass=user))` for users. Audit the `Unknown/Any` arm at the same time.

**F12 — CSV import maps read-only attributes; re-importing the app's own export fails the whole batch. (S2)**
`Services/BulkUserCsvImporter.cs:176`
```csharp
var ldap = AttributeCatalog.Ldap(header.Trim());
return AttributeCatalog.IsKnown(ldap) ? (Field.Attribute, ldap) : (Field.Unknown, null);
```
No `IsReadOnly`/`IsMultiValued`/`IsDnValued` guard. The list export's first column is `Name`, and the
catalog maps it to `name` — flagged read-only (AttributeCatalog.cs:40). The importer stores it as an
override, the bulk engine merges it, and `CreateUserAsync` tries to write the system-owned RDN attribute:
the DC rejects it and **every row fails** with an opaque LDAP error. Same for `Distinguished name`,
`Member of`, `When created` columns.
*Fix shape:* in `Classify`, accept only writable single-valued non-DN attributes; report skipped columns
as import warnings (the warnings channel already exists) rather than silently dropping them.

**F11 — A pasted line with a quoted, comma-containing name collapses to its last address. (S2)**
`Services/PastedMemberParser.cs:77`
```csharp
if (parts.Length > 1 && parts.All(p => p.Contains('@'))) return parts;
```
The comma-split path requires **every** piece to contain `@`. For
`"Doe, Jane" <jane@x.com>, John Smith <john@y.com>` the piece `"Doe` has no `@`, so the whole line stays
one term — and `Clean()` then unwraps only the **last** `<...>` pair. Jane Doe silently disappears:
no row, not counted in duplicates/dropped/unreadable. This is the same silent-loss class the
multi-recipient fix (7fd4ef1) addressed, surviving for quoted names — the standard Outlook/Gmail
To-line format.
*Fix shape:* split on commas **outside quotes and angle brackets** (a small state machine, not a regex),
then require `@` only in pieces that aren't quoted display-name+address pairs. Extend
`test-paste-parser.ps1` — the suite's assertions define the ladder, and this case belongs in it.

### Favourites and navigation (2.3.0/2.3.1 code)

**F17 — Favourites vanish after a reconnect: the root is rebuilt as an orphan. (S2)**
`ViewModels/MainViewModel.cs:325`
```csharp
RootNodes.Clear();
_cloudRoot = null;          // _favoritesRoot is NOT reset
```
`Initialize()` (Settings ▸ reconnect path) clears the tree but never nulls `_favoritesRoot`, so
`RebuildFavorites()` takes the not-null path, skips `RootNodes.Insert(0, ...)`, and repopulates children
of a node that is no longer in the tree. The Favourites section disappears until app restart, and
pin/unpin/reorder keep operating on the orphan — they save correctly but change nothing visible.
*Fix shape:* `_favoritesRoot = null;` beside `_cloudRoot = null;`. Regression test: simulate
Initialize-after-pins and assert the rebuilt root is `RootNodes[0]` — `test-favorites.ps1` is the home.

**F16 — Synthetic `fav:`/`cloud:` DNs still leak out of `SelectedNode` into real operations. (S3)**
`ViewModels/MainViewModel.cs:465` (New User), `:578` (Advanced Search base); Bulk Create takes the same
default-OU path.
```csharp
_dialogs.ShowNewUser(SelectedNode?.DistinguishedName, onCreated: ...);
```
The #8 fix guarded child enumeration only. With the Favourites row selected, New User's target OU is the
literal `fav:root` and the create fails with the same BAD_NAME class of error; a pinned saved search
gives `fav:<name>`; the cloud sections have leaked `cloud:<kind>` this way since they were added.
*Fix shape (the durable one):* stop overloading `DistinguishedName` as a marker channel. Add
`TreeNodeViewModel.DirectoryDn` that returns null for favourites root, saved-search pins, cloud nodes and
placeholders (a pinned **container** returns its real DN), and switch every consumer. That makes the next
consumer safe by construction, which per-call-site guards do not.

### Startup, settings, dialogs

**F8 — A torn settings write silently destroys every favourite and setting. (S2)**
`Services/SettingsStore.cs:44` (write), `:39` (fallback)
```csharp
try { File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions)); }
...
return new AppSettings();   // Load(), on any parse failure
```
Non-atomic write plus silent fresh-object fallback: a crash/power-loss/disk-full mid-write truncates
`settings.json`; the next launch parses it, fails, returns defaults, and the first Save overwrites the
remnants. Since 2.3.0 this file carries operator data (favourites), not just layout.
*Fix shape:* write to `settings.json.tmp`, then `File.Replace`/`File.Move(overwrite)` — and on parse
failure, rename the corrupt file aside (`settings.json.bad`) instead of silently discarding it, and say
so in the status bar.

**F21 — Auto-connect drops the ignore-cert flag, so LDAPS-with-untrusted-cert never reconnects. (S3)**
`ViewModels/MainViewModel.cs:304`. `ConnectionProfile.IgnoreCertificateErrors` exists
(Models/ConnectionProfile.cs:22) and the connect dialog sets it, but `AppSettings` has no field for it,
so the startup profile rebuild omits it and the silent reconnect throws a certificate error every
launch; the Settings dialog also shows the box unticked.
*Fix shape:* persist it in `AppSettings` beside `LastUseLdaps`, restore it in both the auto-connect
profile and the dialog prefill.

**F22 — The Entra Connect sync has no timeout and cannot be cancelled. (S2)**
`Services/CloudProvisioningService.cs:39` → `Services/EntraSyncService.cs:26`. `RunDeltaSyncAsync` has no
`CancellationToken` parameter, and the child `powershell.exe` is awaited unboundedly, so a WinRM hang
stalls New User / Copy User / Bulk Create provisioning forever — Bulk Create's own cancel can't reach it
(the ct exists upstream but the signature drops it).
*Fix shape:* thread a ct through, add a generous overall timeout (minutes, configurable), and kill the
child on expiry with a clear "sync did not finish" report — the retry affordances already exist.

**F24 — Unticking every cloud group still forces the Entra sync on. (S3)**
`ViewModels/NewUserViewModel.cs:95`
```csharp
public bool SyncMandatory => CloudGroups.Count > 0 || DistributionGroups.Count > 0 || IssueTap;
```
Counts rows, not `Include`-ticked rows, and Include toggles raise no CollectionChanged. Template seeds
cloud groups → operator unticks them all → `RunEntraSync` stays forced on with the checkbox disabled →
`CreateAsync` blocks demanding an Entra Connect server for a sync nothing needs. Only deleting the rows
unblocks.
*Fix shape:* count `.Any(g => g.Include)` and re-evaluate on Include changes (subscribe row
PropertyChanged, as the group pickers do elsewhere).

**F25 — The delete-confirmation alphabet kept the confusable 'o'. (S3)**
`ViewModels/MainViewModel.cs:994` vs `Services/PassphraseGenerator.cs:20`. The type-to-confirm token
generator hand-rolls its alphabet; it kept lowercase `o` while `SuffixChars`' documented "no l/o"
exclusion dropped it — in exactly the string an operator retypes to authorise a destructive delete.
*Fix shape:* expose the generator's unambiguous alphabet and use it here; delete the local constant.

**F27 — `Alert` throws instead of showing when no window exists. (S3)**
`Views/DialogService.cs:149` — `MessageBox.Show(Owner!, ...)` with a null-forgiven Owner throws
`ArgumentNullException` when no window is active during teardown, so late async alerts ("Password not
set", "sync failed") are lost. Every other dialog method assigns `Owner = Owner` (nullable-safe); only
`Alert` dereferences.
*Fix shape:* `Owner is null ? MessageBox.Show(message, ...) : MessageBox.Show(Owner, message, ...)`.

**F28 — The multi-select OU picker ignores its seeded selection. (S3)**
`Views/Dialogs/OuPickerWindow.xaml.cs:30` — `initialDns` is consumed only by the single-select branch, so
Advanced Search's "pick OUs" (which passes the current search bases) opens with nothing ticked and OK
returns an empty list: the operator's existing scoping silently evaporates.
*Fix shape:* pre-tick `initialDns` in the multi-select tree as nodes load (match by DN,
case-insensitive), and seed the checked-summary.

### Consolidation with teeth

**F26 — Copy User forked the template token engine, and the fork has already drifted. (S3, cleanup)**
`ViewModels/CopyUserViewModel.cs:403` — a near-verbatim private copy of `UserAttributeBuilder.Resolve`
(same regex, nine tokens, same switch) plus a copied `Ini` helper, despite the builder's header calling
itself the single source of truth. Confirmed drift: the copy resolves `{upnSuffix}` untrimmed, and
defaults a mail pattern the builder leaves empty — the same template already yields different
suggestions in New User vs Copy User. Any token added to the builder never reaches Copy User.
*Fix shape:* expose `UserAttributeBuilder.Resolve` (internal) or extend `Input` for the display-name
pattern, and delete the copy. `test-user-attributes.ps1` then covers all three creation paths for real.

---

## Tier 2 — reported with evidence, NOT independently verified

Strong leads from single finders. Verify each against the source before acting; line numbers are the
finder's, at `ac09e77`.

**Correctness-shaped**
- `GraphService.cs:1076` — a failed SubscribedSkus fetch is cached as a permanently-"ready" **empty** SKU
  map (`return _skuMap = map;` after the catch); one 429 at startup and licenses render as GUIDs all
  session. Same pattern for `_licenseGroupMap` at 1151. *(The line was verified to exist; the "never
  retried" claim was not traced.)*
- `GraphService.cs:898` — `_groupNameCache` is a plain `Dictionary` written from concurrent async flows
  and `Clear()`ed from Configure; also caches a null (failed) lookup forever.
- `GraphService.cs:109` — if deleting `graph-auth.bin` fails on sign-out (AV lock), `Configure` reloads
  the same record via `_record ??=` and the operator is silently signed back in on a shared workstation.
- `NameResolver.cs:41` — a transient LDAP failure caches the RDN fallback as if resolved, until
  reconnect.
- `Native/DnsSrv.cs:32` — `Marshal.PtrToStructure<DNS_SRV_RECORD>` runs before the `wType` check on every
  record in the DnsQuery result list, an out-of-bounds unmanaged read on non-SRV records that can AV
  uncatchably. Check `wType`/`wDataLength` first.
- `ViewModels/BulkCreateUsersViewModel.cs:122` — two "Edit Batch User" windows on the same row (nothing
  prevents it; non-modal) → second commit's `Rows.IndexOf(row)` is −1 → duplicate row → potentially two
  AD accounts for one hire.
- `Services/SavedSearchStore.cs:108` — names that sanitize to the same filename silently overwrite each
  other; a favourite pinned to the old search then runs the wrong query.
- `Services/BulkUserCsvImporter.cs:138` — import warnings count kept rows, so blank lines make "Row N"
  point at the wrong physical line.
- `Services/CsvText.cs:13` — the formula-injection apostrophe is added on export, never stripped on
  import: round-tripping the app's own CSV writes `'+1555...` into AD.
- `ViewModels/EditPaneViewModel.cs:288` — the group Members tab receives the `Truncated` flag but only
  logs it; the on-screen list reads as complete. (The delete flow surfaces the same flag correctly.)
- `ViewModels/CloudObjectDetailViewModel.cs:768` — after cloud enable/disable, `SetTarget(row)` recomputes
  buttons from **stale** `row.Values` (nothing writes accountEnabled back) and its `Clear()` wipes the
  success Status: buttons never flip, action looks failed.
- `ViewModels/CopyUserViewModel.cs:207` — MiddleName/Initials have no changed-handlers, so `{middleInitial}`
  naming patterns produce stale sAM/UPN suggestions (New User wires all four).

**Efficiency (verify impact before optimising)**
- `DirectoryService.cs:342` — one LDAP bind per DN-valued attribute value on object open (~350 sequential
  binds for a heavily-grouped user); the chunked-OR pattern at `GetGroupTypesAsync:1000` is the fix
  template. Same per-DN path in `ProjectColumn:1151` when a DN column is visible.
- `MainViewModel.cs:735` — `LoadCorrelationAsync` fetches all attributes (and triggers the bind storm) to
  read one, once per row in bulk cloud-group adds; `GetBasicInfoAsync` is the cheap template.
- `CloudProvisioningService.cs:84` — the sync poll calls `GetUserByUpnAsync` (≈3 Graph calls incl.
  licenses/groups) per user per attempt; one filtered `$select=id` probe per attempt suffices.
- `DirectoryService.cs:1027` — bulk add-to-groups loops per target (M×N binds and full member-list
  fetches); inverting to one `ModifyMembers(groupDn, allDns)` per group already exists at :879.
- `ViewModels/HybridGroupPickerViewModel.cs:51` — AD and Graph searches awaited sequentially; independent.
- `ViewModels/ObjectListViewModel.cs:195` — hiding a column triggers a full LDAP re-query (and per-row
  DACL reads); the cloud list's rebuild-only path is the template. `:84` — the quick filter refreshes per
  keystroke with no debounce — **already covered by the locked issue-#5 plan; fix it there, not twice.**
- `ViewModels/CopyGroupsViewModel.cs:178` — full `LoadObjectAsync` to read two strings.

**Duplication (consolidation targets, no urgency)**
- Store triplication: `TemplateStore`/`ScenarioStore`/`SavedSearchStore` are one JSON store copied three
  times; only SavedSearchStore got the offline-folder guard and case-insensitive import. Promote it.
- Error-mapper inconsistency: several cloud VMs show raw `ex.Message` where `GraphErrors.Friendly` exists
  (CloudObjectListViewModel:237 et al.), while two sites double-apply `ExchangeErrors.Friendly` to
  already-humanized `ExchangeException`s (MailboxRecipientPickerViewModel:86, NewCloudGroupViewModel:324),
  duplicating the RBAC hint. One `CloudErrors.Friendly(Exception)` dispatcher ends the per-site guessing.
- Cloud-twin correlation implemented three times (MainViewModel:692+733, ScenarioRunner:428/347,
  CloudTabViewModel:56) with confirmed shape drift (throw vs null).
- `CloudGroup.IsExchangeManaged` re-derived from label strings at five sites.
- User-creation preflight rules copied into three dialogs with drifted wording.
- The six-fold ConvertTo-Json array/object unwrap in ExchangeService; the guards duplicated between
  cloud Add/RemoveFromGroupsAsync; `HasUnsavedChanges` hand-mirroring `SaveAsync`;
  `_pendingDeleteMembership` field smuggling a local; dead `HasSelection`; DN-split and UPN-suffix parse
  copies. Also cosmetic-but-user-facing: `CopyUserWindow.xaml:23` claims "role" is copied — no such
  attribute exists in the flow.

---

## Suggested work packages

1. **`fix/exchange-channel` — F1, F14, F15, F13, F20, F6.** One file dominates
   (`ExchangeService.cs`); F1 first since F15's worst case depends on it. `test-hostscript.ps1` is the
   harness; mutation-test the timeout race and the single-result guard.
2. **`fix/silent-truncation` — F3, F4 (+ Tier 2 GetUserGroupsAsync swallow).** Small, high-value,
   one theme: a read that failed or truncated must say so, and a destructive step must refuse to succeed
   on one. Aligns with the house rule the group-delete record established.
3. **`fix/edit-races` — F2, F18, F19.** Three instances of one missing pattern (staleness token after
   await). The fixes are small; the tests are the work — the off-screen WPF harness pattern
   (`test-tabselection.ps1`) drives these.
4. **`fix/scenario-truth` — F9, F23.** Tiny diffs, audit-log honesty.
5. **`fix/directory-writes` — F5, F10, F12, F11.** The on-prem write/parse batch.
   F5 changes write semantics — live-test against a scratch group, both casings, and a >1500-member group
   if one exists.
6. **`fix/favourites-2` — F17, F16.** F17 is one line plus a regression test in `test-favorites.ps1`;
   F16 is the `DirectoryDn` refactor and worth doing properly once.
7. **`fix/small-sharp-edges` — F8, F21, F22, F24, F25, F27, F28, F26.** Independent one-file fixes;
   batch into one branch, one commit each.

Tier 2 items: verify-then-fold into whichever package touches the same file, or file as issues.
The two efficiency items marked as issue-#5 territory must go through that locked plan instead.
