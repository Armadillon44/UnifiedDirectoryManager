# Where the app's functions live — a placement audit

Status: **P1 done. 8 findings left (P2–P9), plus T1 (customisable toolbar), whose three open questions
are now decided — see [T1 decisions](#t1-decisions-settled).**

Measured against `master` at **`8a2dc29`**. Everything below was derived from the XAML and
`MainViewModel.cs` rather than from memory, and the method is in
[How this was measured](#how-this-was-measured) so it can be re-run after changes.

The complaint that prompted it, in the maintainer's words:

> There are some functions that are only accessible via right-click and some that are only in the top menu
> bar, then others that are in both. Some are even in the action bar below the menu bar. I don't want users
> of the app having to memorize where to find the app's functions without it being intuitive.

---

## Status

| # | Finding | State |
|---|---|---|
| **P1** | The cloud (Entra) object list has no context menu at all | **Done** — `master`, see below |
| **P2** | Favourites is reachable only by right-clicking a tree node | **Not started** |
| **P3** | OU management is reachable only by right-clicking a tree node | **Not started** |
| **P4** | Seven object actions are right-click-only | **Not started** |
| **P5** | Object actions that are menu-only never appear on right-click | **Not started** |
| **P6** | The same command carries different labels in different places | **Not started** |
| **P7** | Refresh and the log commands are duplicated *within* the menu bar | **Not started** |
| **P8** | The menu bar is organised by nothing in particular | **Not started** |
| **P9** | There are no keyboard shortcuts anywhere in the app | **Not started** |
| **T1** | *(feature)* Let the operator choose what is on the toolbar | **Designed, not started** |

---

## The diagnosis in one paragraph

Individual placements are mostly defensible. What is missing is a RULE: the same *kind* of action lands in
a different place depending on which kind it is, so there is nothing for an operator to learn and
generalise from. Creating an object is four different gestures depending on the object. Acting on the
selected row is three. Exporting is three. That is why it has to be memorised — not because any one item is
hidden, but because knowing where one thing lives predicts nothing about where the next one lives.

---

## The rule this document recommends

Three lines. Every finding below is an application of one of them.

1. **The menu bar is COMPLETE.** Every command appears there. It is the index. A menu bar that is a subset
   is worse than no menu bar, because it teaches "if it is not here, it does not exist" — and that lesson
   is currently false for seven commands, for all of Favourites, and for all of OU management.

2. **The context menu is CONTEXTUAL and GENEROUS.** Everything that can be done to the selected thing
   appears on right-clicking it, whether or not it is also in the menu bar. Duplication here is a feature.
   Right-click is how Windows administrators explore an unfamiliar tool, and it is the gesture ADUC
   trained them on.

3. **The toolbar is CONVENIENCE and owns NOTHING.** A short, fixed set of frequent, non-destructive
   actions, every one of which exists elsewhere. It happens to obey this today. **T1 makes obeying it
   mandatory** — see that section.

Everything gets at least one guaranteed home (the menu bar) and object actions get a second, predictable
one (right-click). Nothing has to be memorised because the menu bar can always be searched.

---

## What is where today

30 `[RelayCommand]` methods on `MainViewModel`, by surface:

| Surface | Count | Commands |
|---|---|---|
| Menu bar only | 11 | About, Copy Groups to User, Entra Sync, Exit, Manage Scenarios, New Cloud Group, New Group, Settings, Toggle Dock, View Log, View README |
| Right-click only | 7 | Append Group Members, Disable Selected, Enable Selected, Export Group Members, Move Selected to OU, Save Selected as Template, Unlock Selected |
| Menu + toolbar | 8 | Advanced Search, Bulk Create Users, Export CSV, Manage Templates, New User, Open Logs, Refresh (also in View), Open Selected |
| Menu + toolbar + right-click | 2 | Add Selected to Groups, Bulk Edit |
| Nowhere | 0 | — |

Two further surfaces do not use `MainViewModel` commands at all, which is why they are easy to miss:

- **The tree context menu** (`MainWindow.xaml`, click handlers): Create OU here, New Group here, New cloud
  group, Pin to Favourites, Move up, Move down, Unpin, Properties, Delete.
- **The cloud pane** (`CloudObjectListView.xaml` / `CloudDetailView.xaml`, buttons): Search, Export loaded,
  Export all, Enable, Disable, Revoke sessions, Load more — and in the detail pane, Members…, Assign
  license…, Add to groups…, Add members…, Paste a list…, Remove selected, Save changes, Revert.

And the **edit pane** (`EditPaneView.xaml`) carries its own per-object buttons: Save, Reload, Enable,
Disable, Reset Password…, Unlock, Add direct reports…, Add members…, Paste a list…, Add to groups…,
Remove selected, Copy groups to user…, Look up in Entra ID.

### The same kind of action, scattered

| Kind | Where each one lives |
|---|---|
| **Create an object** | New User → menu + toolbar · New Group → menu only · New Cloud Group → menu only · Create OU → tree right-click only |
| **Act on the selection** | Enable / Disable / Unlock → right-click only · Reset Password → edit-pane button only · Delete → menu + right-click · Move to OU → right-click only |
| **Export** | Export List to CSV → menu + toolbar · Export group members → right-click only · Export loaded / Export all (cloud) → pane buttons |

---

## Findings

### P1 — The cloud object list has no context menu at all — **DONE**

`CloudObjectListView.xaml` contains no `ContextMenu`. Right-clicking an Entra user does nothing. The same
actions exist, as buttons above the list: Enable, Disable, Revoke sessions, Export loaded, Export all.

This is the largest inconsistency in the app. One window, two object lists, two unrelated interaction
models. An operator who has learned "right-click a row to act on it" from the AD side finds the gesture
dead on the cloud side, with no indication why.

*Fixed.* The cloud list has a context menu offering Properties…, Enable, Disable and Revoke sign-in
sessions, the last three only in the Users list. The buttons are unchanged.

Two things came out of doing it that were not obvious from the audit:

- **Right-click did not select the row.** WPF does not do this on its own, and the on-prem list has carried
  a `PreviewMouseRightButtonDown` handler for it all along. Without the same handler, a cloud context
  action would have targeted whatever was selected *before* the right-click — acting on the wrong user
  while looking correct.
- **The two surfaces need different targets.** The buttons act on the CHECKED set and say so (the count
  sits beside them; they grey out with nothing ticked). The context menu acts on the right-clicked row,
  falling back to the checked set when there is one — the rule `MainViewModel.SelectedRowsOrSingle` already
  uses on-prem. Two entry points, two targets, but ONE implementation: `RunBulkAsync` takes its row list as
  a parameter, so the six commands are three lines each. Copying the body per entry point is how Copy
  User's token resolver drifted from New User's (F26).

Covered by `test-ui-placement.ps1`, which also carries an invariant worth having regardless of this audit:
**every `{Binding …Command}` in every view resolves to a command a view model actually defines.** A typo
there fails silently — WPF finds no such property, the control greys out, and it is indistinguishable from
a command that is legitimately unavailable. Nothing else in the build catches it.

### P2 — Favourites is reachable only by right-clicking a tree node

Pin to Favourites, Unpin, Move up and Move down exist only in the tree's context menu. Nothing in the menu
bar mentions favourites. An operator who does not happen to right-click a tree node will not discover that
the feature exists.

This is a whole feature, shipped in 2.3.0 and extended since, that is invisible unless you guess the
gesture.

*Fix:* a **View ▸ Favourites** submenu acting on the selected tree node (Pin / Unpin / Move up / Move
down), with the items enabled per the same flags the context menu uses (`CanPin`, `IsFavorite`).

### P3 — OU management is reachable only by right-clicking a tree node

Create OU here, Properties and Delete are tree-context-only, with the same discoverability problem as P2.
Creating an OU is not an obscure operation.

*Fix:* Create OU belongs with the other create commands (see P8's **New** menu). OU Properties and Delete
belong wherever the selected-node actions end up, alongside the Favourites items from P2.

### P4 — Seven object actions are right-click-only

Enable Selected, Disable Selected, Unlock Selected, Move Selected to OU, Save Selected as Template, Export
Group Members, Append Group Members.

Enable/Disable/Unlock are among the most-used actions in a directory tool and appear nowhere in the menu
bar. (Enable/Disable/Unlock *also* exist as edit-pane buttons for a single object, which is a third
location, and a different one again.)

*Fix:* all seven into the menu bar, under whatever the selected-object menu becomes in P8. The two export
commands sit naturally beside Export List to CSV.

### P5 — Object actions that are menu-only never appear on right-click

Copy Groups to User is the clearest case: it is a per-user action, it lives in the **Edit** menu, it also
exists as an edit-pane button — and it is absent from the object list's right-click menu, which is the
first place an operator would look for it.

New Group and New Cloud Group are menu-only, while the tree's right-click offers "New Group here…" and
"New cloud group…" — so the same two operations have menu entries and tree entries that look like
different features.

*Fix:* add Copy Groups to User to the object-list context menu. Reconcile the create-group entries so the
menu-bar and tree wordings describe one feature (see P6).

### P6 — The same command carries different labels in different places

| Command | Menu bar | Toolbar |
|---|---|---|
| `ExportCsvCommand` | "Export List to CSV…" | "Export CSV…" |
| `BulkCreateUsersCommand` | "Bulk Create Users…" | "Bulk Create…" |
| `ManageTemplatesCommand` | "User Creation Templates…" | "Templates…" |

An operator who saw "Templates…" on the toolbar and goes looking for it in the menus finds "User Creation
Templates…" under Edit. Abbreviating for toolbar width is reasonable; using a *different noun* is not.

*Fix:* one label per command, used everywhere. Where the toolbar genuinely needs to be shorter, shorten
the canonical label rather than inventing a second one.

### P7 — Refresh and the log commands are duplicated inside the menu bar

- **Refresh** appears in File, in View, and on the toolbar.
- **View Log File…** and **Open Logs Folder** appear in both File and Help.

Meanwhile the **View** menu has only two items in total (Toggle Pane Dock, Refresh), so it reads as a
leftover rather than a category.

*Fix:* Refresh in View and on the toolbar, not File. Logs in Help only (or File only) — not both.

### P8 — The menu bar is organised by nothing in particular

Current shape, with the odd placements marked:

```
File   New User… · Bulk Create Users… · New Group… · New Cloud Group…
       Open Selected…        <- an action on the selection, not a File operation
       Refresh               <- also in View, also on the toolbar (P7)
       Export List to CSV…
       View Log File… · Open Logs Folder      <- also in Help (P7)
       Settings… · Exit
Edit   Advanced Search…      <- a find operation, not an edit
       Copy User… · Copy Groups to User… · Add Selected to Groups… · Bulk Edit…
       Delete Selected…
       User Creation Templates…               <- managing templates, not an edit of the selection
View   Toggle Pane Dock · Refresh             <- two items
Tools  Entra Connect Delta Sync… · Run Scenario on Selected · Manage Scenarios…
Help   View README… · View Log File… · Open Logs Folder · About…
```

Four creates sit in File while a fifth (Create OU) is not in the menu bar at all. Actions on the selection
are split across File, Edit and Tools. This is the structural reason the other findings exist.

*Fix (the one subjective recommendation in this document):* reorganise by noun.

```
File      Settings… · View Log File… · Open Logs Folder · Exit
New       User… · Bulk Create Users… · Group… · Cloud Group… · Organizational Unit…
Selected  Open… · Enable · Disable · Unlock · Reset Password…
          Add to Groups… · Copy User… · Copy Groups to User… · Save as Template…
          Move to OU… · Bulk Edit… · Export Members to CSV… · Append Members to CSV…
          Run Scenario ▸ · Delete…
View      Refresh · Toggle Pane Dock · Favourites ▸ (Pin / Unpin / Move up / Move down)
          Export List to CSV…
Tools     Advanced Search… · Entra Connect Delta Sync… · Manage Scenarios… · User Creation Templates…
Help      View README… · About…
```

The test to apply: an operator who wants to do something *to the thing they have selected* should have
exactly one menu to open, and everything should be in it.

### P9 — There are no keyboard shortcuts anywhere

No `InputBindings` outside a few search boxes (Enter to search), and no `InputGestureText` on any menu
item — so the menus do not advertise shortcuts either, because there are none to advertise.

ADUC binds F5, Delete and Ctrl+F. Operators arrive with that muscle memory and it fails silently.

*Fix:* F5 Refresh, Delete on the selection, Ctrl+F Advanced Search, Ctrl+N New User, F1 README. Declare
them with `InputGestureText` so the menu teaches them.

---

## T1 — Let the operator choose what is on the toolbar

Requested alongside this audit. It interacts with the rule above in a way that decides the design.

### Why this makes rule 3 load-bearing

Today the toolbar owning nothing is a happy accident. **Once the toolbar is customisable it becomes a
safety requirement**: if an operator can remove a button, no command may be reachable *only* from the
toolbar, or removing it destroys access to a feature with no way back except re-customising.

So T1 must not ship before the menu bar is complete (P1–P5). Do those first, or do them as part of this.

The catalogue below enforces it mechanically: a command is only offered as a toolbar item if it is
registered as living somewhere else too. That way the invariant is checked rather than remembered.

### Design

**A catalogue, not free text.** One place listing every command that MAY go on the toolbar:

```csharp
/// id, the canonical label (P6 — the SAME one the menu uses), the command, and where else it lives.
public sealed record ToolbarItem(string Id, string Label, string CommandName, ToolbarScope Scope);

/// Which view an item applies to. The toolbar already hides on-prem actions in the cloud view via
/// IsAdView; customisation must preserve that rather than letting an operator pin a dead button.
public enum ToolbarScope { Both, OnPremOnly, CloudOnly }
```

**Storage.** `AppSettings` gains `List<string> ToolbarItemIds`. Empty or absent means "the default set",
so an existing `settings.json` needs no migration and a new install behaves as it does today.

Unknown ids are dropped on load, not treated as an error — a settings file written by a newer build that
knew about more buttons must still open.

**The default set** is exactly today's toolbar, so nobody's layout changes on upgrade:
New User… · Bulk Create… · Templates… · | · Advanced Search… · Add to Groups… · Bulk Edit… · | ·
Refresh · Export CSV… · | · Logs

**Separators** are items too (`Id = "separator"`, repeatable), or the operator cannot group anything.

**The customisation UI.** A page in the Settings dialog: two lists (available / on the toolbar), Add,
Remove, Move up, Move down, Reset to defaults. Plus **right-click the toolbar ▸ Customise toolbar…**
opening that same page, because that is where a Windows user reaches for it first.

**Rendering.** The toolbar becomes an `ItemsControl` over the chosen items with a `DataTemplate` per kind
(button / separator), rather than hand-written `<Button>` elements.

### T1 decisions (settled)

1. **One toolbar**, whose items declare which views they apply to (`ToolbarScope`). Not separate AD and
   cloud toolbars.
2. **Per-user.** It lives in `AppSettings` under `%APPDATA%`, so a customised toolbar follows the operator
   rather than the workstation.
3. **Icons: yes, minimal.** Proposed set below.

### Proposed icons

Use **Segoe MDL2 Assets** — present on every Windows 10 and 11 install, so no image assets to ship, no
scaling problems on a high-DPI display, and it recolours with the theme. Icon **beside** the text, not
instead of it: an icon-only toolbar is its own memorisation problem, which is the thing this audit exists
to remove.

Glyphs are named rather than given as codepoints, because a wrong codepoint renders as a box and is worth
checking against the font at the time rather than trusting a number written down here.

| Toolbar item | Glyph | Why |
|---|---|---|
| New User… | `AddFriend` | The only "add a person" glyph in the set |
| Bulk Create… | `AddFriend` + `…` / `People` | Same act at scale; `People` reads as plural |
| Templates… | `Page` | A template is a document that gets copied |
| Advanced Search… | `Search` | Universal |
| Add to Groups… | `People` | Group membership |
| Bulk Edit… | `Edit` (pencil) | Universal |
| Refresh | `Refresh` | Universal |
| Export CSV… | `Save` or `Download` | `Download` reads as "out of the app" better than a floppy |
| Logs | `ShowResults` or `List` | A list of what happened |
| Export Members… | `Download` + `People` is not available; use `Download` | Distinguished by its label |
| Delete… | `Delete` | Universal, and the one item that should never be icon-only |

Two rules for whoever implements it:

- **No icon is better than a vague icon.** An item with no obvious glyph keeps its text alone rather than
  borrowing one that means something else.
- **Destructive items keep their words.** Delete never appears as a bare icon, whatever the operator has
  customised.

### Still open

- **Whether a locked-down deployment needs to pin the toolbar.** Per-user storage was chosen deliberately,
  but it means an operator can customise their way into a layout a support call then has to reason about.
  A read-only override is not designed here and is not thought to be needed.

### Testing

`ToolbarCatalogue` and the settings round-trip are pure and testable in the existing PowerShell harness.
Two assertions matter more than the rest:

- **Every catalogue item's command is reachable from the menu bar.** This is rule 3 made executable, and
  it is the assertion that stops a future button becoming a feature's only home.
- **An unknown id in the saved list is dropped and the rest survive**, rather than the whole layout being
  discarded back to defaults.

---

## Work packages

1. **`ui/menu-completeness` — P1, P2, P3, P4, P5.** The findings that change whether a feature is findable
   at all. Largest is P1 (a new context menu for the cloud list); P2–P5 are menu entries bound to commands
   that already exist.
2. **`ui/consistency` — P6, P7, P8.** Labels, de-duplication, and the menu reorganisation. P8 is the only
   subjective item here and is worth agreeing before it is built.
3. **`ui/shortcuts` — P9.** Small and self-contained.
4. **`ui/custom-toolbar` — T1.** After package 1, for the reason given above.

---

## How this was measured

Re-runnable after changes. The matrix came from cross-referencing every `[RelayCommand]` in
`MainViewModel.cs` against the three regions of `MainWindow.xaml` (`<Menu>`, `<ToolBarTray>`,
`<TreeView>`) and the `<ContextMenu>` in `ObjectListView.xaml`, matching on `{Binding <Name>Command}`.

The two surfaces that do not use commands — the tree context menu and the cloud pane — were read directly,
since a `Click=` handler cannot be matched that way. That is itself worth remembering: **a surface wired
to click handlers is invisible to any audit that looks for commands**, which is part of why P2 and P3 went
unnoticed.

A script that regenerates the matrix is not checked in; it is about twenty lines and is quicker to rewrite
than to maintain. The regex that matters is:

```
\[RelayCommand[^\]]*\]\s*(?:private|public)\s+(?:async\s+)?(?:Task|void)\s+(\w+?)(Async)?\s*\(
```

with the generated command name being the method name minus a trailing `Async`, plus `Command`.
