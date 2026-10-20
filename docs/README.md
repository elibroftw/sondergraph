# docs/

Supporting documents for sondergraph. `DESIGN.md` is the primary design; these are the
deeper dives behind its open questions.

| File | Covers |
|---|---|
| `markdown-trackers.md` | Evaluation of existing markdown-backed task trackers (task 0010) |
| `architecture-precedents.md` | CLI/core/backend layering, local server conventions, locking, packaging (addendum §B) |
| `decisions/` | ADRs — one file per settled decision, immutable once accepted |

## ADR convention

Design decisions that are expensive to reverse get a numbered file in `decisions/`:

```
decisions/0001-shared-core-library.md
```

Each records: **Context**, **Decision**, **Consequences**, **Alternatives considered**.
Once accepted, an ADR is not edited — a superseding ADR is written instead. The value of
an ADR is the record of what was believed at the time, so editing one destroys the point.
