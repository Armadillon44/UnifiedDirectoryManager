# Reference brief: what Google Workspace actually does with a departing user's Drive

Prepared for the UnifiedDirectoryManager OneDrive-in-terminate-scenario design work.
Date: 2026-09-01. Every claim below is tagged with the source it came from. Claims I could not
confirm in official documentation are marked **UNVERIFIED** with the test that would settle them.

Note on URLs: `support.google.com/a/answer/*` now 301-redirects to `knowledge.workspace.google.com/*`.
Both forms are cited; the `knowledge.workspace.google.com` form is what actually serves the content today.

---

## 1. The admin-console "Transfer ownership" flow

**Where it lives.** Admin console > Menu > Apps > Google Workspace > Drive and Docs > *Transfer
ownership*. Requires the Drive and Docs administrator privilege. You give a **From user** and a
**To user**, both selected from the directory, then click *Transfer Files*.
[knowledge.workspace.google.com/admin/drive/transfer-drive-files-to-a-new-owner-as-an-admin](https://knowledge.workspace.google.com/admin/drive/transfer-drive-files-to-a-new-owner-as-an-admin)

**What moves.** Ownership of the items the departing user *owns*. Verbatim, the contents of the
resulting transfer folder are:

> "Transferred folders and files that were in the previous owner's My Drive."
> "Transferred Computers folders if the previous owner used a Drive sync client (for example, Drive for Desktop)."
> "Shortcuts to the previous owner's files whose parent folders are not shared with the new owner."

So: **files and folders both**, and the Drive-for-Desktop "Computers" backup trees as well.
[same source]

**What does not move:**

| Item | Behaviour | Source |
|---|---|---|
| Files owned by *other people* but shared with the departing user | Not transferred; the departing user never owned them. On account deletion these "files that other users own" are "automatically moved to their owners' My Drive." | [delete-or-remove-a-user](https://knowledge.workspace.google.com/admin/users/delete-or-remove-a-user-from-your-organization) |
| Files in **shared drives** | Untouched. "Files in shared drives - Your organization owns these files, not users." | [delete-or-remove-a-user](https://knowledge.workspace.google.com/admin/users/delete-or-remove-a-user-from-your-organization) |
| Items in Trash | "Items in Trash are not transferred." | [transfer-drive-files](https://knowledge.workspace.google.com/admin/drive/transfer-drive-files-to-a-new-owner-as-an-admin) |
| Google Maps files | "You can't transfer ownership of Google Maps files." | same |
| Anything owned by, or going to, an external account | "You can't transfer ownership to or from an external user, such as a personal Google Account or a user in another organization." | same |
| Orphaned files with an unknown owner | Needs a separate one-file-at-a-time admin flow (below). | [transfer-a-drive-file-from-an-unknown-owner](https://knowledge.workspace.google.com/admin/security/transfer-a-drive-file-from-an-unknown-owner) |
| Gmail | Not part of this flow at all. Gmail moves via migration or forwarding, separately. | [delete-or-remove-a-user](https://knowledge.workspace.google.com/admin/users/delete-or-remove-a-user-from-your-organization) |

**Is the My Drive / shared drive distinction the crux? Yes.** Google's model is that My Drive files
have exactly one individual owner and shared drive files are owned by the organisation. The Drive API
says it flatly:

> "An organization that owns a shared drive owns the files within it."
> "...ownership transfers are not supported for files and folders in shared drives."

[developers.google.com/workspace/drive/api/guides/transfer-file](https://developers.google.com/workspace/drive/api/guides/transfer-file)

Consequently the *entire* purpose of Google's offboarding transfer is to fix the My Drive problem. An
organisation that has already moved its work into shared drives has almost nothing to transfer at
offboarding. This is the most important structural fact in this brief, because Microsoft 365 splits
the same way: OneDrive = personal, one owner; SharePoint = organisation-owned.

**Async, and it can fail on size.** Verbatim:

> "If you transfer ownership of many files and folders at once, it might take some time to see the changes. If a transfer takes longer than 36 hours, it's unsuccessful and will stop."

[transfer-drive-files](https://knowledge.workspace.google.com/admin/drive/transfer-drive-files-to-a-new-owner-as-an-admin)

**Notification.** "The new owner, the previous owner, and the admin who started the transfer get a
confirmation email." [same source]

**The old owner keeps working access.** Verbatim: "The previous owner can still edit any transferred
files, unless you delete their account or change their permissions." [same source] The Drive API puts
the same rule in permission terms: "When a file is transferred, the previous owner's role is
downgraded to `writer`."
[developers.google.com/workspace/drive/api/guides/transfer-file](https://developers.google.com/workspace/drive/api/guides/transfer-file)

**Transfer at deletion time.** A **super admin** can transfer as part of the delete-user wizard;
lesser admins must transfer first, separately. The data types offered in the wizard are "Drive and
Docs files, primary Calendar data, and Data Studio data," with an option to "include files that aren't
shared with anyone."
[delete-or-remove-a-user](https://knowledge.workspace.google.com/admin/users/delete-or-remove-a-user-from-your-organization)

**Orphans.** A separate, single-file admin path exists for files whose owner is unknown. It needs "the
document ID of the file, which you get from the URL"; the file "must be owned by a user in your
organization"; and "These steps only work for changing the owner of a file. You can't change the owner
of a folder." The security-investigation-tool route additionally requires the file to have "been
viewed or edited in the last 180 days."
[transfer-a-drive-file-from-an-unknown-owner](https://knowledge.workspace.google.com/admin/security/transfer-a-drive-file-from-an-unknown-owner)

---

## 2. The Data Transfer API (Admin SDK)

The console flow's API equivalent. "Manages the transfer of data from one user to another within a
domain." [developers.google.com/workspace/admin/data-transfer](https://developers.google.com/workspace/admin/data-transfer)

**Shape of a call.** POST a `DataTransfer` resource to `transfers.insert()`:

    {
      "oldOwnerUserId": "<directory user id>",
      "newOwnerUserId": "<directory user id>",
      "applicationDataTransfers": [
        { "applicationId": "55656082996",
          "applicationTransferParams": [
            { "key": "PRIVACY_LEVEL", "value": ["PRIVATE", "SHARED"] }
          ] }
      ]
    }

User IDs come from the Directory API's `users.get()`, not from email addresses directly.
[developers.google.com/admin-sdk/data-transfer/v1/transfer-data](https://developers.google.com/admin-sdk/data-transfer/v1/transfer-data)

**Applications it supports** (this is the whole list on the parameters page):

| App | ID | Params |
|---|---|---|
| Google Calendar | 435070579839 | `RELEASE_RESOURCES` = `TRUE` |
| Google Docs and Google Drive | 55656082996 | `PRIVACY_LEVEL` = `PRIVATE`, `SHARED`, or both |
| Data Studio | 810260081642 | none; all files transfer |

[developers.google.com/workspace/admin/data-transfer/v1/parameters](https://developers.google.com/workspace/admin/data-transfer/v1/parameters)

**The `PRIVACY_LEVEL` trap.** `PRIVATE` means "Files that aren't shared with anyone." `SHARED` means
"Files shared with at least one other user." **If you do not specify, the default is `SHARED` only** -
the departing user's genuinely private files are silently left behind. You must pass both values to
move everything. [same source] This is the API-side equivalent of the console's "include files that
aren't shared with anyone" checkbox.

**What it does NOT transfer.** Gmail is not in the application list. Neither are Chat, Keep, Groups
membership, or shared drive content. The overview says only "Not all Google Workspace applications
work with the Data Transfer API."
[developers.google.com/workspace/admin/data-transfer](https://developers.google.com/workspace/admin/data-transfer)

**Asynchronous, polled.** The resource carries read-only `overallTransferStatusCode` ("Overall
transfer status"), a per-application `applicationTransferStatus` ("Current status of transfer for this
application"), and `requestTime`. You retrieve progress with `transfers.get(id)` or filter
`transfers.list()` by a `status` query parameter. Scopes:
`https://www.googleapis.com/auth/admin.datatransfer` (read/write) and
`https://www.googleapis.com/auth/admin.datatransfer.readonly`.
[REST reference](https://developers.google.com/workspace/admin/data-transfer/reference/rest/v1/transfers),
[transfers.list](https://developers.google.com/workspace/admin/data-transfer/reference/rest/v1/transfers/list),
and the API discovery document at
`https://www.googleapis.com/discovery/v1/apis/admin/datatransfer_v1/rest`.

> **UNVERIFIED:** the literal enum values of `overallTransferStatusCode` /
> `applicationTransferStatus` (commonly reported as `inProgress`, `completed`, `failed`). Google
> documents the *fields* but not the *value set* - I checked the REST reference, the `transfers.list`
> page and the raw discovery JSON, and none of them enumerate it.
> **Test that settles it:** run one real `transfers.insert()` in a test tenant and poll
> `transfers.get()` to completion, recording every distinct string returned; or read the enum
> constants in the Google API client library for .NET
> (`Google.Apis.Admin.DataTransfer.datatransfer_v1`).

> **UNVERIFIED:** whether `transfers.insert()` returns a resource already carrying a status, or
> whether status only appears on a subsequent `get()`. Same test settles it.

---

## 3. Suspended vs deleted account

**Suspended.** "When you suspend a user, it blocks their access to your organization's Google
services." Data remains intact, indefinitely. **The licence keeps being billed** - suspended accounts
incur the same charges as active ones.
[options-to-preserve-former-employee-data](https://knowledge.workspace.google.com/admin/getting-started/options-to-preserve-former-employee-data),
[preserve-data-for-users-who-leave](https://knowledge.workspace.google.com/admin/vault/preserve-data-for-users-who-leave-your-organization)

**Deleted.** "If you don't transfer the content to another user, the content is deleted." Specifically
for Drive: "Drive files the user owns - These files are saved for 20 days but are only accessible if
you restore the user." After 20 days the account cannot be restored and the data is gone. Files the
departing user did *not* own are "automatically moved to their owners' My Drive." Shared drive content
is unaffected because the organisation owns it.
[delete-or-remove-a-user](https://knowledge.workspace.google.com/admin/users/delete-or-remove-a-user-from-your-organization)

**Critical caveat - the 20 days is not a safety net if Vault is involved.** Verbatim:

> "After you delete an account, you can recover it within 20 days. However, account data that was marked for deletion is no longer protected by retention rules or holds and can be purged."
> "This data can't be recovered, even when you restore the account."

[delete-a-user-with-data-on-hold](https://knowledge.workspace.google.com/vault/holds/delete-a-user-with-data-on-hold)

**Separately: user-deleted files.** An admin can recover items a user deleted from Drive for up to
**25 days** after the user empties Trash; after that Google purges them.
[recover-deleted-files-and-folders-for-drive-users](https://knowledge.workspace.google.com/admin/drive/recover-deleted-files-and-folders-for-drive-users)

**The third option: Archived User (AU) licence.** A cheap licence that keeps the account and its data
alive for Vault purposes without an active seat: "Google securely maintains the user's data in
standard Google production systems. The data remains preserved by any Vault retention rules and holds
set by your organization." Google's own recommendation is to assign an AU licence "instead of, or
shortly after, suspending their account."
[options-to-preserve-former-employee-data](https://knowledge.workspace.google.com/admin/getting-started/options-to-preserve-former-employee-data),
[preserve-data-for-users-who-leave](https://knowledge.workspace.google.com/admin/vault/preserve-data-for-users-who-leave-your-organization)

---

## 4. Google Vault as the alternative to transfer

**What it retains for Drive:** "files in users' Drives and shared drives" and "files encrypted with
Google Workspace client-side encryption." It explicitly does **not** retain "folders and Drive
shortcuts," "linked files," or "external files shared with your users."
[retain-drive-files-with-vault](https://knowledge.workspace.google.com/vault/retention/retain-drive-files-with-vault)

Note the folder exclusion. Vault preserves the *documents*, not the *structure*. An export gives you
content, not the user's filing system.

**How you get content back:** Vault search and Vault export, performed by an admin with Vault
privileges. [same source]

**Does it preserve content independently of the account existing? No. This is the decisive fact.**

> "If you delete a user, you also delete that user's Google Workspace data, including data held or retained by Vault."

[preserve-data-for-users-who-leave](https://knowledge.workspace.google.com/admin/vault/preserve-data-for-users-who-leave-your-organization)

Vault retention is **account-coupled**. It is a policy layered over a live mailbox/Drive, not an
independent archive. Kill the account and the archive goes with it. Google's answer to that is the
Archived User licence: keep the account, drop it to a cheap SKU, let Vault keep holding it. A hold
also requires the user to hold a Vault licence - downgrade them to an edition without one and "the
hold no longer applies and data may be purged immediately."
[Vault FAQ](https://support.google.com/vault/answer/2539616?hl=en)

Vault is also configured wrong at your peril: "an improperly configured retention rule can cause the
immediate and irreversible purging of data."
[retain-drive-files-with-vault](https://knowledge.workspace.google.com/vault/retention/retain-drive-files-with-vault)

---

## 5. The exact semantics of Drive "ownership"

Ownership is a **permission on the file**, not a container or a location. You express it as a
permission with `role=owner`, `type=user`.
[developers.google.com/workspace/drive/api/guides/transfer-file](https://developers.google.com/workspace/drive/api/guides/transfer-file)

Reassignment rules, verbatim or near-verbatim from that guide plus the end-user doc:

- **Within one Workspace org, transfer is immediate and needs no consent.** Set `role=owner` with
  `transferOwnership=true`. The old owner is "downgraded to `writer`."
- **Between consumer accounts it is a two-step consent dance.** Old owner sets `role=writer`,
  `pendingOwner=true`; the prospective owner then sets `role=owner` with `transferOwnership=true`.
- **Same-domain only for Workspace.** "You can only transfer ownership of files and folders to someone
  in your organization." And "You can't transfer a file from your personal Google account to someone
  with a work or school account."
  [support.google.com/drive/answer/2494892](https://support.google.com/drive/answer/2494892?hl=en&co=GENIE.Platform%3DDesktop)
- **Shared drives are excluded entirely.** "Ownership transfers are not supported for files and
  folders in shared drives."
- **Service accounts cannot receive ownership.** "Service accounts don't have storage quota in Drive,
  so ownership transfers to service accounts will fail." (Directly relevant to any M365 design that
  wanted to park files under a service principal.)
- **Folders transfer independently of their contents at user level.** "When you make someone else the
  owner of a folder, you still own the files inside." The *admin* bulk flow does not have this problem
  because it enumerates and reassigns everything the user owns.
- **Storage follows ownership.** "When you transfer ownership of a file, it's no longer in My Drive
  and doesn't count towards your storage. It'll count towards the storage of the new owner."
  [support.google.com/drive/answer/2494892](https://support.google.com/drive/answer/2494892?hl=en&co=GENIE.Platform%3DDesktop)
- **Untransferable types:** Google Maps files (admin flow doc). Folders, in the unknown-owner flow.

The consequence worth internalising: **nothing is copied and nothing is moved between storage
locations.** A Google ownership transfer rewrites an attribute. The bytes never move. That is why it
is fast for metadata and why the only slow part is enumerating a large tree.

---

## 6. What the receiving user actually sees

A **transfer folder appears in the new owner's My Drive**, containing:

> "Transferred folders and files that were in the previous owner's My Drive."
> "Transferred Computers folders if the previous owner used a Drive sync client (for example, Drive for Desktop)."
> "Shortcuts to the previous owner's files whose parent folders are not shared with the new owner."

Plus these behaviours:

- If a file the departing user owned lived in *someone else's* My Drive, and that folder is shared
  with the new owner, ownership transfers but **the file stays where it is and no shortcut is
  created**.
- "Sometimes, a separate empty transfer folder is also created."
- "If no files change ownership, no transfer folder is created."
- The new owner, old owner and initiating admin all get a confirmation email.

[transfer-drive-files](https://knowledge.workspace.google.com/admin/drive/transfer-drive-files-to-a-new-owner-as-an-admin)

> **UNVERIFIED: the exact literal name of the transfer folder.** Google's documentation consistently
> says only "a transfer folder" and never states a name or naming template. I checked the admin help
> page, the end-user help page, and searched specifically for the naming format; it is not documented.
> Widely-repeated community reports say it is the departing user's email address, or
> `<email>'s files` / `<email> transfer <date>` - but every one of those is user-generated content,
> not a Google statement, so treat it as unconfirmed.
> **Test that settles it:** run one transfer between two throwaway accounts in a test Workspace tenant
> and read the folder name off the receiving My Drive; or call `files.list` as the receiver with
> `q="'root' in parents"` immediately after and read the `name` field.

The design point stands regardless of the literal string: **the receiver gets one clearly-labelled,
self-contained folder at the top of their own My Drive, not a scattering of files merged into their
existing tree.** That containment is the behaviour worth copying.

---

## What "as close as possible" would have to mean

Ranked by how much it matters to an admin running an offboarding.

1. **One admin action, initiated from the offboarding step, that reassigns everything the leaver
   personally owned to a named colleague, without the leaver's cooperation.** This is the whole
   feature; everything else is refinement. In M365 terms: the departing user's OneDrive being made
   available to the manager, driven by the same wizard that disables the account.

2. **The receiver gets it as one contained, obviously-labelled folder in their own storage** - not a
   merge, not a separate portal, not a link they have to keep clicking. Google's transfer folder is
   the model. If the M365 answer turns out to be "the manager becomes secondary admin on the OneDrive
   URL," that is materially *worse* than Google, and the gap should be stated to the user plainly
   rather than papered over.

3. **Honest scoping: personal storage only, and say so.** Google transfers My Drive and refuses shared
   drives, because the org already owns shared drives. An M365 equivalent must draw the identical line
   at OneDrive vs SharePoint, and the UI must tell the admin that SharePoint/Teams content is
   deliberately untouched - otherwise admins will assume the terminate scenario handled everything.

4. **Cover the private files, and default to covering them.** The single most damaging Google default
   is `PRIVACY_LEVEL` defaulting to `SHARED` only. If our equivalent has any "shared only" notion it
   must default to *everything*, or force an explicit choice. Files nobody else can see are exactly
   the files that vanish silently.

5. **Asynchronous with a real status you can poll and surface.** Google's transfer is a job with an
   ID, a status and a 36-hour ceiling. An offboarding tool that fires and forgets will produce "we
   transferred it" claims that are false for large accounts. The scenario needs a persisted job
   record, a status the admin can come back to, and an explicit failure state.

6. **Notify the parties.** Google emails the receiver, the leaver and the admin. At minimum the
   receiver must learn that they now own someone else's files, and where they are.

7. **Preserve the leaver's residual access as an explicit choice.** Google leaves the old owner as
   `writer` until you delete or re-permission them. For an offboarding that is usually the *wrong*
   default - the terminate scenario almost certainly wants the leaver's access removed. Worth
   deviating from Google deliberately here, and documenting the deviation.

8. **Storage accounting must be visible.** In Google the quota follows ownership onto the receiver.
   Any M365 approach that genuinely moves bytes will do the same to the manager's OneDrive quota, and
   an admin needs warning before dropping hundreds of GB on someone.

9. **Handle "the leaver owned things sitting in other people's areas" gracefully.** Google transfers
   those in place and only creates shortcuts where the receiver cannot see the parent. The principle
   to copy: do not rip files out of collaborative locations just to satisfy a transfer.

10. **Retention as a separate, parallel answer - and understand it is not equivalent.** Google's Vault
    story is explicitly *account-coupled*: delete the account and the held data dies with it, hence
    the Archived User licence. Any M365 "just leave it under retention" proposal must be tested
    against the same question - does the content survive the identity being deleted, and for how long
    - before it is offered as an alternative to transfer. Transfer and retention answer different
    questions: transfer is about *the business keeping working*; retention is about *the business
    being able to answer a lawyer later*. Google ships both and does not pretend either substitutes
    for the other.

**What is NOT worth copying:** the 36-hour hard failure, the folders-transfer-but-not-their-contents
quirk at user level, and the "sometimes an extra empty folder appears" behaviour.

---

## Source list

Official Google documentation, all fetched 2026-09-01:

- https://knowledge.workspace.google.com/admin/drive/transfer-drive-files-to-a-new-owner-as-an-admin (was support.google.com/a/answer/1247799)
- https://knowledge.workspace.google.com/admin/users/delete-or-remove-a-user-from-your-organization (was support.google.com/a/answer/33314)
- https://knowledge.workspace.google.com/admin/getting-started/options-to-preserve-former-employee-data
- https://knowledge.workspace.google.com/admin/security/transfer-a-drive-file-from-an-unknown-owner (was support.google.com/a/answer/12281293)
- https://knowledge.workspace.google.com/admin/drive/recover-deleted-files-and-folders-for-drive-users
- https://knowledge.workspace.google.com/vault/retention/retain-drive-files-with-vault (was support.google.com/vault/answer/7657465)
- https://knowledge.workspace.google.com/admin/vault/preserve-data-for-users-who-leave-your-organization (was support.google.com/vault/answer/2736622)
- https://knowledge.workspace.google.com/vault/holds/delete-a-user-with-data-on-hold (was support.google.com/vault/answer/11542069)
- https://support.google.com/vault/answer/2539616 (Vault FAQ)
- https://support.google.com/drive/answer/2494892 (end-user ownership transfer)
- https://developers.google.com/workspace/drive/api/guides/transfer-file
- https://developers.google.com/workspace/admin/data-transfer
- https://developers.google.com/admin-sdk/data-transfer/v1/transfer-data
- https://developers.google.com/workspace/admin/data-transfer/v1/parameters
- https://developers.google.com/workspace/admin/data-transfer/reference/rest/v1/transfers
- https://developers.google.com/workspace/admin/data-transfer/reference/rest/v1/transfers/list
- https://www.googleapis.com/discovery/v1/apis/admin/datatransfer_v1/rest (API discovery document)

No blog or community sources were relied on for any factual claim. Community reports are referenced
once, in section 6, and are explicitly marked as unconfirmed.
