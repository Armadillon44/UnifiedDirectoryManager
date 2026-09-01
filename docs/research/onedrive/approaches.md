# OneDrive offboarding: the viable approaches, compared

**Status: decision input, nothing locked. Audited 2026-09-01 — see the change list below.** Written
2026-09-01 against UnifiedDirectoryManager v2.3.0 on
`feature/issues-4-5`. Builds on the four research briefs in this folder (`google-drive.md`,
`onedrive-mechanisms.md`, `api-and-auth.md`, `app-architecture.md`) and does not repeat their research.
Where a brief marked something `UNVERIFIED`, it stays unverified here — §8 carries the whole list forward.

Three external claims below are **new** verification done for this document, not inherited, because the
recommendation rests on them:

- `Set-PnPTenantSite -Owners` — "Specifies owner(s) to add as **secondary site collection administrators**.
  They will be added as additional secondary site collection administrators. Existing administrators will
  stay." Required permission: "SharePoint: Access to the SharePoint Tenant Administration site."
  https://pnp.github.io/powershell/cmdlets/Set-PnPTenantSite.html
- `Add-PnPSiteCollectionAdmin` — "Adds one or more users as site collection administrators to the site
  collection in the current context." You must already be a Site Collection Admin to run it; if you are not
  but hold the SharePoint Online admin role, the docs direct you to `Set-PnPTenantSite -Owners` instead.
  https://pnp.github.io/powershell/cmdlets/Add-PnPSiteCollectionAdmin.html
- `Connect-IPPSSession` **has an `-AccessToken` parameter** — "The AccessToken parameter specifies the OAuth
  JSON Web Token (JWT) that's used to connect to Security and Compliance PowerShell", available in "module
  version 3.8.0-Preview1 or later", in the **same `ExchangeOnlineManagement` module the app already hosts**.
  https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/connect-ippssession

And two repo facts re-read directly rather than inherited:

- `GraphService.cs:19-27` — the five delegated Graph scopes. No `Files.*`, no `Sites.*`.
- **The app has no mail-sending code of any kind.** `grep -riE "smtp|mailmessage|sendmail|System\.Net\.Mail"`
  over `app/src` returns only address-string handling in `Models/` (`GroupRef.cs`, `MailboxRecipient.cs`,
  `MailboxInfo.cs`, …). Nothing sends mail. Google behaviour #6 (notify the parties) therefore has **no
  existing machinery to hang off** in any approach below. That is a build cost, not a configuration choice.

---

## Audit log — what a verification pass changed, 2026-09-01

An adversarial re-check against the repository at commit `7423bcb` and against the vendor documentation.
**The recommendation (A6) survived. The reasoning behind it did not survive intact.** Most serious first:

| # | What was wrong | Where it is fixed |
|---|---|---|
| 1 | A6's whole "record what was done" discipline rests on the operation log, which **is not written unless the scenario contains a `SaveOperationLog` step — and the seeded Terminate User scenario does not have one.** On a stock install the granted URL is recorded nowhere. | A6 step 3 (boxed), A6 "How it fails" |
| 2 | The Microsoft pricing example was misread: **$614.40 is one 1 TB account's reactivation fee, not 100 accounts'.** | A5(c) |
| 3 | The day-365 deletion clock is a **cumulative unpaid-days** clock with a 1 July 2026 start and a 1 July 2027 floor — not 365 days from unlicensing. The app must not compute a day-365 date. | A5(a), §5 |
| 4 | The A1 build-effort anchor cited commit `7b1fdd0` (10 files, +461/−41) — that is the **nav-section** commit. The scenario-step comparator is `4bb3750`: 8 files, +170/−27, for five actions. | A1 "Build effort" |
| 5 | "Well under the 90 s op timeout" attributed an **`ExchangeService` constant to a channel that doesn't exist yet**, and ignored cold-connect cost (EXO budgets 60 s + 150 s) and PnP's one-hour non-renewable token. | A1 "Build effort" |
| 6 | This document and `transfer-vs-retain.md` named **two different cmdlets** for the same primitive, and neither is what Microsoft actually documents (a UI walkthrough naming no cmdlet). | A1 "Which cmdlet" |
| 7 | A2 implied a long `OrphanedPersonalSitesRetentionPeriod` buys proportionally longer life. It does not — **archiving happens on day 93 regardless of the retention period.** | A2 |
| 8 | Neither document raised **Teams chat files**, which live in the leaver's OneDrive and which a Graph copy does not preserve. This changes the A1-vs-A3 comparison. | new §3.5 |
| 9 | Neither document raised that **scenario targets are on-prem AD objects**, so a cloud-only leaver cannot be reached by a scenario step at all. | A6 |
| 10 | "Neither admins nor end users have access" was quoted truncated; the sentence continues with four admin actions, and the page separately documents a PowerShell unlock for read-only accounts. | A5(b) |
| 11 | Missing compliance finding: **the site collection recycle bin isn't indexed, so an eDiscovery hold can't reach content in it.** | A5 |
| 12 | Repo line references inherited from `app-architecture.md` had drifted 5–12 lines (`:245`, `:260-262`, `:305-307`, `:319`). | throughout; see §6 |

Claims that were checked and **held**: `Set-SPOSite -Owner`'s unsupported-for-OneDrive sentence (verbatim);
all four `driveItem: copy` quotes (verbatim); the SharePoint limits (100 GB / 30,000 / 2 GB / 15 GB / 400
chars / 13 / 2,600 / 10,000); `Connect-IPPSSession -AccessToken` exists in `ExchangeOnlineManagement` at
3.8.0-Preview1+; `Set-PnPTenantSite -Owners` vs `-PrimarySiteCollectionAdmin` (verbatim); "about an hour" for
a role change; the no-automatic-access sentence; day 275 eDiscovery; the retention-and-deletion timeline; the
seeded Terminate User scenario's five steps; the three permissive `default:` arms; the absence of any
mail-sending code.

---

## 0. The two facts that bound every approach

Both are established in the briefs. Everything downstream is a consequence, so they are stated once here and
not re-argued per approach.

### Fact 1 — there is no ownership transfer in Microsoft 365

A OneDrive is a site collection, not a bag of files with an owner attribute (`onedrive-mechanisms.md` §0).
Graph exposes no ownership operation. And the naive fix is documented as forbidden, verbatim from
[Set-SPOSite](https://learn.microsoft.com/en-us/powershell/module/sharepoint-online/set-sposite?view=sharepoint-ps):

> "Specifies the owner of the site collection. **Changing the Owner of a OneDrive is not supported and causes
> many experiences to break.**"

Reinforced by the unlicensed-accounts article, FAQ 18: "Modifying the 'owner' property of a OneDrive account to
any user or group other than the user who the account was provisioned for results in an unsupported state of
the OneDrive account." https://learn.microsoft.com/en-us/sharepoint/unlicensed-onedrive-accounts

**The trap worth naming:** the cmdlet will *let* you do it. `Set-SPOSite` lists `Owner` as a valid parameter on
a OneDrive site collection. It works, and it is unsupported. The PnP equivalent to avoid is
`-PrimarySiteCollectionAdmin`, which "will replace the current primary site collection administrator". The PnP
parameter that is *safe* is `-Owners`, which adds **secondary** administrators and leaves the primary alone.
Those are one word apart in the cmdlet surface, and picking the wrong one ships an unsupported tenant state
that looks like success.

### Fact 2 — delegated Graph cannot bootstrap itself into another user's OneDrive

Delegated `Files.*.All` means "all files **that the signed-in user can access**"
(https://learn.microsoft.com/en-us/graph/permissions-reference). And
[sharepoint-admin-role](https://learn.microsoft.com/en-us/sharepoint/sharepoint-admin-role) says: "Global
Administrators and SharePoint Administrators **don't have automatic access to all sites and each user's
OneDrive**, but they can give themselves access to any site or OneDrive."

So a SharePoint Administrator signed in to this app gets 403 against a departing user's drive until somebody
grants access to *that specific site* — and that grant has no Graph endpoint. The consequence reorganises this
whole comparison:

> **The site-collection-administrator grant is not one approach among several. It is the prerequisite for every
> approach that touches file content.** A1 below is not an alternative to A3/A4; it is the first half of them.

Approaches that only *configure and report* (A2) or only *preserve* (A5) escape Fact 2 entirely, because they
never read a file. That is most of why they are cheap.

---

## 1. The credential flag, up front

The task asked for every place an approach would need an app-only credential or certificate. Here is the whole
set in one table, because the answer is encouraging and should not be buried.

| Mechanism needed | Naive route | Needs a secret or certificate? | Delegated alternative that preserves the public-client model |
|---|---|---|---|
| Grant site collection admin on a OneDrive | `Connect-SPOService -ClientId -TenantId -CertificateThumbprint` | **YES — certificate.** This is the loud one. | **Yes.** `Connect-PnPOnline -AccessToken <borrowed delegated SPO token>` then `Set-PnPTenantSite -Owners`. Public client, PKCE, no secret. Exact analogue of the existing EXO borrow-the-token pattern (`ExchangeService.cs:24`, `:912`, `:1517`). |
| Read/write the tenant OneDrive retention period | `Set-SPOTenant` via SPO Management Shell (cert or `-Credential`) | **YES via that route** | **Yes**, on the same PnP channel. `UNVERIFIED:` that PnP exposes `OrphanedPersonalSitesRetentionPeriod` under that name — settle with `Get-Help Set-PnPTenant -Parameter *`. |
| Read another user's drive, enumerate items, copy | Graph **application** permission `Files.ReadWrite.All` — "all files in all site collections **without a signed-in user**" | **YES — app-only.** Tempting precisely because it skips the Fact-2 bootstrap. | **Yes**, delegated `Files.ReadWrite.All` / `Sites.ReadWrite.All` — **but only after** the site-collection-admin grant. App-only carries its own penalty: "Cross-geo copy isn't supported when using app-only authentication" ([driveitem-copy](https://learn.microsoft.com/en-us/graph/api/driveitem-copy?view=graph-rest-1.0)). |
| Purview retention policy or hold | `Connect-IPPSSession -AppId -CertificateThumbprint` | **YES via CBA** | **Yes.** `Connect-IPPSSession -AccessToken`, module 3.8.0-Preview1+, in the module the app already hosts. Token audience `UNVERIFIED` (§8). |
| SPO Management Shell at all | `Connect-SPOService -Url https://<t>-admin.sharepoint.com` (interactive by default; `-Credential` for unattended) | No secret **by default** — but `Connect-SPOService` has **no `-AccessToken` parameter**, so it cannot borrow the app's token. `-Credential` is username+password: **incompatible with MFA and with this app's model. Reject that variant outright.** | Interactive / `-UseSystemBrowser` avoids a secret but is a **second interactive sign-in in a child process** with its own token cache. It breaks the single-sign-in model rather than the no-secret model. Acceptable only as a fallback if PnP fails the spike. |
| Set the unlicensed **active-storage period** (push read-only/archive past 93 days) | `Set-SPOTenant -UnlicensedOneDriveAccountsActiveStoragePeriod <93–3650>` | No secret, but **SPO Management Shell only**, build **16.0.27600.12000+**, so it inherits the row above's auth problem | **None.** No PnP equivalent is documented, and Microsoft states *"There is no SharePoint Online PowerShell parameter for enabling the billing policy or Standard storage"* — the enablement toggles are **admin-center UI only**. The app can neither enable nor set this. Report at most. |

**Verdict on the credential question: no approach below requires a client secret or a certificate.** Every one
has a delegated, public-client route. The cost is not a credential — it is **one new delegated SharePoint API
permission** on the existing registration (resource "Office 365 SharePoint Online", app id
`00000003-0000-0ff1-ce00-000000000000`, e.g. `AllSites.FullControl`), admin-consented once, structurally
identical to what the app already does for Exchange. Plus, for the copy approaches only, a new delegated
**Graph** scope in the static `Scopes` array — that is the one that raises a re-consent question
(`app-architecture.md` §1.4, still `UNVERIFIED`).

One thing that is *not* a credential problem but is regularly mistaken for one: **the human must hold
SharePoint Administrator or Global Administrator**, and role changes take "about an hour" to take effect
(https://learn.microsoft.com/en-us/sharepoint/sharepoint-admin-role). No app permission substitutes for that,
and no amount of consent makes an ordinary helpdesk operator able to run these steps.

---

## A0. The non-approach, named so it stays dead

**Repoint the OneDrive at the manager.** `Set-SPOSite -Owner <manager>` on the leaver's personal site, or
`Set-PnPTenantSite -PrimarySiteCollectionAdmin`. This is the first idea everyone has, it is a one-line change,
it appears to work, and Microsoft documents it as unsupported in two places (Fact 1). The failure mode is not
an error — it is "many experiences break" later, on a site holding a departed employee's only copy of
something. **Do not build it. Do not leave it reachable from a settings screen.** If the PnP channel is built,
the wrapper must expose `-Owners` and must not expose `-PrimarySiteCollectionAdmin` at all.

Two details make this worse than it first reads, both re-fetched for this pass:

- `Set-SPOSite`'s own cmdlet description names `Owner` as one of a short list of parameters that **are** valid
  on a OneDrive: *"For OneDrive for Business site collection, the only valid parameters are Identity,
  AllowDownloadingNonWebViewableFiles, AllowEditing, ConditionalAccessPolicy, DefaultLinkPermission,
  DefaultSharingLinkType, DisableCompanyWideSharingLinks, LimitedAccessFileType, LockState, **Owner**,
  SharingAllowedDomainList, …"*
  (https://learn.microsoft.com/en-us/powershell/module/sharepoint-online/set-sposite). So it is not merely
  tolerated — it is explicitly listed as applicable, one line away from the sentence saying it breaks things.
- **It does not even buy the thing someone would try it for.** The obvious motive is "repoint the OneDrive at
  a licensed manager and the unlicensed clock stops". It does not. Verbatim, FAQ 18: *"the unlicensed
  enforcement effort doesn't utilize the 'owner' property to validate licensing. Therefore, modifying the
  'owner' property, while generally not a supported change, won't directly cause an account to be considered
  unlicensed."* Read in the direction that matters here: changing the owner changes nothing about
  licensing enforcement. It is pure downside.
  https://learn.microsoft.com/en-us/sharepoint/unlicensed-onedrive-accounts

---

## A1. Grant the manager access, leave the content in place

The primitive: add the manager as a **secondary site collection administrator** on the departing user's
OneDrive site collection, from inside the terminate scenario, at the moment of termination.

**Which cmdlet — reconciled, because the two decision documents disagreed.** `transfer-vs-retain.md` names
`Set-SPOUser -IsSiteCollectionAdmin`; this document names `Set-PnPTenantSite -Owners`. Both are inferences.
**Microsoft's own first-party instructions name no cmdlet at all** — they are an admin-center UI walkthrough,
verbatim from
[remove-former-employee-step-5](https://learn.microsoft.com/en-us/microsoft-365/admin/add-users/remove-former-employee-step-5):

> "In the navigation pane, select **More features**. Then, under **User profiles**, select **Open**. […] Under
> **People**, select **Manage User Profiles**. […] Right-click the former employee's user account, and then
> choose **Manage site collection owners**. Add the new user to **Site collection administrators** and select
> **OK**. The user can now access the former employee's OneDrive using their OneDrive URL."

Note *where* that lives: the **same classic User Profile Service page** as "Enable access delegation" (A2's
unverifiable precondition). The documented route is UI-only, which is exactly why every automated route here
is an inference. Three candidates, in descending order of fit for this app:

| Route | Module | Takes a borrowed delegated token? | Status |
|---|---|---|---|
| `Set-PnPTenantSite -Identity <odUrl> -Owners <upn>` | `PnP.PowerShell` (community) | **Yes** — `Connect-PnPOnline -AccessToken` | The only route compatible with the app's single-sign-in model. `UNVERIFIED` against a `/personal/…` URL (U3) |
| `Set-SPOUser -Site <odUrl> -LoginName <upn> -IsSiteCollectionAdmin $true` | SharePoint Online Management Shell (first-party) | **No** — `Connect-SPOService` has no `-AccessToken` | Better provenance, wrong auth shape. Fallback only |
| SharePoint admin center → More features → User profiles | none | n/a | What Microsoft documents. Not automatable |

The app should build the PnP route and **document the admin-center route as the manual fallback**, because if
U3 comes back negative that walkthrough is what the operator will fall back to.

**What the admin does.** Adds one step to the Terminate User scenario — "OneDrive: grant access to manager", or
to a picked person. Runs the scenario as usual. The step resolves the target's OneDrive URL, resolves the
grantee, makes one PnP call, and returns a one-line detail into the operation log. No new window, no waiting.

**What happens to the data.** Nothing. Not one byte moves, no permission on any item changes, no sharing link
breaks, version history is untouched. The site keeps its primary owner (the departed principal), which is the
supported state. This is the only approach in this document with **zero** data risk.

**What the receiving person sees.** A URL, and full control of that library when they open it. Microsoft's
wording is only *"The user can now access the former employee's OneDrive using their OneDrive URL"*
([remove-former-employee-step-5](https://learn.microsoft.com/en-us/microsoft-365/admin/add-users/remove-former-employee-step-5));
the `https://<tenant>-my.sharepoint.com/personal/<user>_<domain>_com` shape is **not** stated on that page —
it comes from `onedrive-mechanisms.md` §C, and see the URL-derivation failure mode below. It does
**not** appear inside their own OneDrive. It does not appear in "Shared with me". If they lose the link they
have nothing. Nobody emails them, because the app cannot send mail and this grant does not trigger any
Microsoft notification (the Microsoft emails are triggered by *Entra deletion*, not by an admin grant — see
A2). **This is the sharpest single gap against Google in the entire document** and it is a UX gap, not a
capability gap: the access is real and complete, it is just not discoverable.

Partial mitigations, in order of cost:
- Put the URL in the operation log and in the confirm dialog so the admin can paste it into a ticket. Free.
- Additionally `POST /drives/{driveId}/root/invite` with `roles: ["write"]` to the manager, which does surface
  it under "Shared with me" — but this requires the Graph file scopes and the Fact-2 bootstrap, so it drags in
  most of A3's cost for a cosmetic win. `UNVERIFIED:` whether invite against a drive root behaves as expected.
- The `remoteItem` shortcut (`onedrive-mechanisms.md` mechanism Q) would make it appear *inside* the manager's
  OneDrive, which is the visible half of what Google does. It is `UNVERIFIED` as a supported v1.0 write and
  should not be planned around until tested.

**Credentials and roles.**
- App: **one new delegated SharePoint permission** (`AllSites.FullControl` or narrower). No secret, no cert.
- **No new Graph scope.** The SPO token goes through the existing `GetAccessTokenAsync(string[] scopes)`
  escape hatch exactly as the EXO token does, so `GraphService.Scopes` is untouched and the Graph re-consent
  question never arises. This is a real and underrated advantage over A3/A4.
- Human: SharePoint Administrator or Global Administrator.
- Machine: `PnP.PowerShell` v3 becomes a documented prerequisite alongside pwsh 7 and
  `ExchangeOnlineManagement`, and the README's "PowerShell 7" line tightens to **7.4.6+**.

**Build effort against this codebase.** Medium, and almost all of it is channel plumbing, not feature logic.
- The one genuinely new thing: a **second out-of-process PowerShell channel**. `ExchangeService` is hard-wired
  to one module — `Import-Module ExchangeOnlineManagement`, `Connect-ExchangeOnline -AccessToken`,
  `Disconnect-ExchangeOnline` on QUIT. A PnP channel needs either a near-copy of ~350 lines of process/framing
  plumbing or a refactor of that plumbing into a shared base with a per-module preamble. **The refactor is the
  right call** and it is the bulk of the work.
- The step itself is the standard 8-file checklist from `app-architecture.md` §3.5: `Models/Scenario.cs`
  (enum member + a `OneDriveGranteeRef` shaped like the existing `ForwardingTargetRef`),
  `ScenarioRunner.cs` (case + guard + resolver + detail string), `Views/Converters.cs`,
  `ScenarioEditorViewModel.cs`, `ScenarioEditorWindow.xaml`, `MainViewModel.DescribeStep` + caution line,
  `IDialogService`/`DialogService` picker, README/Wiki.
- **Size anchor, corrected.** An earlier draft of this document compared the step to "the 2.3.0 Exchange
  phase-1 commit (10 files, +461/−41)". Those numbers are real but they belong to commit `7b1fdd0`, which
  built the **nav section with mailbox + DL lists** — not a scenario step. The correct comparator, verified
  with `git show --stat`, is commit `4bb3750` *"Add Exchange Online scenario actions (v2.0 Phase 2)"*:
  **8 files, +170/−27, and it added five scenario actions at once** (`ExchangeConvertToShared`,
  `ConvertToRegular`, `SetForwarding`, `ClearForwarding`, `DelegateToManager`). It touched
  `App.xaml.cs`, `Models/Scenario.cs`, `Services/ExchangeService.cs`, `Services/ScenarioRunner.cs`,
  `ViewModels/MainViewModel.cs`, `ViewModels/ScenarioEditorViewModel.cs`, `Views/Converters.cs` and the plan
  doc — note that it did **not** touch `ScenarioEditorWindow.xaml` or `IDialogService`/`DialogService`,
  because no picker shipped in that commit. So a single OneDrive step *with* a grantee picker is somewhat
  more than one-fifth of `4bb3750`, and materially less than the ~460 lines the old anchor implied. **The
  step is small. The channel is the cost.** Call it 3–4 commits, dominated by the channel.
- **It fits the runner's contract natively — but not on the 90-second budget, and that number needs
  correcting.** `OpTimeout = 90 s` is a `private static readonly` field of **`ExchangeService`**
  (`ExchangeService.cs:30`), a class a PnP step would never call. A new channel sets its own budgets, and
  three costs the EXO precedent makes visible have to be budgeted for:
  - **Cold connect.** The first OneDrive step in a run pays `pwsh` process start + `Import-Module` +
    `Connect-…`. `ExchangeService` budgets that at `StartupTimeout = 60 s` plus `ConnectTimeout = 150 s`
    (`:29`, `:31`) — up to **210 s before the first op runs**. `PnP.PowerShell` v3 import is not fast, and
    nothing about it is faster than the EXO module. Budget it as a connect, not as an op.
  - **Token lifetime.** PnP's own `-AccessToken` documentation: *"if the token expires (typically after 1
    hour) will not be able to acquire a new valid token, which the other connection methods do allow. You are
    responsible for providing your own valid access token when using this parameter, for the correct
    audience, with the correct permissions scopes."*
    (https://pnp.github.io/powershell/cmdlets/Connect-PnPOnline.html). `ExchangeService` mints once at
    connect and relies on a **string heuristic** over the error text (`IsSessionExpired`, `:1075-1084`) to
    decide to reconnect once. A bulk termination run longer than an hour will hit this; a PnP channel needs a
    real expiry check, not the heuristic.
  - **Timeout blast radius.** On the EXO channel a timeout calls `KillLocked()` and takes the session down
    for every other Exchange feature (`ReadLineLockedAsync`, `:1046-1063`). If the plumbing is refactored
    into a shared base — which is the recommendation below — the design must keep **one host process per
    module**, or a slow PnP call will kill the Exchange session mid-scenario.

  With those three stated, the step itself is genuinely one bounded call returning a one-line detail, and no
  new architecture concept. Nothing else in this document can say that.

**What it silently loses.** Almost nothing about the data. What it loses is *knowledge*:
- The manager is never told. Silent by construction, given the app cannot mail.
- It does not stop any clock. The OneDrive still dies on schedule (A2's timeline) unless something else
  intervenes. **An admin who runs this step will reasonably believe the files are safe. They are not — they
  are accessible.** The confirm-dialog caution must say so with a number of days in it.
- If the scenario also removes the licence, the unlicensed clock starts: read-only at 60 days, archived at 93
  (https://learn.microsoft.com/en-us/sharepoint/unlicensed-onedrive-accounts). The manager's shiny new access
  becomes read-only two months later with no warning in any admin surface the app shows.

**How it fails.**
- **403 / no permission** if the human is not SharePoint Admin, or was granted the role under an hour ago.
- **Site not found** if the user never provisioned a OneDrive (never signed in to it). This is common and must
  be a distinguishable outcome, not a crash — the runner has no `Skipped` state, so it becomes a success whose
  detail says "no OneDrive provisioned", following the house pattern at `ScenarioRunner.cs:236`
  (`"Exchange: set forwarding (no target configured)"`) and `:257`/`:259`
  (`"Exchange: delegate to manager (no manager set — skipped)"`). *(An earlier draft cited `:245`; that line
  is inside the forwarding **detail** string, not the no-op arm. Line numbers re-checked against the working
  tree at `7423bcb`.)*
- **Ordering.** The runner executes steps in the order listed and has no dependency model
  (`ScenarioRunner.cs:42`). Placed after a licence-removal or a `MoveToOu` that soft-deletes the cloud user,
  this step acts on a resource being torn down. The only available mitigation today is the confirm-dialog
  caution block.
- **Token expiry mid-session**: PnP "will not be able to acquire a new valid token" from a supplied
  `-AccessToken`, so the channel must re-mint per operation the way `ExchangeService.EnsureConnectedAsync`
  does (`ExchangeService.cs:909-930`).
- **The URL is derived wrong.** Deriving `personal/<upn>_<domain>_com` by string-mangling the UPN is fragile
  (renames, apostrophes, non-ASCII). The robust source is Graph `/users/{id}/drive` `webUrl` — which needs
  the Fact-2 grant first, so there is a chicken-and-egg ordering here: grant to the manager, *then* read the
  URL to log it, or read it via the SPO channel with `Get-PnPTenantSite`.

---

## A2. Configure and report: rely on the built-in retention window plus automatic access delegation

The app acts on nothing. It verifies preconditions, states the timeline, records what it found, and hands off
to Microsoft's own behaviour — including Microsoft's own Google-parity feature.

**What the admin does.** Runs the terminate scenario. A "OneDrive: report" step (or a panel in the detail
pane) tells them: whether the user has a OneDrive and its URL; whether the **Entra** manager attribute is
populated and who it resolves to; the tenant retention period; and the deletion timeline in dates, not
concepts. The admin then does the actual preservation decision outside the app, or does nothing deliberately.

**What happens to the data.** It follows the documented lifecycle, verbatim from
https://learn.microsoft.com/en-us/sharepoint/retention-and-deletion:

> "The OneDrive retention period for cleanup of OneDrive begins when a user account is deleted from Microsoft
> Entra ID. **No other action causes the cleanup process to occur, including blocking the user from signing in
> or removing the user's license.**"

Then: deletion syncs to SharePoint → the Clean Up Job marks the OneDrive → the manager (or a configured
secondary owner) is emailed that they have access and that it will be deleted at the end of the retention
period → a reminder seven days before expiry → the OneDrive moves to the site collection recycle bin, "where
it's kept for 93 days. During this time, users will no longer be able to access any shared content."

Default retention is 30 days; the setting accepts "a value from 30 through 3650"
(https://learn.microsoft.com/en-us/sharepoint/set-retention).

**But lengthening it does not buy proportionally longer usable life, and this is the trap in A2.** The same
retention-and-deletion page carries a note that flatly caps the value of a long retention period:

> "Unlicensed OneDrive accounts get automatically archived on their 93rd unlicensed day, **even if the account
> is retained for longer than 93 days due to the retention period**. When a user account is deleted from
> Microsoft Entra ID, the OneDrive account is considered unlicensed."

So `Set-SPOTenant -OrphanedPersonalSitesRetentionPeriod 365` does **not** give the manager 365 usable days. It
gives roughly 60 days of read-write, 33 more of read-only, and then an archived site needing a paid
reactivation — with the retention period only governing when the site is finally recycled. Any report the app
prints must show the archive date, not just the retention date, or it will tell the operator a comfortable
number that is wrong by a factor of four.

**What the receiving person sees.** The best experience of any approach here, and it is not ours — it is
Microsoft's. They get a real email from Microsoft with a link, a second reminder before expiry, and (where the
feature has rolled out) the **Simplified file transfer** experience: filter to shared or important files,
select, pick a destination, and — verbatim —

> "It's a full move. The files are relocated from the original OneDrive to the new destination, and their
> location is updated accordingly."

with "Keep sharing with the same people" retaining collaborator access.
https://support.microsoft.com/en-us/onedrive/simplified-file-transfer-for-departing-employees

That is a genuine move that **preserves sharing**, which no API-driven approach in this document can do. It is
the closest thing in Microsoft 365 to Google's transfer. It is driven by the manager, in a browser, and the
support article documents **no admin UI, no cmdlet, and no Graph endpoint**. The app cannot invoke it. It can
only make sure the conditions for it exist.

**Credentials and roles.** Cheapest of all, and it splits in two:
- **A2-lite** (report only): resolve the Entra manager and, if readable, the drive URL. The manager read is
  covered by the existing `User.ReadWrite.All` — **zero new permissions, zero new modules, zero new channels.**
- **A2-full** (also read/set the tenant retention period): needs the PnP channel from A1, and therefore all of
  A1's permission and prerequisite cost.

**Build effort.** A2-lite is roughly one commit: a Graph read, a report step or a read-only panel, and careful
prose. A2-full inherits A1's channel.

**What it silently loses — and this is the part that disqualifies A2 as a standalone answer.**

1. **The app cannot verify the single most important precondition.** "Enable access delegation" and the
   secondary owner live on the classic User Profile Service page and there is **no supported API to read or
   set them** (`onedrive-mechanisms.md` mechanism A, `UNVERIFIED` but researched twice). So a report that says
   "the manager will get access" is *asserting tenant configuration it cannot see*. It must say "if access
   delegation is enabled — verify at SharePoint admin center → More features → User profiles → …" and that
   caveat will be ignored by every operator who reads it more than twice.
2. **A terminate scenario that disables rather than deletes fires none of this.** The seeded Terminate User
   scenario (`ScenarioStore.SeedDefaults:90-114`) is `Disable` → `RemoveAllGroups` → `ClearAttribute(manager)`
   → `MoveToOu` → `SetDescription`. Nothing deletes the Entra account. Under the quoted rule, **no cleanup
   runs, no manager email is sent, and the OneDrive sits there indefinitely** — accumulating unlicensed days if
   the licence was pulled. A2 is a plan for a deletion the app does not perform.
3. **`ClearAttribute(manager)` is in the seeded scenario.** If an equivalent clears the *Entra* manager before
   deletion, automatic access delegation has nobody to delegate to. The app would be actively destroying the
   precondition it is relying on. Worth checking against the real scenario before this is offered.
4. **The app's existing manager lookup reads on-prem AD, not Entra.** `ScenarioRunner.cs:256` resolves the
   manager via `CorrelationAsync(dn, "manager")` against AD. Automatic access delegation depends on the manager
   relationship **in Entra ID**. In a hybrid tenant those can differ if the attribute is not syncing. A
   precondition check must read Entra explicitly; reusing the existing AD lookup would produce a confident,
   wrong answer.
5. **Private files.** Simplified file transfer's UI filters to "shared or important files". Whether the manager
   remembers to widen that is out of anyone's control. This reproduces Google's worst default
   (`PRIVACY_LEVEL` defaulting to `SHARED`) rather than fixing it.
6. **Lists and Forms are excluded** from Simplified file transfer, per its own page.

**How it fails.** Quietly and completely, and usually months later. Every failure mode is "nobody got an email
and nobody noticed": delegation disabled tenant-wide, no Entra manager and no secondary owner (documented
outcome: "no one has automatic access when the user is deleted or be warned that the OneDrive will be
deleted"), the account disabled instead of deleted, or the manager receiving the mail and ignoring it until
day 31. Also: Purview retention settings "always take precedence" over this process and can delete content
*before* 30 days, so even the timeline the app reports may be wrong in a tenant the app cannot inspect.

---

## A3. Copy the content into the manager's OneDrive

The literal Google-shaped answer: put the leaver's files in the receiver's own storage, in one folder.

**What the admin does.** A step configured with a destination person. On execution: grant self site collection
admin on the source (A1's primitive, aimed at the operator instead of the manager), enumerate, create a
destination folder in the manager's drive, `POST /drives/{driveId}/items/{itemId}/copy` with
`childrenOnly: true`, log the monitor URL, return. **Not** wait for it.

**What happens to the data.** An entirely new set of items is created in the destination. Verbatim from
[driveitem-copy](https://learn.microsoft.com/en-us/graph/api/driveitem-copy?view=graph-rest-1.0):

> "Metadata isn't retained when a driveItem is copied, including system metadata and custom metadata. An
> entirely new driveItem is created in the target location instead."
>
> "**Permissions are not retained when a driveItem is copied. The copied driveItem inherits the permissions of
> the destination folder.**"
>
> "File versions are only retained when the includeAllVersionHistory parameter is explicitly set to true.
> Otherwise, only the latest version is copied."

Note also that Graph **cannot move** across drives — "Items cannot be moved between Drives using this request"
([driveitem-move](https://learn.microsoft.com/en-us/graph/api/driveitem-move?view=graph-rest-1.0)) — so a
"move" is copy-then-delete, and the delete half is only reversible via a recycle bin.

**What the receiving person sees.** The best-looking result of any approach: a folder in their own OneDrive
with the leaver's files in it, exactly like Google's transfer folder. Which is precisely why this approach is
dangerous — **it looks the most like Google and behaves the least like it.** Every collaborator who had access
to those files through the leaver's shares loses it, silently, at the moment of copy. Google's documentation
says the opposite of this about its own transfer: "Transferring files does not affect who has access to the
files."

**Credentials and roles.** The most expensive set.
- A1's entire cost (SPO permission, PnP channel, SharePoint Admin), because of Fact 2. Plus:
- **New delegated Graph scopes** — `Files.ReadWrite.All` and/or `Sites.ReadWrite.All` — which must go into the
  static `Scopes` array at `GraphService.cs:19-27` because that array is baked into `GraphServiceClient` at
  `Configure` time and is what `AuthenticateAsync` requests. Whether that forces existing admins to re-consent
  is `UNVERIFIED` (`app-architecture.md` §1.4); admin consent on the registration should prevent a prompt, and
  the live warm-cache test is cheap.
- No secret, no certificate. The app-only shortcut that would skip the Fact-2 bootstrap is available and must
  be refused.

**Build effort. This is the expensive one, and the expense is architectural rather than mechanical.**
- Everything in A1, plus Graph enumeration, plus copy orchestration, plus monitor polling.
- **The app has no home for a long-running job.** `app-architecture.md` §4.3 is unambiguous: no background job
  store, no queue, no task persistence, no `202 Accepted` / `Location` monitor pattern anywhere in the repo,
  no progress percentage, and no way for an operation to outlive the modal that started it. The longest
  deliberate wait in the entire codebase is ~2 minutes of polling inside a modal.
- `ScenarioRunner` is synchronous per target (`:41-70`); a step that blocks for an hour blocks every remaining
  target in the run.
- **Therefore the step must be fire-and-record**: start the copy, write the monitor URL and destination into
  the operation log, return immediately. That is the only shape that does not require rearchitecting the
  runner — and it changes what the log can honestly claim from "files transferred" to "copy started". For a
  termination audit trail that distinction matters.
- Realistically 6+ commits and the first genuinely new architectural concept in the app.

**What it silently loses.** More than any other approach, and the losses are the kind that look like success:
- **All sharing permissions.** Restated because it is the single most dangerous line in the research.
- **All metadata**, system and custom.
- **Version history**, unless `includeAllVersionHistory: true` — and there is a documented known issue that
  the parameter "is ignored if the name request parameter is also passed", with the workaround being copy
  first, rename after. Get that wrong and you have silently kept only the latest version of everything.
- **Old sharing URLs**, which encode the source site and will not resolve.
- **The truth of the log entry**, unless the monitor is read to a terminal state. A copy returns 202 up front
  and can then report `{"status":"failed","error":{"code":"nameAlreadyExists"}}` on the monitor. **Code that
  checks only the POST result reports success on a failed copy.**
- **The manager's storage quota**, consumed without warning. Google has the same property (quota follows
  ownership) and warns about it; we would need to build the pre-flight size check ourselves.

**How it fails.** Loudly at the edges and quietly in the middle:
- **Hard ceilings**: "No more than 100 GB total file size", "No more than 30,000 files" for cross-site
  move/copy; `driveitem-copy` restates "restricted to 30,000 driveItems"; OneNote 2 GB; cross-geo 15 GB
  (https://learn.microsoft.com/en-us/office365/servicedescriptions/sharepoint-online-service-description/sharepoint-online-limits).
  A real executive's OneDrive can exceed these. The design must state what happens when it does, and the
  honest answer is "the copy fails and the admin does it by hand".
- **Throttling**: 100 GB/hour per-user egress, 3,000 requests per 5 minutes per user, permission reads at 5
  resource units each. Microsoft names this exact workload as a throttling cause and *requires* honouring
  `Retry-After`, noting SharePoint "does not return or support IETF RateLimit headers". Escalation is a block
  "at the app or user level" that "prevents the offending process from running".
- **Path length**: nesting the source tree under a `Files from <name>/` folder lengthens every path, against a
  documented 400-character limit on the whole decoded path. Files that were legal in the source become illegal
  in the destination.
- **The monitor URL is not on the Graph endpoint** and needs no auth; code that assumes a `graph.microsoft.com`
  base breaks. Its lifetime is undocumented (`UNVERIFIED`), so a lost monitor must be treated as "unknown,
  verify by enumerating the destination", never as "failed".

---

## A4. Copy the content into a SharePoint site instead

Mechanically A3 with a different destination. Strategically a different proposition, and a better one.

**What the admin does.** Same step, but the destination is a document library in a SharePoint site — a
departures archive, or the team's own site — rather than a person.

**What happens to the data.** Identical to A3 and with identical losses. Same Graph copy, same permission
destruction, same limits.

**What the receiving person sees.** A folder in a site they navigate to, not something in their own OneDrive.
Worse than A3 on Google behaviour #2. But the destination's permissions are a *team's* permissions, so
whoever needs the files can be given access without another offboarding, and access survives the manager
themselves leaving.

**Why it is nonetheless the better copy target.** Microsoft's own retention documentation makes the argument,
verbatim:

> "When a user leaves your organization, any content created by that user isn't affected because SharePoint is
> considered a collaborative environment, unlike a user's mailbox or OneDrive account."

https://learn.microsoft.com/en-us/purview/retention-policies-sharepoint

A3 solves the leaver's-personal-storage problem by moving the content into **another person's personal
storage**, which is the same problem with a later date on it. When the manager leaves, someone runs this
scenario again on a OneDrive now containing two people's files. A4 exits the cycle. It is also the structural
lesson from Google: an organisation that has already moved its work into shared drives has almost nothing to
transfer at offboarding (`google-drive.md` §1). SharePoint is the M365 shared drive.

**Credentials and roles.** Same as A3, with one simplification: the destination is a site the acting admin can
likely already reach or be granted on once, permanently, rather than a fresh per-termination grant into a
colleague's personal site.

**Build effort.** Same as A3 minus a little: destination picking is simpler and more reusable (one configured
archive site vs a person picked per run), and quota is a site quota rather than a surprise on a colleague.

**What it silently loses.** Everything A3 loses, plus discoverability: nobody gets even a URL in their own
storage unless told. Gains back: durability, and a permission model somebody actually administers.

**How it fails.** Same ceilings, same throttling, same path length. One extra: the destination library has its
own list-view and item limits and will accumulate every termination, so the archive site needs a lifecycle of
its own or it becomes the next problem.

---

## A5. Apply a retention policy or hold so the content survives account deletion

The compliance answer, and — this is the part that must not be soft-pedalled — **not a substitute for any of
the above.**

**What the admin does.** Either configures nothing in the app and relies on a tenant-wide Purview policy that
already exists, or (if built) the app adds the departing user's OneDrive URL to a retention policy's included
locations at termination time.

**What happens to the data.** Verbatim from
https://learn.microsoft.com/en-us/purview/retention-policies-sharepoint:

> "**OneDrive:** If a user leaves your organization, any files that are subject to a retention policy or has a
> retention label will remain subject to the retention settings for the duration of the retention period
> specified in the policy or label. During that time, all sharing access continues to work and the content
> continues to be discoverable by Content Search and eDiscovery."
>
> "When the retention period expires and the retention settings included a delete action, content moves into
> the Site Collection Recycle Bin and isn't accessible to anyone except the admin."

Edits and deletes under retention are copied into a hidden **Preservation Hold library**, where "It's not
supported to edit, delete, or move these automatically retained files yourself." Cleanup runs on a timer, so
"it can take up to 37 days for content to be deleted from the Preservation Hold library."

**What the receiving person sees.** Nothing. There is no receiving person. Content is reachable through
eDiscovery export, by someone with eDiscovery privileges, as an export — not as working files. Retained
content preserves the documents, not a usable filing system anyone can browse.

**Credentials and roles.** Better than the briefs assumed. `Connect-IPPSSession` **does** take `-AccessToken`
(module 3.8.0-Preview1+), and it lives in `ExchangeOnlineManagement` — **the module the app already hosts**.
So Security & Compliance PowerShell is potentially reachable over the *existing* pwsh channel with a different
token audience, rather than needing a third module. No secret, no certificate. Two caveats:
`UNVERIFIED:` the correct token audience for Security & Compliance PowerShell (test: mint candidates, decode
`aud`/`scp` the way `ExchangeService.LogTokenClaims:1236-1250` already does for EXO, and try the connect); and
the existing host is hard-wired to one `Connect-ExchangeOnline` per session, so holding an EXO and an IPPS
session in one runspace is a real design question, not a free win. Also requires an appropriate Purview
entitlement — `UNVERIFIED` which SKU.

**Build effort.** Deceptively low on the connection and high on the judgement. The connect is cheap. Writing
policy configuration into a production tenant from a desktop app is not a small decision, and Microsoft's own
warning about the neighbouring Google product applies in spirit here too: an improperly configured retention
rule destroys data irreversibly. **Recommendation: the app should never author retention policy.** At most it
should *report* whether the OneDrive is covered, and even that is a read the app cannot fully do today.

**What it silently loses.**
- **Usability.** The content survives and is simultaneously unusable as work product. Retention answers "can
  the business answer a lawyer later", not "can the business keep working". Google ships both and does not
  pretend either substitutes for the other; neither should we.
- **Per-user scoping burns a limited resource.** Adding a URL per termination is technically possible and
  unusual, and it counts against a 2,600-location limit (13 if all sites are auto-included), which itself sits
  under a 10,000-per-tenant compliance cap shared with DLP, information barriers and sensitivity labels.

**How it fails — and this is the disqualifying finding.** Retention does **not** guarantee survival. Verbatim
from https://learn.microsoft.com/en-us/sharepoint/unlicensed-onedrive-accounts, FAQ 7:

> "**If billing is not enabled for unlicensed OneDrive accounts, and accounts are still retained 365 days after
> becoming unlicensed, then the unlicensed accounts will be subject to deletion 365 days after being
> unlicensed even if retention policies, settings, or holds exist on the OneDrive account.**"

Reinforced on the retention-and-deletion page: "After 12 month of Unpaid storage/archive the OneDrive Data
might be deleted regardless of Retention settings, retention policies, eDiscovery, and all holds."

**Three corrections to how that clock was described in an earlier draft, all re-fetched from the page itself
on 2026-09-01 (`ms.date` 2026-08-26).**

**(a) The day-365 clock is a *cumulative nonpayment* clock, not an unlicensed-days clock, and it started on
1 July 2026.** Verbatim:

> "As of July 1, 2026, unlicensed OneDrive accounts that remain unpaid are subject to a **cumulative 365-day
> nonpayment clock**. Accounts that were already unlicensed before July 1, 2026 are in scope, and accounts
> that become unlicensed on or after July 1, 2026 are also in scope. For accounts already unlicensed and
> unpaid on July 1, 2026, deletion risk begins **no earlier than July 1, 2027**."

And FAQ 4's table is explicit that the count **pauses** while the account is paid: *"Billing is enabled for 60
days | Count pauses while the account is paid"*. So "day 365" is not a date you can compute from the
termination date alone — it is the running total of unpaid days, floored at 1 July 2027 for the tenant's
existing backlog. Any date the app prints for this milestone would be a guess, and it should print the
**day-60 and day-93 dates** (which *are* computable from the unlicensed date) and describe day 365 in prose.
*(Also: "These changes don't apply to EDU, GCC, or DoD customers.")*

**(b) The read-only and archived states are not as absolute as the truncated quote suggests.** The full
sentence is:

> "Unlicensed OneDrive accounts are automatically put into read-only mode after 60 unlicensed days and
> archived after 93 unlicensed days. While read-only and archived accounts remain visible to admins through
> administrative tools, neither admins nor end users have access to their content **unless an admin takes a
> supported action such as assigning a license, enabling applicable billing, reactivating archived content, or
> moving content that must be retained.**"

The page then lists, for a read-only account specifically, two admin options: *"Add a license"* and *"Use
PowerShell to unlock the site"* (linking to `manage-lock-status`, i.e. `Set-SPOSite -LockState Unlock`, which
that cmdlet's reference confirms is valid on a OneDrive site collection). So day 60 is a wall for an
*unassisted* grantee, not for an admin holding the SharePoint role. Whether the enforcement job simply
re-applies read-only after an admin unlocks is not stated — `UNVERIFIED`, and until it is settled the app
should still present day 60 as the deadline it tells the operator about.

**(c) The pricing worked example was misread.** The $614.40 is the reactivation of **one** 1 TB account, not
of 100 accounts. Verbatim:

> "If the billing is enabled to reactivate one particular unlicensed account, the reactivation fee is applied
> for $0.60/GB for that account, and from that month onward, the storage fee of $0.05/GB/Month is applicable
> **for all unlicensed accounts within the organization for longer than 93 days**."
>
> "For example, if an organization has 100 unlicensed OneDrive accounts, each consuming 1 TB for a total of
> 100 TB […] If the organization needs to reactivate a specific account in December 2025 and set up billing,
> they incur the following costs: A one-time reactivation fee of $0.60/GB **for 1TB, totaling $614.40**. A
> monthly storage fee of $0.05/GB **for 100TB, amounting to $5,120/month** starting from December 2025."

The structural point survives and is if anything sharper: the **reactivation fee is per account you actually
reactivate** ($614.40 per terabyte), while the **monthly meter is tenant-wide** the moment you switch billing
on. FAQ 7's note adds that a OneDrive held by a retention policy or litigation hold *still* incurs the monthly
archive charge once billing is on. So the first bill is your whole existing backlog whether or not you ever
open any of it.

Also worth knowing before this is priced: from **calendar year 2026, the reactivation fee is waived when
Standard storage is enabled**
(https://learn.microsoft.com/en-us/sharepoint/standard-storage-unlicensed-onedrive-accounts).

So "just leave it under retention" buys, in an unpaid tenant, roughly **60 days of unassisted access and — at
the earliest — a year of existence**, with the exact deletion date depending on cumulative unpaid days rather
than on the termination date. That is a budget conversation with whoever owns the Microsoft 365 bill, not an
assumption the app should encode. Google's Vault is account-coupled in the same way, which is why Google sells
the Archived User licence — but Google's holds are not overridden by nonpayment in the way Microsoft documents
here.

**And one thing that must not be lost in the pricing argument: the recycle bin is a compliance dead end.** The
retention-and-deletion page, on the step where the OneDrive lands in the site collection recycle bin:

> "The Recycle Bin isn't indexed and therefore searches don't find content there. This means that **an
> eDiscovery hold can't locate any content in the Recycle Bin in order to hold it.**"

That is the sharpest single sentence against treating A5 as a safety net. Once the site is recycled — 93 days
of nominal survival — eDiscovery cannot see it, so the 93 days are storage, not discoverability. The
counterweight, from the same page, is that in the *normal* (non-nonpayment) path a hold really does block the
deletion: *"if a OneDrive is put on hold as part of an eDiscovery case, managers and secondary owners are sent
email about the pending OneDrive deletion, but the OneDrive won't be deleted until the hold is removed."*
A5 works when somebody is paying and a hold is in force. It fails silently when nobody is.

---

## A6. Hybrid: act now on access, record what was done, make it undoable

Not a fifth mechanism. A **discipline applied to A1**, with A2's checks folded in as a report and A3/A4 held
behind a spike. This is the recommendation, so it is specified in more detail than the others.

**What the admin does.** One scenario step. On execution it:

1. **Checks preconditions and reports them** (A2-lite): does a OneDrive exist and what is its URL; is the
   **Entra** manager set and to whom; is the grantee resolvable. Each has three distinguishable outcomes —
   resolved, not found, not looked up — per the house rule at `docs/exchange-online-nav-and-cloud-groups-plan.md`
   G13. Never two.
2. **Grants** the manager (or a picked person) secondary site collection admin via
   `Set-PnPTenantSite -Owners` (A1). Never `-PrimarySiteCollectionAdmin`. Never `Set-SPOSite -Owner`.
3. **Records a re-addable, re-removable record** in the operation log: the OneDrive URL, the grantee's UPN,
   the timestamp, and the exact revoke instruction. This follows the established house pattern — removals are
   recorded as name **and** DN on-prem (`ScenarioRunner.cs:124-126`), name **and** object id in Entra
   (`:321-322`), name **and** SMTP plus channel and kind for Exchange DLs (`:310-314`) — where the stated
   intent is that the log is what lets an admin put access back. Here it is what lets an admin **take access
   away** when the handover is done, which is the same idea pointed the other way.

   > **⚠ This step of A6 does not work today, and the gap is in the app, not the plan.** The operation log is
   > **opt-in per scenario and off by default.** `MainViewModel.RunScenarioAsync` computes
   > `var wantsLog = scenario.Steps.Any(s => s.Action == ScenarioActionType.SaveOperationLog);` (`:1042`) and
   > passes `operationLog` to the runner **only when that is true** (`var operationLog = logPath is not null ?
   > new List<string>() : null;`, `:1113`; the Save-As itself is gated at `:1098-1100`). Without a
   > `SaveOperationLog` step there is no Save-As prompt, no
   > list, and no file — the runner's `operationLog?.Add(…)` calls all no-op.
   > **And the seeded "Terminate User" scenario does not contain a `SaveOperationLog` step**
   > (`ScenarioStore.SeedDefaults`, `:105-111`: `Disable` → `RemoveAllGroups` → `ClearAttribute(manager)` →
   > `MoveToOu` → `SetDescription`). So on a stock install, A6's record is written to nothing at all.
   >
   > Three consequences the plan must own: **(i)** the seeded Terminate User scenario has to gain a
   > `SaveOperationLog` step as part of this work; **(ii)** the OneDrive step's confirm-dialog caution must
   > follow the existing `wantsLog` pattern (`:1044-1051` already writes a different caution line depending on
   > whether a log will be kept — see `:1043-1051`) and say plainly that **without a log step, the granted URL
   > is recorded nowhere**; **(iii)** `DescribeStep` has no `SaveOperationLog` arm at all (`MainViewModel.cs:1163-1190`),
   > so that step currently renders in the confirm dialog as the raw enum name — worth fixing alongside the
   > `default:` arms in §A6's closing paragraph.
4. **States the deadline in the log, in days**: what happens if the account is deleted (retention clock) and
   what happens if the licence is pulled (60-day read-only, 93-day archive). A grant with no expiry date
   attached is how a temporary access grant becomes permanent.

Then, separately and later, an admin can act on that record: revoke the grant, or run a copy into a SharePoint
archive if the tenant decides it wants A4.

**What happens to the data.** Nothing, same as A1. That is the point. The reversible option is taken first,
the irreversible one is deferred to a decision made with information.

**What the receiving person sees.** A1's answer — a URL, and no email — plus a log entry the admin can paste
into a ticket. Honest, and worse than Google. See §3.

**Credentials and roles.** A1's exactly: one new delegated SharePoint permission, no new Graph scope, no
secret, no certificate, SharePoint Admin on the human, PnP.PowerShell as a new prerequisite.

**Build effort.** A1 plus a modest amount of report-and-record logic that reuses machinery the app already has
(the operation log, the caution block, the picker pattern). The extra beyond A1 is small; the discipline is
mostly in prose and test coverage rather than in new subsystems.

**What it silently loses.** The same two things A1 loses — nobody tells the manager, and no clock stops — but
it loses them *visibly*, which is the entire argument for it. The log says what was granted, to whom, and by
when it will stop mattering.

**How it fails.** A1's failure modes, plus two of its own.

1. **The follow-up never happens.** A record nobody reads is not a control.
2. **The record is never written**, per the boxed warning in step 3 above. The mitigation — keep the record in
   the operation log the admin is already prompted to save (`MainViewModel.RunScenarioAsync:1095-1106`, which
   offers a Save-As and re-confirms if declined) — is only available **if the scenario contains a
   `SaveOperationLog` step**, which the seeded Terminate User scenario does not. Fixing the seed is part of
   this work, not a nicety. Inventing a new persisted store is still the wrong answer; the app has no pattern
   for one (`app-architecture.md` §4.3).

**One structural limit of the runner that neither this document nor `transfer-vs-retain.md` originally
stated, and that bounds every scenario-step approach here:** `ScenarioRunner.RunAsync` takes
`IReadOnlyList<AdObjectRow>` (`ScenarioRunner.cs:22-27`) — **scenario targets are on-prem AD objects**. Every
cloud and Exchange step resolves its cloud twin by reading `userPrincipalName` off the on-prem object with
`CorrelationAsync(dn, "userPrincipalName", …)` (`ResolveMailboxIdentityAsync` `:384-393`,
`ResolveCloudUserIdAsync` `:416-424`, `CorrelationAsync` `:450-457`), and the code comment on
`ResolveMailboxIdentityAsync` states the assumption outright: the on-prem UPN *"equals the cloud mailbox's
UPN/primary SMTP for synced users"*. Two consequences for a OneDrive step:

- **A cloud-only leaver cannot be terminated by a scenario at all.** There is no AD object to select, so the
  step is unreachable for exactly the population most likely to be a contractor, a service account or a
  recent acquisition. The app's OneDrive answer for those users has to be the cloud pane, or nothing.
- **Alternate-login-ID / UPN-mismatch tenants resolve to the wrong person or to nothing.** The existing
  Exchange steps already carry this risk; a OneDrive grant carries it against a *file store*, where a
  wrong-target grant is a data-exposure incident rather than a mail annoyance. The step should resolve the
  Entra user and log the resolved cloud UPN and drive URL **before** granting, not after.

One codebase defect this work must close, in any approach: `ScenarioRunner.RunStepAsync` ends with
`default: detail = step.Action.ToString();` (`ScenarioRunner.cs:265-267`), and both
`ScenarioActionToLabelConverter` and `MainViewModel.DescribeStep` have equally permissive default arms. **An
enum member added without a runner case runs, does nothing, and is logged with a ✓ and the raw enum name.**
For a step whose job is to preserve a departing employee's files, that is unacceptable. Throw on `default`, or
add an exhaustiveness test, as part of this work.

---

## 2. Ranked against the Google behaviours

Rows are the ranked list from `google-drive.md` "What 'as close as possible' would have to mean", in its order.
● met · ◐ partial · ○ not met · — not applicable.

| # | Google behaviour | A1 grant | A2 report | A3 copy→OneDrive | A4 copy→SharePoint | A5 retention | A6 hybrid |
|---|---|---|---|---|---|---|---|
| 1 | One admin action in the offboarding step, no leaver cooperation | ● | ○ | ◐ starts only | ◐ starts only | ○ | ● |
| 2 | Receiver gets one contained folder **in their own storage** | ○ | ◐ if manager runs Simplified transfer | ● | ○ | ○ | ○ |
| 3 | Honest scoping: personal storage only, and say so | ● | ● | ● | ● | ● | ● |
| 4 | Covers private files, and defaults to covering them | ● | ◐ manager's choice | ● | ● | ● | ● |
| 5 | Asynchronous with a real, pollable status | — instant | ○ | ◐ monitor exists, app can't persist | ◐ same | ○ | — instant |
| 6 | Notify the parties | ○ no mail in app | ● Microsoft's own emails | ○ | ○ | ○ | ○ |
| 7 | Leaver's residual access as an explicit choice | ● deviates deliberately | — | ● | ● | — | ● |
| 8 | Storage accounting visible | — no bytes move | ○ | ○ must be built | ◐ site quota | ○ | — |
| 9 | Handles items the leaver owned in other people's areas | ● untouched | ● untouched | ○ duplicates them and strips permissions | ○ same | ● | ● |
| 10 | Retention as a **separate, non-equivalent** parallel answer | — | ◐ reports it | — | — | ● is the answer | ◐ reports it |

Reading the table honestly:

- **A1/A6 win every row that is about the leaver's data being safe and correct**, and lose the two rows that
  are about the receiver's experience (#2, #6).
- **A3 wins the one row everybody looks at (#2) and loses #9 catastrophically** — the row about not damaging
  collaboration. It is the approach most likely to be chosen on a demo and regretted in production.
- **A2 is the only approach that satisfies #6**, and it does so by not being our software.
- **No column is all ●.** No combination of columns is all ● either, because #2 and #9 are in direct conflict
  in Microsoft 365 in a way they are not in Google. That conflict is the subject of §3.

---

## 3. What cannot be reproduced, and why

The user asked for "as close as possible". This section is the honest answer about the gap, and it is the most
valuable part of this document.

### 3.1 Ownership transfer itself. Absolutely not, ever.

Google's transfer rewrites an attribute; the bytes never move (`google-drive.md` §5). Microsoft has no such
attribute to rewrite, and the closest field is documented as unsupported to change (Fact 1). This is not a
missing API that might arrive. It is a difference in what a personal file store *is*: Google models ownership
as a permission on a file, Microsoft models it as a site collection provisioned for a principal. **Every
consequence below follows from this one fact.**

### 3.2 The pick-two triangle

Google's admin transfer delivers three things at once. Microsoft 365 offers three mechanisms, each of which
delivers **two** of them and forfeits the third. There is no mechanism that delivers all three, and this is
the precise shape of the gap:

|  | Admin-driven, no leaver or manager cooperation | Lands in the receiver's own storage | Preserves existing sharing |
|---|---|---|---|
| **Site collection admin grant** (A1/A6) | ● | ○ | ● |
| **Graph copy** (A3/A4) | ● | ● | ○ |
| **Simplified file transfer** (A2) | ○ manager-driven, browser only | ● | ● |
| *Google admin transfer* | ● | ● | ● |

Read the last column as the price list. Wanting the Google-shaped visual result (middle column) costs either
every sharing permission on every file, or the admin's ability to do it without the manager. **That is the
gap, stated as precisely as it can be stated.** No amount of build effort moves a row.

### 3.3 The specific behaviours that cannot be reproduced

1. **"Transferring files does not affect who has access to the files."** Reproducible only by not moving the
   files (A1) or by the manager doing it by hand in a browser (A2). Any code we write that relocates content
   uses Graph copy, and "Permissions are not retained when a driveItem is copied."
2. **Instant, size-independent transfer.** Google rewrites metadata. Any Microsoft relocation is a physical
   copy subject to 100 GB / 30,000 items, 100 GB/hour egress, and a 400-character path limit. Google's own
   limit is a 36-hour job ceiling; ours is a hard refusal at a size a real user can reach.
3. **Notification to receiver, leaver and admin.** Google emails all three. Microsoft emails the manager, but
   only on **Entra deletion** with delegation configured, and never for an admin-initiated grant. The app
   cannot mail — there is no mail code in the repository at all. Reproducing #6 for A1/A6 means building an
   email capability, which is a new dependency, a new configuration surface and a new failure mode for a
   one-line courtesy. It is probably not worth it; putting the URL in the operation log and the ticket is.
4. **"The previous owner can still edit any transferred files."** Meaningless here. In Google the leaver is
   downgraded to `writer`; in a Microsoft termination the leaver is disabled or deleted before this runs.
   Deviating from Google here is correct and should be documented as deliberate (Google behaviour #7).
5. **The contained, obviously-labelled transfer folder in the receiver's storage.** Reproducible *only* by
   A3, at the cost in 3.2. Whether that trade is worth it is the user's decision, and it should be made with
   the permission destruction stated in one sentence, not in a footnote.
6. **Automating Microsoft's own best answer.** Simplified file transfer is the closest analogue to Google's
   feature and it is browser-only and manager-driven; its support page documents no admin UI, no cmdlet and
   no Graph endpoint. `UNVERIFIED` whether any private endpoint backs it — settle by capturing the network
   traffic of a transfer in the web UI. Until then, unautomatable.
7. **Indefinite survival under hold.** Google's Vault is account-coupled: delete the user and held data can be
   purged. Microsoft is *worse* on this axis, not better — nonpayment past 365 unlicensed days deletes content
   "even if retention policies, settings, or holds exist". Neither product gives "leave it forever" for free;
   Google sells the Archived User licence, Microsoft sells the unlicensed-storage meter. Any promise the app
   makes about durability must name which of those the tenant is paying for.

### 3.4 The one place we can beat Google

Google's `PRIVACY_LEVEL` defaults to `SHARED` only, silently leaving the leaver's genuinely private files
behind — the single most damaging default in Google's design (`google-drive.md` §2, #4). A site collection
admin grant covers **everything** in the site with no privacy filter and no option to get it wrong. If the app
ever offers a scope filter, it must default to everything or force an explicit choice.

### 3.5 The consideration neither this document nor `transfer-vs-retain.md` raised: **Teams chat files**

Any Microsoft 365 admin who has done a termination will ask this within a minute, and it was missing from both
decision documents. A OneDrive is not only the leaver's own filing cabinet. It is also **the backing store for
every file that person ever posted into a Teams 1:1 or group chat**, plus recordings and transcripts of
meetings they organised outside a channel. Purview's Teams retention page states the storage location plainly:

> "Emails and files that you use with Teams aren't included in retention policies for Teams. […] However, for
> files and Teams meeting recordings and transcripts **from user chats**, you need a retention policy that
> includes the **organizer's OneDrive account** as the location."
> https://learn.microsoft.com/en-us/purview/retention-policies-teams

(The client-visible folder name, `Microsoft Teams Chat Files` in the sharer's OneDrive, is documented only in
Microsoft Q&A community answers — treat the folder *name* as low-confidence, the storage *location* as
first-party. `UNVERIFIED:` settle by posting a file into a scratch 1:1 chat and looking at the sender's drive.)

The unlicensed-accounts page then closes the loop, FAQ 12: *"**What happens to external sharing links and
Teams file references once an unlicensed OneDrive is deleted?** Answer: Once a OneDrive is deleted, content
associated with that OneDrive can no longer be accessed."*

This reorders part of the comparison:

- **It makes A1/A6 better than the table gives them credit for.** Leaving the site in place and adding an
  administrator keeps every Teams chat file reference resolving for every counterparty, with no action by
  anyone. That is a real, invisible win.
- **It makes A3/A4 worse in a way not previously stated.** A Graph copy duplicates the bytes into a new
  location. The links embedded in months of Teams conversations still point at the **source** items in the
  leaver's OneDrive, which the copy does nothing to preserve. So a "successful" transfer leaves every chat
  reference in the organisation pointing at content on a decay clock — and when the copy is followed by a
  delete (which is what "move" means here, per `driveitem-move`'s cross-drive restriction), it breaks them
  immediately. **Row #9 of the §2 table ("items the leaver owned in other people's areas") should be read as
  covering this too, and it is a bigger population than most admins expect.**
- **It makes the blast radius of a termination organisation-wide, not manager-shaped.** The people harmed by
  the OneDrive expiring are not the manager — they are everyone the leaver ever sent a file to in a chat, and
  none of them are notified by anything, in any approach in this document.

**What the app should do about it:** nothing clever, one honest sentence. The confirm dialog and the operation
log should state that the OneDrive also backs the leaver's Teams chat file shares, and that those references
follow the OneDrive's fate — not the transfer's. `UNVERIFIED:` whether Graph can cheaply count items under
the chat-files folder to put a number on it; if it can, that number belongs in the log next to the drive size.

---

## 4. Recommendation

**Build A6. Ship A1's grant as its acting core, fold A2-lite's precondition checks in as a report, and defer
A4 behind the spikes. Do not build A3 as a default. Never build A0.**

The reasoning, in order of weight:

1. **It is the only approach that fits the architecture as built.** One bounded call, well under the 90-second
   op timeout, a one-line detail into the operation log, no long-running job, no new progress concept, no
   change to `ScenarioRunner`'s synchronous contract. `app-architecture.md` §4.3 states the conclusion
   independently: "leave the files in place and re-permission them" fits this architecture; "move the files
   somewhere else" does not.
2. **It is the only approach with zero data risk.** No bytes move, no permission is destroyed, no version
   history is dropped, no sharing link breaks. Everything it does is reversible with one cmdlet. For a feature
   that runs during a termination against a real person's only copy of things, reversibility is worth more
   than fidelity to a competitor's UX.
3. **It costs no new Graph scope**, so the re-consent question never arises. Only one delegated SharePoint
   permission, which is structurally what the app already does for Exchange. The public-client, no-secret
   posture survives intact and the README's security statement stays true.
4. **It preserves the option to do more later.** A1's grant is the mandatory first half of A3 and A4 anyway
   (Fact 2). Building it is not a detour from the copy approaches — it is their first commit. If the tenant
   later decides it wants content copied into a SharePoint archive, none of this work is thrown away.
5. **A3 is the trap.** It produces the demo that looks most like Google and silently revokes every
   collaborator's access to every file it touches. If the user wants it anyway after reading §3.2, build it as
   **A4** — a SharePoint destination, not a colleague's personal storage — because A3 solves the
   personal-storage problem by creating a fresh instance of the personal-storage problem.
6. **A5 is not an alternative and must not be offered as one.** It answers a different question, it degrades
   to read-only at 60 unlicensed days, and Microsoft documents that nonpayment overrides holds at 365. The
   app should report retention, never author it.

**What the user must be told plainly, in the app and not only in a plan document:**

- The manager gets a **URL**, not a folder in their OneDrive. Google's transfer folder cannot be reproduced
  without destroying sharing permissions.
- Granting access **does not stop any clock.** The OneDrive still dies on the tenant's schedule.
- SharePoint and Teams content is **deliberately untouched**, exactly as Google refuses to transfer shared
  drives. Say it in the UI, or admins will assume the terminate scenario handled everything.

**Before any of this is locked, run the four experiments from `api-and-auth.md` §8** — the 403 test, the token
test, the copy test, the owner-role test — plus one added here: confirm `Set-PnPTenantSite -Owners` actually
works against a `-my.sharepoint.com/personal/...` URL, since the PnP documentation does not say it does. The
403 test is the cheapest and the most load-bearing; if it comes back 200 without a grant, Fact 2 is wrong and
this entire document reorganises.

---

## 5. Carried-forward UNVERIFIED items

Everything from `onedrive-mechanisms.md` §7 and `api-and-auth.md` §8 still stands. The ones that specifically
gate the recommendation, plus the new ones this document introduces:

| # | Claim | Test that settles it |
|---|---|---|
| U1 | Delegated Graph 403s against another user's OneDrive without a site-collection-admin grant (Fact 2 — an inference joining two cited quotes, not a single quotation) | `GET /users/{x}/drives` as SharePoint Admin without the grant; then grant; repeat. Expect 403 → 200. |
| U2 | The public-client registration can mint `https://<tenant>-admin.sharepoint.com/.default`, and PnP accepts it for tenant-admin cmdlets | Mint, decode `aud`/`scp` per `LogTokenClaims`, `Connect-PnPOnline -AccessToken`, run `Set-PnPTenantSite -Owners`. |
| U3 | **New here:** `Set-PnPTenantSite -Owners` works against a personal (`/personal/...`) site URL | Run it against a scratch tenant OneDrive as SharePoint Admin; confirm with `Get-PnPSiteCollectionAdmin` or `Get-SPOUser -...IsSiteAdmin`. |
| U4 | **New here:** the correct token audience for `Connect-IPPSSession -AccessToken`, and whether an EXO and an IPPS session can coexist in one runspace | Mint candidate audiences, decode, connect; then attempt both connects in one pwsh session. |
| U5 | **New here:** whether PnP exposes `OrphanedPersonalSitesRetentionPeriod` | `Get-Help Set-PnPTenant -Parameter *`. |
| U6 | Adding a delegated Graph scope forces existing signed-in admins to re-consent | Add scope, grant admin consent, launch on a machine with a warm cache and existing `graph-auth.bin`, observe whether a browser opens. Only gates A3/A4. |
| U7 | "Enable access delegation" and the secondary owner are readable or settable by any supported API | `Get-PnPUserProfileProperty`, UPS CSOM `MySiteCleanup`, or inspect `_layouts/15/mysitesettings.aspx`. Gates whether A2's report can ever be truthful. |
| U8 | Simplified file transfer preserves full version history, and whether any endpoint backs it | Three-version file, delete user, transfer as manager, inspect history; capture web-UI network traffic. |
| U9 | `roles: ["owner"]` on `driveItem: invite` | POST it against a scratch item; record the status. Expect 400. |
| U10 | `remoteItem` POST to `/me/drive/root/children` is a supported v1.0 write | POST against a folder shared from another user's OneDrive, delegated; expect 201 or not. Only gates the A1 discoverability mitigation. |
| U11 | **New here:** whether the unlicensed-enforcement job **re-applies** read-only after an admin unlocks the site with `Set-SPOSite -LockState Unlock`. The page documents the unlock as a supported admin action for a read-only account but does not say it sticks. | Let a scratch account pass day 60, unlock, wait a full enforcement cycle, re-check `LockState`. Determines whether "day 60" is a wall or a speed bump for the admin. |
| U12 | **New here:** whether `PnP.PowerShell` v3 accepts `-AccessToken` **without** `-ClientId`. The cmdlet reference says *"You will also have to provide the `-ClientId` parameter starting September 9, 2024"* for the interactive parameter sets; whether the Access Token set is exempt is not stated. | `Connect-PnPOnline -Url <tenant-admin> -AccessToken <token>` with no `-ClientId`, on a clean machine with no `ENTRAID_CLIENT_ID` env var. |
| U13 | **New here:** whether the `Microsoft Teams Chat Files` folder name is stable/first-party, and whether Graph can cheaply count items under it (§3.5) | Post a file into a scratch 1:1 chat; enumerate the sender's drive root; check for the folder and its child count. Only affects how precisely the log can state the Teams blast radius. |

Two carried-forward items are now **partly settled** by re-reading the sources for this pass, and are kept
here rather than deleted so the change is visible:

- On whether a **read-only** (day 60–93) account is reachable by an admin: the unlicensed-accounts page
  documents two supported actions — *"Add a license"* and *"Use PowerShell to unlock the site"*. So it is not
  a hard wall for a SharePoint Administrator. What remains open is U11 above.
- On the **exact deletion clock**: it is a *cumulative unpaid-days* clock with a 1 July 2026 policy start and
  a 1 July 2027 floor for the pre-existing backlog, not a simple 365-days-from-unlicensing counter. See §A5(a).
  This is settled, not unverified, and the app must therefore **not** compute a day-365 date.

---

## 6. Sources

Newly fetched for this document:

- https://pnp.github.io/powershell/cmdlets/Set-PnPTenantSite.html
- https://pnp.github.io/powershell/cmdlets/Add-PnPSiteCollectionAdmin.html
- https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/connect-ippssession

Re-fetched and read in full during the **verification pass of 2026-09-01**, because the claims resting on
them were load-bearing and two of them were being reported incorrectly:

- https://learn.microsoft.com/en-us/sharepoint/unlicensed-onedrive-accounts (`ms.date` 2026-08-26) — corrected
  the pricing worked example and the shape of the 365-day clock; supplied FAQ 18's owner-property finding
- https://learn.microsoft.com/en-us/sharepoint/standard-storage-unlicensed-onedrive-accounts (`ms.date`
  2026-08-26) — **not covered by the four research briefs**; supplied the active-storage period, the
  admin-center-only enablement, and the 2026 reactivation-fee waiver
- https://learn.microsoft.com/en-us/sharepoint/retention-and-deletion — supplied the day-93-archive-regardless
  note and the Recycle-Bin/eDiscovery finding
- https://learn.microsoft.com/en-us/powershell/module/sharepoint-online/set-sposite — confirmed the `Owner`
  quote verbatim and that `Owner`/`LockState` are among the few parameters valid on a OneDrive
- https://pnp.github.io/powershell/cmdlets/Connect-PnPOnline.html — the `-AccessToken` renewal limitation and
  the v3 `-ClientId` requirement
- https://learn.microsoft.com/en-us/purview/retention-policies-teams — Teams chat files live in the
  organizer's/sharer's OneDrive (§3.5)
- https://learn.microsoft.com/en-us/microsoft-365/admin/add-users/remove-former-employee-step-5 — the
  first-party grant walkthrough names **no cmdlet**; it is the classic User Profiles UI
- https://learn.microsoft.com/en-us/sharepoint/sharepoint-admin-role — "about an hour" and the
  no-automatic-access sentence, both confirmed verbatim
- https://learn.microsoft.com/en-us/graph/api/driveitem-copy?view=graph-rest-1.0 — all four quoted sentences
  confirmed verbatim
- https://learn.microsoft.com/en-us/office365/servicedescriptions/sharepoint-online-service-description/sharepoint-online-limits
  — 100 GB / 30,000 files / 2 GB OneNote / 15 GB cross-geo / 400 characters / 13 & 2,600 & 10,000 holds, all
  confirmed verbatim

Relied on from the briefs (all first-party unless noted; full lists are in each brief's own source section):

- https://learn.microsoft.com/en-us/sharepoint/retention-and-deletion
- https://learn.microsoft.com/en-us/sharepoint/unlicensed-onedrive-accounts
- https://learn.microsoft.com/en-us/sharepoint/set-retention
- https://learn.microsoft.com/en-us/sharepoint/sharepoint-admin-role
- https://learn.microsoft.com/en-us/powershell/module/sharepoint-online/set-sposite?view=sharepoint-ps
- https://learn.microsoft.com/en-us/microsoft-365/admin/add-users/remove-former-employee-step-5
- https://learn.microsoft.com/en-us/graph/api/driveitem-copy?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/driveitem-move?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/permissions-reference
- https://learn.microsoft.com/en-us/purview/retention-policies-sharepoint
- https://learn.microsoft.com/en-us/office365/servicedescriptions/sharepoint-online-service-description/sharepoint-online-limits
- https://learn.microsoft.com/en-us/sharepoint/dev/general-development/how-to-avoid-getting-throttled-or-blocked-in-sharepoint-online
- https://support.microsoft.com/en-us/onedrive/simplified-file-transfer-for-departing-employees
- https://knowledge.workspace.google.com/admin/drive/transfer-drive-files-to-a-new-owner-as-an-admin
- https://developers.google.com/workspace/admin/data-transfer/v1/parameters
- https://developers.google.com/workspace/drive/api/guides/transfer-file

PnP PowerShell docs are a community-maintained Microsoft 365 & Power Platform Community project, not a
Microsoft product doc — authoritative for the module, one notch below learn.microsoft.com in confidence.

Repository code read directly for this document: `Services/GraphService.cs:19-27`, plus a repo-wide grep for
mail-sending code.

Repository code read directly during the **verification pass**, at working-tree commit `7423bcb` on
`feature/issues-4-5`: `Services/ScenarioRunner.cs` (whole file), `Models/Scenario.cs` (whole file),
`Services/ExchangeService.cs:1-140` and `:860-1300`, `Services/ScenarioStore.cs:93-119`,
`ViewModels/MainViewModel.cs:1030-1191`, `ViewModels/CloudObjectDetailViewModel.cs:860-935`,
`Views/Converters.cs:54-86`, plus `git show --stat` on commits `4bb3750` and `7b1fdd0`. **Every
`ScenarioRunner.cs` and `MainViewModel.cs` line reference in this document was re-derived from that read; the
ones inherited from `app-architecture.md` had drifted by 5–12 lines and are corrected in place.**
