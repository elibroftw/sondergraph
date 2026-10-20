---
id: 0010
title: Evaluate existing markdown-backed task trackers
status: in-progress
priority: medium
assignee: elopez
labels: [tooling, research]
blocked_by: []
created: 2026-10-19
updated: 2026-10-19
---

## Context

The user asked whether an existing open-source TUI or web UI already provides a JIRA-like
interface over plain markdown files in a repo, so we can adopt rather than build.

This matters because building a tracker UI is a project in itself, and it is orthogonal to
sondergraph's actual purpose.

## Acceptance criteria

- [ ] Survey covers at minimum: Backlog.md, git-bug, Obsidian Kanban/Tasks, Logseq,
      taskell, Vikunja, Focalboard, Markwhen.
- [ ] Each entry reports: repo URL, maintenance status, install method, TUI vs web,
      markdown convention it imposes, whether it needs a daemon, scriptability, license.
- [ ] Explicit assessment of which fit `tasks/*.md` **inside a larger repo** without
      imposing their own layout or a database.
- [ ] Recommendation: adopt one, adopt with a shim, or build a minimal viewer.
- [ ] Findings written to `docs/markdown-trackers.md`.

## Notes

Adoption is preferred if something fits, with one caveat: a tool that insists on owning the
whole repo root, or on a database, is disqualified — sondergraph's tasks live in a
subdirectory of a normal code repo and that must not change.

Research in progress. Findings land in `docs/markdown-trackers.md`.
