# Facebook export — observed schema

**Source:** a real 2026-10-19 export (`facebook-<user>-2026-10-19-*.zip`), extracted.
**Observed:** 2026-10-19. **Parser:** not yet written.

Structures below are transcribed from the actual files. Values are invented.

---

## 1. The headline finding

**Facebook friend records are clean and uniform — the opposite of Instagram.**

Every friend-ish file uses the *same* shape: a top-level object with one plausibly-named key
whose value is a list of `{name, timestamp}`. There is no nesting, no `label_values`, no
placeholder garbage.

The complication is not shape, it is **semantics**: the same `{name, timestamp}` pair means
different things in different files, and only the filename tells you which.

---

## 2. Layout

**Counts are from the original zip** (`facebook-<user>-2026-10-19-*.zip`, 67 entries), which
is the authoritative source. Reading the extracted directory instead gives different numbers
and hides one file entirely — see §11.

```
facebook/
├── connections/
│   ├── friends/
│   │   ├── your_friends.json                # 353  friends_v2
│   │   ├── sent_friend_requests.json        # 45   sent_requests_v2
│   │   ├── removed_friends.json             # 22   deleted_friends_v2
│   │   ├── rejected_friend_requests.json    # 16   rejected_requests_v2
│   │   ├── received_friend_requests.json    # 2    received_requests_v2
│   │   ├── suggested_friends.json           # 106  placeholder shape (§4)
│   │   ├── people_you_may_know.json         #      placeholder shape (§4)
│   │   └── your_post_audiences.json         # 4    placeholder shape (§4)
│   └── followers/
│       └── who_you've_followed.json         # 8    following_v3
├── personal_information/
│   └── other_personal_information/
│       └── your_imported_contacts.json      # 179  contacts WITH PHONE NUMBERS (§10)
├── your_facebook_activity/
│   ├── comments_and_reactions/
│   │   ├── comments.json                    # 48   comments_v2
│   │   ├── likes_and_reactions.json         # 244  bare list
│   │   └── likes_and_reactions_1.json
│   ├── posts/                               # media only — 6 jpg, no text (§7)
│   ├── your_places/                         # 3 places_you've_created
│   ├── saved_items_and_collections/
│   ├── activity_you're_tagged_in/
│   └── other_activity/
└── preferences/
```

---

## 3. The friend record shape

```json
{
  "friends_v2": [
    { "name": "Example Person", "timestamp": 1419867088 }
  ]
}
```

**That is the whole record.** No id, no handle, no URL, no avatar, no mutual-friend count.

| File | Wrapper key | Meaning of the row |
|---|---|---|
| `your_friends.json` | `friends_v2` | Current friends |
| `sent_friend_requests.json` | `sent_requests_v2` | Requests you sent (may be pending) |
| `received_friend_requests.json` | `received_requests_v2` | Requests you received |
| `rejected_friend_requests.json` | `rejected_requests_v2` | Requests you rejected |
| `removed_friends.json` | `deleted_friends_v2` | Former friends |
| `who_you've_followed.json` | `following_v3` | Accounts you follow |

**The wrapper key is not a reliable discriminator.** It varies (`friends_v2`, `deleted_friends_v2`,
`following_v3`) and carries no consistent convention. **Dispatch on filename**, and treat the
wrapper key as a validation check rather than the source of truth.

---

## 4. The placeholder shape (a real trap)

`suggested_friends.json`, `people_you_may_know.json`, and `your_post_audiences.json` share a
completely different shape:

```json
[
  {
    "media": [],
    "label_values": [ { "label": "...", "vec": "..." } ],
    "fbid": "..."
  }
]
```

Two problems:

1. **`label_values` here uses `vec`, not `value`** — different from Instagram's `close_friends.json`,
   which uses `value`. A shared helper between the two parsers would silently read nothing.
2. **These carry no usable name.** `suggested_friends.json` has 106 entries with no `name`
   field at all. They are *suggestions*, not connections.

**Finding: suggested friends and post audiences should not be ingested as contacts.** They
are algorithmic output about people the user has no relationship with — including them would
inflate the contact graph with strangers. The parser should skip these files entirely, or
ingest them into a clearly separate bucket if a future feature wants them.

This is also the only place `fbid` appears (§5).

---

## 5. Identifiers

**There is no identifier on a friend record.** Only `name` and `timestamp`.

Consequence, per ADR 0003 rule 1: the parser must derive an id. The defensible key is the
**normalised display name** (trimmed, case-folded, whitespace-collapsed). This is weak — two
people can share a name — and it is the single most important modelling finding for Facebook.

`fbid` appears only in the placeholder files (§4), where it identifies a stranger, so it
cannot serve as the key for actual friends.

**Implication for identity resolution (DESIGN.md §4.3):** Facebook cannot be merged
automatically even in principle, because there is nothing to match on. Two records named
"Example Person" are genuinely ambiguous. The merge-candidate flow is not a nicety here —
it is the only way to resolve Facebook contacts at all.

---

## 6. Timestamps

- **Unix epoch seconds, as integers.**
- **Historically meaningful**, unlike Instagram's. Observed ranges are plausible and span
  years of real activity:

| File | Earliest | Latest | Future-dated |
|---|---|---|---|
| `your_friends.json` | 2014-11-30 | 2026-01-06 | 0 |
| `sent_friend_requests.json` | 2014-12-11 | 2026-06-14 | 0 |
| `who_you've_followed.json` | 2015-03-27 | 2026-03-18 | 0 |
| `removed_friends.json` | 2016-01-14 | 2021-08-18 | 0 |
| `rejected_friend_requests.json` | 2016-09-05 | 2024-01-08 | 0 |

**No future-dated timestamps**, and the earliest values line up with plausible account
creation. This data supports the staleness feature ("who haven't I spoken to since X") in a
way the Instagram export does not.

Semantics per file: friend → when you became friends; removed → when unfriended; sent →
when the request was sent. **These are different events sharing one field name**, so the
parser must map them to distinct `InteractionKind` values rather than one generic relation.

---

## 7. Other activity

`comments_and_reactions/comments.json` (48 entries) has a richer shape and **is** useful for
feature #3 timelines:

```json
{
  "comments_v2": [
    {
      "timestamp": 1419867088,
      "title": "...",
      "data": [ { "comment": { "comment": "...", "author": "..." } } ],
      "attachments": [ { "data": [ { "media": { "uri": "..." } } ] } ]
    }
  ]
}
```

Note `attachments[].data[].media.uri` is a **relative path** into
`your_facebook_activity/posts/media/...`. So media does exist on disk (§8) but is referenced
only from activity records, never from a posts file.

---

## 8. What is absent

| Feature needs | Present? |
|---|---|
| Friends / requests / removed | **Yes**, uniform shape (§3) |
| Follows | Yes (8) |
| Comments | Yes (48), with media references |
| Reactions | Present but shaped inconsistently (§9) |
| **Posts** | **No posts JSON at all** — only `posts/media/` images |
| Post text | **No.** Media exists with no accompanying text records |
| **Messages** | **No** |
| Phone numbers | **No** |
| Email addresses | **No** |
| Profile URLs | **No** |
| Avatars | Only profile-picture images under `posts/media/Profilepictures_*` |

**Consequence for feature #2 (Hugo export + RSS):** as with Instagram, this export cannot
supply posts for a blog export — only images. Six JPGs total.

**Consequence for feature #1 (mass contact view):** this is the *better* of the two
connections sources. 353 friends with real, dated friendship timestamps is a usable contact
graph, and it is the one the mass view should be built from.

---

## 9. Known quirks

- **`likes_and_reactions.json` has a different top-level type from `comments.json`** — it
  deserialises as a bare array, not an object with a wrapper key. A parser that assumes
  `{key: [...]}` for every file under `comments_and_reactions/` will fail. **Check the
  top-level type before dispatching.**
- **Two reaction files** (`likes_and_reactions.json`, `likes_and_reactions_1.json`) — the
  `_1` suffix indicates Meta split the export across files. Assume pagination is possible for
  any file type and glob rather than hardcode.
- **Apostrophes in paths**: `who_you've_followed.json`, `activity_you're_tagged_in/`.
  Do not construct these names programmatically.
- No encoding problems: **all 73 JSON files decoded as clean UTF-8**, no BOM, no replacement
  characters.

---

## 10. `your_imported_contacts.json` — the most valuable file in the export

**179 contacts with phone numbers.** This is the only place in any Facebook export where a
contact has a way to actually reach them, and it was missed on the first pass because it sits
under `personal_information/other_personal_information/` rather than anywhere near
`connections/`.

```json
[
  {
    "timestamp": 1727408809,
    "media": [],
    "label_values": [
      { "label": "Update time",   "timestamp_value": 1778407262 },
      { "label": "Contact name",  "value": "Example Name" },
      { "label": "Contact point", "value": "+15550100123" }
    ],
    "fbid": "1278691909966772"
  }
]
```

**A third `label_values` sub-shape.** Here the entries are `{label, timestamp_value}` and
`{label, value}` — and `timestamp_value` is a **second** form not seen elsewhere:

| File | `label_values` entry form |
|---|---|
| Instagram `close_friends.json` | `{label, value}` |
| Facebook `your_imported_contacts.json` | `{label, value}` **and** `{label, timestamp_value}` |
| Facebook `people_you_may_know.json` | `{label, vec}` |

**`vec` is not `value`.** A shared helper across these parsers would silently read nothing
from at least one of them. This is a concrete instance of the leaky-abstraction risk in
ADR 0003.

Two distinct timestamps per record, and they differ: `timestamp` (1727408809 → 2024-09-27)
and `Update time.timestamp_value` (1778407262 → 2026-05-10). Neither is clearly "when the
contact was created". Record both in `Extra` and do not pick a winner without evidence.

**Modelling consequence:** this file supplies phone numbers, which are the strong
identifiers the rest of the Facebook export lacks (§5). Facebook friend records and imported
contacts can potentially be joined **by name**, which is weak but is the only available
bridge. Expect it to be imperfect and route mismatches through merge candidates.

---

## 11. Extraction is not the same as the zip

**Counts read from the extracted directory disagree with the zip, and one file disappears.**

| | Zip | Extracted directory |
|---|---|---|
| Facebook entries | 67 | 79 files |
| `your_imported_contacts.json` | **present** | **absent** |

The extraction on disk contains files the zip does not (an extra 12, apparently from a
different or partial extraction) and is missing the imported-contacts file entirely. Reading
it produced understated counts throughout this document's first revision.

**Rule: parse the zip, not the extracted tree.** This is a real requirement rather than a
convenience:

- The zip is what the platform actually produced. An extraction is a derived artifact.
- The zip carries the platform's own directory structure and is reproducible.
- Extraction can be partial, interrupted, or overwritten by a second export of the same
  account.

`IAcquirer` for export-bundle platforms should therefore **read the archive directly**
(`System.IO.Compression.ZipFile`), never assume an extracted directory. Supporting an
already-extracted tree is a convenience fallback, not the primary path.

---

## 12. Parser plan

Maps to `IPlatformParser` from ADR 0003.

1. **Read from the zip**, not the extracted tree (§11).
2. **Allowlist the connection files by name**, rather than globbing everything:
   `your_friends`, `sent_friend_requests`, `received_friend_requests`,
   `rejected_friend_requests`, `removed_friends`, `who_you've_followed`,
   `your_imported_contacts`. Explicitly **exclude** `suggested_friends`,
   `people_you_may_know`, and `your_post_audiences` (§4).
3. Read the wrapper key from the file rather than assuming it; validate it against the
   expected value for that filename and warn on mismatch. Note that the imported-contacts
   file is a **bare array**, not a wrapped object.
4. Derive `NativeId` from the normalised name (§5), and record the name in `Extra` so
   ambiguity is auditable — **except** for imported contacts, which should key on the
   normalised phone number (E.164), matching the vCard strategy.
5. Map each file to a distinct `InteractionKind` — friend, request-sent, request-received,
   request-rejected, unfriended, followed (§6) — rather than one generic relation.
6. Emit `ObservedAccount` (name, plus phone where available) + `ObservedInteraction`
   (kind, timestamp).
7. Optionally parse `comments.json` for timeline data, keeping it out of the contact graph.

**Priority note:** `your_imported_contacts.json` is more valuable to feature #1 than the
friend list, despite being smaller — 179 joins with a name *and* a phone number outperform
353 names with no way to contact anyone. Implement it first.

**`ParserVersion` starts at 1.** The most likely revision triggers are the reaction-file
shape (§9) and the three `label_values` sub-shapes (§10).
