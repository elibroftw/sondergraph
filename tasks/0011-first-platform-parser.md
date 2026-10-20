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

- [x] Platforms identified (DESIGN.md §10 Q1) — **five sources**, all in hand.
- [x] Real exports obtained and placed outside the repo.
- [x] **Findings documented** in `docs/platforms/` — facebook, instagram, vcard,
      google-takeout, linkedin.
- [ ] `IPlatformParser` implementations, per ADR 0003 rules, `ParserVersion` declared.
- [ ] Fixture subsets under `tests/fixtures/<platform>/`, **scrubbed of third-party personal
      data** (see privacy note below).
- [ ] Fixture tests pass; malformed-record handling covered by a test.
- [ ] Re-parse is idempotent against the real exports, not just fixtures.

## Outcome so far: schema discovery

Full detail in `docs/platforms/`. Five sources, **four formats**, **four date representations**.

| Source | Format | Contacts | Dates | Phones | Photos |
|---|---|---|---|---|---|
| vCard (standalone) | vCard **2.1** | 235 | no | **245** | inline base64 |
| Google Takeout | vCard **3.0** | **432** | no | **243** | separate `.jpg` |
| Facebook | JSON | 353 + **179** | **yes** | **179** | 6 |
| LinkedIn | **CSV** | **683** | **yes** | no | no |
| Instagram | JSON | 1,976 rel. | unreliable | no | no |

**No single source is complete.** Facebook and LinkedIn know *when*; the two vCards know
*how to reach people*; only Instagram has social handles. Feature #1 earns its place by
joining them.

### Findings that shape implementation

**Parse the zip, not an extracted directory.** Reading Facebook's extracted tree instead of
its zip silently hid `your_imported_contacts.json` — 179 contacts with phone numbers. See
`docs/platforms/README.md`.

**vCard exists in two incompatible versions in this dataset.** 2.1 (LF, `TEL;CELL`, inline
base64 photos) and 3.0 (CRLF, `TEL;TYPE=CELL`, separate `.jpg` files). Both parse without
error under the wrong assumptions and produce wrong values. The parser must be
version-aware — this is the most likely silent bug in the whole effort.

**Four different date representations.** Unix epoch integer (Facebook), unreliable epoch
(Instagram), `dd MMM yyyy` string (LinkedIn), none (both vCards). LinkedIn's is the only
string date and needs an invariant culture.

**Instagram uses three record shapes for one concept**, and 36% of its records (blocked
profiles, removed suggestions) are **not connections** — ingesting them as contacts would
inflate the graph by a third.

**Facebook friend records have no identifier at all** (`{name, timestamp}`), so identity
resolution cannot match — merge candidates are the only path.

**Three sources are deferred**, all pending feature #3: Takeout Mail (726 MB of mbox),
LinkedIn messages, and post content. The first is two orders of magnitude larger than
everything else combined.

### Recommended implementation order

1. **vCard** — serves both `contacts.vcf` and Takeout; the identity anchor (488 phones total).
2. **LinkedIn** — 683 dated connections with stable profile URLs; simplest format.
3. **Facebook** — start with `your_imported_contacts.json` (179 phones), then friends.
4. **Instagram** — hardest; three shapes and a filename with an apostrophe.

## Notes

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
