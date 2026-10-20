---
id: "0011"
title: First real platform parser
status: pending
priority: high
labels: [ingest, parsers, schema-discovery]
dependencies: ["0004"]
created: 2026-10-19
---

## Context

ADR 0003 defines the parser seam; task 0004 builds it. This task proves it against a real
platform export, which is also the **schema-discovery** work: figuring out what a platform
actually produces, as opposed to what its documentation claims.

**This task cannot start without a real export.** It is blocked on the platform decision
(DESIGN.md §10 Q1) and on obtaining the data.

## Acceptance criteria

- [ ] Platform chosen and recorded in DESIGN.md §10 Q1.
- [ ] A real export obtained and placed under `data/raw/<platform>/`.
- [ ] **Findings documented in `docs/platforms/<platform>.md`**: file layout, identifier
      scheme, timestamp formats, encoding quirks, what is absent, what is ambiguous.
      This document is the actual deliverable of the reverse-engineering step.
- [ ] Fixture subset checked in under `tests/fixtures/<platform>/`, **scrubbed of third-party
      personal data** (see privacy note below).
- [ ] Parser implemented per ADR 0003 rules, with `ParserVersion` declared.
- [ ] Fixture tests pass; malformed-record handling is covered by a test.
- [ ] Re-parse is idempotent against the real export (not just fixtures).
- [ ] Round-trip sanity: parsed accounts and posts match a manual spot-check of the export.

## Notes

**Privacy: exports contain other people's data, not just yours.** Contacts' names, handles,
possibly phone numbers and private messages. Two consequences:

1. `data/` is gitignored — verify before anything lands there.
2. Fixtures checked into the repo must be **synthetic or scrubbed**. A real export must
   never be committed, even in a test fixture. Replace handles, names, and message bodies
   with invented values while preserving structure and quirks.

**Discovery questions to answer from the real data**, because documentation will not:

- Are timestamps UTC, local, or mixed within one file?
- Is there a stable per-record id, or must one be derived from content?
- Are "followers" and "following" in one file or several, and do they share a shape?
- What encoding is used for non-ASCII text, and is it consistent?
- Are deleted or edited items represented at all?
- What is absent that the feature set assumes is present?

The last question is the one most likely to force a design change, so answer it early.

**If the parser cannot supply something a feature needs**, the right response is usually to
record the gap in the platform document and degrade gracefully — not to widen
`ParsedRecord` for one platform's sake (ADR 0003, the leaky-abstraction risk).
