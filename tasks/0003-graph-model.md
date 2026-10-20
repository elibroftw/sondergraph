---
id: 0003
title: Person graph model and on-disk format
status: todo
priority: critical
assignee: elopez
labels: [core, data-model]
blocked_by: [0001, 0002]
created: 2026-10-19
updated: 2026-10-19
---

## Context

DESIGN.md §4. This defines the contract every other feature builds on: nodes as people,
edges as relationships with tier weights.

Two open questions in DESIGN.md §10 must be settled here: graph index format
(`graph.json` vs SQLite) and person identity resolution (automatic vs confirmed matching).

## Acceptance criteria

- [ ] `Person` node type: id, name, aliases, tier, locations, tags, accounts.
- [ ] Account-to-person mapping persisted in `data/people/<id>/accounts.json`.
- [ ] Graph index rebuildable from scratch — deleting it is always safe.
- [ ] Decision recorded on JSON vs SQLite, with reasoning, in `docs/`.
- [ ] Merge (`graft`) is non-destructive and logged to `merges.json`.
- [ ] Merge is reversible via `sg ungraft <merge-id>`.
- [ ] Field-level conflict rules: per-field winner, union for list fields (aliases,
      locations, accounts).
- [ ] Ingested duplicate accounts are surfaced as candidates, never auto-merged.

## Notes

**Auto-merge that guesses wrong is worse than no auto-merge.** Surfacing candidates and
requiring confirmation is the safer default; revisit only with evidence.

Leaning on the index question: JSON as source of truth (greppable, diff-friendly), SQLite
as a rebuildable query index if the triangle/decay queries in 0008 need it. Do not adopt
SQLite as truth — it would make the repo opaque and unmergeable.
