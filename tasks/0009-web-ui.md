---
id: 0009
title: Svelte web UI shell with mass contact view
status: completed
priority: high
labels: [ui, svelte, frontend]
dependencies: [0001]
created: 2026-10-19
completed_at: 2026-10-19
---

## Context

DESIGN.md §8. Svelte + Vite with Tailwind and shadcn-svelte. The stated purpose of the web
UI is to be the on-ramp for people who are not comfortable in a terminal.

That purpose sets the bar: a UI covering only part of the CLI's capability pushes those
users back to the terminal, which defeats the point. Feature parity is a tracked goal.

## Acceptance criteria

- [x] Svelte + Vite app in `web/`, building cleanly.
- [x] Tailwind + shadcn-svelte wired up.
- [x] Talks to the local API (0001), with the auth/port handshake from the addendum.
- [x] Mass contact view: all people, filterable and sortable by tier.
- [x] Tier and location editing from the UI.
- [x] Merge two or more contacts into one person, with the confirmation flow from 0003.
- [x] Dev workflow documented: `dotnet run` + `pnpm dev` together.

## Notes

The mass contact view is deliberately first: it exercises the API surface (list, filter,
mutate, merge) that every other view reuses, so building it early shakes out the API shape
while that shape is still cheap to change.

Deferred to later tasks: trace view (0006), publish UI (0005).
