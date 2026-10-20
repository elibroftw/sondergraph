---
id: 0002
title: Workspace discovery and locking
status: todo
priority: critical
assignee: elopez
labels: [core, workspace, concurrency]
blocked_by: [0001]
created: 2026-10-19
updated: 2026-10-19
---

## Context

DESIGN.md §5. The CLI and the API can touch `data/` concurrently, so this must be
correct before any write path exists. Git's model is the reference, but its error message
("another git process seems to be running") is the thing to improve on.

## Acceptance criteria

- [ ] Workspace discovery walks up from CWD looking for `data/.sondergraph/`, and honours
      `$SONDERGRAPH_WORKSPACE`. Mirrors git's `.git` / `GIT_DIR` behaviour.
- [ ] Write lock at `data/.sondergraph/lock`, exclusive for writers, free for readers.
- [ ] Atomic write protocol: temp file in same directory -> fsync -> rename. A concurrent
      reader can never observe a torn file.
- [ ] Lock contention reports the holder (pid, command, start time), not just "locked".
- [ ] `--wait` blocks with a timeout.
- [ ] Stale lock detection: if the holding pid is dead, offer to break the lock.
- [ ] Works when `data/` is a Docker volume mount (see 0007).
- [ ] Tests: two processes racing for the lock; a killed process leaving a stale lock; a
      reader during a write.

## Notes

Open: whether to use `FileStream` with `FileShare.None` or a separate lock file. The
cross-platform behaviour of advisory locking differs on Windows vs Linux/macOS and needs
confirming before committing — see the addendum in `docs/`.

Do not put the lock inside `data/raw/`; raw dumps are append-only and may be written by
ingest outside the main lock (to be decided when 0004 lands).
