# OneDrive at offboarding

Status: **reviewed. D1–D8 locked; the design is gated on the Phase 0 spike.** See
[`onedrive-phase0-spike.md`](onedrive-phase0-spike.md) for the procedure that unblocks it.

The ask: when a Scenario terminates a user, deal with their OneDrive — ideally transferring it to someone else,
such as their manager — reproducing Google Workspace's Drive ownership transfer as closely as possible. Open
question from the ask: move the files somewhere else, or leave them under a retention policy?

Researched against vendor documentation and this codebase, then adversarially verified. The verification pass
corrected 17 material errors in the drafts, including a misread pricing example, a wrong deletion clock, and
three claims about this repo that were simply not true. Where something could not be settled from
documentation it is marked **UNVERIFIED** with the test that settles it, rather than stated as fact.

---

## The answer nobody wants: there is no ownership transfer in Microsoft 365

Google models ownership as **an attribute on a file**. Transfer rewrites it; the bytes never move; sharing is
untouched; it is instant regardless of size.

Microsoft models a OneDrive as **a SharePoint site collection provisioned for a principal**. There is no
ownership attribute to rewrite. Graph exposes no ownership operation. And the field that looks like one is
documented as forbidden, verbatim from [`Set-SPOSite`](https://learn.microsoft.com/en-us/powershell/module/sharepoint-online/set-sposite):

> "Specifies the owner of the site collection. **Changing the Owner of a OneDrive is not supported and causes
> many experiences to break.**"

Reinforced by [Manage unlicensed OneDrive user accounts](https://learn.microsoft.com/en-us/sharepoint/unlicensed-onedrive-accounts),
FAQ 18: modifying the owner property "results in an unsupported state of the OneDrive account".

**The trap worth naming:** the cmdlet lets you do it. `Owner` is a valid parameter on a OneDrive site
collection. It succeeds, and it is unsupported. The PnP equivalent to avoid is `-PrimarySiteCollectionAdmin`,
which *replaces* the primary. The safe one is `-Owners`, which adds a **secondary** administrator. Those are
one word apart in the cmdlet surface, and picking the wrong one ships a broken tenant that looks like success.

This is not a missing API that might arrive. It is a difference in what a personal file store *is*.

## The pick-two triangle

Google's admin transfer delivers three things at once. Every Microsoft mechanism delivers **two** and forfeits
the third. This is the gap, stated as precisely as it can be stated:

| | Admin-driven, no cooperation needed | Lands in the receiver's own storage | Preserves existing sharing |
|---|---|---|---|
| **Site-collection-admin grant** | ● | ○ | ● |
| **Graph copy** | ● | ● | ○ |
| **Simplified file transfer** (browser, manager-driven) | ○ | ● | ● |
| *Google admin transfer* | ● | ● | ● |

Read the last column as a price list. Wanting the Google-shaped visual result — files appearing in the
manager's own storage — costs either **every sharing permission on every file**, or **the admin's ability to
do it without the manager**. No amount of build effort moves a row.

Graph is explicit: *"Permissions are not retained when a driveItem is copied."* And *"Items cannot be moved
between Drives"* — a cross-drive relocation is always a physical copy, subject to 100 GB / 30,000 items,
100 GB/hour egress, and a 400-character path limit. Google's equivalent limit is a 36-hour job ceiling; ours
is a hard refusal at a size a real user reaches.

Microsoft's own closest analogue to Google's feature — **Simplified file transfer** — is browser-only and
manager-driven. Its support page documents no admin UI, no cmdlet and no Graph endpoint.

## "Leave it under retention" is not a resting state

The other half of the ask fails differently. An unlicensed OneDrive is on a **decay curve**:

| Milestone | What happens |
|---|---|
| Day 60 | Goes **read-only** |
| Day 93 | **Archived**. Lengthening the tenant retention period past 93 days buys nothing — archiving happens then regardless |
| Day 275 | Drops out of **eDiscovery** |
| 365 **cumulative unpaid days** | Eligible for **deletion** |

And the deletion overrides compliance, verbatim from FAQ 7 of the unlicensed-accounts page:

> "…the unlicensed accounts will be subject to deletion 365 days after being unlicensed **even if retention
> policies, settings, or holds exist** on the OneDrive account."

The failure mode is silent: the compliance dashboard keeps reporting the policy as healthy. Note the last
clock counts *cumulative unpaid days*, pausing while billing is on, with a policy start of 1 July 2026 — so
the app must **describe that milestone in prose and never compute it as a date**. The 93 recycle-bin days do
not count as compliance time either: the Recycle Bin is not indexed, so an eDiscovery hold cannot locate
content in it.

**So the ask's binary is false.** Retention answers "can we answer a lawyer later"; transfer answers "can the
business keep working". Google ships both and treats neither as a substitute. Retention is the correct and
irreplaceable tool for the compliance question — it just cannot carry the continuity question, which is what a
terminate scenario is for.

---

## What is actually buildable here

### Delegated Graph cannot bootstrap itself

[SharePoint admin role](https://learn.microsoft.com/en-us/sharepoint/sharepoint-admin-role): "Global
Administrators and SharePoint Administrators **don't have automatic access to all sites and each user's
OneDrive**, but they can give themselves access to any site or OneDrive."

Delegated `Files.*.All` means "all files *that the signed-in user can access*". So a SharePoint Administrator
signed into this app gets **403** against a departing user's drive until something grants access to that
specific site — and **that grant has no Graph endpoint**.

> The site-collection-admin grant is not one approach among several. **It is the prerequisite for every
> approach that touches file content.**

This is the single most load-bearing inference in the research, and it is an inference joining two cited
quotes rather than one quotation. It is **UNVERIFIED (U1)** and the cheapest possible spike settles it.

### The good news: no secret, no certificate

Every route has a delegated, public-client path. The app's security posture survives intact.

| Need | Naive route | Delegated alternative |
|---|---|---|
| Grant site-collection admin | `Connect-SPOService -CertificateThumbprint` — **needs a certificate** | `Connect-PnPOnline -AccessToken` + `Set-PnPTenantSite -Owners`. Public client, PKCE, no secret — the exact analogue of the existing Exchange borrow-the-token pattern |
| Read a drive, enumerate, copy | Graph **application** `Files.ReadWrite.All` — **app-only** | Delegated `Files.ReadWrite.All` / `Sites.ReadWrite.All`, *after* the grant |
| Purview retention or hold | `Connect-IPPSSession -CertificateThumbprint` | `Connect-IPPSSession -AccessToken`, module 3.8.0-Preview1+, in the module the app already hosts |

**`Connect-SPOService` has no `-AccessToken` parameter in any of its four parameter sets**, so the SharePoint
Online Management Shell cannot borrow the app's token at all. Its `-Credential` variant is username+password,
incompatible with MFA and with this app's model. It also requires `-UseWindowsPowerShell`, so it is not native
pwsh 7. **PnP.PowerShell is the only viable channel**, and it accepts `-AccessToken` with `-ClientId`
documented as optional in that parameter set (**UNVERIFIED, U12**).

Cost is not a credential. It is **one new delegated SharePoint permission** on the existing registration
(resource `00000003-0000-0ff1-ce00-000000000000`), admin-consented once — structurally what the app already
does for Exchange. Only the copy approaches would need a new **Graph** scope, which is the one that raises a
re-consent question for existing users (**UNVERIFIED, U6**).

### What the architecture refuses

`ScenarioRunner` is synchronous per target. The pwsh channel's ceiling is 180 s and **a timeout kills the host
process, taking every open Exchange session with it**. There is no job store, no queue, nothing that survives
the window that started it.

> "Leave the files in place and re-permission them" fits this architecture. "Move the files somewhere else"
> does not.

---

## Three findings about this repo that change the plan

These came out of verification against the code, and each one breaks something the drafts assumed.

**1. The operation log does not exist unless the scenario asks for it.** `MainViewModel.RunScenarioAsync`
allocates the log only when the scenario contains a `SaveOperationLog` step, and **the seeded Terminate User
scenario has no such step**. On a stock install the granted URL would be written nowhere and no Save-As would
appear. Since "record the URL so the manager can find it in six months" *is* the discoverability answer, this
has to be fixed for the feature to mean anything.

**2. Scenario targets are on-prem AD objects.** `RunAsync` takes `IReadOnlyList<AdObjectRow>`, and every cloud
step resolves the twin from the on-prem `userPrincipalName`. A **cloud-only leaver is unreachable** by any
scenario step, and an alternate-login-ID tenant resolves the wrong UPN — which for a file store is a
data-exposure risk, not just an annoyance.

**3. `ScenarioActionType` has no licence action at all**, so a terminate scenario cannot remove a licence. The
existing mailbox guardrail lives on the cloud pane only. Most licence removals happen outside this app
entirely, so a OneDrive warning needs **two homes** or it will not be seen.

Also worth carrying: three `switch` statements have permissive `default:` arms, so an enum member added
without a runner case **"runs, does nothing, and is logged with a ✓"**. For a step whose job is preserving a
departing employee's files, that is unacceptable.

---

## The consideration that was missing: Teams chat files

Files the leaver posted into 1:1 and group chats live in **their** OneDrive, under a `Microsoft Teams Chat
Files` folder. Purview: to retain them "you need a retention policy that includes the organizer's OneDrive
account".

A Graph copy leaves every chat reference pointing at the **source**, so a "successful" transfer does not save
them. A grant preserves all of them by doing nothing. This widens the blast radius of getting it wrong from
"the manager" to **everyone the leaver ever sent a file to**.

---

## Recommendation

**Grant, record and warn. Do not move bytes.**

The app should set up, verify and document the transfer; it should not perform the file move. That is not a
retreat — reimplementing the move over Graph copy would produce a feature that **reports success while
silently revoking every collaborator's access**, which is measurably worse than doing nothing.

Proposed behaviour:

| # | Behaviour |
|---|---|
| 1 | Default terminate action: grant a **named person** site-collection-admin on the leaver's OneDrive. Move nothing. Bounded, fast, fully reversible with one cmdlet |
| 2 | Default the grantee to the Entra manager, but **require explicit confirmation** — an unset manager is the most common silent failure |
| 3 | **Never write "files transferred" unless bytes moved.** Write "access granted, URL recorded" |
| 4 | Log the OneDrive **URL, storage used, item count**, the read-only and archive dates — and the deletion milestone **in prose, never as a date** |
| 5 | A licence-removal guardrail modelled on the mailbox one: "⚠ This OneDrive (N GB, M items) becomes READ-ONLY in 60 days and is ARCHIVED in 93. There is no OneDrive equivalent of converting a mailbox to shared." |
| 6 | **Do not delete the Entra identity in the same run**, and warn if the operator sequences it that way — deletion destroys the free relicensing recovery path |
| 7 | **Report** the tenant retention period and access-delegation state. **Never set them** |
| 8 | Close the three silent `default:` arms |
| 9 | **Refuse to act, loudly, if an active hold is detected** — three outcomes, not two: held / not held / **not checked** |

Where the ask said "the manager's OneDrive", the research pushes back: moving a leaver's files into another
individual's personal storage solves this offboarding and **schedules the identical problem for the manager's
own departure**. If a copy is ever built, an organisation-owned SharePoint site is the right destination, with
the manager's OneDrive as an explicit fallback rather than the default.

One place we can **beat** Google: Google's transfer defaults to shared files only, silently leaving the
leaver's genuinely private files behind. A site-collection-admin grant covers everything, with no privacy
filter to get wrong.

---

## Phase 0 must come first

Thirteen items are UNVERIFIED, and the Exchange work established the precedent that a spike comes before a
plan is locked. The load-bearing ones:

| # | Claim | Test |
|---|---|---|
| **U1** | Delegated Graph 403s against another user's OneDrive without a grant | `GET /users/{x}/drives` as SharePoint Admin, then grant, then repeat. Expect 403 → 200. **If it returns 200 without a grant, this entire plan reorganises** |
| **U2** | The public client can mint a `<tenant>-admin.sharepoint.com` token and PnP accepts it | Mint, decode `aud`/`scp`, `Connect-PnPOnline -AccessToken`, run a tenant-admin cmdlet |
| **U3** | `Set-PnPTenantSite -Owners` works against a `/personal/...` URL — PnP's docs do not say it does | Run against a scratch tenant OneDrive, confirm with `Get-PnPSiteCollectionAdmin` |
| **U12** | PnP v3 accepts `-AccessToken` **without** `-ClientId` | Clean machine, no `ENTRAID_CLIENT_ID`, connect with token only |
| **U7** | Access-delegation state is readable by any supported API | Gates whether behaviour #7's report can ever be truthful |

U1 is the cheapest and the most load-bearing. It should be run first, alone, before anything else is decided.

---

## Locked decisions

**D1 — the app grants access; it does not perform the routine file move.** A site-collection-admin grant on
the leaver's OneDrive, recorded, with nothing relocated. This is the default and the only thing a terminate
scenario does to file content. Rationale: it is bounded, reversible with one cmdlet, destroys nothing, and is
the mandatory first half of every other option anyway.

**D2 — a copy IS in scope, but only to an organisation-owned SharePoint site, and never as the default.**
Offered as an explicit, separate action behind an acknowledgement that **sharing permissions are destroyed**
(Graph: *"Permissions are not retained when a driveItem is copied."*). The manager's personal OneDrive is
**not** a supported destination: parking a leaver's files in another individual's personal storage solves this
offboarding and schedules the identical problem for the manager's own departure. This deliberately declines
the closest visual match to Google, and the reason is recorded so it is not revisited by accident.

**D3 — a copy must state its blast radius before it runs.** Item count, total size, the SharePoint ceilings it
could hit (100 GB, 30,000 items, 400-character paths), and the fact that files the leaver owned **in other
people's areas** get duplicated with permissions stripped. Teams chat files are named explicitly, because a
copy leaves every chat reference pointing at the source drive.

**D4 — Phase 0 comes before the design is locked.** U1, U2, U3 and U12 against a scratch tenant. U1 runs
first and alone: if delegated Graph returns 200 against another user's drive with no grant, Fact 2 is wrong
and the whole comparison reorganises. Same precedent as the Exchange spike, which changed that design.

**D5 — the three repo defects are fixed in this branch, as prerequisites rather than follow-ups.** The seeded
Terminate User scenario gains a `SaveOperationLog` step (without it the recorded URL goes nowhere, and the
record *is* the discoverability answer); the three permissive `default:` arms are closed (today an enum member
with no runner case runs, does nothing, and logs a ✓); and the on-prem-only target resolution is at minimum
detected and reported, so a cloud-only leaver fails loudly instead of silently skipping.

**D6 — the first shippable slice is read-only.** A user's OneDrive URL, storage used, item count, hold state,
and the computed read-only and archive dates. It is useful on its own, it proves the PnP channel before
anything writes, and it is safe to run against production while the write path is still unproven.

**D7 — the PnP channel is a separate host process from Exchange.** The pwsh channel kills its host on
operation timeout, taking every open session with it. Sharing one process would let a slow PnP call destroy an
Exchange session mid-scenario. Shared base class, separate processes.

**D8 — the app reports retention and access-delegation state; it never authors them.** Both are tenant-wide
with no per-user override. The deletion milestone is described **in prose, never computed as a date**, because
it runs on cumulative unpaid days with a policy start of 1 July 2026 rather than a simple 365-day counter.

Two smaller calls taken by default: the grantee **defaults to the Entra manager but requires explicit
confirmation** (an unset manager is the most common silent failure), and the app **refuses to act when an
active hold is detected**, treating "could not determine hold state" as a third outcome distinct from "not
held".

---

## Sequencing

0. **Phase 0 spike** — U1, U2, U3, U12 against a scratch tenant. Half a day, and it decides whether the rest
   is real. Procedure in [`onedrive-phase0-spike.md`](onedrive-phase0-spike.md). **Nothing below starts until
   U1 is settled.**
1. **The repo prerequisites (D5)** — the seeded scenario's `SaveOperationLog` step, the three `default:` arms,
   the cloud-only-leaver detection. Independent of the spike, so it can run in parallel with it.
2. **The PnP channel (D7)** — a second hosted-module channel beside Exchange, following `ExchangeService`,
   in its own host process.
3. **The read-only surface (D6)** — URL, size, item count, hold state, computed clock dates.
4. **The grant (D1)** — as a pane action and a scenario step, with the record and the refusal-on-hold.
5. **The guardrails** — the licence warning in both homes, since `ScenarioActionType` has no licence action
   and most licence removals happen outside this app entirely.
6. **Copy to SharePoint (D2, D3)** — last, explicit, and never default.
