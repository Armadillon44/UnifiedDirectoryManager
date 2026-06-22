# Unified Directory Manager

A native Windows desktop tool for browsing and managing **Active Directory users and computers** —
a focused, modern take on the classic "Active Directory Users and Computers" (ADUC) snap-in, plus
features ADUC lacks: reusable **new-user templates**, a GUI **advanced search**, and **bulk edits**.

Built with WPF on .NET 10. Ships as a self-contained single-file `.exe` for **win-x64** and
**win-arm64** (Windows 10 and 11).

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
- **Credentials** are stored (optionally) in the **Windows Credential Manager** under
  `UnifiedDirectoryManager:<domain>` — per Windows user, encrypted at rest by the OS. View them with
  `control /name Microsoft.CredentialManager`.
- **Where data is stored:** being a self-contained exe only bundles the .NET runtime — it does not
  change where user data goes. Settings (`settings.json`), templates, and logs all live under
  `%APPDATA%\UnifiedDirectoryManager\` (your roaming profile), and credentials in the Windows vault. The exe is
  portable; your data follows your Windows profile.

## The window

- **Left** — directory tree (domain ▸ OUs/containers), lazy-loaded on expand.
- **Center** — object list with a Users / Computers / **Groups** / All filter, a quick-filter box,
  optional sub-OU search, **header-click sorting**, and a **"Columns ▾"** chooser (friendly labels;
  choices persist across restarts). Columns include name, logon, display name, first/last name,
  manager, employee ID, title, department, email, office, city, state, country, and more. **Disabled
  accounts are greyed and italicised.** Multi-select feeds Bulk Edit and **Add to Groups**.

  Working with groups: select a group to edit it; the **Members** tab lists every member and lets you
  **Add members…** (picker searches users, computers, and groups) or **Remove selected**. Changes are
  confirmed and written to the group's `member` attribute.

  A sortable **Status** column shows Enabled/Disabled — click its header to group active vs disabled
  accounts. **Double-click any row** (or File ▸ Open Selected) to edit that object in its own window;
  multiple editor windows can be open at once. **File ▸ Export List to CSV…** exports the current view
  (visible columns, current sort/filter) to a CSV file. **Edit ▸ Delete Selected…** permanently deletes
  the selected object(s) after a confirmation listing exactly what will be removed. The list refreshes
  automatically after edits (e.g. disabling a user immediately greys it).
- **Edit pane** — dockable **Right (default) or Bottom** (toolbar toggle). Tabs adapt to the object:
  - **Users:** General, Account, Address, Organization (with a **Manager** picker), Member Of, Email
    (`mail` + proxy addresses), Attribute Editor. The header has **Enable / Disable**, **Reset
    Password…**, and **Unlock** buttons; the Account tab shows a read-only **Lockout status** line.
  - **Groups:** General, **Members** (view all members; add any users/computers/groups via the picker,
    or remove selected), Member Of, Attribute Editor.
  - **Computers:** General, Member Of, Attribute Editor.
- **Toolbar** — New User, Templates, Advanced Search, Bulk Edit, Refresh, dock toggle.

### Target OU picker
Both the New User wizard and the template editor have a **Browse…** button that opens a directory-tree
picker so you choose the target OU/container instead of typing its DN.

### Entra Connect delta sync
**Tools ▸ Entra Connect Delta Sync…** runs `Start-ADSyncSyncCycle -PolicyType Delta` on your Entra
Connect (Azure AD Connect) server via PowerShell remoting (WinRM). Enter the server (remembered for
next time) and either use your current logged-in user or supply credentials. Output is shown in the
dialog. Note: WinRM must be enabled on the Connect server and the account must be allowed to run the
ADSync cmdlet; from an Entra-only client the "current user" option typically won't authenticate, so
supply on-prem credentials.

### New-user templates
Create / save / recall / edit / delete templates (Templates… button or from the New User wizard).
A template stores a target OU, a UPN suffix, a country (pick by friendly name; it stores `co`, `c`,
and `countryCode` together), attribute defaults with **tokens**, and groups to add. Supported tokens:
`{first} {last} {middle} {firstInitial} {lastInitial} {middleInitial} {initials} {sam} {upnSuffix}`
(e.g. `sAMAccountName = {firstInitial}{last}`, `userPrincipalName = {sam}@{upnSuffix}`). The New User
wizard takes first/middle/last/initials, can **generate a passphrase** (3–4 mixed-case words joined by
hyphens/underscores, ≥12 chars — readable yet meeting AD complexity; the same generator is used by New
User, Copy User, and bulk create), and previews the resolved attributes live before creating the account.
The template editor and New User wizard are non-modal windows.

Templates are JSON files in `%APPDATA%\UnifiedDirectoryManager\Templates` by default. Use **Import…/Export…** in the
template editor to share a single template as a `.json` file, or point the templates directory at a
shared folder to share the whole set across a team.

### Bulk create users
**File ▸ Bulk Create Users…** (or the **Bulk Create…** toolbar button) provisions many users in one pass.
Pick a batch **template** (it supplies the defaults — target OU, UPN suffix, attribute/token defaults,
on-prem and cloud groups), then build the batch list two ways:

- **Add user…** opens the **standard New User form** (in capture mode) so you configure each user with the
  exact same fields you'd use to create one — per-user OU, manager, proxies, on-prem + cloud groups, and a
  per-user Temporary Access Pass. Clicking **Add to batch** validates and adds them to the list rather than
  creating the account immediately (the password and Entra-sync sections are hidden — the batch handles both).
- **Import CSV…** adds rows in bulk (columns below).

The list is read-only; **double-click a row or click Edit** to reopen it in the New User form, or **✕** to
remove it. Cloud (Entra) groups and a Temporary Access Pass are **per user**.

The run is **phased** so it scales (unlike the single-user wizard's per-user sync wait):

1. create **every** user on-prem (a single failure never aborts the batch);
2. if any row needs cloud (groups / a TAP), run **one** Entra Connect delta sync for the whole batch;
3. wait for the new users to appear in Entra ID (one settle, then a shared poll);
4. add each row's cloud groups and issue its Temporary Access Pass.

**Passwords:** each user gets a unique, human-readable **passphrase** (3–4 mixed-case words joined by
hyphens/underscores, ≥12 chars — readable yet meeting AD complexity). Users are **not** forced to change it
at next logon. Passphrases (and any TAPs) are shown **once** in the **post-run report** — copy them, or
**Export CSV…** (the export contains the plaintext passwords, so it warns you and you store it securely).
Passphrases and TAPs are **never written to the log**. If the post-create sync fails, the on-prem accounts
still exist and a **Retry cloud** button re-runs the sync/groups/TAP without recreating anyone.

**CSV columns** (header names, case-insensitive; unrecognized headers map to attributes by friendly name,
e.g. `Title`, `Department`, `Office`): `First name`, `Middle name`, `Last name`, `Initials`,
`Logon name (sAMAccountName)`, `User logon name (UPN)`, `Email`, `Manager` (sAMAccountName / UPN / DN,
resolved best-effort), `Cloud groups` (`;`-separated names, resolved best-effort), `Issue TAP` (yes/true).
Anything that can't be mapped or resolved is reported as an import warning rather than dropped.

### Saved settings
Window size, the edit-pane dock side (right/bottom), the tree and edit-pane sizes, and the **last
successful connection** (domain, the DC actually bound to, fall-backs, LDAPS, username — never the
password) are saved to `%APPDATA%\UnifiedDirectoryManager\settings.json`. The next launch restores your layout and
pre-fills the connection dialog with the last DC, so you don't have to re-discover each time.

### Add selected users to groups
Select one or more objects in the list and use **Edit ▸ Add Selected to Groups…** (or the toolbar
button) to pick multiple groups and add every selected object to all of them in one confirmed batch,
with a per-object result report.

### Country picker
The Address tab has a searchable country dropdown (full ISO 3166-1 list). Choosing a country sets
`co` (name), `c` (two-letter code), and `countryCode` (numeric) together, matching ADUC.

### Account expiration
The Account tab has a dedicated **Account expiration** control: a "Never expires" checkbox plus a
date picker and a 24-hour time box. Saving writes the `accountExpires` attribute (an INTEGER8 FILETIME)
via an LDAP modify; "Never" stores `0`.

### Editing attributes directly
The **Attribute Editor** tab is editable: single-valued attributes edit inline; multi-valued ones
(e.g. `proxyAddresses`) open a values editor via **Edit…**. Read-only/system attributes are disabled.
All edits across the tabs are batched and shown in the confirmation diff when you click **Save**.

### Logging
The app writes a rolling daily log to `%APPDATA%\UnifiedDirectoryManager\Logs\UnifiedDirectoryManager-YYYYMMDD.log` (Info level:
connection attempts/outcomes, every attribute/group/create/enable-disable/bulk write, warnings and
exceptions; files older than 30 days are pruned). Open it from **File ▸ View Log File…** (in-app
viewer with Refresh) or **File ▸ Open Logs Folder**. Unhandled errors are also written here.

### Menus
A standard **File / Edit / View / Help** menu bar exposes every action (New User, Open Selected,
Export CSV, Advanced Search, Add to Groups, Bulk Edit, Templates, dock toggle, README & log viewer,
About), alongside the quick-access toolbar. **Help ▸ View README** shows this document inside the app.

### Advanced search → bulk edit
Build conditions by friendly attribute name + operator (equals / contains / starts-with / present /
…), combine with ALL/ANY, choose scope, or drop to a raw LDAP filter. Results land in the list pane;
multi-select rows and run **Bulk Edit** to set attributes, enable/disable, or add/remove group
membership across all selected objects.

## Build & run

Prerequisites: .NET 10 SDK and the .NET 10 Windows Desktop runtime.

```powershell
dotnet build UnifiedDirectoryManager.sln -c Release        # compile
dotnet run --project src/UnifiedDirectoryManager            # run from source
```

## Publish (self-contained executables)

```powershell
./build/publish.ps1                           # both win-x64 and win-arm64 -> dist/
./build/publish.ps1 -Runtimes win-arm64       # one RID
```

Output: `dist/win-x64/UnifiedDirectoryManager.exe` and `dist/win-arm64/UnifiedDirectoryManager.exe`. These are self-contained
(no .NET install needed on the target) single-file executables for Windows 10/11.

## Installer (MSI)

For distribution, the app is packaged as a **per-machine MSI** per architecture, built with the
**WiX Toolset v5**. Because the exe is self-contained, the MSI's only payload is `UnifiedDirectoryManager.exe` —
there are **no prerequisites to chain in** (the .NET runtime is bundled). The installer puts the app
in `Program Files\Unified Directory Manager`, adds a Start Menu shortcut, and registers an Apps-&-Features /
add-remove-programs entry with the app icon and a clean uninstall.

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

Output: `dist/UnifiedDirectoryManager-x64-<version>.msi` and `dist/UnifiedDirectoryManager-arm64-<version>.msi`. The version is
read from the csproj `<Version>`. Both share one `UpgradeCode`, so installing a newer version cleanly
replaces an older one. WiX sources live in `build/installer/` (`Product.wxs`, `notice.rtf`).

Deploy interactively (double-click) or silently for GPO / Intune / SCCM:

```powershell
msiexec /i UnifiedDirectoryManager-x64-1.0.0.msi /qn          # silent install
msiexec /x UnifiedDirectoryManager-x64-1.0.0.msi /qn          # silent uninstall
```

## Connecting

1. **Domain (FQDN)** — e.g. `corp.example.com`.
2. **Primary DC** — a DC hostname or IP (required on Entra-only clients). *Discover* will try DNS-SRV
   and fill this if your client can reach the domain's DNS.
3. **Fall-back DCs** — optional, one per line; tried in order after the primary.
4. **Username** — `DOMAIN\user` or `user@domain`.
5. Optionally **LDAPS** (port 636) and **Save credentials**.

LDAP binds use Kerberos/NTLM with **signing & sealing** by default; LDAPS is available as an option.

## Security notes

- **LDAPS certificate validation is on by default.** If a DC's certificate isn't trusted or doesn't
  match the host/IP you connect to, enable **"Ignore cert errors (insecure)"** in the connection
  dialog — this bypasses MITM protection, so use it only on a trusted network.
- Non-LDAPS connections use Kerberos/NTLM with **signing and sealing** (the channel is encrypted/signed).
- Passwords are never written to the log; the log records usernames, servers, DNs and the
  before/after of attribute changes (a local audit trail under your profile).
- The Entra-sync feature passes any supplied password to PowerShell via **stdin** (never on the
  command line) and escapes inputs to prevent script injection.
- CSV export neutralizes spreadsheet formula-injection (`= + - @`) in exported values.
- Dependencies are scanned with `dotnet list package --vulnerable` (currently clean).

## Scope & verification notes

- This build was verified to compile, publish (x64 + arm64), launch, and load every window without
  error, and its domain-independent logic (friendly⇄LDAP mapping, LDAP filter generation, template
  CRUD) was exercised directly. **Full Active Directory behavior — tree population, attribute reads,
  and all write paths — can only be validated against a live domain controller**, which was not
  available in the development environment. Test those on a client with line-of-sight to a DC.
- Password set during user creation uses ADSI `SetPassword`; if the channel doesn't permit it
  (no LDAPS / Kerberos), the user is still created **disabled** and the result is reported.
- **Reset password / unlock account** (edit pane, users only): enter or **Generate** a new password
  (entered twice to confirm), optionally requiring a change at next logon and/or **unlocking** the
  account in the same step. Reset uses ADSI `SetPassword` (needs LDAPS or a Kerberos sign/seal
  channel); unlock writes `lockoutTime = 0` via LDAP. The new password is **never written to the log**.
- Display-specifier-driven attribute labels are a natural future addition; the attribute catalog and
  write layer leave room for them.

## Project layout

```
src/UnifiedDirectoryManager/
  Models/      domain models (AdNode, AdObjectRow, AdAttribute, UserTemplate, SearchQuery, ...)
  Services/    AttributeCatalog, NameResolver, DirectoryService, DomainLocator,
               WindowsCredentialStore, TemplateStore, DialogService
  Native/      CredentialManager (advapi32), DnsSrv (dnsapi)
  ViewModels/  one per screen/dialog (MVVM via CommunityToolkit.Mvvm)
  Views/       windows, the two pane controls, and dialogs
build/publish.ps1   self-contained publish for both architectures
```
