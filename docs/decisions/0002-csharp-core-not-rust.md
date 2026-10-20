# ADR 0002 — C# for the core, not Rust

**Status:** Accepted
**Date:** 2026-10-19
**Supersedes:** none
**Related:** [ADR 0001](0001-shared-core-library.md), `docs/architecture-precedents.md` §5–6

## Context

The proposal was to write `Sondergraph.Core` in Rust instead of C#, on the grounds that it
would be easier to interface with Tauri and with "all native platform languages."

This is the second time the language choice has been questioned, so the reasoning is
recorded here rather than re-derived each time.

## Decision

**Keep C# for `Sondergraph.Core`.** Rust is not adopted, and no polyglot split is adopted.

## Consequences

Stated as consequences rather than justifications, so they can be checked later:

**Accepted costs:**

- Core is not usable as a native library from non-.NET callers without creating a host
  process. This is deliberate — see "Interoperability" below.
- Cold start is ~150–250 ms for the self-contained single-file build, versus roughly
  40 ms for an equivalent AOT Rust binary. Judged irrelevant for an interactive
  contact browser.
- We ship a runtime (~80–110 MB) where Rust would ship a ~10 MB binary. Already accepted
  in `docs/architecture-precedents.md` §5, for reasons unrelated to language.

**Realized benefits:**

- One language across CLI, API, and desktop shell. Three stacks instead of four
  (C#, Svelte/TypeScript, and the shared webview each desktop option provides).
- Task 0001's scaffolding, the layering tripwire, and the ADR 0001 thin-shell rule all
  remain valid. No rework.
- Research investment stands: the self-contained single-file decision, the
  `FileShare`/flock analysis, the Kestrel IPC options, and the Photino in-process hosting
  recommendation are all .NET-idiomatic.

## Rationale

### The Tauri premise does not hold

Tauri does not interface with a backend by language. It uses **`externalBin` — a
subprocess**, whose contract is stdout and HTTP. A self-contained .NET single-file
executable and a Rust binary are indistinguishable to that mechanism.

Concretely, choosing Rust would change **nothing** about the Tauri integration work:

- the same Rust supervisor,
- the same handshake line parsing (the `{"event":"server.ready",...}` contract in
  `docs/architecture-precedents.md` §3),
- the same job-object / process-group kill logic for the orphaned-sidecar problem,
- the same `capabilities/default.json` argument guard,
- the same macOS nested-binary codesigning constraint.

Tauri's advantage over Photino is its bundler, updater, and plugin ecosystem — **not the
language its sidecar is written in.** Rewriting Core in Rust buys none of that.

There is a further irony: our desktop recommendation is **Photino.NET**, which hosts the
backend *in the same process*. Rust would make that path strictly harder, not easier.

### The interoperability benefit has no consumer

"Easier to interface with all native platform languages" presumes consumers that do not
exist. Enumerating the actual callers:

| Consumer | Language-agnostic? |
|---|---|
| `sg` CLI | Same repository, same language either way |
| Local API | Same repository, same language either way |
| Desktop shell | Subprocess or in-process — language-agnostic |
| Third parties | **None.** No plugin API, no extension model, no SDK is planned |

The FFI surface is **zero**. This is a personal archival tool, not a platform. Paying a
real architectural cost for interoperability without a consumer is the definition of
speculative generality.

### Interoperability is already better without FFI

Core is already reachable by any language through the **local HTTP API** (Option A,
ADR 0001). That is a strictly better interop story than a native shared library:

- HTTP/JSON is a stable, versioned, debuggable contract. A C ABI is none of those.
- Any language with a socket can call it. Binding to a native library requires
  per-language binding maintenance.
- The protocol version is already explicit (`SondergraphVersion.Protocol`), so
  mismatches are detectable.

### Rust's advantages are real but do not apply

| Rust wins when | Applies here? |
|---|---|
| Single static binary, no runtime | **No** — self-contained .NET single-file already achieves this |
| Fast cold start | **Marginally** — 150–250 ms vs ~40 ms is imperceptible for this workload |
| Memory safety without GC | **No** — I/O-bound file processing, not a latency-sensitive engine |
| Rich type system, no null | **.NET has it** — nullable reference types enabled, warnings-as-errors |
| No GC pauses | **No** — irrelevant at this scale |

Rust is a better tool for a problem .NET already solves adequately. That is not a reason
to switch.

## Alternatives considered

**Rust core with a C ABI.** Rejected: creates per-language binding maintenance, a C ABI
surface to version, and a host process anyway for the CLI/API. No identified consumer.

**Rust core with .NET shells via FFI.** Rejected: worst of both — two toolchains, two
build systems, and a marshalling boundary, to reach a CLI and an HTTP server that a single
language already serves.

**Polyglot: C# core, Rust for hot paths only.** **Not rejected — deferred.** This is the
sanctioned escape hatch. If a specific component is measured to be too slow, it may be
written in Rust and placed behind the existing interface, without changing the core
language. The architecture permits this because Option A's boundaries are the API and the
CLI, not a shared binary. Converting the argument from architectural to empirical costs
almost nothing later.

**Rust for the desktop shell only (Tauri).** Compatible with this ADR. Tauri remains the
documented runner-up in `docs/architecture-precedents.md` §6, and choosing it does not
require Rust anywhere else.

## Notes

**This ADR is about the core language, not about forbidding Rust entirely.** Tauri remains
a legitimate desktop choice, and a measured hot path may be rewritten later. The decision
is that the *core* is C#, for a single-language codebase with no FFI surface to maintain.

If this is revisited, the trigger should be **a measurement**, not a preference: a
profiled bottleneck Rust would plausibly fix, or a named external consumer that cannot
speak HTTP. Absent either, the answer stands.
