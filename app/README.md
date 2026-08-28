# Unified Directory Manager

A native Windows desktop tool for managing **on-premises Active Directory**, **Entra ID** and
**Exchange Online** from one window — a modern take on the classic "Active Directory Users and
Computers" (ADUC) snap-in, plus features ADUC lacks: reusable **new-user templates**, a GUI
**advanced search**, **bulk edits**, repeatable **scenarios**, and cloud group and mailbox
management.

Built with WPF on .NET 10. Ships as a self-contained single-file `.exe` for **win-x64** and
**win-arm64** (Windows 10 and 11).

This file is the user guide. **Help ▸ View README** shows it inside the app, so it is readable with
no internet connection.

## Key points

- **No domain join required.** Every operation binds with the **domain FQDN + credentials you enter**
  — never the machine's own domain context or the signed-in Windows identity. This works from
  **Entra-joined-only** machines as long as the client has network line-of-sight to a domain
  controller. Enter a DC **hostname or IP** directly; DNS-SRV discovery is an optional best-effort
  convenience that simply does nothing if corporate DNS isn't reachable.
- **Friendly names everywhere, real attribute names under the hood.** The UI shows "First name",
  "Office", "Proxy addresses"; all reads, writes, filters, and columns use the true
  `lDAPDisplayName` (`givenName`, `physicalDeliveryOfficeName`, `proxyAddresses`). DN-valued
  attributes (manager, member-of, members, direct reports) resolve to display names — never raw DNs.
- **Writes are always confirmed.** Editing attributes, changing group membership, creating users,
  enabling/disabling accounts, and bulk changes each show a friendly-name preview/diff before
  committing. Bulk operations report per-object success/failure and never abort the batch on a
  single failure.
- **Each object is reached through the service that actually owns it.** On-prem objects go through
  LDAP, Entra security and Microsoft 365 groups through Microsoft Graph, and mailboxes and
  distribution groups through Exchange Online. This is not an optimisation — Graph refuses the
  Exchange-owned kinds outright, and Exchange rejects every write against an object synced from
  on-premises AD. Where a change has to be made somewhere else, the app says where instead of
  failing at the service.
- **Credentials** are stored (optionally) in the **Windows Credential Manager** under
  `UnifiedDirectoryManager:<domain>` — per Windows user, encrypted at rest by the OS. View them with
  `control /name Microsoft.CredentialManager`.
- **Where data is stored:** being a self-contained exe only bundles the .NET runtime — it does not
  change where user data goes. Settings (`settings.json`), templates, saved searches, scenarios and
  logs all live under `%APPDATA%\UnifiedDirectoryManager\` (your roaming profile), and credentials in
  the Windows vault. The exe is portable; your data follows your Windows profile.

## Connecting and signing in

Everything is configured from **File ▸ Settings…**, which has four tabs: **On-prem AD (LDAP)**,
**Cloud (Entra ID)**, **Entra Connect** and **Logs**. The same dialog opens from the **Connect…**
button on the yellow "not connected" bar.

The on-prem bind and the Entra sign-in are independent, and the app is usable with either one alone:
with no on-prem bind you still get the cloud sections, and with no Entra sign-in you still get the
whole AD tree. **Exchange Online is not independent** — it takes its access token from the Entra
sign-in, so it needs one.

### On-premises Active Directory

1. **Domain (FQDN)** — e.g. `corp.example.com`.
2. **Primary DC (host/IP)** — required on Entra-only clients. **Discover** will try DNS-SRV and fill
   this in if your client can reach the domain's DNS.
3. **Fall-back DCs** — optional, one per line; tried in order after the primary.
4. **Username** — `DOMAIN\user` or `user@domain` — and **Password**.
5. Optionally **Use LDAPS (port 636)**, **Ignore cert errors (insecure)**, and **Save credentials in
   Windows Credential Manager**.

LDAP binds use Kerberos/NTLM with **signing & sealing** by default; LDAPS is available as an option.
**Reconnect** on the same tab rebinds to a different domain controller without restarting the app.

### Entra ID (cloud)

The **Cloud (Entra ID)** tab takes a **Tenant ID** (GUID or tenant domain) and a **Client ID** from
an Entra app registration — a **Mobile & desktop** platform with the redirect URI
`http://localhost`. **No client secret is used**; **Sign in…** opens your system browser.

The app requests these delegated permissions:

- `User.ReadWrite.All` — read, edit, enable/disable and revoke sessions for cloud users
- `Group.ReadWrite.All` — read and edit cloud groups and their membership
- `Organization.Read.All` — licence (SKU) information
- `Device.Read.All` — the Devices list
- `UserAuthenticationMethod.ReadWrite.All` — issue a Temporary Access Pass

The sign-in is cached, so it survives a restart until you press **Sign out**.

### Exchange Online prerequisites

Exchange **borrows its access token from the Entra sign-in**, so signing in to Entra ID is a
prerequisite for every Exchange feature. Mailbox and distribution-group work then runs
**out-of-process** through the machine's PowerShell 7 and the Exchange Online module — the app does
**not** bundle PowerShell. That separation is deliberate: the Exchange module ships its own copies of
the Microsoft identity libraries that clash with the Graph SDK's newer ones inside a single process.

On each machine that will use Exchange features:

1. **PowerShell 7 (`pwsh`)** installed. The app looks for
   `%ProgramFiles%\PowerShell\7\pwsh.exe` and otherwise relies on `PATH`.
2. The **`ExchangeOnlineManagement`** module, e.g.
   `Install-Module ExchangeOnlineManagement -Scope CurrentUser` (or `AllUsers`).
3. In Entra, the app registration granted the **delegated `Exchange.Manage`** permission on *Office
   365 Exchange Online* with **admin consent**. The REST-API `Exchange.ManageV2` permission is **not**
   sufficient. After granting it, sign out and back in so the token is reissued with the new scope.
4. An Exchange RBAC role on the signing-in admin. **They differ per operation:**
   - **Reading** mailboxes and distribution groups — **Recipient Management** (or Exchange
     Administrator).
   - **Changing a distribution group**, its membership or its settings — additionally
     **Organization Management**, or the **Security Group Creation and Membership** role. Those
     writes pass `-BypassSecurityGroupManagerCheck`, because a group's owner is usually a role group
     that no individual is a "manager of", and without it every edit fails for a reason unrelated to
     the edit.
   - **Creating** a distribution list or mail-enabled security group — the **Distribution Groups**
     role. Recipient Management does not carry group creation.

If `pwsh` or the module is missing, or a role is, the app tells you on first use — none of it is
discoverable before then, which is also why the Exchange section stays visible in the tree even when
Exchange is not set up. The first mailbox look-up per session takes a few seconds while the session
is established, then it is reused.

An RBAC refusal keeps **Exchange's own wording** and appends a hint, rather than replacing it with a
fixed sentence naming one role. A tenant policy rejection reads a lot like a permissions error, and
it has to reach you verbatim to be diagnosable.

## The window

- **Left** — the directory tree. It holds, top to bottom: **Favourites** (only when something is
  pinned), the domain root with its OUs and containers (lazy-loaded on expand), **Entra ID (cloud)**
  with Users / Groups / Devices, and **Exchange Online** with Mailboxes / Distribution groups. The
  two cloud roots appear only while you are signed in to Entra ID. Drag objects from the list onto an
  **OU or container** here to move them; cloud nodes are not drop targets, because they have no
  distinguished name to move anything into.
- **Center** — the object list. Which toolbar you get depends on which section you are in.
  - **On-prem:** a Users / Computers / **Groups** / All filter, **Include sub-OUs**, a quick-filter
    box, **header-click sorting**, and a **"Columns ▾"** chooser (friendly labels; choices persist
    across restarts). Columns include name, logon, display name, first/last name, manager, employee
    ID, title, department, email, office, city, state, country, **Group type** (e.g. *Security ·
    Global*) and more. **Disabled accounts are greyed and italicised.** A sortable **Status** column
    shows Enabled/Disabled. Multi-select feeds Bulk Edit and **Add to Groups**.
  - **Cloud (Entra and Exchange):** a **Search** box that queries the service itself, a **Show:**
    drop-down of per-list filters, a **Filter:** box that narrows the rows already loaded,
    **Columns ▾**, and **Export loaded… / Export all…**.
- **Right or bottom** — the detail pane, docked with **View ▸ Toggle Pane Dock (Right / Bottom)**.
  On-prem objects get the tabbed edit pane; cloud objects get the cloud detail pane.
- **Bottom** — a status bar showing the connection, the last message, and the active list's own
  status line (including a capped Exchange list saying so).
- A yellow bar appears across the top when on-prem AD is not bound, with a **Connect…** button. The
  app stays usable for cloud work while it is showing.

**Double-click any row** (or **File ▸ Open Selected…**) to edit that object in its own window;
multiple editor windows can be open at once. The list refreshes automatically after edits, so
disabling a user immediately greys it.

Right-clicking the on-prem list offers **Modify…**, **Enable / Disable / Unlock account(s)**,
**Add to groups…**, **Copy user…**, **Save as template…**, **Move to OU…**, **Bulk edit…**,
**Run scenario**, and **Delete…**.

### The edit pane (on-prem objects)

The header carries **Save**, **Reload**, and for users **Enable**, **Disable**, **Reset Password…**
and **Unlock**. Tabs adapt to the object:

- **Users:** General, Account, Address, Organization (with a **Manager** picker), Email (`mail` +
  proxy addresses), Member Of, Attribute Editor, plus **Cloud** and **ExOL**. The Account tab shows a
  read-only **Lockout status** line.
- **Groups:** General (including **Group scope**, **Group type** and a ***Managed by*** picker),
  **Members**, Member Of, Attribute Editor, plus **Cloud**.
- **Computers:** General, Member Of, Attribute Editor, plus **Cloud**.

The **General** tab also shows the object's **Location (OU)** with a **Move…** button, and its
*protect object from accidental deletion* flag.

**Cloud** is offered on every user, group and computer, and **ExOL** on every user — the tab being
there says nothing about whether a cloud counterpart exists. The **Cloud** tab has a **Look up in
Entra ID** button and then shows the *same* cloud detail pane the Entra section shows, so the on-prem
and cloud views of one person cannot drift apart. The **ExOL** tab is the mailbox actions control
described under *Exchange Online* below. Neither fetches automatically — the first cloud or Exchange
call of a session is slow enough that it has to be asked for, so it is the lookup, not the tab, that
tells you whether there is a counterpart at all.

## Menus

A standard **File / Edit / View / Tools / Help** menu bar exposes every action.

- **File** — New User…, Bulk Create Users…, New Group…, **New Cloud Group…** (in the cloud view),
  Open Selected…, Refresh, Export List to CSV…, View Log File…, Open Logs Folder, Settings…, Exit.
- **Edit** — Advanced Search…, Copy User…, Copy Groups to User…, Add Selected to Groups…,
  Bulk Edit…, Delete Selected…, User Creation Templates…. The whole menu is disabled in the cloud
  view, because every item on it acts on on-prem objects.
- **View** — Toggle Pane Dock (Right / Bottom), Refresh.
- **Tools** — Entra Connect Delta Sync…, **Run Scenario on Selected** (a submenu of your saved
  scenarios), Manage Scenarios….
- **Help** — View README…, View Log File…, Open Logs Folder, About Unified Directory Manager….

## Favourites

*(New in 2.3.0.)* Pin the containers and searches you use constantly to a **Favourites** row at the
top of the tree, so they are one click away instead of a drill-down.

- **Pin a container** — right-click an OU, a container or the domain root and choose
  **Pin to Favourites**.
- **Pin a saved search** — open **Edit ▸ Advanced Search…**, select a search in the *Saved search*
  combo, and press **Pin**. The button reads **Unpin** when the selected search is already pinned,
  and is disabled until you select one. Save the search first: the pin stores its **name**.
- **Reorder** — **Move up** / **Move down** on a pinned row's own right-click menu. New pins are
  appended; the order you set is what is stored.
- **Unpin** — **Unpin** on the pinned row. It removes the shortcut only. The OU or search itself is
  untouched.

**A favourite is a reference, not a copy.** It stores the distinguished name, or the saved search's
name, and resolves it at the moment you click it. Nothing about the target is copied into the pin,
so nothing can go quietly stale — and **editing a saved search changes what its pin does**, which is
almost always what you want.

Some consequences worth knowing:

- **Pins are kept per domain**, keyed on the domain FQDN. A distinguished name only means something
  in the domain it came from, so one global list would show entries that fail the moment they are
  clicked. Connecting to another domain shows that domain's pins; the first domain's are still there
  when you go back. A saved search pinned in one domain is pinned only there — the search itself is
  untouched, and can be pinned again in another domain.
- **Favourites need an on-prem connection.** There is no Favourites row and no Pin button in a
  cloud-only session, because there is no domain to scope the pins to.
- **The Entra ID and Exchange Online sections are deliberately not pinnable.** They are already
  top-level and one click away, so a pin would only be a second route to the same place. Individual
  users, groups and computers are out of scope as well.
- **A pin whose target is gone is greyed with a ⚠ and left in place** for you to unpin, with a
  message saying so. It is not removed automatically: a domain controller being briefly unreachable
  is not evidence that an OU no longer exists, and a silently shorter list is not something anyone
  notices in time to object. The check happens when you click the pin, not when the tree is drawn,
  so a dead pin looks normal until it is used.
- **Renames do not follow.** A rename changes the distinguished name, so a renamed OU reads as
  unavailable rather than following the rename. Unpin it and pin it again.
- **A favourite shows the leftmost part of the stored DN** (so `OU=Sales,DC=contoso,DC=net` reads
  `Sales`), or the saved search's name. Favourites cannot be renamed.

Pins are written to `settings.json` immediately on every change — a pin lost to a crash is one you
have to notice was lost.

## Finding objects

The list's **Filter:** box narrows what is already loaded. **Include sub-OUs** widens a container
load to everything beneath it.

### Advanced search

**Edit ▸ Advanced Search…** builds conditions from friendly attribute names and an operator (equals
/ contains / starts-with / present / …), combined with **Match ALL conditions** or **Match ANY
condition**. Set a **Scope**, add one or more OUs under **Search in:** with **Add OU…** (leave it
empty to search the whole domain), or tick **Use raw LDAP filter (advanced)** and type the filter
yourself. The **Effective filter** box always shows exactly what will be sent.

Results land in the list pane behind a green **🔍 Search results** banner with a **Clear search**
button. Multi-select rows there and run **Bulk Edit** to set attributes, enable/disable, or
add/remove group membership across everything selected.

### Saved searches

The bar at the top of the same dialog stores searches by name: type into **Save current as:** and
press **Save**, then **Load** or **Delete** them later from the *Saved search* combo. **Pin** puts
the selected one in Favourites. **Export…** writes one out as a file and **Import…** reads it back,
which is how a search reaches someone else's copy of the app.

Saved searches are JSON files in `%APPDATA%\UnifiedDirectoryManager\Searches`.

## Editing objects

All edits across the tabs are batched and shown in a confirmation diff when you click **Save**.

- **Attribute Editor** — single-valued attributes edit inline; multi-valued ones (e.g.
  `proxyAddresses`) open a values editor via **Edit…**. Read-only and system attributes are disabled.
- **Country picker** — the Address tab has a searchable dropdown of the full ISO 3166-1 list.
  Choosing a country sets `co` (name), `c` (two-letter code) and `countryCode` (numeric) together,
  matching ADUC.
- **Account expiration** — the Account tab has a "Never expires" checkbox plus a date picker and a
  24-hour time box. Saving writes `accountExpires` (an INTEGER8 FILETIME) via an LDAP modify;
  "Never" stores `0`.
- **Reset password / unlock** (users only) — enter or **Generate** a new password, twice to confirm,
  optionally requiring a change at next logon and/or **unlocking** the account in the same step. The
  reset tries ADSI `SetPassword` first and falls back to writing `unicodePwd` over the sealed
  channel, so it works without LDAPS. Unlock writes `lockoutTime = 0`. The new password is **never
  written to the log**.

## Organizational units

Right-click an OU or the domain root in the tree:

- **Create OU here…** — name, description, and *protect object from accidental deletion*.
- **Properties** — name, the distinguished name **and** the canonical name (both copyable),
  description, and the protection flag.
- **Delete** — behind a two-step guard: the menu item opens onto **Yes, I'm sure…**, and then a
  type-to-confirm phrase.

## Groups: create, modify, delete

**Create** — right-click an OU (or the domain root) in the tree and choose **New Group here…**, or
use **File ▸ New Group…** and pick the OU with the **Browse…** button. The dialog takes the **group
name**, a **logon name** derived from it (editable; group names keep their case, spaces, and hyphens
rather than being flattened the way user logon names are), **Group scope** (Global / Domain local /
Universal), **Group type** (Security / Distribution), description, initial **members**, and *protect
object from accidental deletion*.

The group is created by the first write, so the follow-up steps (members, protection) are each
reported as a **warning** if they fail rather than being treated as a failed create — the group
exists either way, and the message says exactly which step didn't land. Adding the initial members is
one atomic LDAP modify, so a single member the group's scope forbids would drop them all; when that
happens they are retried one at a time, so the legal ones land and the warning can name the offenders.

**Modify** — the **General** tab exposes **Group scope** and **Group type** as dropdowns, plus a
***Managed by*** picker. Changing Global ↔ Domain local directly is rejected up front with an
explanation (Active Directory requires an intermediate hop through Universal), rather than being
sent to the directory to fail.

**Delete** — select one or more groups in the list and use **Edit ▸ Delete Selected…** or the row
right-click. The confirmation lists exactly what will be removed. Deleting **several** objects at once
additionally makes you **type the number of objects**; deleting a single one is one confirmation, with
no phrase — the dialog already names the one thing at risk. (An OU is the stricter case: a two-step
menu *and* a random type-to-confirm string, because deleting an OU takes everything inside it too.)

That confirmation also carries a **Save a record of each group (attributes + members) before
deleting** checkbox — offered only when the selection contains a group, and **ticked by default**.
Leave it ticked and each group is written to a **record** in the operation-log folder before anything
is deleted:

- `group-<name>-<timestamp>.txt` — every populated attribute, with distinguished names alongside the
  friendly names for `memberOf` / `managedBy` (a display name alone can't be restored).
- `group-<name>-<timestamp>-members.csv` — the full membership including each member's **DN**, which
  is what the pickers act on, so the membership can be re-added later.

If the member list can't be confirmed complete — LDAP can't distinguish "this group has no members"
from "you may not read this group's members", because the server omits the attribute in both cases —
the record is stamped with a warning in **both** files instead of recording an authoritative-looking
zero. A group whose record can't be written is **skipped rather than deleted** — a record you asked
for and did not get is not a reason to lose the group anyway.

Large groups are read correctly: Active Directory returns at most ~1500 values per attribute read and
renames the property (`member;range=0-1499`), so membership is walked range by range instead of
silently truncating.

## Group membership

Select a group and open its **Members** tab. **Add members…** opens a picker that searches users,
computers and groups; **Remove selected** takes out whatever is highlighted. Changes are confirmed
and written to the group's `member` attribute.

The same three buttons appear on an Entra group's Members tab in the cloud pane, and in the
**Distribution group members** window for an Exchange group.

### Paste a list of members

*(New in 2.3.0.)* **Paste a list…** sits beside *Add members…* on an AD group, an Entra group and a
distribution group. Paste up to **100 entries**, press **Resolve**, and review what every line
resolved to before anything is written.

**What it accepts.** One person per line, mixed freely: email addresses, logon names, `Doe, Jane`,
plain display names, `Jane Doe <jane@contoso.com>`, bulleted (`- `, `• `, `* `) or numbered (`1. `,
`1) `) lines, quoted spreadsheet cells, tab-separated columns, and a whole Outlook **To:** or **Cc:**
line carrying several recipients. A semicolon always separates people; a comma only separates when
the line holds more than one `@` and every piece has one, so `Doe, Jane` stays one person. Blank
lines are skipped. A `Doe, Jane` entry is searched as "Jane Doe", because that is the order the
directory stores it in.

**A line resolves itself only when an exact match returns exactly one person.** Both halves matter:

- A **near match** never resolves a row even when it is the only hit — it shows as *Close match —
  confirm*, for you to settle from the drop-down. A search hit is a guess about a half-specified
  name.
- An exact match that returns **two or more** people shows as *{n} matches — choose*. Two people
  really do share a display name, and picking the first is how the wrong one gets added.

There is no recycle bin for group membership, and the audit trail records the add as a deliberate act
by whoever ran it.

Rows reading **Not found**, **Already a member**, **This is the group itself**, **Same person as
line *n***, or still awaiting a choice are **skipped**. Lines that could not be read, repeated lines,
and anything past the 100-entry limit are **counted in the status line** rather than dropped
silently — a discarded line looks exactly like a line that found nobody, and you would go hunting for
a person who was never searched for.

Each candidate is labelled with the display name and the address or logon name behind it, and a
non-person candidate carries its recipient kind in brackets — a group in a list of people is nearly
always a mistake, and a nested one is invisible afterwards.

**Names are resolved against the directory that owns the group's membership**, never against
whichever one the pasted text happens to look like. A cloud-only user has no distinguished name for
an on-prem group, and a user with no mailbox cannot join a distribution list. That has three
practical consequences:

- **On-prem AD** resolves users only — the filter is `(&(objectCategory=person)(objectClass=user)…)`,
  so pasting the name of a group or a contact returns *Not found*. It also runs one unbounded exact
  search per term, plus a second unbounded `anr` search for every term the first one missed, so paste
  identifiers (UPN, logon name, address) rather than partial names against a large domain.
- **Entra ID** searches the users collection only, and compares whole strings, so a bare logon name
  like `jdoe` lands in *Close match — confirm* rather than resolving itself. On the AD and Exchange
  paths the same text matches exactly and resolves.
- **Exchange Online** returns any recipient type, so distribution groups, mail contacts and shared
  mailboxes can all appear as candidates. Read the bracketed kind before you confirm.

Editing the paste box after resolving clears the grid and asks you to press **Resolve** again, so the
previous list can never be written by accident. Pressing **Add these members** with nothing settled
does not close the window. **The dialog itself never writes** — it hands the list back to the
membership editor that opened it, which runs its own confirmation listing every person by name. The
last thing you see before the write is who is being added, not how many.

**Add only, one group at a time.** Removing several members at once already works in the editors, and
removal is the more destructive direction — it deserves its own confirmation design rather than
inheriting this one.

### Open a member from the list

*(New in 2.3.0.)* Double-click a member on a group's **Members** tab — on-prem or cloud — to open
**that member in its own non-modal properties window**. The group you were working on stays where it
was, and several members can be open at once. Only a row counts: a double-click on a column header or
on empty space below the last row does nothing. It is not wired to the **Member Of** tab.

## Add selected users to groups

Select one or more objects in the list and use **Edit ▸ Add Selected to Groups…** (or the toolbar
button) to pick multiple groups and add every selected object to all of them in one confirmed batch,
with a per-object result report.

The picker searches on-prem AD, Entra ID and Exchange Online distribution groups and lets you add any
mix of them into one basket; each row is labelled with the service it came from, and each membership
is written through that service.

## Copy groups to user

**Edit ▸ Copy Groups to User…** copies one user's memberships onto another. Untick any you don't want,
pick the target with **Pick user…**, then **Copy**. The target's existing memberships are left as-is;
this only adds. Each row is labelled with the backend that owns it — *Exchange* rather than *Cloud*
for a distribution list — before you apply anything, and each membership is written through that
backend.

## Bulk edit

Multi-select rows in the list (including advanced-search results) and run **Bulk Edit** to set
attributes, enable/disable accounts, or add and remove group membership across every selected object.
Every object's outcome is reported individually and one failure never aborts the batch.

## New-user templates

Create / save / recall / edit / delete templates (**Templates…** button, **Edit ▸ User Creation
Templates…**, or from the New User wizard). A template stores a target OU, a UPN suffix, a country
(pick by friendly name; it stores `co`, `c`, and `countryCode` together), attribute defaults with
**tokens**, and groups to add. Supported tokens:
`{first} {last} {middle} {firstInitial} {lastInitial} {middleInitial} {initials} {sam} {upnSuffix}`
(e.g. `sAMAccountName = {firstInitial}{last}`, `userPrincipalName = {sam}@{upnSuffix}`).

The template's groups come from the unified picker, so one template can carry on-prem AD groups,
**Cloud (Entra ID) groups** and **Exchange distribution groups** together; the New User wizard shows
them in those three labelled buckets, each row individually tickable.

The **New User** wizard takes first/middle/last/initials, can **generate a passphrase** (three
Title-cased words joined by a random `-` or `_`, then one more separator and five random unambiguous
characters — e.g. `Brave-Tiger_Maple-7kR2m`; readable, yet the casing, the symbol and the suffix cover
AD's complexity rule between them. The same generator serves New User, Copy User, Bulk Create and
Reset Password), and previews the resolved attributes
live before creating the account. It can also run an **Entra Connect delta sync** after creating and
issue a **Temporary Access Pass** (lifetime 10–43200 minutes, optionally one-time use) once the user
has synced. Both the template editor and the New User wizard are non-modal windows.

If the password cannot be set on the channel in use, the user is still created — **disabled** — and
the result says so.

Templates are JSON files in `%APPDATA%\UnifiedDirectoryManager\Templates`. The template editor's
**New**, **Clone**, **Delete**, **Import…** and **Export…** buttons manage the set; Export writes one
template out as a `.json` file for someone else to Import.

### Target OU picker

The New User wizard, the template editor and the New Group dialog all have a **Browse…** button that
opens a directory-tree picker, so you choose the target OU/container instead of typing its DN.

## Copy user

**Edit ▸ Copy User…** (or **Copy user…** on the row right-click) creates a new user from an existing
one. Naming autofills from the chosen naming-convention template; address, office, title, department,
manager and group memberships are copied from the source, with the groups editable on the right. It
offers the same delta sync and Temporary Access Pass steps as the New User wizard.

## Bulk create users

**File ▸ Bulk Create Users…** (or the **Bulk Create…** toolbar button) provisions many users in one
pass. Pick a batch **template** (it supplies the defaults — target OU, UPN suffix, attribute/token
defaults, on-prem and cloud groups), then build the batch list two ways:

- **Add user…** opens the **standard New User form** (in capture mode) so you configure each user
  with the exact same fields you'd use to create one — per-user OU, manager, proxies, on-prem + cloud
  groups, and a per-user Temporary Access Pass. Clicking **Add to batch** validates and adds them to
  the list rather than creating the account immediately (the password and Entra-sync sections are
  hidden — the batch handles both).
- **Import CSV…** adds rows in bulk (columns below). **Download CSV template** writes a starter file
  with the right headers.

The list is read-only; **double-click a row or click Edit** to reopen it in the New User form, **✕**
to remove one, or **Clear all** to empty the batch. Cloud (Entra) groups and a Temporary Access Pass
are **per user**. Press **Create users** to run it.

The run is **phased** so it scales (unlike the single-user wizard's per-user sync wait):

1. create **every** user on-prem (a single failure never aborts the batch);
2. if any row needs cloud (groups / a TAP), run **one** Entra Connect delta sync for the whole batch;
3. wait for the new users to appear in Entra ID (one settle, then a shared poll);
4. add each row's cloud groups and issue its Temporary Access Pass.

**Passwords:** each user gets a unique, human-readable **passphrase** — three Title-cased words joined
by a random `-` or `_`, then five random characters (`Brave-Tiger_Maple-7kR2m`). Users are **not**
forced to change it at next logon unless you tick
**Require password change at next logon**, and the pre-run confirmation states which it is. Passphrases
(and any TAPs) are shown **once** in the **post-run report** — copy them, or **Export CSV…** (the
export contains the plaintext passwords, so it warns you and you store it securely). Passphrases and
TAPs are **never written to the log**. If the post-create sync fails, the on-prem accounts still
exist and a **Retry cloud** button re-runs the sync/groups/TAP without recreating anyone.

**CSV columns** (header names, case-insensitive; unrecognized headers map to attributes by friendly
name, e.g. `Title`, `Department`, `Office`): `First name`, `Middle name`, `Last name`, `Initials`,
`Logon name (sAMAccountName)`, `User logon name (UPN)`, `Email`, `Manager` (sAMAccountName / UPN / DN,
resolved best-effort), `Cloud groups` (`;`-separated names, resolved best-effort), `Issue TAP`
(yes/true). Anything that can't be mapped or resolved is reported as an import warning rather than
dropped.

### When a post-sync step fails

*(New in 2.3.0.)* Appearing in Entra ID is not the same as being usable: Graph, Exchange and the
tenant's write limiter all catch up at different speeds. The three post-sync cloud steps — **Entra
group adds**, **Exchange distribution group adds** and the **Temporary Access Pass** — therefore
retry automatically when the failure means "the directory hasn't caught up yet". Up to four retries,
eight seconds apart, per item, and the progress log says so while it waits:
`… <group>: the directory hasn't caught up yet — retrying in 8s (1 of 4)`. This applies to New User,
Copy User and Bulk Create alike.

**Only five wordings are retried**, all of them Microsoft's for "not visible to the service yet" or
"too many concurrent writes": *queried reference-property objects are not present*, *Couldn't find
object*, *couldn't be found*, *concurrent requests*, and *Please wait briefly and retry*.

**Nothing else is retried.** A permissions or RBAC failure, a missing consent, a bad identity, a
licensing failure or a policy rejection fails on the first attempt and is reported immediately — a
broad retry turns a real, permanent failure into a long wait ending in the same error. So **if a step
failed instantly, treat it as a real failure and check the account's roles**; re-running it without
fixing the permission will fail the same way.

If a step still fails after the retries, the error you see is the real one. The account already
exists, so do not recreate it: run **Tools ▸ Entra Connect Delta Sync…**, then add the remaining
groups from the user's **Cloud** tab (**Member Of ▸ Add to groups…**). A **Temporary Access Pass
cannot be issued from there** — the app only issues one as a step of New User, Copy User or Bulk
Create, so a pass that was missed has to come from the Entra admin center.

Every line of a progress log — New User, Bulk create, Copy user, Copy groups and the scenario runner
— can be **selected with the mouse and copied with right-click ▸ Copy** *(New in 2.3.0.)*, and the
scenario progress window additionally has a **Copy log** button that puts the whole log on the
clipboard at once. Attach that text to a ticket rather than a screenshot.

## Scenarios

A scenario is a named, reusable sequence of actions run in order against one or many selected
objects — a Terminate-User flow, for example. Build them in **Tools ▸ Manage Scenarios…** and run one
from **Tools ▸ Run Scenario on Selected** or the list's right-click **Run scenario** submenu.

Available steps:

- **On-prem:** disable, enable, unlock, add to groups, remove from groups, remove from **all** groups,
  set an attribute, clear an attribute, set the description, move to an OU. Values support the
  `{date} {datetime} {time} {admin} {name}` tokens.
- **Entra ID:** disable the cloud account, enable it, revoke sign-in sessions, add to cloud groups,
  remove from cloud groups, remove from **all** cloud groups (dynamic and on-prem-synced groups are
  skipped, because their membership is not managed there).
- **Exchange Online:** convert the mailbox to shared, convert it back to regular, set internal
  forwarding, clear forwarding, and **delegate the mailbox to the user's manager** (Full Access,
  auto-mapped).
- **Save an operation log** — a meta-step that makes no directory change. It writes a plain-text
  record of every step taken and every change made, including the DNs and ids of groups that were
  removed, so the memberships can be re-added.

Before a run, the confirmation lists every step in order and calls out the consequences that are not
obvious from the step list: that removing all groups is **not** recorded unless a save-operation-log
step is present; that moving a directory-synced object can make the next Entra Connect sync disable or
soft-delete the cloud user and mailbox (recoverable for about 30 days); and that cloud and Exchange
steps need an Entra sign-in, and an Exchange admin role, and act on each object's synced twin or
mailbox — skipped per object where no match is found.

Scenarios are JSON files in `%APPDATA%\UnifiedDirectoryManager\Scenarios`.

## Entra Connect delta sync

**Tools ▸ Entra Connect Delta Sync…** runs `Start-ADSyncSyncCycle -PolicyType Delta` on your Entra
Connect (Azure AD Connect) server via PowerShell remoting (WinRM). Enter the server (remembered for
next time) and either use your current logged-in user or supply credentials; the account can also be
saved once under **Settings ▸ Entra Connect** and reused everywhere a sync runs. Output is shown in
the dialog.

WinRM must be enabled on the Connect server and the account must be allowed to run the ADSync cmdlet.
From an Entra-only client the "current user" option typically won't authenticate, so supply on-prem
credentials.

## Entra ID (cloud)

Signing in adds an **Entra ID (cloud)** root to the tree with **Users**, **Groups** and **Devices**.
Each list searches server-side from the **Search** box, filters client-side from **Show:** and
**Filter:**, and pages with **Load more**.

- **Users** filter by Enabled, Disabled, Synced from on-prem, Cloud-only. Tick rows to **Enable**,
  **Disable** or **Revoke sessions** in bulk.
- **Groups** filter by Security, Distribution, Microsoft 365, Teams, Synced from on-prem, Cloud-only.
- **Devices** filter by Compliant, Non-compliant, Hybrid joined, Entra joined, Enabled.

Selecting a row fills the detail pane. Its property sections are:

- **User** — Identity, Account, On-premises sync, Organization, Contact. Plus **Licenses**
  (*Assign license…* / *Remove selected*) and **Member Of** (*Add to groups…* / *Remove selected*),
  and **Disable account** / **Enable account** / **Revoke sessions** in the header.
- **Group** — Identity, Type, Mail, On-premises, Dates, plus a **Members** tab.
- **Device** — Identity, Operating system, Join / management, State, Hardware. Every device row is
  read-only; there is nothing cloud-editable on a device in this SDK.

**What you can edit here.** On a **cloud-only** user: display name, first and last name, job title,
department, company, employee ID and type, office, street, city, state, postal code, country, mobile
and fax. On a **cloud-only** group: display name, description and mail nickname. **Usage location**
is editable even on a synced user, because a licence cannot be assigned until it is set. Everything
else is read-only, and says which of the two reasons applies: *Synced from on-premises AD — edit it
in Active Directory and run a directory sync*, or *System-managed — not editable in Entra ID*.
Unknown properties are read-only by default rather than optimistically writable.

Edits collect in a **Save changes / Revert** bar at the bottom of the pane.

**Removing a licence is guarded.** Taking an Exchange-providing licence off a user who still has a
**regular** mailbox deletes that mailbox after about 30 days. Where the app can determine that is the
case, the confirmation says so prominently and suggests converting the mailbox to shared first.

## Exchange Online

*(New in 2.3.0.)* Exchange Online has its own root in the tree, with **Mailboxes** and **Distribution
groups**. It appears whenever you are signed in to Entra ID — even if Exchange itself is not
configured, or PowerShell 7 or the module is missing — because those failures are only discoverable
on first use, and a missing section is indistinguishable from a bug. The list pane explains what to
fix instead.

Both lists search **server-side** on display name, primary SMTP address and alias. Wildcards you type
are stripped: the app supplies its own leading and trailing wildcard, and a `*` in the middle of a
term is rejected by these cmdlets.

**Both lists are capped at 200 rows, not paged.** Exchange PowerShell has no continuation token, so
there is no "Load more" to offer. A truncated list says so in the status bar —
`… capped at 200; narrow the search to see the rest` — rather than passing itself off as the whole
set. **Export all…** runs under a higher cap of 5000 and appends its own sentence when it truncates.
Exchange exports are named with an `exchange-` prefix.

Microsoft 365 (unified) groups do **not** appear in the Distribution groups list. They are
Graph-managed and live in the Entra **Groups** list.

### Mailboxes

Default columns are display name, **Primary SMTP**, **Mailbox type**, **User principal name** and
**Directory sync**; alias, hidden-from-address-lists, created date and object ID are available from
**Columns ▾**. **Show:** filters by User mailboxes, Shared mailboxes, Rooms and equipment, Synced
from on-prem, Cloud-only.

Selecting a mailbox loads eight property sections, each its own tab: **Identity**, **Type and
visibility**, **Addresses** (primary, secondary and other, split by the address prefix Exchange
stores), **Delivery and forwarding**, **Quotas**, **Hold and retention**, **Archive**, and
**Protocols** (Outlook on the web, ActiveSync, MAPI, EWS, IMAP, POP).

Two details in that pane are worth knowing:

- **Archive presence comes from the archive GUID, not the reported archive status.** Both are shown,
  but only the GUID-derived answer is labelled as the answer; the other reads *Archive status
  (reported)*. Microsoft documents the status reading `None` for a live archive after a re-license or
  a migration.
- **The Protocols section fails as a unit.** It comes from a second cmdlet behind a second
  permission, so if that read fails the whole section collapses to one row saying it could not be
  read. Rendering the six flags as "—" would use this pane's symbol for "no value" to mean "we
  couldn't read it", and you would conclude ActiveSync was off.

Every mailbox property row is read-only here — *Mailbox settings are changed through their own
actions, not by editing this list*.

**Load size and usage** is a separate button in the pane header, beside the mailbox name. It adds a
ninth section — mailbox size, item count, recoverable items size and count, and last logon. It is not
part of the normal load, and it is never run across a list, because it queries the **mailbox store**
rather than the directory, takes **one mailbox per call**, and fanning it out is the documented way to
exhaust the Exchange throttling budget and trigger *Micro Delay Applied*. The button switches off once
the figures are on screen, so it cannot invite a second expensive read.

### Mailbox actions

The **Actions** tab on a mailbox — the same control as the **ExOL** tab on the AD edit pane, on the
same view model, so the two cannot drift apart — offers:

- **Look up mailbox**, which fetches the mailbox and its delegates. It is a button rather than an
  automatic fetch because the first Exchange connect of a session is slow.
- **Convert to Shared** / **Convert to Regular**, so a departing user's mailbox survives unlicensing.
  Convert is hidden for room and equipment mailboxes.
- **Set forwarding…** / **Clear forwarding** for internal forwarding, with an optional *Also deliver
  a copy to the mailbox*.
- **Delegates** — grant, change or remove **Full Access**, **Send As** and **Send on Behalf**, with an
  **Auto-map (Full Access)** option. Each permission is applied independently and idempotently:
  "already exists" counts as success on an add, "not present" as success on a remove, and one failing
  does not skip the others.

Every action confirms first, and the pane **re-reads the mailbox afterwards**, copying the new values
back into the list row so the *Mailbox type* column agrees with what you just did. Two things follow
from that re-read and are normal: the tab strip returns to the first properties tab (one click back to
Actions), and any size-and-usage figures you had loaded are cleared, so press **Load size and usage**
again if you still need them.

The **ExOL** tab on the AD edit pane remains the route to these actions for a **hybrid user who has no
row in the Exchange list at all**.

### Distribution groups

Default columns are display name, **Primary SMTP**, **Type**, **Directory sync**, **Managed by** and
**External senders**; join restriction, alias, hidden-from-address-lists, created date and object ID
are available from **Columns ▾**. **Show:** filters by Distribution, Mail-enabled security,
**Cloud-only (editable here)**, Synced from on-prem, and External senders allowed. That third filter
is the one that answers "which of these can I actually change from here?".

External senders reads **Allowed** or **Blocked**, which is the inverse of the flag Exchange actually
stores — the app phrases it the way an operator thinks about it, not the way the service stores it.

Selecting a group loads seven sections: **Identity**, **Description**, **Addresses**, **Visibility**,
**Ownership and joining**, **Mail flow**, and **Moderation**. Double-clicking a row, or the
**Members…** button under the group's name in the pane header, opens the **Distribution group
members** window with **Add members…**, **Paste a list…** and **Remove selected**.

Adds and removes run one Exchange call per member — that is what the cmdlets accept — and each is
reported individually. Both are idempotent, and the list re-reads from the server afterwards rather
than being patched locally, because the server is the authority on what actually stuck.

#### What you can change

Thirteen settings are editable. Every one with a fixed set of values is a **drop-down, never a text
box** — typing the value would be a spelling test whose wrong answers are either a silent no-op or a
service error.

| Setting | Choices |
|---|---|
| Description | free text, clearable |
| MailTip | free text, up to 175 characters |
| Room list | Yes only — see below |
| Hidden from address lists | Yes / No |
| Members can join | Open / Closed / ApprovalRequired |
| Members can leave | Open / Closed |
| External senders | Allowed / Blocked |
| BCC blocked | Yes / No |
| Delivery reports go to | Message sender / Group owner |
| Send auto-replies to sender | Yes / No |
| Moderated | Yes / No |
| Notify senders | Always / Internal / Never |
| Secondary addresses | semicolon-separated addresses |

Six **recipient lists** are editable too, each through a picker seeded with what is already there:
**Owners**, **Send on behalf**, **Accept only from**, **Reject from**, **Moderators** and **Bypass
moderation**. Addresses behind the displayed names are resolved only when you open an editor, because
a display name cannot be written back and resolving on open would cost a directory lookup per entry
on every group you click.

**Recipient lists are saved as an add/remove difference, never as a replacement.** That matters
because a role group such as *Organization Management* — which owns most groups — can be **read out
of** a list but never **written back to** it: it is not a mail recipient, and Exchange documents these
properties as requiring a mailbox, mail user or mail-enabled security group. A replacement would
delete such an entry, so entries the app cannot name are **neither added nor removed**. They stay
owners, whatever else you change on that row, and they stay visible in the row's text — dropping them
from the display would make the pane lie about who owns the group.

**Secondary addresses are saved as a difference too**, and only the secondary row is editable. Handing
Exchange a whole address collection with no primary entry in it makes the service promote the first
entry to primary, which is exactly the accident this pane exists to avoid. An added address may not
contain a `:` (a type prefix is precisely how the reply address gets moved), must have exactly one
`@` that is neither the first nor the last character, and must contain no whitespace.

**Delivery reports is one decision, not two.** Exchange stores it as two flags, and Microsoft
documents setting both — or neither — as leaving messages to the group with no return path, which
some mail servers reject. The pane offers one two-value choice and always writes both flags. A group
already in an invalid combination is shown as it really is ("Both the sender and the group owner", or
"Nobody") and the row is made read-only, with a tip naming the state and saying to correct it in the
Exchange admin center. It is never silently normalised.

If a setting's current value is not one of its own choices, the row is downgraded to read-only rather
than offered for editing — the app will not overwrite something it never successfully read.

Saving re-reads the group and copies the new values back into the list row. If the write lands but
the re-read fails, the status line keeps **both** facts, so a read failure cannot erase a write
success or leave the Save bar armed over changes that were already applied.

#### What is deliberately not editable, and why

These are not omissions. Each is a case where the service will not honour the change, or will honour
it in a way you did not ask for.

- **Display name** — a group naming policy rewrites a display name on edit as well as on create, and
  administrators are exempt from it, so the same save behaves differently for everyone else.
- **Primary email address** — changing the address replies come from redirects the group's mail and
  stops the old address working.
- **Alias** — wherever an email address policy applies, which is the usual case for a cloud group,
  changing the alias **also rewrites the primary address**. That is the same consequence as editing
  the primary address, reached by another route. The pane shows an **Email address policy** row
  precisely so you can see whether that applies to this group.
- **Service (routing) addresses** — anything at `*.onmicrosoft.com`. Microsoft 365 maintains these for
  routing; removing one breaks mail flow and the service restores it anyway. They are held out of the
  editable secondary row entirely, rather than offered and then refused.
- **Other (non-SMTP) addresses** — x500 and sip entries, which exist so replies to old messages still
  resolve.
- **Membership hidden** — shown because it cannot be undone: you need to know it is already on.
  Microsoft documents no supported way to turn it back off. It **can** be set when the group is
  created (see below).
- **Room list** — offered in **one direction only**, editable while the group is not a room list and
  closed once it is. Exchange Online has no supported way to turn a room list back into an ordinary
  distribution list, and the repair Microsoft documents is an Active Directory attribute that a
  cloud-only group does not have. A Yes-to-No choice would report success and change nothing. Setting
  it warns you on the row and again in the save confirmation.
- **Max send size / Max receive size** — Microsoft documents these parameters as available only in
  on-premises Exchange. Exchange Online takes the message-size limit from the organization, so they
  can be read but never written.
- **Members can join / leave on a mail-enabled security group** — such a group can only be Closed.
  Exchange rejects Open, and accepts ApprovalRequired without ever sending join requests to an owner.
- **Custom attributes 1–15** — not shown at all. They can silently change what a dynamic rule matches.
- **Converting between distribution and mail-enabled security** — `Set-DistributionGroup` has no
  parameter for it.
- **Deleting a distribution group** — deliberately not in this app. A distribution group is
  **permanently deleted immediately**: there is no 30-day recycle bin, unlike Microsoft 365 and
  security groups. The Exchange admin center already does it.
- **Everything, on a group synced from on-premises AD.** Exchange Online rejects every write against a
  synced group, so the whole pane is read-only and all three of the members window's buttons —
  **Add members…**, **Paste a list…** and **Remove selected** — are replaced by an explanation. In a
  hybrid organisation that is the default case, not an edge case. Make the change in Active Directory
  and let it sync.

One editing refusal remains that is not permanent: a recipient list with **more entries than can be
resolved in one read** cannot be edited here, because the picker would show you only part of it.
Use the Exchange admin center for those.

#### The "?" beside a property row

*(New in 2.3.0.)* Most rows in the cloud detail pane — distribution groups, mailboxes, and Entra
users, groups and devices — carry a small grey **?** circle next to the label. Hover it, or click it
if you would rather; a click works because WPF shows tooltips on hover only, which leaves a touch user
no way to read it at all.

It gives one sentence saying **what the setting is** and, underneath, **the reason it cannot be
changed** when it cannot. One place answers both questions. A row with no "?" simply has no
explanation written for it — an absent explanation is better than a guessed one. The "?" is in the
cloud pane only; the on-premises AD edit pane does not have it.

### Creating cloud groups

*(New in 2.3.0.)* **File ▸ New Cloud Group…** in the cloud view, or **New cloud group…** on the
right-click menu of the **Entra ID ▸ Groups** or **Exchange Online ▸ Distribution groups** node
(which preselects the matching type). The **Group type** drop-down names its own backend:
*Security (Entra ID)*, *Microsoft 365 (Entra ID)*, *Distribution list (Exchange Online)* and
*Mail-enabled security (Exchange Online)*.

Group management overall is split across three services, and creating, reading membership and
editing properties all go through the one that owns the kind:

| Group kind | Handled by |
|---|---|
| On-prem AD (any scope) | LDAP |
| Entra security | Microsoft Graph |
| Microsoft 365 (unified) | Microsoft Graph |
| Distribution list | Exchange Online |
| Mail-enabled security | Exchange Online |

**The routing is required, not an optimisation.** Graph answers a distribution list's membership with
**403**, and a create of either Exchange-owned kind with **400 — "Cannot Create a mail-enabled
security groups and or distribution list"**.

Name and alias rules **differ by backend**, and the dialog enforces both sets before it calls
anything, because the server-side failures are unhelpful — Exchange in particular does not reject a
bad alias, it silently rewrites it. Switching the type re-validates a name and alias you have already
typed, because a value that was legal a moment ago can stop being legal without you touching it. The
field is labelled **Alias** on the two Exchange kinds and **Mail nickname** on the two Entra ones; one
is required either way — Graph demands it even for a security group that is not mail-enabled — and one
is suggested from the name.

Four checkboxes sit on the dialog because of what they do at create time. **Each appears only for the
kinds it applies to**, so a plain *Security (Entra ID)* group shows none of them:

- **Allow mail from external senders** — the two Exchange kinds. Off by default, matching Exchange,
  where a new distribution group otherwise silently rejects **all** external mail. The dialog always
  sends the value explicitly rather than leaving it to that default. This one *can* be changed later,
  from the group's **Mail flow** section.
- **Send a welcome email to the initial members** — Microsoft 365 only. Off by default, and settable
  **only** as the group is created. Microsoft 365 emails everyone you add unless that is suppressed at
  that moment. An admin building a group from a directory tool is not announcing it, and by the time
  anyone notices, the mail has reached real people.
- **Hide the group in Outlook** — Microsoft 365 only. Off by default, and, like the welcome email,
  settable only while the group is being created.
- **Hide the membership list (permanent)** — every kind except a plain security group, which has no
  membership list to hide. Confirmed before it is applied, because revealing the membership again
  means deleting and recreating the group.

**Owners.** On the Graph path the signed-in admin is seeded as the default owner, because an *admin*
creating a security group with no owners produces an **ownerless group** — Graph auto-assigns the
creator only for Microsoft 365 groups or for non-admin callers. Switching to an Exchange type visibly
drops that seeded row (only the seeded one; an owner you picked is yours), because Exchange addresses
principals by SMTP and puts owners **on** the create, where one unresolvable entry loses the whole
group. There is no ownerless hazard on the Exchange side — Exchange assigns an owner itself. On an
Exchange type the picker also refuses an owner or member with no mail address up front, for the same
reason: one entry Exchange cannot resolve loses the whole create.

After the create, the list reloads with the new group's name pre-seeded as a **server-side search** and
selects it, and the app prefers the **name the service echoed back** over the one you typed, because a
tenant naming policy can rewrite it. If the row still is not listed, the app says so explicitly rather
than leaving you to conclude the create failed.

**Only the list the group landed in is reloaded**, and the two backends are not equally quick to admit
a new group exists. An Exchange group is readable from `Get-DistributionGroup` immediately but takes a
while to reach the **Entra ID** list, because Graph is not immediately consistent and answers with a
**400** during the replication window. A Microsoft 365 group created through Graph takes far longer to
reach the **Exchange Online** list, because its mailbox takes roughly 20–30 minutes to provision.
Reloading the other side would show you an absence that reads as a failed create.

Tenant policy can block a create invisibly and only at write time: group-creation policy for Microsoft
365 groups, the authorization policy for security groups, and naming policy for both. **Global and
User Administrators are exempt from naming policy**, so it will test clean for an admin and fail for
everyone else. The app surfaces the raw policy error rather than reinterpreting it as a permissions
problem.

## Where your data is kept

Everything lives under `%APPDATA%\UnifiedDirectoryManager\` and follows your Windows profile:

- `settings.json` — window size, the edit-pane dock side, tree and pane sizes, the visible columns
  for each list, **favourites** (per domain), the Entra **tenant and client IDs**, the Entra Connect
  server, and the **last successful connection** (domain, the DC actually bound to, fall-backs,
  LDAPS, username — never the password). The next launch restores your layout and pre-fills the
  connection dialog with the last DC.
- `Templates\` — new-user templates.
- `Searches\` — saved searches.
- `Scenarios\` — scenarios.
- `Logs\` — the application log.
- `OperationLogs\` — scenario operation logs and group-deletion records. Change this folder under
  **Settings ▸ Logs**; leave it blank to use the default.

Passwords are never stored here. Credentials go to the Windows Credential Manager, and the Entra
sign-in to a DPAPI-encrypted token cache.

## Logging

The app writes a rolling daily log to
`%APPDATA%\UnifiedDirectoryManager\Logs\UnifiedDirectoryManager-YYYYMMDD.log` (Info level: connection
attempts and outcomes, every attribute/group/create/enable-disable/bulk write, warnings and
exceptions; files older than 30 days are pruned). Open it from **File ▸ View Log File…** (in-app
viewer with Refresh) or **File ▸ Open Logs Folder**. Unhandled errors are written here too.

Passwords, passphrases and Temporary Access Passes are **never** written to it.

## Security notes

- **LDAPS certificate validation is on by default.** If a DC's certificate isn't trusted or doesn't
  match the host/IP you connect to, enable **"Ignore cert errors (insecure)"** in the connection
  dialog — this bypasses MITM protection, so use it only on a trusted network.
- Non-LDAPS connections use Kerberos/NTLM with **signing and sealing** (the channel is
  encrypted/signed).
- Passwords are never written to the log; the log records usernames, servers, DNs and the
  before/after of attribute changes (a local audit trail under your profile).
- Secrets passed to PowerShell — the Entra sync password, the Exchange access token — go via
  **stdin**, never on the command line, and inputs are escaped to prevent script injection.
- CSV export neutralizes spreadsheet formula-injection (`= + - @`) in exported values.
- Dependencies are scanned with `dotnet list package --vulnerable`.

## Build & run

Prerequisites: .NET 10 SDK and the .NET 10 Windows Desktop runtime.

```powershell
dotnet build UnifiedDirectoryManager.sln -c Release        # compile
dotnet run --project src/UnifiedDirectoryManager           # run from source
```

## Publish (self-contained executables)

```powershell
./build/publish.ps1                           # both win-x64 and win-arm64 -> dist/
./build/publish.ps1 -Runtimes win-arm64       # one RID
```

Output: `dist/win-x64/UnifiedDirectoryManager.exe` and `dist/win-arm64/UnifiedDirectoryManager.exe`.
These are self-contained (no .NET install needed on the target) single-file executables for
Windows 10/11.

## Installer (MSI)

For distribution, the app is packaged as a **per-machine MSI** per architecture, built with the
**WiX Toolset v5**. Because the exe is self-contained, the MSI's only payload is
`UnifiedDirectoryManager.exe` — there are **no prerequisites to chain in** (the .NET runtime is
bundled). The installer puts the app in `Program Files\Unified Directory Manager`, adds a Start Menu
shortcut, and registers an Apps-&-Features entry with the app icon and a clean uninstall.

One-time tooling (WiX v5 is the last free version; v6+ require a paid maintenance-fee EULA):

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.UI.wixext/5.0.2
```

Build the installers:

```powershell
./build/build-installer.ps1                    # publishes, then builds both MSIs -> dist/
./build/build-installer.ps1 -SkipPublish       # reuse existing dist/<rid>/UnifiedDirectoryManager.exe
./build/build-installer.ps1 -Runtimes win-arm64
```

Output: `dist/UnifiedDirectoryManager-x64-<version>.msi` and
`dist/UnifiedDirectoryManager-arm64-<version>.msi`. The version is read from the csproj `<Version>`.
Both share one `UpgradeCode`, so installing a newer version cleanly replaces an older one. WiX
sources live in `build/installer/` (`Product.wxs`, `notice.rtf`).

Deploy interactively (double-click) or silently for GPO / Intune / SCCM:

```powershell
msiexec /i UnifiedDirectoryManager-x64-2.3.0.msi /qn          # silent install
msiexec /x UnifiedDirectoryManager-x64-2.3.0.msi /qn          # silent uninstall
```

## Project layout

```
src/UnifiedDirectoryManager/
  Models/      domain models (AdNode, AdObjectRow, CloudObjectRow, CloudProperty,
               FavoriteEntry, PastedMember, Scenario, SearchQuery, UserTemplate, ...)
  Services/    AttributeCatalog, NameResolver, DirectoryService, GraphService,
               ExchangeService, PastedMemberParser, MemberResolvers, Favorites,
               CloudPropertyHelp, SavedSearchStore, ScenarioRunner, OperationLog
  Native/      CredentialManager (advapi32), DnsSrv (dnsapi)
  ViewModels/  one per screen/dialog (MVVM via CommunityToolkit.Mvvm)
  Views/       windows, the pane controls, dialogs, and DialogService (the
               IDialogService implementation that owns every window)
build/publish.ps1          self-contained publish for both architectures
build/build-installer.ps1  MSI build
build/test-*.ps1           test suites (host script, parser, favourites, dialogs)
```
