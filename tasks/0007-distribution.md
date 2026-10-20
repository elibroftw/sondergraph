---
id: 0007
title: Distribution — single-file publish, Docker, desktop shell
status: pending
priority: medium
labels: [packaging, distribution, desktop]
dependencies: [0001]
created: 2026-10-19
---

## Context

DESIGN.md §9. Three targets: local dev, Docker (`docker run`), and a desktop app.

**Decisions already taken** (see `docs/architecture-precedents.md` §5-6):

- **Self-contained single-file**, reversing the original "require the .NET runtime" plan.
  ~80-110 MB, but no install step and no ARM64-vs-x64 runtime confusion. The cost: no
  roll-forward to .NET security patches, so we must ship patch releases.
- **Photino.NET** for the desktop shell, which hosts Kestrel in the same process and thus
  removes the API-sidecar-lifecycle problem class entirely.
- **Native AOT deferred** to a separate headless profile.

**What the desktop shell is not:** it is a webview that talks to the API over loopback HTTP
(or a socket). It does **not** reference `Core`, and it does **not** invoke the CLI as a
subprocess. See DESIGN.md §2.1. Getting this wrong — shelling out to `sg` and parsing its
output — is the Option C failure mode ADR 0001 rejects.

What remains is execution: produce the builds and verify the numbers on this codebase.

## Acceptance criteria

- [x] Decision recorded on framework-dependent vs self-contained vs AOT.
- [ ] **Actual measured** binary sizes and cold-start times for this app (published
      benchmarks are not measurements — we need our own numbers).
- [ ] Verified whether trimming/AOT breaks minimal APIs, EF Core (if used), or
      `System.Text.Json` in this codebase specifically.
- [ ] `dotnet publish` produces a single-file CLI binary.
- [ ] Dockerfile runs the API with `data/` as a mounted volume.
- [ ] **Locking verified across the container boundary** — file locks behave differently
      through a bind mount and this interacts directly with 0002.
- [x] Desktop shell chosen and justified (Photino.NET; in-process host, no API sidecar).
- [ ] Photino.NET shell actually builds and launches the API in-process.
- [ ] The shell makes **no reference to `Core`** and never spawns `sg`; a test asserts this.
- [ ] Documented runtime prerequisite, stated plainly in the README.

## Notes

**Wording: "sidecar" always means the API server process, never the CLI.**

**If Tauri is ever revisited** (e.g. we want its updater/plugin ecosystem), these are the
known traps from the research: API sidecars are copied next to the main exe with the
`-<target-triple>` suffix stripped; resolution is relative to `current_exe()`, not the
config file; **killing sidecars on exit is not automatic** (the classic orphaned-backend
bug); and every executable in a macOS `Contents/MacOS/` must be signed and notarized —
test notarization early, not the week before release.

The Docker + locking interaction is the subtle one. Verify it early; discovering a
locking bug only in the container path is expensive. Note also that flock is unreliable on
network filesystems, which includes some Docker volume drivers on macOS/Windows.

Linux WebKitGTK (`libwebkit2gtk-4.1`) is a real package dependency and the main Linux
packaging risk for Photino. Budget time for it.
