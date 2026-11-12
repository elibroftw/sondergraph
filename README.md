# sondergraph

> *everything, all at once.*

Every contact is a person with a life as vivid and complex as your own. This tool
maps them — their platforms, their circles, your history with them — into a single
graph you actually own.

## Status

Pre-alpha. Scaffolding and design in progress.

- [DESIGN.md](./DESIGN.md) — architecture, stack decisions, and open questions
- [tasks/](./tasks/) — task tracking, as markdown files

## Licence

Sondergraph is free software under the **GNU Affero General Public License v3.0**
(see [LICENSE](./LICENSE)).

The AGPL is deliberate. This program holds other people's personal data — phone numbers,
addresses, where they live, who they know — and is meant to be run as a local service. The
AGPL closes the gap the GPL leaves open: anyone who runs a modified version *as a network
service* must offer its source to the people using it. For a tool built around personal data,
users should be able to check what is being done with theirs.

Copyright (C) 2026 Elijah Lopez.

## Building

```bash
dotnet build Sondergraph.slnx   # all projects
dotnet test  Sondergraph.slnx   # all tests
```

Requires the .NET 10 SDK, and Node 20+ for the frontend.

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

# 4. Web UI — build the frontend first
cd web && npm ci && npm run build && cd ..
dotnet run --project src/Sondergraph.Api
```

The API serves the web UI from `src/Sondergraph.Api/wwwroot/`, which is **generated output and
not committed** (`npm run build` writes it there, making the app a single origin with no CORS).
A fresh clone therefore has no UI until the frontend is built — step 4 above does that.

For frontend work, run Vite's dev server instead, which proxies `/api` to the backend:

```bash
cd web && npm run dev     # http://localhost:5173
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
