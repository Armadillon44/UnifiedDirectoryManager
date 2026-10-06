# Unified Directory Manager

A native Windows desktop tool for managing **Active Directory** and **Entra ID (Microsoft 365)** from
one place — a modern take on the classic *Active Directory Users and Computers* (ADUC) snap-in, plus
the things ADUC never had: reusable **new-user templates**, GUI **advanced search**, **bulk edits**,
**cloud (Entra) group** management, and **Exchange Online** mailboxes and distribution groups.

Built with **WPF on .NET 10**. Ships as a **self-contained, single-file `.exe`** for **win-x64** and
**win-arm64** — no .NET install required on the target machine (Windows 10 / 11).

> **v2.3.5 — wait longer, start from nothing, and open where you want.**
>
> **The cloud retries are yours to set.** Exchange Online answering “couldn't find object” for a user
> that had just been created was ruining new-user flows. The retry was 5 attempts 8 seconds apart; it
> is now **10 at 10 seconds**, and both numbers are editable under **Settings ▸ Retries**, from 5–50
> attempts and 5–60 seconds. Entra and Exchange are set **separately**, because their per-attempt cost
> differs by about ninety times: a Graph failure returns in well under a second, while an Exchange call
> that *hangs* costs the full 90-second operation budget before the wait even starts. The page shows
> what the numbers cost in words as you type them, because “50 attempts, 60 seconds apart” is not
> something anyone can weigh up.
>
> **And a Cancel**, without which a longer retry is a trap rather than a setting. It interrupts the
> *wait* as well as the attempts, and it does not undo the account: cancelling reports that the user
> exists and which groups were added before the stop, because a flat “cancelled” sends you looking for
> an account that is already there.
>
> **New User can start from nothing.** “No template — start from scratch” is the first entry in the
> dropdown, and it reveals a Details block — job title, department, company, office, telephone,
> description — plus **Add attribute…** for the rest of the catalogue. New User used to *require* a
> template for a narrower reason than it looked: the common name, logon name, display name and UPN were
> always derived from the names you type, and the template only layered defaults on top.
>
> **You choose which template each window opens on.** An **Open new users on** picker in the template
> editor, honoured by New User, Bulk Create and the Copy user naming picker, and it saves as you choose
> it. “No template” can be the default too. It says where a window *opens*, not what it keeps —
> choosing something else stays chosen.
>
> **The password and access pass are where you are looking.** Both now appear in a highlighted panel
> directly above the progress pane, each with its own Copy button. They were already on the form, but
> in the left scrollable column — so with your eyes on the progress log, the two values that must be
> captured before the window closes were off screen unless you knew to scroll. The New User progress
> pane also spans the full width of the window now, as Copy user's already did.
>> **v2.3.4 — the AD Recycle Bin, and a cloud sign-in that tells you.**
>
> Two things the app could not tell you before: what has been deleted, and whether its own cloud
> sign-in still works.
>
> **View the AD Recycle Bin.** **Tools ▸ Deleted Objects…** lists what has been deleted from the
> domain and is still recoverable — what it was called, which container it came out of, and when.
> Read-only for now. The line above the list distinguishes four situations that all produce an empty
> grid and mean entirely different things: the Recycle Bin is on, it is off and these are tombstones,
> you are not allowed to read the container (a **Domain Admin** right by default), or the domain
> controller refused the request. The control is sent as critical precisely so a DC that will not
> honour it says so rather than answering “nothing has been deleted”.
>
> **The cloud sign-in is checked at startup.** If the saved Entra ID sign-in has stopped working you
> are told when the app opens, not when the first cloud operation of the day fails. It checks by
> actually asking for a token rather than by trusting the saved record — the record outlives the token
> it was saved with, surviving the refresh token ageing out, consent being withdrawn, and the account
> being disabled. It never opens a sign-in window by itself, stays silent on a machine where Entra ID
> was never configured, and does not claim you are signed out when it could not tell.
>
> **The creation record says what the account was created with.** New User and Copy user now list
> every attribute, by LDAP name, in the progress pane and the saved log. Passwords and Temporary
> Access Passes are still never written, and that now covers the attribute list: anything named like a
> secret shows as `(not recorded)`.
>
> **v2.3.3 — where everything lives, decided once.**
>
> An audit of where every function in the app could be reached from, and the nine findings it produced.
> The complaint behind it: some things were only on right-click, some only in the menu bar, some in
> both, and knowing where one thing lived predicted nothing about the next.
>
> **The menu bar is now File / Action / View / Tools / Help**, laid out the way ADUC and every MMC
> snap-in lay one out. **File** is about the list you are looking at, **Action** about the thing you
> have selected, **View** about the tree, **Tools** about finding things and about the app itself.
> **The Edit menu is gone** — everything in it moved to Action, along with the creates that were in
> File and four things that were previously only on right-click.
>
> Three rules now hold, and are enforced by tests rather than by anyone remembering:
> - **Every command in the app is in the menu bar**, including the cloud list's, which were in no menu
>   at all. An entry that does not apply greys out rather than vanishing.
> - **The right-click menu is generous**, and may shorten a label but never swap in a different word.
> - **The toolbar owns nothing.** Everything on it is also in the menus — which became a safety
>   requirement the moment buttons could be removed.
>
> **Keyboard shortcuts**, which the app had none of: `F5`, `Ctrl+F`, `Ctrl+N`, `F1`, `Del` and
> `Alt+Enter`. The menus advertise them. Delete is scoped to the object lists, so it cannot act on
> something you are not looking at while you browse the tree.
>
> **A customisable toolbar.** Right-click it ▸ **Customise Toolbar…** to pick from 31 items, arrange
> them and group them with separators. Per-user, so it follows you between workstations.
>
> **One command, one label.** Sixteen commands used different words in different places; opening an
> object had four names and is **Properties…** everywhere now. **Reset Password…** joined the Action
> menu.
>
> **Fixed: menus opened in mirror image on some machines.** Windows mirrors menus for left-handed pen
> use, switched on by the Tablet PC handedness setting — so it appears on Surfaces, touchscreen
> laptops and anything with a digitizer, usually without anyone choosing it.
>
> **Also:** export group members to CSV (and append to an existing one), save the steps taken when
> creating or copying a user, and an empty group is now told apart from one you cannot read.
>
> **v2.3.2 — a correctness release. 24 defects found by a full code review, fixed and covered by tests.**
>
> No new features. Everything here is something the app was already doing wrong, most of it quietly. The
> review swept the whole codebase, confirmed 28 defects by hand, and this release closes 24 of them; the
> four left are documented with reasons in `docs/code-review-2.3.1-findings.md`.
>
> **Operations that said they had worked, and had not.**
> - **Removing somebody from a group could silently do nothing.** The check for whether a change was needed
>   compared distinguished names case-sensitively, while AD compares them case-insensitively. A name that
>   arrived in different casing — from a CSV, from Entra, from an older export — made the removal skip and
>   report success while the person stayed in the group. Adding one duplicate could also throw away every
>   other add in the same batch. Membership changes now go straight to the directory and let it decide.
> - **"Remove all cloud groups" reported success when the membership read had failed** or been cut short,
>   so a termination could report a clean finish over groups nobody had looked at.
> - **Cloud member and membership lists stopped at 200** and presented that as the whole set.
> - **A cancelled scenario was recorded as a success**, including one interrupted part-way through a target.
> - **Editing two objects quickly could apply one object's changes to the other's** distinguished name.
>
> **Things that hung, or could not be stopped.**
> - **A hung Exchange Online call blocked every Exchange feature** for the rest of the session. The timeout
>   that was supposed to prevent that could never fire, and the Cancel buttons could not reach it.
> - **Closing the window during an Exchange operation froze the app** for up to three minutes on the way out.
> - **The Entra Connect sync had no time limit at all**, so an unresponsive server stalled New User, Copy
>   User and Bulk Create until the app was killed. It now gives up, says so, and stops the helper it started.
>
> **Wrong identity.**
> - **An Exchange Online session outlived the administrator it belonged to.** Signing out and back in as a
>   different admin in the same tenant kept the first admin's session, so mailbox and distribution-list
>   changes ran with their permissions and under their name in the audit log.
> - **Changing the tenant kept the previous tenant's signed-in account**, which is what the check above
>   depends on.
>
> **Data and input that went missing.**
> - **A damaged settings file destroyed every pinned favourite.** Settings are now written to a temporary
>   file and renamed into place, and an unreadable one is kept aside and reported at startup rather than
>   quietly replaced with defaults.
> - **Pasting an Outlook address line silently dropped people.** `"Doe, Jane" <jane@x.com>, John Smith
>   <john@y.com>` kept only the last address, and the lost person was not counted anywhere.
> - **Advanced Search's "pick OUs" threw away the scope you already had** — it opened with nothing ticked
>   and returned nothing.
> - **Favourites vanished after reconnecting**, and pinning went on editing a list that was no longer shown.
> - **Late error messages were thrown away** when no window was open — "Password not set", a failed import,
>   a failed sync.
>
> **Wrong thing on screen, or in the request.**
> - **Advanced Search's "Contacts" always came back empty**, and "Users" quietly included mail contacts —
>   which is how a contact could be set as somebody's manager.
> - **Re-importing the app's own CSV export failed every row.** Columns the directory owns, like `Name`, are
>   now named and skipped before the import runs instead of being sent to the domain controller.
> - **Copy user and New User disagreed about the same template**, because Copy user had its own copy of the
>   naming-pattern code. It had drifted, and could write a user principal name with a trailing space.
> - **Unticking every cloud group still forced the Entra sync on**, with its checkbox disabled, and then
>   refused to create the user without an Entra Connect server for a sync nothing needed.
> - **Re-selecting the same object doubled its licences, memberships and members** on screen.
> - **An ambiguous mailbox lookup returned a mailbox stitched together from several**, and the convert and
>   forwarding actions then acted on it.
> - **Favourites and the cloud sections could send an internal marker to a domain controller** as if it were
>   a real name, from New User, Bulk Create and Advanced Search.
>
> **Under the hood.** The app now has 19 test suites and 942 assertions, run by GitHub Actions on
> every push. Every fix above was checked by reverting it and confirming the tests caught it. One defect
> still got through that — see "What a real domain controller changed" in the findings document, which is
> worth reading before trusting a test that stands in for a live system.
>
> **v2.3.1 — an Employee ID at creation, and a favourites fix.**
> - **Employee ID** (`employeeID`) can now be set while creating a user, on both the **New User** wizard and
>   **Copy user…**. It was already editable afterwards, searchable, a list column, and importable per row from
>   Bulk Create's CSV (an `Employee ID` column) — the create dialogs were the only place it could not be typed.
>   Bulk Create's **Add user…** form takes it too, and a row keeps it when reopened for editing.
> - **Copy user… never inherits it.** An employee ID identifies a person, so the field starts blank and asks
>   for the new user's own. **Save as template…** no longer captures one either: a template applies to
>   everyone created from it, and its attribute rows are ticked by default, so a captured employee ID would
>   have been handed silently to every future hire made from that template.
> - **Fixed: the Favourites row asked a domain controller to enumerate a synthetic name** ([issue #8](../../issues/8)).
>   It answered `0000208F … BAD_NAME` on `fav:root`, and because the row was emptied before the failed call,
>   collapsing and re-expanding Favourites made the pins disappear until something rebuilt them.
> - **A failed folder load no longer empties the folder.** Children are now swapped in only on success, so a
>   domain controller hiccup leaves the tree as it was instead of reading as "this OU is empty now".
>
> **v2.3.0 — an Exchange Online section, batch member adds by pasting a list, and pinned favourites.**
>
> **Exchange Online: a section of its own, and editable groups.**
> - **New nav section** with **Mailboxes** and **Distribution groups** lists, searched server-side and capped
>   (Exchange has no continuation token, so a capped list says so rather than passing itself off as the whole set).
> - **Mailbox properties** — identity, type, addresses, forwarding, quotas, hold and retention, archive, and
>   protocol access. **Size and usage** is a button rather than part of the load: it reads the mailbox store
>   rather than the directory and is the documented way to exhaust the throttling budget.
> - **Mailbox actions** — convert **Regular ↔ Shared**, set/clear **forwarding**, and manage **delegates**,
>   now available beside the mailbox as well as on the AD **ExOL** tab. One shared control, so the two cannot drift.
> - **Distribution group properties, and editing them.** Alias-adjacent scalars (description, MailTip, hidden
>   from address lists, room list, join/depart restriction, external senders, BCC, delivery reports, auto-replies,
>   moderation), the **secondary email addresses**, and the six **recipient lists** — owners, send on behalf,
>   accept/reject senders, moderators, bypass — each edited through a picker seeded with what is already there.
>   Those lists are written as an **add/remove difference**, never as a replacement, so an entry Exchange will
>   report but not accept back — a role group such as *Organization Management* owning the group — is left alone
>   instead of blocking the whole edit.
> - **Create cloud-only groups** — security and Microsoft 365 in Entra, distribution lists and mail-enabled
>   security groups in Exchange Online (see the routing table below).
> - **A "?" beside every property row** explaining what the setting is, and why it can't be changed when it can't.
>
> Some things are deliberately **not** editable, because the service will not honour them:
> **message size limits** (Microsoft documents those parameters as on-premises only), **display name** (a naming
> policy rewrites it on save, and admins are exempt so it behaves differently for others), the **primary address**
> and **alias** (an address policy makes an alias change rewrite the primary address, which breaks the old one),
> and **hidden membership** and **room list**, which Exchange can turn on but not off.
>
> **Add members by pasting a list.** A **Paste a list…** button sits beside *Add members…* on an AD group, an
> Entra group, and a distribution group. Paste up to **100 entries** — addresses, logon names, `Doe, Jane`,
> plain names, bulleted or numbered lines, quoted spreadsheet cells, or a whole Outlook **To:** line with several
> recipients on it — and review what every line resolved to before anything is written.
> - **A line resolves itself only when an exact match returns exactly one person.** A near match, or two people
>   who share a display name, is shown as a choice for you to settle. There is no recycle bin for group
>   membership, and the audit trail records the add as a deliberate act by whoever ran it.
> - Rows reading **Not found**, **Already a member**, **This is the group itself**, **Same person as line *n***,
>   or still awaiting a choice are **skipped**. Lines that could not be read, repeats, and anything past the
>   100-entry limit are counted in the status line rather than dropped silently, because a discarded line looks
>   exactly like a line that found nobody.
> - Names are resolved against **the directory that owns the group's membership** — LDAP, Graph or Exchange —
>   never against whichever one the pasted text happens to look like. A cloud-only user has no distinguished name
>   for an on-prem group, and a user with no mailbox cannot join a distribution list.
> - **Add only, one group at a time.** The group's own editor still confirms the full list of people before the
>   write, so the last thing you see is who is being added, not how many.
>
> **Pinned favourites ([issue #7](../../issues/7)).** Right-click an OU, container or the domain root and choose
> **Pin to Favourites**, or pin a **saved search** from the *Pin* button in Advanced Search. Pins collect in a
> **Favourites** row at the top of the tree, ordered with **Move up** / **Move down**.
> - A favourite is a **reference, not a copy**: it stores the distinguished name, or the saved search's name, and
>   resolves it when clicked. Editing a saved search therefore changes what its pin does — nothing goes stale
>   behind your back.
> - Pins are kept **per domain**, since a distinguished name only means something in the domain it came from.
>   Connecting to another domain shows that domain's pins; the first domain's are still there when you go back.
> - A pin whose target has been renamed, moved or deleted is **greyed with a ⚠ and left in place** for you to
>   unpin. A domain controller being briefly unreachable is not evidence that an OU no longer exists, and a
>   silently shorter list is not something anyone notices in time to object.
> - The **Entra ID** and **Exchange Online** sections are deliberately not pinnable — they are already top-level
>   and one click away, so a pin would only be a second route to the same place. Individual users, groups and
>   computers are out of scope for this pass. Favourites need an on-prem connection, so a cloud-only session has
>   no Favourites row.
>
> **Double-click a member to open it.** On the **Members** tab of an AD group's edit pane or a cloud group's
> detail pane, double-clicking a member opens **its own non-modal properties window**. The group you were working
> on stays where it was, and several members can be open at once. It is not wired to the *Member Of* tab.
>
> **Also fixed.**
> - **New-user provisioning now retries the steps that only failed because the directory lagged.** Entra group
>   adds, Exchange distribution group adds and the Temporary Access Pass get up to four retries, eight seconds
>   apart, per item, and the progress log says so while it waits. **Only** the five wordings that mean "not
>   visible to the service yet" or "too many concurrent writes" are retried. A permissions or RBAC failure, a bad
>   identity, a licensing failure or a policy rejection fails immediately, exactly as before — a broad retry turns
>   a real, permanent failure into a long wait ending in the same error.
> - **Copy groups to user** sent every cloud group through Graph, so copying a user who belonged to *any*
>   distribution list failed on it. Each group now goes to the backend that owns it, and the rows label an
>   Exchange-managed group as *Exchange* rather than *Cloud* before you apply anything. The same routing bug in
>   the cloud detail pane's membership writes is fixed too.
> - **Remove selected** on the cloud pane's **Members**, **Licenses** and **Member Of** tabs answered
>   "Select one or more … to remove" and removed nothing, whatever was highlighted. All three tabs had it; the
>   on-premises AD pane was never affected.
> - The cloud pane's **Licenses**, **Member Of**, **Members** and **Actions** tabs rendered **blank** — the tab
>   strip was applying its property-section template to controls that already had one. A regression from this
>   release's own flattening of that strip into a single row, caught before it shipped: **2.2.0 is unaffected**,
>   because its tabs were still nested under a *Properties* tab.
> - **Progress logs are selectable.** Every line in New User, Bulk create, Copy user, Copy groups and the
>   scenario runner can be selected and copied with right-click → *Copy*; the scenario progress window also has a
>   **Copy log** button. Attach that text to a ticket instead of a screenshot.
>
> **v2.2.0 — Group management ([issue #3](../../issues/3)).** Create, modify, and delete groups:
> - **Create** from the tree right-click (*New Group here…*) or **File ▸ New Group** (which asks for the OU):
>   *Group scope* (Global / Domain local / Universal), *Group type* (Security / Distribution), description,
>   initial members, and *protect object from accidental deletion*. The logon name is derived from the group
>   name and stays editable. *Managed by* is set afterwards, from the group's General tab.
> - **Modify** a group's **Group scope**, **Group type**, and ***Managed by*** from the edit pane. Illegal scope changes
>   (Global ↔ Domain local) are caught before the write instead of failing at the directory.
> - **Delete** one *or many* groups behind a confirmation that lists exactly what will go; deleting several
>   also makes you type the count. A default-ticked checkbox first writes a **record**: all attributes to a
>   text file, and the full membership — with distinguished names, so it can be
>   re-added — to a CSV. If the membership can't be confirmed complete, the record says so rather than recording
>   an authoritative-looking zero.
> - New **Group type** column in the object list (e.g. *Security · Global*).
>
> **v2.1.1 — OU management from the tree.** Right-click an OU (or the domain root) in the directory tree to
> **create** a child OU, view/edit its **Properties** (name, DN in both LDAP + canonical form, description, and
> the *protect from accidental deletion* flag), or **delete** it behind a two-step, type-to-confirm guard. Also
> fixes the **Member Of** tab so distribution lists show their source as *Exchange* (and can be removed there).
>
> **v2.1.0 — Unified group picker + saved searches.**
> - New-user templates and every create flow now use **one group picker** that spans **on-prem AD, Entra ID
>   (cloud), and Exchange Online distribution groups** in a single bucket — each is applied through the right
>   backend at creation (LDAP / Graph / `Add-DistributionGroupMember`).
> - **Advanced Search** can now **save and recall searches and raw LDAP queries** (export/import to share),
>   so common lookups are one click away.
>
> **v2.0.0 — Exchange Online.** An **ExOL** tab and matching scenario steps for pure-cloud tenants: convert
> mailboxes **Regular ↔ Shared**, set/clear **internal forwarding**, manage **delegation** (Full Access /
> Send As / Send on Behalf), **delegate a departing user's mailbox to their manager**, and remove members
> from **cloud distribution lists / mail-enabled security groups** (which Microsoft Graph can't touch) via
> the Exchange Online module.

---

## Download

Two ways, from the [**Releases**](../../releases/latest) page. Either way the .NET runtime is bundled,
so there is nothing to install first.

| | 64-bit Intel/AMD (most PCs) | ARM64 (Snapdragon / Surface Pro X) |
|---|---|---|
| **Installer** (per-machine, shows in Apps & Features) | `UnifiedDirectoryManager-x64-<version>.msi` | `UnifiedDirectoryManager-arm64-<version>.msi` |
| **Portable** (single file, nothing installed) | `UnifiedDirectoryManager-<version>-win-x64.exe` | `UnifiedDirectoryManager-<version>-win-arm64.exe` |

The `.exe` is portable — put it anywhere and run it. Either way your settings, templates, and logs live
under `%APPDATA%\UnifiedDirectoryManager\` and follow your Windows profile.

## What it does

- **No domain join required.** Every operation binds with the **domain FQDN + credentials you enter** —
  never the machine's own domain context. Works from **Entra-joined-only** clients that have network
  line-of-sight to a domain controller (enter a DC hostname/IP directly; DNS-SRV discovery is optional).
- **Users, computers, and groups** — browse the OU tree, filter/sort/search, choose columns, edit on a
  friendly-name UI backed by real `lDAPDisplayName` attributes, and manage group membership. Double-click a
  member to open it in its own window, so the group you are working on stays put. **Every write is confirmed
  with a diff.**
- **Group management** — **create** groups (tree right-click or Action ▸ New ▸ Group…), **modify** *Group
  scope*, *Group type* and *Managed by*, and **delete** one or many groups. Deleting several at once
  makes you type the number of objects first, and the confirmation offers — ticked by default — to save
  a full record of each group and its members before anything goes.
- **Batch member adds by pasting a list** — **Paste a list…** takes up to 100 addresses, logon names,
  `Doe, Jane` entries or plain names (an Outlook *To:* line included) into an AD, Entra or distribution group,
  and shows what every line resolved to before anything is written. A line resolves itself only on an exact,
  single match; anything else is a choice you settle, and anything not found, already a member, or unanswered
  is skipped.
- **OU management (tree right-click)** — **create** OUs, view/edit **Properties** (DN in LDAP + canonical form,
  description, accidental-deletion protection), and **delete** an OU behind a two-step, type-to-confirm guard.
- **New-user templates + Bulk Create** — target OU, UPN suffix, country, token-driven attribute defaults,
  and groups from a **single unified picker** (on-prem AD + Entra cloud + Exchange distribution groups);
  provision many users in one phased pass with per-user passphrases and Temporary Access Passes.
- **Advanced search → Bulk Edit** — build conditions by friendly attribute name (or raw LDAP), **save and
  recall** searches/queries, then set attributes / enable-disable / add-remove groups across all matches.
- **Pinned favourites** — pin the OUs and saved searches you use most to a **Favourites** row at the top of the
  tree, reorder them, and reach them in one click instead of a drill-down. Pins are kept **per domain**, since
  a distinguished name only means something in the domain it came from, and a pin whose target has been
  renamed or deleted is greyed out and left in place rather than quietly dropped.
- **Entra ID (cloud)** — manage a synced object's Entra groups and account state, and run an **Entra
  Connect delta sync** on demand.
- **Exchange Online** — its own nav section for **mailboxes** and **distribution groups**: read every property
  Graph cannot describe, run mailbox actions (convert, forwarding, delegates), and **edit** a distribution
  group's settings, addresses and recipient lists. Also reachable from the AD **ExOL** tab, which is the path
  for a hybrid user who has no row in the Exchange list.
- **Deleted objects (AD Recycle Bin)** — **Tools ▸ Deleted Objects…** lists what has been deleted and
  is still recoverable, with what it was called, where it was deleted from and when. Read-only. It says
  which of four situations an empty list means, including the likeliest one: reading the Deleted Objects
  container is a **Domain Admin** right by default.
- **A menu bar laid out like ADUC's** — File / Action / View / Tools / Help, with **every** command in
  it, **keyboard shortcuts** the menus advertise, and a **customisable toolbar** whose every item is
  also in the menus, so removing a button can never remove the only way to do something.
- **Scenarios** — compose ordered, repeatable multi-step actions (e.g. a Terminate-User flow: disable →
  remove groups → revoke cloud sessions → convert mailbox to shared → forward → delegate to manager →
  move to an OU), run them across many targets, and save a re-addable **operation log**. Licences are
  not a scenario step — take one back from the cloud pane's **Licenses** tab, which warns you first
  when the user still has a regular mailbox.

### Which group goes through which service

Group management is split across three backends, and not by choice: Microsoft Graph refuses the Exchange-owned
kinds. It answers a distribution list's membership with **403**, and a create with **400 — "Cannot Create a
mail-enabled security groups and or distribution list"**. The app routes each kind to the service that owns it.

| Group kind | Create | Membership | Properties |
|---|---|---|---|
| On-prem AD (any scope) | LDAP | LDAP | LDAP |
| Entra security | Graph | Graph | Graph |
| Microsoft 365 (unified) | Graph | Graph | Graph |
| Distribution list | Exchange Online | Exchange Online | Exchange Online |
| Mail-enabled security | Exchange Online | Exchange Online | Exchange Online |

The Exchange-owned kinds are reached with `New-`/`Get-`/`Set-DistributionGroup` and
`Add-`/`Remove-DistributionGroupMember`. The same split decides where a **pasted list of members** is resolved,
and which backend *Copy groups to user* applies each membership through. A group **synced from on-premises AD**
is read-only in the cloud whichever kind it is — Exchange rejects every write against a synced object — so those
rows say so instead of failing at the service.

## Prerequisites

- **To run:** nothing — the self-contained `.exe` bundles the .NET 10 runtime. (Building from source
  needs the .NET 10 SDK + Windows Desktop runtime.)
- **For Exchange Online (ExOL) features**, on each machine that uses them:
  1. **PowerShell 7 (`pwsh`)** installed.
  2. The **`ExchangeOnlineManagement`** module
     (`Install-Module ExchangeOnlineManagement -Scope CurrentUser`).
  3. In Entra, the app registration granted the **delegated `Exchange.Manage`** permission (with admin
     consent) — **not** `Exchange.ManageV2`.
  4. The signing-in admin holds an Exchange RBAC role. They differ per operation:
     - **Reading** mailboxes and distribution groups — **Recipient Management** (or Exchange Administrator).
     - **Changing a distribution group** — its membership or its settings — additionally needs
       **Organization Management**, or the **Security Group Creation and Membership** role. Those writes pass
       `-BypassSecurityGroupManagerCheck`, because a group's owner is usually a role group that no individual
       is a "manager of", and without it every edit fails for a reason unrelated to the edit.
     - **Creating** a distribution list or mail-enabled security group — the **Distribution Groups** role.
       Recipient Management does not carry group creation.

  An RBAC refusal is shown with Exchange's own wording kept intact and a hint appended, rather than replaced
  by a fixed sentence naming one role — a tenant policy rejection reads a lot like a permissions error and
  needs to reach you verbatim.

  See the [Wiki](../../wiki) for the full ExOL setup and rationale.

## Build from source

```powershell
git clone https://github.com/Armadillon44/UnifiedDirectoryManager.git
cd UnifiedDirectoryManager/app

dotnet build src/UnifiedDirectoryManager/UnifiedDirectoryManager.csproj -c Release   # compile
dotnet run  --project src/UnifiedDirectoryManager                                    # run from source
./build/publish.ps1                                                                  # self-contained exes -> app/dist/
```

`publish.ps1` produces `app/dist/win-x64/UnifiedDirectoryManager.exe` and
`app/dist/win-arm64/UnifiedDirectoryManager.exe`.

## Security notes

- LDAPS certificate validation is **on by default**; non-LDAPS binds use Kerberos/NTLM with **signing &
  sealing**. Passwords are **never written to the log**.
- Credentials are stored (optionally) in the **Windows Credential Manager**, per Windows user, encrypted
  by the OS.
- Secrets passed to PowerShell (Entra sync, Exchange token) go via **stdin**, never the command line;
  inputs are escaped to prevent script injection. CSV export neutralizes spreadsheet formula-injection.

## Documentation

- **In-app:** *Help ▸ View README* shows the full user guide.
- **[Repository Wiki](../../wiki)** — feature guides, the Exchange Online setup, and the scenario engine.
- **[docs/](docs/)** — the design plans and the decisions locked into them: the v2.0 Exchange Online plan and
  its engineering spike findings, the Exchange Online navigation and cloud-groups plan, group management,
  paste-a-list member adds, and pinned favourites. **`ui-placement-audit.md`** is the one behind 2.3.3 —
  where every function in the app could be reached from, the nine findings, and the three rules that now
  hold because tests enforce them.

## License / ownership

Internal tooling for **LaCrosse Footwear, Inc.** © the authors. See the About dialog in-app for version
and build details.
