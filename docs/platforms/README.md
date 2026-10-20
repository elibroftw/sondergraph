# Platform schema notes

What real exports actually contain, as opposed to what the platforms' documentation claims.

One file per platform. These are written from **real data**, not from docs, and they are the
deliverable of the schema-discovery step described in
[ADR 0003](../decisions/0003-per-platform-parsers.md) and task 0011.

## Sources

Five sources, all reviewed. **Only one is currently parsed from a directory; the rest are
zips.**

| Source | File | Format | Contacts | Dates | Phones | Photos |
|---|---|---|---|---|---|---|
| vCard (standalone) | [vcard.md](vcard.md) | vCard **2.1** | 235 | no | **245** | inline base64 |
| Google Takeout | [google-takeout.md](google-takeout.md) | vCard **3.0** | **432** | no | **243** | separate `.jpg` |
| Facebook | [facebook.md](facebook.md) | JSON | 353 + **179** | **yes** | **179** | 6 |
| LinkedIn | [linkedin.md](linkedin.md) | **CSV** | **683** | **yes** | no | no |
| Instagram | [instagram.md](instagram.md) | JSON | 1,976 rel. | unreliable | no | no |

**Four different formats** (vCard 2.1, vCard 3.0, JSON, CSV) and **four different date
representations** (none, none, Unix epoch, `dd MMM yyyy`). This is the strongest evidence yet
for ADR 0003: the mess is real and it belongs in per-platform parsers.

### Deferred: mail and messages

Three sources are **recorded but not parsed**, pending feature #3:

| Source | Size | Why deferred |
|---|---|---|
| Takeout `Mail/*.mbox` | **726 MB** | Out of scope for the three stated features; highest privacy weight; two orders of magnitude larger than everything else combined |
| LinkedIn `messages.csv` | 830 KB | Same |
| Facebook/Instagram posts | — | **Absent from the exports entirely** — blocks feature #2 |

## Parse the archive, not an extracted directory

**This is the first rule, and it was learned the hard way.** The exports are `.zip` files and
the zips are authoritative. An extracted directory is a derived artifact that can be partial,
interrupted, or overwritten by a later export of the same account.

Reading the extracted Facebook tree instead of its zip **silently hid
`your_imported_contacts.json` — 179 contacts with phone numbers**, one of the most valuable
files in the dataset. It also produced understated counts throughout the first revision of
that document.

`IAcquirer` for export-bundle platforms reads the archive directly
(`System.IO.Compression.ZipFile`). An extracted tree is an optional fallback, never the
primary path.

**Note:** the standalone `contacts.vcf` and the `facebook/`+`instagram/` directories are the
exception — they were placed on disk directly, not extracted from a zip. Only those three are
read from the filesystem.

## Duplication risk

**The same contacts appear in more than one source.** This is not a bug to work around; it is
the primary use case for feature #1.

- Standalone `contacts.vcf` (235 cards, 245 phones) and Takeout `My Contacts`
  (235 cards, 243 phones) are very likely **the same people exported twice**.
- Takeout `My Contacts` is a subset of `All Contacts` (432).
- Two Takeout export sets exist with different timestamps — possibly duplicates.

Ingesting naively would triple-count. Matching on phone number is the intended resolution,
and every unmatched case becomes a merge candidate rather than an automatic merge
(DESIGN.md §4.3).

## Why these documents exist

A parser written from documentation will be wrong in ways that only show up against real
data. Recording what was actually observed means:

- the next person (including future us) does not repeat the discovery;
- parser behaviour that looks odd has a documented reason;
- gaps — data a feature needs but the platform does not provide — are recorded as findings
  rather than discovered late as bugs.

## Privacy rule

**These documents describe structure, not people.** No real names, handles, phone numbers,
or message contents. Where a concrete value is needed to explain a quirk, it is replaced
with an invented one of the same shape.

The same rule applies to test fixtures: see task 0011.
