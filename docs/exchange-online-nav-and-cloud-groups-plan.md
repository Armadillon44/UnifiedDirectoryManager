# Exchange Online navigation + cloud group creation

Status: **feature work COMPLETE — commits 1-7 all done and live-tested against the tenant. Remaining: the Wiki, then ship 2.3.0.** All decisions locked, with the D5 corrections in G11. Branch `release/2.3.0` (new
features → minor bump), branched from `master` at v2.2.0. See the sequencing section at the bottom for what
has landed; update it as commits land rather than leaving this line to go stale.

Four asks:

1. A new **Exchange Online** section in the nav tree, from which to view **mailboxes** and **distribution groups**.
2. Create **cloud-only groups**: security and Microsoft 365 in Entra, distribution groups in Exchange Online.
3. **Add members** to Exchange Online distribution groups.
4. **Basic view-only information** for Exchange Online mailboxes.

Constraint from the ask: distribution groups already appear under the Entra ID nav section. They stay there; the
Exchange Online write path gets wired in alongside.

---

## Locked decisions

All four are decided. Nothing below is open for renegotiation mid-implementation.

| # | Decision |
|---|---|
| **D1** | **Ship 2.2.0 first.** Done: [v2.2.0](https://github.com/Armadillon44/UnifiedDirectoryManager/releases/tag/v2.2.0) released, issue #3 closed. This work lands on `release/2.3.0`. |
| **D2** | **Four group types in one dialog, including mail-enabled security.** Security and Microsoft 365 route to Graph; Distribution and mail-enabled security route to Exchange (`New-DistributionGroup -Type Distribution` / `-Type Security`). The two Exchange types differ only by `-Type`, so the marginal cost is one radio button and its validation branch. |
| **D3** | **The Exchange list is the authority and write surface; the Entra list stays a read-only inventory.** The Exchange ▸ Distribution Groups list shows what Graph cannot expose (`IsDirSynced`, `ManagedBy`, `RequireSenderAuthenticationEnabled`, `MemberJoinRestriction`, member count), and add/remove member lives there. Rows carry **both** the primary SMTP address and `ExternalDirectoryObjectId`, so double-clicking an Exchange DL can still open its Entra detail (see G8). |
| **D4** | **Full basic mailbox set, size on demand.** Identity, type, addresses and proxy addresses, delivery and forwarding, quotas, litigation hold, retention policy, archive state, and the protocol flags — two cheap directory-backed calls on open. Size, item count, and last logon sit behind an explicit button, one mailbox at a time, never fanned out across a list (see G6). |

---

## Key finding: no new sign-in, no new consent

`GraphService.Scopes` (Services/GraphService.cs:20-27) already requests `Group.ReadWrite.All`, which is the
**least-privileged delegated permission for creating groups**. Creating cloud security and Microsoft 365 groups
therefore needs no scope change and no forced re-consent for existing users. (`Group.Create` is application-only and
cannot be requested as a delegated scope, so `Group.ReadWrite.All` is the correct and only choice here.)

Exchange Online likewise already has its channel, its token (`Exchange.Manage` against
`https://outlook.office365.com/.default`), and a working `AddDistributionGroupMemberAsync`
(Services/IExchangeService.cs:100). What is missing is list/read/create operations and the UI.

**What does change is RBAC.** Today `app/README.md` documents Recipient Management. Creating groups additionally needs
the *Distribution Groups* management role, and the hardcoded `-BypassSecurityGroupManagerCheck`
(Services/ExchangeService.cs:626, :642) needs Organization Management or *Security Group Creation and Membership*.
README and Wiki must say so, or operators hit permission errors the app cannot explain.

---

## Architecture decision: reuse the cloud pane, do not add a third one

The tempting design is a third top-level view alongside AD and Cloud. It is a trap:

- `IsAdView => !IsCloudView` (ViewModels/MainViewModel.cs:37) is binary, and MainWindow.xaml has **21** bindings to
  the pair, including `<MenuItem Header="_Edit" IsEnabled="{Binding IsAdView}">`.
- `ApplyDock` (Views/MainWindow.xaml.cs:148-160) calls `LayoutPane` exactly twice, for `PaneHost` and
  `CloudPaneHost`. A third host added to the XAML grid without a third `LayoutPane` call gets no row/column
  definitions and silently renders its children stacked on top of each other.
- `RefreshAsync` (MainViewModel.cs:228-243) branches only on `IsCloudView`; anything else falls through to the
  on-prem path.

**Decision: Exchange Online renders in the existing cloud pane.** `IsCloudView` keeps its meaning ("the cloud pane is
showing"), `ApplyDock` is untouched, and the whole list/detail/column/CSV stack
(`CloudObjectListViewModel`, `CloudObjectRow`, `CloudColumnCatalog`, `CloudDetailView`) is reused. The Exchange
section is a sibling root in the tree, not a sibling pane.

---

## Commit 1 · Exchange Online section in the nav tree (read-only lists)

**Model and routing**

- `CloudNodeKind` (ViewModels/TreeNodeViewModel.cs:9): add `Exchange`, `Mailboxes`, `DistributionGroups`.
  Glyphs: `✉` for the root, `📬` mailboxes, `📢` distribution groups.
- `CloudListMode` (Services/CloudColumnCatalog.cs:6): add `Mailboxes`, `DistributionGroups`, plus a
  `DistributionGroupMembers` mode for commit 3.
- `MainViewModel.EnsureCloudRoot` (:176-192): build a second root, "Exchange Online", under the same
  `_graph.IsSignedIn` gate as the Entra root. Rebuild both together (the method already tears down and rebuilds on
  every call, including after the Settings modal closes).
- `OnSelectedNodeChanged` (:196-215): add an explicit `case` per new kind. **The `default:` arm at :207 silently
  loads the Entra Users list**, so a missing case is invisible rather than a crash.
- Column sets in `CloudColumnCatalog`:
  - Mailboxes: display name, UPN, primary SMTP, type (`RecipientTypeDetails`), alias, directory sync, hidden from
    address lists, created.
  - Distribution groups: display name, primary SMTP, alias, type (distribution / mail-enabled security), managed by,
    directory sync, external senders allowed, members (count).
- Persist visible columns via two new `AppSettings` lists, matching `VisibleCloudGroupColumns`.

**Two existing defects this section exposes, fixed here**

- `OnTreeDragOver` / `OnTreeDrop` (Views/MainWindow.xaml.cs:117-131) filter only on `IsPlaceholder`, so **cloud tree
  nodes are already valid drop targets** and a drop reaches `MoveRowsToOuAsync` with a synthetic `cloud:Kind` DN.
  Add `CloudKind is null` to both predicates.
- `exchange.Configure(...)` is called only at startup from saved settings (App.xaml.cs:55-56). Signing in to a
  different tenant needs an app restart. Fix by calling it after a successful sign-in, but note `Configure` calls
  `Disconnect()` which does a blocking `_gate.Wait()` with no timeout (ExchangeService.cs:51, :64): on the UI thread
  with an operation in flight that hard-freezes the app. The call must be async or off-thread.

**Service work**

Two new `IExchangeService` methods returning `CloudPage`-shaped results, and two new ops in the `HostScript`
switch (ExchangeService.cs:543-653).

---

## Commit 2 · Create cloud groups

One dialog, `NewCloudGroupWindow`, modelled on `NewGroupWindow` / `NewGroupViewModel`. A type selector routes to one
of two backends:

| Type | Backend | Call |
|---|---|---|
| Security | Graph | `POST /groups` with `mailEnabled:false, securityEnabled:true, groupTypes:[]` |
| Microsoft 365 | Graph | `POST /groups` with `groupTypes:["Unified"], mailEnabled:true, securityEnabled:false` |
| Distribution | Exchange | `New-DistributionGroup -Type Distribution` |
| Mail-enabled security | Exchange | `New-DistributionGroup -Type Security` |

Graph returns **400 "Cannot Create a mail-enabled security groups and or distribution list"** for the bottom two, so
the routing is not an optimisation, it is required. Match that error on a substring; the typo is in the live service
response.

**Validation diverges by backend and must be enforced client-side:**

- Graph `mailNickname`: max 64, ASCII only, and `@ ( ) \ [ ] " ; : < > ,` and space are forbidden. Also strip
  periods (the Update doc forbids them even though Create does not). Required even for a mail-disabled security group.
- Exchange `Alias`: max 64; `&` breaks Entra Connect sync; periods must be surrounded by valid characters. If omitted,
  Exchange generates one from the name with spaces stripped and unsupported characters turned into `?`. Derive and
  show it, as `NewGroupViewModel.DeriveSam` does, rather than letting the server invent it.

**Create-time-only settings that must be on the dialog** (they can never be set afterwards):

- `RequireSenderAuthenticationEnabled` defaults to **`$true`**, so a new DL silently rejects all external mail.
  Surface it as an "Allow mail from external senders" checkbox, default off, and pass `$false` explicitly when ticked.
- `-HiddenGroupMembershipEnabled` (Exchange) and `visibility: "HiddenMembership"` (Graph M365) are irreversible.
- Graph `resourceBehaviorOptions` (welcome-email suppression, hide from Outlook) is POST-only.
- Security-type groups reject `MemberJoinRestriction Open` / `MemberDepartRestriction Open`; force Closed.

**Owners.** An **admin** creating a security group with no owners produces an **ownerless group** (Graph auto-assigns
the creator only for M365 groups, or for non-admin callers). Default the owner field to the signed-in admin. Separately,
a non-admin who lists their own id in `owners@odata.bind` gets 400 "Request contains a property with duplicate values",
a documented known issue with no workaround, so strip the caller's own id from that array.

**Initial members.** Graph caps `owners` + `members` combined at **20** on create, and 20 per `PATCH members@odata.bind`.
Exchange `-Members` caps at 10,000. Add the first 20 on the create call and the remainder in follow-up batches,
reporting partial failures the way `GroupCreateResult.Warnings` already does for on-prem groups.

**Entry point.** All three natural surfaces are currently blocked for cloud nodes: the tree context menu is cancelled
unless `IsContainerNode` (MainWindow.xaml.cs:76-80), File ▸ New Group is hidden in cloud view (MainWindow.xaml:13),
and the cloud list has no context menu at all. **Decision: relax the context-menu gate** to allow a cloud-specific
menu on the Entra ▸ Groups and Exchange ▸ Distribution Groups nodes ("New cloud group here…"), and add a matching
File ▸ New Cloud Group item enabled in cloud view.

**Post-create refresh, across the latency boundary:**

- After an **Exchange** create, reload only the Exchange list. `Get-DistributionGroup` is immediately consistent;
  Graph is not, and returns 400 `Request_BadRequest` "The source resource object ... don't exist" during the
  replication window.
- After a **Graph** create, reload only the Entra list, and never chain an Exchange cmdlet onto it. A new M365 group's
  mailbox takes roughly 20 to 30 minutes to provision (Microsoft's guidance: typically under 30 minutes, up to 24 hours).
- Retry the first member add with backoff; the same replication window applies.

**Tenant policy can block this invisibly and only at write time.** `Group.Unified`'s `EnableGroupCreation` governs
M365 groups; `authorizationPolicy.defaultUserRolePermissions.allowedToCreateSecurityGroups` governs security groups;
naming policy rewrites or rejects names, **and Global/User Administrators are exempt from naming policy**, so it will
test clean for an admin and fail for everyone else. Surface the raw policy error rather than pretending to explain it.

---

## Commit 3 · Distribution group members

`AddDistributionGroupMemberAsync` and `RemoveDistributionGroupMemberAsync` already exist and are already idempotent.
Missing pieces:

- **List members.** New op wrapping `Get-DistributionGroupMember -ResultSize Unlimited` (default page is 1,000 and
  truncates silently). Does not work on dynamic distribution groups; detect and say so.
- **A multi-select picker.** `MailboxRecipientPickerViewModel.Commit()` is **single-select with no basket**, and
  `CloudMemberPickerViewModel` returns Entra object ids, which Exchange cannot consume without a lookup per member on
  an already-serialised channel. **Decision: give the mailbox recipient picker a basket**, matching the on-prem
  `ObjectPickerWindow`. This is the real work item in this commit, not a detail.
- **Per-member results.** `Add-DistributionGroupMember` takes one member per call and the channel serialises
  everything behind a single semaphore, so N members is N round trips. Report per-member success/failure like
  `BulkResult` rather than failing the batch.
- **Block synced groups.** In a hybrid org this is the *default* case: every write against an `IsDirSynced` group
  fails with "the object is being synchronized from your on-premises organization". Read `IsDirSynced` on load,
  disable the write controls with that sentence, **and** add a third mapping to `ExchangeErrors.Humanize`
  (Services/ExchangeErrors.cs:19-33, currently only not-found and access-denied) as a backstop.

**Related correctness debt, fixed here.** Once the app advertises DL management, operators will try it from the Entra
side, where it is currently broken: `CloudGroupPickerViewModel` filters only `!IsSynced` and so offers DLs, and
`CloudObjectDetailViewModel`'s add/remove member paths route everything through Graph, which returns **403** for
distribution and mail-enabled security groups. Either route those to Exchange or block them with an explanation.

---

## Commit 4 · Mailbox information (view-only)

Rendered as `CloudPropertySection`s with every row `CloudPropertyEditability.SystemReadOnly`, reusing the existing
cloud detail pane. Sections: Identity, Type and visibility, Addresses, Delivery and forwarding, Quotas, Hold and
retention, Archive, Protocols.

Two cheap directory-backed calls on open: `Get-EXOMailbox -Identity <id> -PropertySets Minimum,AddressList,Delivery,
Hold,Policy,Quota,Archive,StatisticsSeed -Properties WhenMailboxCreated,IsDirSynced` and `Get-EXOCasMailbox -Identity <id>`
(all seven protocol flags are in its Minimum set). Never `-PropertySets All`, which Microsoft documents as slow and
less reliable.

**The existing `get-mailbox` op must not be reused.** It runs with `-ErrorAction SilentlyContinue`
(ExchangeService.cs:545), so access-denied and not-found both return `data = null` and the UI says "no mailbox found",
which is actively misleading for a permissions problem. The new op uses `-ErrorAction Stop`.

Graph can replace none of this. `mailboxSettings/userPurpose` is the only Graph surface for mailbox type and is
coarser than `RecipientTypeDetails`; forwarding, holds, quotas, archive state, `WhenMailboxCreated` and every protocol
flag have no Graph equivalent at all.

---

## Commit 5 · Exchange properties surface (view) — ROUGH

Commits 1-4 leave both Exchange lists without a properties view: selecting a row blanks the detail pane and
double-click is refused, because `CloudObjectDetailViewModel` reads exclusively through Microsoft Graph, which
cannot describe a mailbox and returns 403 for a distribution list. **That refactor is the substance of this
commit, not a detail** — commit 4's mailbox view needs it too, so whichever lands first pays for it.

- **Give the detail view model a source.** Today `SetTarget` branches on `CloudObjectKind` and every path ends in
  a Graph call. It needs an Exchange-backed path that produces the same `CloudPropertySection` rows, so the pane,
  the properties window and the CSV of a section all keep working unchanged.
- **Distribution group properties.** New op wrapping `Get-DistributionGroup -Identity` (validate the identity
  first — a non-existent one returns EVERY group). Sections: Identity, Mail and addresses, Delivery and
  moderation, Membership rules, On-premises.
- **Mark what can never change.** `HiddenGroupMembershipEnabled` is create-time only. Showing it as an ordinary
  read-only row invites someone to look for the edit control; it needs the same "why" treatment
  `CloudPropertyEditability.SystemReadOnly` already gives on-prem-mastered fields.
- **Mailbox properties** are already specced in commit 4 and drop into the same surface.

## Commit 6 · Exchange manipulations — ROUGH

- **Distribution group edits** via `Set-DistributionGroup`: display name, alias, description, owners,
  `RequireSenderAuthenticationEnabled`, `HiddenFromAddressListsEnabled`, join/depart restriction, moderation.
  Same `-BypassSecurityGroupManagerCheck` requirement as the member operations, for the same reason: the owner is
  usually a role group, so no individual passes Exchange's "manager of the group" check.
- **Mailbox actions from the Exchange section**: convert Regular ↔ Shared, set/clear forwarding, manage
  delegates. `IExchangeService` already implements all of these — this is wiring and confirmation UI, not new
  service work.
- **Deletes**, if wanted (see D6). A distribution group is **permanently deleted immediately** — there is no
  30-day recycle bin, unlike Microsoft 365 and security groups. That is a harder guarantee to lose than the
  on-prem group delete, which at least writes a record first.

### Locked decisions

**D5 — the editable surface.** Chosen setting by setting from the full `Set-DistributionGroup` surface, not
by category. `Set-DistributionGroup` has no `-Type`, so converting a distribution list to mail-enabled
security (or back) is impossible and was never on the table.

| Editable | Deliberately NOT editable |
|---|---|
| Alias | Rename (display name) — a naming policy silently rewrites it, and admins are exempt so it misbehaves differently for others |
| Description, MailTip | Email addresses — changing the primary redirects mail and breaks the old address |
| Hidden from address lists | Hide membership — one-way, and excluded rather than guarded |
| Mark as a room list | Custom attributes 1–15 — can silently change what a dynamic rule matches |
| Owners (ManagedBy) | |
| Join restriction, depart restriction | |
| Send on behalf | |
| External senders | |
| Accept / reject sender lists | |
| Moderation (enable, moderators, notifications, bypass) | |
| Size limits, BCC blocked, delivery reports | |

**D6 — no delete.** Deleting a distribution group stays out of this app. It is the one operation with no
recycle bin and no recovery, and the Exchange admin center already does it.

**D7 — share one control.** `ExchangeTabViewModel` is hosted in the mailbox detail pane as well as the ExOL
tab rather than reimplemented. It already takes only `IExchangeService`, `IGraphService` and `IDialogService`
plus `SetTarget(type, identity)`; the only AD coupling is one `AdObjectType.User` gate to relax. One
implementation means the two surfaces cannot drift, and the ExOL tab stays the path for a hybrid user who has
no row in the Exchange list.

### What D5 implies for the build

Four of the chosen settings are multi-value **recipient** properties — owners, send on behalf, accept/reject
lists, and moderators. They need the picker basket, which is why commit 3 built it. The rest are scalars,
toggles and enums.

That splits commit 6 naturally in two, and the split is worth keeping: the scalar half is low-risk and lands
quickly, while the recipient half carries the pickers, the multi-value editing, and the identity-resolution
traps this project keeps re-learning (a name that stringifies to a canonical name or a bare GUID).

Sequencing note: the valid values for join and depart restriction depend on the group type — `Open` is
invalid on a mail-enabled security group, and `ApprovalRequired` is accepted there but never routes requests
to an owner. The editor has to offer only what the selected group can actually take.

### Sharp edges already established

- Every write against an `IsDirSynced` object is rejected by Exchange. In a hybrid org that is the default case,
  not an edge case — the same gate commit 3 builds applies to everything here.
- A group naming policy can rewrite a display name on edit as well as on create.
- `Get-*` with a non-existent `-Identity` returns every object rather than erroring, so any single-object read on
  this surface has to validate its identity first.

---

## Cross-cutting gotchas

**G1 · The channel has no paging and one fixed timeout.** `OpTimeout` is a hard 90 s (ExchangeService.cs:30) with no
per-op override, and a timeout **kills the host process** (:349-353), destroying the session for the Exchange tab, the
licence guardrail, and any in-flight scenario. Exchange PowerShell has no skip/continuation token: you cap with
`-ResultSize` or you get everything. **Decision: thread a per-op timeout through `RunOpAsync` → `SendAndReadLockedAsync`
(180 s for list ops), and cap list loads** (default `-ResultSize 200`) with a status line saying the list is capped and
to search to narrow. Searching uses `-Anr` for groups and OPATH `-Filter` for mailboxes, both server-side.

**G2 · `Get-DistributionGroup -Identity $null` returns every group** rather than erroring. Validate identity before
every single-object call or a "get one group" path silently becomes "get the whole tenant".

**G3 · `HostScript` is an unchecked 166-line PowerShell string literal** (ExchangeService.cs:493-659) with no syntax
checking, no tests, and no tenant in the dev environment. This work roughly doubles it. Every op must capture or
`6>$null` all cmdlet output or the frame corrupts; every `-Filter` built from user input must repeat the
`.Replace("'","''")` escaping at :565.

**G4 · Everything comes back as a string.** EXO V3 returns booleans as `"True"`/`"False"`, and `-not "False"` is
`$false` in PowerShell. The trap is already documented at :577; it applies to every new flag. Cast scalars with
`[string]`, compare booleans with `([string]$x -eq 'True')`, and project collections element by element.

**G5 · Awkward Exchange types.** `ByteQuantifiedSize` (quotas, sizes) serialises to a nested blob; cast to `[string]`
for "49.5 GB (53,150,220,288 bytes)" and do not call `.Value.ToBytes()` (in REST mode the object is a
`Deserialized.*` property bag with no methods and the call throws). `ADObjectId` (`ForwardingAddress`, `ManagedBy`,
`RetentionPolicy`) stringifies to a canonical name, **not** an SMTP address; use the `-Include*WithDisplayNames`
switches or resolve separately. `EmailAddresses` elements carry `SMTP:` (primary) / `smtp:` (secondary) prefixes.
Pin dates with `.ToUniversalTime().ToString('o')`.

**G6 · `Get-MailboxStatistics` is the expensive one.** It hits the mailbox store, takes one mailbox per call, and
piping a list into it is the canonical way to exhaust the throttling budget and trigger "Micro Delay Applied".
Behind an explicit "Load size and usage" button on a single selected mailbox, never fanned out across a list.
`LastLogonTime` is in the `All` property set only.

**G7 · Archive detection.** `ArchiveStatus` reads `None` even for an active archive after a re-license or migration.
For a pure-cloud tenant use `ArchiveGuid -ne [guid]::Empty`.

**G8 · Identity duality.** Entra rows key on Graph object id; Exchange keys on primary SMTP.
`Get-DistributionGroup` returns `ExternalDirectoryObjectId`, which is the bridge. Carry **both** on the Exchange row
model, otherwise double-clicking an Exchange DL can never open its Entra detail.

**G10 · Display-name switches do not cover every recipient field.** `Get-DistributionGroup` has
`-Include*WithDisplayNames` switches for `ManagedBy`, `ModeratedBy`, `GrantSendOnBehalfTo`,
`AcceptMessagesOnlyFromSendersOrMembers` and `BypassModerationFromSendersOrMembers` (all in parameter set
**(All)**, so they bind with `-Identity`). Without the switch the field comes back as **bare GUIDs** and the
parallel `*WithDisplayNames` property is empty. There is **no** switch for
`RejectMessagesFromSendersOrMembers` — that one always needs local resolution. Always keep the raw property
as a fallback: an unbindable switch or an empty variant must degrade to today's answer, not to nothing.

**G11 · Two D5 choices do not survive contact with the cmdlet.**
• `MaxSendSize` / `MaxReceiveSize` on `Set-DistributionGroup` are documented **"available only in
on-premises Exchange"**. Exchange Online takes the message-size limit from the organization, so these can be
read but never written. Both rows stay read-only.
• `-RoomList` is a **SwitchParameter with no documented off value**, and the repair Microsoft documents for
a room list that must become an ordinary group again is the `msExchRecipientTypeDetails` AD attribute — which
a cloud-only group does not have. So the setting is offered in ONE direction: editable while the group is not
a room list, closed once it is. A Yes-to-No choice would report success and change nothing.
• Related, and the reason the pane shows an "Email address policy" row: **changing the Alias rewrites the
primary SMTP address** whenever `EmailAddressPolicyEnabled` is True, which is the usual case for a cloud
group. That is the same consequence D5 excluded address editing to avoid, arrived at by another route.

**G12 · `ReportToManagerEnabled` and `ReportToOriginatorEnabled` are one decision, not two.** Microsoft
documents setting both, or neither, as leaving messages to the group with no return path, which some mail
servers reject. They are presented as one choice and always written as a pair; a group already in an invalid
combination is shown as it really is rather than silently normalised.

**G13 · A recipient list needs THREE outcomes per entry, not two.** Resolving one costs a `Get-Recipient`,
so it happens on demand rather than on open. Each entry comes back resolved, *not found*, or *not looked up*
(the per-read budget ran out) — and those last two must not share a representation. An empty address alone
cannot carry the difference, and reporting a long list as one full of deleted accounts sends an operator
looking for a problem in their directory that is really this app's limit. A mail contact or mail user can
also have **no primary SMTP**: fall back to `ExternalEmailAddress` then `Name`, which
`Set-DistributionGroup` accepts — `list-dl-members` already relied on this.

**G14 · The detail pane's tab strip selects a collapsed tab.** The strip is a `CompositeCollection` of the
section tabs plus fixed `TabItem`s that are `Collapsed` for whatever they do not apply to. Left alone the
`TabControl` selects the first REAL `TabItem`, and at first measure the sections have not arrived — so it
lands on a collapsed one and never moves, rendering the pane blank. Refilling the sections does not help.
Every rebuild has to name the tab it means. `app/build/test-tabselection.ps1` reproduces it off-screen.

**G9 · The `"Distribution"` / `"Mail-enabled security"` string test is duplicated in five places**
(`HybridGroupPickerViewModel.cs:71`, `BulkUserCsvImporter.cs:216`, `ScenarioRunner.cs:302`, `CopyUserViewModel.cs:180`,
`CopyToTemplateViewModel.cs:152`). Adding a sixth and seventh consumer is the moment to centralise it next to
`ClassifyGroupKind` (GraphService.cs:763-773).

---

## Risks to validate during implementation

1. **List load time and the 90 s ceiling** (G1). Validate against the live tenant before building the UI around it.
2. **`ListGroupsAsync` combines `$orderby=displayName` with `$search` and `$count`** under `ConsistencyLevel: eventual`
   (GraphService.cs:232-237). Graph restricts combining `$orderby` with `$search` on directory objects. If that path
   has only ever run unsearched, a new search box modelled on it may 400 on first use. Test rather than copy.
3. **Whether the tenant's security-group creation policy actually blocks the API path.** Microsoft's own
   troubleshooting doc warns the portal-facing toggle historically did not block PowerShell/API creation.
4. **`-BypassSecurityGroupManagerCheck` permissions.** It is hardcoded on today's member ops; the create path inherits
   the same requirement, and Recipient Management alone does not carry it.

---

## Sequencing

1. ~~**Ship 2.2.0** (D1): bump, merge `release/2.2.0`, tag, release, README + Wiki.~~ **Done.**
2. ~~Branch `release/2.3.0`.~~ **Done.**
3. ~~Commit 1: nav section, read-only lists, per-op timeout, drag-drop and `Configure` fixes.~~ **Done**
   (`7b1fdd0`), plus `80a311c` and `9eefbbf` for distribution-group owner resolution and the findings from
   the adversarial pass over both. Awaiting a live retest of the *Managed by* column.
4. ~~Commit 2: create cloud groups (both backends).~~ **Done** (`d62972b`), plus `476e7d0` for owner defaulting,
   the Microsoft 365 welcome-email/Outlook-visibility controls, and the findings from the adversarial pass.
5. ~~Commit 3: distribution group members, picker basket, synced-group blocking, Entra-side routing fix.~~ **Done** (`c6d7172`), live-tested.
6. ~~Commit 4: mailbox information view.~~ **Done** (`97eee52`), plus `c4bcfeb` for the usage fix, flat section
   tabs and the header button. Live-tested. The refactor this plan feared was never needed.
7. ~~Commit 5: distribution-group properties view.~~ **Done** (`68b42e9`). Cheaper than planned, as expected:
   one host op plus a section projection, no detail-pane refactor. `CloudObjectRow` gained a `Source` so a
   distribution list and an Entra security group stop being indistinguishable, which is what keeps the
   Graph-backed Members tab off a DL. Review before landing caught a double-stripped primary address on the
   mailbox pane, five recipient fields resolved by hand that Exchange will resolve server-side (see G10), and
   a stale lookup-degradation counter. **Commit 6a is next.**
8. ~~Commit 6a: the scalar group edits.~~ **Done** (`b89b046`), NOT yet live-tested. Delivered alias,
   description, MailTip, hidden from address lists, room list (one way), join/depart restriction, external
   senders, BCC, delivery reports, auto-replies, moderation on/off and moderation notifications.
   **Two D5 items could not be delivered as written — see G11. Commit 6b is next.**
9. ~~Commit 6b: the recipient-valued edits.~~ **Done** (`e63996b`), NOT yet live-tested. All six lists edit
   through the seeded recipient picker. The read stays on the display-name switches; addresses are resolved
   on demand only when an editor opens, because a display name cannot be written back (see G13). Also added
   the "Members…" button under the group name, and fixed the blank properties pane (G14).
10. Commit 7: mailbox actions in the Exchange section, by hosting the existing ExchangeTabViewModel (D7).
10b. ~~Commit 7: mailbox actions in the Exchange section, by hosting the existing ExchangeTabViewModel (D7).~~
    **Done** (`cb453af`), live-tested. The markup was extracted into a shared ExchangeActionsView so both
    panes show one control. It also uncovered G14 — fixed tabs had been rendering blank since c4bcfeb.
11. ~~README: the raised RBAC bar, the new nav section, the group-type routing table.~~ **Done.**
    Wiki still to do, then ship 2.3.0.

D5-D7 are locked (see above), and the editable surface was chosen setting by setting against the real
`Set-DistributionGroup` parameter list. What commits 5-7 still lack is the file-level detail commits 1-4
had: which host ops, which view models, which validation. Work that out per commit as it starts.

Each commit builds clean (0 warnings, 0 errors), gets an adversarial review pass before landing, and is verified in
the deployed binary before being handed over for testing.
