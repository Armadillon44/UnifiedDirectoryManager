# OneDrive Phase 0 spike

Status: **not run.** Findings go in this file as they come back.

Four questions that gate [`onedrive-offboarding-plan.md`](onedrive-offboarding-plan.md). Needs a scratch
tenant, a signed-in account holding **SharePoint Administrator**, and a throwaway user with a provisioned
OneDrive containing at least one file. Half a day.

Run them **in order**. U1 comes first and alone.

> **Note on the admin role.** SharePoint Administrator or Global Administrator has to be *held by the human*,
> not granted to the app. Role changes take "about an hour" to take effect
> ([sharepoint-admin-role](https://learn.microsoft.com/en-us/sharepoint/sharepoint-admin-role)), so assign it
> the day before rather than losing an hour to a false negative on U1.

---

## U1 — does delegated Graph really 403 without a grant?

**Why it is first.** The plan's Fact 2 says a SharePoint Administrator cannot read another user's OneDrive
until something grants access to that specific site, and that no Graph endpoint performs the grant. That is an
**inference joining two cited quotes**, not a single quotation. If it is wrong — if a SharePoint Admin already
has read access — then the PnP channel may not be needed at all, and the whole design gets simpler and
cheaper. This is the highest-value half-hour in the project.

**Procedure**

1. As the SharePoint Admin, with **no** site-collection-admin grant on the target user's OneDrive:
   ```
   GET /v1.0/users/{targetUpn}/drive
   GET /v1.0/users/{targetUpn}/drive/root/children
   ```
   Use Graph Explorer or any delegated token. Record the **exact HTTP status and error body** for each.
2. Grant the signed-in admin site-collection admin on that OneDrive **through the admin centre UI**, so the
   grant mechanism is not itself a variable at this stage.
3. Repeat both calls. Record the statuses again.

**Expected:** 403 → 200.

| Outcome | What it means |
|---|---|
| **403 then 200** | Fact 2 holds. The plan stands. Continue to U2. |
| **200 both times** | **Fact 2 is wrong.** Delegated Graph alone may be enough for the read-only surface (D6), and possibly for more. Stop and re-plan before building a PnP channel that may be unnecessary. |
| **403 both times** | The UI grant did not take, or propagation is slow. Wait, retry, and check the grant landed before concluding anything. |

Record which permission scopes the token actually carried — `Files.Read.All`, `Sites.Read.All`, or others —
because "it worked" is not a finding without knowing what it worked *with*.

---

## U2 — can the public client mint a SharePoint token that PnP accepts?

The app is a public client with no secret. The Exchange channel already borrows a delegated token into a child
pwsh; this asks whether the same trick works for SharePoint.

1. Request a token for `https://<tenant>-admin.sharepoint.com/.default` from the app's existing registration
   (public client, PKCE). Note whether consent is prompted and whether it is admin-consent.
2. Decode it. Record `aud`, `scp`, `roles`, `appid`. The app already has a `LogTokenClaims` helper for
   exactly this shape of check — reuse it rather than pasting the token anywhere.
3. `Connect-PnPOnline -Url https://<tenant>-admin.sharepoint.com -AccessToken <token>`
4. Run any tenant-admin read, e.g. `Get-PnPTenantSite -Identity <a site url>`.

**Records:** whether the mint succeeded, the exact `aud` and `scp`, whether PnP accepted it, and the error
verbatim if it did not.

**If this fails**, the fallback is a second interactive sign-in inside the child process. That breaks the
single-sign-in model rather than the no-secret model, and it is worth knowing early because it changes what
the feature feels like to use.

---

## U3 — does `Set-PnPTenantSite -Owners` work against a personal site?

PnP's documentation does not say it does. The whole grant (D1) rests on it.

1. Against the throwaway user's OneDrive URL (`https://<tenant>-my.sharepoint.com/personal/<mangled_upn>`):
   ```
   Set-PnPTenantSite -Identity <personal site url> -Owners "manager@tenant"
   ```
2. Confirm independently: `Get-PnPSiteCollectionAdmin`, or `Get-SPOUser -Site <url> -LoginName <manager>` and
   check `IsSiteAdmin`.
3. Sign in **as the manager** and open the URL. Confirm the files are actually reachable, not merely that the
   cmdlet returned without error.
4. Remove the grant and confirm access goes away. Reversibility is a locked claim (D1) and must be tested,
   not assumed.

**Do not test `-PrimarySiteCollectionAdmin` or `Set-SPOSite -Owner` on anything you care about.** Both are
documented as producing an unsupported OneDrive state, and both will appear to succeed. If you want to
confirm the trap is real, use an account you are willing to throw away entirely.

**Also record:** what the manager actually *sees* on opening that URL — is it obviously the leaver's OneDrive,
is it labelled, would someone finding this URL in a ticket six months later know what they were looking at?
That answer feeds directly into what the operation log needs to say.

---

## U12 — does PnP v3 accept `-AccessToken` without `-ClientId`?

The cmdlet reference says `-ClientId` becomes required from 9 September 2024 for the *interactive* parameter
sets. Whether the Access Token set is exempt is not stated.

1. On a machine with **no** `ENTRAID_CLIENT_ID` environment variable set:
   ```
   Connect-PnPOnline -Url <tenant-admin url> -AccessToken <token from U2>
   ```
2. Repeat with `-ClientId <the app's own client id>`.

Record which forms work. If `-ClientId` is required, note whether the app's own registration id is accepted or
whether PnP demands a registration of its own — the latter is a deployment prerequisite for every customer
and belongs in the README, not discovered during a rollout.

---

## Also worth capturing while a scratch tenant is open

Cheap to answer at the same time, and each removes an UNVERIFIED from the plan:

- **U5** — does PnP expose the tenant OneDrive retention period? `Get-Help Set-PnPTenant -Parameter *` and
  look for `OrphanedPersonalSitesRetentionPeriod`. Gates whether D8's report can read it at all.
- **U7** — is "Enable access delegation" readable by any supported API? Try `Get-PnPUserProfileProperty`.
  Gates whether D8's report can ever be truthful about it.
- **U13** — post a file into a 1:1 Teams chat, then enumerate the sender's drive root. Confirm the folder is
  named `Microsoft Teams Chat Files` and whether its item count is cheap to read. Determines how precisely
  the log can state the Teams blast radius (D3).
- **Storage and item count** — whether `GET /users/{upn}/drive` returns `quota.used` and a usable item count
  in one call, or whether the read-only surface (D6) needs a recursive enumeration. That difference decides
  whether the surface is instant or slow enough to need progress.

---

## What "done" looks like

This file, filled in, with a verdict line per question and the verbatim errors for anything that failed.
Then the plan's D1–D8 either stand or get revised, and step 2 of the sequencing can start.

If U1 comes back 200/200, **stop and re-plan** rather than continuing down the list — several of the
remaining questions stop mattering.
