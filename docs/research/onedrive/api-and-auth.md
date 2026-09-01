# OneDrive administration in UnifiedDirectoryManager — what is reachable in code, and under what credentials

Brief 1 of the OneDrive planning set. Scope: APIs, permissions, PowerShell modules, auth flows,
throttling, async behaviour. Written against the app as it exists at v2.3.0.

Every external claim below carries a URL that was fetched and read. Claims that could not be settled
from vendor documentation are marked **UNVERIFIED** with the exact test that would settle them.

---

## 0. The app as it actually is (read from the repo, not assumed)

| Fact | Evidence |
|---|---|
| Entra auth is `InteractiveBrowserCredential`, public client, redirect `http://localhost`, DPAPI-persisted MSAL cache + `AuthenticationRecord`. No secret, no certificate. | `app/src/UnifiedDirectoryManager/Services/GraphService.cs:77-87` |
| Delegated Graph scopes today: `User.ReadWrite.All`, `Organization.Read.All`, `Group.ReadWrite.All`, `Device.Read.All`, `UserAuthenticationMethod.ReadWrite.All`. **No Files.\* and no Sites.\*.** | `GraphService.cs:19-27` |
| A generic token-minting hook already exists: `GetAccessTokenAsync(string[] scopes)` — silent-first, interactive fallback. | `GraphService.cs:113-121` |
| Exchange runs in a child `pwsh` started `-NoProfile -NoLogo -NonInteractive -ExecutionPolicy Bypass -File <script>`, one long-lived session, line-framed base64 JSON over stdin/stdout. | `ExchangeService.cs:940-980`, `1018-1042` |
| The EXO token is borrowed: scope `https://outlook.office365.com/.default`, passed to `Connect-ExchangeOnline -AccessToken`. | `ExchangeService.cs:24`, `912`, `1517-1519` |
| Timeouts: connect 150 s, **default op 90 s**, list ops 180 s, startup 60 s. **A timeout kills the host process** and takes the whole session down. | `ExchangeService.cs:29-36`, `1044-1060` |
| Scenario steps are per-target, synchronous, isolated, and expected to return a one-line detail string for the operation log. | `ScenarioRunner.cs:41-70` |
| Existing terminate-shaped actions include `ExchangeConvertToShared`, `ExchangeSetForwarding`, `ExchangeDelegateToManager`. | `Models/Scenario.cs:44-56` |

The bar an OneDrive step must clear: **it must fit a per-target step that returns in well under 90 seconds,
or it must not run inside the pwsh channel at all.** See §5.1.

---

## 1. Microsoft Graph — endpoints, and delegated vs application

### 1.1 The permission wording that decides everything

**Correction, 2026-09-01.** The block that stood here was presented as verbatim from
`learn.microsoft.com/en-us/graph/permissions-reference` and was not. It paraphrased. The paraphrases were
directionally right and the argument below survives intact, but the strings were wrong, so they are replaced
here with wording that was actually fetched and read. That reference page is too large to retrieve in one
piece; the table below is verbatim from **Understanding OneDrive API permission scopes**, first-party on
learn.microsoft.com (https://learn.microsoft.com/en-us/onedrive/developer/rest-api/concepts/permissions_reference?view=odsp-graph-online),
which is the same permission set and which also carries the admin-consent column.

| Permission | Delegated description (verbatim) | Application description (verbatim) | Admin consent required (delegated) |
|---|---|---|---|
| `Files.Read` | "Allows the app to read the signed-in user's files." | (none) | No |
| `Files.Read.All` | "Allows the app to read all files the signed-in user can access." | "Allows the app to read all files in all site collections without a signed in user." | No |
| `Files.ReadWrite` | "Allows the app to read, create, update, and delete the signed-in user's files." | (none) | No |
| `Files.ReadWrite.All` | "Allows the app to read, create, update, and delete all files the signed-in user can access." | "Allows the app to read, create, update, and delete all files in all site collections without a signed in user." | No |
| `Sites.Read.All` | "Allows the app to read documents and list items in all site collections on behalf of the signed-in user." | "Allows the app to read documents and list items in all site collections without a signed in user." | No |
| `Sites.ReadWrite.All` | "Allows the app to edit or delete documents and list items in all site collections on behalf of the signed-in user." | "Allows the app to create, read, update, and delete documents and list items in all site collections without a signed in user." | No |
| `Sites.Manage.All` | "Allows the app to manage and create lists, documents, and list items in all site collections on behalf of the signed-in user." | "Allows the app to manage and create lists, documents, and list items in all site collections without a signed-in user." | No |
| `Sites.FullControl.All` | "Allows the app to have full control to SharePoint sites in all site collections on behalf of the signed-in user." | "Allows the app to have full control to SharePoint sites in all site collections without a signed-in user." | **Yes** |

Two notes on that table. First, the live tenant string for `Sites.FullControl.All` delegated reads *"Allows
the application to have full control of all site collections on behalf of the signed-in user"*, which differs
cosmetically from the learn.microsoft.com page; the two Microsoft surfaces are not word-identical. Cross-check
against a third-party mirror of the live servicePrincipal data
(https://graphpermissions.merill.net/permission/Sites.FullControl.All, **third party, lower confidence**).
The meaning is the same either way. Second, that same learn page has a copy-paste defect in its *Application*
Sites table, where the Display Strings for `Sites.Manage.All` and `Sites.FullControl.All` are swapped. Read
the Description column, not the Display String.

**UNVERIFIED:** the exact strings on `learn.microsoft.com/en-us/graph/permissions-reference` itself. That page
could not be fetched whole in this pass; it truncated mid-alphabet before the Files entries. Test: open the
page and read the `Files.*` and `Sites.*` rows directly, or decode `scp` from a real token the way
`LogTokenClaims` already does.

**The admin-consent column is a finding in its own right and it corrects §7 below.** `Files.ReadWrite.All` and
`Sites.ReadWrite.All` delegated do **not** require admin consent. `Sites.FullControl.All` delegated does.

The delegated `Files.*` wording is the load-bearing phrase: **"the signed-in user can access."** It is
not "every file in the tenant". Pair it with this, verbatim from
https://learn.microsoft.com/en-us/sharepoint/sharepoint-admin-role:

> "Global Administrators and SharePoint Administrators **don't have automatic access to all sites and each
> user's OneDrive**, but they can give themselves access to any site or OneDrive."

**Conclusion (high confidence):** a delegated Graph call against a departing user's OneDrive returns 403 for
a SharePoint Administrator who has not first been granted access to that specific OneDrive site. The admin
role is *necessary* but not *sufficient*. This is the single most important fact in this brief and it
reshapes the design: the first step of any OneDrive operation is a **grant-myself-access** step against the
SharePoint admin surface (§2), which is exactly the surface the app cannot currently reach (§3).

### 1.2 Endpoint-by-endpoint

| Operation | Endpoint | Delegated (work/school) | Application | Source |
|---|---|---|---|---|
| Get a user's drive | `GET /users/{idOrUpn}/drive` | least `Files.Read`; higher `Files.Read.All`, `Files.ReadWrite`, `Files.ReadWrite.All`, `Sites.Read.All`, `Sites.ReadWrite.All`, `User.Read` | **"Not supported."** | [drive-get](https://learn.microsoft.com/en-us/graph/api/drive-get?view=graph-rest-1.0) |
| List a user's drives | `GET /users/{userId}/drives` | least `Files.Read`; higher `Files.Read.All`, `Files.ReadWrite`, `Files.ReadWrite.All`, `Sites.Read.All`, `Sites.ReadWrite.All` | least `Files.Read.All`; higher `Files.ReadWrite.All`, `Sites.Read.All`, `Sites.ReadWrite.All` | [drive-list](https://learn.microsoft.com/en-us/graph/api/drive-list?view=graph-rest-1.0) |
| Enumerate items | `GET /drives/{id}/items/{id}/children`, `.../delta` | `Files.Read` → `Files.Read.All` / `Sites.Read.All` | supported (same family) | drive-list, plus the delta guidance in the throttling doc (§4) |
| Copy an item (incl. children) | `POST /drives/{driveId}/items/{itemId}/copy` | least `Files.ReadWrite`; higher `Files.ReadWrite.All`, `Sites.ReadWrite.All` | least `Files.ReadWrite.All`; higher `Sites.ReadWrite.All` | [driveitem-copy](https://learn.microsoft.com/en-us/graph/api/driveitem-copy?view=graph-rest-1.0) |
| Move an item | `PATCH /drives/{driveId}/items/{itemId}` (update `parentReference`) | least `Files.ReadWrite`; higher `Files.ReadWrite.All`, `Sites.ReadWrite.All` | least `Files.ReadWrite.All`; higher `Sites.ReadWrite.All` | [driveitem-move](https://learn.microsoft.com/en-us/graph/api/driveitem-move?view=graph-rest-1.0) |
| Grant a person access to an item | `POST /drives/{driveId}/items/{itemId}/invite` | least `Files.ReadWrite`; higher `Files.ReadWrite.All`, `Sites.ReadWrite.All` | least `Files.ReadWrite.All`; higher `Sites.ReadWrite.All` | [driveitem-invite](https://learn.microsoft.com/en-us/graph/api/driveitem-invite?view=graph-rest-1.0) |
| **Change ownership of a drive or item** | — | **does not exist** | **does not exist** | see §1.3 |
| Create a OneDrive restore session (M365 Backup) | `POST /solutions/backupRestore/oneDriveForBusinessRestoreSessions` | `BackupRestore-Restore.ReadWrite.All` (higher: "Not available.") | `BackupRestore-Restore.ReadWrite.All` | [backuprestoreroot-post-onedriveforbusinessrestoresessions](https://learn.microsoft.com/en-us/graph/api/backuprestoreroot-post-onedriveforbusinessrestoresessions?view=graph-rest-1.0) |

**Added 2026-09-01: Microsoft 365 Backup has a delegated Graph v1.0 surface, which the first draft missed.**
The service root is `backupRestoreRoot`
(https://learn.microsoft.com/en-us/graph/api/resources/backuprestoreroot?view=graph-rest-1.0), with v1.0
relationships including `oneDriveForBusinessProtectionPolicies`, `oneDriveForBusinessRestoreSessions`,
`driveProtectionUnits`, `restorePoints` and `oneDriveForBusinessBrowseSessions`. The restore-session body's
`driveRestoreArtifact` carries `destinationType`, whose documented example value is `"new"`. This does not
change the ownership-transfer conclusion, and Backup is a pay-as-you-go add-on the tenant may not have, but
it is the one *Graph-native, delegated, v1.0* lever in this whole area that the app could in principle drive
with the credential it already has, so it belongs in the option set rather than being omitted. Caveat: that
API is marked available on the Global service only (US Gov L4, US Gov L5/DoD and 21Vianet all show ❌).

Note the `drive-get` oddity: the docs mark the whole `GET .../drive` API "Application: Not supported", while
`GET /users/{userId}/drives` (plural) explicitly *does* list application permissions. If an app-only design
is ever considered, the plural form is the documented route. Either way this is irrelevant to the current
public-client model, which is delegated-only.

### 1.3 There is no ownership transfer in Graph. At all.

Three separate documented facts converge:

1. **Move cannot cross drives.** Verbatim from
   [driveitem-move](https://learn.microsoft.com/en-us/graph/api/driveitem-move?view=graph-rest-1.0):
   > "Items cannot be moved between Drives using this request."

   So the natural "move the departing user's files into the manager's OneDrive" is *not* a Graph move.
2. **Copy is the only cross-drive primitive**, and it creates a new item, not a re-parented one. Verbatim from
   [driveitem-copy](https://learn.microsoft.com/en-us/graph/api/driveitem-copy?view=graph-rest-1.0):
   > "Metadata isn't retained when a **driveItem** is copied, including system metadata and custom metadata.
   > An entirely new **driveItem** is created in the target location instead."
   >
   > "Permissions are not retained when a driveItem is copied. The copied driveItem inherits the permissions
   > of the destination folder."
   >
   > "File versions are only retained when the **includeAllVersionHistory** parameter is explicitly set to
   > `true`. Otherwise, only the latest version is copied."
3. **`invite` grants `read` or `write`, not ownership.** The documented request body is
   `"roles": [ "read | write"]`.

   **UNVERIFIED:** whether `roles: ["owner"]` is accepted by `/invite` against a OneDrive item. The v1.0
   invite reference does not list it. *Test:* `POST /drives/{d}/items/{i}/invite` with
   `{"recipients":[{"email":"..."}],"roles":["owner"],"sendInvitation":false,"requireSignIn":true}` against a
   scratch tenant item and record the HTTP status. My expectation is 400 `invalidRequest`, but do not build on
   it until tested.

**Compare Google.** Google Workspace's admin transfer tool
(https://knowledge.workspace.google.com/admin/drive/transfer-drive-files-to-a-new-owner-as-an-admin)
does exactly what Microsoft has no equivalent for: it changes the owner of the files the departing user
owns, drops them in "a transfer folder ... created in the new owner's My Drive", and — critically —
"Transferring files does not affect who has access to the files." Google preserves sharing; a Graph copy
destroys it. **A faithful Google-parity feature is not buildable on Graph.** That is a product-level finding,
not just a technical one, and it should be stated plainly to the user before any code is written.

---

## 2. The SharePoint admin surface

### 2.1 `Set-SPOSite -Owner` is explicitly forbidden for OneDrive

Verbatim from
[Set-SPOSite](https://learn.microsoft.com/en-us/powershell/module/sharepoint-online/set-sposite?view=sharepoint-ps),
the `-Owner` parameter:

> "Specifies the owner of the site collection. **Changing the Owner of a OneDrive is not supported and causes
> many experiences to break.**"

That closes the "just re-point the OneDrive at the manager" idea. It is not a limitation to work around; it
is a documented supported-configuration boundary. Do not ship it.

For completeness, the same page confirms `Owner` *is* in the list of parameters valid on a OneDrive site
collection ("For OneDrive for Business site collection, the only valid parameters are Identity, ... LockState,
**Owner**, ..."). So the cmdlet will let you do it. It just tells you not to. That combination is a trap worth
calling out explicitly in the plan.

### 2.2 `Set-SPOUser -IsSiteCollectionAdmin` is the supported grant-access primitive

From [Set-SPOUser](https://learn.microsoft.com/en-us/powershell/module/sharepoint-online/set-spouser?view=sharepoint-ps):

> Syntax: `Set-SPOUser -Site <SpoSitePipeBind> -LoginName <String> [-IsSiteCollectionAdmin <Boolean>] [-UpdateUserTypeFromAzureAD]`
>
> "Use the `Set-SPOUser` cmdlet to configure properties of an existing user. That is, to add or remove a user
> as a SharePoint Online site collection administrator."
>
> "You must be at least a SharePoint administrator to run the cmdlet."
>
> "This cmdlet is not supported for use with granular delegated admin privileges (GDAP)."

`Set-SPOSiteAdmin` does not exist as a cmdlet; the surface is `Set-SPOUser -IsSiteCollectionAdmin`, plus
`Set-SPOSite -Owner` for the primary admin (forbidden on OneDrive, §2.1).

PnP equivalents: `Add-PnPSiteCollectionAdmin -Owners <list>` adds users as site collection administrators
and preserves the existing ones, but per its own docs *"You must be a Site Collection Admin to run this
command"*; if you are not, and you hold the SharePoint Online admin role, it directs you to
`Set-PnPTenantSite -Owners`
(https://pnp.github.io/powershell/cmdlets/Add-PnPSiteCollectionAdmin.html). That is the bootstrap dependency
again, one layer down: the cmdlet that grants site collection admin needs you to already be one, so the
tenant-admin cmdlet is the real entry point.

**Trap worth calling out explicitly, added 2026-09-01.** `Set-PnPTenantSite -Owners` is safe on a OneDrive:
verbatim, *"Specifies owner(s) to add as secondary site collection administrators. They will be added as
additional secondary site collection administrators. Existing administrators will stay."*
(https://pnp.github.io/powershell/cmdlets/Set-PnPTenantSite.html). Its sibling parameter
`-PrimarySiteCollectionAdmin`, present on both `Set-PnPTenantSite` and `Add-PnPSiteCollectionAdmin`, is
**not** safe: it replaces the primary site collection administrator, which is precisely the operation
`Set-SPOSite -Owner` documents as "not supported and causes many experiences to break" on a OneDrive (§2.1).
The PnP docs carry no such warning, so this is a foot-gun the plan must forbid in code review, not just in
prose. Use `-Owners` only.

**UNVERIFIED:** whether `Add-PnPSiteCollectionAdmin` / `Set-PnPTenantSite -Owners` works against a personal
(`-my.sharepoint.com/personal/...`) site. The PnP page does not say. *Test:* run it against a scratch tenant
OneDrive URL as SharePoint Administrator and check both the exit status and whether `Get-SPOUser` then
reports `IsSiteAdmin`.

### 2.3 Module / PowerShell-edition matrix — this is the decisive table

| Module | Provides | PowerShell edition | Runs natively in the app's `pwsh` host? |
|---|---|---|---|
| `Microsoft.Online.SharePoint.PowerShell` (SharePoint Online Management Shell) | `Connect-SPOService`, `Set-SPOUser`, `Set-SPOSite`, `Get-SPOSite`, `Set-SPOTenant` | **Windows PowerShell 5.1**; in pwsh 7 only via the WinPS compatibility shim | **No — shim only** |
| `PnP.PowerShell` v3 | `Connect-PnPOnline`, `Add-PnPSiteCollectionAdmin`, `Set-PnPTenantSite`, file cmdlets | **PowerShell 7.4.6+ / .NET 8, native** | **Yes** |

Evidence for the SPO Management Shell being a Windows PowerShell module — verbatim from
[Get started with the SharePoint Online Management Shell](https://learn.microsoft.com/en-us/powershell/sharepoint/sharepoint-online/connect-sharepoint-online):

> "In order to run SharePoint Online PowerShell commands in a Windows PowerShell 7 console, you must import
> the SharePoint module using the -UseWindowsPowerShell parameter."
>
> `Import-Module Microsoft.Online.SharePoint.PowerShell -UseWindowsPowerShell`

The same page's install instruction is gated on "If your operating system is using Windows PowerShell 5".
Corroborating: Microsoft's own
[PowerShell 7 module compatibility](https://learn.microsoft.com/en-us/powershell/scripting/whats-new/module-compatibility?view=powershell-7.5)
list of "modules known to support PowerShell 7" includes **Exchange Online Management 2.0** ("EXO v2.0.4 or
later is supported in PowerShell 7.0.3 or later") and does **not** include
`Microsoft.Online.SharePoint.PowerShell`.

Two honesty notes on that corroboration, added after re-verification on 2026-09-01. First, that page opens
"This article contains a **partial list** of PowerShell modules published by Microsoft," so absence from it
is weak evidence on its own. The load-bearing evidence is the `-UseWindowsPowerShell` instruction above,
which is unambiguous and is on the module's own get-started page. Second, and pushing the same way: Microsoft's
newest OneDrive lifecycle controls (the unlicensed-account Standard storage period and per-account
reactivation dates, `Set-SPOTenant -UnlicensedOneDriveAccountsActiveStoragePeriod` and
`Set-SPOSiteArchiveState -KeepActiveUntil`) are documented **only** through this module, connect with
`Connect-SPOService`, and carry a floor of *"PowerShell version 16.0.27600.12000 or higher"*
(https://learn.microsoft.com/en-us/sharepoint/standard-storage-unlicensed-onedrive-accounts). So the module is
not a legacy surface being wound down; it is where the current controls are shipping, and it still has no
`-AccessToken`.

What `-UseWindowsPowerShell` actually costs, verbatim from
[about_Windows_PowerShell_Compatibility](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_windows_powershell_compatibility?view=powershell-7.5):

> "PowerShell creates a remote session named `WinPSCompatSession` that runs in a background Windows
> PowerShell 5.1 process."
>
> Limitations: "1. Only works locally on Windows computers 2. Requires Windows PowerShell 5.1 3. **Operates on
> serialized cmdlet parameters and return values, not on live objects** 4. Shares a single runspace for all
> modules imported into the Windows PowerShell remoting session."

So going the SPO Management Shell route means: `UnifiedDirectoryManager.exe` → child `pwsh 7` →
implicit-remoting proxy → background `powershell.exe 5.1` → CSOM → SharePoint. Three processes, proxy modules
written to `$Env:TEMP`, deserialized objects. It works, but it is a materially worse host story than the EXO
channel, and it adds Windows PowerShell 5.1 module-path placement as a new prerequisite — the module must be
importable by WinPS 5.1, not by pwsh.

One more documented gotcha from the Get-started page, relevant to a machine that also has SharePoint dev
tooling: "There is a known issue between the SharePoint Online Management Shell module and SharePoint Client
Components SDK where the module will fail to load if both are installed on the same computer."

---

## 3. Authentication — can the "borrow the token" pattern be reused?

### 3.1 SharePoint Online Management Shell: **NO. There is no `-AccessToken`.**

Full parameter surface of `Connect-SPOService`, from
[the cmdlet reference](https://learn.microsoft.com/en-us/powershell/module/sharepoint-online/connect-sposervice?view=sharepoint-ps),
across all four parameter sets:

- `AuthenticationLocation`: `-Url`, `-Credential`, `-ClientTag`, `-ModernAuth`, `-UseSystemBrowser`,
  `-Region`
- `AuthenticationUrl`: `-Url`, `-Credential`, `-ClientTag`, `-ModernAuth`, `-UseSystemBrowser`,
  `-AuthenticationUrl` (mandatory). Re-checked 2026-09-01: the first draft merged these two sets and put
  `-Region` and `-AuthenticationUrl` in the same list, which they are not; `-Region` belongs to
  `AuthenticationLocation` and `AuthenticationCertificate`, `-AuthenticationUrl` to `AuthenticationUrl` and
  `AuthenticationCertificate`. The conclusion is unaffected.
- `AuthenticationCertificate`: `-Url`, `-ClientId`, `-TenantId`, `-Certificate` / `-CertificatePath` /
  `-CertificateThumbprint` / `-CertificatePassword`, `-Region`, `-AuthenticationUrl`, `-ClientTag`
- `AuthenticationManagedIdentity`: `-Url`, `-ManagedIdentity`, `-ManagedIdentityType`,
  `-ManagedIdentityClientId`, `-ClientTag`

**There is no `-AccessToken` parameter in any parameter set.** The EXO pattern — mint a token in C#, hand it
to the module — has no counterpart here. The three real options are:

1. `-Credential` — a username and password. **Incompatible with the app's security model and with MFA.**
   Do not consider it.
2. `-UseSystemBrowser $true` — "Uses the Microsoft Authentication Library (MSAL) to authenticate the user by
   using the system browser." No secret, no certificate. **But it is a second, separate interactive sign-in**,
   in a child process, with its own token cache, on top of the sign-in the admin already did. Note also the
   documented `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\SPO\CMDLETS\UseSystemBrowser` registry alternative — a
   machine-wide, HKLM setting this app should not be writing.
3. `-ClientId` + `-TenantId` + certificate — **this is the app-only/certificate departure. Flagging it
   loudly, as instructed: adopting this means the app ships or provisions an X.509 certificate and the
   "public client, PKCE, no secret" statement in the README stops being true.** It is a real architectural
   change requiring the user's explicit sign-off, not an implementation detail.

Also from the same page, an operational constraint worth carrying into the design: "Only one SharePoint
Online service connection is supported per Windows PowerShell session and per geo within an organization."

### 3.2 PnP.PowerShell: **YES, with two new prerequisites.**

PnP.PowerShell *does* accept a borrowed token. From
[Connect-PnPOnline](https://pnp.github.io/powershell/cmdlets/Connect-PnPOnline.html):

> `-AccessToken`: "Using this parameter you can provide your own access token. Notice that it is recommend to
> use one of the other connection methods as this will limits the offered functionality on PnP PowerShell."
>
> "For instance if the token expires (typically after 1 hour) will not be able to acquire a new valid token,
> which the other connection methods do allow. You are responsible for providing your own valid access token
> when using this parameter, for the correct audience, with the correct permissions scopes."

And the scoping rule, from the
[authentication article](https://pnp.github.io/powershell/articles/authentication.html):

> "if you pass in an access token for your SharePoint Online tenant, you can only execute cmdlets that will
> directly target your SharePoint Online environment. If you would use a cmdlet that communicates with
> Microsoft Graph behind the scenes, it will throw an access denied exception."

`Add-PnPSiteCollectionAdmin` and `Set-PnPTenantSite` are SharePoint-CSOM cmdlets, so a SharePoint-audience
token is the right one. This is a genuine, secret-free reuse of the EXO pattern:

```
GetAccessTokenAsync(["https://<tenant>-admin.sharepoint.com/.default"])
  -> child pwsh -> Connect-PnPOnline -Url https://<tenant>-admin.sharepoint.com -AccessToken $t
  -> Add-PnPSiteCollectionAdmin / Set-PnPTenantSite -Owners
```

The one-hour non-renewal caveat is not a problem for this shape: each call is short, and the app re-mints
silently per operation exactly as `ExchangeService.EnsureConnectedAsync` does today
(`ExchangeService.cs:909-930`).

The two new prerequisites:

1. **The app registration must gain a delegated *SharePoint* API permission.** Resource "Office 365
   SharePoint Online", app id `00000003-0000-0ff1-ce00-000000000000`; the scope is `AllSites.FullControl`.
   **Better source found 2026-09-01.** The first draft cited the app-only guidance page, which shows the
   scope only incidentally inside a `Register-PnPAzureADApp` example
   (`-SharePointDelegatePermissions "AllSites.FullControl"`) and never states the app id. The page that
   states both, and that settles what was previously an UNVERIFIED, is **SharePoint admin APIs authentication
   and authorization**
   (https://learn.microsoft.com/en-us/sharepoint/dev/sp-add-ins/sharepoint-admin-apis-authentication-and-authorization),
   verbatim:

   > "Admin API operations on behalf of a user require applications to receive consent for SharePoint
   > `AllSites.FullControl` application permission. This permission requires admin consent on the consuming
   > tenant before any user from the tenant can consent to it."

   and, in the same page's "What's next":

   > "Configure your application manifest to request the required permissions for Office 365 SharePoint Online
   > (resourceAppId: `00000003-0000-0ff1-ce00-000000000000`)."
   >
   > | Access type | Permission name | `resourceAccess` id | `resourceAccess` type |
   > | On behalf of a user | `AllSites.FullControl` | `56680e0d-d2a3-4ae1-80d8-3c4f2100e3d0` | Scope |
   > | Without a user | `Sites.FullControl.All` | `a82116e5-55eb-4c41-a434-62fe8a61c773` | Role |

   That is a directly usable manifest entry: type `Scope` means delegated. Structurally identical to what the
   app already does for Exchange (`https://outlook.office365.com/.default`, `ExchangeService.cs:24`), and it
   requires **no secret and no certificate**, only an admin-consented delegated scope.

   **Partly settled, partly still open.** Microsoft names `AllSites.FullControl` as *the* delegated permission
   for admin APIs, so the earlier "what is the minimum scope" question is answered as far as documentation
   goes, and the same page adds *"We are currently working on providing more granular, less-permissive scopes
   for applications to use based on what admin APIs the applications want to have access to."* What is still
   **UNVERIFIED** is whether a lesser scope such as `AllSites.Manage` happens to work in practice for the two
   cmdlets this feature actually needs. *Test:* mint tokens for the `-admin.sharepoint.com` audience with
   `AllSites.FullControl`, then with `AllSites.Manage`, and see which lets `Add-PnPSiteCollectionAdmin`
   succeed. Decode `aud`/`scp` the way `LogTokenClaims` (`ExchangeService.cs:1234-1249`) already does for EXO.
2. **`PnP.PowerShell` v3 becomes a documented per-machine prerequisite**, alongside pwsh 7 and
   `ExchangeOnlineManagement`. **Corrected 2026-09-01: PnP's two own pages disagree on the floor, and the
   ".NET 8" half was not sourced at all.** The v3.0.0 release post (27 Mar 2025) says *"PnP PowerShell 3.0
   requires `PowerShell 7.4.6` or later versions of it"*
   (https://pnp.github.io/blog/pnp-powershell/pnp-powershell-v3-0-0/). The current installation page says
   *"You need PowerShell 7.4.0 or later to use PnP PowerShell. It is available for Windows, Linux and Mac"*
   (https://pnp.github.io/powershell/articles/installation.html). Neither page states a .NET version; that
   claim is withdrawn. Practical guidance: pin the README to **7.4.6+**, because it is the stricter of the two
   documented floors and costs nothing, but do not present 7.4.6 as the only documented figure.

### 3.3 PnP's own app-registration requirement — real, but it does not bite here

Widely reported and confirmed. From
[Register an Entra ID Application to use with PnP PowerShell](https://pnp.github.io/powershell/articles/registerapplication.html):

> "It has always been a recommended practice to register your own Entra ID Application to use with PnP
> PowerShell. **As of September 9th, 2024, this has become mandatory step.**"

Microsoft retired the shared multi-tenant "PnP Management Shell" app, so `Connect-PnPOnline -Interactive` now
requires `-ClientId` of a tenant-owned registration (or the `ENTRAID_APP_ID` / `ENTRAID_CLIENT_ID` environment
variable, or `Set-PnPManagedAppId`). The registration PnP creates for interactive use is a public client with
an `http://localhost` redirect — the same shape as this app's own registration.

**This requirement does not block us**, because on the `-AccessToken` path PnP never performs its own sign-in;
the app's *existing* registration is the client. Per the Connect-PnPOnline reference, `-ClientId` is mandatory
only in the ACS-legacy and app-only-with-certificate parameter sets, and optional for Credentials/DeviceLogin/
Interactive. That is fortunate and worth stating explicitly in the plan, because "PnP now needs its own app
registration" is the objection most likely to be raised second-hand.

### 3.4 Roles the signed-in admin needs — distinct from app permissions

Two independent axes; conflating them is a common planning error.

| Axis | Requirement |
|---|---|
| **App (Entra) permissions** — delegated, admin-consented once | Graph: `Files.ReadWrite.All` and/or `Sites.ReadWrite.All` (neither is currently requested — `GraphService.cs:19-27`). SharePoint: `AllSites.FullControl` (delegated) if the PnP path is taken. |
| **Directory role on the human signing in** | **SharePoint Administrator**, or Global Administrator, which "already has all the permissions of the SharePoint Administrator role" ([sharepoint-admin-role](https://learn.microsoft.com/en-us/sharepoint/sharepoint-admin-role)). `Set-SPOUser`: "You must be at least a SharePoint administrator to run the cmdlet." |
| **Per-site permission** | Site collection administrator **on that specific OneDrive** — because Global and SharePoint Admins "don't have automatic access to ... each user's OneDrive". Must be granted before any Graph file call against that drive. |

Note also, from `Set-SPOUser`: "This cmdlet is not supported for use with granular delegated admin privileges
(GDAP)" — relevant only if this tenant is ever managed by a CSP partner.

Role-change latency, from the same admin-role page: "If a user's role is changed so they gain or lose access
to the SharePoint admin center, it takes about an hour for the change to take effect." Do not design a flow
that assumes a just-granted SharePoint Administrator role is immediately usable.

**UNVERIFIED but high-confidence inference:** once the signed-in admin has been made site collection admin on
the departing user's OneDrive, delegated `Files.ReadWrite.All` calls against that drive succeed, because site
collection admin confers full control of the site and the delegated grant is "all files that the signed-in
user can access". Both halves are cited above; the join is an inference, not a quotation. *Test:* as a
SharePoint Admin who is **not** site collection admin on target OneDrive X, call `GET /users/{x}/drives` and
record the status; then `Set-SPOUser -Site <X> -LoginName <admin> -IsSiteCollectionAdmin $true`, wait, and
repeat. Expect 403 → 200. This is the cheapest experiment in the whole plan and it should be run before
anything else is designed.

---

## 4. Throttling

All figures verbatim from
[Avoid getting throttled or blocked in SharePoint Online](https://learn.microsoft.com/en-us/sharepoint/dev/general-development/how-to-avoid-getting-throttled-or-blocked-in-sharepoint-online).
Note Microsoft's caveat under every table: "Displayed limits are default values. Microsoft may change these
limits at any time."

**Graph resource-unit cost per request:**

| Resource units | Operations |
|---|---|
| 1 | Single item query (get item); delta **with** a token; download file from drive item |
| 2 | Multi-item query (list children), except delta with a token; **create, update, delete, and upload** |
| 5 | **All permission resource operations**, including `$expand=permissions` |

**User throttling** (the axis a delegated app runs on): Requests 3,000 per 5 min, Ingress 50 GB/hour,
Egress 100 GB/hour, Delegation Token Request 50 per 5 min, External sharing emails 200/hour.

**A caveat the first draft omitted, and it is pointed straight at this feature.** Under every throttling
table on that page Microsoft adds, alongside the "default values" note: *"Microsoft Reserves the right to
lower limits on Unpaid/Unlicensed usage."* A departing user's OneDrive is by definition unlicensed the moment
the licence is pulled or the account is deleted, so the published ceilings are exactly the ones least safe to
assume for the drive this feature reads from.

**Per-app-per-tenant:** Resource Units 1,250/min at 0–1,000 licences rising to 6,250/min at 50,000+;
1,200,000 per 24 h at 0–1,000 licences rising to 6,000,000; Ingress and Egress 400 GB/hour each.

**Tenant:** Resource Units 18,750 per 5 min at 0–1,000 licences, up to 93,750 at 50,000+.

How SharePoint/OneDrive throttling differs from Graph throttling generally: it is metered in *resource units*,
not raw request counts, and — importantly — "Every request that an application makes across all API endpoints,
including Microsoft Graph, CSOM, and REST, counts towards the application's usage." A copy orchestrated over
Graph and a `Set-SPOUser` issued over CSOM draw on the same bucket. Microsoft's own sizing rule: "you can
estimate the request rate using an average of 2 resource units per request, and divide resource unit limits by
2 to get the estimated request rate." And on the CSOM/REST path specifically: "CSOM and REST don't have a
predetermined resource unit cost, and they usually consume more resource units than Microsoft Graph APIs to
achieve the same functionality ... they may experience more throttling than the limits described in this
document."

**What a whole-OneDrive bulk copy runs into:**

- Every enumerate page costs 2 RU; **every permission read costs 5 RU** — and permission reads are exactly
  what a "preserve who had access" feature would need, at 2.5× the cost of everything else.
- Egress/ingress: a 200 GB OneDrive exceeds the 100 GB/hour per-user egress ceiling outright.
- Microsoft names this workload as a throttling cause: "Failing to follow best practices for scanning
  applications that process files in bulk will likely result in throttling. These apps include sync engines,
  backup providers, search indexers, classification engines, data loss prevention tools, and any other tool,
  that attempts to reason over the entirety of data and apply changes to it."
- Mandatory handling: "we require using the `Retry-After` HTTP header." "Throttled requests count towards
  usage limits, so failure to honor `Retry-After` may result in more throttling." And note: "SharePoint Online
  does not return or support `IETF RateLimit` headers" — so `Retry-After` is the only signal to code against.
- Traffic decoration is not optional politeness: "Well-decorated traffic will be prioritized over traffic that
  isn't properly decorated." The documented format for an in-house app is
  `NONISV|<CompanyName>|<AppName>/<Version>` as the `User-Agent`. **The app sets no User-Agent on Graph calls
  today** (nothing in `GraphService.cs` configures one) — worth fixing regardless of which OneDrive design wins.
- One more, specific to this feature: "When a client makes excessive attempts to access many OneDrive site
  collections that do not exist, we may throttle requests from that client's IP address."
- Blocking is the escalation: "A block — which is placed at the app or user level — prevents the offending
  process from running until you fix the problem," notified via the Message Center.

**Hard operation limits**, from
[SharePoint limits → Moving and copying across sites and containers](https://learn.microsoft.com/en-us/office365/servicedescriptions/sharepoint-online-service-description/sharepoint-online-limits):

> "Total File Size: No more than 100 GB total file size Number of Files: No more than 30,000 files
> Specific Settings or Configurations: OneNote Files: 2 GB limit · Cross-Geo Copy or Move: 15 GB file size limit"

`driveitem-copy` restates it: "The copy operation is restricted to 30,000 driveItems." Plus, app-only only:
"Cross-geo copy isn't supported when using app-only authentication" — a delegated design sidesteps that one.
Also relevant from the same limits page: 250 GB per-file upload limit; "The entire decoded file path,
including the file name, can't contain more than 400 characters" — a copy that nests the source tree under
`Files from <name>/` **lengthens every path** and can push previously-legal files over 400 characters.

---

## 5. What is asynchronous, and how completion is reported

**`driveItem: copy` is the only long-running action in this area.** From
[Working with long-running actions](https://learn.microsoft.com/en-us/graph/long-running-actions-overview),
whose "Supported resources" table lists exactly one row: `driveItem` → `copy`.

The pattern:

```
POST /drives/{d}/items/{i}/copy   ->  HTTP/1.1 202 Accepted
                                      Location: https://contoso.sharepoint.com/_api/v2.0/monitor/<guid>
GET <monitor url>                 ->  { "percentageComplete": 27.8, "status": "inProgress" }
GET <monitor url>                 ->  { "percentageComplete": 100.0, "resourceId": "01MOW...", "status": "completed" }
```

Three properties of the monitor URL that matter for the app's architecture:

1. **"This request doesn't require authentication, because the URL is short-lived and unique to the original
   caller."** So polling needs no token and no PowerShell — a plain `HttpClient` in C# suffices.
2. **"The location URL returned might not be on the Microsoft Graph API endpoint."** Correction 2026-09-01:
   the earlier claim that "it is a `*.sharepoint.com/_api/v2.0/monitor/...` URL" was too specific. The docs
   show at least three shapes: `https://api.onedrive.com/monitor/<guid>` (long-running-actions overview),
   `https://contoso.sharepoint.com/_api/v2.0/monitor/<guid>` and
   `https://contoso.sharepoint.com/sites/FromSite/_api/v2.1/monitor/<guid>` (driveItem: copy examples 1 and
   10). Treat the `Location` header as an opaque absolute URL. Any code that assumes a `graph.microsoft.com`
   base will break, and so will any code that assumes a `sharepoint.com` one.
3. Failure is reported *in the status body*, not in the 202. A name collision returns 202 up front and then
   `{"status":"failed","error":{"code":"nameAlreadyExists"}}` on the monitor; a `childrenOnly` copy over
   existing folders returns a `details[]` array of per-item errors. **A copy step that only checks the POST
   result will silently report success on a failed copy.** Set `@microsoft.graph.conflictBehavior=rename`
   (`replace` is files-only and "folders with conflicts use the `fail` behavior instead") and always read the
   terminal monitor state.

Also worth encoding from the copy reference: copying the drive root is rejected ("Cannot copy the root
folder") unless `childrenOnly: true`, and `childrenOnly` cannot be combined with `name`. And the known issue:
"the **includeAllVersionHistory** request parameter is ignored if the **name** request parameter is also
passed. To avoid this issue, perform the copy operation without the **name** parameter first, then rename the
target item when the copy completes."

The docs give no polling-interval guidance and no stated monitor-URL lifetime. **UNVERIFIED:** how long a
monitor URL remains valid, and what a poller should do if it 404s mid-job. *Test:* start a large copy, poll,
and separately re-poll the same URL after 1 h and after 24 h; record the response. Design defensively: treat
monitor loss as "unknown, verify by enumerating the destination", never as "failed".

### 5.1 The 90-second timeout: this cannot use the Exchange pattern as-is

Stated plainly, because the task asked for bluntness. The Exchange channel's contract is
`RunOpAsync(..., OpTimeout = 90s)` and **on expiry it kills the host process** (`ExchangeService.cs:1044-1060`;
the code comment at `:33-36` says as much — "A timeout kills the host process ... taking the session down").
A whole-OneDrive copy takes minutes to hours. Three consequences:

- **Nothing that waits for a copy to finish may run inside the pwsh channel.** Not at 90 s, not at 180 s, not
  with a raised timeout — raising it just makes a hung SharePoint call hold the serialised channel hostage
  longer, and the stated reason the timeout kills the process (a cmdlet cannot be interrupted mid-run) applies
  to CSOM exactly as it does to EXO.
- **The correct split is:** short, bounded, idempotent calls in pwsh — grant site collection admin, and revoke
  it afterwards, each a single CSOM round-trip — with everything long-running in C# against Graph, the monitor
  URL polled on the app's own schedule and surfaced through the `IProgress<string> live` channel
  `ScenarioRunner` already threads through (`ScenarioRunner.cs:24`, `:53`).
- **`ScenarioRunner` is synchronous per target** (`ScenarioRunner.cs:41-70`); a step that blocks for an hour
  blocks the whole scenario, including every other target. Either the OneDrive step is *fire-and-record* —
  start the copy, log the monitor URL and the destination folder, return immediately — or the runner needs a
  genuine long-running-job concept it does not have today. **Fire-and-record is the only option that does not
  require rearchitecting the runner**, and it changes what the operation log can honestly claim: "copy
  started", not "files transferred". That honesty matters for a termination audit trail.

---

## 6. The option the user has not yet been told about: do nothing, deliberately

The user is undecided between moving files and leaving them under retention. The second option is better
documented and largely automatic — but it has a teeth-bearing caveat.

From [OneDrive retention and deletion](https://learn.microsoft.com/en-us/sharepoint/retention-and-deletion):

> "**By default, when a user is deleted, the user's manager is automatically given access to the user's
> OneDrive.**"

Configured at SharePoint admin center → More features → User profiles → Setup My Sites → My Site Cleanup →
**Enable access delegation**, with an optional **secondary owner**, "the appointed owner of the OneDrive if the
user's manager isn't set in Microsoft Entra ID". Warning from the same page: "If access delegation is disabled
or a manager or secondary owner isn't set for a user, no one has automatic access when the user is deleted or
be warned that the OneDrive will be deleted."

The documented timeline: user deleted from Entra ID → deletion syncs to SharePoint → the OneDrive Clean Up Job
marks the OneDrive for deletion → the manager (or secondary owner) is emailed that they have access and that
it will be deleted at the end of the retention period → a second reminder email 7 days before expiry → the
OneDrive moves to the site collection recycle bin, "where it's kept for 93 days", during which "users will no
longer be able to access any shared content."

Critically: "The OneDrive retention period for cleanup of OneDrive begins when a user account is deleted from
Microsoft Entra ID. **No other action causes the cleanup process to occur, including blocking the user from
signing in or removing the user's license.**" A terminate scenario that disables rather than deletes fires
none of this.

Retention window: 30 days by default, "Enter a value from 30 through 3650" days
([Set the OneDrive retention for deleted users](https://learn.microsoft.com/en-us/sharepoint/set-retention)),
or `Set-SPOTenant -OrphanedPersonalSitesRetentionPeriod <int32>`.

**The caveat that must be in the plan, verbatim:**

> "All OneDrive accounts that don't have a valid OneDrive license are automatically archived on their 93rd
> unlicensed day. Retention settings, retention policies, eDiscovery, and all holds are still honored while an
> unlicensed account is in the paid archive state. **After 12 month of Unpaid storage/archive the OneDrive Data
> might be deleted regardless of Retention settings, retention policies, eDiscovery, and all holds.**"

And: "When a user account is deleted from Microsoft Entra ID, the OneDrive account is considered unlicensed."

A terminate scenario that strips the licence starts a 93-day archive clock the operator probably does not know
about. "Leave it in place under retention" is therefore **not** indefinite, and it interacts badly with the
app's existing unlicensing behaviour. This deserves to be surfaced as a decision input, not buried.

Also note the precedence rule, which cuts both ways: "Microsoft 365 retention settings from retention policies
and retention labels always take precedence to the standard OneDrive deletion process, so content included for
OneDrive retention could be deleted before 30 days or retained for longer than the OneDrive retention period."
The app cannot predict the outcome without knowing the tenant's Purview configuration, so it should not claim
to.

### 6.1 Microsoft's own Google-parity feature exists — and is not automatable

From the same page, linking to
[Simplified file transfer for departing employees](https://support.microsoft.com/office/simplified-file-transfer-for-departing-employees-125a47a6-2a93-42e8-88ac-241ca5c1b118):
the manager or secondary owner who received automatic access can move files out, and unlike a Graph copy,
**"It's a full move. The files are relocated from the original OneDrive to the new destination, and their
location is updated accordingly."** Sharing is retained by ticking **"Keep sharing with the same people"**
during the transfer, and collaborators receive a single consolidated notification when six or more files move
together. Not transferred: Microsoft Lists and Forms, which the page says "aren't stored as traditional files
in OneDrive."

Two corrections from the 2026-09-01 re-fetch. **The URL used in §9 of the first draft 404s**: the working
canonical is `https://support.microsoft.com/office/simplified-file-transfer-for-departing-employees-125a47a6-2a93-42e8-88ac-241ca5c1b118`
(the form with an `/en-us/onedrive/` path segment *and* the GUID does not resolve; the bare
`https://support.microsoft.com/en-us/onedrive/simplified-file-transfer-for-departing-employees` does).
And the phrase *"the ability to move multiple files while preserving sharing permissions"* could not be found
on the page; it is replaced above with wording that was actually read. The substance is unchanged.

That is the closest thing Microsoft has to Google's transfer, and it preserves sharing, which a Graph copy
does not. **But the support article documents no admin-center UI, no cmdlet and no Graph API** — it is driven
by the manager in the OneDrive web experience.

**UNVERIFIED:** whether any API or cmdlet backs it. *Test:* capture the network traffic of a transfer performed
in the OneDrive web UI and identify the endpoint; separately, search the Graph changelog and the beta reference
for a cross-drive move. Until that is settled, treat it as **not automatable**, and consider whether this app's
best contribution is to *set up the conditions* for it — confirm the manager attribute is populated, confirm
access delegation is enabled, confirm a secondary owner exists, and record the OneDrive URL in the operation
log — rather than to reimplement it badly over copy.

---

## 7. Blunt summary of blockers

1. **No ownership transfer exists in Microsoft 365.** Graph has no such operation; `Set-SPOSite -Owner` on a
   OneDrive is documented as "not supported and causes many experiences to break". A faithful Google-parity
   feature cannot be built. Anything shipped will be copy-plus-grant, which loses permissions, metadata and
   (by default) version history.
2. **Delegated Graph cannot touch another user's OneDrive out of the box.** SharePoint Administrator is not
   enough; the admin must first be made site collection admin on that specific OneDrive. That grant is the
   critical-path dependency for everything else.
3. **The grant cannot be done over Graph.** It needs the SharePoint admin surface, and `Connect-SPOService`
   has **no `-AccessToken`** — the Exchange borrow-the-token pattern does not transfer to the SharePoint
   Online Management Shell, which is additionally a Windows PowerShell 5.1 module reachable from pwsh 7 only
   through the compatibility shim.
4. **`PnP.PowerShell` is the way through, and it is a real one:** native pwsh 7.4.6+, accepts `-AccessToken`
   for SharePoint-audience tokens, and its mandatory-own-app-registration rule applies to its interactive
   flows, not to a caller-supplied token. Cost: one new delegated SharePoint API permission on the existing
   registration (no secret, no certificate) and one new machine prerequisite.
5. **The certificate path is the loud one.** `Connect-SPOService -ClientId -TenantId -Certificate` works and
   would be simpler, but it makes the app's "public client, PKCE, no secret" statement false. It should not be
   adopted without the user explicitly deciding to change that stance.
6. **Nothing long-running may go through the pwsh channel.** The 90-second op timeout kills the host process.
   Copy is 202-plus-monitor-URL; the monitor needs no auth and is not on the Graph endpoint, so poll it from
   C#. `ScenarioRunner` is synchronous per target, so realistically the step is fire-and-record.
7. **Bulk copy hits hard walls:** 30,000 items and 100 GB per copy operation, 100 GB/hour per-user egress,
   3,000 requests per 5 minutes per user, permission operations at 5 resource units each, 400-character paths.
   A large OneDrive will exceed at least one of these, and the design must say what happens when it does.
8. **The "leave it under retention" option has a licence trap at 60 and 93 days** and a documented "might be
   deleted regardless of ... holds". **Corrected 2026-09-01: the earlier phrasing "after 12 months of
   archive" was wrong.** The 365-day clock is counted in *cumulative unpaid days from the day the account
   became unlicensed*, not from the day it was archived. The unlicensed article's own milestone table puts
   Day 1 at licence removal or Entra deletion, Day 60 read-only, Day 93 archive, Day 275 out of eDiscovery,
   Day 365 subject to deletion. Deletion risk therefore begins roughly 272 days after archival, not 365.
   Also missing from the first draft: the read-only milestone at **Day 60** arrives a month before archival,
   and both 60 and 93 are *tenant defaults* that Standard storage can extend (93 through 3,650 days, via
   `Set-SPOTenant -UnlicensedOneDriveAccountsActiveStoragePeriod`, generally available from October 2026).
   https://learn.microsoft.com/en-us/sharepoint/unlicensed-onedrive-accounts /
   https://learn.microsoft.com/en-us/sharepoint/standard-storage-unlicensed-onedrive-accounts
   It is not the safe do-nothing default it appears to be.
9. **The app is not currently permitted to do any of this**, but the consent cost is smaller than this brief
   first claimed. No `Files.*` or `Sites.*` Graph scope is requested today, so shipping requires a new
   consent event. **Corrected 2026-09-01:** per the permissions reference table in §1.1, delegated
   `Files.ReadWrite.All` and `Sites.ReadWrite.All` are marked *"Admin Consent Required: No"*, so a signed-in
   SharePoint Administrator can consent for themselves at first use. What genuinely does require a tenant
   admin-consent event is delegated `Sites.FullControl.All` (Graph) and, on the PnP route, SharePoint
   `AllSites.FullControl`, which Microsoft documents as *"requires admin consent on the consuming tenant
   before any user from the tenant can consent to it"*
   (https://learn.microsoft.com/en-us/sharepoint/dev/sp-add-ins/sharepoint-admin-apis-authentication-and-authorization).
   So: a Graph-copy-only design is a code change plus a first-use consent prompt; the PnP/SharePoint-admin
   route is a code change plus a real tenant admin-consent deployment step.

---

## 8. The four experiments that should run before any design is locked

1. **The 403 test.** As SharePoint Admin *without* site collection admin on target OneDrive X, call
   `GET /users/{x}/drives`; record the status. Then `Set-SPOUser -Site <X> -LoginName <admin>
   -IsSiteCollectionAdmin $true` and repeat. Expect 403 → 200. (Settles §1.1 / §3.4 — the whole shape of the
   feature depends on this.)
2. **The token test.** Mint `https://<tenant>-admin.sharepoint.com/.default` from the existing public-client
   registration; decode `aud` and `scp`; `Connect-PnPOnline -AccessToken`; run `Add-PnPSiteCollectionAdmin`
   against a scratch OneDrive. (Settles §2.2 / §3.2 — decides PnP vs certificate vs second sign-in.)
3. **The copy test.** Cross-drive copy a ~5,000-item folder; time it; poll the monitor URL with no auth
   header; re-poll after 1 h and 24 h. Confirm the terminal `status` and whether permissions and versions
   survive. (Settles §5.)
4. **The owner-role test.** `POST .../invite` with `roles: ["owner"]`; record the exact error. (Settles §1.3 —
   a cheap way to close off the last "maybe there's a way" question.)

---

## 9. Sources

Microsoft Learn / Microsoft Support (all fetched and read):

- https://learn.microsoft.com/en-us/graph/permissions-reference
- https://learn.microsoft.com/en-us/graph/api/drive-get?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/drive-list?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/driveitem-copy?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/driveitem-move?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/driveitem-invite?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/long-running-actions-overview
- https://learn.microsoft.com/en-us/sharepoint/sharepoint-admin-role
- https://learn.microsoft.com/en-us/sharepoint/retention-and-deletion
- https://learn.microsoft.com/en-us/sharepoint/set-retention
- https://learn.microsoft.com/en-us/sharepoint/dev/general-development/how-to-avoid-getting-throttled-or-blocked-in-sharepoint-online
- https://learn.microsoft.com/en-us/sharepoint/dev/solution-guidance/security-apponly-azuread
- https://learn.microsoft.com/en-us/office365/servicedescriptions/sharepoint-online-service-description/sharepoint-online-limits
- https://learn.microsoft.com/en-us/powershell/sharepoint/sharepoint-online/connect-sharepoint-online
- https://learn.microsoft.com/en-us/powershell/module/sharepoint-online/connect-sposervice?view=sharepoint-ps
- https://learn.microsoft.com/en-us/powershell/module/sharepoint-online/set-spouser?view=sharepoint-ps
- https://learn.microsoft.com/en-us/powershell/module/sharepoint-online/set-sposite?view=sharepoint-ps
- https://learn.microsoft.com/en-us/powershell/scripting/whats-new/module-compatibility?view=powershell-7.5
- https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_windows_powershell_compatibility?view=powershell-7.5
- https://support.microsoft.com/office/simplified-file-transfer-for-departing-employees-125a47a6-2a93-42e8-88ac-241ca5c1b118
  (the `/en-us/onedrive/...-125a47a6-...` variant used in the first draft returns HTTP 404)
- https://learn.microsoft.com/en-us/onedrive/developer/rest-api/concepts/permissions_reference?view=odsp-graph-online
- https://learn.microsoft.com/en-us/sharepoint/dev/sp-add-ins/sharepoint-admin-apis-authentication-and-authorization
- https://learn.microsoft.com/en-us/sharepoint/standard-storage-unlicensed-onedrive-accounts
- https://learn.microsoft.com/en-us/sharepoint/unlicensed-onedrive-accounts
- https://learn.microsoft.com/en-us/graph/api/resources/site?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/site-post-permissions?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/resources/backuprestoreroot?view=graph-rest-1.0
- https://learn.microsoft.com/en-us/graph/api/backuprestoreroot-post-onedriveforbusinessrestoresessions?view=graph-rest-1.0

PnP PowerShell official documentation (community-maintained Microsoft 365 & Power Platform Community project,
not a Microsoft product doc — confidence slightly lower than learn.microsoft.com, but it is the authoritative
source for this module):

- https://pnp.github.io/powershell/articles/authentication.html
- https://pnp.github.io/powershell/articles/registerapplication.html
- https://pnp.github.io/powershell/cmdlets/Connect-PnPOnline.html
- https://pnp.github.io/powershell/cmdlets/Add-PnPSiteCollectionAdmin.html
- https://pnp.github.io/powershell/articles/installation.html
- https://pnp.github.io/powershell/cmdlets/Set-PnPTenantSite.html
- https://pnp.github.io/blog/pnp-powershell/pnp-powershell-v3-0-0/

Google (for the parity comparison only):

- https://knowledge.workspace.google.com/admin/drive/transfer-drive-files-to-a-new-owner-as-an-admin

Third party, used only where noted inline and marked as lower confidence:

- https://graphpermissions.merill.net/permission/Files.ReadWrite.All
- https://graphpermissions.merill.net/permission/Sites.FullControl.All

Repository code read directly: `Services/GraphService.cs`, `Services/ExchangeService.cs`,
`Services/ScenarioRunner.cs`, `Models/Scenario.cs`, `UnifiedDirectoryManager.csproj`.

---

## 10. Verification record, 2026-09-01

Every learn.microsoft.com and pnp.github.io source above was independently re-fetched and read, rather than
trusted because it was cited.

**Confirmed verbatim, unchanged:**

- All five Graph permission tables in §1.2 (`drive-get` including its "Application: Not supported." row,
  `drive-list`, `driveitem-copy`, `driveitem-move`, `driveitem-invite`). Every least/higher privileged cell
  matches.
- "Items cannot be moved between Drives using this request." (`driveitem-move`)
- Every `driveitem-copy` caveat: metadata not retained, permissions not retained, `includeAllVersionHistory`
  behaviour and the `name` known issue, 30,000-item restriction, cross-geo app-only limitation, root-folder
  and `childrenOnly` rejections, `conflictBehavior` semantics including "folders with conflicts use the
  `fail` behavior instead".
- `driveitem-invite`'s request body `"roles": [ "read | write"]`, with no `owner` value anywhere on the page.
  The §1.3 UNVERIFIED stands as written.
- Long-running actions: the supported-resources table still has exactly one row (`driveItem` / `copy`); "This
  request doesn't require authentication, because the URL is short-lived and unique to the original caller";
  "The location URL returned might not be on the Microsoft Graph API endpoint"; the `percentageComplete` 27.8
  / 100.0 examples. No monitor-URL lifetime is stated, so that UNVERIFIED stands.
- `Set-SPOSite -Owner`: "Changing the Owner of a OneDrive is not supported and causes many experiences to
  break", and the OneDrive valid-parameter list that does include `Owner`. The §2.1 trap is real.
- `Set-SPOUser`: syntax, "You must be at least a SharePoint administrator to run the cmdlet", and "This cmdlet
  is not supported for use with granular delegated admin privileges (GDAP)".
- `Connect-SPOService`: **no `-AccessToken` parameter exists in any of the four parameter sets.** Confirmed
  by reading the full parameter reference. Also confirmed: the `-UseSystemBrowser` MSAL description, the
  `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\SPO\CMDLETS\` registry alternative, and "Only one SharePoint Online
  service connection is supported per Windows PowerShell session and per geo within an organization."
- The `-UseWindowsPowerShell` requirement and the SharePoint Client Components SDK known issue.
- `about_Windows_PowerShell_Compatibility`'s `WinPSCompatSession` behaviour and its four limitations.
- PnP: the `-AccessToken` description word for word; the SharePoint-vs-Graph audience rule word for word; "As
  of September 9th, 2024, this has become mandatory step"; the `http://localhost` "Mobile and desktop
  applications" public-client registration shape; and, critically for §3.3, that **`-ClientId` is optional in
  the Access Token parameter set** and mandatory only in the ACS-legacy and certificate app-only sets. The
  §3.3 argument holds.
- `Add-PnPSiteCollectionAdmin`: "You must be a Site Collection Admin to run this command", with the pointer
  to `Set-PnPTenantSite -Owners` for SharePoint admins who are not.
- `sharepoint-admin-role`: all three quotations, including "don't have automatic access to all sites and each
  user's OneDrive" and the roughly one hour role-propagation delay.
- Every throttling figure in §4: the 1/2/5 resource-unit table, user limits, per-app-per-tenant limits
  (1,250/min to 6,250/min; 1.2M to 6M per 24h; 400 GB ingress and egress), tenant limits (18,750 to 93,750
  per 5 min), the Retry-After requirement, "SharePoint Online does not return or support `IETF RateLimit`
  headers", the `NONISV|CompanyName|AppName/Version` format, the bulk-scanning passage, the nonexistent-
  OneDrive-site-collections passage, and the blocking passage.
- Every SharePoint limits figure in §4: 100 GB / 30,000 files / 2 GB OneNote / 15 GB cross-geo, 250 GB upload,
  400-character path.
- §6's retention timeline in full, including "By default, when a user is deleted, the user's manager is
  automatically given access to the user's OneDrive", the access-delegation warning, the 7-day reminder, the
  93-day site collection recycle bin, "No other action causes the cleanup process to occur", the 30-3650
  range, and the unlicensed archive caveat block.

**Corrected in this pass:** §1.1's permission descriptions, which were paraphrases presented as verbatim;
§7.8's "12 months of archive", which mis-anchors the 365-day clock; §7.9's blanket admin-consent claim;
§3.2's PnP version floor and unsourced .NET 8; §3.2's SharePoint-scope sourcing and app id, now first-party;
§5's over-specific monitor-URL host; §6.1's 404 URL and an unfindable quotation; §3.1's merged parameter
sets. Added: the `-PrimarySiteCollectionAdmin` foot-gun in §2.2, the unlicensed-usage throttling caveat in
§4, the Standard storage lever in §7.8, and the Microsoft 365 Backup Graph v1.0 surface in §1.2.

**Not re-verified in this pass**, and so not to be treated as confirmed: nothing in §0 (repository code
citations were taken as given, since the reviewer's brief was the vendor documentation).
