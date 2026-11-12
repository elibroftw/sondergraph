#!/usr/bin/env bash
# Publishes the ASP.NET backend as a single-file executable into desktop/binaries/.
#
# Tauri's `externalBin` requires the file to be named with the target triple appended:
#
#   desktop/binaries/sondergraph-server-x86_64-unknown-linux-gnu
#
# The bundler strips that suffix when it copies the file next to the app executable, so at
# runtime it is plain `sondergraph-server` (or `sondergraph-server.exe` on Windows). Getting
# the name wrong is a silent failure: the build succeeds and the app then reports a missing
# server at launch.
#
# Self-contained, not framework-dependent: a desktop app must not require the user to
# install the .NET runtime first (docs/architecture-precedents.md §5).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/.." && pwd)"
# Tauri resolves `externalBin` relative to the directory containing tauri.conf.json, i.e.
# src-tauri/, NOT the project root. Putting the binary in desktop/binaries/ compiles fine
# and then fails at build-script time with:
#
#   resource path `binaries/sondergraph-server-<triple>` doesn't exist
#
# so the output directory must be src-tauri/binaries.
out="$here/src-tauri/binaries"

# Resolve the target triple: honour an explicit RID, otherwise infer from the host.
rid="${1:-}"
if [[ -z "$rid" ]]; then
  case "$(uname -s)/$(uname -m)" in
    Linux/x86_64)  rid="linux-x64"   ; triple="x86_64-unknown-linux-gnu"   ;;
    Linux/aarch64) rid="linux-arm64" ; triple="aarch64-unknown-linux-gnu"  ;;
    Darwin/x86_64) rid="osx-x64"     ; triple="x86_64-apple-darwin"         ;;
    Darwin/arm64)  rid="osx-arm64"   ; triple="aarch64-apple-darwin"        ;;
    *) echo "unsupported host: $(uname -s)/$(uname -m); pass a RID explicitly" >&2; exit 1 ;;
  esac
else
  case "$rid" in
    linux-x64)   triple="x86_64-unknown-linux-gnu"   ;;
    linux-arm64) triple="aarch64-unknown-linux-gnu"  ;;
    osx-x64)     triple="x86_64-apple-darwin"        ;;
    osx-arm64)   triple="aarch64-apple-darwin"       ;;
    win-x64)     triple="x86_64-pc-windows-msvc"     ;;
    win-arm64)   triple="aarch64-pc-windows-msvc"    ;;
    *) echo "unknown RID: $rid" >&2; exit 1 ;;
  esac
fi

echo "Publishing the backend for $rid ($triple)"

# Fail before a long publish if the web UI has never been built.
#
# wwwroot is generated output and is no longer committed, so a fresh clone has none. Without
# this check the publish succeeds, the sidecar starts, binds and connects - and serves 404 for
# the UI, which looks like a routing bug rather than a missing build step.
web_src="$repo/src/Sondergraph.Api/wwwroot/index.html"
if [[ ! -f "$web_src" ]]; then
  echo "error: the web UI has not been built." >&2
  echo "       run:  (cd '$repo/web' && npm ci && npm run build)" >&2
  exit 1
fi

mkdir -p "$out"

# `dotnet publish --output` merges into an existing directory rather than replacing it, so
# every Vite build left its hashed bundle behind: after enough rebuilds the publish tree held
# dozens of dead index-*.js files next to the live one. That is how a stale index.html kept
# being served - the old asset it referenced was still on disk, so nothing 404'd and nothing
# looked wrong. Start from an empty directory so the tree only ever holds this build.
rm -rf "$out/publish-$rid"

# Publish flags, and why:
#   --self-contained                  the user must not have to install .NET first
#   PublishSingleFile                 one artifact, not a directory of DLLs
#   IncludeNativeLibrariesForSelfExtract
#       bundle native libs INTO the single file. On macOS every executable in Contents/MacOS
#       must be signed and notarized, and one file is one thing to sign.
#   PublishReadyToRun                 trade a little size for a faster cold start; worth it
#       for an app the user opens and closes
dotnet publish "$repo/src/Sondergraph.Api/Sondergraph.Api.csproj" \
  --configuration Release \
  --runtime "$rid" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishReadyToRun=true \
  --output "$out/publish-$rid"

ext=""
[[ "$rid" == win-* ]] && ext=".exe"

# The bundler resolves externalBin relative to src-tauri, so the binaries dir must sit
# there. Copy with the triple suffix the bundler expects.
cp "$out/publish-$rid/Sondergraph.Api$ext" "$out/sondergraph-server-$triple$ext"
chmod +x "$out/sondergraph-server-$triple$ext"

# The single-file publish does NOT embed wwwroot: ASP.NET reads static files from disk, and
# the publish output has a wwwroot directory beside the executable. Tauri's externalBin only
# copies the binary, so without this the app starts, binds, connects - and serves a 404 for
# the UI. Copy it next to the sidecar and declare it as a bundle resource.
rm -rf "$out/wwwroot"
cp -r "$out/publish-$rid/wwwroot" "$out/wwwroot"
echo "Copied wwwroot ($(find "$out/wwwroot" -type f | wc -l) files) beside the sidecar"

# Freshness assertion. Every stale-artifact bug in this project has had the same shape: the
# source was right, the served file was older, and nothing complained. Comparing the bundle
# index.html actually names against the newest bundle on disk catches the case where the
# copied tree is not the one we just built.
want=$(grep -o 'index-[A-Za-z0-9_-]*\.js' "$web_src" | head -1)
got=$(grep -o 'index-[A-Za-z0-9_-]*\.js' "$out/wwwroot/index.html" | head -1)
if [[ "$want" != "$got" ]]; then
  echo "stale wwwroot: source index.html references $want, sidecar references $got" >&2
  echo "run 'npm run build' in web/ first" >&2
  exit 1
fi
echo "wwwroot is current ($want)"

size=$(du -h "$out/sondergraph-server-$triple$ext" | cut -f1)
echo
echo "Wrote $out/sondergraph-server-$triple$ext ($size)"
echo "Tauri will bundle it as 'sondergraph-server' next to the app executable."
