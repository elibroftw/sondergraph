---
id: 0003
title: Person model and on-disk format
status: pending
priority: critical
labels: [core, data-model]
dependencies: [0001, 0002]
created: 2026-10-19
---

## Context

DESIGN.md §4. This defines the contract every other feature builds on: people as nodes,
relationships with tier weights, assembled into a graph **in memory on demand**.

Storage is settled (DESIGN.md §4.2): **`people/` is markdown files; there is no database
and no persisted graph index.** This task implements that rather than re-deciding it.

Remaining open question that must be settled here: person identity resolution —
automatic matching vs confirmed.

## Acceptance criteria

- [ ] `Person` type: id, name, aliases, tier, locations, tags, accounts.
- [ ] Read/write `data/people/<id>/person.md` with YAML frontmatter.
- [ ] Platform handles stored in `person.md` frontmatter under `accounts:` — **not** a
      separate `accounts.json`.
- [ ] **Ingest merges into the existing `accounts` list** rather than rewriting the file,
      so a hand-added alias or location is never lost.
- [ ] Graph assembled in memory from files on demand; **no `graph.json`**.
- [ ] Merge (`graft`) is non-destructive and logged to append-only `merges.json`.
- [ ] Merge is reversible via `sg ungraft <merge-id>`.
- [ ] Merge appears in `git diff` — the absorbed record is not deleted silently.
- [ ] Field-level conflict rules: per-field winner, union for list fields (aliases,
      locations, accounts).
- [ ] Ingested duplicate accounts are surfaced as candidates, **never** auto-merged.
- [ ] A test asserting round-trip: write a person, read it back, get an equal object.

## Notes

**Auto-merge that guesses wrong is worse than no auto-merge.** Surface candidates and
require confirmation.

**No database, and no index.** The graph is built in memory per command; process lifetime
is the cache. At realistic contact counts (hundreds to low thousands) this is milliseconds,
and it removes the entire class of stale-index bugs. If `raw/` scanning ever becomes a
measured bottleneck, a *disposable* SQLite cache beside `raw/` is permitted — but only
under the rule in DESIGN.md §4.2: fully rebuildable, with a test proving identical results
after delete-and-rebuild.

Do not add a database "for performance" without a measurement. The predicted bottleneck is
`raw/` post volume, not person records.
