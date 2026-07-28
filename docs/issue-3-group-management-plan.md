# Issue #3 — Create, delete, and modify groups

Status: **planned, not started.** Target branch `release/2.2.0` (new features → minor bump).

[GitHub issue #3](https://github.com/Armadillon44/UnifiedDirectoryManager/issues/3):

> Ability to create, delete, and modify groups. Right-click → New Group in/on OU, or File → New Group.
> Right-click → Delete group with confirmation. Option to log all information and members of the group
> being deleted. Modify group attributes as we already can with users.

Plus a follow-up ask: **deleting multiple groups at once.**

---

## Key finding: "modify" is already ~85% done

Verified by direct code inspection (v2.1.1 sources):

| Already works for groups | Where |
|---|---|
| **General** tab: `cn` (read-only), `sAMAccountName`, `displayName`, `description`, `mail`, `info` (Notes), `groupType` | `EditPaneViewModel.FieldLayout()` — ViewModels/EditPaneViewModel.cs:991-996 |
| **Members** tab — list, *Add members…* (object picker), *Remove selected* | Views/Controls/EditPaneView.xaml:329; `IDirectoryService.AddMembersAsync` / `RemoveMembersAsync` |
| **Member Of** tab, fully editable **Attribute Editor** | Views/Controls/EditPaneView.xaml |
| **Protect from accidental deletion** checkbox (any object type) | `EditPaneViewModel.IsProtectedFromDeletion` |
| **Delete** (single *and* multi-select) via list right-click → *Delete…* | Views/Controls/ObjectListView.xaml:81 → `MainViewModel.DeleteSelectedAsync` (:916-952) |
| Scope/category **classification for display** ("Security · Global") | Services/GroupTypeClassifier.cs |

So the genuine gaps are: **create (nothing exists)**, **delete-with-a-record (no logging; single delete is a plain Yes/No)**, and **two modify gaps** (below).

---

## Locked decisions

| Decision | Choice |
|---|---|
| Delete confirmation strength | Single confirm dialog + "save a record" checkbox (not the OU-style random-string guard). N>1 still requires typing the count — the existing convention. |
| Group scope/category editing | **In scope** — include it, plus Managed By. |
| Record output | A `.txt` of attributes **and** a companion members `.csv`. |
| Batch record layout | **Flat** — one `.txt` + `.csv` pair per group, straight into the operation-log folder. No batch subfolders. |
| Menu surface | **One group-aware `Delete…`** (the existing item), not a second menu entry. |
| Initial members at create | **Yes** — include a members picker in the New Group dialog. |
| Record write fails | That group is **skipped, not deleted** (a record was requested; failing to produce one shouldn't lose the group). |

---

## Commit 1 · Modify gaps

1. **`GroupTypeClassifier`** — add `GroupScope` / `GroupCategory` enums, `Build(scope, category) → bitmask`,
   `Parse(raw) → (scope, category)`. Flag constants already exist as `private const` (GroupTypeClassifier.cs:11-14):
   `Global 0x2`, `DomainLocal 0x4`, `Universal 0x8`, `SecurityEnabled 0x80000000`.
2. **Scope + Type combos** on the group's General tab (gated `IsGroup`), seeded from the loaded `groupType`.
   On save emit `PendingChange.Set("groupType", bitmask)`.
   - *No new plumbing needed:* `AttributeCatalog` marks `groupType` `IsInteger`, so `DirectoryService.IsLdapInteger`
     (:888) is already true and the write routes through `ModifyViaLdap` — the correct path for the bitmask.
   - `groupType` is currently `IsReadOnly: true` (AttributeCatalog.cs:103). Keep the **raw** attribute read-only in
     the Attribute Editor and drive it solely from the combos, so there's one authoritative editor.
   - **AD transition rules:** Global↔Domain-local is not a legal direct change (must pass through Universal);
     Global→Universal is refused if the group is a member of another Global group; Domain-local→Universal is
     refused if it contains another Domain-local group. Filter the obviously-illegal direct transitions in the UI
     and surface AD's refusal verbatim rather than reimplementing every rule.
3. **Managed By** picker — `managedBy` is catalogued (`IsDnValued`, AttributeCatalog.cs:102) but absent from the
   group field layout, and DN-valued fields are skipped by the generic save path
   (`if (field.IsReadOnly || field.IsDnValued || field.IsMultiValued …) continue`). Needs the same dedicated
   pending-DN treatment `manager` gets for users (`_pendingManagerDn`, EditPaneViewModel.cs:86).
4. Display `groupType` as "Security · Global" via `GroupTypeClassifier.Describe` instead of a raw number.

## Commit 2 · Create groups

1. **`IDirectoryService.CreateGroupAsync(parentDn, name, samAccountName, scope, category, description,
   managedByDn?, protectFromDeletion, initialMemberDns) → (string Dn, string? ProtectionError)`** — mirror the
   hardened `CreateOrganizationalUnitAsync` (DirectoryService.cs:621):
   - `parent.Children.Add($"CN={EscapeRdn(name)}", "group")`, set `sAMAccountName` / `description` / `groupType`,
     `CommitChanges()`, read back `distinguishedName` (escaped fallback).
   - ⚠ **`groupType` must be set as an `int` on the `DirectoryEntry` BEFORE the first `CommitChanges()`** — the way
     `userAccountControl` is set at DirectoryService.cs:601. Do **not** route it through the generic
     integer-attribute path: `AttributeCatalog` marks `groupType` `IsInteger`, so a generic loop would defer it to a
     post-commit `ModifyViaLdap` and the group would be created with AD's default type first.
   - ⚠ **The security bit `0x80000000` overflows `Int32`.** `groupType`'s LDAP syntax is a signed 32-bit
     enumeration, so a security group must be written as `unchecked((int)0x80000002)` (= -2147483646), never as
     `uint`/`long`. (`GroupTypeClassifier`'s own comment documents that AD returns it negative.)
   - Protection and initial-member adds are **non-fatal follow-ups** — neither may turn a created group into a
     reported failure (the exact bug the OU review caught).
2. **`NewGroupViewModel` + `NewGroupWindow`** mirroring `NewOuViewModel`/`NewOuWindow`, including the 2.1.1
   hardening: `OnClosing` guard while busy, `Created`-event close pattern, `IsBusy` input gating.
   Fields: name → auto-derived `sAMAccountName` (editable), scope, type, description, protect (default on),
   **Add members… picker**.
3. **`IDialogService.ShowNewGroup(parentDn)`** + `DialogService` implementation.
4. **Entry points:**
   - Tree right-click **"New Group here…"** — gate on `TreeNodeViewModel.IsContainerNode`, **not** `CanCreateChildOu`:
     groups are legal in containers such as `CN=Users`, not just OUs.
   - **File → New Group…** — a `NewGroupCommand` using `SelectedNode?.DistinguishedName` as the default parent,
     exactly how `NewUserCommand` works (MainWindow.xaml:11).
5. Duplicate `sAMAccountName` pre-check via the existing `FindExistingSamAccountNamesAsync`; refresh with
   `List.ReloadAsync()` (groups land in the object list, not the tree).

## Commit 3 · Delete group(s) + records

Extends the **existing** `MainViewModel.DeleteSelectedAsync` (:916-952) rather than adding a parallel command —
that loop already handles multi-select, aggregates per-object failures without aborting, and reloads the list.

1. Add **`SelectionHasGroups`** to `UpdateSelectionState` (:855-860).
2. **Group-aware confirmation:** when the selection includes ≥1 group, the dialog additionally shows each group's
   **member count** and scope/type, and offers the **"Save a record of each group (attributes + members) before
   deleting"** checkbox (default on). Users/computers behave exactly as today. N>1 keeps the existing
   type-the-count safeguard. `ConfirmWindow` has no checkbox, so this needs a small new dialog.
3. **Protection pre-check** — protected groups are listed and **skipped**, with the option to proceed with the rest
   (a batch shouldn't be an all-or-nothing refusal). ✅ **Free:** `AdObjectRow.IsProtected` is already populated on
   every list row (`SearchAsync` sets `SecurityMasks=Dacl` and loads `ntSecurityDescriptor`) — no extra round-trip
   and no need for `GetDeletionProtectionAsync` here.
4. **Record writer** (per group, written *before* that group's delete), flat in
   `OperationLog.ResolveDirectory(settings)` using `OperationLog.SafeFileNamePart(name)`:
   - `group-<name>-<yyyyMMdd-HHmmss>.txt` — every populated attribute, **plus** the friendly
     `GroupTypeClassifier.Describe(groupType)` and `canonicalName` (neither is usable from `LoadObjectAsync`:
     groupType arrives as a raw signed int, canonicalName isn't returned at all — use `GetBasicInfoAsync`).
   - `group-<name>-<yyyyMMdd-HHmmss>-members.csv` — members as **display name + full DN** (the DN is what makes the
     record re-addable, matching the `RemoveAllGroups` scenario precedent). Use the existing **`CsvText.Field/Row`**
     helper, which already neutralizes formula injection.
   - If the record can't be written → **skip that group's delete** and report it. Note this inverts
     `WriteOperationLog`'s contract (it deliberately *swallows* write failures because the scenario already ran);
     a pre-delete record must be written and verified first, so don't reuse that method as-is.
5. Summary reports **deleted / skipped / failed** and where records were saved.

> ### ⚠ Blocker to solve first: `member` range retrieval
> `LoadObjectAsync` requests `PropertiesToLoad("*")` and **no range retrieval is implemented anywhere in
> `DirectoryService`**. AD caps a single attribute read at **1500 values** and returns the property renamed
> `member;range=0-1499`. Consequences:
> - **Existing bug:** the Members tab renders **empty** for any group with >1500 members (`map.TryGetValue("member", …)`
>   misses), and the Attribute Editor shows a bogus editable `Member;range=0-1499` row.
> - **For this feature:** a member dump built on that path would **silently record an empty or partial member list for
>   exactly the large groups where the record matters most** — the worst possible failure for a
>   delete-safety feature.
>
> **So Commit 3 must add range retrieval** (loop `member;range=0-1499`, `1500-2999`, … until `*`) for the record —
> ideally as a reusable `GetGroupMembersAsync(dn)` on `IDirectoryService`, which also fixes the Members tab.

---

## Cross-cutting gotchas (from a research pass over the three subsystems)

**Wiring**
- Tree context-menu items use **`Click` handlers in `MainWindow.xaml.cs`, not Commands**, and resolve their target
  via `NodeFromMenu` (which walks up nested MenuItems). `OnNodeContextMenuOpening` sets `e.Handled = true` for any
  node that isn't `IsContainerNode`, suppressing the whole menu — so gate "New Group here…" on `IsContainerNode`.
- The **list** context menu reaches `MainViewModel` only through the `PlacementTarget.Tag` indirection. Unlike the
  tree, **right-click in the list moves the selection to the clicked row** (ObjectListView.xaml.cs:126-131) — so the
  group delete needs none of the selection-repair complexity `DeleteOuAsync` required.
- A new **File-menu** entry needs `Visibility="{Binding IsAdView, …}"` like *New User* — the app runs cloud-only when
  on-prem AD is unbound, and `Required` throws "Not connected to a domain controller."
- `UpdateSelectionState` is invoked from exactly one place (the `List.SelectionChanged` subscription in the ctor).

**Pre-existing warts to avoid tripping over (or fix opportunistically)**
- **Save dedup collision:** `byLdap` is keyed on lDAPDisplayName and the Attribute-Editor loop runs *after* the
  fixed-field loop, so the Attribute Editor value **silently overwrites** the General-tab value for the same
  attribute. For groups that's `description` / `displayName` / `mail` / `info` / `sAMAccountName` — both places.
- **Person-only rows on groups:** `IsRelevantCategory` maps Group → the *whole* General category, so the Attribute
  Editor offers `givenName` / `sn` / `middleName` / `initials` on a group; saving one is rejected by the DC with a
  schema violation. Nothing filters by objectClass.
- **`sAMAccountName` edit doesn't rename `cn`** (ADUC renames both), and there's no rename op anywhere, so the RDN
  and logon name go permanently out of sync after such an edit. Worth calling out in the UI or deferring a rename op.
- **Membership writes bypass the pending-change model** (`AddMembersAsync`/`RemoveMembersAsync` write immediately
  then reload), silently discarding unsaved General/Attribute-Editor edits. Only `MoveAsync` checks
  `HasUnsavedChanges()`. If new group fields are added, keep `HasUnsavedChanges` in sync with `SaveAsync`.
- **Attribute-Editor DN fields have no validation** — `managedBy` typed free-text produces a raw LDAP error. Once it
  gets a picker, exclude it from the Attribute Editor the way `manager` already is (EditPaneViewModel.cs:952).
- **Group delete side effects nothing currently explains:** it strips the group from every member's `memberOf`, and
  it **fails outright if the group is any user's `primaryGroupID` target**. Worth surfacing in the confirm dialog or
  at least translating the resulting error.
- **Encoding is inconsistent** across writers (`WriteOperationLog` = no BOM; `ExportCsv` = UTF8 **with** BOM for
  Excel). Pick deliberately: BOM for the members `.csv`.
- There is **no shared timestamped-filename helper** (only an inline expression at MainViewModel.cs:711) and **no
  test project** in the repo — verification is build + manual run.

## Risks to validate during implementation

- **`groupType` at create time** — confirm ADSI accepts it in the initial property set; fall back to a post-create
  `ModifyViaLdap` if it doesn't (this is how `CreateUserAsync` defers integer attributes like `countryCode`).
- **Scope transitions** — AD enforces rules the UI can't fully predict; surface errors rather than guessing.
- **No domain controller is available in the dev environment**, so every commit ships compile- + adversarial-review-
  verified only. Live smoke tests required:
  1. Create a group in an OU *and* in `CN=Users`; verify scope/type/description/`sAMAccountName`, protection, and
     initial members all land.
  2. Change an existing group's scope and category; confirm a legal change succeeds and an illegal one surfaces a
     readable AD error.
  3. Set Managed By; confirm it persists.
  4. Delete one group with the record checkbox on; verify both files and their contents.
  5. Multi-select several groups (including one protected) and delete; verify skip-and-report, per-group records,
     and the deleted/skipped/failed summary.
  6. **A group with >1500 members** — verify the Members tab and the deletion record both list *all* members
     (this is the range-retrieval case that silently returns nothing today).

## Sequencing

Commit 1 (small — closes the literal "modify" ask) → Commit 2 (headline) → Commit 3 (destructive, most care).
Each independently shippable; adversarially review each before committing, as with the v2.1.1 OU work.
