#!/usr/bin/env bash
# CPU profile of a ZstdEncodeBench run on macOS with xctrace (Time Profiler), with the hot instructions of the JIT-compiled
# methods: where the greedy and lazy match finders stall on memory at large table sizes.
#
#   profile-mac.sh <label> <method-name-filter> <ZstdEncodeBench arguments...>
#   e.g.  profile-mac.sh l9 'ZstdLazyMatchFinder:*' big ~/onearchive/corpora/silesia.tar 9 --seconds 6
#
# Builds the bench in Release when BENCH_DLL is not set. xctrace shows JIT frames as bare addresses, so the run also writes a
# perf map (DOTNET_PerfMapEnabled=1) and a JitDisasm listing of the methods matching the filter; the annotate script from
# tools/deflate-bench joins the three. Tiered compilation is off so the listing is the code that ran (full opts, no OSR).
# Results go to $PROFILE_DIR (default ./profile-out). Environment: BENCH_DLL, DOTNET, SECONDS_LIMIT (default 10), TOP_METHODS,
# CONTEXT. Light work: one process, no timing claims.
set -euo pipefail
label="$1"; filter="$2"; shift 2
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dotnet="${DOTNET:-$(command -v dotnet)}"
out="${PROFILE_DIR:-./profile-out}"; mkdir -p "$out"; out="$(cd "$out" && pwd)"
if [ -z "${BENCH_DLL:-}" ]; then
  export ONEARCHIVE_ARTIFACTS="${ONEARCHIVE_ARTIFACTS:-$out/artifacts}"
  "$dotnet" build "$here/ZstdEncodeBench.csproj" -c Release -v quiet -clp:NoSummary
  BENCH_DLL="$(find "$ONEARCHIVE_ARTIFACTS" -name ZstdEncodeBench.dll -path '*release*' | head -1)"
fi
rm -rf "$out/$label.trace" "$out/$label.asm"
export DOTNET_PerfMapEnabled=1 DOTNET_TieredCompilation=0 DOTNET_JitDisasm="$filter" DOTNET_JitDisasmWithCodeBytes=1 DOTNET_JitStdOutFile="$out/$label.asm"
xctrace record --template 'Time Profiler' --output "$out/$label.trace" --time-limit "${SECONDS_LIMIT:-10}s" --no-prompt --launch -- "$dotnet" "$BENCH_DLL" "$@" > "$out/$label.record.log" 2>&1 || true
pid="$(xctrace export --input "$out/$label.trace" --toc 2>/dev/null | grep -o 'name="dotnet" pid="[0-9]*"' | head -1 | grep -o '[0-9]*')"
cp "/tmp/perf-$pid.map" "$out/$label.map"
xctrace export --input "$out/$label.trace" --xpath '/trace-toc/run[@number="1"]/data/table[@schema="time-profile"]' > "$out/$label.xml" 2>/dev/null
python3 "$here/../deflate-bench/profile-annotate.py" "$out/$label.xml" "$out/$label.map" "$out/$label.asm" "${TOP_METHODS:-4}" "${CONTEXT:-5}" | tee "$out/$label.txt"
