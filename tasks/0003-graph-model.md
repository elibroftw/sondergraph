---
id: 0003
title: Person model and on-disk format
status: completed
priority: critical
labels: [core, data-model]
dependencies: [0001, 0002]
created: 2026-10-19
completed_at: 2026-10-19
---

## Context

DESIGN.md §4. This defines the contract every other feature builds on: people as nodes,
relationships with tier weights, assembled into a graph **in memory on demand**.

Storage is settled (DESIGN.md §4.2): **`people/` is markdown files; there is no database
and no persisted graph index.** This task implements that rather than re-deciding it.

Remaining open question that must be settled here: person identity resolution —
automatic matching vs confirmed.

## Acceptance criteria

- [x] `Person` type: id, name, aliases, tier, locations, tags, accounts.
- [x] Read/write `data/people/<id>/person.md` with YAML frontmatter.
- [x] Platform handles stored in `person.md` frontmatter under `accounts:` — **not** a
      separate `accounts.json`.
- [x] **Ingest merges into the existing `accounts` list** rather than rewriting the file,
      so a hand-added alias or location is never lost.
- [x] Graph assembled in memory from files on demand; **no `graph.json`**.
- [x] Merge (`graft`) is non-destructive and logged to append-only `merges.json`.
- [x] Merge is reversible via `sg ungraft <merge-id>`.
- [x] Merge appears in `git diff` — the absorbed record is not deleted silently.
- [x] Field-level conflict rules: per-field winner, union for list fields (aliases,
      locations, accounts).
- [x] Ingested duplicate accounts are surfaced as candidates, **never** auto-merged.
- [x] A test asserting round-trip: write a person, read it back, get an equal object.

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
