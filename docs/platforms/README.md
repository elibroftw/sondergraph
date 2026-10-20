# Platform schema notes

What real exports actually contain, as opposed to what the platforms' documentation claims.

One file per platform. These are written from **real data**, not from docs, and they are the
deliverable of the schema-discovery step described in
[ADR 0003](../decisions/0003-per-platform-parsers.md) and task 0011.

| Platform | File | Source | Contacts | Phones |
|---|---|---|---|---|
| Instagram | [instagram.md](instagram.md) | zip, 2026-10-19 | 1,976 relations | no |
| Facebook | [facebook.md](facebook.md) | zip, 2026-10-19 | 353 friends + **179 imported contacts** | **yes (179)** |
| vCard | [vcard.md](vcard.md) | `contacts.vcf` | 235 cards | **yes (245)** |

## Parse the archive, not an extracted directory

**This is the first rule, and it was learned the hard way.** The exports are `.zip` files and
the zips are authoritative. An extracted directory is a derived artifact that can be partial,
interrupted, or overwritten by a later export of the same account.

Reading the extracted Facebook tree instead of its zip **silently hid
`your_imported_contacts.json` — 179 contacts with phone numbers**, the single most valuable
file in any of the three exports. It also produced understated counts throughout the first
revision of that document.

`IAcquirer` for export-bundle platforms reads the archive directly
(`System.IO.Compression.ZipFile`). An extracted tree is an optional fallback, never the
primary path.

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
