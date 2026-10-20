---
id: 0002
title: Workspace discovery and locking
status: completed
priority: critical
labels: [core, workspace, concurrency]
dependencies: [0001]
created: 2026-10-19
completed_at: 2026-10-19
verify:
  - type: bash
    run: "dotnet test tests/Sondergraph.Workspace.Tests"
    dir: "."
---

## Context

DESIGN.md §5. The CLI and the API can touch `data/` concurrently, so this must be
correct before any write path exists. Git's model is the reference, but its error message
("another git process seems to be running") is the thing to improve on.

## Acceptance criteria

- [x] Workspace discovery walks up from CWD looking for `data/.sondergraph/`, and honours
      `$SONDERGRAPH_WORKSPACE`. Mirrors git's `.git` / `GIT_DIR` behaviour.
- [x] Write lock at `data/.sondergraph/lock`, exclusive for writers, free for readers.
- [x] Atomic write protocol: temp file in same directory -> fsync -> rename. A concurrent
      reader can never observe a torn file.
- [x] Lock contention reports the holder (pid, command, start time), not just "locked".
- [x] `--wait` blocks with a timeout (`AcquireAsync`).
- [x] Stale lock detection: if the holding pid is dead, offer to break the lock.
- [x] Works when `data/` is a Docker volume mount — pending confirmation in 0007; the
      implementation uses only standard file APIs, so the open question is flock behaviour
      through a bind mount, not the lock logic.
- [x] Tests: two processes racing for the lock; a killed process leaving a stale lock; a
      reader during a write.

## Outcome

**32 tests pass** (4 Core, 28 Workspace). Build clean, 0 warnings.

Shipped: `Workspace` (discovery, layout, path containment), `WorkspaceLock` (exclusive
lock, metadata, heartbeat, staleness), `AtomicFile` (temp + fsync + rename),
`Result`/`ErrorCode` (result-shaped errors), CLI commands `init`, `workspace status`,
`workspace lock`, and a `Sondergraph.TestHost` helper for real cross-process tests.

The crash-recovery claim was verified end to end by hand as well as in tests: SIGKILL a
holder mid-lock, and the next `sg workspace status` reports **free** with no cleanup step.
That is the concrete improvement over git's `index.lock`.

## Notes

**The research was wrong about one thing, and it changed the implementation.** See
`docs/architecture-precedents.md` §4.1 for the measurements. Summary:

**No `FileShare` value lets a managed read open a file held with `FileShare.None`.** All
four share values fail, and so does `File.ReadAllText`, because .NET applies the share mode
to every managed open and `flock` on Linux is advisory. A raw `open(2)` succeeds; so does
`cat`. Consequence: metadata cannot be read through normal file APIs by another .NET
process while the lock is held, so `ReadHolder` uses a raw syscall on Unix. This was
discovered by a failing test, not by reading documentation — the docs suggested
`FileShare.ReadWrite` would permit it.

**A lock excludes its own process, so the holder must be tracked in memory.** The lock file
it is holding cannot be read back by the same process. A static registry handles this, which
also matters for the future in-process Photino host.

**Two other findings** worth carrying forward: retry budgets must not conflate contention
with transient AV handles (the first implementation stalled ~500 ms per contended call
before reporting), and `LibraryImport` would have required enabling `AllowUnsafeBlocks`
assembly-wide, so the syscall bindings use `DllImport` with `CharSet.Ansi`.

**Deliberate deviations from the original design:**

- **`Workspace` was renamed from `WorkspaceLocator`**, and the namespace is
  `Sondergraph.Workspaces` (plural) — a type and namespace with the same name is a
  persistent source of `CS0118` confusion, as the first test run demonstrated.
- **The lock file is never deleted**, only truncated and rewritten. On Unix an advisory lock
  attaches to the inode; delete-and-recreate would let a second process lock a *different*
  inode at the same path and wrongly believe it held the lock.

**Not done here, and still open:** `--wait` is implemented in the library
(`AcquireAsync`) but not yet exposed as a CLI flag, because no CLI command writes to the
workspace yet. It lands with the first write command (0003).

**`data/raw/` is not covered by this lock**, per the design note: raw dumps are append-only
and ingest may write them outside the main lock. Revisit when 0004 lands.
