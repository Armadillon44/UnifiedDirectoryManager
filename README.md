# Unified Directory Manager

A native Windows desktop tool for managing **Active Directory** and **Entra ID (Microsoft 365)** from
one place — a modern take on the classic *Active Directory Users and Computers* (ADUC) snap-in, plus
the things ADUC never had: reusable **new-user templates**, GUI **advanced search**, **bulk edits**,
**cloud (Entra) group** management, and **Exchange Online** mailbox actions.

Built with **WPF on .NET 10**. Ships as a **self-contained, single-file `.exe`** for **win-x64** and
**win-arm64** — no .NET install required on the target machine (Windows 10 / 11).

> **v2.3.0 — Exchange Online: a section of its own, and editable groups.**
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
> **v2.2.0 — Group management ([issue #3](../../issues/3)).** Create, modify, and delete groups:
> - **Create** from the tree right-click (*New Group here…*) or **File ▸ New Group** (which asks for the OU):
>   scope (Global / Domain local / Universal), category (Security / Distribution), description, *Managed by*,
>   initial members, and *protect from accidental deletion*. The logon name is derived from the group name and
>   stays editable.
> - **Modify** a group's **scope**, **category**, and ***Managed by*** from the edit pane. Illegal scope changes
>   (Global ↔ Domain local) are caught before the write instead of failing at the directory.
> - **Delete** one *or many* groups behind a two-step, type-to-confirm guard. Every deletion first writes a
>   **record**: all attributes to a text file, and the full membership — with distinguished names, so it can be
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
  friendly-name UI backed by real `lDAPDisplayName` attributes, and manage group membership. **Every
  write is confirmed with a diff.**
- **Group management** — **create** groups (tree right-click or File ▸ New Group), **modify** scope,
  category, and *Managed by*, and **delete** one or many groups behind a type-to-confirm guard that first
  saves a full record of the group and its members.
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
  remove groups → convert mailbox to shared → forward → delegate to manager → remove license), run them
  across many targets, and save a re-addable **operation log**.

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
`Add-`/`Remove-DistributionGroupMember`. A group **synced from on-premises AD** is read-only in the cloud
whichever kind it is — Exchange rejects every write against a synced object — so those rows say so instead of
failing at the service.

## Prerequisites

- **To run:** nothing — the self-contained `.exe` bundles the .NET 10 runtime. (Building from source
  needs the .NET 10 SDK + Windows Desktop runtime.)
- **For Exchange Online (ExOL) features**, on each machine that uses them:
  1. **PowerShell 7 (`pwsh`)** installed.
  2. The **`ExchangeOnlineManagement`** module (`Install-Module ExchangeOnlineManagement`).
  3. In Entra, the app registration granted the **delegated `Exchange.Manage`** permission (with admin
     consent) — **not** `Exchange.ManageV2`.
  4. The signing-in admin holds an Exchange RBAC role:
     - **Reading** mailboxes and distribution groups — **Recipient Management** (or Exchange Administrator).
     - **Changing a distribution group** — its membership or its settings — additionally needs
       **Organization Management**, or the **Security Group Creation and Membership** role. Those writes pass
       `-BypassSecurityGroupManagerCheck`, because a group's owner is usually a role group that no individual
       is a "manager of", and without it every edit fails for a reason unrelated to the edit.

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
- **[docs/](docs/)** — the v2.0 Exchange Online plan and engineering spike findings.

## License / ownership

Internal tooling for **LaCrosse Footwear, Inc.** © the authors. See the About dialog in-app for version
and build details.
```

