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

mkdir -p "$out"

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

size=$(du -h "$out/sondergraph-server-$triple$ext" | cut -f1)
echo
echo "Wrote $out/sondergraph-server-$triple$ext ($size)"
echo "Tauri will bundle it as 'sondergraph-server' next to the app executable."
