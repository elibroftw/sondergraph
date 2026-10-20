---
id: 0001
title: Scaffold the .NET solution and project skeleton
status: pending
priority: critical
labels: [infra, dotnet]
dependencies: []
created: 2026-10-19
---

## Context

Nothing exists yet. Everything else is blocked on a compiling solution with the four
projects from DESIGN.md §2.2 and the layering rule enforced from day one.

The "thin shell" rule (Core must not know whether a CLI or an HTTP request is driving it)
is much cheaper to enforce now than to retrofit.

## Acceptance criteria

- [ ] `Sondergraph.sln` with `Sondergraph.Core`, `Sondergraph.Cli`, `Sondergraph.Api`,
      `Sondergraph.Workspace`, plus test projects.
- [ ] `Sondergraph.Core` references neither `Cli` nor `Api`; a test asserts this so it
      cannot regress.
- [ ] `sg --version` works from the CLI.
- [ ] `GET /health` returns 200 from the API.
- [ ] `dotnet build` clean on .NET 10 with warnings-as-errors.
- [ ] `Directory.Build.props` centralizes TargetFramework, nullable, and analyzers.

## Notes

Locking primitives and single-file publish settings are deliberately out of scope here —
they belong to 0005 and 0007. This task is only "it compiles and the layering holds."
