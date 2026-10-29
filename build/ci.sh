#!/usr/bin/env bash
# The one script CI, the build machines and developers all run.
#
#   build/ci.sh [step...]     steps: build test aot aotcfb wasm   (default: build test aot)
#
# Needs the .NET SDK named in global.json on PATH (or DOTNET_ROOT). Output goes to
# $ONEARCHIVE_ARTIFACTS (default: artifacts/ at the repository root).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export MSBUILDDISABLENODEREUSE=1
artifacts="${ONEARCHIVE_ARTIFACTS:-$root/artifacts}"
export ONEARCHIVE_ARTIFACTS="$artifacts"

steps=("$@")
if [ ${#steps[@]} -eq 0 ]; then steps=(build test aot); fi

quiet=(-v quiet -clp:NoSummary)

restore() {
  dotnet restore OneArchive.slnx "${quiet[@]}"
}

step_build() {
  restore
  dotnet build OneArchive.slnx -c Release --no-restore "${quiet[@]}"
}

step_test() {
  restore
  # Tests run in the Checked configuration: optimized, with unchecked accessors bounds-checked.
  dotnet test --solution OneArchive.slnx -c Checked --no-restore --results-directory "$artifacts/TestResults"
}

step_aot() {
  # Publishing with NativeAOT and trimming must produce no warnings (warnings are errors).
  out="$artifacts/publish/aot-trim"
  rm -rf "$out"
  dotnet publish smoke/aot-trim -c Release -o "$out" "${quiet[@]}"
  exe="$out/AotTrimSmoke"
  if [ -f "$exe.exe" ]; then exe="$exe.exe"; fi
  "$exe"

  # The slim app reads and writes only .tar.zst: the other codecs, and brotli's dictionary, must be gone from its binary.
  slim="$artifacts/publish/aot-trim-slim"
  rm -rf "$slim"
  dotnet publish smoke/aot-trim-slim -c Release -o "$slim" "${quiet[@]}"
  exe="$slim/AotTrimSlimSmoke"
  if [ -f "$exe.exe" ]; then exe="$exe.exe"; fi
  "$exe"
  # Positive control first: a name the app does use must be findable, or the absence checks prove nothing.
  grep -qa ZstdStream "$exe" || { echo "slim check: ZstdStream not found in the binary"; return 1; }
  for name in BrotliStream BrotliDecoder BrotliEncoder timedownlifeleft LzmaDecoder LzmaEncoder Lzma2Decoder XzDecoder XzStream \
      BZip2Stream BZip2Decoder Deflate64Decoder DeflateDecoder LzxDecoder LzipStream Lz4Stream PpmdStream PpmdHDecoder PpmdIModel; do
    if grep -qa "$name" "$exe"; then echo "slim check: $name is in the .tar.zst-only binary"; return 1; fi
  done
}

step_aotcfb() {
  # An app that only reads compound files: no codec, and none of the names below, may be in its binary.
  cfb="$artifacts/publish/aot-trim-cfb"
  rm -rf "$cfb"
  dotnet publish smoke/aot-trim-cfb -c Release -o "$cfb" "${quiet[@]}"
  exe="$cfb/AotTrimCfbSmoke"
  if [ -f "$exe.exe" ]; then exe="$exe.exe"; fi
  "$exe"
  grep -qa CompoundFileReader "$exe" || { echo "cfb check: CompoundFileReader not found in the binary"; return 1; }
  for name in ZstdStream ZstdDecoder BrotliStream BrotliDecoder timedownlifeleft LzmaDecoder Lzma2Decoder XzDecoder BZip2Decoder \
      Deflate64Decoder DeflateDecoder LzxDecoder LzipStream Lz4Stream PpmdHDecoder; do
    if grep -qa "$name" "$exe"; then echo "cfb check: $name is in the compound-file-only binary"; return 1; fi
  done
}

step_wasm() {
  if ! dotnet workload list | grep -q '^wasm-tools'; then
    dotnet workload install wasm-tools
  fi
  dotnet build smoke/wasm -c Release "${quiet[@]}"
  node smoke/wasm/run.mjs "$artifacts/bin/WasmHost/release/wwwroot"
}

for s in "${steps[@]}"; do
  echo "== $s"
  "step_$s"
done
echo "== ci ok"
