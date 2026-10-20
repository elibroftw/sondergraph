---
id: 0006
title: Trace view — per-contact dual pane
status: todo
priority: medium
assignee: elopez
labels: [ui, notes, integrations]
blocked_by: [0003, 0009]
created: 2026-10-19
updated: 2026-10-19
---

## Context

DESIGN.md §1 feature #3 and §6.1. Clicking a contact opens their social media on one side
and your notes/personal info on the other. Bonus: integrate with Notesnook, Apple Notes,
etc.

The core risk is making an external app the only copy of the user's notes. The rule is
import/export, never delegation.

## Acceptance criteria

- [ ] Canonical notes are plain markdown in `data/people/<id>/notes/`. Always available,
      no integration required, works offline.
- [ ] Dual-pane view: contact's social activity on one side, notes + personal info on the
      other.
- [ ] At least one external adapter implemented end to end.
- [ ] External adapters are one-way import/export unless round-trip safety is proven.
- [ ] No external app is ever the sole store. If an adapter is removed, notes survive in
      markdown.
- [ ] Note edits are atomic (0002) and never leave a partially written file.

## Notes

Adapter options and their tradeoffs:

- **Obsidian / Logseq** — point at a vault directory. Cheapest and safest, because these
  are already just markdown on disk.
- **Notesnook** — needs its local API/export surface confirmed viable. Blocked on research.
- **Apple Notes** — AppleScript is the least fragile path; the SQLite store is undocumented
  and changes between macOS releases. Treat as read-mostly.

Sequencing note: the markdown path should ship and be useful *before* any adapter, so the
feature has a floor that cannot break.
