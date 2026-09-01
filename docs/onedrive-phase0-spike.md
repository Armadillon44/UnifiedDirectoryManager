# OneDrive Phase 0 spike

Status: **not run.** Findings go in this file as they come back.

Four questions that gate [`onedrive-offboarding-plan.md`](onedrive-offboarding-plan.md).

**This runs against the live production tenant.** There is no scratch tenant. Everything below is written on
that assumption: the read tests are harmless, the one write test is contained to a purpose-made test user and
is reversible, and the destructive tests that would have been acceptable on a throwaway tenant have been
**removed rather than adapted**.

Budget half a day, most of it waiting on provisioning and role propagation.

---

## Rules for a live tenant

Read these before running anything.

**Never run, at all:**

| Command | Why |
|---|---|
| `Set-SPOSite -Owner` on a OneDrive | Documented as producing an unsupported OneDrive state. It **succeeds**, which is what makes it dangerous. There is no version of "just to confirm the docs are right" that is worth this on a live tenant — the documentation is unambiguous and this spike takes it on faith |
| `Set-PnPTenantSite -PrimarySiteCollectionAdmin` | Same thing, one word away from the parameter we *do* want (`-Owners`) |
| Anything `Set-SPOTenant` | Tenant-wide. The retention period, the unlicensed-storage period and the billing toggles all affect every account in the organisation. This spike **reads** tenant settings and never writes one |
| Anything against a real departing user | Use the purpose-made test user. A real leaver's OneDrive is someone's only copy of things |

**Do not grant the new SharePoint permission to the production app registration yet.** Admin consent is
tenant-wide and applies to every user of the app. Use a **separate throwaway registration** for the spike
(step 0 below) and only touch the production one once the design is locked and the permission is known to be
the right one.

**Expect an audit trail.** The grant in U3 lands in the unified audit log as a site-collection-administrator
change. That is fine and worth knowing in advance so nobody is surprised by an alert.

---

## Step 0 — prerequisites, in this order

Each of these has bitten someone. The order matters.

### 0a. A throwaway app registration

A new Entra app registration, **public client / native**, redirect URI `http://localhost`, in the production
tenant. Configure it the way the real app is configured, then add the delegated SharePoint permission the
plan proposes — resource **Office 365 SharePoint Online** (`00000003-0000-0ff1-ce00-000000000000`),
`AllSites.FullControl` — and grant admin consent **on this registration only**.

Record its client id. It is needed for U2 and U12.

Delete it at the end (see Cleanup).

### 0b. The SharePoint Administrator role, assigned the day before

The role has to be held by **the human running the spike**, not granted to the app. Microsoft documents role
changes as taking **"about an hour"** to take effect
([sharepoint-admin-role](https://learn.microsoft.com/en-us/sharepoint/sharepoint-admin-role)).

If it is already held, note that too — "it worked" means nothing if you cannot say what role it worked with.

### 0c. A licensed test user

Needs a licence that **includes SharePoint**. Pre-provisioning requires it explicitly: *"The user accounts
that you're pre-provisioning must be allowed to sign in and must also have a SharePoint license assigned."*
([pre-provision-accounts](https://learn.microsoft.com/sharepoint/pre-provision-accounts))

This consumes a real seat for the duration. That is the unavoidable cost of not having a scratch tenant.

### 0d. **Provision the OneDrive, and confirm it exists**

**This is the step that decides whether U1 produces a real answer or a false one.**

> "Unless OneDrive accounts are pre-provisioned, the URL isn't created until a user accesses their OneDrive
> for the first time."
> — [View OneDrive URLs for users](https://learn.microsoft.com/sharepoint/list-onedrive-urls)

A test user created this morning has **no OneDrive**. A Graph call against them returns **404 — no drive** —
which looks a lot like a permissions failure if you are expecting one, and would falsely confirm Fact 2.

Simplest reliable method: **sign in as the test user in a browser and open OneDrive once.** Wait for it to
finish appearing. Then confirm the URL exists before going further:

```
https://<tenant>-my.sharepoint.com/personal/<upn with . @ and spaces replaced by _>
```

Do not proceed to U1 until you have loaded that URL successfully as the test user.

### 0e. Put realistic content in it

While signed in as the test user:

- Two or three files, including at least one **shared with a third person** — needed to see what a grant does
  and does not change about other people's access.
- One file with **three or more versions**, for the version-history question.
- One file posted into a **1:1 Teams chat** with someone else, which lands in `Microsoft Teams Chat Files` in
  this user's OneDrive. This is what makes U13 answerable and it is easiest to do now.

---

## U1 — does delegated Graph really 403 without a grant?

**Why it is first and why it runs alone.** The plan's Fact 2 says a SharePoint Administrator cannot read
another user's OneDrive until something grants access to that specific site, and that no Graph endpoint
performs the grant. That is an **inference joining two cited quotes**, not a single quotation. If it is
wrong — if a SharePoint Admin already has read access — the PnP channel may be unnecessary and the design
gets simpler and cheaper. Highest-value half-hour in the project.

Entirely read-only until step 3.

**Procedure**

1. **Confirm the drive exists at all**, as the test user or via the admin centre. Step 0d. If you skip this,
   the rest of U1 is uninterpretable.
2. As the SharePoint Admin, with **no** site-collection-admin grant on the test user's OneDrive:
   ```
   GET /v1.0/users/{testUpn}/drive
   GET /v1.0/users/{testUpn}/drive/root/children
   ```
   Graph Explorer is fine. Record the **exact HTTP status and the full error body** for each — the error code
   inside the body distinguishes the cases below, and the bare status does not.
3. Grant yourself site-collection admin on that OneDrive **through the SharePoint admin centre UI**, so the
   grant mechanism is not itself a variable at this stage. (U3 tests the programmatic grant separately.)
4. Repeat both calls. Record the statuses.
5. **Remove the grant** through the same UI.

**Outcomes**

| Result | Meaning |
|---|---|
| **403 then 200** | Fact 2 holds. Plan stands. Continue to U2 |
| **404 either time** | **Not an answer.** Either the OneDrive was never provisioned (step 0d) or the UPN is wrong. Fix and re-run — do not record this as a refusal |
| **200 both times** | **Fact 2 is wrong.** Delegated Graph alone may cover the read-only surface, possibly more. **Stop and re-plan** before building a PnP channel that may not be needed |
| **403 both times** | The UI grant did not take, or has not propagated. Confirm the grant landed in the admin centre, wait, retry. Do not conclude anything yet |

Also record **which scopes the token actually carried**. "It worked" is not a finding without knowing what it
worked with.

---

## U2 — can a public client mint a SharePoint token that PnP accepts?

Read-only. Uses the throwaway registration from 0a, not the production one.

1. Request a token for `https://<tenant>-admin.sharepoint.com/.default` using the **throwaway** client id,
   public client, PKCE. Note whether consent is prompted, and whether it asks for admin consent.
2. Decode it. Record `aud`, `scp`, `roles`, `appid`. The app already has a `LogTokenClaims` helper for this
   shape of check — reuse it rather than pasting a live token into a web decoder.
3. `Connect-PnPOnline -Url https://<tenant>-admin.sharepoint.com -AccessToken <token> -ClientId <throwaway>`
4. Run a tenant-admin **read**: `Get-PnPTenantSite -Identity <the test user's OneDrive URL>`.

**Record:** whether the mint succeeded, the exact `aud` and `scp`, whether PnP accepted it, and the verbatim
error if not.

**If this fails**, the fallback is a second interactive sign-in inside the child process — which breaks the
single-sign-in model rather than the no-secret model. Worth knowing early, because it changes what the
feature feels like to use.

---

## U3 — does `Set-PnPTenantSite -Owners` work against a personal site?

**The only write test in this spike.** Contained to the test user, reversible, and step 4 proves the
reversal actually works.

PnP's documentation does not say this works against a personal site. The whole grant (D1) rests on it.

1. Against the test user's OneDrive URL:
   ```
   Set-PnPTenantSite -Identity <personal site url> -Owners "<your admin upn>"
   ```
   **`-Owners`. Not `-PrimarySiteCollectionAdmin`.** Re-read that line before pressing enter.
2. Confirm independently — `Get-PnPSiteCollectionAdmin`, or the admin centre UI.
3. Open the URL as the grantee. Confirm the files are **actually reachable**, not merely that the cmdlet
   returned without error.
4. **Remove the grant and confirm access goes away.** Reversibility is a locked claim in D1 and has to be
   tested, not assumed.

**Also record, because it feeds straight into what the operation log must say:** what does the grantee
actually *see* on opening that URL? Is it obviously the leaver's OneDrive? Is it labelled with their name?
Would someone finding this URL in a ticket six months from now know what they were looking at?

And check the **third person's** access to the shared file from 0e, before and after. D1 claims a grant
changes nothing about existing sharing. Verify it.

---

## U12 — does PnP v3 accept `-AccessToken` without `-ClientId`?

The cmdlet reference says `-ClientId` becomes required from 9 September 2024 for the *interactive* parameter
sets. Whether the Access Token set is exempt is not stated.

1. On a machine with **no** `ENTRAID_CLIENT_ID` environment variable:
   ```
   Connect-PnPOnline -Url <tenant-admin url> -AccessToken <token from U2>
   ```
2. Repeat with `-ClientId <throwaway client id>`.

Record which forms work. If `-ClientId` is required, note whether an arbitrary registration is accepted or
whether PnP demands one of its own — the latter is a **deployment prerequisite for every customer** and
belongs in the README, not discovered during a rollout.

---

## Cheap extras, while you are set up

Each removes an UNVERIFIED from the plan. All read-only.

- **U5** — `Get-Help Set-PnPTenant -Parameter *`, look for `OrphanedPersonalSitesRetentionPeriod`. Gates
  whether D8's report can read the retention period at all. **Read the help; do not run the setter.**
- **U7** — is "Enable access delegation" readable? Try `Get-PnPUserProfileProperty`. Gates whether D8's
  report can ever be truthful about it.
- **U13** — enumerate the test user's drive root and confirm the Teams folder from 0e is named
  `Microsoft Teams Chat Files`, and whether its item count is cheap to read.
- **Storage and item count** — does `GET /users/{upn}/drive` return `quota.used` and a usable item count in
  one call, or does the read-only surface (D6) need a recursive enumeration? This decides whether that
  surface is instant or slow enough to need a progress indicator.
- **Tenant retention period** — read it via the admin centre or `Get-PnPTenant`. Just record the number;
  the app will report it and never set it.

---

## What cannot be answered without a scratch tenant

State these as permanently unverified rather than leaving them looking pending:

- **U11** — whether the unlicensed-enforcement job re-applies read-only after an admin unlocks a site.
  Requires letting an account pass **day 60 unlicensed**. Not doable on a live tenant on any sane timeline.
- **The decay curve itself** — days 60, 93, 275 and the deletion milestone. Documented, not testable here.
  The app must present these as Microsoft's published behaviour, never as something it has observed.
- **U8** — whether Simplified file transfer preserves full version history. Requires deleting a user and
  acting as their manager. Possible in principle with the test user, but deleting an account in production
  starts real clocks; not worth it for a question that only affects a feature we are not building.

---

## Cleanup

- [ ] U3's grant removed and the removal confirmed (step 4 of U3)
- [ ] Any grant made through the admin centre UI in U1 step 3 removed
- [ ] The throwaway app registration (0a) **deleted**
- [ ] The test user's licence reclaimed, or the user deleted, per your normal process
- [ ] SharePoint Administrator role left as it should be — if it was assigned only for this, remove it
- [ ] Nothing tenant-wide changed. If anything was, say so here loudly

---

## What "done" looks like

This file, filled in, with a verdict line per question and verbatim errors for anything that failed. Then
D1–D8 either stand or get revised, and step 1 of the sequencing can start.

**If U1 comes back 200/200, stop and re-plan** rather than continuing down the list — several of the
remaining questions stop mattering.

---

## Findings

*(fill in as you go — one section per question, verdict first, evidence under it)*

### U1 —
### U2 —
### U3 —
### U12 —
### Extras —
