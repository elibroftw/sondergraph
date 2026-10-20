---
id: 0001
title: Scaffold the .NET solution and project skeleton
status: completed
priority: critical
labels: [infra, dotnet]
dependencies: []
created: 2026-10-19
completed_at: 2026-10-19
verify:
  - type: bash
    run: "dotnet build"
    dir: "."
  - type: bash
    run: "dotnet test"
    dir: "."
---

## Context

Nothing exists yet. Everything else is blocked on a compiling solution with the four
projects from DESIGN.md §2.2 and the layering rule enforced from day one.

The "thin shell" rule (Core must not know whether a CLI or an HTTP request is driving it)
is much cheaper to enforce now than to retrofit.

## Acceptance criteria

- [x] `Sondergraph.slnx` with `Sondergraph.Core`, `Sondergraph.Cli`, `Sondergraph.Api`,
      `Sondergraph.Workspace`, plus test projects.
- [x] `Sondergraph.Core` references neither `Cli` nor `Api`; a test asserts this so it
      cannot regress.
- [x] `sg --version` works from the CLI.
- [x] `GET /health` returns 200 from the API.
- [x] `dotnet build` clean on .NET 10 with warnings-as-errors.
- [x] `Directory.Build.props` centralizes TargetFramework, nullable, and analyzers.

## Outcome

Solution scaffolds and builds clean: **0 warnings, 0 errors**. 4 tests pass.

The reference graph is exactly as ADR 0001 requires — `Core` has **zero** outgoing
references, and both shells reference `Core` + `Workspace`.

## Notes

**The layering test was verified by deliberately breaking it**, not just by watching it
pass. A tripwire that has never fired is indistinguishable from one that cannot fire.

Two violation shapes were tested and they behave differently:

- **`Core` -> `Cli` project reference**: fails at *restore* with `error MSB4006: circular
  dependency in the target dependency graph`. The build cannot even start, so this class of
  violation is structurally impossible rather than merely detected. Stronger than expected.
- **`Core` -> ASP.NET package reference**: builds fine (no cycle) and the test catches it —
  `Sondergraph.Core must not reference a shell or web framework (ADR 0001), but references:
  Microsoft.AspNetCore.Http.Abstractions`. This is the case the test earns its keep on;
  reaching for a web or console framework is the realistic way `Core` stops being
  host-agnostic.

**Analyzer severity belongs in `.editorconfig`, not `NoWarn`.** `TreatWarningsAsErrors`
promotes warnings to errors, and `NoWarn` cannot suppress something already at error
severity. The first attempt set `NoWarn=CA1707` in `Directory.Build.props` and it silently
did nothing. Severities now live in `.editorconfig` under `[tests/**/*.cs]`.

Related: the xunit template does **not** set `IsTestProject`, so any conditional config
keyed on it would silently not apply. It is now set explicitly in the test csproj.

`SondergraphVersion.Protocol` is deliberately separate from `Version`. The CLI and API
version independently of the wire contract, and the handshake design
(`docs/architecture-precedents.md` §3) needs to detect a protocol mismatch and tell the
user to restart — which only works if the two are not conflated.

`WorkspaceLocator` is discovery only. It acquires no lock; locking, atomic writes and
stale-lock handling are **task 0002**. Do not write through it yet.

Out of scope here, as intended: locking primitives (0002) and single-file publish
settings (0007).
