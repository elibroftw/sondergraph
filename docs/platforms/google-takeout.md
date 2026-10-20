# Google Takeout — observed schema

**Source:** `takeout-20261020T025135Z-{1,2}-001.zip` and a second export set under
`takeout/takeout-20261020T025301Z-{1,2}-001.zip`.
**Observed:** 2026-10-19. **Parser:** not yet written.

Structures transcribed from the actual archives. Values are invented.

---

## 1. What Takeout actually is

Takeout is not one product — it is a container for whichever Google services the user
selected, split across multiple zips by size. **The archives must be surveyed, not assumed.**

| Archive | Size | Contents |
|---|---|---|
| `...T025135Z-1-001.zip` | 4 KB | `Takeout/Contacts/Starred in Android/*.vcf` (empty) |
| `...T025135Z-2-001.zip` | **486 MB** | `Takeout/Mail/{Archived,Starred,Sent}.mbox` |
| `takeout/...T025301Z-1-001.zip` | small | **`Takeout/Contacts/` — the useful one** |
| `takeout/...T025301Z-2-001.zip` | large | Mail (duplicate of the above) |

**This export contains only Contacts and Mail.** There is no `Takeout/Profile`, no
`Takeout/My Activity`, no YouTube, no Location History. If those were selected, they did not
make it into this download — or a second selection is still pending.

**Both Takeout sets appear to be duplicates of each other** (same timestamp date, different
seconds). Confirm before parsing both — ingesting the same contacts twice is exactly the
duplication the idempotency requirement (task 0004) exists to prevent.

---

## 2. Contacts layout — multiple groups, one per device

```
Takeout/Contacts/
├── All Contacts/
│   ├── All Contacts.vcf              # 432 cards
│   └── <Name>.jpg                    # 10 photo files
├── My Contacts/
│   ├── My Contacts.vcf               # 235 cards
│   └── <Name>.jpg                    # 10 photo files
├── Device contacts - Google - Pixel 9 Pro/
│   └── Device contacts - ....vcf     # 0 bytes
├── Device contacts - Samsung - SM-X820/
│   └── Device contacts - ....vcf     # 0 bytes
├── Starred in Android/
│   ├── Starred in Android.vcf        # 0 bytes
│   └── <Name>.jpg                    # 2 photo files
├── Toronto/Toronto.vcf               # 1 card
└── SP/SP.vcf                         # 1 card
```

**The groups overlap.** `My Contacts` (235) is a subset of `All Contacts` (432) — both use
`CATEGORIES:myContacts`. Ingesting every `.vcf` in the tree would produce heavy duplication.

**Recommendation: ingest `All Contacts/All Contacts.vcf` only.** It is a superset. Groups
like `Toronto` and `SP` are Google Contacts labels, and their members are already in
`All Contacts` — but note `Toronto.vcf` and `SP.vcf` carry a `CATEGORIES` value
(`Toronto,myContacts` / `SP,myContacts`) that identifies them as such.

**Empty files are normal, not an error.** Three `.vcf` files are 0 bytes. A parser must treat
an empty file as "no contacts", not as corruption.

---

## 3. vCard 3.0, not 2.1 — a genuine format difference

**This is the most important finding for implementation, and it contradicts the existing
`vcard.md`.**

| | `contacts.vcf` (standalone) | Takeout vCard |
|---|---|---|
| Version | **2.1** | **3.0** |
| Line endings | **LF** | **CRLF** |
| TEL params | `TEL;CELL` | `TEL;TYPE=CELL` |
| PHOTO | `PHOTO;ENCODING=BASE64;JPEG:<data>` | `PHOTO` with no params |
| CATEGORIES | absent | **present** (`myContacts`, `starred`, …) |
| Escaping | unknown | `\,` (58), `\n` (50), `\;` (0) |

**Two versions of the same nominal format, with different syntax.** The vCard parser must
handle both, or be version-aware. Treating 3.0 as 2.1 will mis-parse every `TEL` parameter
(`TYPE=CELL` would be read as a parameter literally named `TYPE` with value `CELL`, or worse,
as a type).

### PHOTO is different here, and much better

In the standalone `contacts.vcf`, photos were **inline base64**. In Takeout they are
**separate `.jpg` files on disk**, referenced by name from the vCard:

```
Takeout/Contacts/All Contacts/Lalit Lopez.jpg    (17,422 bytes)
```

This is far easier and is the model the UI wants: real files at known paths, no base64
decoding.

**But note the parsing hazard:** the JPEG filename is the contact's *display name*, and
display names contain spaces, emoji, and punctuation
(`2025 Lexus IS 350 F-Sport 3 DEEX 829.jpg`, `Ramp 💳.jpg`). Matching a `PHOTO` property to
its file must go through the vCard's own reference, not by reconstructing a filename from
`FN` — emoji normalisation and whitespace differences will break naive reconstruction.

**`PHOTO` properties are only 9, but there are 10 JPEG files** in `All Contacts` — so some
images exist with no corresponding `PHOTO` property. Do not assume a 1:1 mapping.

---

## 4. Record fields

From `All Contacts.vcf` (432 cards):

| Property | Count | Notes |
|---|---|---|
| `FN` | 315 | **117 cards have no `FN`** |
| `N` | 315 | Matches `FN` count |
| `TEL` | 243 | Fewer than cards — many have no phone |
| `CATEGORIES` | 235 | Group labels |
| `ADR` | 28 | |
| `EMAIL` | 26 | |
| `NOTE` | 16 | |
| `PHOTO` | 9 | Plus 10 separate jpg files |

**117 of 432 cards (27%) have neither `FN` nor `N`.** These are likely partial or
device-synced entries. A parser must skip them and count them — an account with no name is
not a usable observation (same rule as Instagram §3d).

---

## 5. Identifiers

- **Phone numbers: 243.** The strongest identifier, matching the `contacts.vcf` strategy.
- **Emails: 26.**
- **Names: 315.**
- **No stable per-card id** — no `UID` property observed.

`NativeId`: normalised phone (E.164) → email → name hash, consistent with `vcard.md` §7.

**Overlap with the standalone `contacts.vcf`:** that file has 235 cards and 245 phones;
Takeout `My Contacts` also has 235 cards and 243 phones. These are very likely **the same
contacts exported twice through different paths**. This is the single most important merge
case in the whole dataset, and it validates the merge-candidate design — the two sources
should be matched on phone number and presented as one person.

---

## 6. Mail — 486 MB of mbox

```
Takeout/Mail/
├── Archived.mbox   (343 MB)
├── Starred.mbox     (17 MB)
└── Sent.mbox       (366 MB)
```

Three **mbox** files (RFC 4155): concatenated email messages with `From ` separator lines.

**Assessment for this project: out of scope for now, but relevant later.**

- **Not needed for any of the three features as stated.** Mail is not social media, and the
  contact graph does not require it.
- **Highly relevant to feature #3 (timelines)** — `Sent.mbox` is 366 MB of "who did I talk to
  and when", which is the strongest signal for contact decay ("who haven't I spoken to in a
  year?", DESIGN.md §4.1).
- **Privacy weight is the highest of any source.** Email is far more sensitive than a
  follower list, and these mailboxes contain other people's messages in full.
- **Scale is different by two orders of magnitude.** 726 MB uncompressed versus ~2 MB of
  JSON across Facebook and Instagram combined. Parsing it needs streaming, not
  `ReadAllText`, and it is the first source that would justify the deferred SQLite cache
  (DESIGN.md §4.2).

**Recommendation:** record it, do not parse it yet. Revisit when feature #3 starts, and treat
it as a separate task with its own privacy review.

---

## 7. What is absent

| Feature needs | Present? |
|---|---|
| Contacts with phones | **Yes** — 432 cards, 243 phones |
| Contact photos | **Yes** — as separate jpg files |
| Group labels | Yes (`CATEGORIES`) |
| **Relationship kind** | **No** — `starred` is the only hint, and it is a favourite, not closeness |
| **Timestamps** | **No** |
| Social handles | No |
| Posts / activity | No (not selected) |

Same gap as the standalone vCard: detail without relationship or time. Facebook supplies the
dates; these supply the reachability.

---

## 8. Parser plan

Maps to `IPlatformParser` from ADR 0003.

1. **Read from the zip** (ADR 0003; `docs/platforms/README.md`).
2. Locate `Takeout/Contacts/All Contacts/All Contacts.vcf`. **Prefer the superset** and do
   not ingest every `.vcf` in the tree — the groups overlap (§2).
3. The vCard parser must be **3.0-aware** (§3): CRLF, `TYPE=` parameters, separate photo
   files, `CATEGORIES`, backslash escaping.
4. Treat 0-byte `.vcf` files as empty, not as errors.
5. Skip cards with neither `FN` nor `N` (27%), counting them (§4).
6. Map `CATEGORIES` values to tags; `starred` may hint at `best` tier but **do not assign a
   tier automatically** — a favourite is not a closeness judgement.
7. Emit `ObservedAccount` with phones/emails/photos in `Extra`.
8. Defer Mail (§6).

**`ParserVersion` starts at 1.** Most likely revision trigger: vCard version handling, since
this format has now appeared in two incompatible versions in one dataset.

---

## 9. Platform naming

Takeout contains multiple Google services. Modelling it as a single `google` platform would
conflate them.

**Recommendation:** treat these as separate platforms sharing a container:
- `vcard` — the format parser (handles 2.1 and 3.0, shared with `contacts.vcf` and any
  Google/Apple Contacts export)
- `google-takeout-mail` — the mbox parser, deferred

The Takeout zip is an **acquisition container**, not a platform. `IAcquirer` for `google`
unpacks the container and hands each part to the relevant parser. This matches ADR 0003's
separation: acquire knows about zips, parse knows about formats.
