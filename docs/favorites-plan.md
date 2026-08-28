# Pinned favourites ([issue #7](../../issues/7))

Status: **done** — shipped over three commits on `release/2.3.0`.

Pin the OUs, containers and saved searches you use most to the top of the navigation tree, so reaching them
is one click instead of a drill-down through the OU structure.

---

## Locked decisions

**D1 — two kinds of favourite: containers and saved searches.** An OU, a container, or the domain root; and a
saved advanced search / raw LDAP query. The Entra and Exchange sections are deliberately **not** pinnable:
they are already top-level and one click away, so pinning one would only add a second route to the same place.
Individual objects are out of scope for this pass.

**D2 — favourites are per domain.** A distinguished name only means something in the domain it came from, so
one list per domain, keyed on the FQDN the app already records as `LastDomainFqdn`. Connecting elsewhere shows
that domain's own favourites rather than a set of entries that fail when clicked. A saved search pinned in one
domain is therefore pinned only there; the query itself stays in the shared store and can be pinned again.

**D3 — a favourite shows the object's own name.** No custom labels. The name comes from the entry itself each
time it is shown, never from a copy taken when it was pinned, so a favourite cannot drift from the thing it
points at.

**D4 — the order is the operator's, and is stored.** Added during step 3, at the operator's request. Move up
and move down from a favourite's own right-click menu, swapping with its neighbour; the resulting order goes
into settings rather than being derived from pin time or name. A move that cannot happen — already at the end
it is being moved towards, or not pinned at all — reports failure and changes nothing, so the caller does not
save and redraw for a move that did not occur. Re-selecting the moved row is suppressed: reordering a pin is
not a request to open it again.

---

## A favourite is a reference, not a copy

This is the rule the whole feature turns on. A favourite stores **what it points at**, and resolves it on use:

| Kind | Stored | Activating it does |
|---|---|---|
| Container | The distinguished name | `List.LoadContainerAsync(dn)` — the same call selecting it in the tree makes |
| Saved search | The search's name | `LoadAll()`, find it, then `List.LoadQueryAsync(search.Query)` |

Anything copied at pin time — a display name, a column set, a query — is a second source of truth that goes
quietly stale. Reusing the existing activation paths also means a favourite cannot behave differently from the
real thing, which is the same reason the Exchange actions control is hosted twice rather than written twice.

## When the target is gone

An OU gets renamed, moved or deleted; a saved search gets deleted. The favourite then points at nothing.

**It is shown as unavailable and left in place** — greyed, with the reason — and the operator removes it.
Dropping someone's pin because one lookup failed is the worse error of the two: a domain controller being
briefly unreachable is not evidence that an OU no longer exists, and a silently shorter list is not something
anyone notices in time to object.

Note that a rename changes the DN, so a renamed OU reads as unavailable rather than following the rename.
Following it would mean resolving by GUID, which is a bigger change than this issue asks for — worth doing
later if renames turn out to be common.

---

## Where it lives

- **The tree.** A `Favourites` root above the domain root and the cloud sections. `MainViewModel.RootNodes` is
  already assembled by hand, with the Entra and Exchange roots added the same way, so this needs no
  restructuring. Empty, it shows a single "Nothing pinned yet" hint rather than an empty expander.
- **Pinning.** From the tree's existing right-click menu, beside *Create OU here* and *Properties*, gated the
  same way its other entries are (`IsContainerNode`). Unpin from the favourite's own right-click menu.
- **Saved searches** are pinned from wherever a saved search is chosen, which is the Advanced Search dialog.
- **Persistence.** `AppSettings`, which already keeps per-user layout and column choices in
  `%APPDATA%\UnifiedDirectoryManager\settings.json`.

## Sequencing

1. **The model and persistence** — the entry type, the per-domain store on `AppSettings`, load and save.
   Testable without a directory.
2. **The Favourites root and container pinning** — the node, the two menu entries, activation, the unavailable
   state.
3. **Saved-search favourites** — pinning from the Advanced Search dialog, and running one on activation.
   Reordering landed here too.

## What shipped

| Step | Commit | Covered by |
|---|---|---|
| 1 — model and persistence | `FavoriteEntry`, `Favorites`, `AppSettings.Favorites` | `test-favorites.ps1` |
| 2 — the Favourites row, pinning, unpinning | `TreeNodeViewModel`, `MainViewModel`, `MainWindow` | `test-favorites.ps1` |
| 3 — saved searches and reordering | `Favorites.Move`, `SavedSearchPinning`, `AdvancedSearchViewModel` | `test-favorites.ps1`, `test-search-dialog.ps1` |

**The dialog does not own the favourites.** `AppSettings` lives with `MainViewModel`, which knows the
connected domain; `DialogService` does not hold one. Rather than hand a dialog the whole settings object,
`ShowAdvancedSearch` takes a `SavedSearchPinning` — two hooks, *is this pinned* and *toggle it* — and
passes null when there is no connection, which hides the button entirely. Ownership stays where it was.

**The Pin button is disabled until a saved search is selected.** An enabled button that does nothing when
pressed reads as broken, and this one had exactly that shape until the off-screen harness clicked it.

## Sharp edges

- **The tree node type carries two shapes already** (an on-prem `AdNode`, or a `CloudNodeKind` for a section).
  A favourite is a third. The switch in `OnSelectedNodeChanged` lists every cloud kind explicitly *because* a
  fall-through would silently show the Entra Users list — the same care is needed here, or a favourite whose
  kind is not handled will look like it works.
- **Pinning the same thing twice** must be a no-op, not a duplicate row.
- **Settings are shared with the window layout**, so writing favourites must not clobber a concurrent layout
  save; the store already round-trips the whole object, so favourites are read-modify-written with it.
