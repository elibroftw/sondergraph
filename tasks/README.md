# Task tracking convention

Tasks live as markdown files in `tasks/`. The files are the source of truth; any UI is a
view over them. `git` supplies the history, branching, and review that a tracker normally
reinvents.

## File naming

```
tasks/<id>-<slug>.md
```

`<id>` is a zero-padded sequential number (`0001`). `<slug>` is a short kebab-case
description. Example: `tasks/0001-scaffold-solution.md`.

## Frontmatter

```yaml
---
id: 0001
title: Scaffold the solution skeleton
status: todo          # todo | in-progress | blocked | review | done | wontfix
priority: high        # critical | high | medium | low
assignee: elopez
labels: [infra, dotnet]
due: 2026-11-01       # optional, ISO date
blocked_by: []        # list of task ids
parent:               # optional task id
created: 2026-10-19
updated: 2026-10-19
---
```

## Body sections

```markdown
## Context
Why this task exists.

## Acceptance criteria
- [ ] Concrete, checkable outcome.

## Notes
Freeform. Decisions, links, dead ends.
```

## Conventions

- **One task per file.** If a task needs sub-tasks, use `parent` / `blocked_by` rather
  than nesting headings.
- **`status: done`** requires every acceptance criterion to be checked.
- **`blocked`** requires a `## Notes` entry naming the blocker — the same discipline
  applied to goals generally. "Hard" is not a blocker.
- **Moving a task between states is a git commit**, and the commit message names the task
  id. This is the changelog.
- **Do not renumber ids.** Gaps are fine; reused ids corrupt history.

## Tooling

See `docs/` for the evaluation of existing markdown-backed trackers (Backlog.md,
git-bug, Obsidian Kanban, etc.) and whether to adopt one rather than build a viewer.
