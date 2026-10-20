# Platform schema notes

What real exports actually contain, as opposed to what the platforms' documentation claims.

One file per platform. These are written from **real data**, not from docs, and they are the
deliverable of the schema-discovery step described in
[ADR 0003](../decisions/0003-per-platform-parsers.md) and task 0011.

| Platform | File | Status |
|---|---|---|
| Instagram | [instagram.md](instagram.md) | Discovered from a 2026-10-19 export |
| Facebook | [facebook.md](facebook.md) | Discovered from a 2026-10-19 export |
| vCard contacts | [vcard.md](vcard.md) | Discovered from a 235-card `contacts.vcf` |

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
