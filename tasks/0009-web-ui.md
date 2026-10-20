---
id: 0009
title: Svelte web UI shell with mass contact view
status: todo
priority: high
assignee: elopez
labels: [ui, svelte, frontend]
blocked_by: [0001]
created: 2026-10-19
updated: 2026-10-19
---

## Context

DESIGN.md §8. Svelte + Vite with Tailwind and shadcn-svelte. The stated purpose of the web
UI is to be the on-ramp for people who are not comfortable in a terminal.

That purpose sets the bar: a UI covering only part of the CLI's capability pushes those
users back to the terminal, which defeats the point. Feature parity is a tracked goal.

## Acceptance criteria

- [ ] Svelte + Vite app in `web/`, building cleanly.
- [ ] Tailwind + shadcn-svelte wired up.
- [ ] Talks to the local API (0001), with the auth/port handshake from the addendum.
- [ ] Mass contact view: all people, filterable and sortable by tier.
- [ ] Tier and location editing from the UI.
- [ ] Merge two or more contacts into one person, with the confirmation flow from 0003.
- [ ] Dev workflow documented: `dotnet run` + `pnpm dev` together.

## Notes

The mass contact view is deliberately first: it exercises the API surface (list, filter,
mutate, merge) that every other view reuses, so building it early shakes out the API shape
while that shape is still cheap to change.

Deferred to later tasks: trace view (0006), publish UI (0005).
