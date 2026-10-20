# vCard contacts (`contacts.vcf`) — observed schema

**Source:** a real 235-card export, dated 2026-10-19.
**Observed:** 2026-10-19. **Parser:** not yet written.

Structures below are transcribed from the actual file. Values are invented.

---

## 1. The headline finding

**This is the best contact data of the three sources, and the only one with real identifiers.**

| Source | Records | Has phone | Has email | Has photo | Stable id |
|---|---|---|---|---|---|
| Facebook | 353 friends | No | No | No | **No** |
| Instagram | ~1,200 relations | No | No | No | **No** |
| **vCard** | **235 cards** | **Yes (245)** | **Yes (33)** | **Yes (21)** | **Phone/email** |

Facebook and Instagram give names or handles with no way to contact anyone. The vCard gives
phone numbers, which are **globally unique identifiers** in practice. This is the anchor
source for identity resolution (DESIGN.md §4.3): match on phone first, then email, then name.

The cost: `PHOTO` properties embed **base64 images inline** (§5), and 1808 continuation
lines make this a genuinely fiddly format to parse correctly (§4).

---

## 2. Format facts

| Property | Value |
|---|---|
| Cards | 235 (`BEGIN`/`END` counts match) |
| Cards with `FN` | 232 — **3 cards have `N` but no `FN`** |
| Version | vCard **2.1** (all 235) |
| Line endings | **LF only** (no CRLF) |
| Encoding | UTF-8, **0 replacement characters** |
| Continuation lines | **1808** |
| Unique names | 230 — **2 duplicated display names** |

**vCard 2.1 is a legacy format**, but **do not assume the parser only sees 2.1.**

A Google Takeout export in the same dataset contains **vCard 3.0** contacts, and the two
versions differ in syntax that a parser touches directly
([google-takeout.md](google-takeout.md) §3):

| | This file (2.1) | Takeout (3.0) |
|---|---|---|
| Line endings | LF | **CRLF** |
| `TEL` params | `TEL;CELL` | **`TEL;TYPE=CELL`** |
| `PHOTO` | inline base64 | **separate `.jpg` file** |
| `CATEGORIES` | absent | present |

**One parser, version-aware.** A 3.0 file read with 2.1 assumptions mis-parses every `TEL`
parameter — `TYPE=CELL` becomes a parameter named `TYPE` — and a 2.1 file read with 3.0
assumptions fails on photo decoding. Detect from `VERSION:` and branch, or normalise both to
a common shape immediately after unfolding.

This is the single most likely source of a silent bug in the vCard parser, because both files
parse *without error* under the wrong assumptions and simply produce wrong values.

---

## 3. Property inventory

| Property | Count | Notes |
|---|---|---|
| `TEL` | 245 | 10 distinct parameter combinations (§6) |
| `N` | 235 | Structured name: `N:;Given;;;` |
| `FN` | 232 | Display name |
| `EMAIL` | 33 | |
| `ADR` | 32 | Postal addresses |
| `PHOTO` | 21 | Inline base64 |
| `NOTE` | 14 | |
| `ORG` | 11 | |
| `BDAY` | 9 | |

**`TEL` (245) exceeds card count (235)** — several contacts have multiple numbers. A parser
must not assume one phone per contact.

---

## 4. Line folding — the main parsing hazard

1808 continuation lines. vCard folds long lines by inserting a line break followed by a
**single space or tab**, which must be removed to reconstruct the logical line.

Raw bytes observed:

```
...AAAAQwAABtbnRyUkdCIFhZWiAH4AABAAEAAAAAAABhY3NwAAAAAAAAAAAAAAAA
 AAAAAAAAAAAAAAAAAAAAAQAA9tYAAQAAAADTLQAAAAA...
```

**A naive line-by-line parser will corrupt every folded value**, and this is not a corner
case — it affects 1808 lines in a 235-card file, i.e. most `PHOTO` values and many others.

Rules:
1. **Unfold before parsing**: a line beginning with a space or tab continues the previous line.
2. **Unfolding must not strip meaningful leading whitespace from values.** Only the single
   fold marker is removed.
3. Line endings here are LF, but the spec allows CRLF. Normalise both.

---

## 5. `PHOTO` is inline base64

```
PHOTO;ENCODING=BASE64;JPEG:/9j/4AAQSkZJRgABAQAAAQABAAD/4gHYSUNDX1...
```

- **No `data:` URI, no file path.** The image is a base64 blob in the property value,
  continuing across many folded lines.
- Media type is a bare parameter: `JPEG` — not `TYPE=JPEG`.
- 21 of 235 cards have one.

**Consequence:** parsing photos means base64-decoding a multi-line value and writing a real
file into `data/people/<id>/media/`. This is the one place where the vCard parser writes
binary data, and where a fixture must not embed a real person's photograph — use a
generated placeholder image of the same shape (task 0011 privacy rule).

**Memory note:** decode streams rather than concatenating the whole value, since a folded
base64 string for a large photo can be hundreds of kilobytes.

---

## 6. `TEL` parameters vary widely

Ten distinct forms observed:

```
TEL:            TEL;CELL        TEL;CELL;PREF     TEL;HOME
TEL;HOME;PREF   TEL;PAGER;PREF  TEL;PREF          TEL;VOICE
TEL;VOICE;PREF  TEL;WORK        TEL;WORK;FAX      TEL;WORK;PREF
```

- Parameters are **unprefixed in 2.1** (`CELL`, not `TYPE=CELL`).
- `PREF` marks the preferred number but is not guaranteed present.
- `WORK;FAX` is a fax number — **a parser that treats every `TEL` as a callable mobile will
  include fax lines.**

**Recommendation:** capture the parameters verbatim into `Extra` and pick a "primary" phone
with an explicit precedence (`CELL` > `VOICE` > `HOME` > `WORK` > others), recording which
was chosen. Do not silently drop the rest.

---

## 7. Identifiers and duplicates

- **Phone numbers are the strongest identifier available across all three sources.** Normalise
  to E.164 where possible; store the original string in `Extra` because normalisation is
  lossy and can be wrong for unusual country codes.
- **Emails are the second-strongest** (33 cards).
- **2 duplicated display names** were observed. Name alone is not sufficient — consistent
  with the Facebook finding, and further evidence that merge candidates must be user-confirmed.
- **3 cards lack `FN`.** Derive a display name from `N` (`N:;Given;;;` → `Given`), and do not
  assume `FN` exists.

`NativeId`: prefer the normalised primary phone; fall back to email; fall back to a
content hash of `N` + `FN`. Record which strategy was used.

---

## 8. What is absent

| Feature needs | Present? |
|---|---|
| Names | Yes (232 `FN`, 235 `N`) |
| Phone numbers | **Yes** — the standout |
| Emails | Yes (33) |
| Postal addresses | Yes (32) |
| Photos | Yes (21, inline base64) |
| Birthdays | Yes (9) |
| **Social handles** | **No** |
| **Relationship kind** | **No** — every card is flat; no friend/close distinction |
| **Timestamps** | **No** — no notion of when a contact was added |
| Organization | Yes (11) |

**Consequence for feature #1:** the vCard supplies contact *details* but no *relationships*
and no *time*. Tiers must be user-assigned, and there is no decay data. This is the inverse
of Facebook, which supplies dated relationships but no contact details.

**This is the core argument for the merge feature.** No single source is complete: Facebook
knows *who* and *when*, the vCard knows *how to reach them*, Instagram knows *handles*.
Feature #1's mass contact view only becomes useful when they are joined.

---

## 9. Parser plan

Maps to `IPlatformParser` from ADR 0003.

**A vCard parser should be considered shared, not platform-specific.** This format is a
standard and other sources will produce it (Google Contacts, Apple Contacts, phone exports).
Implementing it under `IPlatformParser` with `Platform = "vcard"` is correct and reuses the
seam, but the parser itself is general-purpose — note this rather than treating it as an
Instagram-style bespoke parser.

1. Read the file, normalising CRLF/LF.
2. **Unfold continuation lines first** (§4) — everything downstream depends on this.
3. Split on `BEGIN:VCARD` / `END:VCARD`; tolerate a missing trailing `END`.
4. Parse properties as `NAME;PARAM;PARAM:VALUE`, splitting the value only at the **first**
   unescaped `:` (base64 and URLs contain colons).
5. Handle vCard escaping in values: `\,` `\;` `\n` `\\`.
6. Emit one `ObservedAccount` per card, with phones/emails/addresses in `Extra`, plus the
   chosen `NativeId` strategy.
7. Decode `PHOTO` to `data/people/<id>/media/` (§5), streaming the base64.

**`ParserVersion` starts at 1.** Most likely revision triggers: parameter handling in `TEL`
(§6) and `PHOTO` decoding (§5).
