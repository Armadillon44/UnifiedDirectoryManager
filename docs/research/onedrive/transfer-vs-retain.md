# Transfer the files, or leave them under retention?

**Status: decision document, not yet a plan. Audited 2026-09-01 — see the change list below. Written
2026-09-01 against the four OneDrive research briefs in
this folder (`google-drive.md`, `onedrive-mechanisms.md`, `api-and-auth.md`, `app-architecture.md`), plus three
Microsoft Learn pages re-fetched and re-read for this pass because the whole cost argument rests on them.
Every external claim carries a URL. Claims the briefs marked `UNVERIFIED:` are carried forward as unverified
and are not quietly hardened here. Nothing below is committed to code.**

The ask, in the user's words: *"I don't know if the best course is to move their OneDrive files somewhere else,
or keep them as-is with a retention policy."*

---

## Audit log — what a verification pass changed, 2026-09-01

An adversarial re-check against the repository at commit `7423bcb` and against the vendor documentation.
**The recommendation stands. Several of the numbers underneath it did not.** Most serious first:

| # | What was wrong | Where it is fixed |
|---|---|---|
| 1 | **The $614.40 figure was misread.** Microsoft's example bills $614.40 for reactivating **one 1 TB account**, not for 100 accounts. The reactivation fee is per-account; only the $0.05/GB/month meter is tenant-wide. | §1, "B2" |
| 2 | **The 365-day clock is a *cumulative unpaid-days* clock**, started 1 July 2026, pausable while billing is on, floored at 1 July 2027 for the existing backlog. It is not 365 days from unlicensing, and the app must not print it as a date. | §1 timeline, default #4 |
| 3 | **Default #5's guardrail doesn't fire where this document implied.** It lives on the cloud pane's remove-licence command only, and **`ScenarioActionType` has no licence action at all**, so a terminate scenario cannot remove a licence. Most licence removals happen outside this app entirely. | correction (ii) |
| 4 | **Defaults #3 and #4 assume an operation log that is off by default** — it is written only if the scenario contains a `SaveOperationLog` step, and the seeded Terminate User scenario has none. | correction (iii) |
| 5 | **This document and `approaches.md` named two different cmdlets for the same primitive**, and neither is what Microsoft documents. Only the PnP route fits this app's auth model. | correction (i) |
| 6 | **"It is tenant-wide, so you can't extend it for one person" is wrong** — `Set-SPOSiteArchiveState -KeepActiveUntil` is a per-account lever. Also missing: the 2026 reactivation-fee waiver, and that neither Standard storage nor billing can be enabled by PowerShell at all. | §1, §6 |
| 7 | **B1 was priced as "a full M365 seat".** The clock is stopped by *any* SKU that includes OneDrive, so the real lever is downgrade, not keep. Which SKU, and at what price, is left `UNVERIFIED` rather than guessed. | §1, "B1" |
| 8 | **The 93 recycle-bin days were counted as compliance time.** The recycle bin isn't indexed, so eDiscovery can't reach it — the discoverability window is the retention period alone. | §4 caveat 3, §5 |
| 9 | **Neither document raised Teams chat files**, which live in the leaver's OneDrive and which a Graph copy does not save. | corrections block; `approaches.md` §3.5 |
| 10 | **Neither document raised that scenario targets are on-prem AD objects**, so a cloud-only leaver is unreachable by any scenario step. | correction (iv) |
| 11 | Lengthening the OneDrive retention period past 93 days does **not** extend usable life — archiving happens on day 93 regardless. | §5 baseline 2 |
| 12 | The Simplified-file-transfer notification was paraphrased as a quotation. | §2 |

Claims that were checked and **held**: the whole `driveItem: copy` mechanism table row (four quotes, verbatim);
`Set-SPOSite -Owner`'s unsupported-for-OneDrive sentence; day 275 eDiscovery; FAQ 5, 7, 10, 11 and 12; the
"compliance dashboard lies" paragraph (verbatim, and it really is that alarming); the SharePoint limits (100 GB
/ 30,000 / 250 GB / 2 GB / 15 GB / 400 chars); the "SharePoint is considered a collaborative environment"
contrast; the two "move actively needed content to a SharePoint site" recommendations; "about an hour" for a
role change; the no-automatic-access sentence; "only the latest version is moved" for the admin-center
Move/Copy; the guardrail's existence and wording; the seeded Terminate User scenario's five steps.

---

## Key finding: this is a false binary, and one half of it is not a stable state

Transfer and retention answer different questions. Google ships both and does not treat either as a substitute
for the other (`google-drive.md`, §"What as close as possible would have to mean", item 10): transfer is about
*the business continuing to work*; retention is about *the business being able to answer a lawyer later*.

But the more important finding is that on Microsoft 365, **"leave it as-is under a retention policy" is not a
resting state. It is a decay curve with a paid brake.** An unlicensed OneDrive goes read-only on day 60,
archived on day 93, drops out of eDiscovery on day 275, and becomes eligible for deletion at **365 cumulative
unpaid days** — and Microsoft states outright that that deletion happens *regardless of retention policies and
holds* if the storage is unpaid:

> "If billing is not enabled for unlicensed OneDrive accounts, and accounts are still retained 365 days after
> becoming unlicensed, then the unlicensed accounts will be subject to deletion 365 days after being
> unlicensed even if retention policies, settings, or holds exist on the OneDrive account."
> — [Manage unlicensed OneDrive user accounts, FAQ 7](https://learn.microsoft.com/en-us/sharepoint/unlicensed-onedrive-accounts) (ms.date 2026-08-26)

So the real question is not "move or retain". It is:

1. **Do we move the files that matter, and where to?** — and the answer is yes, and preferably not into another
   individual's personal storage.
2. **What do we pay to keep the residue, and for how long?** — a budget decision, not a technical one.

The rest of this document argues both sides on the seven criteria the ask named, then commits.

---

## The two options, stated precisely

Neither option is one thing. Both have variants with materially different costs, and conflating the variants is
how this decision gets made badly.

**Option A — Transfer.** Relocate the leaver's files out of their OneDrive. Three destinations:

| | Destination | Mechanism |
|---|---|---|
| **A1** | The manager's own OneDrive | Manager-driven web move, or Graph copy |
| **A2** | A SharePoint site (team site / document library) | Same mechanisms, different target |
| **A3** | Nothing moves; the manager is granted access to the leaver's OneDrive URL | SharePoint admin center → User profiles → *Manage site collection owners* (what Microsoft documents); `Set-SPOUser -IsSiteCollectionAdmin` or `Set-PnPTenantSite -Owners` as the automatable equivalents — **only the PnP one fits this app's auth model**, see correction (i) below; or automatic access delegation |

A3 is the one Microsoft actually automates and the one the app can plausibly perform (`api-and-auth.md` §7.6,
`app-architecture.md` §4.3). It is *not* a transfer — it is a grant — but it is what most "transfer the
OneDrive" conversations end up meaning in M365, so it belongs in the comparison.

**Option B — Leave in place under retention.** Three variants, distinguished by what is being paid:

| | Variant | Recurring cost |
|---|---|---|
| **B1** | Keep the leaver licensed | A full M365 seat, per leaver, indefinitely |
| **B2** | Unlicense, enable unlicensed-OneDrive billing | $0.05/GB/month **across every unlicensed OneDrive in the tenant** older than 93 days |
| **B3** | Unlicense, pay nothing, rely on the retention policy | $0 — and the data is gone in ~365 days |

B3 is what "leave them as-is with a retention policy" means if nobody signs a cheque. It is the variant most
people have in mind, and it is the one that does not work.

---

## 1. Licensing cost — and this usually decides it

### Does transfer require keeping the leaver licensed?

**No, and this is transfer's strongest single advantage.** The licence can be reclaimed on day 1. Microsoft is
explicit that an admin can reach the content of an unlicensed account:

> "If you remove a user's license but don't delete the account, you can give yourself access to the content in
> the user's OneDrive."
> — [Remove a former employee, step 5](https://learn.microsoft.com/en-us/microsoft-365/admin/add-users/remove-former-employee-step-5)

The constraint is not licensing, it is **time**. Removing the licence starts the unlicensed clock immediately
(`onedrive-mechanisms.md` §K), so the move has to happen before day 60:

| Day | State | Source |
|---|---|---|
| 1 | Clock starts on licence removal **or** Entra deletion — *for accounts unlicensed on or after 1 July 2026*. For the tenant's pre-existing backlog the clock started **1 July 2026** | [unlicensed-onedrive-accounts](https://learn.microsoft.com/en-us/sharepoint/unlicensed-onedrive-accounts) |
| 60 | Read-only. A wall for the grantee; **not** for an admin — see below | same |
| 93 | "The account is archived **or moved to the recycle bin depending on account and retention state**" | same |
| 275 | "The account is no longer available in eDiscovery." | same |
| 365 | Subject to deletion after **365 cumulative *unpaid* days**, holds notwithstanding | same |

**Two corrections to that table, from a re-read of the page (`ms.date` 2026-08-26) during the 2026-09-01
verification pass.**

**(a) The 365-day clock is cumulative, pausable, and dated.** Verbatim: *"As of July 1, 2026, unlicensed
OneDrive accounts that remain unpaid are subject to a **cumulative 365-day nonpayment clock**. […] For
accounts already unlicensed and unpaid on July 1, 2026, deletion risk begins **no earlier than July 1,
2027**."* FAQ 4's worked table shows the count **pausing** while billing is on — *"Billing is enabled for 60
days | Count pauses while the account is paid"* — and the page prints three worked examples of the
earliest-deletion-risk date. Consequence for the app, and it changes proposed default #4 below: **day 60 and
day 93 are computable from the unlicensed date and should be printed as dates; day 365 is not, and must be
prose.** A confidently wrong deletion date in a termination log is worse than no date at all.

**(b) Day 60 is a deadline for *unassisted* access, not a point of no return.** The page's own list of
options for a read-only account is *"Add a license"* and *"Use PowerShell to unlock the site"* — the latter
linking to `manage-lock-status`, i.e. `Set-SPOSite -LockState Unlock`, which that cmdlet's reference confirms
is one of the short list of parameters valid on a OneDrive for Business site collection
(https://learn.microsoft.com/en-us/powershell/module/sharepoint-online/set-sposite). This **partly resolves
carried-forward item 10** at the foot of this document, in the direction the item guessed was awkward. What
remains open is whether the enforcement job simply re-applies read-only afterwards. Until that is settled the
app should keep presenting day 60 as the operator's deadline — but it must not tell anyone the content is
gone, because it is not.

*(Also from the same page, and relevant to any tenant this ships into: "These changes don't apply to EDU, GCC,
or DoD customers.")*

> "Unlicensed OneDrive accounts are automatically put into read-only mode after 60 unlicensed days and archived
> after 93 unlicensed days. While read-only and archived accounts remain visible to admins through
> administrative tools, **neither admins nor end users have access to their content** unless an admin takes a
> supported action such as assigning a license, enabling applicable billing, reactivating archived content, or
> moving content that must be retained." — same page

**Net for transfer: zero ongoing licence cost, one hard 60-day deadline.** Realistically the move should happen
in the same week as the termination, not in the sixtieth day of a race.

### Does retention require keeping the leaver licensed?

**Effectively yes — or paid, which is the same conversation with a different invoice.**

**B1, keep the seat.** Works, indefinitely, and is the only variant with no clock at all. It also defeats the
purpose of offboarding: every termination permanently adds a seat, and the cost accumulates forever with no
natural end. It is the one most likely to be adopted by accident (nobody removes the licence, nobody notices).

**But the first draft priced B1 as "a full M365 seat" and that overstates it, which matters because it is the
variant an operator can actually action today.** The unlicensed clock is driven by *"a Microsoft 365 or Office
365 subscription to the user that includes OneDrive"* — not by the leaver's original E3 or E5. So the real B1
lever is **downgrade, not keep**: strip the expensive SKU and leave the cheapest one in the tenant's inventory
that still carries a OneDrive service plan, which keeps the account licensed and stops the clock at a fraction
of the seat cost. Whether a *standalone* OneDrive/SharePoint plan is still purchasable, and at what price, is
**`UNVERIFIED`** and deliberately not asserted here — it is a purchasing question for whoever owns the bill,
not a technical one, and the plan-availability page is the wrong thing to guess about. *Test:* look at what
SKUs the tenant already owns spare seats of, in Microsoft 365 admin center → Billing → Your products, and
check which of them include a OneDrive service plan. The app can help here — it already reads
`assignedLicenses` / `licenseAssignmentStates` and resolves SKU part numbers
(`GraphService.cs`, `LicenseCatalog.cs`) — but only as a **report**. It must not pick a SKU.

**B2, pay the unlicensed-storage meter.** The pricing has a cost structure that is easy to misread:

> "If the billing is enabled to reactivate one particular unlicensed account, the reactivation fee is applied
> for $0.60/GB for that account, and from that month onward, the storage fee of $0.05/GB/Month is applicable
> **for all unlicensed accounts within the organization for longer than 93 days**."
> — [unlicensed-onedrive-accounts, Charges from archived accounts](https://learn.microsoft.com/en-us/sharepoint/unlicensed-onedrive-accounts)

Read that twice, because the first draft of this document misread it. **The two halves have different
scopes.** The *reactivation* fee is **per account you actually reactivate**; the *storage* meter is
**tenant-wide**. Microsoft's own worked example, verbatim, corrected here:

> "For example, if an organization has 100 unlicensed OneDrive accounts, each consuming 1 TB for a total of
> 100 TB […] If the organization needs to reactivate **a specific account** in December 2025 and set up
> billing, they incur the following costs:
> - A one-time reactivation fee of $0.60/GB **for 1TB, totaling $614.40**.
> - A monthly storage fee of $0.05/GB **for 100TB, amounting to $5,120/month** starting from December 2025."

So $614.40 is **one 1 TB account**, not a hundred of them. The structural point survives and gets sharper: the
marginal cost of keeping *one more* leaver is trivial, but **switching the meter on for the first time bills
your entire existing backlog** — $5,120/month in Microsoft's example — whether or not you ever open any of
those accounts. FAQ 7 adds the twist that makes it worse for a compliance-driven tenant: *"If a OneDrive
account is retained due to a retention policy, retention setting, or litigation hold and has been archived due
to being unlicensed for 93 days or longer, then you still pay for the monthly archive storage costs when
billing is enabled."* Holding it costs money; holding it without paying loses it.

One cost lever that cuts the other way, from the Standard-storage page: **"During calendar year 2026, the
reactivation fee is waived when Standard storage is enabled."** If this decision is being priced in 2026, the
$0.60/GB half of it may be zero.

So B2's real question is not "what does this leaver cost" but "how many orphaned OneDrives does this tenant
already have, and what is the sum of their storage". That number should be pulled from **SharePoint admin
center → Reports → OneDrive accounts** before anyone commits to B2. It is knowable today and it is the single
most useful piece of homework in this document.

**B2 also does not stop the archive.** Paying keeps the data alive and honours holds, but the account is still
archived at day 93 and still needs a reactivation (up to 24 hours) to be readable, after which it stays active
for only 30 days before re-archiving (FAQ 10, same page). Paying buys *survival*, not *usability* — **unless
you also opt into Standard storage and pass `-KeepActiveUntil`, which the first draft of this document
missed.** See the correction two paragraphs down.

There is a newer lever the earlier briefs did not cover: a tenant-wide active-storage period that pushes the
read-only and archive milestones out.

> "Set the active-storage period | Tenant (all applicable unlicensed OneDrive accounts) | Changes when accounts
> become read-only and are archived. Supported values are 93 through 3,650 days."
>
> `Set-SPOTenant -UnlicensedOneDriveAccountsActiveStoragePeriod 365`
>
> "The read-only stage begins 33 days before the configured archive date."
>
> "There's no way to keep an account active indefinitely."
> — [Standard storage for unlicensed OneDrive accounts](https://learn.microsoft.com/en-us/sharepoint/standard-storage-unlicensed-onedrive-accounts) (ms.date 2026-08-26)

Three caveats that blunt it: it requires pay-as-you-go billing **and** a separate Standard storage opt-in; it
is **tenant-wide**, so extending it for one departing executive extends it for every unlicensed OneDrive you
own and bills accordingly; and it is **not generally available until October 2026** — "Standard storage will be
generally available for unlicensed OneDrive accounts starting in October 2026." Microsoft's own advice is to
leave it alone: *"Keep the default at 93 days unless your organization has a defined business, legal, or
compliance need for a longer lifecycle. Use the shortest period that meets the requirements."*

**Correction: there is also a *per-account* lever, and the first draft's "it is tenant-wide, so you can't do
it for one person" is therefore wrong.** The same page documents `Set-SPOSiteArchiveState` with
`-KeepActiveUntil`, scoped to *"One archived unlicensed OneDrive account"*:

```powershell
Set-SPOSiteArchiveState -Identity "https://contoso-my.sharepoint.com/personal/user_contoso_com" `
    -ArchiveState Active -KeepActiveUntil "2027-08-13"
```

Re-running it with a later date extends; `-ArchiveState Archived` returns the account to Archive early. So the
honest shape of the B2 argument is: **the tenant-wide `UnlicensedOneDriveAccountsActiveStoragePeriod` should
stay at 93, and a genuine one-off exception is a per-account `-KeepActiveUntil`.** That is a much better fit
for "one departing executive" than the first draft allowed.

Four things gate it, and together they mean the app cannot touch any of this:

1. **Standard storage must be enabled**, and — verbatim — *"There is no SharePoint Online PowerShell parameter
   for enabling the billing policy or Standard storage."* Both toggles are **admin-center UI only**.
2. **The cmdlets are SharePoint Online Management Shell**, not PnP, and the tenant properties need build
   **16.0.27600.12000 or higher**. `Connect-SPOService` has no `-AccessToken`, so this module cannot borrow
   the app's Entra token the way `ExchangeOnlineManagement` and `PnP.PowerShell` can (`approaches.md` §1).
3. **`Get-SPOTenant` returns 0** for the property where the tenant hasn't got the server build — a silent
   "not configured" that is indistinguishable from a real 0 unless the code knows to treat it as "unknown".
   That is the app's own house rule about three outcomes, not two, arriving from Microsoft's side.
4. **`-KeepActiveUntil` requires a date and there is no "forever"**: *"There's no way to keep an account active
   indefinitely."*

Net: this whole lever is a **P6 policy question the app reports and never operates**, and it does not change
the recommendation — it just means "retention has a paid brake" is more nuanced than "pay the tenant meter".

**B3, pay nothing.** Free, and the data is deleted at day 365 whether or not a retention policy or a legal hold
says otherwise. This is not a retention strategy. It is a 12-month delay on data loss.

### The asymmetry with Exchange that will trip the operator up

The app already ships `ExchangeConvertToShared` precisely so a mailbox survives unlicensing
(`Models/Scenario.cs:45`). Exchange also has the inactive-mailbox concept, which preserves a mailbox after the
user is deleted while it is on hold
([inactive mailboxes](https://learn.microsoft.com/en-us/purview/inactive-mailboxes-in-office-365)).

**OneDrive has no documented counterpart to either.** There is no "convert to shared" for a OneDrive, and
`onedrive-mechanisms.md` §0 records that no first-party doc states "there is no inactive OneDrive" — it is an
argument from absence across the Purview and SharePoint doc sets. `UNVERIFIED:` carried forward. The test that
settles it is a support case asking directly.

The practical consequence stands either way: an operator who has learned "convert the mailbox and it survives"
will reasonably assume OneDrive has an equivalent. It does not, and the app must say so at the point of action.

**Criterion 1 verdict: transfer wins decisively.** Transfer costs nothing ongoing and reclaims the seat
immediately. Every durable form of retention requires either a permanent seat per leaver or switching on a
tenant-wide meter whose first bill is your whole backlog.

---

## 2. Sharing links and other people's access

This is retention's best criterion and transfer's worst, and the honest comparison is closer than it looks.

### Retention preserves access — for exactly as long as you pay

> "**OneDrive:** If a user leaves your organization, any files that are subject to a retention policy or has a
> retention label will remain subject to the retention settings for the duration of the retention period
> specified in the policy or label. **During that time, all sharing access continues to work** and the content
> continues to be discoverable by Content Search and eDiscovery."
> — [Learn about retention for SharePoint and OneDrive](https://learn.microsoft.com/en-us/purview/retention-policies-sharepoint)

Nothing in the transfer column can match that sentence. Every link, every Teams file reference, every embedded
document keeps resolving, with no action by anyone.

**But it dies on the same clock as everything else.** At day 60 the site is read-only — *"neither admins nor
end users have access to their content unless an admin takes a supported action"*, and the collaborators whose
links this criterion is about are not admins, so for them it is simply over. At the end of retention, or when
the OneDrive is recycled, it is total:

> "After seven days, the OneDrive for the deleted user is moved to the site collection recycle bin, where it's
> kept for 93 days. **During this time, users will no longer be able to access any shared content in the
> OneDrive.**" — [OneDrive retention and deletion](https://learn.microsoft.com/en-us/sharepoint/retention-and-deletion)

> "**12. What happens to external sharing links and Teams file references once an unlicensed OneDrive is
> deleted?** Answer: Once a OneDrive is deleted, content associated with that OneDrive can no longer be
> accessed." — unlicensed-onedrive-accounts, FAQ 12

### Transfer breaks links, and how badly depends entirely on the mechanism

From `onedrive-mechanisms.md` §3, which is the most decision-relevant table in the whole research set:

| Mechanism | Sharing links | Permissions | Version history |
|---|---|---|---|
| Grant site collection admin (A3) | Untouched | Untouched, one admin added | Untouched |
| Leave under retention (B) | Keep working while retention runs | Untouched | Preserved; version limits ignored |
| Web UI "Move to" / Simplified file transfer | Old URLs break; "Keep sharing with the same people" preserves the grantees | Preserved if that option is ticked | "the history of the document is copied to the new destination" |
| Admin center "Create link" then Move/Copy | Old URLs break | Not stated | "only the latest version is moved" |
| **Graph copy** | Old URLs break | **Destroyed** | Latest only, unless `includeAllVersionHistory: true` |

The Graph row is the one that must not be glossed over, verbatim from
[driveItem: copy](https://learn.microsoft.com/en-us/graph/api/driveitem-copy?view=graph-rest-1.0):

> "Permissions are not retained when a driveItem is copied. The copied driveItem inherits the permissions of the
> destination folder."

> "Metadata isn't retained when a driveItem is copied, including system metadata and custom metadata. An
> entirely new driveItem is created in the target location instead."

**A "transfer the OneDrive" feature built on Graph copy will report success, produce every file, and silently
revoke every collaborator's access.** That is the worst failure mode available in this whole design space, it
is invisible in the operation log, and it is precisely the class of silent failure the codebase's own house
style calls out as the primary risk (`app-architecture.md` §6: *"a missing section is indistinguishable from a
bug"*). This alone disqualifies Graph copy as the default transfer mechanism.

Note also that nothing in the same-tenant toolkit leaves a redirect behind. Only cross-tenant migration does,
and that is a paid add-on for mergers and divestitures that does not apply here (`onedrive-mechanisms.md` §N).

### The framing that actually resolves this criterion

The user's stated fear is "a link that breaks silently weeks later". Both options break links. The difference is
**who chooses the day**:

- **Transfer breaks links once, on a date you pick, in an operation you can announce.** The web-UI move even
  emails the collaborators — verbatim, *"Collaborators will get one email when six or more files are moved"*
  ([Simplified file transfer](https://support.microsoft.com/en-us/onedrive/simplified-file-transfer-for-departing-employees)).
  *(The first draft rendered this as "one consolidated notification when six or more files move"; corrected to
  the page's own wording. Note also that this notification belongs to the **manager-driven web move**, which is
  the one mechanism in this document the app cannot invoke — it is not available to any code we write.)*
- **Retention breaks links on day 60, or day 93, or at the end of a retention period nobody has in a calendar,
  with no notification to anyone.**

A scheduled, announced break is strictly better than an unscheduled, silent one. **Criterion 2 verdict:
retention wins on paper and loses in practice** — but only if the transfer uses a mechanism that preserves
grantees. With Graph copy, retention wins outright.

---

## 3. Discoverability for the manager, months later

The ask names this specifically: *"access to a deleted user's OneDrive URL is not something anyone remembers."*
The evidence agrees, emphatically.

**A3 (grant access) is the worst possible answer on this criterion.** From `onedrive-mechanisms.md` §C:

> "They browse to `https://<tenant>-my.sharepoint.com/personal/<user>_<domain>_com` and get full control of that
> library. It does **not** appear inside their own OneDrive; it is a separate URL they must keep."

The manager's only durable record of that URL is an automated email. And that email has three preconditions,
each of which fails silently:

1. **Automatic access delegation must be enabled** on the classic User Profile Service page. `UNVERIFIED:` no
   supported API to read or set it was found (`onedrive-mechanisms.md` §A). The app cannot check it today.
2. **The manager attribute must be populated in Entra ID**, or a secondary owner configured. *"If access
   delegation is disabled or a manager or secondary owner isn't set for a user, no one has automatic access when
   the user is deleted or be warned that the OneDrive will be deleted."* (retention-and-deletion)
3. **The Entra account must be deleted.** *"The OneDrive retention period for cleanup of OneDrive begins when a
   user account is deleted from Microsoft Entra ID. No other action causes the cleanup process to occur,
   including blocking the user from signing in or removing the user's license."* (retention-and-deletion)

Condition 3 is the sting. **A terminate scenario that disables the account rather than deleting it — which is
what this app's seeded Terminate User scenario does (`ScenarioStore.SeedDefaults`, `app-architecture.md` §3.7)
— sends nobody any email at all.** The manager is never told the OneDrive exists, never told they have access,
and never told it is decaying.

`UNVERIFIED:` whether a site-collection-admin grant makes the leaver's content appear in the manager's Microsoft
Search or Delve results. Nothing in the fetched docs says it does, and the "separate URL they must keep" wording
implies not. *Test:* grant the admin role on a scratch OneDrive, wait for indexing, and search for a
distinctively-named file from the grantee's account.

**A1 (into the manager's OneDrive)** is the Google-parity answer and is genuinely good here: the files appear in
the manager's own file list and their own search, which is exactly the containment behaviour worth copying
(`google-drive.md` §6: *"the receiver gets one clearly-labelled, self-contained folder at the top of their own
My Drive"*).

**A2 (into a SharePoint site)** is slightly worse on day one — the manager has to know which site — and
decisively better at the twelve-month horizon, because it is indexed, browsable by the whole team, and it does
not evaporate when the manager themselves leaves.

**Criterion 3 verdict: transfer wins, and A2 wins the long horizon.** Retention/grant-access loses badly, and in
the app's most likely configuration (disable, don't delete) it does not even produce the email.

---

## 4. Legal, compliance and eDiscovery

Retention is the *purpose-built* answer here and it earns real credit, with two sharp caveats.

**What retention genuinely gives you:** content stays discoverable by Content Search and eDiscovery for the
retention period (retention-policies-sharepoint, quoted in §2 above), and this survives archiving:

> "Microsoft Purview eDiscovery and Content Search are still discoverable in archived content. Exporting the
> content that's supporting the search results doesn't require manual reactivation of the archived account, and
> it takes up to 24 hours to complete." — unlicensed-onedrive-accounts

**Caveat 1 — it expires at day 275**, per the same page's timeline: *"Day 275 | The account is no longer
available in eDiscovery."* So the eDiscovery guarantee has a nine-month shelf life on an unpaid account,
notwithstanding the sentence above about archived content being discoverable.

**Caveat 2 — the compliance dashboard lies after the data is gone.** This is the most alarming sentence in the
entire research set and it is first-party:

> "If deletion occurs, eDiscovery hold records remain, but held OneDrive content becomes unrecoverable. **Data
> lifecycle management retention policy or label assignments can still appear healthy, but the target site no
> longer exists.** Existing case exports are unaffected. New searches return no content from the deleted
> location." — unlicensed-onedrive-accounts

An organisation relying on B3 will see a green retention policy covering a leaver whose files were deleted
months earlier, and will only discover it when a matter actually needs the content. That is a worse outcome than
having no policy at all, because no policy at least does not create false confidence.

**Caveat 3 — the 93 days in the recycle bin are storage, not discoverability, and the first draft counted them
as if they were both.** This document's §5 baseline computes "~123 days" as 30 days of retention plus 93 days
in the site collection recycle bin. Those 93 days are real for *restoring the site*, and worth nothing for
eDiscovery. The retention-and-deletion page says so on the same step:

> "The Recycle Bin isn't indexed and therefore searches don't find content there. This means that **an
> eDiscovery hold can't locate any content in the Recycle Bin in order to hold it.**"
> — [OneDrive retention and deletion](https://learn.microsoft.com/en-us/sharepoint/retention-and-deletion)

So the honest compliance window on baseline 2 is the **retention period**, not retention plus recycle bin.

**And the counterweight, because this section should not read as "retention never works".** On the *normal*
deletion path — a deleted identity, billing in order — a hold really does stop the process, from the same page:

> "if a OneDrive is put on hold as part of an eDiscovery case, managers and secondary owners are sent email
> about the pending OneDrive deletion, but **the OneDrive won't be deleted until the hold is removed**."

The retention-and-deletion page also states the honouring order explicitly, and it is worth writing down
because it is the whole argument in four lines — *"This process honors retention mechanisms in the following
order **when billing is enabled** for unlicensed OneDrive accounts: 1. OneDrive retention period 2. Retention
policies 3. Legal holds"* (unlicensed-onedrive-accounts). Every word of that guarantee is conditioned on
**"when billing is enabled"**. Retention works when someone is paying. It fails silently when nobody is.

**What transfer gives you on this criterion is structural, and it is underrated.** Purview's own contrast is the
argument:

> "**SharePoint**: When a user leaves your organization, any content created by that user isn't affected because
> SharePoint is considered a collaborative environment, unlike a user's mailbox or OneDrive account."
> — retention-policies-sharepoint

Content in a SharePoint site is not coupled to any individual's identity, licence, or unlicensed clock. Moving
business records out of personal storage and into org-owned storage makes the compliance problem *go away*
rather than requiring a policy to hold it back. **Microsoft says this outright, twice, on the unlicensed page:**

> "we recommend admins move any actively needed content to a SharePoint site or an active and licensed OneDrive
> account."

> "2. Moving any actively needed content to a SharePoint site or an active and licensed OneDrive account."
> — unlicensed-onedrive-accounts (assign-license section and FAQ 11)

**The one thing transfer must never do: move content that is under an active legal hold or belongs to a live
matter.** Holds block cross-tenant migration outright (`onedrive-mechanisms.md` §J), and while a same-tenant
move of held content is not documented as blocked, moving evidence during a matter is a decision for whoever
owns legal, not for an offboarding tool. `UNVERIFIED:` whether the app can detect an active hold on a OneDrive
from a delegated context at all. *Test:* place a scratch OneDrive under an eDiscovery hold and see whether any
delegated Graph or PnP read surfaces the hold state.

**Criterion 4 verdict: split, and both are needed.** Retention is the right tool for the compliance question and
transfer does not replace it. But retention on an unpaid account is a nine-to-twelve-month guarantee that
reports itself as healthy after it has failed, so it cannot be the *only* answer either. Under a hold, neither
option applies — freeze and escalate.

---

## 5. The do-nothing baseline

The ask requires this explicitly. There are two do-nothing baselines and they behave differently, which almost
nobody realises.

### Baseline 1 — account disabled and/or unlicensed, but NOT deleted in Entra

This is what the app's terminate scenario produces today.

| Day | What happens | Who is told |
|---|---|---|
| 1 | Unlicensed clock starts. **The OneDrive deletion clock does not start.** | Nobody |
| 60 | Read-only. Content inaccessible to admins and users. | Nobody |
| 93 | Archived. Needs reactivation (up to 24 h) plus billing to read. | Nobody |
| 275 | Out of eDiscovery. | Nobody |
| 365 | Deletion risk — "even if retention policies, settings, or holds exist". | Nobody |

**Time to permanent loss: ~365 days. Time to unusable: 60 days. Notifications sent: zero**, because access
delegation fires on Entra deletion only.

### Baseline 2 — account deleted in Entra

| Stage | Duration | Source |
|---|---|---|
| OneDrive retention period | 30 days default, settable 30–3650 | [set-retention](https://learn.microsoft.com/en-us/sharepoint/set-retention) |
| Manager emailed that they have access and it will be deleted | at the start | retention-and-deletion |
| Reminder email | 7 days before expiry | retention-and-deletion |
| Site collection recycle bin | 93 days, PowerShell-only restore — **and not indexed, so eDiscovery cannot reach it** (§4, caveat 3) | retention-and-deletion |

**Time to permanent loss: ~123 days. Time to loss of discoverability: ~30 days** — the retention period only,
because the recycle-bin 93 aren't searchable.

**And lengthening the retention period does not buy proportionally more.** The "settable 30–3650" in the first
row invites exactly the wrong move. The same page's note on step 3 caps it:

> "Unlicensed OneDrive accounts get automatically archived on their 93rd unlicensed day, **even if the account
> is retained for longer than 93 days due to the retention period**. When a user account is deleted from
> Microsoft Entra ID, the OneDrive account is considered unlicensed."

So `Set-SPOTenant -OrphanedPersonalSitesRetentionPeriod 365` gives roughly 60 read-write days, 33 read-only
days, then an archived site needing a paid reactivation — with the 365 governing only when it is finally
recycled. **Proposed default #4 below must therefore print the archive date alongside the retention date**, or
it will hand the operator a comfortable number that is wrong by a factor of four.

But somebody is actually told — twice — provided access delegation is on and the manager attribute was
populated.

### The perverse conclusion

**The gentler action gives a longer window and notifies nobody. The harsher action gives a much shorter window
and is the only one that emails the manager.** An operator who disables rather than deletes, believing they are
being cautious, has in fact chosen the variant where the manager learns nothing and the content becomes
unreachable in 60 days.

This is the strongest single argument that the app must take an explicit, visible action at termination time
rather than relying on tenant defaults. Whichever option the organisation picks, **the do-nothing baseline is
not it**, and the current terminate scenario is the do-nothing baseline.

---

## 6. Reversibility

| Action | Reversible? | Window and cost |
|---|---|---|
| Grant site collection admin (A3) | **Yes, fully, both directions** | Instant; documented as "Revoke admin access to a user's OneDrive" |
| Change the tenant OneDrive retention period | Yes as a setting | Effect on an already-expiring OneDrive `UNVERIFIED:` (`onedrive-mechanisms.md` §B) |
| **Copy** files out | Yes — delete the copy | Additive; nothing is lost at source |
| **Move** files out | **No**, except by moving back | Links broken twice; version history already degraded by the first move |
| Graph copy's permission destruction | **No, unless snapshotted first** | Original grants are unrecorded anywhere once the source is gone |
| Restore from site collection recycle bin | Yes | 93 days, SharePoint Administrator, PowerShell only |
| Reactivate an archived account | Yes | ≤24 h, requires billing, then re-archives after 30 days — **extendable per account** with `Set-SPOSiteArchiveState -KeepActiveUntil <date>` if Standard storage is enabled (§1) |
| Assign a licence to an archived account **with** an owner | Yes | Auto-reactivates in 24–48 h, **no reactivation fee** (FAQ 11) |
| Assign a licence to an archived account **without** an owner | **No, not directly** | Identity deleted → must enable the meter, move content out, then fix the site user ID mismatch (FAQ 11) |
| `Remove-SPODeletedSite` | **No** | Permanent |
| Day-365 unpaid deletion | **No** | Permanent, holds notwithstanding |
| Passing day 275 | **No** | Out of eDiscovery |

Two asymmetries drive the recommendation:

1. **Granting access is free and perfectly reversible. Moving files is neither.** That argues for
   grant-first-move-later as the sequencing, always.
2. **Deleting the Entra identity destroys the cheap recovery path.** With an owner, relicensing auto-reactivates
   for free. Without one, you are into meter-enabling and site-user-ID-mismatch repair. **Whatever else the
   terminate scenario does, deleting the cloud identity should be the last thing it does, and ideally a separate
   decision on a separate day.**

---

## 7. Volume — and yes, it changes the answer

Transfer's cost is proportional to size. Retention's is proportional to size *and* time. But the thing that
actually changes the answer is that transfer has **hard ceilings**, not just slow paths.

From [SharePoint limits](https://learn.microsoft.com/en-us/office365/servicedescriptions/sharepoint-online-service-description/sharepoint-online-limits)
and the copy reference:

- **100 GB total and 30,000 files** per cross-site move or copy. `driveItem: copy` restates it: *"The copy
  operation is restricted to 30,000 driveItems."*
- **250 GB** per-file upload; **2 GB** for OneNote; **15 GB** cross-geo.
- **400 characters** for the entire decoded file path. Nesting the source tree under a `Files from <name>/`
  prefix — which is exactly the Google-style containment folder worth copying — **lengthens every path** and can
  push previously-legal files over the limit.
- **100 GB/hour egress per user** ([throttling guidance](https://learn.microsoft.com/en-us/sharepoint/dev/general-development/how-to-avoid-getting-throttled-or-blocked-in-sharepoint-online)).
  A 200 GB OneDrive cannot be copied in an hour regardless of how the code is written.
- **3,000 requests per 5 minutes per user**; list operations cost 2 resource units, and **permission operations
  cost 5** — so a transfer that tries to preserve sharing is the most expensive kind per item.
- Microsoft names this exact workload as a throttling cause: *"Failing to follow best practices for scanning
  applications that process files in bulk will likely result in throttling."*

Google's analogue fails the same way and admits it: *"If a transfer takes longer than 36 hours, it's
unsuccessful and will stop."* (`google-drive.md` §1).

**The threshold, and what changes above it.** Above roughly **100 GB or 30,000 items**, "transfer the OneDrive"
stops being an operation and becomes a project — multiple chunked operations, throttling management, and a real
chance of partial completion. At that size the correct answer changes shape:

> **Do not transfer the whole OneDrive. Transfer the files the business needs, and let the rest expire under
> whatever retention the organisation has chosen.**

That is not a compromise, it is what Microsoft's own Google-parity feature does by design: Simplified file
transfer has the manager *"filter to shared or important files"* before selecting. Google transfers everything
because ownership transfer is a metadata rewrite that costs nothing; Microsoft moves bytes, so transferring
everything is actively wrong at scale (`onedrive-mechanisms.md` §0, §F).

One more volume effect, from Google's model and equally true here: **storage follows the files.** Dropping
300 GB into a manager's OneDrive consumes the manager's quota. A SharePoint site draws on tenant pooled storage
instead, which usually has more headroom — another point for A2 over A1. Note that unlicensed OneDrives
explicitly *cannot* borrow that pooled headroom: *"Unlicensed OneDrive accounts can't utilize unused SharePoint
storage quota"* (FAQ 5).

**Criterion 7 verdict: at small volumes transfer is easy and clearly right. At large volumes, selective transfer
plus retention on the residue is the only workable answer, and "transfer everything" is a trap.**

---

## Recommendation

**Transfer the files that matter, to an organisation-owned SharePoint location by default, and treat retention
as a parallel compliance control on the residue rather than as an alternative.** Then, separately and later,
delete the identity.

The reasoning, in the order that decided it:

1. **Retention is not a resting state, and its failure mode is silent.** It decays to unusable at day 60,
   archived at 93, undiscoverable at 275, and deleted at 365 regardless of holds if unpaid — while the
   compliance dashboard continues to report the policy as healthy. The only way to stop the decay is to keep
   paying, either a permanent seat per leaver (B1) or a tenant-wide meter whose first bill is the entire
   existing backlog (B2). *(§1, §4)*
2. **Transfer costs nothing ongoing and reclaims the seat on day one.** Its only hard constraint is a 60-day
   deadline that a same-week action trivially satisfies. *(§1)*
3. **Discoverability is not close.** A URL in an automated email is not a filing system, and in this app's most
   likely configuration — disable rather than delete — that email is never sent at all. *(§3)*
4. **Retention's genuine advantage, unbroken sharing links, expires on the same clock as everything else.** Both
   options break links eventually; transfer breaks them once, on a chosen day, with notification available.
   *(§2)*
5. **The do-nothing baseline is bad in both variants**, and the app's current terminate scenario *is* the
   do-nothing baseline. *(§5)*
6. **Microsoft recommends exactly this**, twice on the unlicensed-accounts page: move actively needed content to
   a SharePoint site or an active licensed OneDrive. Purview gives the reason — SharePoint content is unaffected
   when a user leaves, because it is a collaborative environment rather than personal storage. *(§4)*

**Default destination should be a SharePoint site, not the manager's OneDrive**, even though the manager's
OneDrive is what the ask asked for. Moving a leaver's files into another individual's personal storage solves
this offboarding and schedules the identical problem for the manager's own departure. Google's structural lesson
is precisely this: an organisation that has already moved its work into shared drives *"has almost nothing to
transfer at offboarding"* (`google-drive.md` §1). Offer the manager's OneDrive as the explicit fallback when
there is no sensible site — do not make it the default.

**Two things this recommendation does not say.**

It does not say retention is wrong. Retention is the correct and irreplaceable tool for the compliance question,
and the organisation should have a OneDrive retention policy independent of this feature. It says retention
cannot carry the *business continuity* question, which is what a terminate scenario is actually for.

**And it does not say the app should perform the transfer.** That is the uncomfortable part, and it is the next
section.

---

## What the app should actually do — and what it should refuse to do

The architecture constrains this more than the policy does. From `app-architecture.md` §4.3, stated bluntly
there and repeated here because it is decisive:

> "Any OneDrive design whose core operation is 'copy/move N GB of files' or 'run a server-side job to
> completion' has **no home in this app as built**. […] **'leave the files in place and re-permission them' fits
> this architecture; 'move the files somewhere else' does not.**"

Concretely: `ScenarioRunner` is synchronous per target (`ScenarioRunner.cs:41-70`), the pwsh channel's ceiling
is 180 s and a timeout **kills the host process and takes every other Exchange feature down with it**
(`ExchangeService.cs:1044-1060`), and there is no job store, no queue, and nothing that survives the window that
started it.

Meanwhile the *good* transfer mechanism — Simplified file transfer, the one that preserves sharing — has **no
admin API, no cmdlet and no Graph endpoint** (`onedrive-mechanisms.md` §F). It is manager-driven, in a browser.
And the only programmatic cross-drive primitive, Graph copy, destroys every permission.

So the recommendation for the app is:

> **The app should set up, verify and document the transfer. It should not perform the file move.**

That is not a retreat. Reimplementing the move over Graph copy would produce a feature that reports success
while silently revoking every collaborator's access — measurably worse than doing nothing.

### Proposed app defaults

| # | Behaviour | Rationale |
|---|---|---|
| 1 | **Default terminate action: grant a named person site-collection-admin access to the leaver's OneDrive, record the URL, move nothing.** | Fast, bounded, fully reversible, fits the 90 s budget, and is the precondition for every other option |
| 2 | **Default the grantee to the Entra manager, but require an explicit confirmation of who it is.** | Manager may be unset — the most common silent failure (`onedrive-mechanisms.md` §A) |
| 3 | **Never write "files transferred" in the operation log unless bytes actually moved.** Write "access granted, URL recorded". | The log is a termination audit trail; `api-and-auth.md` §5.1 makes the same point about "copy started" vs "transferred" |
| 4 | **Log the OneDrive URL, storage used, item count, and both computed clock dates** (read-only day, archive day) — **and describe the deletion milestone in prose, never as a date**, because it runs on cumulative unpaid days (§1a). If the tenant retention period exceeds 93 days, log the archive date *and* the retention date and say which one bites first. | The operation log is already the app's re-add mechanism (`app-architecture.md` §3.4). This makes the OneDrive re-findable in six months, which is the entire discoverability problem. **But see the note under #3 — the log is not written at all unless the scenario has a `SaveOperationLog` step, and the seeded Terminate User scenario does not** |
| 5 | **A licence-removal guardrail modelled on the existing mailbox one** — see the correction below, because the existing one does not fire where this document implied it would. Wording: "⚠ This user's OneDrive (N GB, M items) becomes READ-ONLY in 60 days and is ARCHIVED in 93. There is no OneDrive equivalent of converting a mailbox to shared. Move anything the business needs before then." | Same shape the codebase already uses: real state of the dependent resource, concrete consequence, timeframe, named remedy, degrades to silence rather than blocking |
| 6 | **Do not delete the Entra identity in the same run**, and warn loudly if the operator sequences it that way. | Deleting the identity destroys the free relicensing recovery path (§6) and starts the 30-day clock |
| 7 | **Report the tenant's retention period and access-delegation state; never set them.** | Both are tenant-wide with no per-user override; access delegation has no known API (`UNVERIFIED:`) |
| 8 | **Close the silent-default arms in all three switches** (`ScenarioRunner.RunStepAsync`, `ScenarioActionToLabelConverter`, `DescribeStep`). | Today an enum member without a runner case "runs, does nothing, and is logged with a ✓" (`app-architecture.md` §3.5). For a step whose job is preserving a departing employee's files, that is unacceptable |
| 9 | **If a file move is ever built:** snapshot permissions to the log *before* copying, set `includeAllVersionHistory: true`, never pass `name` alongside it, set `conflictBehavior=rename` — **never `replace`** — and always read the **terminal** monitor state. Create the "Files from <name>" containment folder as a real destination *first*; with `childrenOnly: true` the `name` parameter is rejected outright (`400 invalidRequest`, "Cannot use name parameter alongside childrenOnly"), and `conflictBehavior=replace` fails whenever any child is a folder. | `driveitem-copy` known issues + Examples 5 and 9, re-verified 2026-09-01; a step that only checks the 202 will report success on a failed copy (`api-and-auth.md` §5) |
| 10 | **Refuse to act, loudly, if an active hold is detected** — and say so if hold state could not be determined. | Three outcomes, not two: held / not held / not checked (`app-architecture.md` §2.6, plan gotcha G13) |

### Four corrections to those defaults, from the 2026-09-01 verification pass against the repo at `7423bcb`

**(i) Default #1 names the wrong cmdlet, and so does `approaches.md` — differently.** This document says
`Set-SPOUser -IsSiteCollectionAdmin`; `approaches.md` A1 says `Set-PnPTenantSite -Owners`. **Microsoft's own
instructions name no cmdlet at all** — they are a SharePoint admin center walkthrough (More features → User
profiles → Manage User Profiles → right-click → *Manage site collection owners* → add to *Site collection
administrators*), on the same classic page as "Enable access delegation"
([remove-former-employee-step-5](https://learn.microsoft.com/en-us/microsoft-365/admin/add-users/remove-former-employee-step-5)).
`Set-SPOUser` has better provenance but ships in the SharePoint Online Management Shell, and
`Connect-SPOService` has **no `-AccessToken` parameter** — so it cannot borrow the app's Entra token the way
`ExchangeOnlineManagement` does. **`Set-PnPTenantSite -Owners` is the only candidate compatible with this
app's single-sign-in, no-secret model**, and it is `UNVERIFIED` against a `/personal/…` URL. `approaches.md`
A1 now carries the reconciled table; this document defers to it.

**(ii) Default #5's guardrail exists, but not where a termination would meet it.** `AddMailboxGuardrailAsync`
is real — declared at `CloudObjectDetailViewModel.cs:908`, warning text at `:918-921` — and its wording is the
right model to copy. But two things bound it:

- It is called from exactly one place: `await AddMailboxGuardrailAsync(row, lines);` at `:889`, inside the
  cloud pane's `RemoveLicensesAsync` command (declared `:863`). It is not reachable from a scenario run.
- **There is no licence action in `ScenarioActionType` at all** (`Models/Scenario.cs:4-63` — Disable/Enable/
  Unlock/groups/attributes/MoveToOu, six Cloud\* actions, five Exchange\* actions, `SaveOperationLog`). A
  terminate scenario **cannot remove a licence**, so a scenario-time OneDrive-clock warning has nothing to
  hang off.

And the practical point that follows: **most licences are removed somewhere else entirely** — the Microsoft
365 admin center, a group-based-licensing change, or an HR-driven automation — where this app cannot warn
about anything. So the OneDrive clock warning has to appear in **at least two places**: the cloud pane's
remove-licence confirm (alongside the mailbox one), and the terminate scenario's confirm dialog as a
*standing* caution whenever a OneDrive step is present, regardless of whether the run itself touches a licence.
Treating it as one guardrail in one place will miss the common case.

**(iii) Defaults #3 and #4 assume an operation log that is off by default.**
`MainViewModel.RunScenarioAsync` computes `wantsLog` from the presence of a `SaveOperationLog` step (`:1042`)
and only allocates the log when a Save-As path was chosen (`:1113`); without that step the runner's
`operationLog?.Add(…)` calls all no-op and no file is offered. **The seeded Terminate User scenario has no such
step** (`ScenarioStore.SeedDefaults:105-111`). So "the log is the receipt" is aspirational until the seed is
changed. Add it to the seed as part of this work, and make the OneDrive step's caution line say what is lost
without it — the same `wantsLog` conditional the remove-all-groups cautions already use (`:1043-1051`).

**(iv) Scenario steps can only reach on-prem AD objects, which bounds every default above.**
`ScenarioRunner.RunAsync` takes `IReadOnlyList<AdObjectRow>` (`:22-27`), and every cloud/Exchange step resolves
its twin from the on-prem `userPrincipalName` (`ResolveMailboxIdentityAsync` `:384-393`,
`ResolveCloudUserIdAsync` `:416-424`). **A cloud-only leaver cannot be terminated by a scenario at all**, and
a tenant using alternate login IDs will resolve the wrong UPN. For a mailbox that is an annoyance; for a file
store it is a data-exposure incident. The step must resolve the Entra user and log the resolved cloud UPN and
drive URL **before** it grants anything.

**And one whole consideration neither document raised: Teams chat files.** Files the leaver posted into Teams
1:1 and group chats — plus recordings and transcripts of meetings they organised outside a channel — are
stored in **their** OneDrive, per Purview: *"for files and Teams meeting recordings and transcripts from user
chats, you need a retention policy that includes the **organizer's OneDrive account** as the location"*
(https://learn.microsoft.com/en-us/purview/retention-policies-teams). A Graph copy duplicates the bytes and
leaves every chat reference pointing at the source, so a "successful" transfer does not save them; a
site-collection-admin grant preserves all of them by doing nothing. This is a point **for** the grant-first
sequencing this document already recommends, and it widens the blast radius of a termination from "the
manager" to "everyone the leaver ever sent a file to". See `approaches.md` §3.5 for the full treatment, and
add one sentence about it to the confirm dialog.

Prerequisite to all of it, and it is not small: **the signed-in admin cannot read another user's OneDrive at all
until they have been made site collection admin on it.** SharePoint Administrator is necessary but not
sufficient — *"Global Administrators and SharePoint Administrators don't have automatic access to all sites and
each user's OneDrive"* ([sharepoint-admin-role](https://learn.microsoft.com/en-us/sharepoint/sharepoint-admin-role)).
That grant needs the SharePoint admin surface, which Graph cannot reach, which means the PnP-over-borrowed-token
route in `api-and-auth.md` §3.2. **The 403 test (`api-and-auth.md` §8, experiment 1) should be run before any of
this is designed** — it is the cheapest experiment available and the shape of the whole feature depends on it.

---

## What is policy, not product

These are decisions the organisation must make. The app's job is to surface them and record the answer, never to
choose silently.

| # | Question | Owner | What the app should do |
|---|---|---|---|
| P1 | **How long must a leaver's files remain retrievable?** | Legal / records management | Nothing. This determines everything else and cannot be inferred |
| P2 | **Do we enable unlicensed-OneDrive billing?** Pull the current unlicensed count and total GB from SharePoint admin center → Reports → OneDrive accounts first — the first month's bill is the whole backlog | Whoever owns the M365 bill | Report the per-user consequence and the clock dates; never enable anything |
| P3 | **Default transfer destination: SharePoint site or manager's OneDrive?** | IT + the business | Prompt every time. **No silent default** — this is the decision that determines whether the problem recurs |
| P4 | **Is automatic access delegation on, and is a secondary owner set?** | SharePoint admin | Check and warn if it can; `UNVERIFIED:` whether any API exposes it. Until proven otherwise, a documented manual tenant prerequisite |
| P5 | **Tenant OneDrive retention period (30–3650 days)** | IT + legal | Read and display; never set. Tenant-wide, no per-user override |
| P6 | **Do we extend the unlicensed active-storage period past 93 days?** (GA October 2026, needs pay-as-you-go, tenant-wide) | Whoever owns the bill | Report only. Microsoft's own advice is to leave it at 93 |
| P7 | **Do we delete terminated identities, and when?** | IT + HR | Make both clocks visible at confirm time; default to *not* deleting in the same run |
| P8 | **Is there a hold or live matter on this person?** | Legal | Refuse to act if detectable; state plainly when it could not be checked |
| P9 | **Is a Purview retention policy scoped to OneDrive at all today?** | Compliance | Report if reachable; never author policies. Note that an improperly configured policy can *accelerate* deletion — retention settings "always take precedence" over the standard process, in both directions |

---

## UNVERIFIED, carried forward

Not hardened, not resolved. Items 1–8 are from the briefs; 9–11 are new to this pass.

1. Whether any first-party doc states there is no inactive-OneDrive equivalent, or whether it is only an
   argument from absence. *Test:* support case.
2. Whether lengthening `OrphanedPersonalSitesRetentionPeriod` rescues an already-expiring OneDrive.
3. Whether "Enable access delegation" and the secondary owner are readable or settable by any supported API.
4. Whether Simplified file transfer preserves full version history.
5. Whether Microsoft 365 Backup can restore a OneDrive whose Entra owner has been deleted, and to a new URL.
6. Exact Purview SKU required for the retention configuration in question.
7. Whether the app's public-client registration can mint a `<tenant>-admin.sharepoint.com/.default` delegated
   token, and whether PnP accepts it for site-collection-admin cmdlets.
8. ~~Exact clock semantics of "After seven days" in deletion-process step 6.~~ **Largely resolved on re-read.**
   Steps 5 and 6 are consecutive and describe the same seven days: step 5 is *"Seven days before the OneDrive
   retention period expires, a second email is sent…"*; step 6's *"After seven days"* is therefore *after
   those seven days*, i.e. at the expiry of the retention period. The 30 + 93 = ~123-day baseline in §5 stands.
   Residual doubt is only about job latency, not about which clock is meant.
9. **Whether a site-collection-admin grant makes the leaver's content discoverable in the grantee's Microsoft
   Search / Delve.** *Test:* grant, wait for indexing, search for a distinctively-named file as the grantee.
   Bears directly on §3 — if it does, A3 is less bad than assessed.
10. **Whether a *copy out* still works between day 60 (read-only) and day 93 (archived).** *Partly resolved.*
    The awkwardness dissolves once the full sentence is read: content is inaccessible *"unless an admin takes a
    supported action such as assigning a license, enabling applicable billing, reactivating archived content,
    or moving content that must be retained"*, and the page then lists a PowerShell unlock
    (`Set-SPOSite -LockState Unlock`) as one of two admin options for a read-only account. So the two
    statements are consistent: **read-only is a wall for the grantee and a speed bump for a SharePoint
    Administrator.** What is still open, and is now item 12: whether enforcement re-applies the lock.
    Meanwhile the operational advice is unchanged — **treat day 60 as the deadline you tell people about.**
11. **Whether an active eDiscovery hold on a OneDrive is visible from a delegated context.** Determines whether
    default #10 above is implementable or merely aspirational.
12. **New (verification pass):** whether the unlicensed-enforcement job **re-applies** read-only after an admin
    unlocks the site. *Test:* let a scratch account pass day 60, `Set-SPOSite -LockState Unlock`, wait a full
    enforcement cycle, re-check. Decides whether item 10's "speed bump" reading is durable or one-shot.
13. **New (verification pass):** whether a *standalone* or low-cost SKU that still carries a OneDrive service
    plan is purchasable in this tenant, and at what price. Decides whether B1 is "a full seat forever" or a
    cheap parking brake. *Test:* Microsoft 365 admin center → Billing → Your products; check which owned SKUs
    include a OneDrive service plan and how many spare seats exist. **Deliberately not asserted above.**
14. **New (verification pass):** whether `Get-SPOTenant`'s `UnlicensedOneDriveAccountsActiveStoragePeriod`
    returning `0` can be distinguished from a genuine unset value. Microsoft says a `0` may just mean the
    endpoint lacks the server build. Gates whether default #7's report can honour the app's own
    three-outcomes rule (resolved / not set / not readable).
15. **New (verification pass):** whether Graph can cheaply count items under the leaver's
    `Microsoft Teams Chat Files` folder, and whether that folder name is stable. Only affects how precisely
    the log can state the Teams blast radius (see the Teams paragraph above). The *storage location* is
    first-party; the *folder name* is currently only in Microsoft Q&A community answers.

---

## Sources

Re-fetched and read in full for this pass, 2026-09-01:

- https://learn.microsoft.com/en-us/sharepoint/unlicensed-onedrive-accounts (ms.date 2026-08-26)
- https://learn.microsoft.com/en-us/sharepoint/standard-storage-unlicensed-onedrive-accounts (ms.date 2026-08-26) — **not covered by the earlier briefs**
- https://learn.microsoft.com/en-us/purview/retention-policies-sharepoint

Relied on via the four briefs in this folder, each of which cites the URL directly:

- https://learn.microsoft.com/en-us/sharepoint/retention-and-deletion
- https://learn.microsoft.com/en-us/sharepoint/set-retention
- https://learn.microsoft.com/en-us/sharepoint/sharepoint-admin-role
- https://learn.microsoft.com/en-us/sharepoint/restore-deleted-onedrive
- https://learn.microsoft.com/en-us/microsoft-365/admin/add-users/remove-former-employee-step-5
- https://support.microsoft.com/en-us/onedrive/simplified-file-transfer-for-departing-employees
- https://learn.microsoft.com/en-us/graph/api/driveitem-copy?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/driveitem-move?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/office365/servicedescriptions/sharepoint-online-service-description/sharepoint-online-limits
- https://learn.microsoft.com/en-us/sharepoint/dev/general-development/how-to-avoid-getting-throttled-or-blocked-in-sharepoint-online
- https://learn.microsoft.com/en-us/purview/inactive-mailboxes-in-office-365
- https://knowledge.workspace.google.com/admin/drive/transfer-drive-files-to-a-new-owner-as-an-admin

Repo evidence via `app-architecture.md`: `Services/ScenarioRunner.cs`, `Services/ExchangeService.cs`,
`Services/GraphService.cs`, `Models/Scenario.cs`, `ViewModels/CloudObjectDetailViewModel.cs`,
`Services/ScenarioStore.cs`.

Added by the **verification pass of 2026-09-01** — fetched and read in full, not inherited:

- https://learn.microsoft.com/en-us/sharepoint/standard-storage-unlicensed-onedrive-accounts (`ms.date`
  2026-08-26) — `-KeepActiveUntil`, the admin-center-only enablement, the build requirement, the 2026 fee waiver
- https://learn.microsoft.com/en-us/powershell/module/sharepoint-online/set-sposite — `-Owner` verbatim, and
  that `Owner`/`LockState` are among the few parameters valid on a OneDrive site collection
- https://learn.microsoft.com/en-us/purview/retention-policies-teams — Teams chat files live in the user's
  OneDrive; also confirms deleted users' *chat messages* survive in an **inactive mailbox**, which sharpens
  the Exchange/OneDrive asymmetry in §1
- https://learn.microsoft.com/en-us/microsoft-365/admin/add-users/remove-former-employee-step-5 — the grant
  walkthrough names **no cmdlet**; also "only the latest version is moved", confirmed verbatim
- https://pnp.github.io/powershell/cmdlets/Connect-PnPOnline.html and
  https://pnp.github.io/powershell/cmdlets/Set-PnPTenantSite.html — the auth-shape reconciliation in
  correction (i)

Repo code read **directly** for this pass, at commit `7423bcb` on `feature/issues-4-5`, rather than via
`app-architecture.md`: `Services/ScenarioRunner.cs` (whole file), `Models/Scenario.cs` (whole file),
`Services/ScenarioStore.cs:93-119`, `ViewModels/MainViewModel.cs:1030-1191`,
`ViewModels/CloudObjectDetailViewModel.cs:860-935`, `Services/ExchangeService.cs:1-140` and `:860-1300`.
