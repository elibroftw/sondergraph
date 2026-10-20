# sondergraph — Technical Design

> *everything, all at once.*

**Status:** Draft for review
**Date:** 2026-10-19

---

## 1. What this is

A locally-run tool that:

1. **Ingests** data from social media platforms — posts, contacts, profile data — into a
   `data/` directory you own.
2. **Features:**
   - **Mass contact view** — merge multiple accounts into one *person* node; tier them
     (friend / close / best); attach location.
   - **Publish** — export your own posts as Hugo-friendly markdown with a configurable
     directory structure, plus an RSS feed.
   - **Trace** — a per-contact dual-pane view: their social media on one side, your notes
     on the other, with integrations into Notesnook / Apple Notes / plain markdown.
3. **Adjacent features** — the things that fall out of the graph model: shared-neighbor
   detection, decay/staleness, shortest path between two contacts, location history.

The graph is not a metaphor. It is the data model (§4).

---

## 2. Architectural decisions (proposed)

### 2.1 Layering: shared core library

```
┌─────────────┐   ┌──────────────┐   ┌─────────────┐
│  sg (CLI)   │   │  ASP.NET API │   │  Desktop    │
│             │   │  (localhost) │   │  shell      │
└──────┬──────┘   └──────┬───────┘   └──────┬──────┘
       │                 │                  │
       │                 │  serves the Svelte UI
       │                 │                  │
       └────────┬────────┘                  │
                ▼                           │
        ┌───────────────┐                   │
        │ Sondergraph.  │◄──────────────────┘
        │ Core          │   (desktop shells the same API)
        └───────┬───────┘
                ▼
        ┌───────────────┐
        │  Workspace    │  data/, tasks/, config
        │  (on disk)    │
        └───────────────┘
```

**Decision: shared core library. The CLI and the backend both reference `Sondergraph.Core`;
neither calls the other.**

Rationale and the alternatives considered are in §3; the supporting precedent research is in
[docs/architecture-precedents.md](./docs/architecture-precedents.md).

**Additionally: the backend is an optional shared host, never a mandatory intermediary.**
The CLI runs Core in-process and works fully headless. A long-lived server exists for the
Web UI to attach to, and holds the workspace lock, job state, and file watchers while
running. This is Podman's hybrid (in-process library for local, REST for remote), not
Docker's (nothing works without the daemon).

### 2.2 Proposed solution layout

```
sondergraph/
├── Sondergraph.sln
├── src/
│   ├── Sondergraph.Core/          # all logic. no console I/O, no HTTP.
│   ├── Sondergraph.Cli/           # sg — thin. arg parsing, rendering, exit codes.
│   ├── Sondergraph.Api/           # ASP.NET minimal API. thin. HTTP mapping only.
│   └── Sondergraph.Workspace/     # workspace discovery, locking, atomic writes
├── web/                           # Svelte + Tailwind + shadcn-svelte
├── desktop/                       # Tauri (or equivalent) shell
├── tests/
│   ├── Sondergraph.Core.Tests/
│   └── Sondergraph.Api.Tests/
├── tasks/                         # markdown task files (see §7)
├── docs/
└── DESIGN.md
```

**The "thin shell" rule:** `Core` must not know it is being driven by a CLI or an HTTP
request. If a Core method needs to report progress or ask a question, it takes a callback
or returns a result — it never writes to `Console` and never returns an `IActionResult`.

This rule is what makes §3's Option A actually work. Violating it is how shared-core
designs rot into "the CLI and the API do subtly different things."

---

## 3. The CLI/backend question

This was flagged as the hard problem. Here are the three options, and why A wins.

### Option A — shared core library ✅ **recommended**

CLI and API are both thin adapters over `Sondergraph.Core`.

- **For:** One implementation of every behavior. No serialization boundary to maintain.
  Fast startup for the CLI (it never spawns a server). Trivially testable — test Core once.
  Streaming progress (`IProgress<T>`) and structured errors work naturally because
  everything is in-process.
- **Against:** Requires discipline to keep the shells thin. Any behavior that is genuinely
  interactive (a confirmation prompt) has to be modelled as a Core-level abstraction, which
  takes design effort up front.

### Option B — CLI is an HTTP client of a spawned server

`sg` starts (or connects to) the local API and talks HTTP.

- **For:** Guarantees CLI and UI do exactly the same thing — literally one code path.
- **Against:** Kills CLI ergonomics. Every command pays server startup (or must manage a
  daemon). `sg` becomes unusable in scripts and pipes without a running server. You
  inherit all of §5's auth and port-management problems for the *terminal* case, where
  they buy nothing. This is the Docker model, and Docker pays for it with a daemon that
  is a genuine operational burden.

### Option C — backend shells out to the CLI

- **For:** CLI is the single source of truth; easy to add a "copy this command" affordance
  in the UI.
- **Against:** Worst of all worlds. Parsing human-oriented stdout is brittle; you lose
  structured errors and typed results; long-running operations need stdout scraping for
  progress; process-spawn overhead per request. Actively harmful.

### Verdict

**Option A.** Option C is a non-starter. Option B is only defensible if you want the
daemon *anyway*, and sondergraph does not need a daemon — the workloads are
file-oriented, not service-oriented.

**Mitigating A's main risk:** the "thin shell" rule above, enforced by a test that asserts
`Sondergraph.Core` has no reference to `Sondergraph.Cli` or `Sondergraph.Api`, and by
keeping Core's public surface result-shaped (`Result<T>`, not exceptions-as-control-flow).

> **Note for the reader:** CLI-as-HTTP-client (Option B) becomes more attractive if a
> future feature *requires* a resident process — e.g. live streaming from a platform's
> push API, or background sync while the UI is closed. If that happens, the migration is
> additive: the CLI keeps calling Core, and Core gains an event source. This is another
> reason A is the right starting point — it does not foreclose B.

---

## 4. Data model

### 4.1 The graph

- **Node** = a `Person`. One person, many platform accounts.
- **Edge** = a link between people: a platform relationship, a shared circle, a
  co-occurrence (same event, same location).
- **Edge weight** = relationship tier (`friend` < `close` < `best`), or a computed
  affinity score.
- **Node attributes** = locations, notes, tags, tier.

This makes the requested features fall out as graph operations:

| Feature | Graph operation |
|---|---|
| Merge contacts | Edge contraction (union-find over account records) |
| Mass contact view | Projection of all nodes, sorted/filtered by edge weight |
| Tier assignment | Edge weight mutation |
| Location attach | Node attribute write |
| Contact timeline | Ego graph of one node, temporal slice |
| "Who knows each other that I haven't introduced?" | Triangle query where the ego-edge is absent |
| "Who haven't I talked to in a year?" | Edge-age filter |

That last pair is the argument for the graph model: they are one-liners here and
near-impossible over a contact table.

### 4.2 On-disk shape

The `data/` directory is the source of truth. Proposed:

```
data/
├── people/
│   └── <person-id>/
│       ├── person.md          # frontmatter: name, tier, locations, tags, aliases
│       ├── accounts.json      # platform handles mapped to this person
│       ├── notes/             # your notes (see §6)
│       └── media/
├── raw/                       # untouched dumps, per-platform, append-only
│   ├── mastodon/
│   ├── instagram/
│   └── <platform>/
├── graph.json                 # derived index; rebuildable from above
└── .sondergraph/
    ├── lock
    └── cache/
```

**Principle: raw dumps are append-only and never edited. Everything else is derived and
rebuildable.** `graph.json` is a cache; deleting it must be safe.

### 4.3 Merge semantics

Merging is the riskiest operation, so it gets explicit rules:

- Merges are **recorded, not destructive** — a `merges.json` log plus a redirect from the
  absorbed person-id.
- **Field-level conflict resolution:** per field, choose winner, or union for list-typed
  fields (aliases, locations, accounts).
- **Reversible:** `sg ungraft <merge-id>` restores the prior state from the log.

Overwriting a contact record with no way back is the one mistake users will not forgive.

---

## 5. Locking & workspace (git-style)

The concern about "workspace context similar to how git operates regarding file mutexes"
is well founded: at minimum, the CLI and the API can touch `data/` concurrently.

- **Workspace discovery:** walk up from CWD looking for `data/.sondergraph/`, honour
  `$SONDERGRAPH_WORKSPACE`, mirroring git's `GIT_DIR` / `.git` discovery.
- **Lock file** at `data/.sondergraph/lock`, acquired for writes only. Reads are lock-free
  and rely on atomic-rename writes so no reader ever sees a torn file.
- **Write protocol:** write to temp file in the same directory → `fsync` → atomic rename.
  This is what makes lock-free reads safe.
- **`sg` reports the lock holder** (pid, command, start time) when acquisition fails, and
  offers `--wait`. Git's "another git process seems to be running" message is famously
  unhelpful; this is a cheap place to do better.
- **Stale lock detection:** if the holding pid is gone, offer to break the lock.

Exact .NET locking primitives, their Unix/Windows gotchas, and the local-transport
conventions are in [docs/architecture-precedents.md](./docs/architecture-precedents.md) §3–4.

**Sharp edges found there, to design around now:**

- On Unix, only `FileShare.None` is meaningful — every other share value permits multiple
  writers.
- `File.Move` / `File.Delete` ignore advisory locks on Unix, so a lock *path* can be swapped.
  Lock a stable file, or re-stat the fd after acquiring.
- `FileStream.Lock` is unsupported on macOS.
- Prefer flock on an open fd over lockfile existence: the OS releases it on death, so a crash
  cannot leave a stale lock. This is a deliberate improvement on git's `index.lock`.
- Never hold the lock across user interaction — a GUI modal holding the lock hangs every CLI.

---

## 6. Integrations

### 6.1 Notes (Feature #3)

The dual-pane trace view needs notes to interoperate without owning them.

- **Markdown-first.** The canonical store is plain markdown in `data/people/<id>/notes/`.
  This is always available, requires no integration, and is the fallback.
- **Adapters** are optional and read/write against external apps:
  - **Notesnook** — via its local API/export surface if viable.
  - **Apple Notes** — via AppleScript on macOS, or the SQLite store (fragile; read-mostly).
  - **Obsidian / Logseq** — point at a vault directory; these are just markdown.
  - **Plain directory** — a user-configured folder.
- **Rule: never make an external app the only copy.** Import/export, not delegation.
  Proprietary stores change format without notice.

### 6.2 Platform ingestion — posture

Needs an explicit stance, not because it is a legal minefield in the abstract, but
because platform ToS and local law vary by jurisdiction and change. Concretely:

- **Prefer official APIs and data exports.** GDPR/CCPA export bundles and official APIs
  are both more stable and less fraught than scraping.
- **Authenticated, user-initiated, rate-limited.** No background crawling as a default.
- **Document the posture** in the README so users are not surprised.
- **Design assumption:** ingestion is pluggable per platform, and each adapter declares
  its own mechanism and constraints. Platforms without a viable path simply do not get an
  adapter, and that is acceptable.

This is not a reason to avoid the feature — it is a reason to build the adapter
interface carefully and be honest in the docs.

---

## 7. Task tracking as markdown

**Requirement:** tasks are markdown files in the repo, presentable JIRA-like.

The research addendum compares existing tools. The interim design:

- One file per task: `tasks/<id>-<slug>.md`, YAML frontmatter + markdown body.
- Frontmatter fields: `id`, `title`, `status`, `priority`, `assignee`, `labels`, `due`,
  `blocked_by`, `parent`.
- Body: `## Context`, `## Acceptance criteria`, `## Notes`.
- **Source of truth is the files.** Any UI is a view. `git` provides history, branching,
  and review — which is most of what a tracker is for.

Adoption of an existing tool is preferred over building a viewer, if one fits. See
addendum §A.

---

## 8. Web UI

- **Svelte + Vite.** shadcn-svelte + Tailwind for components.
- **Tier 1 views:** mass contact view, per-contact trace (dual pane), publish/export.
- **Accessibility note:** if the UI is the on-ramp for non-terminal users, it needs to be
  genuinely complete — a UI that covers 60% of the CLI's capability pushes users back to
  the terminal, which defeats the stated purpose. Feature parity is a tracked goal, not an
  aspiration.

---

## 9. Distribution

Three targets, in priority order:

1. **Local dev** — `dotnet run` + `pnpm dev`.
2. **Docker** — `docker run` with a mounted workspace volume. Note the volume-mount
   requirements for locking (§5) to work correctly across the container boundary.
3. **Desktop** — **Photino.NET**, which opens the window and hosts Kestrel in the *same*
   process. This eliminates the entire sidecar-lifecycle problem class (orphaned backends,
   handshake races, nested-binary codesigning on macOS) rather than solving it. Tauri is the
   runner-up if the bundler/updater ecosystem is wanted; its caveats are documented in
   [docs/architecture-precedents.md](./docs/architecture-precedents.md) §6.

**On the .NET runtime dependency — reversed from the original plan.** The intent was to
require users to install the runtime and avoid a large download. The evidence says ship
**self-contained single-file** (~80–110 MB, ~150–300 ms cold start) instead:

- Framework-dependent pushes an install step onto users and creates an ARM64-vs-x64 runtime
  choice on macOS/Linux — a class of "works on my machine" support tickets.
- The Svelte bundle is negligible (100–500 KB gzipped). The runtime decides the bundle size,
  not the UI.
- The real cost: self-contained does **not** roll forward to .NET security patches, so we
  must ship patch releases. That is the trade being made, deliberately.

**Native AOT is deferred.** It would need source-gen JSON, no EF Core (officially
experimental under AOT), no SPA middleware, and no reflection-based DI. That is a lot of
discipline for a desktop app. Keep it as a separately-tested headless "server profile" if
CI/container size ever matters.

**Verified on .NET 10.0.400, not assumed:** `ListenLocalhost(0)` *throws*
(`Dynamic port binding is not supported when binding to localhost`); the working pattern is
`Listen(IPAddress.Loopback, 0)` then read `IServerAddressesFeature` after `StartAsync`.
Local IPC and the handshake contract are in
[docs/architecture-precedents.md](./docs/architecture-precedents.md) §3.

---

## 10. Open questions

1. **Platform priority.** Which ingest adapters first? This drives the `raw/` schema.
2. **Graph index format.** `graph.json` vs SQLite. JSON is greppable and diff-friendly;
   SQLite is better for the triangle/decay queries. Possibly: JSON as truth, SQLite as a
   rebuildable index. (Locking research leans toward SQLite-for-mutable-state, WAL mode.)
3. **Person identity resolution.** How much automatic matching, and how much user
   confirmation? Auto-merge that guesses wrong is worse than no auto-merge.
4. ~~**Desktop shell choice.**~~ **Resolved** — Photino.NET; Tauri as runner-up.
   See [docs/architecture-precedents.md](./docs/architecture-precedents.md) §6.
5. **Media handling.** Photos/video from social dumps will dominate storage. Git LFS?
   Gitignore `data/`? (Leaning: `data/` is gitignored, with an opt-in encrypted
   export for backup.)
6. **Notes integration depth** — see §6.1.

---

## 11. Addenda

- **[docs/markdown-trackers.md](./docs/markdown-trackers.md)** — task-tracker landscape.
  **Outcome: adopt `taskmd`.** The `tasks/` format is now a validated spec, not a convention
  of ours. Was task 0010.
- **[docs/architecture-precedents.md](./docs/architecture-precedents.md)** — CLI/core/backend
  precedents, local IPC and handshake, .NET locking primitives, deployment mode, desktop
  packaging. Resolved §10 Q4 and reversed §9's runtime decision.
- **[docs/decisions/](./docs/decisions/)** — ADRs. [0001](./docs/decisions/0001-shared-core-library.md)
  records the shared-core decision.
