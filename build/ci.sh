#!/usr/bin/env bash
# The one script CI, the build machines and developers all run.
#
#   build/ci.sh [step...]     steps: build test aot wasm   (default: build test aot)
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
