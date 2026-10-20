---
id: 0002
title: Workspace discovery and locking
status: pending
priority: critical
labels: [core, workspace, concurrency]
dependencies: [0001]
created: 2026-10-19
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

**Primitive decision:** take flock on an **open fd** (Unix) / `FileShare.None` on a
long-lived stream (Windows), not lockfile existence. The OS releases it on process death,
so a crash cannot leave a stale lock — a deliberate improvement over git's `index.lock`,
which has no staleness expiry and tells users to `rm` it by hand.

Gotchas to design around, from `docs/architecture-precedents.md` §4:

- **On Unix only `FileShare.None` is meaningful** — every other share value maps to a
  *shared* flock, so multiple writers would succeed.
- **`File.Move`/`File.Delete` ignore advisory locks on Unix.** Lock a stable file we never
  rename, or re-stat the fd after acquiring and retry on inode mismatch.
- **`FileStream.Lock` is unsupported on macOS.** Do not use it.
- **Atomic rename is same-volume only and not durable without fsync.** Use
  `Flush(flushToDisk: true)` for critical manifests.
- **Windows:** contention throws `IOException` (0x80070020) — translate it. Antivirus and
  indexers hold transient handles, so retry 3-5x with 50-250 ms backoff.
- **Network filesystems** (SMB/NFS/Dropbox/iCloud) have unreliable flock. Detect and warn;
  consider single-instance-only mode for such paths.

Put lock metadata *in* the lock file (`pid`, `host`, `user`, `startedAt`, `command`,
`heartbeatAt`) and heartbeat it. Reporting **who holds the lock and since when** is the
single change that fixes most of the `index.lock` frustration.

Never hold the lock across user interaction.

Do not put the lock inside `data/raw/`; raw dumps are append-only and may be written by
ingest outside the main lock (to be decided when 0004 lands).
