# Unified Directory Manager

A native Windows desktop tool for managing **Active Directory** and **Entra ID (Microsoft 365)** from
one place — a modern take on the classic *Active Directory Users and Computers* (ADUC) snap-in, plus
the things ADUC never had: reusable **new-user templates**, GUI **advanced search**, **bulk edits**,
**cloud (Entra) group** management, and **Exchange Online** mailboxes and distribution groups.

Built with **WPF on .NET 10**. Ships as a **self-contained, single-file `.exe`** for **win-x64** and
**win-arm64** — no .NET install required on the target machine (Windows 10 / 11).

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

Grab the latest self-contained executable from the [**Releases**](../../releases/latest) page:

| Architecture | File |
|---|---|
| 64-bit Intel/AMD (most PCs) | `UnifiedDirectoryManager-<version>-win-x64.exe` |
| ARM64 (Snapdragon / Surface Pro X) | `UnifiedDirectoryManager-<version>-win-arm64.exe` |

The `.exe` is portable — put it anywhere and run it. It bundles the .NET runtime; your settings,
templates, and logs live under `%APPDATA%\UnifiedDirectoryManager\` and follow your Windows profile.

## What it does

- **No domain join required.** Every operation binds with the **domain FQDN + credentials you enter** —
  never the machine's own domain context. Works from **Entra-joined-only** clients that have network
  line-of-sight to a domain controller (enter a DC hostname/IP directly; DNS-SRV discovery is optional).
- **Users, computers, and groups** — browse the OU tree, filter/sort/search, choose columns, edit on a
  friendly-name UI backed by real `lDAPDisplayName` attributes, and manage group membership. Double-click a
  member to open it in its own window, so the group you are working on stays put. **Every write is confirmed
  with a diff.**
- **Group management** — **create** groups (tree right-click or File ▸ New Group), **modify** *Group
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
  paste-a-list member adds, and pinned favourites.

## License / ownership

Internal tooling for **LaCrosse Footwear, Inc.** © the authors. See the About dialog in-app for version
and build details.
