# Rename users and groups

Status: **group rename built (2026-11-16), not yet tried against a live DC or tenant.** Users come later;
their decisions (D2 to D5 below) are still open.

Built:

- `GroupRenameService` (routing, checks, writes, read-back) and `GroupNaming` (rules, address suggestion,
  `proxyAddresses` planning); the Rename Group dialog (`RenameGroupViewModel` / `RenameGroupWindow`).
- Entry points: Action ▸ Rename Group…, the list's right-click, the edit pane's Rename… button, the toolbar
  catalogue, and a Rename… button on a cloud group's properties (Entra list and Exchange list).
- `IDirectoryService.RenameObjectAsync`, `GetObjectGuidAsync`, `FindDnByGuidAsync`, `FindDnBySidAsync`,
  `FindAddressOwnersAsync`; Graph `GetGroupNamingAsync`, `FindAddressOwnersAsync`,
  `FindGroupIdsByDisplayNameAsync`; Exchange host ops `get-dl-naming`, `rename-dl`, `set-unified-primary`,
  `recipient-conflicts`.
- GUID-backed references: `UserTemplate.GroupGuids`, `ScenarioStep.GroupGuids`, `GroupLinks` (resolve,
  retarget, carry), `GroupReferenceMaintenance` (sweep on connect and after the editors close, retarget after
  an in-app rename or move, cloud name refresh). The scenario runner resolves groups by GUID at run time.
  One instance for the app, behind one lock that every caller waits on asynchronously: a sweep holds it while its
  directory lookups come back to the UI thread, so blocking that thread for the lock would freeze the window.
  A template that cannot be saved is logged; it never turns a finished rename into a reported failure.
- The address checks recognise the group's own copy in the cloud (by its Entra ID, or for a synced group by its
  SID through Graph, matched against Exchange's `ExternalDirectoryObjectId`), so making one of the group's own
  aliases its primary is not refused as a clash with itself. With no answer from Graph that copy cannot be told
  apart, and an Exchange holder of an address the group already has is a warning rather than a refusal.
- Fixed: a cloud distribution list opened from Entra > Groups offered edits Graph always refuses.
- The cloud pane's Rename… is hidden on an on-prem object's Cloud tab (`AllowRename = false`): the group there is
  the synced copy of the one open in the pane, whose own Rename… refreshes that pane.
- Tests: `app/build/test-group-rename.ps1`, in CI.

Graph has no contact-read permission in this app (`OrgContact.Read.All` is not requested), so mail contacts
are checked through Exchange when it is connected; the Graph contact lookup is allowed to be refused.

## Locked decisions for group rename (2026-11-16)

| Decision | Choice |
|---|---|
| Scope | Groups only: on-prem AD groups (synced or not), Entra security groups, Microsoft 365 groups and Teams, Exchange Online distribution lists, mail-enabled security groups and room lists. Dynamic distribution groups are out (the app doesn't list them). |
| Email address | Optional. Names only by default; an "Also change the email address" box sets a new primary address and always keeps the old one as a secondary. |
| Stored references | Templates and scenarios store each on-prem group's **objectGUID** next to its DN and find it by GUID when used, so renames and moves made anywhere (ADUC included) don't break them. Existing entries get their GUID while their DN still resolves. |
| Sync after an on-prem rename | A **"Sync now" button**, not an automatic sync. |
| Exchange distribution group naming policy | **Applied.** No `-IgnoreNamingPolicy`; the dialog reads back and shows the name Exchange actually set. |

How each kind is renamed:

| Kind | Written through | What changes |
|---|---|---|
| On-prem AD group (synced or not) | AD: one LDAP modify (`sAMAccountName`, `displayName`, optional `mail`/`proxyAddresses`), then one modify-DN for `cn` | The cloud copy follows after sync |
| Synced group opened from the Entra or Exchange list | Sent to its on-prem object (found by SID); the cloud refuses the write | As above |
| Entra security group, cloud-only | Graph `PATCH /groups/{id}` `displayName` | Display name |
| Microsoft 365 group / Team | Graph `displayName`, read back (the Entra naming policy exempts only Global Administrator and User Administrator); optional address via `Set-UnifiedGroup -PrimarySmtpAddress` | The team's name is the group's name; the SharePoint site URL does not change |
| Distribution list, mail-enabled security group, room list, cloud-only | `Set-DistributionGroup -Name -DisplayName` together (Microsoft: without `-DisplayName` the address book keeps the old name), optional `-PrimarySmtpAddress`, addressed by GUID | Name and display name |

Found while planning: a cloud distribution list opened from **Entra > Groups** offers display name, description
and alias edits that Graph always refuses (distribution lists and mail-enabled security groups are read-only in
Entra). Fix those rows as read-only there.

The sections below are the original research for users and groups together.

Original status: researched 2026-11-16, not started.

The request:

> I want to add a simple way to rename users and groups. Research how AD users and groups are renamed and
> the best method to go about it so that renames are complete and seamless.

No domain controller or tenant was reachable for this research. Everything here comes from Microsoft's
documentation and from reading this codebase at 2.3.9. What still has to be checked against the real
environment is listed under *Risks to validate*.

---

## Key finding: a user has six names, and AD keeps none of them in step

| Name (ADUC label) | Attribute | How it is written | Constraint |
|---|---|---|---|
| Full name: the name in the tree and in the DN | `cn` (and `name`) | Only by a modify-DN operation (`Rename-ADObject`). A normal modify of the naming attribute is refused. | 64 characters; unique among the objects in the same OU |
| Display name | `displayName` | Normal modify | 256 characters |
| First name, last name | `givenName`, `sn` | Normal modify | |
| User logon name (UPN) | `userPrincipalName` | Normal modify | Unique in the forest. DCs on 2012 R2 or later refuse a duplicate on create, modify, rename and restore (error 8648, `0x21C8`). |
| User logon name (pre-Windows 2000) | `sAMAccountName` | Normal modify | 20 characters for users, 256 for groups; unique among security principals in the domain; none of `" / \ [ ] : ; \| = , + * ? < >`; may not end with a period |
| Email | `mail`, `proxyAddresses` (and `mailNickname` if populated) | Normal modify | Each SMTP address unique across the tenant |

A group has `cn`, `sAMAccountName` (ADUC: "Group name (pre-Windows 2000)") and `displayName`, plus `mail` and
`proxyAddresses` when it is mail-enabled. ADUC leaves `displayName` empty when it creates a group.

**What a rename never changes.** The objectGUID, the SID and Entra Connect's source anchor stay the same.
Group memberships, permissions (stored as SIDs), the Exchange Online mailbox, licenses and the link to the
cloud object all carry over. AD also rewrites every DN-syntax reference to the object on its own: `member`,
`manager` and `managedBy`, with `memberOf` and `directReports` computed from those.

**What ADUC does.** Renaming a user in ADUC opens a dialog with Full name, First name, Last name, Display
name, the UPN prefix and suffix, and the pre-Windows 2000 logon name. It never touches `mail` or
`proxyAddresses` (this is ADUC's behaviour, not something Microsoft documents on one page; `Rename-ADObject`
documents the same split, `name` only, with `Set-ADUser` for the rest). In a synced tenant an ADUC rename
therefore leaves the email address behind. That is the gap this feature closes.

---

## What this tenant adds: AD to Entra Connect to Exchange Online, with no hybrid Exchange

The tenant has no on-premises Exchange ([v2.0 plan](v2.0-exchange-online-plan.md)), so every name and address
of a synced object is mastered in AD.

1. **Addresses can only change on-prem.** Exchange Online refuses `Set-Mailbox -EmailAddresses` on a synced
   user ("out of the current user's write scope ... being synchronized from your on-premises organization").
   The cloud address list is calculated from the on-prem `proxyAddresses`, plus the tenant routing address
   (MOERA, `alias@tenant.onmicrosoft.com`) and, for a licensed user, the UPN.
2. **Demote the old address, never remove it.** The old primary stays in `proxyAddresses` as lowercase
   `smtp:`, or mail sent to it stops arriving. The MOERA cannot be changed or removed, and a rename leaves it
   alone.
3. **The UPN and the email address change together.** Microsoft: changing a synced user's UPN "without
   updating corresponding email address attributes may cause the Exchange Online mailbox primary SMTP address
   to differ from the authoritative synchronized address ... update and synchronize the corresponding mail and
   proxyAddresses attributes with the UPN change." Microsoft also recommends changing the UPN whenever the
   primary address changes. The default template patterns derive all three from the logon name:
   `{sam}@{upnSuffix}` and `SMTP:{sam}@lacrossefootwear.com`.
4. **The DCs' uniqueness checks do not cover cloud-only objects.** If a cloud-only user, group or contact
   already holds the new UPN or address, Entra's duplicate attribute resiliency *quarantines* the value. It is
   reported once in the sync error email and never logged by the sync client. A quarantined UPN is replaced
   with a placeholder, `<prefix><4 digits>@<tenant>.onmicrosoft.com`. The cloud object's
   `onPremisesProvisioningErrors` shows the conflict.
5. **The UPN suffix must be a verified domain in the tenant.** Otherwise sync replaces it with
   `.onmicrosoft.com`.
6. **A licensed user's UPN change only syncs with `SynchronizeUpnForManagedUsers` enabled.** Without it the
   change is silently ignored. Once enabled it cannot be disabled.
7. **Synced group names.** The cloud group's name comes from `displayName`. A community answer (not Microsoft
   documentation) gives the rule as `IIF(IsPresent([displayName]),[displayName],[cn])`, falling back to `cn`.
   Mail-enabled groups with no display name do not sync at all (KB 2508722). A group rename should therefore
   always set `displayName`.
8. **Timing.** The Connect scheduler runs every 30 minutes. Microsoft says UPN changes can take several hours
   to propagate through Microsoft 365 (OneDrive in particular). Entra Connect reads from its own choice of DC,
   so a delta sync started straight after the rename can miss it until AD replication catches up.

---

## What the renamed user notices

Renaming only the display name has no effect on the user. Changing the logon names (UPN and pre-Windows 2000)
does, and Microsoft documents each of these effects:

- **OneDrive URL changes.** Shared links, shortcuts and favourites to it stop working. The sync client
  switches itself; restart it if it errors.
- **Office** shows the old UPN until the user signs out and back in. **Teams'** OneDrive app may show
  "404 File not found" until the user sends a file in a 1:1 chat.
- **Authenticator** shows a second account and keeps the old one. Push notifications may not arrive;
  "Check for notifications" works.
- **Intune / Company Portal devices must re-register** before management and Conditional Access work again.
- **Entra-joined Windows:** the user picks "Other user" and signs in with the new UPN. The profile folder
  `C:\Users\oldname` keeps its old name by design. Leave it: Microsoft's rename workaround is for Windows 7, and
  it warns that on Windows 10 and later the workaround stops `winget` from working.
- **SaaS apps** that create profiles at first sign-in, keyed by UPN, may create a second profile.
- **Teams meeting notes** created under the old UPN are no longer reachable from Teams (they can be
  downloaded from OneDrive).
- **`DOMAIN\oldname` stops working:** scripts, saved credentials, services running as the account, and apps
  keyed on `sAMAccountName`.

Microsoft recommends making UPN changes outside working hours, and in batches when there are many.

---

## Gaps in this app that a rename would expose

Verified in the 2.3.9 sources.

| # | Gap | Where | Consequence |
|---|---|---|---|
| G1 | No way to rename at all | `cn` and `name` are read-only: `AttributeCatalog.cs:39-40`, `:151` | The feature itself |
| G2 | Templates and scenarios store DNs | `UserTemplate.GroupDns` (`Models/UserTemplate.cs:25`), a template's default manager (`TemplateEditorViewModel.cs:146`, `:354`), `ScenarioStep.GroupDns` (`Models/Scenario.cs:96`) | A renamed group, or a renamed manager, silently drops out of every template and scenario that uses it. **Moving a group already does this today.** Microsoft's guidance: applications that store references should store the objectGUID. |
| G3 | Users are matched to their cloud account by UPN | `GraphService.GetUserByUpnAsync` (`GraphService.cs:234`), reached from the edit pane (`EditPaneViewModel.cs:426`, `:432`, `:436`, `:442`, `:1034`), `CloudTabViewModel.cs:114`, `CloudObjectDetailViewModel.cs:632`, `MainViewModel.cs:924`, `CopyGroupsViewModel.cs:190`, `NewCloudGroupViewModel.cs:135`, `ScenarioRunner.cs:461`, `:475` | Until the next sync, the Cloud tab, the Exchange tab (its identity is the UPN), cloud groups on Member Of and cloud scenario steps all report the user as not in Entra. Groups already correlate by SID (`GetGroupByOnPremSidAsync`, `GraphService.cs:426`). Graph supports `onPremisesSecurityIdentifier eq` on users too, and the pane already reads the user's SID (`EditPaneViewModel.cs:427`). |
| G4 | No 20-character cap on a user's `sAMAccountName` | `UserAttributeBuilder.SanitizeSam` (`UserAttributeBuilder.cs:198`) strips characters but never shortens | New User, Copy User and Bulk Create can already produce "christopher.vanderberg" (22). A rename built on the same rules would too. |
| G5 | The wrong escaping helper is close at hand | `EscapeRdn` (`DirectoryService.cs:1563`) is for ADSI's `Children.Add` and adds the ADsPath `\/` escape. A raw LDAP modify-DN needs `EscapeDnValue` (`:1557`). Neither escapes a leading or trailing space. | Use `EscapeDnValue`; trim names (or refuse leading and trailing spaces) |

`CloudProvisioningService.cs:59` and `:89` also call `GetUserByUpnAsync`, but for a brand-new user under the
UPN it was just created with, so a rename does not affect them.

---

## Open decisions

| # | Decision | Recommended | Alternatives |
|---|---|---|---|
| D1 | Scope | Synced on-prem AD users and groups | Also cloud-only Entra users and groups (Graph PATCH; a Microsoft 365 group's address via `Set-UnifiedGroup -PrimarySmtpAddress`; the group naming policy applies on edit), and Exchange Online distribution lists (`Set-DistributionGroup`). The DL pane deliberately blocks display name, alias and primary address today (`ExchangeService.cs:534-539`, `:572`, tips at `:641-651`). |
| D2 | What a user rename changes | One switch with two levels. **Name**: first, last, full name and display name; no effect on the user. **Name, logon and email**: also `sAMAccountName`, the UPN and the primary address. | Always change everything; or ADUC parity (no email) |
| D3 | Old addresses | Always kept, as secondaries | Optional removal. No rename case for it has come up. |
| D4 | Stored references (G2) | Store the objectGUID next to the DN and resolve by GUID when used. This survives renames and moves made in ADUC as well. Existing entries get their GUID on first load while the DN still resolves. | Rewrite matching DNs in templates and scenarios at rename time. Cheaper, and Move could share it, but it does nothing for changes made outside the app. |
| D5 | Sync after a rename | When an Entra Connect server is configured, run a delta sync and verify the result automatically | A "Sync now" button only; or leave it to the 30-minute cycle |

---

## Design, assuming the recommended answers

### Where it lives

- **Rename… (F2)** in the edit pane's header buttons, next to Move…, and on the object list's right-click
  menu. One object at a time; users and groups only. Computers are left out on purpose: a computer is renamed
  on the machine itself, which updates AD. Renaming only the AD object leaves it out of step with the
  machine, and changing its `sAMAccountName` breaks the machine's trust with the domain.
- **The pane must be clean first.** A rename writes several attributes the pane also edits (`displayName`,
  `givenName`, `sn`, the UPN, `sAMAccountName`, `mail`). With unsaved edits, run the existing
  `ConfirmLeaveAsync` (Save / Discard / Cancel) first; Cancel abandons the rename.

### The user dialog

- Fields are prefilled from the object. As First or Last changes, the derived fields follow until the
  operator edits one, using the suggestion pattern `NewUserViewModel` already uses (overwrite a field only
  while it still equals the last suggestion). Derivation goes through `UserAttributeBuilder` with a naming
  template chosen the way Copy User chooses one, including "no template".
- **Name:** first, last, full name (`cn`), display name.
- **Logon and email (the switch):** pre-Windows 2000 logon name; UPN prefix plus a suffix drop-down that
  marks suffixes not verified in the tenant; the resulting address list.
- **Address rule:** for every SMTP address whose local part is the old logon name, add the same domain with
  the new local part. The new primary is the new local part at the old primary's domain. The old primary is
  demoted to `smtp:`. Everything else (X500, SIP, onmicrosoft.com, unrelated aliases) is left as it is.
- **`mailNickname`:** update it only if it is populated and equals the old logon name.
- **Confirm:** a before-and-after list of every value that will change, in the app's existing confirm style.

### The group dialog

Group name (`cn`); pre-Windows 2000 name, defaulting to the new name; display name, set to the new name when
it is empty or equal to the old name, otherwise shown for the operator to decide. The address changes only
on request, with the same demote rule.

### Pre-flight (read-only)

These **block** the rename:

- lengths and characters (see the table at the top)
- `cn` free among the objects in the same OU (one-level search under the parent)
- `sAMAccountName` free in the domain (`FindExistingSamAccountNamesAsync`, `DirectoryService.cs:1238`)
- the UPN free in the forest (Global Catalog search)
- each new SMTP address free on-prem (`proxyAddresses=smtp:...`) and in the cloud: Graph
  `proxyAddresses/any(p:p eq 'smtp:...')` over users, groups and contacts, or Exchange `Get-Recipient`
- the UPN free in the cloud, and its suffix a verified tenant domain (Graph `/domains`)
- the operator's write rights, via `allowedAttributesEffective`

These **warn** but don't block:

- `homeDirectory`, `profilePath` or `scriptPath` contains the old logon name (AD does not rename the folder)
- the account has a `servicePrincipalName` (it is a service account; whatever runs as `DOMAIN\oldname` breaks)
- a short "what the user will notice" summary for a logon change, with Microsoft's off-hours advice
- templates and scenarios that reference the object, by name
- not signed in to Entra: say plainly that the cloud checks were skipped, rather than passing them

### The write, all on the one DC the app is bound to (`ConnectionState.Server`)

1. Read the object fresh. If `uSNChanged` moved since the dialog opened, stop and show what changed.
2. **One LDAP modify with every attribute change.** Single-valued attributes are replaced; `proxyAddresses`
   is replaced whole with the computed list. LDAP applies a modify as one atomic operation, and the DC
   enforces UPN and `sAMAccountName` uniqueness inside it, so a conflict or a refusal changes nothing.
3. **One modify-DN** to `CN=` + `EscapeDnValue(newName)` under the same parent (delete-old-RDN on, the
   default). This is a new `IDirectoryService.RenameObjectAsync` that returns the new DN, mirroring
   `MoveObjectAsync` (`DirectoryService.cs:581`).
4. Read the object back by its new DN and compare every written value.

If step 3 fails after step 2 succeeded, the names are changed but the object's name in the tree is not. Say
exactly that, keep the old DN, and offer "Retry the name change" (step 3 alone, which is safe to repeat). If
step 2 fails, nothing changed.

The order is deliberate: the attribute modify carries the constraints most likely to fail, and the modify-DN's
plausible failures (a name clash in the OU, a missing right) are both checked in pre-flight.

### After the write

- **Pane:** take the new DN and reload, as `MoveAsync` does (`EditPaneViewModel.cs:1251`). Refresh the list
  row; pop-out editor windows on the same object have to follow too.
- **Stored references:** per D4.
- **Operation log:** one record with the old and new value of every attribute, the old and new DN, the
  operator and the time (`OperationLog`).
- **Sync and verify (D5):** run `EntraSyncService.RunDeltaSyncAsync` (`EntraSyncService.cs:39`) when a
  server is configured. Then look the user up by SID until the cloud shows the new UPN and primary address,
  or a timeout. Show `onPremisesProvisioningErrors` if a value was quarantined. If everything changed except
  the UPN, point at `SynchronizeUpnForManagedUsers`.

---

## Sequencing

1. **Groundwork, worth having on its own:** SID-first correlation for users, falling back to the UPN (G3),
   wired into the Cloud tab, the Exchange tab (identity becomes the cloud user's current UPN), cloud groups
   on Member Of and scenarios; the 20-character cap (G4) with a message in New User, Copy User and Bulk
   Create; `IDirectoryService.RenameObjectAsync` with its fake.
2. **User rename:** dialog, pre-flight, write sequence, operation log.
3. **Group rename and stored references** (D4).
4. **Sync and cloud verification** (D5).
5. If D1 widens the scope: cloud-only users and groups, and distribution lists.

Tests follow the project's habit: a behavioural suite (`test-rename.ps1`) against `FakeDirectoryService` that
asserts on what is written and in what order, including the partial-failure path and each pre-flight
refusal; a render check of the dialog's bindings; and a mutation run where every mutation must be caught by a
named check.

---

## Risks to validate (no DC or tenant was available)

- Whether `SynchronizeUpnForManagedUsers` is enabled in the tenant.
- Whether `mailNickname` is populated on-prem. `AttributeCatalog` lists it, but the schema may not carry it.
- The group display-name rule on the Connect server (Synchronization Rules Editor, "In from AD - Group
  Common").
- Rename rights for delegated staff, and whether `allowedAttributesEffective` reflects them for `cn` and
  `name`.
- Whether Graph's `proxyAddresses` `eq` filter ignores the case of the `SMTP:` / `smtp:` prefix.
  `Get-Recipient` avoids the question.
- Whether Exchange still resolves stored SMTP references (`ForwardingTargetRef.Identity`,
  `DistributionGroupRef.Smtp`) once the old address is a secondary.
- Which DC Entra Connect reads from, and how long replication takes to reach it.

---

## Sources

- [Plan and troubleshoot UPN changes in Microsoft Entra ID](https://learn.microsoft.com/entra/identity/hybrid/connect/howto-troubleshoot-upn-changes)
- [How UPN changes affect the OneDrive URL and OneDrive features](https://learn.microsoft.com/sharepoint/upn-changes)
- [How the proxyAddresses attribute is populated in Microsoft Entra ID](https://learn.microsoft.com/troubleshoot/entra/entra-id/user-prov-sync/proxyaddresses-attribute-populate)
- [Identity synchronization and duplicate attribute resiliency](https://learn.microsoft.com/entra/identity/hybrid/connect/how-to-connect-syncservice-duplicate-attribute-resiliency)
- [Number added to user names and email addresses when synced](https://learn.microsoft.com/troubleshoot/entra/entra-id/user-prov-sync/number-add-username-email-address)
- [End-to-end troubleshooting of Entra Connect objects and attributes](https://learn.microsoft.com/troubleshoot/entra/entra-id/user-prov-sync/troubleshoot-aad-connect-objects-attributes) (SynchronizeUpnForManagedUsers)
- [SPN and UPN uniqueness](https://learn.microsoft.com/windows-server/identity/ad-ds/manage/component-updates/spn-and-upn-uniqueness)
- [SAM-Account-Name attribute](https://learn.microsoft.com/windows/win32/adschema/a-samaccountname)
- [AD DS maximum limits and scalability](https://learn.microsoft.com/windows-server/identity/ad-ds/plan/active-directory-domain-services-maximum-limits)
- [Object(DS-DN) syntax](https://learn.microsoft.com/windows/win32/adschema/s-object-ds-dn)
- [Object names and identities](https://learn.microsoft.com/windows/win32/ad/object-names-and-identities)
- [Rename-ADObject](https://learn.microsoft.com/powershell/module/activedirectory/rename-adobject)
- [Mail-enabled groups that have an email address aren't synchronized](https://learn.microsoft.com/troubleshoot/exchange/groups-and-distribution-lists/mail-enabled-groups-not-synchronized)
- [Microsoft Q&A: adding an address to a synced mailbox](https://learn.microsoft.com/answers/a/8271079)
- [Microsoft Q&A: group names not updating (display-name rule)](https://learn.microsoft.com/en-us/answers/questions/2184019/entra-cloud-sync-not-updating-group-names)
- [Renaming a user account doesn't change the profile path](https://learn.microsoft.com/troubleshoot/windows-client/user-profiles-and-logon/renaming-user-account-not-change-profile-path)
- [Microsoft Graph user resource](https://learn.microsoft.com/graph/api/resources/user)
- [Manage a Microsoft 365 group (change its email address)](https://learn.microsoft.com/microsoft-365/admin/create-groups/manage-groups)
