# OneDrive offboarding: complete mechanism inventory

Research date: 2026-09-01. Every external claim below carries the URL it came from. Claims I could not
source are prefixed **UNVERIFIED:** with the test that would settle them.

---

## 0. The hypothesis, tested: CONFIRMED

**There is no true ownership transfer for OneDrive in the Google sense. The hypothesis is confirmed.**

Three independent pieces of official documentation establish this:

1. **A OneDrive is a site collection, not a bag of files with an owner attribute.** *"Each OneDrive site
   currently uses the same architecture as that of a personal site or My Sites... This means that each
   OneDrive site is their own site collection."*
   https://learn.microsoft.com/en-us/sharepoint/dev/solution-guidance/customize-onedrive-for-business-sites
   **Caveat on that citation (re-checked 2026-09-01):** that page is `ms.date: 2023-03-08` and opens with
   *"This applies to **only** the classic OneDrive experience in SharePoint Online."* It is branding guidance,
   not an architecture statement of record. The claim itself holds, and is better sourced from current
   operational docs that treat a OneDrive as a site collection throughout: the deletion process moves it "to
   the site collection recycle bin" (https://learn.microsoft.com/en-us/sharepoint/retention-and-deletion); it
   is restored with `Get-SPODeletedSite` / `Restore-SPODeletedSite`
   (https://learn.microsoft.com/en-us/sharepoint/restore-deleted-onedrive); and `Set-SPOSite` states "For
   OneDrive for Business site collection, the only valid parameters are Identity, ... Owner, ..."
   (https://learn.microsoft.com/en-us/powershell/module/sharepoint-online/set-sposite?view=sharepoint-ps).
   Prefer those three.

2. **Reassigning the owner is explicitly unsupported.** From the current (ms.date 2026-08-26)
   unlicensed-accounts article, FAQ 18: *"Modifying the 'owner' property of a OneDrive account to any user or
   group other than the user who the account was provisioned for results in an unsupported state of the
   OneDrive account. This can cause many issues for the account."*
   https://learn.microsoft.com/en-us/sharepoint/unlicensed-onedrive-accounts

3. **Microsoft's own "transfer" feature is a file move plus an access grant, not a rebinding of the
   container.** The new Simplified file transfer feature: *"It's a full move. The files are relocated from
   the original OneDrive to the new destination, and their location is updated accordingly."*
   https://support.microsoft.com/en-us/onedrive/simplified-file-transfer-for-departing-employees

Consequence for the design: any "transfer to the manager" in UnifiedDirectoryManager is necessarily one of
(a) grant the manager access to the departing user's site collection, (b) physically relocate bytes into the
manager's own site collection, or (c) both. There is no fourth option where the existing container changes
hands. Google's model, where a file carries an `owner` field that an admin rewrites in place
(https://knowledge.workspace.google.com/admin/drive/transfer-drive-files-to-a-new-owner-as-an-admin),
has no Microsoft 365 analogue.

Note also there is no OneDrive equivalent of the Exchange **inactive mailbox**. The inactive-mailbox concept
is Exchange-only (https://learn.microsoft.com/en-us/purview/inactive-mailboxes-in-office-365); for OneDrive
the only "keep it after the account is gone" levers are the tenant retention period and Purview retention,
mechanisms B and I below. **UNVERIFIED:** I found no doc that says in so many words "there is no inactive
OneDrive." This is an argument from absence across the Purview and SharePoint doc sets. A support case asking
"is there an inactive-OneDrive equivalent" would settle it.

---

## 1. The default timeline, verbatim

From https://learn.microsoft.com/en-us/sharepoint/retention-and-deletion (ms.date 2026-06-02):

> The OneDrive retention period for cleanup of OneDrive begins when a user account is deleted from
> Microsoft Entra ID. **No other action causes the cleanup process to occur, including blocking the user from
> signing in or removing the user's license.**

1. User deleted in the M365 admin center, or removed by directory sync.
2. Deletion syncs to SharePoint.
3. OneDrive Clean Up Job marks the OneDrive for deletion. Deleted user visible in admin center for 30 days.
   Default OneDrive retention is also 30 days.
4. If a manager is set, the manager gets an email saying they have access and that the OneDrive will be
   deleted at the end of the retention period. If no manager but a secondary owner is configured, the
   secondary owner is emailed instead.
5. Seven days before expiry, a reminder email goes to the manager or secondary owner.
6. **"After seven days, the OneDrive for the deleted user is moved to the site collection recycle bin, where
   it's kept for 93 days. During this time, users will no longer be able to access any shared content in the
   OneDrive. To restore the OneDrive, you need to use PowerShell."**

> Microsoft 365 retention settings from retention policies and retention labels **always take precedence** to
> the standard OneDrive deletion process, so content included for OneDrive retention could be deleted before
> 30 days or retained for longer than the OneDrive retention period. Likewise, if a OneDrive is put on hold as
> part of an eDiscovery case, managers and secondary owners are sent email about the pending OneDrive
> deletion, but the OneDrive won't be deleted until the hold is removed.

> The Recycle Bin isn't indexed and therefore searches don't find content there. This means that an
> eDiscovery hold can't locate any content in the Recycle Bin in order to hold it.

Step 6's wording is ambiguous about whether the seven days runs from step 5 or restates the reminder.
**UNVERIFIED:** exact clock semantics of step 6. Test: delete a throwaway account with a 30-day retention
setting and record the date `Get-SPODeletedSite` first returns the personal site.

**The unlicensed-account clock runs in parallel and can pre-empt all of this.** See mechanism K, the single
most consequential recent change and one that is absent from most older guidance.

---

## 2. Mechanism inventory

### A. Automatic access delegation (the manager-access-on-deletion behaviour)

The official name is **"automatic access delegation"**, configured under **My Site Cleanup** in the classic
User Profile service, via the **Enable access delegation** checkbox.
Source: https://learn.microsoft.com/en-us/sharepoint/retention-and-deletion

- **What it does.** *"By default, when a user is deleted, the user's manager is automatically given access to
  the user's OneDrive."* You can also set a **secondary owner** used when the user has no manager in Entra ID.
  *"If access delegation is disabled or a manager or secondary owner isn't set for a user, no one has
  automatic access when the user is deleted or be warned that the OneDrive will be deleted."*
- **On by default?** Yes, per the sentence above. The doc still tells you to go confirm it, which implies it
  can be off in a given tenant.
- **Where it lives.** SharePoint admin center > More features > User profiles > Open > My Site Settings >
  Setup My Sites > My Site Cleanup > Enable access delegation. Classic UPS page. No modern-admin-center or
  Graph surface.
- **What "access" means.** The manager becomes an administrator of the departing user's OneDrive site
  collection and gets a link. Access, not ownership. See C for what the grantee sees.
- **Trigger.** Entra ID account deletion only. Not licence removal, not sign-in block.
- **Duration.** Only as long as the OneDrive survives: the retention period (default 30 days), after which
  the site goes to the site collection recycle bin and shared content becomes inaccessible.
- **Costs.** None.
- **Silently loses.** Nothing by itself, but it silently does nothing when the departing user has no manager
  in Entra ID and no secondary owner is configured. That is the most common failure mode and deserves an
  explicit precondition check in any scenario the app runs.
- **Reversible.** Yes. Remove the site collection administrator (C, "Revoke admin access").
- **Programmatic control.** **UNVERIFIED:** I found no supported API or cmdlet to read or set the
  "Enable access delegation" flag or the secondary owner. It is a classic User Profile Service property.
  Test: `Get-PnPUserProfileProperty`, the UPS CSOM `MySiteCleanup` properties, or inspect the
  `_layouts/15/mysitesettings.aspx` postback. Until proven otherwise, treat as a documented manual tenant
  prerequisite, not something the app configures.

### B. OneDrive retention period for deleted users

- **What it does.** Sets how long a deleted user's OneDrive survives before moving to the site collection
  recycle bin.
- **Where.** SharePoint admin center > Settings > Retention > **"Days to retain files a deleted user's
  OneDrive"**. *"Enter a value from 30 through 3650."*
  https://learn.microsoft.com/en-us/sharepoint/set-retention
- **Programmatically.** `Set-SPOTenant -OrphanedPersonalSitesRetentionPeriod <int32>`
  https://learn.microsoft.com/en-us/sharepoint/retention-and-deletion
  The `Set-SPOTenant` reference lists the parameter as `[-OrphanedPersonalSitesRetentionPeriod <Int32>]` but
  does not document its range; the 30-3650 range comes from the admin-center article.
  https://learn.microsoft.com/en-us/powershell/module/microsoft.online.sharepoint.powershell/set-spotenant
- **When it applies.** *"The setting is activated for the next user that is deleted as well as any users that
  are in the process of being deleted. The count begins as soon as the user account was deleted in the
  Microsoft 365 admin center, even though the deletion process takes time."*
- **Cost.** Storage. A retained OneDrive still consumes tenant storage, and the unlicensed clock (K) runs
  underneath it.
- **Silently loses.** Nothing directly, but see K: **the retention period does not protect against unlicensed
  365-day nonpayment deletion.** The unlicensed article states outright *"The retention period is no longer
  valid if storage is in a unpaid state."*
- **Reversible.** The setting yes. Its effect on an already-deleted OneDrive is unclear.
  **UNVERIFIED:** whether lengthening the period rescues a OneDrive already past its old expiry. Test: a
  controlled deletion with a short period, then lengthen it mid-window and check `Get-SPODeletedSite`.
- **Tenant-wide only.** There is no per-user override. You cannot keep one departing exec's OneDrive for a
  year without changing the behaviour for every deletion in the tenant.

### C. Site collection administrator grant (the actual "give someone access" primitive)

- **What it does.** Adds a second person as an administrator of the departing user's OneDrive site
  collection. This is the primitive underneath both automatic access delegation and the admin-center
  "Create link to files" flow.
- **Admin centre path.** SharePoint admin center > More features > User profiles > Manage User Profiles >
  find the profile > right-click > **Manage site collection owners** > add to **Site collection
  administrators**. *"The user can now access the former employee's OneDrive using their OneDrive URL."*
  https://learn.microsoft.com/en-us/microsoft-365/admin/add-users/remove-former-employee-step-5
- **Programmatically.** `Set-SPOUser -Site <OneDriveUrl> -LoginName <UPN> -IsSiteCollectionAdmin $True`
  https://learn.microsoft.com/en-us/powershell/module/microsoft.online.sharepoint.powershell/set-spouser
  Also step 5 of the restore procedure: https://learn.microsoft.com/en-us/sharepoint/restore-deleted-onedrive
- **Is it access or ownership?** Access. The site's primary owner remains the departed principal, and
  changing that owner is unsupported (section 0, point 2).
- **What the grantee sees.** They browse to
  `https://<tenant>-my.sharepoint.com/personal/<user>_<domain>_com` and get full control of that library.
  It does **not** appear inside their own OneDrive; it is a separate URL they must keep. This is the sharpest
  UX gap versus Google, where transferred content lands in a folder in the new owner's My Drive.
- **Needs.** SharePoint Administrator or Global Administrator. Critically:
  *"Global Administrators and SharePoint Administrators don't have automatic access to all sites and each
  user's OneDrive, but they can give themselves access to any site or OneDrive."*
  https://learn.microsoft.com/en-us/sharepoint/sharepoint-admin-role
  So the admin role alone is not enough. The grant is a required, separate step before any Graph read of that
  drive will work. See section 5.
- **Cost.** None.
- **Silently loses.** Nothing.
- **Reversible.** Yes, documented explicitly as "Revoke admin access to a user's OneDrive".
- **No Graph API. (Corrected 2026-09-01. The earlier wording in this brief was wrong.)** The `site` resource
  *does* expose a `permissions` relationship, and v1.0 *does* have `POST /sites/{siteId}/permissions` plus
  GET / PATCH / DELETE on it
  (https://learn.microsoft.com/en-us/graph/api/resources/site?view=graph-rest-1.0). That API is nevertheless
  useless here, and Microsoft says so outright:
  *"You can only use this method to create a new application permission; you can't use it to create a new user
  site permission."*
  https://learn.microsoft.com/en-us/graph/api/site-post-permissions?view=graph-rest-1.0
  It grants an **application** access to a site (the `Sites.Selected` model); it cannot make a *person* a site
  collection administrator. Delegated `Sites.FullControl.All` is accepted on it, and the same page adds *"In
  delegated workflows, the user must have an administrator role, such as SharePoint Administrator or higher."*
  So the conclusion stands, there is no Graph route to the site-collection-administrator grant, but for this
  reason rather than because the relationship is absent. This is the crux of the app-fit problem in section 5.

### D. Microsoft 365 admin center "Create link to files"

- **What it does.** User properties page > OneDrive > Get access to files > **Create link to files**. Gives
  the acting admin a link into the OneDrive. Then *"Download the files to your computer, or select Move to or
  Copy to to move or copy them to your own OneDrive or to a shared library."*
  https://learn.microsoft.com/en-us/microsoft-365/admin/add-users/remove-former-employee-step-5
- **Works for both** a licence-removed-but-not-deleted user and a deleted user within the retention window:
  *"If you remove a user's license but don't delete the account, you can give yourself access to the content
  in the user's OneDrive. If you delete the user's account, you have 30 days by default to access the former
  user's OneDrive data."*
- **Silently loses.** *"When you move or copy documents that have version history, only the latest version is
  moved."* That note is on the admin-center page and is stricter than the general SharePoint behaviour in E.
  Also *"Administrative options for an active user under the OneDrive tab in the Microsoft 365 admin center
  are currently not supported for multi-geo tenants."*
- **Reversible.** The link grant yes. A completed move, no.

### E. Interactive "Move to" / "Copy to" in the web UI

https://support.microsoft.com/en-us/sharepoint/libraries/move-or-copy-files-in-sharepoint

- **Move to:** *"When you use Move to, the history of the document is copied to the new destination."*
- **Copy to:** *"When you use Copy to with documents that have version history, only the latest version is
  copied."*
- **Copy limit stated on that page:** *"You can copy up to 500 MB of files and folders at one time."*
- **Metadata is not preserved, on either verb** (added 2026-09-01; the first draft's table only tracked
  version history). The same support page states that when files move to a location with different
  properties, only properties that exist at the destination are retained, and that metadata values associated
  with moved or copied files are not retained. So the "Move to preserves everything" reading of the row below
  is too generous: it preserves *version history*, not *metadata*.
- **Service limits for cross-site move/copy** (the authoritative ceiling):
  *"Total File Size: No more than 100 GB total file size Number of Files: No more than 30,000 files...
  OneNote Files: 2 GB limit. Cross-Geo Copy or Move: 15 GB file size limit."*
  https://learn.microsoft.com/en-us/office365/servicedescriptions/sharepoint-online-service-description/sharepoint-online-limits
- **Sharing links.** Moving across site collections breaks the original links because the link encodes the
  source site. The web UI offers **"Keep sharing with the same people"**, which preserves grantees'
  permissions on the item in its new home and emails them, but the old URLs still do not resolve. Sourced
  from Microsoft Q&A rather than a first-party feature page, so **lower confidence**:
  https://learn.microsoft.com/en-us/answers/questions/5334952/moving-files-from-personal-onedrive-folder-to-a-sh
  The Simplified file transfer page (F) confirms the option exists under that exact name.
- **Reversible.** No, other than moving the files back.

### F. Simplified file transfer for departing employees (new; closest thing to Google)

https://support.microsoft.com/en-us/onedrive/simplified-file-transfer-for-departing-employees
Message Center MC1164381, Microsoft 365 Roadmap ID 493946. Rollout mid-October 2025 through May 2026
(rollout dates from a Message Center mirror, https://mc.merill.net/message/MC1164381 -- **third-party mirror,
lower confidence on dates**; the feature page itself is first-party).

- **What it does.** A guided experience on top of automatic access delegation. The manager or secondary owner
  opens the departing user's OneDrive from the notification email, filters to shared or important files,
  selects them, picks a destination, and moves them in bulk.
- **It is a move, not a copy.** *"It's a full move. The files are relocated from the original OneDrive to the
  new destination, and their location is updated accordingly."*
- **Sharing.** *"Keep sharing with the same people"* retains collaborator access during the transfer.
  Collaborators get one consolidated notification when six or more files move.
- **Prerequisite.** Automatic access delegation must be configured (A).
- **Silently loses.** *"Microsoft Lists and Forms aren't stored as traditional files in OneDrive"* and cannot
  be transferred this way. Version history and link durability are not addressed on the page.
  **UNVERIFIED:** whether this move preserves full version history the way web-UI "Move to" does. Test:
  create a file with 3+ versions in a test OneDrive, delete the user, run the transfer as the manager, and
  inspect version history at the destination.
- **Who drives it.** The manager or secondary owner, in a browser. **There is no admin API, no cmdlet, and no
  Graph endpoint for this flow.** UnifiedDirectoryManager cannot invoke it. It can only ensure the
  preconditions (manager set in Entra, access delegation on) and tell the operator what will happen.
- **Reversible.** Not addressed. Assume no.
- **Licensing and limits.** Not specified on the page.

### G. Graph `driveItem: copy` (the programmatic cross-drive relocation)

https://learn.microsoft.com/en-us/graph/api/driveitem-copy?view=graph-rest-1.0

- **Cross-drive is supported.** `parentReference` takes a `driveId` and folder `id`, and the doc's own
  examples copy into a different drive. `POST /drives/{driveId}/items/{itemId}/copy`.
- **Asynchronous**, returns 202 plus a `Location` monitor URL.
- **Permissions.** Delegated least-privilege `Files.ReadWrite`; higher `Files.ReadWrite.All`,
  `Sites.ReadWrite.All`.
- **Limit.** *"The copy operation is restricted to 30,000 driveItems."* Plus the 100 GB / 30,000 file
  cross-site ceiling from the limits page.
- **Silently loses, verbatim:**
  - *"Metadata isn't retained when a driveItem is copied, including system metadata and custom metadata. An
    entirely new driveItem is created in the target location instead."*
  - *"File versions are only retained when the includeAllVersionHistory parameter is explicitly set to true.
    Otherwise, only the latest version is copied."*
  - *"Permissions are not retained when a driveItem is copied. The copied driveItem inherits the permissions
    of the destination folder."* **This is the big one: every sharing grant is destroyed.**
  - *"Cross-geo copy isn't supported when using app-only authentication."*
  - Known issue: `includeAllVersionHistory` is ignored if `name` is also passed. Workaround: copy without
    `name`, rename afterwards.
  - The root folder cannot be copied; use `childrenOnly: true`.
- **Reversible.** The copy is additive, so yes, delete the copy. If you follow it with a delete of the source
  to synthesize a move, that delete is only reversible via the recycle bin.

### H. Graph `driveItem` move (PATCH parentReference) -- NOT usable here

https://learn.microsoft.com/en-us/graph/api/driveitem-move?view=graph-rest-1.0

> **"Items cannot be moved between Drives using this request."**

A OneDrive-to-OneDrive move is a different drive by definition, so this API is out. Any programmatic "move"
must be copy (G) then delete, with all of G's losses. That is materially worse than the web UI's "Move to",
which does preserve version history.

### I. Purview retention policies and retention labels on OneDrive

https://learn.microsoft.com/en-us/purview/retention-policies-sharepoint

- **The leaver statement, verbatim:**
  > **OneDrive:** If a user leaves your organization, any files that are subject to a retention policy or has
  > a retention label will remain subject to the retention settings for the duration of the retention period
  > specified in the policy or label. During that time, all sharing access continues to work and the content
  > continues to be discoverable by Content Search and eDiscovery.
  > When the retention period expires and the retention settings included a delete action, content moves into
  > the Site Collection Recycle Bin and isn't accessible to anyone except the admin.
- **Contrast with SharePoint:** *"When a user leaves your organization, any content created by that user
  isn't affected because SharePoint is considered a collaborative environment, unlike a user's mailbox or
  OneDrive account."* This is the doc's own argument for relocating content to a SharePoint site rather than
  leaving it in a OneDrive.
- **Preservation Hold library.** A hidden system library created per site. When a user edits an item under a
  retention policy, or deletes any item subject to retention, the original is copied there.
  *"It's not supported to edit, delete, or move these automatically retained files yourself."* Access is via
  eDiscovery, not by browsing.
- **Timing of cleanup.** The timer job runs every seven days over items older than 30 days, so
  *"it can take up to 37 days for content to be deleted from the Preservation Hold library."* Expired content
  goes to the second-stage Recycle Bin and is permanently deleted after 93 days.
- **Versions under a retention policy:** *"For items that are subject to a retention policy (or an eDiscovery
  hold), the versioning limits for the document library are ignored until the retention period of the
  document is reached... old versions aren't automatically purged and users are prevented from deleting
  versions."*
- **Hold limits.** SharePoint/OneDrive with all sites automatically included: 13. With specific locations
  included or excluded: 2,600. These count against a 10,000-per-tenant compliance policy cap shared with DLP,
  information barriers and sensitivity labels.
  https://learn.microsoft.com/en-us/office365/servicedescriptions/sharepoint-online-service-description/sharepoint-online-limits
- **Costs.** Requires an appropriate Purview entitlement. **UNVERIFIED:** exact SKU boundary for the specific
  configuration you would use. Test: the Microsoft Purview service description licensing table.
- **Silently loses.** Nothing content-wise, but retained content is **not usable** as working files. It sits
  in a site nobody can browse once the OneDrive is recycled, reachable only through eDiscovery export.
- **Reversible.** Removing a policy stops future retention. Content already in the Preservation Hold library
  is cleaned up on the timer job schedule.
- **Not a per-departing-user tool.** Retention policies are scoped by location (all OneDrive, specific
  OneDrive URLs, or adaptive scopes). Adding a single URL per termination is technically possible via the
  Purview cmdlets but is an unusual pattern and burns against the 2,600 limit.

### J. eDiscovery / legal hold

- Effect on the deletion process: *"if a OneDrive is put on hold as part of an eDiscovery case, managers and
  secondary owners are sent email about the pending OneDrive deletion, but the OneDrive won't be deleted
  until the hold is removed."* https://learn.microsoft.com/en-us/sharepoint/retention-and-deletion
- Blocks cross-tenant migration entirely: *"OneDrive accounts with a Hold policy applied are blocked from
  migration."* https://learn.microsoft.com/en-us/microsoft-365/migration/cross-tenant-onedrive-migration
- **Does not survive the unlicensed clock.** See K.
- Content already in the recycle bin cannot be held, because the recycle bin is not indexed.

### K. The unlicensed OneDrive lifecycle -- the constraint that undermines "just leave it"

https://learn.microsoft.com/en-us/sharepoint/unlicensed-onedrive-accounts (ms.date 2026-08-26, current)

This is the most important and least-known item in this brief.

> Unlicensed OneDrive accounts are automatically put into **read-only mode after 60 unlicensed days** and
> **archived after 93 unlicensed days**. While read-only and archived accounts remain visible to admins
> through administrative tools, **neither admins nor end users have access to their content** unless an admin
> takes a supported action such as assigning a license, enabling applicable billing, reactivating archived
> content, or moving content that must be retained.

> As of **July 1, 2026**, unlicensed OneDrive accounts that remain unpaid are subject to a cumulative
> **365-day nonpayment clock.**

Timeline table from that article:

| Milestone | Expected state |
| --- | --- |
| Day 1 | Clock begins on licence removal or Entra deletion (or 2026-07-01 for pre-existing accounts) |
| Day 60 | Account placed in read-only mode |
| Day 93 | Account archived, or moved to recycle bin depending on account and retention state |
| Day 275 | Account no longer available in eDiscovery |
| Day 365 | Account subject to deletion after 365 cumulative unpaid days |

And the kicker, FAQ 7:

> **If billing is not enabled for unlicensed OneDrive accounts, and accounts are still retained 365 days
> after becoming unlicensed, then the unlicensed accounts will be subject to deletion 365 days after being
> unlicensed even if retention policies, settings, or holds exist on the OneDrive account.**

Reinforced on the retention-and-deletion page itself: *"After 12 month of Unpaid storage/archive the OneDrive
Data might be deleted regardless of Retention settings, retention policies, eDiscovery, and all holds."*

- **Applies to.** All account types: *"The policy applies to all unlicensed OneDrives that remain unpaid in
  full."* Excluded: EDU, GCC, DoD.
- **Note the trigger difference.** Licence removal alone, without deletion, starts the unlicensed clock even
  though it does **not** start the OneDrive deletion clock. So "remove the licence but keep the account"
  leaves the OneDrive read-only at day 60 and archived at day 93.
- **Order of precedence** when billing IS enabled, for an account whose owner was deleted in Entra:
  1. OneDrive retention period, 2. Retention policies, 3. Legal holds. Then recycled and permanently deleted.
- **Costs to keep it accessible.** Enabling unlicensed-OneDrive billing is a pay-as-you-go toggle requiring a
  linked Azure PAYG subscription: *"the reactivation fee is applied for $0.60/GB for that account, and from
  that month onward, the storage fee of $0.05/GB/Month is applicable for all unlicensed accounts within the
  organization for longer than 93 days."* Their worked example: 100 accounts at 1 TB each is a one-time
  $614.40 reactivation plus $5,120/month.
- **Reactivation latency.** Up to 24 hours, and *"Once the account is reactivated, it remains active for a
  total of 30 days before it gets automatically archived again."*
- **Relicensing an archived account with no owner is not possible directly.** FAQ 11: if the identity was
  deleted, you must enable the meter, move needed content out, then follow the site-user-ID-mismatch fix.
- **Silently loses.** Access, at day 60 and again at day 93, with no warning in most admin surfaces. FAQ 13:
  version history and Recycle Bin contents *"become inaccessible until the data is rehydrated into an active
  tier or the unlicensed OneDrive is licensed."*
- **Reversible.** Reactivation yes, at cost, with a 24-hour wait and a 30-day re-archive timer. Deletion at
  day 365, no.
- **You cannot opt out, but since 2026-08 you can move the goalposts.** FAQ 19 still says *"Unlicensed
  OneDrive accounts will get enforced no matter what, including being put into read-only mode and eventually
  Archive."* That governs *whether* enforcement happens. *When* it happens is now a tenant setting. See K2
  below, which the first draft of this brief missed entirely.
- **Reporting surface.** SharePoint admin center > Reports > OneDrive accounts, with a CSV and a detailed
  page (`spo.ms/admin#/oneDriveAccounts/management`) carrying "Unlicensed reason" and "Deletion blocked by"
  columns. `Set-SPOSiteArchiveState` reactivates via PowerShell.

### K2. Standard storage for unlicensed OneDrive accounts (ADDED 2026-09-01; absent from the first draft)

https://learn.microsoft.com/en-us/sharepoint/standard-storage-unlicensed-onedrive-accounts (ms.date 2026-08-26)

The 60/93-day numbers in K are **default tenant settings, not fixed service behaviour.** The unlicensed
article says so itself: *"These timelines reflect the default tenant settings. If your organization needs a
longer active period before archiving, you can change the tenant-wide active-storage period."* It repeats the
pointer three times. Any plan built on "60 days, full stop" is built on a default.

- **Tenant-wide control.** `Set-SPOTenant -UnlicensedOneDriveAccountsActiveStoragePeriod <days>`.
  *"The supported range is 93 through 3,650 days."* Read it back with
  `Get-SPOTenant | Select-Object UnlicensedOneDriveAccountsActiveStoragePeriod`.
- **Read-only is derived, not independent.** *"The read-only stage begins 33 days before the configured
  archive date."* That is where the default 60 comes from: 93 minus 33. Set the period to 365 and the doc's
  own worked example reads: *"The account remains active through day 331. The account becomes read-only on
  day 332. The account is archived on day 365."*
- **Per-account override.** `Set-SPOSiteArchiveState -Identity <OneDriveUrl> -ArchiveState Active
  -KeepActiveUntil "2027-08-13"`. This is a genuine per-user escape hatch for the unlicensed lifecycle.
  Caveat: *"There's no way to keep an account active indefinitely."*
- **Preconditions, and they bite.** Pay-as-you-go billing must be connected **and** Standard storage
  separately enabled in the Microsoft 365 admin center. *"There is no SharePoint Online PowerShell parameter
  for enabling the billing policy or Standard storage."* If billing is later disabled, *"only a reset to 93
  is allowed."*
- **Not generally available as of this brief's date.** *"Standard storage will be generally available for
  unlicensed OneDrive accounts starting in October 2026. Until then, the Standard storage billing option
  might not appear in the Microsoft 365 admin center, even after you connect pay-as-you-go billing."*
  Today is 2026-09-01. Roughly a month out. Do not design against it as though it were live.
- **Cost sweetener with an expiry.** *"During calendar year 2026, the reactivation fee is waived when
  Standard storage is enabled."*
- **Tooling floor, and it lands on section 5.** *"These standard-storage properties require PowerShell
  version 16.0.27600.12000 or higher"* of the SharePoint Online Management Shell, and the doc's own procedure
  connects with `Connect-SPOService`. So the newest OneDrive lifecycle controls live in the one module that
  has no `-AccessToken`.
- **It does not buy you out of the 365-day nonpayment clock on its own.** Standard storage moves read-only
  and archive. The deletion clock is about being *unpaid*, and enabling billing is what pauses it (unlicensed
  FAQ 4: *"Count pauses while the account is paid"*). Standard storage requires billing, so in practice they
  travel together, but they are two levers and the docs keep them separate.
- **UNVERIFIED:** whether `UnlicensedOneDriveAccountsActiveStoragePeriod` can be set at all before the
  October 2026 GA date, and what `Get-SPOTenant` returns in a tenant without Standard storage enabled. The
  doc says a returned `0` means *"the connected SharePoint Online endpoint might not yet expose the
  property."* Test: run the `Get-SPOTenant` line above against this tenant and record the value.

**Design implication:** the "leave it in place under a retention policy" option is materially weaker than it
looks. **At tenant defaults** it buys 60 days of usable access and 365 days of existence unless the tenant
pays the unlicensed-storage meter, and Microsoft states outright that retention and legal holds do not
survive nonpayment past 365 days. The first two numbers are stretchable via Standard storage (K2), at cost,
from October 2026. The 365-day nonpayment clock is not stretchable, only pausable by paying. Any offboarding design that says "we will rely on retention" needs the
unlicensed-billing toggle to be an explicit, budgeted tenant decision. That is a conversation for whoever
owns the Microsoft 365 bill, not an assumption the app should bake in.

### L. Restore a deleted OneDrive from the site collection recycle bin

https://learn.microsoft.com/en-us/sharepoint/restore-deleted-onedrive

- **Window.** After the retention period, *"the OneDrive remains in a deleted state for 93 days and can only
  be restored by a SharePoint Administrator."*
- **Procedure.** `Get-SPODeletedSite -Identity <URL>` (or `-IncludeOnlyPersonalSite | FT url`), then
  `Restore-SPODeletedSite -Identity <URL>`, then
  `Set-SPOUser -Site <URL> -LoginName <UPNofDesiredAdmin> -IsSiteCollectionAdmin $True`.
- **After restore.** *"When a OneDrive is restored, it continues to remain available until it's explicitly
  deleted."* But per unlicensed FAQ 6: *"If 93 days have passed since the license removal, bringing back an
  unlicensed OneDrive account from the recycle bin leads to automatic archival."*
- **Permanent delete.** `Remove-SPOSite` then `Remove-SPODeletedSite`. Not reversible.
- **Requires SPO Management Shell.** See section 5.

### M. Microsoft 365 Backup

https://learn.microsoft.com/en-us/microsoft-365/backup/backup-overview

- **Recovery window.** Configurable per policy: 3 months, 6 months, 1 year, or 2 years. Existing policies and
  artifacts default to 1 year.
- **Recovery points.** Every 10 minutes for the prior two weeks, then weekly snapshots 2-52 weeks back.
- **Granularity.** Backup unit is the whole OneDrive account. Restore granularity is the OneDrive account;
  "files restorable via versions (coming soon)".
- **Restore options.** *"Location: Same or new URL"*, and a OneDrive restore *"rolls back to the state of the
  site at the prior point in time, overwriting all content and metadata since that prior point in time."*
- **Cost.** $0.15/GB/month of protected content; restores are free. Pay-as-you-go, not a per-user licence.
  https://learn.microsoft.com/en-us/microsoft-365/backup/backup-pricing
- **Isolation from Purview.** *"Retention and deletion policies (for example, from Purview) don't affect the
  backup recovery window, which remains fully isolated from those policies."* There is a fixed 90-day grace
  period after offboarding a backup policy.
- **There IS a Microsoft Graph v1.0 API for Backup, and it accepts delegated permissions. (ADDED 2026-09-01.
  The first draft treated Backup as admin-centre-only, which is wrong and matters, because this app is a
  delegated Graph client.)** The overview notes *"Microsoft 365 Backup also supports PowerShell cmdlets"* and
  links the Graph reference. The service root is `backupRestoreRoot`
  (https://learn.microsoft.com/en-us/graph/api/resources/backuprestoreroot?view=graph-rest-1.0), whose v1.0
  relationships include `oneDriveForBusinessProtectionPolicies`, `oneDriveForBusinessRestoreSessions`,
  `driveProtectionUnits`, `restorePoints` and `oneDriveForBusinessBrowseSessions`.
  `POST /solutions/backupRestore/oneDriveForBusinessRestoreSessions` is v1.0, and its permissions table is:
  Delegated (work or school) `BackupRestore-Restore.ReadWrite.All` as least privileged, higher "Not
  available."; Application the same; Delegated (personal) "Not supported."
  https://learn.microsoft.com/en-us/graph/api/backuprestoreroot-post-onedriveforbusinessrestoresessions?view=graph-rest-1.0
  The `driveRestoreArtifact` body carries a `destinationType` whose documented example value is `"new"`,
  so restore-to-a-new-destination is a documented API parameter, not an admin-centre-only affordance.
  National cloud availability is Global service only; US Gov L4, US Gov L5/DoD and 21Vianet are all marked
  unavailable on that page.
- **UNVERIFIED and important:** whether a OneDrive whose owner has been deleted from Entra can still be
  restored from Backup, and where `destinationType: "new"` actually lands the content. The API reference does
  not say whether the caller chooses the target URL or the service generates one, and the overview does not
  say either. Test: protect a throwaway OneDrive, delete the user, wait past the retention window, then POST a
  restore session with `destinationType: "new"` and record both the HTTP status and the resulting URL. This
  determines whether Backup is a viable "vault the leaver's OneDrive" answer or only a ransomware tool.
- **Reversible.** Restores overwrite. Backups are append-only and cannot be modified, only deleted by
  offboarding.

### N. Cross-tenant OneDrive migration -- NOT applicable to same-tenant offboarding

https://learn.microsoft.com/en-us/microsoft-365/migration/cross-tenant-onedrive-migration

`Set-SPOCrossTenantRelationship` plus `Start-SPOCrossTenantUserContentMove`. Explicitly for mergers and
divestitures **between two separate tenants**. Requires a paid **Cross Tenant User Data Migration** add-on
(*"User licenses are per migration (one-time fee)... Microsoft doesn't offer exceptions for this licensing
requirement"*), is blocked by any hold, and fails if a OneDrive already exists for the target user. There is
no documented same-tenant, user-to-user variant. **UNVERIFIED:** whether the cmdlet technically accepts a
same-tenant relationship. The documentation only describes the two-tenant case and I found nothing
sanctioning same-tenant use, so treat as unsupported. Settle it with a support case, not a production
experiment.

Worth noting conceptually: it is the only Microsoft mechanism that *"leaves behind a redirect link on Source"*
so old sharing links keep working. Nothing in the same-tenant toolkit does that.

### O. SPMT, Migration Manager, Mover -- all NOT applicable

- **SharePoint Migration Tool (SPMT)** supported sources: on-premises file shares (local and network) and
  SharePoint Server 2010/2013/2016/2019. SharePoint Online and OneDrive are destinations, not sources.
  https://learn.microsoft.com/en-us/sharepointmigration/what-is-supported-spmt
- **Migration Manager** supported cloud sources: Google Workspace, Box, Dropbox, Egnyte, plus SMB file shares.
  https://learn.microsoft.com/en-us/sharepointmigration/mm-get-started
  https://learn.microsoft.com/en-us/sharepointmigration/mm-google-overview
  **UNVERIFIED:** OneDrive-to-OneDrive within one tenant. No doc lists OneDrive as a source. Treat as
  unsupported.
- **Mover** was the Microsoft-acquired third-party migration service, folded into Migration Manager.
  **UNVERIFIED:** current status; I found no first-party retirement notice in this pass. Do not plan around it.

### P. Site lock state (a blunt "freeze it" tool)

`Set-SPOSite -LockState <NoAccess | ReadOnly | Unlock>`. From the cmdlet reference: *"Sets the lock state on a
site. Valid values are: NoAccess, ReadOnly and Unlock... When the lock state of a site is NoAccess, all traffic
to the site will be blocked... It isn't possible to set the lock state on the root site collection."*
https://learn.microsoft.com/en-us/powershell/module/sharepoint-online/set-sposite?view=sharepoint-ps

**The bootstrap trap of mechanism C applies here too, and this brief previously missed it.** Unlike
`Set-SPOUser` (SharePoint administrator is sufficient), the `Set-SPOSite` reference states: *"You must be a
SharePoint Online administrator **and be a site collection administrator** to run the cmdlet."* Locking a
departing user's OneDrive therefore requires the mechanism-C grant first, exactly as a Graph read does.

Purview notes: *"If a site is configured with the Set-SPOSite
parameter LockState that's set to NoAccess or ReadOnly, items in that site can't be deleted with this site
configuration."* https://learn.microsoft.com/en-us/purview/retention-policies-sharepoint
The unlicensed article confirms an admin lock also blocks automatic deletion ("Deletion blocked by: Active
lock on account") and that enforcement will not override an existing lock (FAQ 17). Unlocking a read-only
unlicensed account: https://learn.microsoft.com/en-us/sharepoint/manage-lock-status

### Q. Graph shortcut into the manager's OneDrive (`remoteItem`)

`POST` to the drive's root children with a `remoteItem` facet carrying the source `driveId` and `id` creates
the "Add shortcut to My files" pointer.
https://learn.microsoft.com/en-us/graph/api/resources/remoteitem?view=graph-rest-1.0
https://learn.microsoft.com/en-us/onedrive/developer/rest-api/concepts/using-sharing-links?view=odsp-graph-online

- **Why it matters.** The only mechanism that makes the departing user's content *appear inside the manager's
  own OneDrive* without moving bytes, which is the visible half of what Google's transfer does.
- **What it does not do.** Does not change ownership, does not survive deletion of the source site, does not
  stop the retention clock. It is a pointer.
- **Citation correction (2026-09-01).** The constraints below are **not** on the `remoteItem` resource page.
  That page documents only the facet, marks every property Read-only except `webDavUrl`, and its sole
  limitation note is *"Unlike with folders in the same drive, a driveItem moved into a remote item may have
  its `id` value changed."* The write operation and its constraints are on **Accessing shared files and
  folders**, https://learn.microsoft.com/en-us/onedrive/developer/rest-api/concepts/using-sharing-links?view=odsp-graph-online
  (ms.date 2017-09-10, updated 2026-05-14). Cite that page, not `resources/remoteitem`.
- **The write IS documented, first-party. (Upgrades the first draft's UNVERIFIED #9.)** Verbatim from that
  page: *"To add the shared folder to a drive, your app POSTs to the drive's root collection with the details
  of the shared folder in the **remoteItem** facet"*, with a worked `POST /drive/root/children` body of
  `{"name": "...", "remoteItem": {"id": "...", "parentReference": {"driveId": "..."}}}` returning
  `201 Created`. *"**Note:** **driveId** and **id** are required."* Removal is
  `DELETE /drive/items/{local-item-id}` returning 204, and *"This does not delete the shared folder or items
  contained in the shared folder."*
- **Documented prerequisites, verbatim:** *"The user has explicit permission to the shared folder, and isn't
  accessing the shared folder through a link."* and *"Your app needs read/write access to the drive where the
  shared folder will be added."*
- **Documented constraints, verbatim:** *"**Note:** A shared folder can only be added to the root of a user's
  drive."* / *"The OneDrive API does not support moving or copying items into a shared folder. New items can
  be created in the folder by using the regular upload actions and targeting the remoteItem **driveId** and
  **id**."* / *"requesting the **children** collection for a remote item will result in an error from the
  server"* (you must re-address the source drive instead) / with `delta`, *"the items contained within a
  shared folder will not be returned. A separate call to **delta** and separate cached delta token is
  required for each shared folder."*
- **What is still UNVERIFIED, and it is the part that matters here.** The page is written for the ordinary
  "someone shared a folder with me" case. Two gaps for offboarding: (a) whether being a *site collection
  administrator* on the departing user's OneDrive satisfies *"explicit permission to the shared folder"*, and
  (b) whether the departing user's whole OneDrive root qualifies as a shared folder at all, or whether the
  manager must be pointed at a specific child folder. Also note the page carries the `odsp-graph-online`
  moniker, so it is the OneDrive REST reference rather than the Graph v1.0 API reference; the shape of the
  URL there is `/drive/root/children`, not `/me/drive/root/children`. Test: as an admin who is site
  collection admin on a scratch OneDrive, `POST /me/drive/root/children` with a `remoteItem` body naming
  that drive's `driveId` and a folder `id`, delegated, and record the status.

---

## 3. What each option does to sharing links, permissions and version history

| Mechanism | Sharing links | Permissions | Version history |
|---|---|---|---|
| Grant site collection admin (C) | Untouched | Untouched, one admin added | Untouched |
| Leave in place under retention (I) | *"all sharing access continues to work"* while retention runs | Untouched | Preserved; version limits ignored while retained |
| Web UI "Move to" (E) and Simplified file transfer (F) | Old URLs break; "Keep sharing with the same people" preserves grantees and emails them | Preserved if the option is ticked | *"the history of the document is copied to the new destination"* -- but **metadata is not** retained (E) |
| Admin center Create link then Move/Copy (D) | Old URLs break | Not stated | *"only the latest version is moved"* |
| Web UI "Copy to" (E) | Old URLs break | Not stated | *"only the latest version is copied"* |
| Graph copy (G) | Old URLs break | **Destroyed.** *"Permissions are not retained... The copied driveItem inherits the permissions of the destination folder"* | Only with `includeAllVersionHistory: true`, ignored if `name` is also sent |
| OneDrive recycled at end of retention | Dead: *"users will no longer be able to access any shared content"* | N/A | N/A |
| Unlicensed archive at day 93 (K) | Dead | N/A | *"Content becomes inaccessible until the data is rehydrated"* |
| Cross-tenant migration (N, not applicable) | Redirect left behind, links keep working | Preserved via identity map | Preserved |

The Graph-copy permissions row is the single most dangerous silent loss in the inventory. A scenario that
"transfers the OneDrive" using Graph copy will look successful, produce every file, and quietly revoke every
collaborator's access.

---

## 4. Licensing implications

- **Does the source account need to keep a licence to grant access or move files?** No. The admin-center flow
  works either way: *"If you remove a user's license but don't delete the account, you can give yourself
  access to the content in the user's OneDrive."*
  https://learn.microsoft.com/en-us/microsoft-365/admin/add-users/remove-former-employee-step-5
- **But removing the licence immediately starts the unlicensed clock** (K): read-only at 60 days, archived at
  93, deletion risk at 365 cumulative unpaid days. True whether or not the account is deleted.
- **Deleting the user starts the OneDrive deletion clock** and simultaneously makes the OneDrive unlicensed.
  Both clocks run. Whichever bites first wins, and the unlicensed one now overrides retention and holds after
  365 unpaid days.
- **Keeping a licence assigned purely to keep a OneDrive alive** works but defeats the point of offboarding.
  Assigning a licence to an archived account that still has an owner auto-reactivates it within 24-48 hours
  with no reactivation fee.
- **Paying instead of licensing:** enable the unlicensed-OneDrive billing meter. $0.60/GB one-time
  reactivation per account plus $0.05/GB/month across all unlicensed accounts over 93 days, tenant-wide.
- **Microsoft 365 Backup:** $0.15/GB/month, pay-as-you-go, no per-user licence.
- **Purview retention:** requires the relevant compliance entitlement. **UNVERIFIED** exact SKU.
- **Cross-tenant migration:** separate paid add-on, per-user one-time, not relevant here.
- **Net for a termination scenario:** the only licence-reclaiming path that loses nothing is *move the content
  out first, then delete the user, then reclaim the licence.* Every path that reclaims the licence before
  moving content is racing a 60-day read-only deadline.

---

## 5. Fit against UnifiedDirectoryManager's actual constraints

Read from the repo, not assumed:

- `app/src/UnifiedDirectoryManager/Services/GraphService.cs` uses `InteractiveBrowserCredential` with
  `RedirectUri = http://localhost`, a DPAPI-persisted MSAL cache, and delegated scopes
  `User.ReadWrite.All`, `Organization.Read.All`, `Group.ReadWrite.All`, `Device.Read.All`,
  `UserAuthenticationMethod.ReadWrite.All`. No client secret anywhere. `GetAccessTokenAsync(string[] scopes)`
  can mint a token for **any** resource the app registration is permitted to request.
- `app/src/UnifiedDirectoryManager/Services/ExchangeService.cs` requests
  `https://outlook.office365.com/.default` from that same credential and hands the token to a long-lived
  child `pwsh` over stdin using a framed line protocol. That is the precedent to extend.

What this means per mechanism:

**Doable in-process with Graph, delegated, no new pattern:**
- Read the departing user's manager (`/users/{id}/manager`) to check the automatic-access-delegation
  precondition. Covered by the existing `User.ReadWrite.All`.
- Enumerate the departing user's drive (`/users/{id}/drive`) and copy items (G) **only after** the signed-in
  admin has been made a site collection administrator of that OneDrive. This is the trap: delegated
  `Files.*.All` means *"all files the signed-in user can access"*
  (https://learn.microsoft.com/en-us/onedrive/developer/rest-api/concepts/permissions_reference), and
  *"Global Administrators and SharePoint Administrators don't have automatic access to... each user's
  OneDrive"* (https://learn.microsoft.com/en-us/sharepoint/sharepoint-admin-role). Delegated permissions are
  the intersection of the app's grant and the user's own rights
  (https://learn.microsoft.com/en-us/graph/permissions-overview), so a Graph-only design cannot bootstrap
  itself. Adding delegated `Sites.FullControl.All` does not fix it; it is still intersected with the
  signed-in user's SharePoint permissions.

**Requires a SharePoint-side call Graph cannot make:**
- Granting site collection administrator (C), restoring a deleted OneDrive (L), reading or setting the tenant
  retention period (B), lock state (P). All are SPO PowerShell or CSOM.

Two viable routes, both a bigger lift than Exchange was:

1. **Child pwsh with PnP.PowerShell and a borrowed SharePoint token.** Exact analogue of the Exchange
   pattern: request `https://<tenant>-admin.sharepoint.com/.default` from the existing
   `InteractiveBrowserCredential`, then `Connect-PnPOnline -Url ... -AccessToken $token`. PnP documents
   `-AccessToken` and warns: *"if you pass in an access token for your SharePoint Online tenant, you can only
   execute cmdlets that will directly target your SharePoint Online environment. If you would use a cmdlet
   that communicates with Microsoft Graph behind the scenes, it will throw an access denied exception."*
   https://pnp.github.io/powershell/articles/authentication.html
   https://pnp.github.io/powershell/cmdlets/Connect-PnPOnline.html
   Cost: the app registration needs SharePoint (Office 365 SharePoint Online) **delegated** permissions such
   as `AllSites.FullControl` / `Sites.FullControl.All`, which require admin consent (permission names per
   https://learn.microsoft.com/en-us/sharepoint/dev/solution-guidance/security-apponly-azuread). Still a
   public client, still no secret, so the documented security posture survives intact. Adds PnP.PowerShell to
   the per-machine prerequisites alongside pwsh and ExchangeOnlineManagement. Token lifetime is roughly an
   hour, so the long-lived-session design needs a refresh path.

2. **Microsoft.Online.SharePoint.PowerShell (SPO Management Shell).** This is what every Microsoft doc in this
   brief tells you to use (`Set-SPOUser`, `Set-SPOTenant`, `Get-SPODeletedSite`, `Restore-SPODeletedSite`,
   `Set-SPOSiteArchiveState`). **It has no documented token-based auth** -- `Connect-SPOService` takes a
   credential or an interactive MFA dialog -- and under PowerShell 7 it must load through the Windows
   PowerShell compatibility shim: *"In order to run SharePoint Online PowerShell commands in a Windows
   PowerShell 7 console, you must import the SharePoint module using the -UseWindowsPowerShell parameter."*
   https://learn.microsoft.com/en-us/powershell/sharepoint/sharepoint-online/connect-sharepoint-online
   A second interactive sign-in inside a child process is a poor experience and breaks the single-sign-in
   model. **Route 1 is the better fit.**

**Not reachable from the app at all:**
- Enable access delegation and secondary owner (A): classic UPS page, no API found.
- Simplified file transfer (F): browser-only, manager-driven.
- Purview retention policy authoring (I): a separate `Connect-IPPSSession` endpoint, i.e. a third PowerShell
  connection.

**UNVERIFIED and worth a spike before committing:** whether the tenant's app registration can obtain a
`https://<tenant>-admin.sharepoint.com/.default` delegated token through the existing public-client
interactive flow, and whether `Connect-PnPOnline -AccessToken` then accepts it for the site-collection-admin
cmdlets (PnP's equivalents are `Add-PnPSiteCollectionAdmin` / `Set-PnPTenantSite`). This is the direct
analogue of the Exchange v2 Phase 0 spike already recorded in project memory and should be run the same way.

---

## 6. Where Microsoft's menu falls short of Google, precisely

| Google behaviour | Closest M365 equivalent | Gap |
|---|---|---|
| Admin rewrites `owner` on every file in place | None | No owner attribute exists to rewrite. Confirmed. |
| Files land in a transfer folder in the new owner's My Drive | Web-UI Move to / Simplified file transfer relocates bytes into the manager's OneDrive | Works, but it is a physical relocation with hard limits (100 GB / 30,000 files) and it breaks old links |
| *"Transferring files does not affect who has access to the files"* | "Keep sharing with the same people" in the web UI | Preserves grantees but **not the URLs**. Graph copy preserves neither. |
| Previous owner keeps edit access | N/A after deletion | Departing user is gone by then |
| Single admin-console action, one screen | Manager-driven browser flow (F), or admin PowerShell plus Graph | No single admin-side "transfer" action exists |
| Transfer fails cleanly after 36 hours | Async Graph copy with a monitor URL | Comparable |
| Cannot transfer to or from external accounts; Trash excluded; blocked by litigation hold | Holds block cross-tenant migration; Lists and Forms excluded from F | Both products carve out similar exceptions |

Honest framing for the user: Microsoft's answer to "give the leaver's files to their manager" is **"grant the
manager access, then have the manager move what they want."** The grant is automatic and free; the move is
manual and lossy. Anything closer to Google requires the app to do the moving itself, in which case Graph
copy's permission destruction is the thing to design around.

---

## 7. Consolidated UNVERIFIED list

1. Exact clock semantics of "After seven days" in deletion-process step 6.
2. Whether lengthening `OrphanedPersonalSitesRetentionPeriod` rescues an already-expiring OneDrive.
3. Whether "Enable access delegation" and the secondary owner are readable or settable by any supported API.
4. Whether Simplified file transfer preserves full version history.
5. Whether Microsoft 365 Backup can restore a OneDrive whose Entra owner has been deleted, and to a new URL.
6. Whether `Start-SPOCrossTenantUserContentMove` functions same-tenant (assume no).
7. Whether Migration Manager accepts a OneDrive as a source (assume no).
8. Current status of Mover.
9. ~~Whether `POST /me/drive/root/children` with a `remoteItem` facet is a supported v1.0 write.~~
   **SETTLED 2026-09-01: it is documented, first-party**, in Accessing shared files and folders
   (odsp-graph-online moniker), with request body, 201 response, prerequisites and constraints. What remains
   open is narrower: whether a site-collection-administrator grant counts as the required *"explicit
   permission to the shared folder"*, and whether a OneDrive root can be the target at all. See Q.
10. Exact Purview SKU required for the retention configuration in question.
11. Whether the app's public-client registration can mint a `<tenant>-admin.sharepoint.com/.default`
    delegated token, and whether PnP.PowerShell accepts it for site-collection-admin cmdlets.
12. Whether "no inactive-OneDrive equivalent" is stated anywhere first-party, or only implied by absence.
13. Whether `Set-SPOTenant -UnlicensedOneDriveAccountsActiveStoragePeriod` is settable in this tenant before
    the October 2026 Standard storage GA, and what `Get-SPOTenant` returns today (K2).
14. Whether Microsoft 365 Backup's `destinationType: "new"` lets the caller choose the destination URL or the
    service generates one, and whether the OneDrive of a deleted Entra user is restorable at all (M).

### Verification pass, 2026-09-01

An independent re-fetch of every first-party source in section 8 was carried out. Findings:

- **Confirmed verbatim and unchanged:** the section 1 deletion-process timeline including "30 days", "Seven
  days", "93 days"; automatic access delegation being on by default and its admin-centre path (A); the
  `Set-SPOUser -IsSiteCollectionAdmin` grant and the "you must be at least a SharePoint administrator" /
  GDAP-unsupported notes (C); the 30-3650 retention range and its activation wording (B); the "Create link to
  files" flow and "only the latest version is moved" (D); the Move-to / Copy-to and 500 MB wordings (E); every
  quotation from Simplified file transfer (F); every quotation and permission row on `driveItem: copy` (G);
  "Items cannot be moved between Drives using this request" (H); the Purview leaver statement, Preservation
  Hold library, 37-day timer job and 93-day second-stage bin, and the versioning-limits-ignored passage (I);
  the hold limits 13 / 2,600 / 10,000 (I); every number and quotation in the unlicensed lifecycle including
  60 / 93 / 275 / 365, July 1 2026, $0.60/GB, $0.05/GB/month, $614.40, $5,120/month, 24 hours, 30 days, and
  FAQs 2, 6, 7, 11, 13, 17, 18 and 19 (K); the 93-day restore window and full PowerShell procedure (L); every
  Backup figure including the 1-year default, 10-minute recovery points, same-or-new-URL restore, $0.15/GB/mo
  and the 90-day offboarding grace (M); the cross-tenant licensing, hold-block and redirect statements (N);
  Migration Manager's source list, which still does not include OneDrive (O); the "changing the Owner of a
  OneDrive is not supported" wording.
- **Corrected:** the "no Graph permissions relationship on `site`" claim (C); the `remoteItem` citation
  attribution (Q); the missing Standard storage lever (K2); the missing Backup Graph API (M); the
  `Set-SPOSite` site-collection-admin requirement (P); metadata loss on move/copy (E and section 3).
- **Not re-verified in this pass, flagged so it is not mistaken for confirmed:** the SPMT supported-sources
  page; the inactive-mailboxes page; and the MC1164381 / Roadmap 493946 rollout dates, which remain
  third-party-mirror sourced. The Simplified file transfer page itself carries **no** rollout dates.

---

## 8. Source list

Primary (first-party, fetched and read):

- https://learn.microsoft.com/en-us/sharepoint/retention-and-deletion
- https://learn.microsoft.com/en-us/sharepoint/set-retention
- https://learn.microsoft.com/en-us/sharepoint/restore-deleted-onedrive
- https://learn.microsoft.com/en-us/sharepoint/unlicensed-onedrive-accounts
- https://learn.microsoft.com/en-us/sharepoint/standard-storage-unlicensed-onedrive-accounts
- https://learn.microsoft.com/en-us/graph/api/resources/site?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/site-post-permissions?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/resources/backuprestoreroot?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/backuprestoreroot-post-onedriveforbusinessrestoresessions?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/onedrive/developer/rest-api/concepts/using-sharing-links?view=odsp-graph-online
- https://learn.microsoft.com/en-us/powershell/module/sharepoint-online/set-sposite?view=sharepoint-ps
- https://learn.microsoft.com/en-us/sharepoint/dev/sp-add-ins/sharepoint-admin-apis-authentication-and-authorization
- https://learn.microsoft.com/en-us/sharepoint/sharepoint-admin-role
- https://learn.microsoft.com/en-us/sharepoint/dev/solution-guidance/customize-onedrive-for-business-sites
- https://learn.microsoft.com/en-us/microsoft-365/admin/add-users/remove-former-employee-step-5
- https://learn.microsoft.com/en-us/microsoft-365/admin/add-users/delete-a-user
- https://support.microsoft.com/en-us/onedrive/simplified-file-transfer-for-departing-employees
- https://support.microsoft.com/en-us/sharepoint/libraries/move-or-copy-files-in-sharepoint
- https://learn.microsoft.com/en-us/graph/api/driveitem-copy?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/driveitem-move?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/resources/remoteitem?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/resources/sharepoint?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/permissions-overview
- https://learn.microsoft.com/en-us/onedrive/developer/rest-api/concepts/permissions_reference
- https://learn.microsoft.com/en-us/purview/retention-policies-sharepoint
- https://learn.microsoft.com/en-us/purview/inactive-mailboxes-in-office-365
- https://learn.microsoft.com/en-us/office365/servicedescriptions/sharepoint-online-service-description/sharepoint-online-limits
- https://learn.microsoft.com/en-us/microsoft-365/migration/cross-tenant-onedrive-migration
- https://learn.microsoft.com/en-us/sharepointmigration/what-is-supported-spmt
- https://learn.microsoft.com/en-us/sharepointmigration/mm-get-started
- https://learn.microsoft.com/en-us/microsoft-365/backup/backup-overview
- https://learn.microsoft.com/en-us/microsoft-365/backup/backup-pricing
- https://learn.microsoft.com/en-us/powershell/module/microsoft.online.sharepoint.powershell/set-spouser
- https://learn.microsoft.com/en-us/powershell/module/microsoft.online.sharepoint.powershell/set-spotenant
- https://learn.microsoft.com/en-us/powershell/sharepoint/sharepoint-online/connect-sharepoint-online
- https://learn.microsoft.com/en-us/sharepoint/manage-lock-status
- https://pnp.github.io/powershell/articles/authentication.html
- https://pnp.github.io/powershell/cmdlets/Connect-PnPOnline.html
- https://knowledge.workspace.google.com/admin/drive/transfer-drive-files-to-a-new-owner-as-an-admin

Lower confidence (community Q&A or third-party mirror; flagged inline wherever used):

- https://learn.microsoft.com/en-us/answers/questions/5334952/moving-files-from-personal-onedrive-folder-to-a-sh
- https://learn.microsoft.com/en-us/answers/questions/576975/sharepoint-online-add-shortcut-to-onedrive-ms-grap
- https://mc.merill.net/message/MC1164381 (Message Center mirror, used only for MC1164381 rollout dates)
