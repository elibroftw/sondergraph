# Architecture precedents & conventions (addendum §B)

**Task:** resolves DESIGN.md §10 open questions 4 (desktop shell) and hardens §3, §5, §9.
**Date:** 2026-10-19

This backs [ADR 0001](decisions/0001-shared-core-library.md) with evidence and settles the
mechanics DESIGN.md left as "TBD": local IPC, locking primitives, deployment mode, and the
desktop shell.

**Two claims in here were verified by running code on this machine** (.NET 10.0.400), not
read from docs. They are marked **[verified]**.

---

## 1. Verdicts

| Question | Answer |
|---|---|
| CLI/backend layering | **Option A** — shared core (ADR 0001). Confirmed by precedent. |
| Should there be a server at all? | **Yes, but as an optional shared host**, never a mandatory intermediary. This is Podman's hybrid, not Docker's. |
| Desktop shell | **Photino.NET** first choice, **Tauri** if the bundler/updater ecosystem is wanted. |
| Deployment | **Self-contained single-file**, not framework-dependent. This reverses the stated plan. |
| Native AOT | **Not now.** Optional headless "server profile" later. |
| Local IPC | Unix socket (macOS/Linux) / loopback TCP + token (Windows). |
| Dynamic port | `Listen(IPAddress.Loopback, 0)`; `ListenLocalhost(0)` **throws**. **[verified]** |

---

## 2. What real projects do

The single most useful finding: **the integration point follows from whether the tool has
long-lived state.**

| Shape | Integration point | Examples |
|---|---|---|
| Stateless per invocation | **CLI** or **core library** | git/libgit2, restic, borg, ripgrep, `gh`, SQLite |
| Long-lived stateful engine | **Local server** | Docker, Syncthing, Ollama, Podman service, Aspire |

sondergraph has a *workspace* (long-lived, lockable) and long-running jobs, so it belongs in
the second row — **but must stay fully usable headless in the first row's style.** That
combination is exactly the recommended shape.

### Precedents worth naming

- **Jujutsu (jj)** — the model for the core's discipline. Its architecture doc states the
  library "should avoid interacting directly with the user via the terminal" and "cannot
  read configuration from the user's home directory." That is the thin-shell rule, stated
  better than we did. jj also has **no first-party GUI**: `jj-lib` existed before any real
  consumer, and GUIs mostly went through the CLI or vendored the lib. Lesson: **don't
  extract a library "for a future GUI."** We aren't — we have two concrete consumers today.
- **Podman** — the target hybrid. Local CLI calls `libpod` **in-process**; remote clients
  use REST. Its daemonless rationale is directly relevant to us: Docker's daemon "controls
  multiple clients from interacting with container storage. While Podman uses the OS (file
  system locking) to allow multiple Podmans to use the storage at the same time."
  **That is our locking design, arrived at independently.**
- **Git's embedding guide** — the canonical rebuttal of Option C, from git itself: parsing
  "occasionally-changing output format... can be inefficient and error-prone."
- **restic / borg** — refuse both a library *and* a daemon; the binary plus repository locks
  *is* the interface. borg's repo-wide lock with `--lock-wait` is our contention model.
- **Syncthing** — the closest single-binary analogue: one binary, embedded web UI, REST API
  on one port, `X-API-Key` auth. The web UI is *just another REST client*.
- **`dotnet-ef`** — even a CLI-first .NET tool chose "load and call the app's code" over
  "parse output."
- **VS Code / Zed** — the litmus test: *anything touching the workspace runs in the server
  or CLI process; the webview only renders.*

---

## 3. Local IPC and the server handshake

### Transport

| Platform | Transport | Why |
|---|---|---|
| macOS / Linux | **Unix domain socket** in a per-user runtime dir | OS-level auth via filesystem permissions. No token in URLs, no DNS-rebinding surface, no port collisions. A browser can't reach it, so only the desktop shell can — ideal. |
| Windows | **Loopback TCP + bearer token** | WebView2 can't fetch over a named pipe without a proxy in the shell. |
| Headless / CI / "open in browser" | **Loopback TCP + token** | The only mode a browser can use. |

Make it a config value: `--listen unix:…`, `--listen pipe:…`, `--listen http://127.0.0.1:0`.
Kestrel speaks HTTP over all three (`ListenUnixSocket` since 2.1, `ListenNamedPipe` since 8).

**macOS caveat:** AF_UNIX paths are limited to 104 characters. Keep the socket path short.

### Dynamic port — [verified]

`ListenLocalhost(0)` **throws** on .NET 10:

```
System.InvalidOperationException: Dynamic port binding is not supported when binding to
localhost. You must either bind to 127.0.0.1:0 or [::1]:0, or both.
```

The working pattern, also verified end to end on .NET 10.0.400:

```csharp
b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
var app = b.Build();
await app.StartAsync();
var addrs = app.Services.GetRequiredService<IServer>()
                           .Features.Get<IServerAddressesFeature>()!;
// → http://127.0.0.1:44747
```

Read the port **after** `StartAsync`, not in a hosted service — startup ordering bites.

### Security layers (loopback is not private)

Any local process can reach a loopback HTTP server, and web pages get partial reach. DNS
rebinding is the concrete threat. Layers, in order:

1. **Bind loopback only.** Never `Any`; never `ListenLocalhost(0)`.
2. **Prefer a socket/pipe** where the client allows it — that removes the whole threat class.
3. **Per-run bearer token** — 32 random bytes, constant-time compare, stored `0600` in the
   handshake file. This is what kills DNS rebinding: a rebound request can't read the token.
4. **Host-header allowlist** as middleware *before* routing.
5. **Same-origin SPA.** Serve the Svelte bundle from the API's `wwwroot` so there is no CORS
   at all. This is what Syncthing and Tap Studio do, and it's strictly simpler.
6. **No cookie auth** for the local API — that keeps CSRF structurally impossible.
7. **Windows pipes:** `PipeOptions.CurrentUserOnly`; don't grant callers `CreateNewInstance`.

Ollama is the anti-example: no auth on localhost by default, and it warns about it.

### Handshake contract

Server prints one JSON object per line, flushed, on stdout:

```json
{"event":"server.listening","addr":"127.0.0.1:54123","pid":42,"token":"…","protocol":3}
{"event":"server.ready","workspace":"/x","elapsedMs":210}
{"event":"server.error","code":"workspace.locked","message":"…"}
```

The parent waits for `server.ready` with a timeout and surfaces `server.error` verbatim.
**Emitting `server.error` is the point** — a launch that fails can say how far it got,
instead of a blank window.

Handshake file at `<state-dir>/server.json`, written atomically:

```json
{ "pid": 4242, "url": "http://127.0.0.1:54123", "token": "…",
  "protocol": 3, "startedAt": "2026-10-19T23:01:02Z", "appVersion": "1.4.0" }
```

**Stale detection must not trust the PID alone** (PID reuse). Validate: handshake
`startedAt` ≥ the process's own start time, **and** a successful health probe returning the
same pid, **and** a matching protocol version. On mismatch, tell the user to restart.

---

## 4. Workspace locking — .NET reality check

The git-style design in DESIGN.md §5 is right, but the .NET primitives have sharp edges
that are not obvious from documentation.

### What git does, and where it hurts

Git uses `O_CREAT|O_EXCL` lockfiles plus atomic rename; **lockfiles block writers, readers
never block**. The failure mode is the problem: locks have **no staleness expiry**, so users
are told to `rm .git/index.lock` by hand. That is a bad experience to copy.

**The improvement to make:** put lock metadata *in* the lock file — `{pid, host, user,
startedAt, command, heartbeatAt}` — and on contention tell the user **who holds it and
since when**. That one change fixes most of the `index.lock` pain.

### The model to copy

**SQLite WAL** is the best mental model: readers don't block writers, one writer at a time,
`busy_timeout` (default 0 — you must set it; use 5000 ms), `BEGIN IMMEDIATE` for write
transactions to avoid upgrade deadlock. Combine with git's split:

- **immutable content-addressed artifacts** → temp + atomic rename, **no lock**
- **mutable indexes** → one lock, or a transaction
- **user-visible output** → temp + rename, never in-place

### .NET gotchas

1. **On Unix, only `FileShare.None` is meaningful.** Every other share value maps to a
   *shared* flock, so multiple writers succeed. If you need reader/writer separation, get it
   from SQLite/WAL, not from `FileShare`.
2. **`File.Move` and `File.Delete` ignore advisory locks on Unix.** A lock *file path* can be
   replaced underneath you. Lock a stable file you never rename, or re-stat the fd after
   acquiring and retry on inode mismatch.
3. **`FileStream.Lock` is unsupported on macOS** (`[UnsupportedOSPlatform("macos")]`). Do not
   rely on it.
4. **Prefer flock on an open fd over lockfile existence** — the OS releases it on death, so
   crashes don't leave stale locks. On Windows, `FileShare.None` on a long-lived stream has
   the same property.
5. **Atomic rename is atomic on the same volume only**, and **not durable without fsync**.
   `Flush(flushToDisk: true)` for critical manifests. Git fsyncs; so should we.
6. **Network filesystems (SMB/NFS/Dropbox/iCloud)** — flock is unreliable or unsupported.
   Both git and jj warn about corruption here. Detect and warn, and consider a
   single-instance-only mode for such paths.
7. **Windows:** `FileShare.None` contention throws `IOException` (0x80070020) — translate it.
   Antivirus/indexers hold transient handles, so retry 3–5 times with 50–250 ms backoff
   before reporting failure.
8. **Never hold the lock across user interaction.** A GUI modal while holding the lock hangs
   every CLI in the workspace.

### 4.1 Verified corrections from implementation

The following were established by **running code on this machine** (.NET 10.0.400, Linux)
during task 0002, and two of them contradict what the research above assumed.

**(a) No `FileShare` value lets a managed read open a file held with `FileShare.None`.**
Measured against a live held lock from a second process:

| Attempt | Result |
|---|---|
| `FileStream` + `FileShare.ReadWrite \| Delete` | `IOException` (sharing violation) |
| `FileStream` + `FileShare.Read` | `IOException` |
| `FileStream` + `FileShare.ReadWrite` | `IOException` |
| `FileStream` + `FileShare.None` | `IOException` |
| `File.ReadAllText` | `IOException` |
| raw POSIX `open(2)` + `read(2)` | **succeeds** |
| `cat` (different binary) | **succeeds** |

.NET applies the share mode to *every* managed open, and on Linux `FileShare.None` becomes
`flock(LOCK_EX)`. `flock` is advisory, so a process that never calls it — `cat`, or a raw
syscall — reads the bytes regardless. **Consequence:** lock metadata cannot be read by
another .NET process through the normal file APIs while the lock is held. Diagnostics in
`WorkspaceLock.ReadHolder` use a raw `open(2)`/`read(2)`/`close(2)` on Unix for exactly
this reason, falling back to `File.ReadAllText` on Windows (where share modes are mandatory
and the read may legitimately fail, degrading to a generic message).

**(b) An exclusive lock excludes its own process too.** `TryAcquire` twice in one process
fails the second time, as it should, but this also means the holder cannot read back its own
metadata. `WorkspaceLock` therefore keeps an in-process registry of the current holder.
This matters for a future in-process desktop host (Photino), where the API and any embedded
caller share a process.

**(c) Retry budgets must not conflate contention with transient handles.** The first
implementation retried 5× with backoff and then reported a generic error, because the
transient-handle retry (for Windows antivirus) and genuine lock contention were
indistinguishable before the attempt. Contention is now detected by the *outcome*, and the
non-waiting path retries only 3× with 20 ms steps — a caller that genuinely wants to wait
uses `AcquireAsync`, which polls with its own backoff. Otherwise every contended call would
stall for half a second before reporting.

**(d) `LibraryImport` requires `AllowUnsafeBlocks`.** Enabling unsafe code across the whole
assembly to read a diagnostic file is a bad trade, so the raw syscall bindings use
`DllImport` with `CharSet.Ansi` (UTF-8-compatible; `CharSet.Unicode` would produce UTF-16
and break non-ASCII paths).

---

## 5. Deployment: self-contained, not framework-dependent

**This reverses the plan in DESIGN.md §9.** The stated approach was to require users to
install the .NET runtime to avoid a large download. The evidence says ship self-contained.

| Mode | Runtime needed | Size | Cold start |
|---|---|---|---|
| Framework-dependent single-file | **Yes** | ~1–3 MB app | ~70–120 ms |
| **Self-contained single-file** | No | **~80–110 MB** | ~150–300 ms |
| + `PublishReadyToRun` | No | +30–40% | **faster** (less JIT) |
| + `PublishTrimmed` | No | ~50–70 MB | ~120–200 ms |
| Native AOT | No | ~10–22 MB | ~40–120 ms |

Official ASP.NET Core numbers (simple API): default 156 ms / 92.6 MB / 96 MB working set;
Native AOT 48 ms / 10.0 MB / 41 MB.

**Why self-contained wins for a desktop app:** framework-dependent pushes an install step
onto users, creates an ARM64-vs-x64 runtime choice on macOS/Linux, and produces a class of
"works on my machine" support tickets. The ~80 MB buys that away. The cost is real though:
self-contained **does not roll forward to .NET security patches**, so we must ship patch
releases.

Note the Svelte bundle is negligible next to this — 100–500 KB gzipped. Bundle size is
decided by the runtime, not the UI.

### Trimming and AOT constraints

- **`PublishTrimmed` silently disables reflection-based `System.Text.Json`** — you get
  `InvalidOperationException: Reflection-based serialization has been disabled`. Mitigate
  with `[JsonSerializable]` source-gen contexts.
- **EF Core Native AOT is officially experimental**: "recommend against deploying EF NativeAOT
  applications in production."
- **ASP.NET SPA middleware is unsupported under AOT.** We don't need it — serve static files
  and add a small fallback-to-`index.html` middleware.
- **`Assembly.Location` returns `""`** in single-file. Use `AppContext.BaseDirectory`.
- **The ASP.NET docs contradict themselves on minimal APIs and AOT**: the tutorial says "not
  compatible," the compatibility table says "partial support." Read it as *works under
  `webapiaot` template constraints.*

**Decision:** self-contained single-file for v1. Keep AOT as a *separate, deliberately tested
headless profile* — its payoff (10 MB, 50 ms) matters for CI/containers, not the desktop app.
Add a CI check so trim/AOT regressions surface before we ever attempt AOT.

---

## 6. Desktop shell

| Option | Bundle | Verdict |
|---|---|---|
| **Photino.NET** | <1 MB wrapper, .NET runtime if self-contained | **Best structural fit.** The same .NET process opens the window *and* hosts Kestrel in-process. No sidecar, no handshake, no orphaned server. Svelte is "front end in whatever." |
| **Tauri 2** | ~57 MB macOS `.app` incl. sidecar | Strong second. Mature bundler/updater/plugins, but you write a small Rust supervisor and must handle sidecar lifecycle yourself. |
| Electron | 80–150 MB compressed; 323 MB uncompressed `.app` measured | **Worst fit** — ships Chromium *and* you still ship .NET. |
| MAUI Blazor Hybrid | MAUI + runtime | **Wrong tool** — hosts Razor, not an arbitrary Svelte SPA. |
| Custom WebView2/WKWebView | Smallest | You're writing the shell framework yourself. |

**Recommendation: Photino.NET** for the desktop target, because it eliminates the entire
sidecar-lifecycle problem class (orphaned processes, handshake races, macOS nested-binary
codesigning) rather than solving it.

**If Tauri is chosen instead**, the specifics that matter:

- `externalBin` copies sidecars next to the main executable, **stripping the
  `-<target-triple>` suffix**. Ship the .NET single-file exe as
  `sondergraph-server-<triple>` and rename in CI.
- Resolution is relative to `current_exe()`, not `tauri.conf.json`.
- **Killing sidecars on exit is NOT automatic.** This is the classic orphaned-backend bug,
  and there's an open PR about it. Implement job-object (Windows) / process-group (POSIX)
  kill, with idle-shutdown plus stale handshake as a backstop.
- **macOS: every executable in `Contents/MacOS/` must be signed and notarized.** Use
  `IncludeNativeLibrariesForSelfExtract=true` so there's one file to sign. Test notarization
  early — this is the kind of thing that surfaces the week before release.
- Lock down `capabilities/default.json` to a single exact arg list. That's the
  argument-injection guard.

**WebView runtime dependencies:** WebView2 Evergreen ships an installer bootstrapper
(~2 MB) as fallback; WKWebView is part of macOS. **Linux WebKitGTK is a real package
dependency** (`libwebkit2gtk-4.1`) and is the main Linux packaging risk for both Tauri and
Photino.

---

## 7. Resulting layout

```
src/
  Sondergraph.Core/       # net10.0. No Console, no HTTP, no HOME config. (jj's rule)
    Workspace/            #   resolver, lock, atomic writes, manifest
    Store/                #   SQLite (WAL, busy_timeout=5000, BEGIN IMMEDIATE)
    Operations/           #   facade: ValueTask<OperationResult<T>>, ProblemCode enum
    Progress/             #   ProgressEvent + IProgress<T> adapters
    Jobs/                 #   job ids shared by CLI and server
  Sondergraph.Cli/        # System.CommandLine + Spectre.Console
  Sondergraph.Api/        # Minimal API + Svelte wwwroot; SSE progress
    Transport/            #   --listen unix:|pipe:|http://127.0.0.1:0 ; token; handshake
  Sondergraph.Desktop/    # Phase 3: Photino.NET
web/                      # Svelte + Vite → builds into the API's wwwroot
```

### Non-negotiables

1. Core never writes to the terminal, never reads HOME config, never assumes one host.
2. One façade; **progress and errors are types, not strings**.
3. Serve SPA and API from the **same origin**.
4. Token + Host check + loopback bind, or a per-user socket/pipe.
5. Handshake line on stdout for GUI-spawned servers; single-instance lock + stale detection.
6. Immutable artifacts via atomic rename; mutable state in one transactional store; **one
   lock implementation in Core**; contention errors that name the holder.
7. Ship self-contained; AOT is a deliberate separate profile, never the default.

---

## 8. Corrections this forced on DESIGN.md

| DESIGN.md said | Now |
|---|---|
| §9 "force users to install the .NET runtime" | **Reversed** — self-contained single-file. |
| §10 Q4 "desktop shell: Tauri vs Photino vs Blazor Hybrid" | **Resolved** — Photino.NET, with Tauri as the runner-up and its sidecar caveats documented. |
| §5 "exact .NET locking primitives ... covered in the addendum" | Covered — §4 above, including the Unix `FileShare` trap and macOS `FileStream.Lock`. |
| §3 "shared core library" | **Confirmed by precedent** (jj, Podman, Syncthing, `dotnet-ef`). |
| — | **New:** server-as-optional-shared-host, with handshake and stale-detection rules. |

---

## 9. Caveats

- Size and startup figures are published benchmarks, **not measured on this machine**. The
  `listen` behaviour *was* measured (see §3).
- `FileShare`/flock behaviour on macOS is documented mainly through runtime issues rather
  than current MS docs — verify on target macOS versions before relying on it.
- The ASP.NET AOT documentation is internally inconsistent (§5); treat AOT as unproven for
  this codebase until a spike says otherwise.
- Precedent research leaned on documentation and READMEs; several source quotes are from
  docs fetched during research and treated as data.
