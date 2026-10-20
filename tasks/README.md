# Task tracking convention

Tasks live as markdown files in `tasks/`. The files are the source of truth; any UI is a
view over them. `git` supplies the history, branching, and review that a tracker normally
reinvents.

## Format: taskmd v1.2

The format is **taskmd spec v1.2**, not an invention of ours. See
`docs/markdown-trackers.md` for why. Adopting a spec with a working validator, a web
Kanban, a dependency graph, and an MCP server is strictly better than hand-rolling one.

Validate at any time:

```bash
taskmd validate tasks/
```

### File naming

```
tasks/<id>-<slug>.md
```

`<id>` is a zero-padded number (`0001`). The slug is descriptive only — tasks resolve by
ID, so a stale slug is valid and does not need fixing.

### Frontmatter

```yaml
---
id: "0001"
title: Scaffold the solution skeleton
status: pending       # pending | in-progress | in-review | completed | blocked | cancelled
priority: critical    # low | medium | high | critical
effort: medium        # small | medium | large
type: chore           # feature | bug | improvement | chore | docs
owner: elopez
labels: [infra, dotnet]        # 'labels' is our alias; spec field is 'tags'
dependencies: ["0001"]         # task IDs that must complete first
parent: "0042"                 # optional; single parent, organizational only
phase: "v0.1"                  # optional; time-ordered stage
created_at: 2026-10-19
---
```

**Two things that bit us already, both now enforced:**

1. **`status: todo` is invalid.** The spec's initial state is `pending`. There is no
   `todo`. This was the only error in our first validation pass.
2. **The dependency field is `dependencies`, not `blocked_by`.** `blocked_by` is silently
   ignored — it validates clean but produces a flat dependency graph, which is worse than
   an error because it looks like it worked. `taskmd graph tasks/ --format ascii` is the
   check.

### Structured fields the spec provides

`touches[]` (abstract code areas a task modifies — drives parallel-work conflict
detection), `context[]` (explicit file paths), `verify[]` (typed acceptance checks:
`bash` runs a command, `assert` is a statement for a human/agent to evaluate), `pr[]`,
`external_id` (bridge to Jira/GitHub), `group` (derived from subdirectory if omitted).

**`verify` is the interesting one for this project.** It turns acceptance criteria into
executable checks, which is exactly the "definition of done" discipline that otherwise
lives in prose:

```yaml
verify:
  - type: bash
    run: "dotnet test tests/Sondergraph.Core.Tests"
    dir: "."
  - type: bash
    run: "pnpm --dir web build"
  - type: assert
    check: "A merge can be reversed with `sg ungraft`"
```

### Organizing axes

The spec is opinionated here and the distinction is worth internalizing:

| Axis | Per task | Ordered? | Ends? | Use for |
|---|---|---|---|---|
| `group` | one | no | never | enduring product area — `cli`, `web`, `core` |
| `tags` | many | no | never | cross-cutting attributes — `mvp`, `ux` |
| `touches` | many | no | never | code areas, drives conflict detection |
| `parent` | one | no | **yes** | a bounded initiative with a goal |
| `phase` | one | **yes** | **yes** | a sprint or release — a real sequence |

The common mistake is putting long-lived workstreams into `phase`. A phase list is a
sequence (`v0.1 → v0.2 → v1.0`); "windows-support" is an initiative that finishes but is
not a stage of a pipeline, so it belongs in a `parent` task.

### Body sections

```markdown
## Context
Why this task exists.

## Acceptance criteria
- [ ] Concrete, checkable outcome.
```

## Conventions

- **One task per file.** Sub-tasks use `parent` / `dependencies`, not nested headings.
- **`completed`** requires every acceptance criterion to be checked, and `verify` to pass
  where checks are defined.
- **`blocked`** requires a `## Notes` entry naming the blocker. "Hard" is not a blocker.
- **Moving a task between states is a git commit** naming the task id. This is the
  changelog.
- **Never renumber ids.** Gaps are fine; reused ids corrupt history.
- **`worklogs`** are opt-in (`.taskmd.yaml`), stored at
  `<task-dir>/<group>/.worklogs/<ID>.md`, append-only with RFC 3339 headings. Useful once
  several agents work the same backlog.

## Tooling

```bash
taskmd list tasks/            # table view
taskmd next tasks/            # highest-value unblocked task
taskmd graph tasks/ --format ascii
taskmd validate tasks/
taskmd web start --task-dir tasks --open     # Kanban + graph + metrics in the browser
```

The web UI binds `127.0.0.1` and its API is **unauthenticated** — do not expose it with
`--host 0.0.0.0` without reading the spec's Network Access section.

### `labels` vs `tags`

The spec field is `tags`. Our files say `labels`. Both validate, but only `tags` is read
by filtering — so **`labels` is currently inert**. Either switch to `tags`, or keep
`labels` as an alias and accept that filtering will not see it. This is unresolved and
tracked in task 0010.

