# ADR 0001 — Shared core library (not CLI-calls-API, not API-calls-CLI)

**Status:** Accepted
**Date:** 2026-10-19
**Task:** 0001

## Context

sondergraph must work fully headless from a terminal, work with a Web UI talking to a local
ASP.NET backend, and later ship as a native desktop app. Three layering options were
considered (DESIGN.md §3):

- **A.** Shared core library; CLI and API both reference it, neither calls the other.
- **B.** API is the only implementer; CLI is a thin HTTP client of a locally spawned server.
- **C.** CLI is the only implementer; the API shells out to the CLI and parses its output.

## Decision

**Option A — shared core library.**

`Sondergraph.Core` holds all behaviour. `Sondergraph.Cli` and `Sondergraph.Api` are thin
adapters: argument parsing and terminal rendering on one side, HTTP route mapping on the
other. Neither references the other.

## Consequences

**Good:**

- One implementation of every behaviour, so the CLI and UI cannot drift apart in substance.
- No serialization boundary to design or maintain.
- CLI startup never pays for a server; the tool stays usable in pipes and scripts.
- Progress reporting and structured errors work in-process via callbacks and result types.
- Core is tested once and both shells inherit the coverage.

**Costs:**

- Requires ongoing discipline to keep the shells thin. This is enforced by a test asserting
  `Sondergraph.Core` has no reference to `Cli` or `Api`, and by keeping Core's public surface
  result-shaped rather than exception-driven.
- Genuinely interactive behaviour (confirmation prompts) must be modelled as a Core-level
  abstraction instead of being written inline. This is design work that B and C would defer,
  and deferring it is the trap.

## Alternatives considered

**Option B (CLI as HTTP client).** Guarantees one code path, but makes the CLI unusable
without a running server, adds daemon lifecycle management, and imports local-server auth
and port problems into the terminal case where they buy nothing. Rejected.

**Option C (API shells out to CLI).** Requires parsing human-oriented stdout, which loses
structured errors and typed results and makes progress reporting a scraping problem.
Actively harmful. Rejected.

## Notes

Option B becomes more attractive if a future feature requires a resident process — live
streaming from a platform push API, or background sync while the UI is closed. That
migration is additive from A: the CLI keeps calling Core, and Core gains an event source.
Choosing A does not foreclose B, which is part of why A is the right starting point.
