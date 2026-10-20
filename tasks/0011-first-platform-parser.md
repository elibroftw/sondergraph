---
id: "0011"
title: First real platform parsers
status: pending
priority: high
labels: [ingest, parsers, schema-discovery]
dependencies: ["0004"]
created: 2026-10-19
---

## Context

ADR 0003 defines the parser seam; task 0004 builds it. This task proves it against real
exports, which is also the **schema-discovery** work: figuring out what a platform actually
produces, as opposed to what its documentation claims.

**Data is now in hand.** Three real exports were obtained and analysed on 2026-10-19:

- Facebook (`facebook/`) — 353 friends, uniform `{name, timestamp}` shape
- Instagram (`instagram/`) — ~1,200 relations across **three different record shapes**
- vCard (`contacts.vcf`) — 235 cards with phone numbers and inline base64 photos

**The schema discovery is done. Findings are in `docs/platforms/`.** What remains is
implementing parsers against documented shapes.

## Acceptance criteria

- [x] Platforms identified (DESIGN.md §10 Q1) — Facebook, Instagram, vCard; all three in hand.
- [x] Real exports obtained and placed outside the repo.
- [x] **Findings documented** in `docs/platforms/{facebook,instagram,vcard}.md`.
- [ ] `IPlatformParser` implementations, per ADR 0003 rules, `ParserVersion` declared.
- [ ] Fixture subsets under `tests/fixtures/<platform>/`, **scrubbed of third-party personal
      data** (see privacy note below).
- [ ] Fixture tests pass; malformed-record handling covered by a test.
- [ ] Re-parse is idempotent against the real exports, not just fixtures.

## Outcome so far: schema discovery

Full detail in `docs/platforms/`. The findings that shape implementation:

**The three sources are complementary, and none is complete.**

| Source | Who | When | How to reach | Handles |
|---|---|---|---|---|
| Facebook | 353 names | **Yes** (real, dated 2014–2026) | No | No |
| Instagram | ~1,200 handles | Yes (but unreliable, see below) | No | **Yes** |
| vCard | 235 names | **No** | **Phone, email** | No |

This is the strongest argument for feature #1: the mass contact view only becomes useful
when these are joined.

**Instagram uses three record shapes for one concept.** `following.json` nests under a key;
`followers_1.json` is a bare array with a `value` field; `close_friends.json` uses
`label_values` with no top-level handle. One is missing `value`; another has an empty `title`.
Practical confirmation that the mess belongs in a per-platform parser (ADR 0003).

**Facebook is uniform in shape, ambiguous in semantics.** Every friend record is
`{name, timestamp}` — no id at all. Identity resolution cannot work by matching; merge
candidates are the *only* way to resolve Facebook contacts. That is a design input for 0003.

**Facebook timestamps are historically real** (2014–2026, none future-dated). Instagram's are
not: observed values are at or after export time, so they cannot be trusted as
relationship-start dates.

**The vCard is the identity anchor.** Phone numbers are the only strong identifiers across
all three sources, so resolution should be phone → email → name. It is also the only source
with photos, and those are **inline base64 across folded lines** — 1808 continuation lines in
a 235-card file, which is the main parsing hazard.

**A vCard parser is general-purpose, not platform-specific.** The format is a standard;
Google Contacts and Apple Contacts produce it too. Implement it behind `IPlatformParser` for
seam consistency, but do not treat it as a bespoke one-off.

## Notes

**Suggested order: vCard first, then Facebook, then Instagram.**
- vCard is the most valuable (identifiers), most self-contained, and most reusable.
- Facebook is uniform in shape — a quick win once the pipeline exists.
- Instagram is three shapes and a filename with an apostrophe; it will exercise the seam
  hardest, so it is the best test *after* the pipeline is proven simple.

**Privacy — the exports contain other people's data.** Real names, phone numbers, and
photographs of people who did not choose to be in this system.

1. Exports live **outside the repo**, in `~/OneDrive/Documents/Sonder Graph Data/`.
2. Fixtures checked into the repo must be **synthetic or scrubbed** — invented names, fake
   phone numbers in reserved ranges (e.g. `+1 555 0100`–`+1 555 0199`), and generated
   placeholder images. Preserve structure and quirks; replace every real value.
3. Never commit an export, not even a "small sample".

**Open question:** Facebook posts and Instagram posts are **absent from these exports** —
only media files, no text. Feature #2 (Hugo + RSS) cannot be built from what we have. Either
a new export must be requested with posts included, or feature #2 targets a different source.
Confirm before starting 0005.
