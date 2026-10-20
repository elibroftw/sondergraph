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

Rationale and the alternatives considered are in §3.

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

Exact .NET locking primitives (`FileStream` + `FileShare`, cross-platform advisory locks,
Windows-vs-Unix gotchas) are covered in the addendum.

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
3. **Desktop** — a shell (Tauri or equivalent) wrapping the Svelte UI and supervising the
   .NET backend as a sidecar. See addendum §B for size/runtime tradeoffs.

**On "force the user to install the .NET runtime":** this is a reasonable call to avoid
shipping a ~70 MB self-contained bundle, but it is a real conversion cost for desktop
users. The addendum weighs self-contained vs framework-dependent, and AOT's
compatibility with ASP.NET Core minimal APIs and `System.Text.Json` — AOT in particular
has real constraints worth confirming before committing.

---

## 10. Open questions

1. **Platform priority.** Which ingest adapters first? This drives the `raw/` schema.
2. **Graph index format.** `graph.json` vs SQLite. JSON is greppable and diff-friendly;
   SQLite is better for the triangle/decay queries. Possibly: JSON as truth, SQLite as a
   rebuildable index.
3. **Person identity resolution.** How much automatic matching, and how much user
   confirmation? Auto-merge that guesses wrong is worse than no auto-merge.
4. **Desktop shell choice.** Tauri vs Photino vs Blazor Hybrid — pending addendum.
5. **Media handling.** Photos/video from social dumps will dominate storage. Git LFS?
   Gitignore `data/`? (Leaning: `data/` is gitignored, with an opt-in encrypted
   export for backup.)
6. **Notes integration depth** — see §6.1.

---

## 11. Addenda (pending)

- **§A — Markdown task-tracker landscape.** Adopt vs build.
- **§B — CLI/core/backend precedents & local-server conventions.** Auth, ports, IPC,
  locking primitives, single-file deployment, desktop packaging.
