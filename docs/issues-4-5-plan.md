# Simple search, and a record of removed members ([#5](../../issues/5), [#4](../../issues/4))

Status: **reviewed and locked.** Decisions D1–D9 below are settled; the work follows the sequencing at the end.

Two unrelated issues, planned together because they are both small, both touch surfaces that have **no test
coverage at all today**, and both turn out to have a hidden dependency that the issue text does not mention.

---

## Locked decisions

**D1 — the quick filter gets its own field set, decoupled from the visible columns.** `name`,
`displayName`, `givenName`, `sn`, `sAMAccountName`, `userPrincipalName`, `mail`, `proxyAddresses`, loaded
whether or not those columns are shown, held in a dedicated search field on the row rather than in
`Values`. The predicate stops reading `Values` entirely, so the two can never drift back together.

**D2 — `manager`, `description` and `groupType` stop being searched.** This follows the issue's explicit
list, which names neither. It is a **behaviour change**: `description` and `groupType` are default columns
today and are therefore searched today, so anyone relying on finding a group by its description will notice.
Advanced Search covers those, which is what the issue asks for.

**D3 — matching is graded: word-boundary prefix, then substring.** Prefix per query token first; substring
only if that rung returns nothing, which preserves today's mid-word matching as a fallback. **No
edit-distance rung** — measured, `jsmith` is distance 1 from `smith`, so typo tolerance pulls in names the
operator did not mean, and that is a poor trade on a live directory. Revisit only if prefix-then-substring
proves insufficient in use.

**D4 — the cloud and Exchange panes are not touched.** Their Filter box already populates every value
regardless of columns, so it does not have the defect this issue describes. Scope stays on the AD quick
filter the issue names. The two boxes keep slightly different field sets; that is a known and accepted
inconsistency, not an oversight.

**D5 — the pickers move to ANR.** `SearchByNameAsync` swaps three medial wildcards for
`(|(anr=v)(userPrincipalName=v*)(mail=v*))`. Independent of the rest of #5: it turns three unindexed
subtree walks into indexed prefix lookups and adds the two attributes ANR misses. ANR is **never** put
behind the quick-filter box, which fires per keystroke with no debounce.

**D6 — records are written for the user-initiated removal surfaces only.** The AD *Members* tab, AD
*Member Of*, the cloud *Members* tab, the cloud *Member Of* tabs, and the distribution-group members
window. The four scenario steps already record name + identifier and are left alone, so a scenario run does
not produce two records of one removal. **Bulk Edit is out of scope** — it is M targets × N groups and needs
a different record shape; its confirmation naming neither side is arguably its own bug and belongs in its
own issue.

**D7 — a removal record cannot block the removal. Warn and proceed.** This deliberately **inverts** the
group-delete rule of *no record, no delete*. That rule is right for a deletion, which is irreversible; a
membership removal is not. Refusing a reversible operation because a log file could not be written would be
a worse failure than the one it prevents. The failure is surfaced, not swallowed.

**D8 — one record file per removal operation**, named and formatted in the `GroupDeletionRecord` style, in
the same configurable operation-log folder. Not an appended ledger: a per-operation file is what makes a
single removal findable afterwards, and it matches the precedent an operator has already seen.

**D9 — the paste parser gains a distinguished-name rung**, so a recorded identifier can be pasted straight
back into **Paste a list…**. Without it a record satisfies *"log who was deleted"* but not *"so they can be
easily re-added"*. It also retroactively makes every 2.2.0 group-deletion CSV directly re-addable.

Two smaller calls, taken by default and easily reversed if either is wrong: the record names **the operator
who performed the removal**, matching the scenario operation log's `Run by` header; and **additions do not
get records**, since an addition is self-evident from the group's own membership and the issue does not ask
for it.

---

# Issue #5 — the simple search

> Do not search manager or other attributes with the simple search. Only search name, (last and first), UPN,
> email (including alias / proxy addresses) and SAM. Allow fuzzy search. Let advanced search handle other
> attributes.

## The title and the body are both right

The title says the search matches *too little*; the body says it matches *too much*. Both describe the same
control, and the code explains why.

[`ObjectListViewModel.cs:206-212`](../app/src/UnifiedDirectoryManager/ViewModels/ObjectListViewModel.cs#L206-L212)
matches `row.Name` plus **every value in `row.Values`**. `row.Values` is populated in
[`DirectoryService.cs:245-246`](../app/src/UnifiedDirectoryManager/Services/DirectoryService.cs#L245-L246)
from exactly the **requested columns**, and the requested columns are the **visible** ones.

So the quick filter searches precisely the columns you have ticked:

| Field the issue names | Searchable today? |
|---|---|
| `name` | Always |
| `displayName`, `sAMAccountName` | Yes — both are default columns |
| `givenName`, `sn` (first / last) | **Only if you turn those columns on** |
| `userPrincipalName`, `mail` | **Only if you turn those columns on** |
| `proxyAddresses` (aliases) | **Never** — not in `DefaultColumns()` at all, so it is never even loaded |
| `manager` | **Yes, whenever its column is on** — and it matches the manager's *resolved display name*, so typing `smith` returns Smith **plus everyone who reports to Smith** |

`description` and `groupType` are also default columns and therefore also searched today.

**The root defect is that search behaviour is a side effect of column choices.** Two people with the same
directory and different column sets get different results from the same query, and neither is told why. Every
other symptom in the issue follows from this one fact.

It is worse than it looks, because the column set never self-heals: restoring saved columns is a **replace,
not a merge** ([`ObjectListViewModel.cs:69-78`](../app/src/UnifiedDirectoryManager/ViewModels/ObjectListViewModel.cs#L69-L78)).
Anyone who has ever touched **Columns ▾** is frozen at that version's defaults — proven by `groupType`, added
later, which stays hidden for them.

## Decouple the search set from the columns

The fix is to give the quick filter **its own fixed field set**, loaded whether or not those columns are
shown, and to stop it reading `row.Values` at all.

Proposed set, taken from the issue: `name`, `displayName`, `givenName`, `sn`, `sAMAccountName`,
`userPrincipalName`, `mail`, `proxyAddresses`. Held on `AdObjectRow` in a dedicated search field, separate
from the display values, so the two can never drift back together.

This costs a handful of extra attributes per row on a read that is already happening. It is not a new query.

## "Fuzzy" — what is actually available

**Active Directory has no fuzzy operator.** [MS-ADTS] 3.1.1.3.1.3.1 states `~=` is *"implemented identically
to equalityMatch… No approximation is performed."* Extensible match rules are unsupported. There is no
server-side similarity matching in AD through any operator, so fuzzy has to be client-side.

**ANR is not a substitute.** `(anr=x)` is *prefix* matching that the DC rewrites into a set of
`(attr=x*)` clauses; `(anr=*x*)` resolves to Undefined, so it cannot be wildcarded. It also **misses two
attributes this issue names** — `userPrincipalName` and `mail` are indexed but not in the ANR set — while
silently **adding** `physicalDeliveryOfficeName` and the `msDS-Phonetic*` set, which is the sort of
"other attribute" the issue asks to stop searching.

### Measured, not guessed

The candidate predicates were benchmarked over 5,000 synthetic rows on .NET 10:

| Predicate | Time | Verdict |
|---|---|---|
| Today (substring over visible columns) | 0.86 ms | — |
| Substring over a fixed identity set | 1.28 ms | Fine |
| Same, with lowercase precomputed at load | **0.34 ms** | **Faster than today** |
| Token word-boundary prefix | 0.82 ms | Fine |
| Subsequence / acronym | 0.62 ms | **Disqualified** — any 3-character query matched **100%** of rows; the full surname `smith` still matched 18% |
| Damerau-Levenshtein | 1.88 ms | Affordable, but only sane at distance 1 on tokens of 4+ characters |

**Cost is not the deciding factor. False positives are.**

### The trap that decides the design

Simply adding `mail`, `userPrincipalName` and `proxyAddresses` to a naive substring match makes the query
`lacrosse` match **every single row**, because every address contains `lacrossefootwear.com`. Widening the
field set without anchoring the match would make the search dramatically worse than it is now.

So matching must be **anchored to word boundaries**, not raw substring, on the address fields at minimum.

### Proposed: graded passes, best rung wins

The same shape [`AdMemberResolver`](../app/src/UnifiedDirectoryManager/Services/MemberResolvers.cs) already
uses — and which is already proven in this codebase:

1. **Word-boundary prefix** on each token of the query, against each field. `jo smi` finds *John Smith*.
2. **Substring**, only if rung 1 returned nothing. Preserves today's mid-word matching as a fallback.
A third rung — Damerau-Levenshtein at distance 1 — was measured and **rejected** under D3. It is affordable
(1.88 ms over 5,000 rows) but `jsmith` sits at distance 1 from `smith`, so it surfaces names the operator
did not ask for. On a directory, a confidently wrong match is worse than no match.

`proxyAddresses` is multi-valued and would flatten to `SMTP:a; smtp:b; x500:…`. It must be matched
**per address with the scheme prefix stripped**, or `smtp` matches every mail-enabled user and the
primary-versus-alias distinction is erased by a case-insensitive compare.

## The pickers, separately

[`SearchByNameAsync`](../app/src/UnifiedDirectoryManager/Services/DirectoryService.cs#L253) is a **different
surface** — server-side, used by the five object pickers, not by the main list. It runs three medial
wildcards (`(cn=*x*)`, `(sAMAccountName=*x*)`, `(displayName=*x*)`) over a domain-wide subtree, which forces
a full walk of the search scope because no relevant attribute carries a tuple index.

Replacing those with `(|(anr=v)(userPrincipalName=v*)(mail=v*))` turns three unindexed walks into indexed
prefix lookups, and adds the two fields ANR misses. This is a **performance and correctness win independent
of #5**, and it belongs only here — never behind the quick-filter box, which fires per keystroke with no
debounce.

## Sharp edges

- **The AD list pane is uncapped.** `PageSize 1000`, no `SizeLimit`
  ([`DirectoryService.cs:214-222`](../app/src/UnifiedDirectoryManager/Services/DirectoryService.cs#L214-L222)).
  Domain root + *Include sub-OUs* + *All* loads the entire directory, and `RowsView.Refresh()` runs
  **synchronously on the UI thread on every keystroke**. The benchmark says 5,000 rows is fine; 100,000 is
  not measured. Precomputing lowercase at load and debouncing the box are cheap insurance either way.
- **"Include sub-OUs" is a scope change, not a filter change** — it switches the server-side search to
  `SearchScope.Subtree`. It does not widen what the quick filter matches, and the plan must not conflate them.
- **The cloud pane's Filter box has the same label but different semantics.** Cloud rows populate
  `row.Values` **fully, regardless of columns**, so cloud filtering is already column-independent. Whatever is
  decided for AD, the two should end up explainable in one sentence.
- **Zero existing tests.** All six suites grep clean for `QuickFilter`, `SearchByName`, `DefaultColumns` and
  `VisibleColumns`. The predicate is pure and trivially testable; this is the easiest part of the work.

---

# Issue #4 — a record of members removed from a group

> When deleting user or users from a group, log who was deleted so they can be easily re-added if needed

## Ten paths, four primitives

Membership removal happens in ten places, all bottoming out in four calls:

| Primitive | Holds |
|---|---|
| `DirectoryService.RemoveMembersAsync` | group DN + member DNs |
| `ChangeOp.RemoveFromGroups` → `ModifyGroupMembership` | member DN + group DNs |
| `GraphService.RemoveMemberFromGroupAsync` | two GUIDs |
| `ExchangeService.RemoveDistributionGroupMemberAsync` | SMTP addresses |

**Every path already holds both identifiers at the moment of the write.** Nothing needs an extra directory
read — which makes this much cheaper than it first appears.

Split by how they are triggered:

- **User-initiated, on screen:** AD *Members* tab, AD *Member Of* tab (which fans out to AD + Graph +
  Exchange behind one button), the cloud *Members* tab, the distribution-group members window, the cloud
  *Member Of* tabs.
- **Batch / off-screen:** Bulk Edit's remove-from-groups (M targets × N groups — and its confirmation names
  *neither* side), and four scenario steps.

## What is logged today is unusable

Every existing log line drops exactly one of the two sides:

| Where | What it writes |
|---|---|
| `DirectoryService:899` | `Removed 3 member(s) from group <groupDN>` — group named, members a **count** |
| `DirectoryService:479` | `Applied 1 change(s) to <memberDN>: Remove from 3 group(s)` — member named, groups a **count** |
| `GraphService:503` | Both GUIDs, **no names** |
| Exchange | **Nothing at all** on success |

There is no undo or re-add affordance anywhere in the app;
[`MainViewModel.cs:1046`](../app/src/UnifiedDirectoryManager/ViewModels/MainViewModel.cs#L1046) says so outright.

## Follow the precedent that already exists

[`GroupDeletionRecord.cs`](../app/src/UnifiedDirectoryManager/Services/GroupDeletionRecord.cs), added in
2.2.0, is the right model: a `.txt` plus a members `.csv` in the configurable operation-log folder, with
DN-valued entries written as `display name  —  distinguished name` for the recorded reason that **a display
name alone cannot be restored**.

The convention to follow is `<friendly name>  —  <the identifier that backend's re-add call consumes>`:
DN for AD, object id for Entra, SMTP for Exchange. The scenario runner already does exactly this in three
places; the gap is that non-scenario removals do not.

## The dependency the issue does not mention

**Nothing in the app can consume a recorded DN.** The object picker searches only `cn`, `sAMAccountName` and
`displayName`; `PastedMemberParser.Classify` has no DN shape at all, so a pasted DN misparses as
*"Last, First"* and resolves to nothing.

So a record alone satisfies *"log who was deleted"* but **not** *"so they can be easily re-added"* — that
still means a human retyping each name.

Closing it is small: a **DN rung in the paste parser**, so a recorded `.csv` column can be pasted straight
back into **Paste a list…**. It also retroactively makes every 2.2.0 group-deletion CSV directly re-addable.
Locked as D9.

## Proposed shape

A record file per removal operation, in the group-delete style, plus a log line that names **both** sides
instead of one side and a count.

Failure policy is D7: **warn and proceed**, deliberately inverting the delete record's *no record, no
delete*. Scope is D6: the user-initiated surfaces, leaving the scenario steps alone because they already
record this and leaving Bulk Edit to its own issue.

## Sharp edges

- **Idempotent no-ops look identical to real removals.** Removing someone who was already not a member
  succeeds. A record that lists them implies something happened. Worth recording only what actually changed,
  which means comparing against the read-back.
- **The `Truncated` / `Unconfirmed` membership distinction already exists** and must be honoured — LDAP
  cannot distinguish "no members" from "you may not read the members".
- **Two scenario steps disagree with each other** — `RemoveAllGroups` records a full DN,
  `RemoveFromGroups` records only an RDN. Worth aligning while here.
- **An Exchange member with no primary SMTP falls back to `Name`**, which is not a re-addable identifier.
  The record should say so rather than writing a name that looks like an address.
- **No test covers any removal path today.**

---

# Sequencing

Independent; #5 is the smaller and lower-risk of the two.

1. **#5 — decouple the search set from the columns.** Fixed field set on the row, predicate stops reading
   `row.Values`. Closes the issue's actual complaint on its own. Tests for the predicate.
2. **#5 — graded matching.** Word-boundary prefix, substring fallback, optional typo rung. Precompute
   lowercase at load; debounce the box.
3. **#5 — the pickers.** Swap three medial wildcards for ANR + the two attributes it misses.
4. **#4 — the record writer**, following `GroupDeletionRecord`, wired to the user-initiated paths.
5. **#4 — the log lines**, naming both sides.
6. **#4 — the DN rung in the paste parser**, if in scope, which is what turns the record into a re-add.
