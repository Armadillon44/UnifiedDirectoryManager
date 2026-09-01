# OneDrive capability — how it would actually be built in this codebase

Scope of this document: **repo evidence only.** Every claim below is traceable to a file and line in
`c:/temp/UnifiedDirectoryManager` at `feature/issues-4-5` (v2.3.0), or to the pinned NuGet package on this
machine. No vendor documentation was consulted; anything that needs a doc or a live tenant to settle is marked
`UNVERIFIED:` with the exact query that would settle it. Paths are relative to
`app/src/UnifiedDirectoryManager/` unless stated.

---

## 0. One-paragraph orientation

The app has exactly **two cloud channels**: the Graph SDK (delegated, in-process) and a single long-lived child
`pwsh` running `ExchangeOnlineManagement` (delegated token borrowed from the Graph sign-in, line protocol over
stdin/stdout). Both hang off one interactive browser sign-in. There is no third pattern, no job queue, no
background worker, and nothing in the app is designed to survive the window that started it. A OneDrive feature
has to land on one of those two channels — or introduce a third, which nothing in the codebase currently
supports.

---

## 1. `Services/GraphService.cs` — authentication, tokens, scopes

### 1.1 What it is

1,184 lines. `GraphService : IGraphService`, constructed once in `App.xaml.cs:44` and passed by interface
everywhere. Uses `Azure.Identity.InteractiveBrowserCredential` + `Microsoft.Graph.GraphServiceClient`
(`Microsoft.Graph` **6.2.0**, `Azure.Identity` **1.21.0**, pinned in `UnifiedDirectoryManager.csproj:44-45`).

### 1.2 Scopes are a single static array, requested up front

```csharp
// GraphService.cs:19-27
private static readonly string[] Scopes =
{
    "https://graph.microsoft.com/User.ReadWrite.All",
    "https://graph.microsoft.com/Organization.Read.All",
    "https://graph.microsoft.com/Group.ReadWrite.All",
    "https://graph.microsoft.com/Device.Read.All",
    "https://graph.microsoft.com/UserAuthenticationMethod.ReadWrite.All",
};
```

That one array is used in **two** places:

- `Configure` (`:87`) — `new GraphServiceClient(_credential, Scopes)`; this is the scope set the Graph client
  asks for on every request it makes.
- `SignInAsync` (`:97`) — `await _credential.AuthenticateAsync(new TokenRequestContext(Scopes), ct)`.

So the model is **up-front, all-at-once**, not incremental. Sign-in asks for the union of everything the app
might ever need from the Graph resource.

### 1.3 Token acquisition and caching

- `Configure(tenantId, clientId)` (`:60-89`) builds `InteractiveBrowserCredentialOptions` with
  `RedirectUri = http://localhost`, `TokenCachePersistenceOptions { Name = "UnifiedDirectoryManager.Graph" }`
  (`:32`, `:82`) and a previously persisted `AuthenticationRecord` (`:83`). No client secret, no certificate —
  consistent with the documented public-client/PKCE decision (`IGraphService.cs:5-10`,
  `AppSettings.cs:46-48`).
- The `AuthenticationRecord` is serialised to
  `%APPDATA%\UnifiedDirectoryManager\graph-auth.bin` (`:34-36`), so a relaunch can go silent.
- `IsSignedIn => _record is not null` (`:56`). `SignOut()` (`:101-110`) deletes the record file and rebuilds the
  credential *without* it, so cached tokens are not reused.
- Tenant/client id live in `AppSettings.EntraTenantId` / `EntraClientId` (`AppSettings.cs:49-50`) and are applied
  at startup in `App.xaml.cs:41-43` — configured but **not** signed in, so a cached token can be used silently.

### 1.4 The escape hatch that Exchange already uses — and that OneDrive would inherit

```csharp
// GraphService.cs:113-121
public async Task<string> GetAccessTokenAsync(string[] scopes, CancellationToken ct = default)
{
    if (_credential is null) throw new InvalidOperationException("Configure Entra ID … and sign in first.");
    if (scopes is null || scopes.Length == 0) throw new ArgumentException(…);
    var token = await _credential.GetTokenAsync(new TokenRequestContext(scopes), ct);
    return token.Token;
}
```

This takes an **arbitrary** scope array and is *not* limited to the static `Scopes`. `ExchangeService` calls it
with `{ "https://outlook.office365.com/.default" }` (`ExchangeService.cs:24`, `:912`) — a resource that appears
nowhere in `GraphService.Scopes`. **This is the existing, proven precedent for reaching a second resource
without touching the sign-in scope set.**

Consequences for a OneDrive/SharePoint scope:

- **If the work is done through Graph** (`Files.*`, `Sites.*`), the scope has to go into the static `Scopes`
  array to be picked up by `GraphServiceClient`, because that array is baked into the client at `Configure`
  time. Adding an entry there changes what `AuthenticateAsync` requests at sign-in.
- **If the work is done through a `.default` token for the SharePoint resource** (the Exchange shape), nothing in
  `Scopes` changes at all — it goes through `GetAccessTokenAsync` like EXO does.

**Re-consent — what the code proves and what it does not.** The code proves the *request* changes: a new entry in
`Scopes` is included in the `TokenRequestContext` at both `AuthenticateAsync` and every Graph call. It also shows
`GetTokenAsync` at `:119` is a **credential that can fall back to interactive** — `InteractiveBrowserCredential`,
not a silent-only credential — so a scope the cache cannot satisfy can pop a browser window mid-operation, on
whatever thread called it.

`UNVERIFIED:` whether adding a delegated scope forces every existing signed-in admin to re-consent. That depends
on whether the tenant granted **admin consent** for the new permission on the app registration, in which case
MSAL should acquire it without a user prompt. Settle it by (a) reading Microsoft's incremental-consent/`.default`
docs for public clients, and (b) the decisive live test: add the scope, grant admin consent on the app
registration, launch on a machine with an existing `graph-auth.bin` and a warm
`UnifiedDirectoryManager.Graph` cache, and observe whether a browser opens. The project already ran the analogous
test for Exchange (`README.md:212-213` documents `Exchange.Manage`, **not** `Exchange.ManageV2`, as the required
delegated permission with admin consent), and the 2.3.0 plan's "Key finding: no new sign-in, no new consent"
section (`docs/exchange-online-nav-and-cloud-groups-plan.md:32-46`) is the house template for writing this up.

### 1.5 Diagnostic precedent worth copying

`ExchangeService.LogTokenClaims` (`:1236-1250`) base64-decodes the JWT payload and logs `aud`, `scp`, `roles`,
`appid` — never the token. When "did my new scope actually land in the token?" becomes the question, this is
already the answer mechanism.

### 1.6 Does the pinned SDK have the OneDrive surface? Yes.

Checked `%USERPROFILE%\.nuget\packages\microsoft.graph\6.2.0\lib\net8.0\Microsoft.Graph.xml`. Present:

- `Microsoft.Graph.Users.Item.Drive.DriveRequestBuilder` and `Users.Item.Drives.DrivesRequestBuilder`
- `Microsoft.Graph.Drives.Item.DriveItemRequestBuilder`, `.Root.RootRequestBuilder`
- `Microsoft.Graph.Drives.Item.Items.Item.Permissions.*` (incl. `Item.Grant.GrantRequestBuilder`)
- `Microsoft.Graph.Drives.Item.Items.Item.Invite.InviteRequestBuilder`
- `Microsoft.Graph.Drives.Item.Items.Item.Copy.CopyRequestBuilder`
- `Microsoft.Graph.Sites.SitesRequestBuilder`, `Sites.Item.Drive.DriveRequestBuilder`
- Reports: `GetOneDriveUsageAccountDetailWithDate/WithPeriod`, `GetOneDriveUsageAccountCountsWithPeriod`,
  `GetOneDriveUsageFileCountsWithPeriod`

So no SDK upgrade is needed to *call* these. Whether the **delegated** permission model allows an admin to act on
another user's OneDrive through them is a separate question this repo cannot answer —
`UNVERIFIED:` needs the Graph permissions reference for `/users/{id}/drive`, `driveItem: invite`,
`driveItem: copy`, and the `getOneDriveUsageAccountDetail` report.

### 1.7 Graph error handling and transient-failure precedent

- `Services/GraphErrors.cs` + `GraphErrors.Friendly(ex)` — every Graph failure is funnelled through it before
  reaching an operator (e.g. `ScenarioRunner.cs:63`).
- `GraphService.BindPrincipalsAsync` (`:399-445`) is the retry precedent: up to 3 retries with
  `Task.Delay(attempt * 2 s)` on a **narrowly matched** replication-delay error (`IsReplicationDelay`), plus an
  `IsAlreadyPresent` branch that treats "already there" as success, not failure. The house rule stated in
  `CloudProvisioningService.cs:113-118` is explicit: *"Deliberately narrow: it matches the specific wordings
  Microsoft returns and retries nothing else, because a broad retry turns a real, permanent failure into a long
  wait ending in the same error."*

---

## 2. `Services/ExchangeService.cs` — the out-of-process pwsh channel

2,099 lines, of which **~820 are a single unchecked PowerShell string literal** (`HostScript`, `:1277` to `:2097`).
This is the template a "Graph can't do it, host a module" OneDrive channel would follow — and its limits are the
single most important input to the OneDrive design.

### 2.1 Why out of process (recorded rationale, `:9-21`)

The EXO module bundles its own MSAL/Azure.Core that clash with the Graph SDK's newer versions in one process
(*"Module could not be correctly formed"*). A child process gets clean assemblies. Confirmed independently by
`docs/v2.0-spike-findings.md` and memory note *Exchange v2 spike findings*. The csproj comment
(`UnifiedDirectoryManager.csproj:46-51`) records the same: **no NuGet dependency; pwsh 7 + the module are a
runtime prerequisite on each admin machine.**

### 2.2 Framing — the line protocol

- Sentinels (`:25-27`): `<<<UDM-BEGIN>>>`, `<<<UDM-END>>>`, `<<<UDM-READY>>>`.
- Host script is written to `%TEMP%\UnifiedDirectoryManager\exo-host.ps1` **on every connect**
  (`WriteHostScript`, `:1259-1266`) and launched by `ResolvePwsh()` (`:1252-1257`, prefers
  `%ProgramFiles%\PowerShell\7\pwsh.exe`, else PATH) with
  `-NoProfile -NoLogo -NonInteractive -ExecutionPolicy Bypass -File <path>` (`StartProcessLocked`, `:941-979`).
- C# → host: one line, `VERB <base64-utf8-json>` (`SendAndReadLockedAsync`, `:1018-1044`). Verbs are
  `CONNECT`, `OP`, `QUIT` (host loop `:1505-1546`).
- Host → C#: `BEGIN` line, one compressed JSON envelope line, `END` line. Envelope shape is
  `{ ok, error, data, detail }`, parsed into `record struct Resp` (`:1086-1106`).
- **Only `__emit` writes to stdout** (`:1279-1285`); every cmdlet's output is captured into a variable or
  suppressed with `6>$null` or `-WarningAction SilentlyContinue`. Stray stdout corrupts the frame. stderr is
  drained separately into a capped 8 KB `StringBuilder` (`:976`, `DrainStderr` `:1065-1073`).
- Everything is serialised behind one `SemaphoreSlim _gate` (`:39`) — **one operation at a time, app-wide**.

### 2.3 How the token is passed in

`EnsureConnectedLockedAsync` (`:901-937`):

1. Start pwsh, read until `READY` (`ReadUntilReadyLockedAsync`, `:999-1016`).
2. `token = await _graph.GetAccessTokenAsync(ExoScopes, ct)` where `ExoScopes = { "https://outlook.office365.com/.default" }`.
   A failure here kills the host and produces a *"grant the app registration the Office 365 Exchange Online
   permission with admin consent"* message (`:916-921`).
3. `LogTokenClaims(token)` — logs `aud`/`scp`/`roles`/`appid`, never the token.
4. `CONNECT` with `{ token, org, upn }`. The host prefers `-UserPrincipalName` and only falls back to
   `-Organization` (`:1513-1522`); the comment at `:923-925` records why: *"Delegated token → connect with
   -UserPrincipalName (the signed-in admin); -Organization is the app-only pattern and leaves a delegated connect
   malformed."*

The token goes over **stdin only**, never the command line (`:19-20`, and the same rule in `EntraSyncService`
where the credential goes via the child's environment, `EntraSyncService.cs:88-94`, and in
`README.md:246-248`).

### 2.4 Timeouts, and what expiry actually does

| Constant | Value | Line |
|---|---|---|
| `StartupTimeout` | 60 s | `:31` |
| `ConnectTimeout` | 150 s | `:29` |
| `OpTimeout` (default per-op) | **90 s** | `:30` |
| `ListTimeout` (opt-in, for sweeps) | **180 s** | `:36` |

`RunOpAsync(request, ct, timeout = null)` (`:872-898`) takes an optional per-op override; that override was added
by the 2.3.0 work (plan gotcha G1). **The ceiling for any single op on this channel is 180 s**, and nothing in the
code raises it.

Two distinct failure modes:

- **Timeout.** `ReadLineLockedAsync` (`:1046-1063`) links the caller's token to a `CancelAfter(timeout)`. On
  expiry it calls `KillLocked()` — *"an EXO cmdlet can't be interrupted mid-run, so abandon the process"* — and
  throws `ExchangeException("The Exchange Online operation timed out.")`. **A timeout destroys the shared
  session for every other feature**, including an in-flight scenario, the ExOL tab and the licence guardrail.
  The comment at `:33-35` says so explicitly.
- **Session lapse.** `RunOpAsync` (`:880-887`) checks `IsSessionExpired(resp.Error)` — a **string heuristic**
  (`:1075-1084`, matching *session / expired / reconnect / Connect-ExchangeOnline / not recognized*) — and on a
  match kills the host, reconnects (which re-borrows a fresh token from `GetAccessTokenAsync`) and retries the
  op **exactly once**. The Entra token itself is never inspected for expiry; refresh is implicit in
  `_credential.GetTokenAsync`.

Cancellation: a user cancel propagates (`:1057`), but the host is killed either way, so an EXO write already in
flight is **abandoned, not undone**. `ScenarioProgressViewModel.Cancel` says as much: *"in-flight Exchange calls
are abandoned"* (`ScenarioProgressViewModel.cs:52-53`).

### 2.5 How a new operation is added (mechanical checklist)

1. **`Services/IExchangeService.cs`** — add the method with its XML doc.
2. **`Services/ExchangeService.cs`** — a thin public method that builds an anonymous object
   `new { op = "verb-name", … }` and calls `RunOpAsync`, optionally with `ListTimeout`
   (e.g. `ConvertMailboxAsync` `:230-235`, `SetForwardingAsync` `:237-242`).
3. **`HostScript`** — a new `case` in the `switch ($r.op)` block (`:1524` onward). The `default:` arm
   (`:2090`) returns `{ ok = $false; error = "Unknown op: …" }`, so a typo fails loudly. Good.
4. **Mapping** — a `MapX(JsonElement)` helper if it returns data; note the recurring pattern that
   `ConvertTo-Json` renders a **single** result as an object and multiple as an array, so every reader handles
   both (`:158-165`, `:180-186`, `:249-256`).
5. **Tests** — `app/build/test-hostscript.ps1` (51 KB) exists precisely because the host script is an unchecked
   literal. Helper functions are deliberately factored out of the op bodies so they can be tested without a
   tenant (see the comment above `__newDgParams`, `:1468-1470`).
6. **Docs** — `README.md` prerequisites and RBAC roles (`README.md:204-229`); the Wiki.

### 2.6 Established sharp edges in the host script (they will bite a OneDrive channel too)

Recorded in the 2.3.0 plan `docs/exchange-online-nav-and-cloud-groups-plan.md:300-360` and visible in the code:

- **G3** — the literal has no compiler, no IntelliSense and no syntax check. Keep logic out of it.
- **G4** — EXO V3 returns booleans as `"True"`/`"False"` strings; `-not "False"` is `$false`. Cast with
  `[string]`, compare `([string]$x -eq 'True')` (`__yn`, `:1291`).
- **G2 / `__isWanted`** (`:1300-1316`) — *"a non-existent `-Identity` makes a Get- cmdlet return EVERY object
  rather than erroring."* Every single-object read validates that the returned object actually carries the
  identifier that was asked for.
- **Budgeted lookups** — `__ownerBudget` / `__ownerReset` / `__ownerDiag` (`:1322-1400`): resolution costs a
  round trip on a serialised channel, so it is capped per op and the degradation is *reported* (`detail`,
  logged at Info by `RunOpAsync:895`) rather than silently passed off as fact.
- **Three outcomes, not two** (plan G13): resolved / not found / not looked up must be distinguishable.

### 2.7 What this means for a OneDrive PowerShell channel

`ExchangeService` is **not** a general-purpose PowerShell host. It is hard-wired to one module:
`Import-Module ExchangeOnlineManagement` (`:2062`, exits 1 on failure), `Connect-ExchangeOnline -AccessToken`
(`:1517`/`:1519`), `Disconnect-ExchangeOnline` on `QUIT` (`:1512`). A SharePoint/OneDrive module would need
either a **second host class** (near-total copy of ~350 lines of process/framing plumbing) or a refactor of that
plumbing into a shared base with a per-module preamble. Neither exists today.

`UNVERIFIED:` whether a SharePoint admin module accepts a **borrowed delegated access token** the way
`Connect-ExchangeOnline -AccessToken` does. That is the single load-bearing assumption of the whole
"host a module" pattern. Settle it by reading the parameter reference for `Connect-SPOService` and for
`Connect-PnPOnline` (the `-AccessToken` / `-Interactive` / `-ClientId` parameter sets) — and note PnP.PowerShell is a
community module, which would be a new supply-chain and prerequisite decision, not just a technical one.

Also note: the app ships **self-contained single-file** and deliberately does not bundle PowerShell
(`README.md:205-211`, csproj comment `:46-51`). A second module is a **second per-machine prerequisite** on every
admin workstation, and the README's prerequisite list is where that cost becomes visible to operators.

---

## 3. `Models/Scenario.cs` + `Services/ScenarioRunner.cs` — the step engine

### 3.1 The full `ScenarioActionType` list (`Models/Scenario.cs:4-62`)

On-prem AD: `Disable`, `Enable`, `Unlock`, `RemoveAllGroups`, `AddToGroups`, `RemoveFromGroups`,
`SetAttribute`, `ClearAttribute`, `SetDescription`, `MoveToOu`.

Entra ID (cloud twin): `CloudDisableAccount`, `CloudEnableAccount`, `CloudRevokeSessions`, `CloudAddToGroups`,
`CloudRemoveFromGroups`, `CloudRemoveAllGroups`.

Exchange Online (pure-cloud, by UPN): `ExchangeConvertToShared`, `ExchangeConvertToRegular`,
`ExchangeSetForwarding`, `ExchangeClearForwarding`, `ExchangeDelegateToManager`.

Meta: `SaveOperationLog`.

**There is no licence step.** Licence add/remove exists only as an interactive command in the Cloud detail pane
(`ViewModels/CloudObjectDetailViewModel.cs:853`, `:897`), never as a scenario step. See §3.6.

### 3.2 How a step is defined

`ScenarioStep` (`Models/Scenario.cs:82-110`) is a flat bag; only the fields relevant to `Action` are used and
the rest stay at defaults:

`Action`, `Attribute`, `Value`, `List<string> GroupDns`, `List<CloudGroupRef> CloudGroups`, `TargetOu`,
`ForwardingTargetRef? ForwardingTarget`, `bool DeliverAndForward`.

`ForwardingTargetRef` (`:71-78`, `{ Identity, Name }`) is the precedent for **a step that needs a picked
principal**: store the machine identity *and* the display name, because the log and the editor both need the
friendly one. A "transfer OneDrive to X" step would add exactly this shape.

`Scenario` (`:116-120`) is `{ Name, Description, List<ScenarioStep> Steps }`, persisted one JSON file per
scenario by `ScenarioStore` (`Services/ScenarioStore.cs`) in
`%APPDATA%\UnifiedDirectoryManager\Scenarios` (or a shared folder, so a team can share them).
`JsonStringEnumConverter` is used *"store action names, not numbers"* (`:16-20`) — adding an enum member is
therefore additive and safe **in the forward direction only**.

> **Trap:** `LoadAll` (`:34-49`) wraps each file in `try { … } catch { }` and *"skips corrupt/unreadable files
> rather than failing the whole load."* A scenario JSON containing a step name an older build does not know
> **silently disappears from the menu** on that build. Mixed-version teams sharing a scenarios folder will see a
> scenario vanish with no error at all.

### 3.3 How a step reports success, failure and skip

`ScenarioRunner.RunAsync` (`:22-86`):

- Outer loop over targets; **each step is isolated** — a step that throws is recorded and the scenario
  **continues** with the remaining steps for that target, because *"AD has no transactions, so earlier steps stay
  committed"* (`:37-39`).
- Per step: `var (newDn, detail) = await RunStepAsync(...)`; `dn = newDn` (a `MoveToOu` renames the object for
  later steps); then `operationLog?.Add($"    {n}. {detail}")` and `live?.Report($"    ✓ {detail}")` (`:50-54`).
- On exception: `GraphErrors.Friendly` for an `ODataError`, else `DirectoryService.Friendly`; the message becomes
  `step "{Action}": {reason}`, appended to `stepFailures`, written to the log as `✗ FAILED — …` (`:60-68`).
- `OperationCanceledException` under a requested cancel breaks the target loop and is recorded as
  `RESULT: Cancelled` (`:55-59`, `:71-80`).
- A target is `BulkItemResult(dn, name, true/false, summary)`; success only if `stepFailures.Count == 0`.
- **There is no `Skipped` state.** A step with nothing to do returns a *success* whose `detail` says so in prose:
  `"Exchange: delegate to manager (no manager set — skipped)"` (`:245`), `"Move to OU (no target configured)"`
  (`:170`), `"Add to on-prem groups (no groups configured)"` (`:135`). A skip is text, not a status.
- The one deliberate exception: `CloudRemoveAllGroupsAsync` (`:271-336`) builds a full detail string of what it
  *did* remove, then **throws** with that string as the message if any group could not be removed —
  *"so a termination run isn't reported as a false success, while the detail still records which groups WERE
  removed (for re-add)"* (`:327-333`). This is the house pattern for "partly succeeded".

### 3.4 The operation log

- The log is an `IList<string>` passed into `RunAsync` and mutated in place; the runner writes
  `=== {Type}: {Name} ===`, the DN, one numbered line per step, then `RESULT: …` and a blank line
  (`:31-33`, `:51`, `:72-82`).
- Multi-item changes use `Bullets()` (`:461-462`) — one `        • item` per line.
- Removals are recorded as **re-addable records**: name **and** DN for on-prem (`:124-126`), name **and** object
  id for Entra (`:319`), name **and** the SMTP actually used plus the channel and kind for Exchange DLs
  (`:305-307`). The stated intent is that the log is what lets an admin put access back.
- `SaveOperationLog` is a **meta-step**: `RunAsync` `continue`s past it (`:45-47`) so it is never counted or run
  per object. Its only effect is that `MainViewModel.RunScenarioAsync` sees it in the step list.
- Where it goes: `MainViewModel.RunScenarioAsync` (`:1074-1080`, `:1101-1112`) computes the default from
  `OperationLog.ResolveDirectory(Settings)` + `{SafeName}-{yyyyMMdd-HHmmss}.log`, shows it in the confirm dialog,
  then offers a Save-As. Declining the Save-As triggers a second confirm ("run without saving a log?").
  `WriteOperationLog` (`:1126+`) prepends a header (scenario, purpose, run-by) and never rethrows on a write
  failure.

### 3.5 Adding a new step type — the complete file list

Nothing generates this; every one of these is hand-edited.

| # | File | What |
|---|---|---|
| 1 | `Models/Scenario.cs` | new `ScenarioActionType` member (+ any new payload field on `ScenarioStep`, and a `*Ref` class if it needs a picked principal) |
| 2 | `Services/ScenarioRunner.cs` | `case` in `RunStepAsync` (`:94-263`), a guard (`EnsureExchange`-style, `:365-380`), an identity resolver (`ResolveMailboxIdentityAsync`, `:384-395`), and the `detail` string |
| 3 | `Views/Converters.cs` | label in `ScenarioActionToLabelConverter` (`:54-86`) |
| 4 | `ViewModels/ScenarioEditorViewModel.cs` | `Show*` property (`:60-66`), `OnPropertyChanged` in `OnActionChanged` (`:68-78`), ctor read-back (`:20-32`), `ToStep()` write-back (`:124+`), a `[RelayCommand]` picker |
| 5 | `Views/Dialogs/ScenarioEditorWindow.xaml` | a panel bound to `Visibility={Binding Show*, Converter={StaticResource BoolToVis}}` (see `:122-131` for the forwarding block) |
| 6 | `ViewModels/MainViewModel.cs` | `DescribeStep` (`:1163-1191`) **and** a caution line in `RunScenarioAsync` (`:1040-1066`) |
| 7 | `Services/IDialogService.cs` + `Views/DialogService.cs` | a picker method, if the step needs one (`PickMailboxRecipient` `:36`, `PickCloudGroups` `:33`) |
| 8 | `README.md` / Wiki | the operator-facing description and any new prerequisite/RBAC |

> **Trap — a missing case is silent in three places.** All three switches have a permissive default arm:
> `RunStepAsync` ends with `default: detail = step.Action.ToString();` (`ScenarioRunner.cs:260-262`),
> `ScenarioActionToLabelConverter` with `_ => value.ToString()`, `DescribeStep` with `_ => step.Action.ToString()`.
> An enum member added without a runner case therefore **runs, does nothing, and is logged with a ✓ and the raw
> enum name as its detail.** For a step whose job is to preserve a departing employee's files, that failure mode
> is unacceptable and should be closed deliberately (throw on `default`, or an exhaustiveness test) as part of
> the work.

### 3.6 Existing cloud steps and the licence-removal guardrail (a OneDrive step's neighbours)

Guards, in order of severity:

- `EnsureSignedIn()` (`:365-369`) — Entra sign-in required.
- `EnsureExchange()` (`:373-380`) — service configured **and** signed in to Entra, *"since its token is borrowed
  from the Entra sign-in."*
- `ResolveMailboxIdentityAsync` (`:384-395`) — users only; resolves the **live** `dn` (not
  `target.DistinguishedName`) so it still works after an earlier `MoveToOu`; throws if there is no
  `userPrincipalName`.
- `ResolveCloudUserIdAsync` / `ResolveCloudObjectIdAsync` (`:400-433`) — UPN→Graph id for users, name for
  computers, `objectSid` for groups; throws *"may not be synced yet"* when there is no match.

The **licence-removal guardrail** is not in the runner — it is in
`ViewModels/CloudObjectDetailViewModel.cs:860-925`:

1. Only `l.CanRemoveDirectly` licences are removable; group-inherited ones are refused with an explanation
   pointing at the "Assigned via" column (`:870-878`, model at `Models/CloudLicense.cs:18-22`).
2. `AddMailboxGuardrailAsync` (`:905-925`) does a **best-effort** `GetMailboxAsync` and, if the mailbox is still
   `Regular`, appends to the confirm dialog: *"⚠ This user still has a REGULAR mailbox. If a removed license
   provides Exchange Online, the mailbox will be DELETED after ~30 days. Convert it to a shared mailbox first…"*
   A failure to check is swallowed to a `Warn` — the guardrail degrades, it never blocks (`:924`).
3. Then a normal `Confirm`.

This is the exact shape a OneDrive guardrail should take: **look at the real state of the dependent resource,
say the concrete consequence and the timeframe, name the remedy, and degrade to silence rather than to a block.**
The same instinct is in `Models/Scenario.cs:45` (*"convert the user's mailbox to a shared mailbox (so it survives
unlicensing)"*) and in the `MoveToOu` caution (`MainViewModel.cs:1052-1054`) about hybrid soft-delete.

**Ordering matters and the app does not enforce it.** Steps run in the order the operator listed them
(`RunAsync:42`), and there is no dependency model. A OneDrive step placed *after* a licence removal, or after a
`MoveToOu` that soft-deletes the cloud user, would act on a resource that is being torn down. Today the only
mitigation available is the confirm-dialog caution block (`RunScenarioAsync:1044-1066`), which lists cautions but
does not reorder or refuse.

### 3.7 The seeded "Terminate User" scenario

`ScenarioStore.SeedDefaults` (`:90-114`): `Disable` → `RemoveAllGroups` → `ClearAttribute(manager)` →
`MoveToOu("")` (deliberately blank; a blank target is a no-op so the seed stays portable) →
`SetDescription("Terminated {date} by {admin}")`. Tokens supported in text values: `{date}`, `{datetime}`,
`{time}`, `{admin}`, `{name}` (`ScenarioRunner.ResolveTokens`, `:465-476`). Note the seed contains **no** cloud
or Exchange steps and **no** `SaveOperationLog` — a real termination flow is assembled by the operator.

---

## 4. How the app surfaces long-running work today

### 4.1 The one real progress surface

`Views/Dialogs/ScenarioProgressWindow.xaml` + `.xaml.cs` + `ViewModels/ScenarioProgressViewModel.cs`:

- **Modal**, opened by `DialogService.ShowScenarioRun` (`Views/DialogService.cs:154-159`) with
  `.ShowDialog()`; the run starts in the window's `Loaded` handler and the caller blocks until the operator
  closes it.
- Live log is an `ObservableCollection<string>` fed by `IProgress<string>`; a `Progress<T>` created on the UI
  thread marshals callbacks back to it (`ScenarioProgressViewModel.cs:68-70`). A `LineAdded` event drives
  auto-scroll. Lines are rendered as read-only borderless `TextBox`es *"because the first thing anyone wants to
  do with an error is copy it"* (XAML comment), plus a "Copy log" button.
- Counter is `"{Done} of {Total} object(s) processed"` — **object-level, not step-level**. There is no
  progress bar and no per-step or byte-level progress anywhere in the app.
- Cancel: sets a flag, cancels a `CancellationTokenSource`, shows *"Cancelling — the run will stop after the
  current step…"*. The window's `Closing` handler (`:31-39`) **refuses to close mid-run** and converts the X
  into a cancel request. Close is enabled only on `IsDone`.

Other progress surfaces are the same shape at a smaller scale: `BulkUserCreator` / `NewUserViewModel` push
`report(...)` lines into a `ProgressSteps` log inside a modal window; `IsBusy` + a status string covers everything
else (`ExchangeTabViewModel.cs:27-29`, `CloudObjectDetailViewModel`).

### 4.2 Is there a minutes-scale precedent? Barely — and it is all polling inside a modal.

The longest deliberate waits in the codebase:

| Where | Shape | Total |
|---|---|---|
| `CloudProvisioningService.PollForCloudUserAsync` (`:44-60`) | 12 s settle, then 12 × 6 s | ≈ 85 s |
| `CloudProvisioningService.PollForCloudUsersAsync` (`:69-91`) | 12 s settle, then 20 × 6 s, shared deadline for the whole batch | ≈ 2 min |
| `RetryWhileCatchingUpAsync` (`:120-133`) | 5 attempts, 8 s apart, **narrow** error match only | ≈ 32 s per item |
| `ExchangeService.ListTimeout` (`:36`) | single blocking op ceiling | 180 s |
| `GraphService.BindPrincipalsAsync` (`:433-438`) | 3 retries, `attempt × 2` s | ≈ 12 s |

Every one of these:

- runs **inside** a modal window that owns the operation,
- reports progress as **prose lines**, not a percentage,
- dies with the window and the process — nothing is persisted, nothing resumes,
- has a **bounded, fairly short deadline** after which it gives up and says so.

### 4.3 What does *not* exist

- No background job store, no queue, no `IHostedService`, no task persistence.
- No polling of a **server-side** long-running operation (no `Location`-header / `202 Accepted` monitor pattern
  anywhere in the repo).
- No progress percentage, no byte counters, no per-item ETA.
- No way for an operation to outlive the modal that started it, or the app session.
- The Exchange channel actively **punishes** long operations: >180 s kills the host and takes every other
  Exchange feature down with it (§2.4). The 20-term `ResolveChunk` batching (`ExchangeService.cs:80-101`) exists
  purely to stay under that ceiling.

**Design consequence.** Any OneDrive design whose core operation is "copy/move N GB of files" or "run a
server-side job to completion" has **no home in this app as built**. The architecture supports (a) fast
metadata reads, (b) fast permission/config writes, and (c) *starting* something and reporting that it started.
A design built on Graph's asynchronous copy/move semantics would be the first thing in this codebase to need a
"we started it, here is how you check on it later" pattern — which means either a new persisted job record or a
deliberate, documented decision that the app only ever fires and forgets, and the operation log is the receipt.
This is the single biggest architectural question for the feature, and it is upstream of the
move-vs-leave-in-place question the user is undecided on: **"leave the files in place and re-permission them"
fits this architecture; "move the files somewhere else" does not.**

---

## 5. Where a "OneDrive" nav section would go

### 5.1 The tree is roots + fixed cloud children, not tabs

`Views/MainWindow.xaml` has a `TreeView` bound to `MainViewModel.RootNodes` on the left and **two** content
hosts on the right, switched by one boolean: `PaneHost` (`:198`, `Visibility={Binding IsAdView}`) and
`CloudPaneHost` (`:210`, `Visibility={Binding IsCloudView}`). `IsAdView => !IsCloudView`
(`MainViewModel.cs:199-200`).

The 2.3.0 plan is explicit that a third pane is a trap
(`docs/exchange-online-nav-and-cloud-groups-plan.md:50-67`): 21 XAML bindings to the `IsAdView`/`IsCloudView`
pair, `ApplyDock` calling `LayoutPane` exactly twice, and `RefreshAsync` branching only on `IsCloudView`.
**The locked decision was: Exchange Online renders in the existing cloud pane; it is a sibling root in the tree,
not a sibling pane.** A OneDrive section should follow the same decision unless there is a concrete reason not
to, and that reason would need stating loudly.

### 5.2 Exactly what 2.3.0 touched (commit `7b1fdd0`, "Exchange Online phase 1")

10 files, +461/−41:

```
Models/CloudObjectRow.cs                  +13   (a Source, so a DL and an Entra group stop being indistinguishable)
Services/AppSettings.cs                    +4   (two visible-column lists)
Services/CloudColumnCatalog.cs            +35   (CloudListMode members + column sets)
Services/ExchangeService.cs              +186   (list ops + host-script ops + the per-op timeout)
Services/IExchangeService.cs              +16
ViewModels/CloudObjectDetailViewModel.cs   +4
ViewModels/CloudObjectListViewModel.cs   +174   (mode routing, paging/capping, filters)
ViewModels/MainViewModel.cs               +52   (the second root + node routing)
ViewModels/TreeNodeViewModel.cs           +12   (CloudNodeKind members + glyphs)
Views/MainWindow.xaml.cs                   +6   (drag/drop predicate fix)
```

### 5.3 The same seams for OneDrive

1. **`ViewModels/TreeNodeViewModel.cs:11`** — `enum CloudNodeKind { Tenant, Users, Groups, Devices, Exchange,
   Mailboxes, DistributionGroups }`. Add `OneDrive` (+ any children). Glyph in the `Glyph` switch (`:139-152`);
   the existing cloud glyphs are `☁ 👤 👥 💻 ✉ 📬 📢`.
2. **`Services/CloudColumnCatalog.cs:7`** — `enum CloudListMode { Users, Groups, Devices, GroupMembers,
   Mailboxes, DistributionGroups }`. Add a mode plus its column set in `Defs(mode)` (`:89-98`), and a
   `VisibleOneDrive*Columns` list on `AppSettings` for persistence (`AppSettings.cs:57-62`).
3. **`ViewModels/MainViewModel.EnsureCloudRoot()` (`:363-391`)** — this is the method that adds/removes cloud
   roots. It tears down and rebuilds **both** roots on every call, gated on `_graph.IsSignedIn`. The Exchange
   root is deliberately shown even when Exchange is unconfigured, because *"a missing section is
   indistinguishable from a bug; the list pane explains what to fix instead"* (`:359-362`) — a rule a OneDrive
   root should follow too.
4. **`MainViewModel.OnSelectedNodeChanged` (`:396-417`)** — add an explicit `case` per new kind. The switch
   intentionally has **no** `default` arm and a comment saying why (`:405-407`); a missing case now falls
   through to *no load at all* rather than to the Users list, which was the 2.3.0 fix.
5. **`ViewModels/CloudObjectListViewModel.LoadAsync` (`:151-169`)** — add the `Header` string, then a branch in
   `LoadFirstPageAsync` / the fetch switch (`:289+`). Headers read `"Entra ID — Users"`,
   `"Exchange Online — Mailboxes"`; a OneDrive one would read `"OneDrive — …"`.
6. **`ViewModels/CloudObjectDetailViewModel.ModeFor` (`:1052-1065`)** — maps a selected row back to a list mode
   for the detail pane.
7. **The detail pane tabs** — `Views/Controls/CloudDetailView.xaml` has `Actions` (`:210`, visible when
   `IsMailbox`), `Licenses` (`:215`), `Member Of` (`:236`), `Members` (`:257`). A OneDrive actions surface would
   be a sibling tab here, gated on a new `IsOneDrive`-style flag. **Read plan gotcha G14 first**
   (`docs/exchange-online-nav-and-cloud-groups-plan.md:352-357`): the tab strip is a `CompositeCollection` and
   the `TabControl` will happily select a *collapsed* tab and render the pane blank; every rebuild must name the
   tab it means. `app/build/test-tabselection.ps1` reproduces it off-screen.
8. **The AD-side edit pane** — `Views/Controls/EditPaneView.xaml` has `Cloud` (`:251`, gated on
   `ShowCloudTab`) and `ExOL` (`:277`, gated on `ShowExchangeTab`) tabs, each hosting a control backed by
   `CloudTabViewModel` / `ExchangeTabViewModel`. A `OneDriveTabViewModel` + `OneDriveActionsView` would sit
   beside `ExchangeActionsView` here. Precedent worth copying from `ExchangeTabViewModel` (`:9-15`): it **never
   auto-fetches on selection** — the admin clicks "Look up mailbox" — *"because connecting to Exchange Online is
   comparatively slow"*, and it re-reads after every write so the display stays true. Note also plan D7: the same
   control is hosted in **both** the mailbox detail pane and the AD edit-pane tab.
9. **Wiring** — `App.xaml.cs:38-66` is the composition root; a new service is constructed there and threaded
   through `DialogService` and `MainViewModel` constructors, both of which already take 9–11 positional
   dependencies.

---

## 6. House style for a plan document

From `docs/exchange-online-nav-and-cloud-groups-plan.md` (32 KB), cross-checked against
`docs/issue-3-group-management-plan.md` and `docs/favorites-plan.md`.

**Structure, in order:**

1. `# Title` — feature-shaped, not ticket-shaped.
2. **Status line in bold**, naming the branch, what has landed, and the version it ships in — with an explicit
   instruction to keep it current: *"update it as commits land rather than leaving this line to go stale."*
3. **The asks**, numbered, in the requester's words, plus any constraint carried over from the ask.
4. **`## Locked decisions`** — a `| # | Decision |` table of `**D1**…**Dn**`, each a bolded one-line verdict
   followed by the reasoning and the trade-off accepted. Prefaced with: *"All four are decided. Nothing below is
   open for renegotiation mid-implementation."*
5. **`## Key finding: …`** — the load-bearing discovery stated up front (2.3.0's was "no new sign-in, no new
   consent", with `GraphService.Scopes` cited by file and line), including what it does **not** cover
   (*"What does change is RBAC"*).
6. **`## Architecture decision: <do X, do not do Y>`** — the rejected design named first, then bullet-pointed
   evidence for why it is a trap (with line numbers), then `**Decision:** …` in bold.
7. **`## Commit N · <title>`** — the work broken into commits, each with sub-headings like **Model and routing**,
   **Service work**, **Validation**, and any pre-existing defects the work exposes, listed as
   *"Two existing defects this section exposes, fixed here"*.
8. **`## Cross-cutting gotchas`** — numbered `**G1 · <one-line title in bold>**` entries. This is the most
   distinctive part of the style: each is a concrete, mechanical trap, usually with the wrong-looking behaviour
   spelled out (*"`Get-DistributionGroup -Identity $null` returns every group rather than erroring"*), a cited
   line, and a `**Decision:**` where one was needed. Later revisions add entries (G11 is literally titled *"Two D5
   choices do not survive contact with the cmdlet"*) rather than quietly editing the original decision.
9. **`## Risks to validate during implementation`** — a numbered list of things that must be tested against the
   live tenant before building on them. *"Test rather than copy."*
10. **`## Sequencing`** — a numbered list with `~~strikethrough~~` + **Done** (`commit-sha`) as items land, and a
    bolded pointer to what is next.

**Tone rules visible throughout:**

- Every claim carries a `File.cs:line` citation.
- Certainty is graded and stated: *"NOT yet live-tested"*, *"Awaiting a live retest"*, *"ROUGH"*.
- A decision that turned out wrong is **kept and annotated**, not deleted — the plan is a record of reasoning,
  not a spec sheet.
- Consequences are stated in operator terms with timeframes (*"the mailbox will be DELETED after ~30 days"*).
- Silent-failure modes are called out as the primary risk, repeatedly (*"a missing section is
  indistinguishable from a bug"*, *"a missing case is invisible rather than a crash"*, *"reads as the create
  having failed"*).
- Second person is avoided; the document argues with itself and then locks a decision.

---

## 7. Open questions this repo cannot answer (hand to the research pass)

1. `UNVERIFIED:` Does adding a delegated `Files.*`/`Sites.*` scope to `GraphService.Scopes` force existing signed-in
   admins to re-consent, given admin consent on the app registration? → incremental consent / `.default` docs for
   public clients, plus the live warm-cache test described in §1.4.
2. `UNVERIFIED:` Can a **delegated** admin token act on another user's OneDrive via `/users/{id}/drive`,
   `driveItem: invite`, `driveItem: copy`? → Graph permissions reference per endpoint.
3. `UNVERIFIED:` Does any SharePoint/OneDrive admin PowerShell module accept a **borrowed access token** the way
   `Connect-ExchangeOnline -AccessToken` does? → `Connect-SPOService` and `Connect-PnPOnline` parameter sets.
   This decides whether the §2 pattern is even reusable.
4. `UNVERIFIED:` Are Graph's copy/move operations asynchronous with a monitor URL, and what is the expected
   duration for a real user's OneDrive? → decides whether §4.3's "no home in this app" problem is real.
5. `UNVERIFIED:` What Microsoft actually provides for OneDrive retention on a deleted/unlicensed user, and whether
   it is reachable from a delegated context at all. → decides whether "leave in place" is even an app feature or
   just a tenant setting the app should *report* rather than set.

---

## Appendix — file map

| Concern | File |
|---|---|
| Graph auth, scopes, token cache | `Services/GraphService.cs:19-121` |
| Graph interface (add methods here) | `Services/IGraphService.cs` |
| Graph error friendliness | `Services/GraphErrors.cs` |
| pwsh channel + host script | `Services/ExchangeService.cs` (plumbing `:872-1106`, script `:1277-2097`) |
| Exchange interface | `Services/IExchangeService.cs` |
| Host-script tests | `app/build/test-hostscript.ps1` |
| Step model | `Models/Scenario.cs` |
| Step execution | `Services/ScenarioRunner.cs` |
| Step persistence | `Services/ScenarioStore.cs` |
| Step editor | `ViewModels/ScenarioEditorViewModel.cs`, `Views/Dialogs/ScenarioEditorWindow.xaml` |
| Step labels | `Views/Converters.cs:54-86`, `ViewModels/MainViewModel.cs:1163-1191` |
| Run confirmation + cautions + log write | `ViewModels/MainViewModel.cs:1030-1160` |
| Live progress | `ViewModels/ScenarioProgressViewModel.cs`, `Views/Dialogs/ScenarioProgressWindow.xaml` |
| Log destination | `Services/OperationLog.cs` |
| Minutes-scale polling precedent | `Services/CloudProvisioningService.cs` |
| Licence guardrail | `ViewModels/CloudObjectDetailViewModel.cs:860-925` |
| Nav tree kinds + glyphs | `ViewModels/TreeNodeViewModel.cs` |
| Cloud roots + node routing | `ViewModels/MainViewModel.cs:363-417` |
| List modes + columns | `Services/CloudColumnCatalog.cs`, `ViewModels/CloudObjectListViewModel.cs` |
| Detail pane tabs | `Views/Controls/CloudDetailView.xaml` |
| AD edit-pane tabs | `Views/Controls/EditPaneView.xaml:251-280` |
| Composition root | `App.xaml.cs:38-66` |
| Settings persistence | `Services/AppSettings.cs` |
| Prereqs + RBAC docs | `README.md:204-232` |
| Plan-doc house style | `docs/exchange-online-nav-and-cloud-groups-plan.md` |
