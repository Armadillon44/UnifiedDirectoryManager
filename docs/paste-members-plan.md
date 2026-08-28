# Batch add members by pasting a list

Status: **agreed, not started.** Branch: to follow `release/2.3.0`.

Add members to a group of **any** kind by pasting a list of people, resolving each line to a real directory
principal, and letting the operator settle anything ambiguous before a single confirmed write.

Today every membership editor requires picking people one search at a time. The common real task — "here are
the 40 people from the onboarding sheet, put them in this group" — is 40 searches.

---

## Locked decisions

**D1 — the paste is mixed, and the parser has to cope.** All four shapes arrive in practice and can be mixed
in one paste, one per line:

| Shape | Example | Detected by |
|---|---|---|
| UPN / SMTP | `jane.doe@lacrossefootwear.com` | contains `@` |
| "Last, First" | `Doe, Jane` | contains `,` and no `@` |
| sAMAccountName / alias | `jdoe` | single token, no space, no `@` |
| Display name | `Jane Doe` | anything else |

Detection picks the ladder's **starting rung**, never the answer. A line that looks like an alias is still
tried as a display name if the alias misses. Quotes, stray trailing commas and semicolons, and the leading
`•`/`-` of a bulleted paste are stripped before detection. A blank line is skipped, not an error.

**D2 — one review grid, not a chain of prompts.** Every pasted line becomes a row. The operator resolves the
unclear ones in any order, sees the whole batch before committing, and adds them with one button. A prompt per
ambiguity blocks on each one and hides the shape of the batch until the end — and the shape is the thing worth
seeing before writing to a group.

**D3 — one group per paste.** The dialog opens from a specific group's membership editor and adds to that
group only. Multi-group targeting multiplies the failure modes and the report, for a task that is already the
uncommon one.

**D4 — the paste is capped at 100 lines.** A realistic largest paste. Beyond it the dialog says so and takes
the first 100 rather than silently truncating or running into the 90-second op timeout, which kills the pwsh
host and takes every open Exchange session with it (G1).

**D5 — add only.** Removing several members at once already works in the membership editors, so removal is out
of scope for this pass. It is also the more destructive direction and deserves its own confirmation design
rather than inheriting this one.

**D6 — only an EXACT match resolves itself; a fuzzy match always asks.** A search that happens to return one
candidate is still a guess about a half-specified name. Rungs 1-3 can resolve a row on their own because they
matched an identifier or a whole name; rung 4 always lands in *Choose*, even with a single hit.

---

## The resolution ladder

Stops at the first **unambiguous** hit. Never guesses.

1. Exact **UPN** / **primary SMTP** / proxy address
2. Exact **sAMAccountName** (AD) or **alias** (Exchange)
3. Exact **display name**, *only when it matches exactly one principal*
4. Otherwise **search** — ANR for AD and Exchange, `$search` for Graph — and offer the candidates

Rung 3 is the one that has to stay strict. Two people called John Smith is the normal case, not the edge one,
and adding the wrong one to a group has no undo: there is no recycle bin for membership, and the audit trail
records it as a deliberate act by the operator.

**Resolution targets the group's own backend**, because membership does:

| Group kind | Members resolved against |
|---|---|
| On-prem AD | LDAP (`DirectoryService.SearchAsync`) |
| Entra security / Microsoft 365 | Graph (`$search`, ConsistencyLevel: eventual) |
| Distribution list / mail-enabled security | Exchange (`SearchRecipientsAsync`, ANR) |

A cloud-only user cannot join an on-prem group, and a user with no mailbox cannot join a distribution list.
Those are **per-row failures with a reason**, not a failed batch.

---

## The review grid

One row per pasted line, each in exactly one state:

| State | Meaning | Action available |
|---|---|---|
| **Resolved** | One unambiguous match | — |
| **Choose** | Several candidates | Pick one, or search again with a different term |
| **Not found** | No candidate | Edit the term and retry, or drop the row |
| **Already a member** | Resolved, and in the group | Skipped on write, not an error |
| **Can't be added** | Resolved but the wrong kind for this group | Reason shown; skipped on write |
| **Duplicate** | Same principal as an earlier row | Collapsed |

The confirm names the count and lists everyone being added. Adding 40 people to a group is not reversible in
one action, so the last thing before the write is the full list, not a number.

---

## Pre-flight, before anything is written

- **A synced group refuses the whole operation up front.** Exchange rejects every write against a synced
  object, and on-prem group membership is mastered in AD. The existing membership editors already gate this.
- Existing membership is read **once** and used for the *Already a member* state, so the write does not
  discover it one failure at a time.
- Duplicate lines collapse to one principal.

## Scale

Exchange resolution is serialised behind one semaphore, so 300 pasted names is 300 sequential round trips.
Exact matches are resolved in bulk where the API allows, repeats are cached within the paste, and the paste is
**capped with a clear message** rather than allowed to run into the 90-second op timeout — which kills the
host process and takes every open Exchange session with it (G1).

---

## Sequencing

1. **The parser and the resolver.** Pure logic, no UI: line → detected shape → ladder → result. Testable
   without a tenant, and where the subtle bugs live.
2. **The review grid** dialog and its view model, wired to one backend (Exchange first — it has the strictest
   rules and the most traps).
3. **The other two backends** behind the same interface, and the dialog wired into all three membership
   editors.
4. **Bulk add + per-item report**, reusing `BulkResult` / `ShowBulkResult`.

## Sharp edges already known

- **An Exchange `Get-` with a non-existent identity returns EVERY object.** Any resolution that goes through
  Exchange must verify the answer carries the identifier that was asked for (`__isWanted`).
- **A display name is not an identity.** It is what an operator pastes and what Exchange returns, but a write
  needs an address or a DN. The resolved row carries both.
- **Ambiguity must never resolve itself.** The failure mode is silent and lands on a real person.
- **Partial success is the normal outcome** of a batch. The report is per row, and says which ones did not go
  in and why.
