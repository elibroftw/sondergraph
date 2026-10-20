---
id: 0010
title: Evaluate existing markdown-backed task trackers
status: completed
priority: medium
labels: [tooling, research]
dependencies: []
created: 2026-10-19
completed_at: 2026-10-19
verify:
  - type: bash
    run: "taskmd validate tasks/"
    dir: "."
---

## Context

The user asked whether an existing open-source TUI or web UI already provides a JIRA-like
interface over plain markdown files in a repo, so we can adopt rather than build.

This matters because building a tracker UI is a project in itself, and it is orthogonal to
sondergraph's actual purpose.

## Acceptance criteria

- [x] Survey covers at minimum: Backlog.md, git-bug, Obsidian Kanban/Tasks, Logseq,
      taskell, Vikunja, Focalboard, Markwhen.
- [x] Each entry reports: repo URL, maintenance status, install method, TUI vs web,
      markdown convention it imposes, whether it needs a daemon, scriptability, license.
- [x] Explicit assessment of which fit `tasks/*.md` **inside a larger repo** without
      imposing their own layout or a database.
- [x] Recommendation: adopt one, adopt with a shim, or build a minimal viewer.
- [x] Findings written to `docs/markdown-trackers.md`.

## Outcome

**Adopt [taskmd](https://github.com/driangle/taskmd)** (MIT, Go single binary, v0.8.0).
Our `tasks/` directory already matched its convention, so adoption cost was near zero.

Verified by running the released binary against our real files: `validate` passes on all
10 tasks, the dependency graph renders correctly, and the web Kanban serves HTTP 200.

Two corrections it caught, both applied:

1. `status: todo` -> `pending` (the spec has no `todo`).
2. `blocked_by` -> `dependencies` -- **the silently-ignored field.** It validated clean
   while producing a flat dependency graph, which is worse than an error.

## Notes

**Open follow-up:** our files use `labels`, but the spec field is `tags` and only `tags` is
read by filtering -- so `labels` is currently inert. Either rename or accept the loss.

Runner-up was **Backlog.md** (~7,077 stars, MIT, far better maintained) -- it lost only
because it wants to own a `backlog/` tree and a `task-{id} - {Title}.md` filename pattern.
It is the fallback if taskmd disappoints.

Adoption is low-risk precisely because the files are plain markdown: if taskmd disappears
we lose the viewer and keep every task.
