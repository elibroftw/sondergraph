# sondergraph

> *everything, all at once.*

Every contact is a person with a life as vivid and complex as your own. This tool
maps them — their platforms, their circles, your history with them — into a single
graph you actually own.

## Status

Pre-alpha. Scaffolding and design in progress.

- [DESIGN.md](./DESIGN.md) — architecture, stack decisions, and open questions
- [tasks/](./tasks/) — task tracking, as markdown files

## Running it

Sondergraph is a CLI plus a local web UI. Both use the same core (ADR 0001).

```bash
# 1. Create a workspace (a directory you own; keep it outside this repo)
sg init ~/sondergraph-data

# 2. Ingest exports. Zips are read directly - never extract them first.
sg ingest parse ~/exports/takeout-contacts-001.zip --workspace ~/sondergraph-data
sg ingest parse ~/exports/contacts.vcf              --workspace ~/sondergraph-data
sg ingest parse ~/exports/linkedin.zip              --workspace ~/sondergraph-data
sg ingest parse ~/exports/facebook.zip              --workspace ~/sondergraph-data
sg ingest parse ~/exports/instagram.zip             --workspace ~/sondergraph-data

# 3. Browse
sg list  --workspace ~/sondergraph-data
sg stats --workspace ~/sondergraph-data
sg show  <id> --workspace ~/sondergraph-data

# 4. Web UI
dotnet run --project src/Sondergraph.Api
```

### Supported sources

| Source | Parser | Notes |
|---|---|---|
| Google Takeout contacts | `vcard` | vCard 3.0; photos are sibling `.jpg` files |
| Standalone `contacts.vcf` | `vcard` | vCard 2.1; photos are inline base64 |
| LinkedIn export | `linkedin` | CSV with a note block before the header |
| Facebook export | `facebook` | Includes `your_imported_contacts.json` (the only phone numbers) |
| Instagram export | `instagram` | Three record shapes; deleted-account tombstones skipped |

### How people are merged

Records are merged only when they share a **strong identifier** — a phone number, an email,
or a profile URL. Names are never used to merge, because two people can share one; instead
suspicious pairs are surfaced as candidates for you to confirm.

Re-running an ingest is safe: it is idempotent, so an unchanged export creates no
duplicates and no spurious diffs.
