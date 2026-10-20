# docs/

Supporting documents for sondergraph. `DESIGN.md` is the primary design; these are the
deeper dives behind its open questions.

| File | Covers |
|---|---|
| `markdown-trackers.md` | Evaluation of markdown-backed task trackers. **Outcome: adopt `taskmd`** (task 0010) |
| `architecture-precedents.md` | CLI/core/backend layering, local IPC, locking, deployment, desktop packaging (addendum §B) |
| `decisions/` | ADRs — one file per settled decision, immutable once accepted |

## Resolved so far

| Question | Answer | Where |
|---|---|---|
| CLI/backend layering | Shared core library (Option A) | [ADR 0001](decisions/0001-shared-core-library.md) |
| Core language | **C#, not Rust** | [ADR 0002](decisions/0002-csharp-core-not-rust.md) |
| Storage | Files, not a database; no graph index | [DESIGN.md](../DESIGN.md) §4.2 |
| Task tracking format | `taskmd` spec v1.2 | [markdown-trackers.md](markdown-trackers.md) |
| Desktop shell | Photino.NET (Tauri runner-up) | [architecture-precedents.md](architecture-precedents.md) §6 |
| Deployment mode | Self-contained single-file (reverses original plan) | [architecture-precedents.md](architecture-precedents.md) §5 |
| Dynamic port | `Listen(IPAddress.Loopback, 0)`; `ListenLocalhost(0)` throws | [architecture-precedents.md](architecture-precedents.md) §3 |

## ADR convention

Design decisions that are expensive to reverse get a numbered file in `decisions/`:

```
decisions/0001-shared-core-library.md
```

Each records: **Context**, **Decision**, **Consequences**, **Alternatives considered**.
Once accepted, an ADR is not edited — a superseding ADR is written instead. The value of
an ADR is the record of what was believed at the time, so editing one destroys the point.
