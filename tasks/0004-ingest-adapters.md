---
id: 0004
title: Ingest adapter interface and first platform
status: todo
priority: high
assignee: elopez
labels: [ingest, adapters]
blocked_by: [0003]
created: 2026-10-19
updated: 2026-10-19
---

## Context

DESIGN.md §6.2. Ingestion is the riskiest part of the project — platform ToS and local law
vary by jurisdiction and change without notice. The design response is a narrow, explicit
adapter interface where each adapter declares its own mechanism and constraints.

Which platform goes first is open (DESIGN.md §10) and should be chosen for a *safe* path,
not a convenient one.

## Acceptance criteria

- [ ] `IIngestAdapter` interface: declares platform, mechanism (official API / user data
      export / scrape), rate limits, and auth requirements.
- [ ] Adapters are user-initiated and rate-limited by default. No background crawling.
- [ ] Raw dumps land in `data/raw/<platform>/`, append-only and never edited by later
      stages.
- [ ] First adapter implemented against an official API or a user data export — **not** a
      scrape.
- [ ] Progress reported via callback, not console writes (thin-shell rule, DESIGN.md §2.2).
- [ ] Ingest is resumable and idempotent: re-running does not duplicate raw records.
- [ ] Posture documented in the README so users are not surprised.

## Notes

Candidate first targets, in rough order of comfort: Mastodon (open API, tolerant), a
GDPR/CCPA data export bundle (user-owned by construction), Bluesky (open API). Instagram
and Facebook are the most valuable and the least friendly — assume an export bundle rather
than an API.

Platforms with no viable path simply do not get an adapter. That is an acceptable outcome,
not a failure.
