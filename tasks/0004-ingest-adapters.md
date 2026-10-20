---
id: "0004"
title: Ingest pipeline and parser interface
status: pending
priority: high
labels: [ingest, adapters, parsers]
dependencies: ["0003"]
created: 2026-10-19
---

## Context

[ADR 0003](../../docs/decisions/0003-per-platform-parsers.md) and DESIGN.md §6.2.
Each platform gets its own parser, behind a shared three-stage pipeline:

```
Acquire (per-platform, impure) -> data/raw/<p>/ (append-only) -> Parse (per-platform, PURE)
                                                                        |
                                                              Materialise (shared) -> model
```

This task builds the **pipeline and the seam**. Populating it with a real platform is
sub-task 0011, which needs a real export.

## Acceptance criteria

**Pipeline (shared, platform-agnostic):**

- [ ] `ParsedRecord` family: `ObservedAccount`, `ObservedPost`, `ObservedInteraction`,
      each with `Platform`, `NativeId`, `SourcePath`, and an `Extra` bag.
- [ ] `IPlatformParser`: `Platform`, `ParserVersion`, `Parse(RawSource, ParseContext)`.
- [ ] `IAcquirer`: declared mechanism, auth requirements, rate limits.
- [ ] Parser registry — no dynamic assembly loading (ADR 0003).
- [ ] Raw dumps land in `data/raw/<platform>/`, append-only, never rewritten by parsing.
- [ ] Materialise stage: records -> merge **candidates**, never auto-merge (DESIGN.md §4.3).
- [ ] Progress reported via callback, not console writes (thin-shell rule, ADR 0001).
- [ ] Re-parsing the same raw input is idempotent: no duplicates, no drift.
- [ ] A malformed record is counted and skipped, not fatal (ADR 0003 parser rule 4).
- [ ] `sg ingest parse <platform>` reads existing raw data with **no network access**.
- [ ] Posture documented in the README.

**Proving the seam works:** the interface must be exercised by at least one real parser
(0011). A pipeline with no implementation is unverified design.

## Notes

**The `Extra` bag is deliberate and load-bearing.** Platform-neutral records tend toward one
of two failures: too thin (every parser invents downstream handling) or too fat (the union
of every platform's quirks baked into the core). `Extra` keeps the core clean with an
explicit escape hatch. Promoting a field from `Extra` to a real property should be
evidence-driven, not speculative.

**Parser rules from ADR 0003 are acceptance criteria, not guidelines:** never invent an id;
never rewrite raw; never resolve identity; never fail the whole parse for one bad record;
declare `ParserVersion`; no network, credentials, or clock.

**Which platform is first is still open** (DESIGN.md §10 Q1). Candidates: Mastodon (open
API, fastest to prove the pipeline), a GDPR/CCPA export bundle (user-owned by construction;
the schema-discovery work is the real task), Bluesky (open API).

Counter-argument to starting with Mastodon: its API is clean enough that it may not
stress-test the parser interface. A messy export is a harder and more informative test of
whether the seam holds.
