# sondergraph desktop shell

A Tauri 2 window that launches the sondergraph backend and displays the web UI.

## What it is, and what it is not

The shell is deliberately thin. It does four things:

1. Start the backend (`Sondergraph.Api`) as a **sidecar** process.
2. Read the port it chose from a handshake on stdout.
3. Point a webview at that URL.
4. Make sure the backend dies when the window closes.

All application behaviour lives in the backend and the web UI. There is no logic here that
the browser version lacks — the seam between shell and app is HTTP, the same seam a browser
uses (ADR 0001, ADR 0002).

**The sidecar is the API server, never the CLI.** Shelling out to `sg` and parsing its
console output is the failure mode ADR 0001 explicitly rejects.

## Why a sidecar rather than in-process hosting

The backend is .NET and the shell is Rust, so they cannot share a process. The alternative —
hosting the backend in-process — is what Photino.NET would give, and it needs no sidecar at
all. Tauri was chosen for its cross-platform bundling, which is worth having; the sidecar is
the cost.

## Layout

```
desktop/
├── flake.nix                  # build environment (WebKitGTK 4.1 without sudo)
├── build-sidecar.sh           # publishes the backend into binaries/ with the triple suffix
├── binaries/                  # generated; gitignored
│   └── sondergraph-server-<target-triple>   (~116 MB)
└── src-tauri/
    ├── Cargo.toml
    ├── tauri.conf.json        # declares externalBin and bundle metadata
    ├── capabilities/          # permissions granted to the window
    ├── placeholder-dist/      # shown only if the window loads before the server is ready
    └── src/main.rs            # the supervisor
```

## Building

### 1. Publish the backend

Tauri's `externalBin` requires the binary to be named with the target triple appended:

```bash
./build-sidecar.sh                    # infers the host triple
./build-sidecar.sh win-x64            # or name a RID explicitly
```

The bundler strips the `-<triple>` suffix when it copies the file next to the app executable,
so at runtime it is plain `sondergraph-server` (or `sondergraph-server.exe`).

**Getting this name wrong fails silently**: the build succeeds and the app then reports a
missing server at launch. That is why the script, not a human, constructs the name.

The binary is **self-contained**, not framework-dependent: a desktop user must not have to
install the .NET runtime first (`docs/architecture-precedents.md` §5).

### 2. Provide the Linux system libraries

Tauri 2 on Linux links against **WebKitGTK 4.1** and **libsoup 3**. These are not present in
a stock Fedora 44 install and would normally need `sudo dnf install webkit2gtk4.1-devel
libsoup3-devel`.

There are two ways to get them:

**With Nix (no root required):**

```bash
nix develop ./desktop
cd src-tauri && cargo build
```

**With system packages (needs root):**

```bash
sudo dnf install webkit2gtk4.1-devel libsoup3-devel
```

### 3. Build

```bash
nix develop ./desktop --command bash -c 'cd desktop/src-tauri && cargo build --release'
```

Release builds enable LTO and strip symbols; the shell itself is a few MB, because the
weight is in the sidecar.

## How the sidecar is supervised

The backend prints one JSON object per line on stdout when started with `--handshake`:

```json
{"event":"server.listening","addr":"127.0.0.1:42181","pid":575332,"protocol":1}
{"event":"server.ready","url":"http://127.0.0.1:42181","workspace":"/home/…","workspaceOk":true,"workspaceError":null}
```

Three events, so a launch that fails can say **how far it got** instead of leaving a blank
window:

| Event | Meaning |
|---|---|
| `server.listening` | Bound; the kernel chose the port. |
| `server.ready` | Safe to navigate the webview here. |
| `server.error` | Failed, with a code and a message shown verbatim. |

The port is ephemeral by design — the kernel picks a free one, because a fixed port would
collide with whatever else is running (`docs/architecture-precedents.md` §3).

### Killing it on exit

**Tauri does not kill sidecars automatically.** This is the classic orphaned-backend bug:
each launch leaves a server holding a port, and users accumulate them. The shell handles it
explicitly:

- the `Child` handle is kept in shared state,
- `RunEvent::ExitRequested` and `RunEvent::Exit` both trigger shutdown,
- shutdown calls `kill()`, then **waits** for the process to actually exit (bounded at 5s),
  because returning early lets the child outlive us,
- if the handshake fails, the half-started child is killed before reporting the error.

### Failure is visible

Every failure path renders a page in the window naming the problem, the likely cause, and
the command that fixes it. A launcher that shows an empty window and no explanation is the
worst possible outcome, so there is no silent path.

## Cross-platform notes

| Platform | Consideration |
|---|---|
| Linux | WebKitGTK 4.1 is a real package dependency; it is the main packaging risk. |
| Windows | WebView2 Evergreen is preinstalled on modern Windows; include the ~2 MB bootstrapper as fallback. |
| macOS | **Every executable in `Contents/MacOS/` must be signed and notarized.** `IncludeNativeLibrariesForSelfExtract=true` means there is one file to sign. Test notarization early — this surfaces late otherwise. |

## Publishing

```bash
# after build-sidecar.sh has run for the target
nix develop ./desktop --command bash -c 'cd desktop/src-tauri && cargo tauri build'
```

## Why not Photino.NET

`docs/architecture-precedents.md` §6 recommends Photino, because it hosts the backend
in-process and removes this entire class of problem. Tauri was chosen instead for its
cross-platform bundling. That is a reasonable trade; the cost is exactly the supervision
work above.
