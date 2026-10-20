---
id: 0007
title: Distribution — single-file publish, Docker, desktop shell
status: todo
priority: medium
assignee: elopez
labels: [packaging, distribution, desktop]
blocked_by: [0001]
created: 2026-10-19
updated: 2026-10-19
---

## Context

DESIGN.md §9. Three targets: local dev, Docker (`docker run`), and a desktop app.

The stated approach is to require the .NET runtime rather than ship a self-contained
bundle, to avoid a large download. That is a real tradeoff worth confirming with numbers
before committing, and AOT in particular has known constraints against ASP.NET Core
minimal APIs and reflection-based `System.Text.Json`.

## Acceptance criteria

- [ ] Decision recorded on framework-dependent vs self-contained vs AOT, with **actual
      measured** binary sizes and cold-start times for this app (not general claims).
- [ ] Verified whether trimming/AOT breaks minimal APIs, EF Core (if used), or
      `System.Text.Json` in this codebase specifically.
- [ ] `dotnet publish` produces a single-file CLI binary.
- [ ] Dockerfile runs the API with `data/` as a mounted volume.
- [ ] **Locking verified across the container boundary** — file locks behave differently
      through a bind mount and this interacts directly with 0002.
- [ ] Desktop shell chosen and justified; it supervises the .NET backend as a sidecar.
- [ ] Documented runtime prerequisite, stated plainly in the README.

## Notes

Desktop shell candidates: Tauri (sidecar mechanism, small bundles), Photino, .NET MAUI
Blazor Hybrid, WebView2/WKWebView wrappers. The open question is how well each supervises
and shuts down a .NET sidecar process — orphaned backend processes are the classic failure
mode here.

The Docker + locking interaction is the subtle one. Verify it early; discovering a
locking bug only in the container path is expensive.
