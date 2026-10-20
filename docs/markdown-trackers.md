# Markdown-backed task trackers — evaluation

**Task:** 0010 · **Date:** 2026-10-19 · **Verdict: adopt `taskmd`.**

The question was whether an existing open-source tool already provides a JIRA-like
interface over markdown files in a repo, so we can adopt rather than build.

**Answer: yes, and we already have.** sondergraph's `tasks/` directory was written to the
taskmd format before the evaluation finished — because taskmd's convention (`tasks/`,
`NNN-slug.md`, YAML frontmatter, `## Acceptance criteria`) is the obvious one to reach for
independently. The evaluation confirmed it was the right call, with two corrections.

---

## Recommendation: taskmd

[driangle/taskmd](https://github.com/driangle/taskmd) — **MIT**, Go single binary, active
(v0.8.0, 2026-10-02), ~75 stars.

**What it gives us:**

| Capability | Detail |
|---|---|
| **Spec** | Versioned format spec (v1.2), documented field-by-field |
| **Validator** | `taskmd validate tasks/` — caught two real errors in our files |
| **Dependency graph** | `taskmd graph --format ascii`; correct against our real backlog |
| **Web Kanban** | `taskmd web start` — board, graph, stats, metrics; verified running |
| **`next`** | Ranks the highest-value unblocked task |
| **MCP server** | Task tools for AI assistants |
| **Single binary** | ~6.5 MB compressed, 17 MB extracted, no runtime, no daemon |
| **Docker** | Official `ghcr.io/driangle/taskmd` image |

**Why it fits:** it wants exactly a `tasks/` directory and imposes no wrapper tree, no
database, and no daemon. That is the whole requirement.

### Verified, not assumed

Every claim above was checked by running the released binary against our actual task files,
not read from a README:

- `taskmd validate tasks/` → 10 tasks, all valid (after fixes below)
- `taskmd graph tasks/ --format ascii` → dependency tree renders correctly
- `taskmd web start --port 18099` → server started, `HTTP 200`, watches for file changes

### Two corrections it caught

1. **`status: todo` is invalid.** The spec's initial state is `pending`; there is no `todo`.
   This was the only error in the first validation pass — all nine task files had it.
2. **The dependency field is `dependencies`, not `blocked_by`.** This one is worth dwelling
   on: `blocked_by` **validated clean and produced a flat graph**. A silently-ignored field
   that looks like it worked is worse than a hard error. Our files now use `dependencies`.

### The nice-to-have we will actually use

`verify[]` turns acceptance criteria into executable checks:

```yaml
verify:
  - type: bash
    run: "dotnet test tests/Sondergraph.Core.Tests"
  - type: assert
    check: "A merge can be reversed with `sg ungraft`"
```

This is the definition-of-done discipline that otherwise rots in prose.

### Caveats

- **Web API is unauthenticated** and binds `127.0.0.1`. Do not use `--host 0.0.0.0` without
  reading the spec's Network Access section. Acceptable for local-only use.
- **Young project.** Created 2026-02, ~75 stars, v0.8.0. Bus factor is real. Mitigation:
  the files are plain markdown with a documented spec, so if taskmd disappears we lose the
  viewer and keep every task. **This is exactly the property that makes adopting it safe.**
- **`labels` vs `tags`:** the spec field is `tags`; our files say `labels`. Both validate,
  but only `tags` is read by filtering, so **`labels` is currently inert.** Unresolved —
  either rename to `tags` or accept that filtering will not see it.

---

## The landscape: why the alternatives lost

The tools split into three camps, and only one camp fits `/tasks/*.md` inside a larger
repo.

### ❌ Database- or daemon-backed — disqualified as source of truth

| Tool | Why not |
|---|---|
| **Vikunja** | SQLite/MySQL/Postgres via xorm. No markdown-file storage. A third-party FUSE bridge exists but its own README disclaims the sync CLI as untested AI-generated code. |
| **Focalboard** | `dbtype: sqlite3\|postgres\|mysql`. Markdown is card *content*. `.focalboard` export format deprecating. |
| **Plane** | Postgres + Redis + RabbitMQ + S3 + OpenSearch; min 4 CPU / 8 GB. Docker quickstart now installs the **Commercial** edition. |
| **OpenProject** | Rails + Postgres. `POST /api/v3/render/markdown` proves markdown is a *rendering target*. |
| **Taskwarrior 3.x** | Local DB (TaskChampion). Docs explicitly forbid Syncthing/rsync file sync as corruption-prone. |
| **TaskLite** | SQLite at `~/TaskLite/main.db`. Good markdown-*editing* story, wrong substrate. |

**On Taskwarrior's git sync:** v3.5.0 / TaskChampion v3.1.0 did add a git backend — but
that is **git as encrypted transport for an opaque database**, not markdown as source of
truth. It does not produce diffable `tasks/*.md`. Worth stating precisely, because the
headline is misleading.

### ❌ Right format, dead or wrong-shaped software

- **Taskell** — the *format* is ideal (`taskell <anyfile>.md`, `## headings` = columns).
  The software is **archived**; last Hackage upload 2021-08-26 with failing builds. Copy
  the convention, not the binary.
- **kanban.bash** — **CSV-backed, not markdown.** Also AGPL-3.0.
- **Obsidian Kanban** — the *format* is close to an ecosystem standard and is
  path-agnostic, but the plugin is **maintainer-seeking with 609 open issues**. Forks
  (Boardwalk, kanban-view) read the same bytes. No CLI.
- **Obsidian Tasks** — richest inline vocabulary (incl. `🆔`/`⛔` dependencies) but
  Obsidian-coupled, one format at a time, `YYYY-MM-DD` only, field order matters.
- **Logseq** — markdown is durable but the datascript DB is the query engine; adopting it
  means adopting journals and a page-per-day model.
- **Dendron / Foam** — dormant or PKM-scoped. No task states or board.
- **mdBook** — checkboxes are **non-persistent by design** ([issue #1185](https://github.com/rust-lang/mdBook/issues/1185),
  closed for lack of options). Presentation only.
- **Markwhen** — timelines, not issues. A nice companion renderer if our tasks ever carry
  `start`/`due` dates; not a tracker.

### ⚠️ Real contenders that lost on specifics

**Backlog.md** ([MrLesk/Backlog.md](https://github.com/MrLesk/Backlog.md)) — the most
polished option: **MIT, ~7,077 stars, pushed the same day as this evaluation**, TUI board
+ web Kanban, `--json` streaming, MCP. It lost on tree ownership:

- Mandates `task-{id} - {Title}.md` — literal `task-` prefix, exactly ` - ` separator; the
  CLI parses filenames for IDs.
- Wants a `backlog/{tasks,completed,archive,drafts,docs,decisions}` tree.
- Directory *is* configurable (`backlog_directory: tasks`), so this is closer than it
  sounds — but it would still want to own the naming and allocate IDs across git branches.
- v1.54.0 had breaking changes (`task complete` removed; mark-Done-then-archive).

**Genuinely tempting.** If taskmd disappoints, Backlog.md is the fallback — it is better
maintained by an order of magnitude. The reason for taskmd is that our files already exist
in its shape and it asks for nothing beyond `tasks/`.

**mdtask** ([syabro/mdtask](https://github.com/syabro/mdtask)) — 248 stars, the only tool
that reads **any `- [ ]` checkbox in any `.md`** with zero migration, and `@blocked_by:`
has real semantics. Lost because it uses inline checkbox tokens rather than frontmatter, so
there is **no `assignee`/`due`/`priority`-as-YAML story**, and its license is
`NOASSERTION` (unclear), which is a problem for a project we may distribute.

**Others evaluated and not chosen:** kanban-md (agent-claims, self-healing IDs — good, but
`kanban/` root), issuectl (best `--json` story, but a *directory* per item), git-issues
variants, ghi, TaskRepo, imorgon, dstask (all personal trackers pointing at `~/.dstask`-style
repos, with dstask explicitly listing collaboration as a non-feature), gh-issue-sync /
gh-md / gh-ext--issue-sync (invert the premise — GitHub stays the source of truth).

---

## If we ever outgrow it: build notes

Battle-tested decisions from the survey, worth stealing if we hand-roll a viewer:

1. **One file per issue**, ID in the filename — creates never conflict.
2. **Random/ULID IDs** for offline collision-free creation; taskmd supports
   `sequential | prefixed | random | ulid`.
3. **One frontmatter field per line** for line-based git merges, plus
   `.gitattributes` marking `tasks/*.md text`.
4. **HTML-comment delimiters** for machine-managed sections — invisible when rendered,
   surgically editable (gh-md, ghi, Backlog.md).
5. **Auto-commit scoped by pathspec** so a task tool never sweeps unrelated staged work.
6. **Derive history from `git log` + commit trailers** (`Refs-Issue:`) rather than a
   separate event store.
7. **JSON everywhere + MCP** for scriptability.
8. **Three-way sync with explicit conflict skip** if we ever mirror a remote — never silent
   overwrite.

### Frontmatter field frequency across ~12 tools

`id`, `title`, `status`, `priority`, `assignee`, `due`, `tags`/`labels`, `created`,
`updated`, `depends-on`, `external_id` are the near-universal set. Two fault lines:

- **`depends-on` / `dependencies` / `blocked_by` / `depends`** is *the most name-divergent
  field in the ecosystem* — and the one that silently failed on us. Always check the graph,
  not just the validator.
- **Status vocabulary** splits between human display strings (`"In Progress"`),
  machine enums (`pending → in-progress → completed`), and bareword block markers
  (`TODO`/`DOING`/`DONE`). taskmd uses machine enums, which is right for a tool that also
  has a CLI.

**No ratified standard exists.** taskmd's spec is the closest thing we found.

---

## Limitations of this evaluation

- `web_fetch` was unavailable to the research agent (provider misconfiguration). Findings
  came from `web_search` returning verbatim README bodies, and were then **independently
  verified via the GitHub API and by running the released taskmd binary**. Where the agent
  reported `drangle/taskmd`, the correct owner is **`driangle/taskmd`** — caught and fixed
  during verification.
- Star counts and licenses were unverified by the agent for several tools; those cited above
  (`Backlog.md` 7,077/MIT, `mdtask` 248/NOASSERTION, `taskmd` 75/MIT) were confirmed
  directly via the GitHub API.
- `vit` could not be assessed — no usable evidence found. Not claimed either way.
