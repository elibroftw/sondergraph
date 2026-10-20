---
id: 0008
title: Adjacent graph features
status: pending
priority: low
labels: [core, graph, features]
dependencies: [0003, 0009]
created: 2026-10-19
---

## Context

DESIGN.md §1 and §4.1. These are the features that justify the graph model: they are
one-liners over a graph and near-impossible over a contact table.

## Acceptance criteria

- [ ] Shared-neighbour detection: "who in my contacts knows each other but I've never
      introduced them?" (triangle query where the ego-edge is absent).
- [ ] Staleness / decay: "who haven't I spoken to in a year?" (edge-age filter).
- [ ] Shortest path between two contacts.
- [ ] Location history per contact.
- [ ] Tier management: friend / close / best, as edge weights.
- [ ] Each feature reachable from both CLI and API — parity is a tracked goal
      (DESIGN.md §8).

## Notes

**These are the tests of the data model.** If any of them requires a schema change or a
scan of the whole dataset, the graph model in 0003 is wrong and should be fixed there
rather than worked around here.

Decay needs a definition before it can be implemented: is it time since last interaction,
time since last ingest, or an explicit user-set cadence per contact? Surface the choice
when this task starts.
