#!/usr/bin/env bash
# Runs the hash-matrix columns on this machine and writes one manifest per column.
#
#   tools/hashmatrix/columns.sh --out DIR --prefix NAME [options]
#
#   --out DIR          where the manifests go (created)
#   --prefix NAME      manifest names are NAME-<column>.tsv, e.g. x64-jit.tsv
#   --columns LIST     comma list of: jit aot scalar noavx threadsN wasm   (default: jit,aot,scalar,noavx,threadsN)
#   --dotnet DIR       directory holding the dotnet executable (default: whatever is on PATH)
#   --node DIR         Node.js prefix, for the wasm column (default: $HOME/onearchive/tools/node, then PATH)
#   --filter TEXT      run only the entries whose name contains TEXT (for example zstd-train)
#   --include-opt-in   also run the opt-in divergent entries (to demonstrate the comparison failing)
#
# Columns: jit = JIT, 1 thread; aot = NativeAOT publish; scalar = DOTNET_EnableHWIntrinsic=0;
# noavx = DOTNET_EnableAVX512F=0 and DOTNET_EnableAVX2=0; threadsN = JIT with N threads, N = core count;
# wasm = browser-wasm under Node. Build output goes under $ONEARCHIVE_ARTIFACTS (default: artifacts/).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
out="" prefix="" columns="jit,aot,scalar,noavx,threadsN" dotnet_dir="" node_dir="" extra=()
while [ $# -gt 0 ]; do
  case "$1" in
    --out) out="$2"; shift 2 ;;
    --prefix) prefix="$2"; shift 2 ;;
    --columns) columns="$2"; shift 2 ;;
    --dotnet) dotnet_dir="$2"; shift 2 ;;
    --node) node_dir="$2"; shift 2 ;;
    --include-opt-in) extra+=(--include-opt-in); shift ;;
    --filter) extra+=(--filter "$2"); shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
[ -n "$out" ] && [ -n "$prefix" ] || { echo "--out and --prefix are required" >&2; exit 2; }

if [ -n "$dotnet_dir" ]; then export DOTNET_ROOT="$dotnet_dir" PATH="$dotnet_dir:$PATH"; fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 MSBUILDDISABLENODEREUSE=1
art="${ONEARCHIVE_ARTIFACTS:-$root/artifacts}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
cores="$( (nproc 2>/dev/null || sysctl -n hw.ncpu) | head -1)"
quiet=(-v quiet -clp:NoSummary)
# Each build flavour gets its own artifacts directory: they share an assembly name.
jit_dll="$art/jit/bin/HashMatrix/release/HashMatrix.dll"

build_jit() {
  [ -f "$jit_dll" ] && [ "${built_jit:-}" = 1 ] && return
  ONEARCHIVE_ARTIFACTS="$art/jit" dotnet build "$root/tools/hashmatrix/Host" -c Release "${quiet[@]}"
  built_jit=1
}

run_jit() { # column threads [env...]
  local column="$1" threads="$2"; shift 2
  build_jit
  echo "== $prefix-$column (threads=$threads)"
  env "$@" dotnet "$jit_dll" run --out "$out/$prefix-$column.tsv" --column "$prefix-$column" --threads "$threads" ${extra[@]+"${extra[@]}"}
}

IFS=, read -r -a list <<<"$columns"
for c in "${list[@]}"; do
  case "$c" in
    jit) run_jit jit 1 ;;
    scalar) run_jit scalar 1 DOTNET_EnableHWIntrinsic=0 ;;
    noavx) run_jit noavx 1 DOTNET_EnableAVX512F=0 DOTNET_EnableAVX2=0 ;;
    threadsN) run_jit "threads$cores" "$cores" ;;
    aot)
      echo "== $prefix-aot (threads=1)"
      ONEARCHIVE_ARTIFACTS="$art/aot" dotnet publish "$root/tools/hashmatrix/Host" -c Release -p:PublishAot=true -o "$art/aot/publish" "${quiet[@]}"
      "$art/aot/publish/HashMatrix" run --out "$out/$prefix-aot.tsv" --column "$prefix-aot" --threads 1 ${extra[@]+"${extra[@]}"}
      ;;
    wasm)
      echo "== $prefix-wasm"
      node_dir="${node_dir:-$HOME/onearchive/tools/node}"
      node_bin="$node_dir/bin/node"; [ -x "$node_bin" ] || node_bin="$(command -v node)"
      ONEARCHIVE_ARTIFACTS="$art/wasm" dotnet build "$root/tools/hashmatrix/Wasm" -c Release "${quiet[@]}"
      raw="$out/$prefix-wasm.raw"
      "$node_bin" "$root/smoke/wasm/run.mjs" "$art/wasm/bin/HashMatrix/release/wwwroot" run --out - --column "$prefix-wasm" --threads "$cores" ${extra[@]+"${extra[@]}"} >"$raw"
      sed -n '/^#BEGIN-MANIFEST$/,/^#END-MANIFEST$/p' "$raw" | sed '1d;$d' >"$out/$prefix-wasm.tsv"
      rm -f "$raw"
      [ -s "$out/$prefix-wasm.tsv" ] || { echo "wasm run produced no manifest" >&2; exit 1; }
      ;;
    *) echo "unknown column $c" >&2; exit 2 ;;
  esac
done
