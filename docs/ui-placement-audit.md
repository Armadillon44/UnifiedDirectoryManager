# Where the app's functions live — a placement audit

Status: **DONE — every finding (P1–P9) and the feature (T1). All three rules hold and are enforced by
tests. Shipping as 2.3.3.**

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
| **P2** | Favourites is reachable only by right-clicking a tree node | **Done** — View ▸ Favourites |
| **P3** | OU management is reachable only by right-clicking a tree node | **Done** — View ▸ Selected Folder |
| **P4** | Seven object actions are right-click-only | **Done** — Edit and File |
| **P5** | Object actions that are menu-only never appear on right-click | **Done** |
| **P6** | The same command carries different labels in different places | **Done** |
| **P7** | Refresh and the log commands are duplicated *within* the menu bar | **Done** |
| **P8** | The menu bar is organised by nothing in particular | **Done** — File/Action/View/Tools/Help |
| **P9** | There are no keyboard shortcuts anywhere in the app | **Done** |
| **T1** | *(feature)* Let the operator choose what is on the toolbar | **Done** |

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

### P2 — Favourites is reachable only by right-clicking a tree node — **DONE**

Pin to Favourites, Unpin, Move up and Move down exist only in the tree's context menu. Nothing in the menu
bar mentions favourites. An operator who does not happen to right-click a tree node will not discover that
the feature exists.

This is a whole feature, shipped in 2.3.0 and extended since, that is invisible unless you guess the
gesture.

*Fixed.* **View ▸ Favourites** — Pin Selected Folder, Unpin Selected Folder, Move Up, Move Down — acting
on the tree selection.

### P3 — OU management is reachable only by right-clicking a tree node — **DONE**

Create OU here, Properties and Delete are tree-context-only, with the same discoverability problem as P2.
Creating an OU is not an obscure operation.

*Fixed.* **View ▸ Selected Folder** — Create OU Here…, Properties…, Delete OU…

Placement here is **provisional**: P8 proposes moving Create OU into a **New** menu and the rest into a
selected-object menu. Grouping them under View now keeps them with the Favourites items, since every one
acts on the tree selection, and leaves one thing for P8 to move rather than seven.

### P4 — Seven object actions are right-click-only — **DONE**

Enable Selected, Disable Selected, Unlock Selected, Move Selected to OU, Save Selected as Template, Export
Group Members, Append Group Members.

Enable/Disable/Unlock are among the most-used actions in a directory tool and appear nowhere in the menu
bar. (Enable/Disable/Unlock *also* exist as edit-pane buttons for a single object, which is a third
location, and a different one again.)

*Fixed.* Enable / Disable / Unlock / Save as Template / Move to OU into **Edit**, grouped by what they act
on; the two group-member exports into **File** beside Export List to CSV.

Doing it required giving every one a `CanExecute`, which they had never had — the context menu gated them
by `Visibility` instead. Without that a menu-bar item would have been permanently enabled and silently
done nothing, or shown an alert telling the operator to go and select something. They are re-evaluated in
`UpdateSelectionState`, and the suite asserts each one is.

### P5 — Object actions that are menu-only never appear on right-click — **DONE**

Copy Groups to User is the clearest case: it is a per-user action, it lives in the **Edit** menu, it also
exists as an edit-pane button — and it is absent from the object list's right-click menu, which is the
first place an operator would look for it.

New Group and New Cloud Group are menu-only, while the tree's right-click offers "New Group here…" and
"New cloud group…" — so the same two operations have menu entries and tree entries that look like
different features.

*Fixed.* Copy Groups to User is on the object list's right-click menu, for a user selection.

The tree's three create entries described one feature three ways — "Create OU here…", "New Group here…",
"New cloud group…". They are now "Create OU Here…", "New Group Here…" and "New Cloud Group…". The last
deliberately keeps no "Here": a cloud group has no container to be created in, and `NewGroupCommand`
already targets the selected container anyway, so the menu-bar and tree entries were always the same
feature.

### P6 — The same command carries different labels in different places — **DONE**

| Command | Menu bar | Toolbar |
|---|---|---|
| `ExportCsvCommand` | "Export List to CSV…" | "Export CSV…" |
| `BulkCreateUsersCommand` | "Bulk Create Users…" | "Bulk Create…" |
| `ManageTemplatesCommand` | "User Creation Templates…" | "Templates…" |

An operator who saw "Templates…" on the toolbar and goes looking for it in the menus finds "User Creation
Templates…" under Edit. Abbreviating for toolbar width is reasonable; using a *different noun* is not.

P4 added two more of the same kind, because it gave menu-bar labels to commands that until then had only a
context-menu one. They are listed here rather than fixed in P4, since choosing the canonical wording is
exactly the judgement P6 is for:

| Command | Menu bar (new in P4) | Context menu |
|---|---|---|
| `ExportGroupMembersCommand` | "Export Group Members to CSV…" | "Export members to CSV…" |
| `AppendGroupMembersCommand` | "Append Group Members to CSV…" | "Append members to a CSV…" |

The menu-bar wording needs the word "Group" — nothing else on File says what is being exported — and the
context menu does not, because it was opened on a group. That makes these defensible, unlike the toolbar
three. The indefinite article in "a CSV" is not defensible either way.

*Fixed*, and the count was worse than the three above: sixteen commands carried more than one label once
every view was read rather than just the menu bar and the toolbar.

**Toolbar = menu, exactly.** `Bulk Create…` → `Bulk Create Users…`, `Export CSV…` → `Export List to
CSV…`, `Logs` → `Open Logs Folder`. `Templates…` and `User Creation Templates…` both became **`User
Templates…`** — the only one where the canonical label was shortened rather than the button widened,
because "User Creation Templates…" is the reason nobody could find "Templates…" by scanning.

**Three names for opening an object became one.** The menu bar said `Open Selected…`, the on-prem list
said `Modify…`, the cloud list said `Properties…`, and the tree said `Properties` with no ellipsis. All
four now say **`Properties…`** — which is also what ADUC says, and this app's operators come from ADUC.

**The context menu may drop words, never change them.** `Delete…` for `Delete Selected…` is fine: you
right-clicked the selection, so the menu need not repeat what the gesture said. `Modify…` for `Open
Selected…` was not, because no word connects the two. The test states this as: the context label's words
appear in the menu label, in order, starting with the same first word.

**`Selected` left the Edit menu** where it was decoration. Every Edit item acts on the selection and only
two said so; `Add Selected to Groups…` is now `Add to Groups…`. `Delete Selected…` keeps it, because
naming the target of the destructive item is worth one inconsistency — the same instinct as T1's rule that
destructive items never go icon-only.

**Beyond the menu bar**, where the same *job* had different words in different windows: the tree's
`Pin to Favourites` / `Unpin` now match the View menu (which had said `Pin Selected Folder`); the cloud
detail pane's `Enable account` / `Disable account` became `Enable` / `Disable`, matching the cloud list's
own buttons two panes away, in a bar that is already only shown for users; `Revoke sign-in sessions…`
became `Revoke sessions`, matching the two buttons that do the same thing; and the scenario editor's
`Pick groups…` / `Remove` became `Add groups…` / `Remove selected`, matching the template editor, which
is the same control doing the same job one dialog away.

**Accelerators were checked while the labels moved**, since renaming an item moves its `_`. Edit had
offered Alt+G twice and Alt+D twice. Every menu now has unique letters, and the suite walks the menu tree
by nesting — not by indentation — so two different submenus may still reuse a letter, which is correct.

### What was deliberately NOT unified

- **`Connect…`** on the not-connected warning bar opens Settings, and the menu item says `Settings…`.
  That is a different noun for one command, which is exactly what this finding is about — but the button
  is a remedy inside a warning, not an index entry, and "Settings…" there would make the remedy less
  obvious. Windows' own warning bars do the same thing.
- **`Save changes` vs `Save`.** The cloud pane's save bar only appears when there are unsaved edits and
  sits beside `Revert`; the on-prem edit pane's is always there beside `Reload`. Different affordance,
  different sentence.
- **`Export CSV…` in the bulk-create report window**, which is a `Click=` handler in its own window and
  exports plaintext passwords — not the list export at all. Worth noting because it is invisible to any
  analysis that matches on `{Binding …Command}`, which is the same blind spot that hid P2 and P3.
- **Three same-named commands on *different* view models**, which a name-based sweep reports as
  conflicts and which are not: the cloud list's `Export loaded…` / `Export all…` pair (a genuine
  distinction — the loaded page versus everything behind it), `Manage…` beside the template combo in
  the New User wizard (the adjacent label supplies the noun), and Bulk Edit's `Add to groups…`, which
  is an *operation to apply to the selection*, not the "add rows to this list" button it shares a name
  with in the template and scenario editors.

After P6, a sweep of every `(label, command)` pair in every view is down from **16 commands with more
than one label to 8**, and each of those 8 is either a context-menu shortening the test accepts or one
of the cases listed here. That number is worth re-measuring after P8.

### P7 — Refresh and the log commands are duplicated inside the menu bar — **DONE**

- **Refresh** appears in File, in View, and on the toolbar.
- **View Log File…** and **Open Logs Folder** appear in both File and Help.

When this was written the **View** menu had only two items in total (Toggle Pane Dock, Refresh) and read
as a leftover rather than a category. P2 and P3 have since given it the Favourites and Selected Folder
submenus, so it is now a real menu and the obvious home for Refresh.

*Fixed.* Refresh is in View and on the toolbar. The logs are in Help — the menu you open when you want to
know what the app did — and on the toolbar. Neither is in File any more.

The toolbar still duplicates both, which is not a regression: rule 3 says duplication is what the toolbar
is *for*. The suite says so out loud, so that nobody "finishes" P7 by deleting the buttons.

Removing items from the middle of a menu leaves separators behind, so the suite also checks File for two
separators in a row and for a separator immediately before `</MenuItem>` — a line drawn across an empty
gap is the visible half of this kind of edit going wrong.

### P8 — The menu bar is organised by nothing in particular — **DONE**

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

The proposal below is what this document argued for. **What was agreed is different in four places**
and is recorded under [The agreed shape](#the-agreed-shape) after it. The original is kept because the
reasoning for the changes only makes sense against it.

```
File      Settings… · View Log File… · Open Logs Folder · Exit
New       User… · Bulk Create Users… · Group… · Cloud Group… · Organizational Unit…
Selected  Properties… · Enable · Disable · Unlock · Reset Password…
          Add to Groups… · Copy User… · Copy Groups to User… · Save as Template…
          Move to OU… · Export Group Members to CSV… · Append Group Members to CSV…
          Bulk Edit… · Run Scenario ▸ · Delete Selected…
View      Refresh · Toggle Pane Dock · Favourites ▸ (Pin to Favourites / Unpin from Favourites /
          Move Up / Move Down) · Export List to CSV…
Tools     Advanced Search… · Entra Connect Delta Sync… · Manage Scenarios… · User Templates…
Help      View README… · About…
```

The test to apply: an operator who wants to do something *to the thing they have selected* should have
exactly one menu to open, and everything should be in it.

### The agreed shape

**`File` / `Action` / `View` / `Tools` / `Help`, with `New` as a submenu inside `Action`.** Not the
`New` + `Selected` pair above. This is the MMC layout — ADUC, DNS, DHCP, Certificates, all of them — so
it is the one shape these operators already have muscle memory for, and the only one where being told
"it's under Action" means anything before they look.

Four decisions behind it, each of which changed the work:

**1. `Action` serves the cloud view too.** Today the Edit menu is disabled wholesale in the cloud view,
and the cloud list's Properties / Enable / Disable / Revoke sessions are in *no* menu at all — rule 1's
test only ever scanned `MainViewModel`, so it never noticed. The Action menu now swaps its contents
with the view, and the test covers `CloudObjectListViewModel` as well. The cloud half gets a named
exemption list rather than a lower standard: `LoadMore` and `Search` are pane furniture, and
`EnableChecked` / `DisableChecked` / `RevokeChecked` are the button twins of the selection commands
that *are* in the menu.

**2. Creating an OU moves into `New`; the other two tree items stay in `View`.** `New ▸ OU…` joins the
other creates. `Properties…` and `Delete OU…` stay under `View ▸ Selected Folder`, because they act on
the tree node rather than on the list selection and putting them in `Action` would make that menu mean
two different things depending on which pane you last touched.

**3. `Reset Password…` is new, and deliberately single-target.** The proposal listed it under Selected,
but it only ever existed as an edit-pane button — there was no selection-level command to move. It is
now in `Action`, enabled only when exactly one user is selected. A bulk password reset is a different
feature with its own confirmation and reporting problems, and does not belong in a reorganisation.

**4. `File` keeps the exports, and gains the cloud ones.** `Export List to CSV…` writes out the list
you are looking at, not the rows you picked, so it is not an `Action`. In the cloud view File now shows
`Export Loaded to CSV…` and `Export All to CSV…` — the cloud list's own two commands, whose
distinction (the loaded page versus everything behind it) is real and worth keeping visible. Exporting
*group members* is the opposite case: it acts on the groups you selected, so it is in `Action`.

```
File    Export List to CSV…                        (AD view)
        Export Loaded to CSV… · Export All to CSV…  (cloud view)
        Settings… · Exit
Action  New ▸ User… · Bulk Create Users… · Group… · OU…        (AD view)
        New ▸ Cloud Group…                                    (cloud view)
        Properties…
        Enable / Disable / Unlock Account(s) · Reset Password…  (AD view)
        Enable / Disable Account(s) · Revoke Sessions           (cloud view)
        Copy User… · Copy Groups to User… · Save as Template…
        Add to Groups… · Move to OU… · Bulk Edit…
        Export Group Members to CSV… · Append Group Members to CSV…
        Run Scenario ▸
        Delete Selected…
View    Refresh · Toggle Pane Dock
        Favourites ▸ (Pin to Favourites / Unpin from Favourites / Move Up / Move Down)
        Selected Folder ▸ (Properties… / Delete OU…)
Tools   Advanced Search… · Entra Connect Delta Sync… · Manage Scenarios… · User Templates…
Help    View README… · View Log File… · Open Logs Folder · About…
```

`Edit` disappears. That is the part worth being uneasy about: Alt+E has done something in this app
since it shipped. Nothing replaces it, because a menu kept only to catch a habit is a menu that has to
be a duplicate of something — which is what P7 just finished removing.

Shipping with P4–P7 as **2.3.3**, so the menus are relearned once rather than twice.

### What building it turned up

**The cloud half of the app was never covered by rule 1, and the suite reported green anyway.** The
test walked `MainViewModel` and stopped. `CloudObjectListViewModel`'s Properties, Enable, Disable and
Revoke sessions were in no menu at all — P1 gave them a context menu and that was the end of it. The
same invariant that found the on-prem gaps had been sitting next to this one for three findings
without seeing it, because it was pointed at one view model. **Half an invariant is worse than none:
it is the half that reports green.** It now walks both, with the cloud exemptions named the same way
the on-prem ones are.

**Dotted bindings were invisible to the typo check too.** `{Binding Cloud.ExportAllCsvCommand}` did not
match `\w+Command`, so the four new ones P8 introduced would have failed exactly as silently as any
other mistyped binding — greyed out forever, indistinguishable from unavailable. They are now resolved
for real, by walking the property path on the built type. Each view that uses one names its root view
model in a small table, so a view that starts using dotted bindings without being listed fails rather
than being checked against the wrong type. The check found a fifth on its first run
(`Connection.ConnectCommand` in `SettingsWindow.xaml`), which is how I learnt the roots have to be
per-view.

**Hiding by VIEW is not the same as hiding by selection**, and the distinction had to be written down
before the Action menu could exist. P4 established "menu items disable, never hide" — but an Action
menu serving both views has to hide Unlock in the cloud view, because there is no lockout in Entra to
clear and the command is not part of that vocabulary at all. The rule, now asserted: **`Visibility` in
the menu bar may bind only to `IsAdView` or `IsCloudView`. Everything else gates with `CanExecute`.**
Any other binding fails the suite by name.

**Two `Action` menus, not one with per-item visibility.** `IsAdView => !IsCloudView` is a strict
complement, so exactly one is ever on screen. It keeps each menu readable, and it saves giving every
separator visibility logic of its own. It does mean two siblings share `Alt+A`, so the accelerator
check was refined: a letter is a clash only when it could mean two **different** things, which two
identically-labelled mutually-exclusive menus cannot.

**The tree's context menu still cannot be checked by the label machinery**, because it is wired with
`Click=` handlers and `BoundLabels` matches on `{Binding …Command}`. Its ten items are each asserted
by name instead, and the suite now counts them, so an eleventh cannot slide in unexamined. This is the
third time that blind spot has mattered in this document.

**Menus opened in mirror image on the machine it was first tried on.** Reported from a dev build with
a screenshot: `Action` aligned its right edge to its header, and `Action ▸ New` flew out to the LEFT.
Not the XAML. Windows has a per-user setting, `SM_MENUDROPALIGNMENT`, that mirrors menus for left-handed
pen use; the Tablet PC handedness setting turns it on, so it appears on Surfaces, touchscreen laptops and
anything with a digitizer, usually without the operator choosing it. WPF reads it once per process and
obeys it everywhere, and there is no public API to override it — the setting is deliberately global.

`MenuDropAlignment.ForceMenusToOpenRightwards()` overwrites the cached value by reflection at startup.
The **order** inside it is the whole trick and is invisible in the code: `SystemParameters` caches the
value on first read, so writing the field before anything has read it is silently undone by that first
read — the override looks right and does nothing. The suite proves this by doing it both ways in child
processes, and says so out loud when it is running on a machine that cannot exercise the proof.

This is the only place in the app that reaches into private framework state. Every step is optional and
failure is logged rather than thrown: the worst case is the behaviour we had.

**`Reset Password…` is the one new command.** Gated on `SelectionIsOneUser` — exactly one user
selected — rather than looping over a multi-selection. Every reset produces a secret that has to reach
a different person, and a bulk version would need the post-run report Bulk Create Users has. That is a
feature, not a menu entry.

### P9 — There are no keyboard shortcuts anywhere — **DONE**

No `InputBindings` outside a few search boxes (Enter to search), and no `InputGestureText` on any menu
item — so the menus do not advertise shortcuts either, because there are none to advertise.

ADUC binds F5, Delete and Ctrl+F. Operators arrive with that muscle memory and it fails silently.

*Fixed*, as listed, plus **Alt+Enter** for Properties — Windows' own gesture for it, and the natural
companion to Delete since both act on the selected row. Every one is declared with `InputGestureText`,
and the suite refuses to let a menu advertise a gesture nothing implements: **a promise that fails is
worse than no promise, because the operator learns it, it does not work, and they stop trusting the
others.**

| Gesture | Does | Scope |
|---|---|---|
| `F5` | Refresh | window |
| `Ctrl+F` | Advanced Search… | window, AD view only |
| `Ctrl+N` | New User… | window, AD view only |
| `F1` | View README… | window |
| `Del` | Delete Selected… | the object lists |
| `Alt+Enter` | Properties… | the object lists |

**Delete is scoped to the lists, and the reason is not the one I expected.** The obvious worry with a
window-wide Delete is that it would fire while somebody is editing a text field. Measured: it would
not. A focused `TextBox` marks Delete handled **even when it is empty and has nothing to delete**, and
WPF skips input bindings for a handled key. Typing was never at risk.

The real hazard is the tree. A `TreeView` does **not** handle Delete, so a window-wide binding would
fire while an operator was browsing folders — and silently mean *"delete whatever is selected over in
the list"*, which they may not even be able to see. That is the kind of accident that ends with a
restore from backup. Scoping it to the list is also what ADUC and Explorer do.

Both of those behaviours are now **assertions**, not comments: the suite builds a small window, sends
real key events, and checks that an empty `TextBox` swallows Delete, that a plain `ListBox` does not,
and that `F5` gets through from inside a text box. If a future .NET changes any of it, the scoping
decision gets re-examined instead of quietly becoming wrong.

**A gesture has nothing to grey out.** `Ctrl+N` and `Ctrl+F` have no menu item to disable in the cloud
view — the whole AD `Action` menu is swapped away — so without a gate the shortcut would open an
on-prem wizard over a cloud list. `NewUserCommand` and `AdvancedSearchCommand` gained `CanExecute` on
`IsAdView`, re-evaluated when the view changes. `DeleteSelectedCommand` gained one on `HasSelection`,
which it should have had since P4.

**The list raises an event; it does not delete anything.** `ObjectListViewModel.RequestDelete()` fires
`DeleteRequested`, and `MainViewModel` runs the same `DeleteSelectedCommand` the menu and the context
menu run — so the keyboard path gets the same confirmation. This follows the `OpenRequested` pattern
the double-click already used.

**One thing a binding can be and still do nothing:** an `InputBinding` is not in the visual tree, so
`{Binding …}` on its `Command` has no obvious reason to resolve, and `RelativeSource` genuinely does
not work there. It does resolve from `DataContext` — but only once the window is shown; it reads null
before that. Measured before the code was written rather than after it failed.

---

## T1 — Let the operator choose what is on the toolbar — **DONE**

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
New User… · Bulk Create Users… · User Templates… · | · Advanced Search… · Add to Groups… · Bulk Edit…
· | · Refresh · Export List to CSV… · | · Open Logs Folder

(These are P6's labels, and they are longer than the ones this section was first written against. With
icons beside the text the row is wider than it is today, which is the practical argument for letting
width be the operator's problem to solve by removing buttons — which is what T1 is for.)

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
| Bulk Create Users… | `AddFriend` + `…` / `People` | Same act at scale; `People` reads as plural |
| User Templates… | `Page` | A template is a document that gets copied |
| Advanced Search… | `Search` | Universal |
| Add to Groups… | `People` | Group membership |
| Bulk Edit… | `Edit` (pencil) | Universal |
| Refresh | `Refresh` | Universal |
| Export List to CSV… | `Save` or `Download` | `Download` reads as "out of the app" better than a floppy |
| Open Logs Folder | `ShowResults` or `List` | A list of what happened |
| Export Group Members to CSV… | `Download` (`People` is not available) | Distinguished by its label |
| Delete… | `Delete` | Universal, and the one item that should never be icon-only |

Two rules for whoever implements it:

- **No icon is better than a vague icon.** An item with no obvious glyph keeps its text alone rather than
  borrowing one that means something else.
- **Destructive items keep their words.** Delete never appears as a bare icon, whatever the operator has
  customised.

### Still open

Nothing. The maintainer confirmed the toolbar never needs to be pinned read-only, so per-user storage
with a Reset to defaults button is the whole of it.

### What building it turned up

**Rule 1 caught the first version of the feature.** `Customise Toolbar…` was on the toolbar's own
right-click menu and nowhere else — which is precisely the shape rule 1 exists to stop, committed by the
change whose own safety argument depends on that rule. The suite named it on the first run. It is in
**View** now, where MMC also puts Customize.

**The old toolbar assertion had to move, not go.** P6 asserted "every toolbar label equals its menu
label" by reading literal `<Button Content=…>` out of the `ToolBarTray`. A data-driven toolbar has none,
so that check would have passed vacuously forever. It now runs over the CATALOGUE, which is strictly
stronger: it covers every button that CAN be added, not just the nine that happen to be on it today.

**One context menu quietly became another.** The suite found the tree's context menu with "the first
`<ContextMenu>` in the file". The toolbar gained one of its own, earlier in the file, so six assertions
silently moved to a menu they were not written for. They are scoped to the `TreeView` region now. Worth
remembering: an anchor that says "the first one" is a bet that nothing will ever be added above it.

**Glyph codepoints were checked against the font rather than trusted**, as this section asked. All 24
are present in the Segoe MDL2 Assets that ships with Windows, and the suite re-checks them, so one
dropped from a future Windows is found here rather than by an operator seeing an empty box.

**Emptying the toolbar cannot be allowed**, which is a consequence of the storage decision rather than a
choice. An empty saved list means "use the defaults" — that is what lets an existing `settings.json` work
unchanged — so removing the last button would silently restore the whole default toolbar. The Remove
button is disabled at one item, and the suite says so in those words.

**Normalise does three things, in order**, each of which has a test: nothing saved means the defaults; an
id this build does not know is dropped while the rest survive in order; and separators are tidied (none
leading, none trailing, never two together) — including the doubled ones that appear only *after* an
unknown id between them is dropped.

### Testing

`ToolbarCatalogue` and the settings round-trip are pure and testable in the existing PowerShell harness.
Two assertions matter more than the rest:

- **Every catalogue item's command is reachable from the menu bar.** This is rule 3 made executable, and
  it is the assertion that stops a future button becoming a feature's only home.
- **An unknown id in the saved list is dropped and the rest survive**, rather than the whole layout being
  discarded back to defaults.

---

## All three rules are now enforced, not just stated

With P1–P5 done, **every command `MainViewModel` exposes is reachable from the menu bar**, and
`test-ui-placement.ps1` asserts it. A command that slips back out fails the suite by name.

There is exactly one exception, listed in the test rather than skipped silently so it can be argued with:

- `OpenSelectedCommand` — double-click and the context menu, and File carries it too.

This is the assertion the whole first work package existed to make possible. It is worth keeping even
after P8 moves everything around: the menus can be reorganised freely as long as the total stays complete.

P6 and P7 added three more that hold across the whole app rather than over one finding:

- **Every toolbar label equals its menu label.** No exemptions. This is rule 3 ("the toolbar owns
  nothing") made checkable — a button that needs its own vocabulary is a button that has become a
  feature's only home.
- **Every context-menu label is a shortening of its menu label**, defined as: same first word, remaining
  words present and in order. It permits the abbreviation a context menu earns and forbids the synonym.
- **No menu offers one accelerator letter twice**, with siblings found by walking the nesting rather than
  by indentation, so two different submenus may reuse a letter.

Each was mutation-checked by putting the old label back: every one fails and names the command, the
surface and both labels.

P8 added four more, and widened the first one:

- **Rule 1 now walks `CloudObjectListViewModel` as well as `MainViewModel`**, with a named exemption
  list for the cloud list's pane furniture and button twins.
- **Every selection action is in `Action`, and appears exactly once in the whole menu bar.** This is
  the test the finding asked for, stated directly: one menu to open, everything in it, nothing
  anywhere else.
- **`Visibility` in the menu bar binds only to `IsAdView` or `IsCloudView`.** Hiding by view is
  honest; hiding by anything else is the lesson this audit undoes.
- **Every dotted binding resolves** against its view's own root view model, by reflection on the built
  assembly rather than by pattern-matching the name.
- **No menu advertises a gesture nothing implements**, and every gesture is advertised by a menu item
  (P9). Plus the two WPF behaviours the Delete scoping rests on, asserted with real key events rather
  than trusted.

Mutation-checked, each failing and naming what it found: a selection command filed in `File`
("DeleteSelectedCommand appears 2 times in the menu bar"), an `Action` item gated by
`SelectionHasUsers` ("Visibility bound to SelectionHasUsers"), a cloud command deleted from the menu
bar, `Cloud.ExportAllCsvCommand` misspelt, two different items given `Alt+U` ("Alt+U means _Unpin from
Favourites AND Move _Up"), and the tree drifting back to "Create OU Here…".

## What the first work package changed, beyond the menu entries

Four things worth knowing before P6–P9.

**The logic was already in the view model.** `PinNode`, `UnpinNode`, `MoveFavorite`, `CreateOuUnderAsync`,
`ShowNodeProperties` and `DeleteOuAsync` were all public methods that the tree's click handlers called.
What was missing was a COMMAND surface over the *selected* node, which is what a menu-bar item needs. The
new commands are one line each and the context menu still calls the same methods with the node it was
opened on. One implementation, two targets — the same shape P1 used for the cloud list.

**Right-click now selects the tree node.** Both object lists already did this; the tree did not. Without
it, right-clicking one folder while another was selected left the two surfaces disagreeing about which
folder they meant — the context menu acting on the clicked one, the View menu on the selected one, with
nothing on screen to say so. That only became reachable once the View menu existed, so P2/P3 created the
hazard and closed it in the same change.

**Menu-bar items are disabled, never hidden.** The context menu hides what does not apply, which is right
for a menu about one object. A menu bar is an index, and an item that vanishes teaches that the feature
does not exist — the exact lesson this audit is undoing. The test suite asserts the two submenus and the five new
Edit entries contain no `Visibility` binding at all, so this does not quietly drift.

**Nine commands gained a `CanExecute` they never had.** The context menu had always gated them by
`Visibility`, so nothing in the view model knew whether they applied. That is why P4 was more than five
menu entries: without the gates, Edit ▸ Unlock Account(s) would have been permanently enabled and done
nothing on an empty selection. The gates also reach the context-menu items, which is harmless — those are
already hidden in exactly the cases the gates now disable.

## Work packages

1. **`ui/menu-completeness` — P1, P2, P3, P4, P5.** ✅ **Done.** The findings that change whether a feature
   is findable at all. Largest was P1 (a new context menu for the cloud list); P2–P5 were menu entries
   bound to commands that already existed, plus the `CanExecute` gates those entries needed.
2. **`ui/consistency` — P6, P7, P8.** ✅ **Done.** Labels, de-duplication, and the menu reorganisation.
   P8 was the only subjective item and was agreed before it was built — see *The agreed shape* above.
3. **`ui/shortcuts` — P9.** ✅ **Done.** Small and self-contained, apart from one measurement that
   changed the design — see the finding.
4. **`ui/custom-toolbar` — T1.** ✅ **Done.** After package 1, for the reason given above — and rule 1
   caught a violation inside T1 itself on the first test run, which is the clearest argument there is
   for having done them in that order.

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
