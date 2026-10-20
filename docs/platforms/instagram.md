# Instagram export — observed schema

**Source:** a real 2026-10-19 export (`instagram-<user>-2026-10-19-*.zip`), extracted.
**Observed:** 2026-10-19. **Parser:** not yet written.

Structures below are transcribed from the actual files. Values are invented.

---

## 1. The headline finding

**Instagram uses at least three different record shapes for the same concept** — a followed
account — depending on which file you read. There is no single "person" record.

| File | Top level | Record shape |
|---|---|---|
| `following.json` | object, key `relationships_following` | `{title, string_list_data[]}` |
| `followers_1.json` | **bare array** | `{title, media_list_data[], string_list_data[]}` |
| `close_friends.json` | **bare array** | `{timestamp, media[], label_values[], fbid}` |

This is the concrete justification for [ADR 0003](../decisions/0003-per-platform-parsers.md):
one platform, three shapes, and a per-platform parser is where that mess belongs.

---

## 2. Layout

```
instagram/
├── connections/
│   └── followers_and_following/
│       ├── following.json                    # 666 accounts (relationships_following)
│       ├── followers_1.json                  # 526 accounts
│       ├── close_friends.json                # 11 accounts
│       ├── blocked_profiles.json
│       ├── restricted_profiles.json
│       ├── hide_story_from.json
│       ├── pending_follow_requests.json
│       ├── recent_follow_requests.json
│       ├── follow_requests_you've_received.json
│       ├── recently_unfollowed_profiles.json
│       └── removed_suggestions.json
└── preferences/
    └── your_topics/
        └── your_ads_see_more/see_less_topics.json
```

**Only `connections/` is currently useful.** There is no posts, messages, or media
directory in this export — see §6.

Note the apostrophe in `follow_requests_you've_received.json`. A parser that constructs
filenames programmatically must handle it; on a filesystem with a different normalisation
this could also be a Unicode apostrophe (U+2019) rather than ASCII (U+0027). **Do not
hardcode the name** — glob the directory.

---

## 3. The three record shapes

### 3a. `following.json` — nested under a key

```json
{
  "relationships_following": [
    {
      "title": "example_user",
      "string_list_data": [
        {
          "href": "https://www.instagram.com/_u/example_user",
          "timestamp": 1792353389
        }
      ]
    }
  ]
}
```

- The handle is in **`title`**, *and* inside `string_list_data[0].href`.
- **No `value` key** in `string_list_data` — unlike 3b. This asymmetry is a trap.
- `timestamp` is a Unix epoch **integer**, seconds.

### 3b. `followers_1.json` — bare array, `value` present

```json
[
  {
    "title": "",
    "media_list_data": [],
    "string_list_data": [
      {
        "href": "https://www.instagram.com/example_user",
        "value": "example_user",
        "timestamp": 1792353390
      }
    ]
  }
]
```

- **`title` is empty** here. The handle is in `string_list_data[0].value`.
- `href` uses `/example_user`, whereas 3a used `/_u/example_user`.
- Includes `media_list_data`, which is empty for followers.

**A parser must not read `title` unconditionally.** For followers it is blank; for following
it is the handle. Correct order: prefer `string_list_data[].value`, fall back to `title`,
fall back to parsing `href`.

### 3c. `close_friends.json` — keys/values, no handle at top level

```json
[
  {
    "timestamp": 1777118439,
    "media": [],
    "label_values": [
      { "label": "URL",      "value": "" },
      { "label": "Name",     "value": "Example Name" },
      { "label": "Username", "value": "example_user" }
    ],
    "fbid": "..."
  }
]
```

- Handle is in `label_values` where `label == "Username"`.
- **Display name is available** — the only one of the three shapes that gives both.
- `URL` may be the empty string.
- `fbid` is present here and absent from 3a/3b.

### 3d. Placeholder-only shape

`restricted_profiles.json` (and `people_you_may_know.json` on Facebook) has this shape:

```json
{ "timestamp": 0, "media": [], "label_values": [...], "fbid": "..." }
```

Observed with **three** `label_values` entries and no handle at all in one case. These are
schema-valid but content-empty. A parser must emit nothing for them rather than a record
with a blank handle — **an account with no handle is not a usable observation.**

---

## 4. Identifiers

**There is no stable per-account id in the common shapes.** Only `close_friends.json`
carries `fbid`, and it is absent from `following.json` and `followers_1.json`.

Consequence, per ADR 0003 rule 1: the parser must derive an id. The defensible key is the
**handle** (normalised: lowercase, trimmed), because it is present in all three shapes and
is what a human would use. `fbid` is recorded in `Extra` when present but cannot be the
primary key, since it would make the same account appear as two records across files.

`ObservedAccount.NativeId` should therefore be the normalised handle, with a documented note
that it is *derived, not platform-issued*. If Instagram ever changes handles, this produces
a duplicate — which is exactly the merge-candidate case DESIGN.md §4.3 is for.

---

## 5. Timestamps

- **Unix epoch seconds, as integers.** Not ISO 8601 strings, not milliseconds.
- Present in all three shapes.
- Semantics are **unclear and probably inconsistent**: in `following.json` it plausibly means
  "when you started following"; in `followers_1.json` "when they followed you". For
  `close_friends.json` it is likely "when added to the list".
- **Observed values are in the future** (e.g. `1792353389` = 2026-10-18, and others later),
  which is consistent with "now" at export time rather than any historical event.

**Finding:** the timestamp cannot be trusted as a relationship-start date without evidence.
The parser should record it as `ObservedInteraction.OccurredAt` with the semantic uncertainty
noted in `Extra`, rather than presenting it as a fact.

---

## 6. What is absent

This is the section most likely to force design changes, so it comes before implementation.

| Feature needs | Present in this export? |
|---|---|
| Contacts / followers / following | **Yes**, in three shapes (§3) |
| Close friends | **Yes** (`close_friends.json`) — maps directly to our `best` tier |
| Blocked / restricted / hidden | Yes, as relation types |
| **Posts** | **No.** No posts directory in this export |
| **Media** | **No.** `media_list_data` is empty throughout |
| **Messages** | **No** |
| Display names | **Only** for close friends (§3c) |
| Profile URLs | Yes, two different URL forms |
| Avatar images | No |

**Consequences for feature #2 (Hugo export + RSS):** this export cannot supply it at all for
Instagram. That feature will need either a different export request (Instagram lets you
choose what to include — posts may simply not have been selected) or a different platform.

**Consequence for feature #1 (mass contact view):** workable. Handles plus a coarse relation
kind is enough to build the contact graph, which is the highest-value part of this data.

---

## 7. Encoding

- All 73 JSON files decode as **UTF-8 with no replacement characters**.
- No BOM observed.
- No mojibake in the sampled files.

So encoding is *not* a problem for this export — worth stating because Meta exports are
widely reported to have encoding issues, and a future export might differ.

---

## 8. Parser plan

Maps to `IPlatformParser` from ADR 0003. A single parser handles all files; the shapes are
variations within one platform, not separate platforms.

1. Glob `connections/followers_and_following/*.json` — never hardcode filenames (§2).
2. Dispatch on **observed shape**, not filename, where possible:
   - object with `relationships_following` → 3a
   - array of `{title, media_list_data, string_list_data}` → 3b
   - array of `{label_values, fbid}` → 3c/3d
3. Extract handle via the fallback chain: `value` → `title` → parse `href`.
4. Skip records with no usable handle (§3d), counting them.
5. Map relation kind from the source file:
   `following` → followee; `followers_1` → follower; `close_friends` → `best` tier hint;
   `blocked`/`restricted` → negative relations, recorded but not shown as contacts.
6. Emit `ObservedAccount` + `ObservedInteraction`, with `fbid` and raw timestamps in `Extra`.

**`ParserVersion` starts at 1.** Treat the shape-dispatch in step 2 as the most likely thing
to need revision.
