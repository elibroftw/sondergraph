# LinkedIn export — observed schema

**Source:** `Basic_LinkedInDataExport_10-20-2026.zip.zip`.
**Observed:** 2026-10-19. **Parser:** not yet written.

Structures transcribed from the actual archive. Values are invented.

---

## 1. The headline finding

**LinkedIn is CSV, not JSON — and its connections are the best-dated contact source in the
entire dataset.**

| | Facebook | Instagram | vCard | **LinkedIn** |
|---|---|---|---|---|
| Format | JSON | JSON | vCard | **CSV** |
| Contacts | 353 | 1,976 | 235–432 | **683** |
| Dates | yes | unreliable | **no** | **yes** |
| Profile URL | no | yes | no | **yes** |
| Employer / role | no | no | no | **yes** |

`Connections.csv` is 683 connections, **every one with a date**, plus a profile URL. That
makes LinkedIn the best source for feature #3's timelines after Facebook, and the only source
that carries *context* (where someone works) alongside the relationship.

---

## 2. Archive contents

19 files, all CSV except two nested paths:

```
Basic_LinkedInDataExport/
├── Connections.csv                 # 683 connections  ← the important one
├── Invitations.csv                 # 3,693 bytes
├── messages.csv                    # 830,124 bytes  ← see §6
├── Profile.csv
├── Email Addresses.csv
├── PhoneNumbers.csv
├── Ad_Targeting.csv                # 28,603 bytes
├── Registration.csv
├── Rich_Media.csv
├── Company Follows.csv
├── Positions.csv / Education.csv / Skills.csv / Projects.csv
├── SavedJobAlerts.csv
├── learning_role_play_messages.csv / learning_coach_messages.csv
├── Whatsapp Phone Numbers.csv
└── Jobs/Saved Jobs.csv
```

**Only `Connections.csv` is a contact graph.** The rest is profile, job-seeking, and
advertising data. `Ad_Targeting.csv` is worth noting: it is LinkedIn's inferred profile of
the user, not contacts, and should not be ingested as people.

---

## 3. `Connections.csv` — the format trap

**The file does not begin with a header.** It begins with a note block:

```
Notes:
"When exporting your connection data, you may notice that some of the email addresses are
missing. You will only see email address..."

First Name,Last Name,URL,Email Address,Company,Position,Connected On
Ryan,Zhu,https://www.linkedin.com/in/example-user,,Example Corp,IT Project Specialist,16 Oct 2026
```

**A naive CSV reader will treat the first note line as the header row** and produce garbage
for every record. This is the primary parsing hazard, and unlike the vCard folding problem it
fails loudly rather than subtly — but it fails.

**Correct approach:** scan for the first line beginning `First Name,` and parse from there.

Columns: `First Name`, `Last Name`, `URL`, `Email Address`, `Company`, `Position`,
`Connected On`.

---

## 4. Field quality

| Column | Populated | Notes |
|---|---|---|
| `First Name` / `Last Name` | 683 / 683 | Always present |
| `URL` | 683 | Profile URL — a **stable identifier** |
| `Connected On` | 683 | Date of connection |
| `Company` | most | Free text |
| `Position` | most | Free text, **contains commas — quoted** |
| `Email Address` | **few** | LinkedIn withholds these; usually blank |

`Position` values contain commas and are CSV-quoted, e.g.
`"Analyst, Equity Research, Metals and Mining"`. Any hand-rolled splitter will corrupt these.

Names may be **single-character or unusual** (a row with `First Name` of length 1 was
observed). Do not filter on name length.

**`Email Address` is largely empty** — LinkedIn states this in its own note (§3). It cannot
serve as an identifier here, unlike the vCard.

---

## 5. Identifiers and dates

**`URL` is the strongest identifier in the dataset for social accounts.** Unlike Instagram
handles, a LinkedIn profile URL is stable and globally unique.

`NativeId`: the URL. Fall back to normalised full name if absent (it never was, in this
export).

**`Connected On` format: `dd MMM yyyy`** (e.g. `16 Oct 2026`). This is a **different date
format from every other source**:

| Source | Date format |
|---|---|
| Facebook / Instagram | Unix epoch **integer** |
| Instagram hrefs | Unix epoch integer |
| **LinkedIn** | **`dd MMM yyyy` string** |
| vCard | no dates at all |

Parse with an explicit invariant culture — `dd MMM yyyy` under a non-English locale will fail
or silently mis-parse.

**Date range observed:** 2021-04-01 through 2026-10-31.

**Caveat on ordering:** these dates are *strings*, so lexicographic sorting gives the wrong
order (`01 Apr 2021` sorts before `28 Sep 2026` but also before `31 Oct 2024` incorrectly in
general). Sort by parsed date, never by the raw string.

---

## 6. `messages.csv` — deferred

830 KB of LinkedIn messages, with participant names and timestamps.

**Same assessment as Takeout Mail:** highly relevant to feature #3 (decay — "who haven't I
spoken to in a year?"), but out of scope for the three stated features and carrying real
privacy weight. Record it, do not parse it yet.

---

## 7. What is absent

| Feature needs | Present? |
|---|---|
| Connections | **Yes** — 683, all dated |
| Profile URLs | **Yes** |
| Employer / position | Yes |
| **Phone numbers** | Only the user's own (`PhoneNumbers.csv`) |
| Email addresses | Rarely |
| Photos | **No** |
| Relationship kind | **No** — every connection is equal |

LinkedIn has no notion of closeness. All 683 are equivalent, so tiers must be user-assigned
as with the vCard.

---

## 8. Parser plan

Maps to `IPlatformParser` from ADR 0003.

1. **Read from the zip.**
2. **Skip the note block**: find the header line starting `First Name,` before parsing (§3).
3. Parse as RFC 4180 CSV — quoted fields with embedded commas (§4). Do not split on commas.
4. Parse `Connected On` as `dd MMM yyyy` with `CultureInfo.InvariantCulture` (§5).
5. `NativeId` = profile URL.
6. Emit `ObservedAccount` (name, URL, company/position in `Extra`) +
   `ObservedInteraction` (kind `Connected`, timestamp).
7. Ignore every other CSV in the archive — they describe the user, not their contacts (§2).
8. Defer `messages.csv` (§6).

**`ParserVersion` starts at 1.** Most likely revision trigger: the note-block preamble, since
its exact wording and line count may vary by export.
