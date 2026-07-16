# Unified Directory Manager

A native Windows desktop tool for managing **Active Directory** and **Entra ID (Microsoft 365)** from
one place — a modern take on the classic *Active Directory Users and Computers* (ADUC) snap-in, plus
the things ADUC never had: reusable **new-user templates**, GUI **advanced search**, **bulk edits**,
**cloud (Entra) group** management, and **Exchange Online** mailbox actions.

Built with **WPF on .NET 10**. Ships as a **self-contained, single-file `.exe`** for **win-x64** and
**win-arm64** — no .NET install required on the target machine (Windows 10 / 11).

> **v2.0.0 — Exchange Online.** This release adds an **ExOL** tab and matching scenario steps for
> pure-cloud tenants: convert mailboxes **Regular ↔ Shared**, set/clear **internal forwarding**,
> manage **delegation** (Full Access / Send As / Send on Behalf), **delegate a departing user's mailbox
> to their manager**, and remove members from **cloud distribution lists / mail-enabled security groups**
> (which Microsoft Graph can't touch) via the Exchange Online module.

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
- **New-user templates + Bulk Create** — target OU, UPN suffix, country, token-driven attribute defaults,
  and on-prem/cloud groups; provision many users in one phased pass with per-user passphrases and
  Temporary Access Passes.
- **Advanced search → Bulk Edit** — build conditions by friendly attribute name (or raw LDAP), then set
  attributes / enable-disable / add-remove groups across all matches.
- **Entra ID (cloud)** — manage a synced object's Entra groups and account state, and run an **Entra
  Connect delta sync** on demand.
- **Exchange Online (ExOL)** — mailbox convert, forwarding, and delegation for pure-cloud tenants (see
  the v2.0 note above).
- **Scenarios** — compose ordered, repeatable multi-step actions (e.g. a Terminate-User flow: disable →
  remove groups → convert mailbox to shared → forward → delegate to manager → remove license), run them
  across many targets, and save a re-addable **operation log**.

## Prerequisites

- **To run:** nothing — the self-contained `.exe` bundles the .NET 10 runtime. (Building from source
  needs the .NET 10 SDK + Windows Desktop runtime.)
- **For Exchange Online (ExOL) features**, on each machine that uses them:
  1. **PowerShell 7 (`pwsh`)** installed.
  2. The **`ExchangeOnlineManagement`** module (`Install-Module ExchangeOnlineManagement`).
  3. In Entra, the app registration granted the **delegated `Exchange.Manage`** permission (with admin
     consent) — **not** `Exchange.ManageV2`.
  4. The signing-in admin holds an Exchange RBAC role — **Recipient Management** (or Exchange Administrator).

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

