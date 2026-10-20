# ADR 0003 — Per-platform parsers behind a shared ingest pipeline

**Status:** Accepted
**Date:** 2026-10-19
**Related:** DESIGN.md §6.2, §4.2 · task 0004 · [ADR 0001](0001-shared-core-library.md)

## Context

Ingestion is the riskiest and most variable part of sondergraph. Every platform differs in
how it exposes data: Mastodon has an open API, Meta platforms effectively require a GDPR
export bundle, Bluesky has an open API, and each export has its own file layout, its own
identifier scheme, and its own idea of what a "contact" is.

The requirement is that **each platform gets its own parser**, without the rest of the
system learning about platform quirks. DESIGN.md §6.2 already commits to pluggable
adapters; this ADR fixes the shape of that plug, which was previously unspecified.

The central realisation: **fetching and parsing are different jobs, and conflating them
is the mistake to avoid.**

## Decision

**A three-stage pipeline, with a narrow per-platform seam in the middle.**

```
   ┌──────────────┐     ┌─────────────────┐     ┌──────────────┐
   │  Acquire     │────▶│  data/raw/<p>/  │────▶│  Parse       │────▶ model
   │  (per-plat)  │     │  append-only    │     │  (per-plat)  │
   └──────────────┘     └─────────────────┘     └──────────────┘
     API call or          immutable,              pure function:
     export import        never rewritten         files → records
```

| Stage | Per-platform? | Input | Output |
|---|---|---|---|
| **Acquire** | Yes — `IAcquirer` | Credentials, config | Files under `data/raw/<platform>/` |
| **Parse** | **Yes — `IPlatformParser`** | Those files | Platform-neutral records |
| **Materialise** | No — shared | Records | `person.md`, accounts, merge candidates |

The seam that matters is **`IPlatformParser`**, and it is deliberately narrow:

```csharp
public interface IPlatformParser
{
    /// Stable identifier, matches the raw/ directory name. e.g. "mastodon".
    string Platform { get; }

    /// Format version this parser understands, so raw dumps can outlive parser changes.
    int ParserVersion { get; }

    /// Reads raw files and yields platform-neutral records, lazily.
    IEnumerable<ParsedRecord> Parse(RawSource source, ParseContext context);
}
```

**A parser is a pure function from files to records.** It does not perform I/O beyond
reading its own source, does not know about the workspace lock, does not write to the
model, and does not make network calls. This is what makes parsers testable with fixtures
and safe to re-run.

## Consequences

**Good:**

- Adding a platform means writing one file and one test fixture set. Nothing else changes.
- Parsers are pure, so they are deterministically testable against checked-in fixtures with
  no network and no credentials.
- Raw dumps are retained, so a parser bug is fixable by re-parsing — no re-fetching, no
  re-requesting an export that takes three days.
- `ParserVersion` decouples parser changes from raw data: a dump made last year still parses.

**Costs:**

- Platform-neutral record types must be expressive enough for every platform. This is a real
  design pressure — see "The leaky-abstraction risk" below.
- Two per-platform artifacts (acquirer + parser) instead of one, which is more ceremony for
  a simple platform. Justified only because fetching is genuinely where credentials,
  rate limits, and unreliability live.

## The shape of a parsed record

Parsers do **not** emit `Person` directly. They emit raw observations, because a parser
cannot know that `@ada@example.social` and `Ada Lovelace` are the same human — that is
identity resolution, and it is explicitly *not* the parser's job (DESIGN.md §10 Q3).

```csharp
/// Something observed in a dump, before identity resolution.
public abstract record ParsedRecord
{
    public required string Platform { get; init; }
    /// Platform's own stable id. Never synthesised, never guessed.
    public required string NativeId { get; init; }
    /// Where in raw/ this came from, for error messages and provenance.
    public required string SourcePath { get; init; }
}

public sealed record ObservedAccount : ParsedRecord
{
    public required string Handle { get; init; }
    public string? DisplayName { get; init; }
    public string? ProfileUrl { get; init; }
    public string? AvatarPath { get; init; }
}

public sealed record ObservedPost : ParsedRecord
{
    public required DateTimeOffset PostedAt { get; init; }
    public required string Body { get; init; }
    public IReadOnlyList<string> MediaPaths { get; init; } = [];
    public string? CanonicalUrl { get; init; }
}

public sealed record ObservedInteraction : ParsedRecord
{
    public required string WithHandle { get; init; }
    public required InteractionKind Kind { get; init; }   // Follow, Reply, Mention, Reaction
    public required DateTimeOffset OccurredAt { get; init; }
}
```

Three kinds, and the set is intentionally small. Feature #1 needs accounts and interactions;
feature #2 needs posts; feature #3 needs the timeline, which is posts plus interactions
ordered by time.

## Non-negotiable parser rules

1. **Never invent an id.** If the platform has no stable identifier for something, parser
   synthesises one from content hash, and marks it as synthetic. Silent id invention makes
   re-parsing produce duplicates.
2. **Never rewrite `raw/`.** Parsers read. Acquire writes. This is what makes re-parsing safe.
3. **Never resolve identity.** Emit observations; let the shared stage surface merge
   candidates (DESIGN.md §4.3).
4. **Never fail the whole parse for one bad record.** Emit what is understood, count what is
   not, and report it. A single malformed row in a 40,000-row export must not lose the rest.
5. **Declare `ParserVersion`.** Re-parse results change when a parser is fixed; the version
   makes "why did my data change?" answerable.
6. **No network, no credentials, no clock.** A parser that reads the wall clock is not
   reproducible. Timestamps come from the data.

## The leaky-abstraction risk

The honest danger: platform-neutral records either become **too thin** (and every parser
does its own thing downstream) or **too fat** (and the union of every platform's quirks is
baked into the core).

The mitigation is that **`ParsedRecord` carries a `Dictionary<string,string> Extra` bag for
platform-specific fields**, which the core ignores and per-platform consumers may read. This
keeps the core clean while keeping the escape hatch explicit rather than implicit. If a
field turns out to be needed by several platforms, it is promoted to a real property — that
promotion is evidence, not speculation.

## Alternatives considered

**One monolithic importer per platform, with no shared pipeline.** Rejected: every platform
reimplements resumability, idempotency, lock acquisition, and merge-candidate detection, and
they drift.

**Parsers emit `Person` directly.** Rejected: forces identity resolution into each parser,
which means N implementations of the riskiest logic in the system, and it makes "surface
candidates, never auto-merge" (DESIGN.md §4.3) impossible to enforce centrally.

**Parse-on-fetch, with no retained raw dumps.** Rejected: a parser bug would require
re-fetching, and for export-bundle platforms that means asking the user to wait days for
Meta to regenerate a file. Retaining raw is cheap and makes parser bugs recoverable.

**A plugin system with dynamically loaded assemblies.** Rejected as premature: parsers ship
in-tree for now, so an interface and a registry are sufficient. Dynamic loading would add
versioning and trust concerns for no present benefit.

## Notes

**Why `Acquire` is separate from `Parse` at all**, given the extra ceremony:

- Acquisition is where **credentials, rate limits, and unreliability** live. Mastodon needs
  an API token and backoff; an Instagram export needs a ZIP from the user's browser.
- Acquisition is **not reproducible** (network, rate limits, time); parsing **is**.
  Separating them lets the reproducible half be tested exhaustively.
- Export-bundle platforms have **no acquisition code at all** — the user drops a `.zip` in
  place. Under a single combined interface, every such platform would carry dead code.

**Fixture-driven development is the intended workflow for a new platform.** The order is:
obtain a real export, hand-place it in `data/raw/<platform>/`, write a failing fixture test,
then write the parser until it passes. This is the "reverse-engineer the schema" step, and
it is where the real work is — the interface above exists only to make that work land in one
place instead of everywhere.
