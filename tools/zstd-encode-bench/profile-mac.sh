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
# Results go to $PROFILE_DIR (default ./profile-out). Environment: BENCH_DLL, DOTNET, SECONDS_LIMIT (default 14), TOP_METHODS,
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
marker="$out/$label.start"; : > "$marker"
export DOTNET_PerfMapEnabled=1 DOTNET_TieredCompilation=0 DOTNET_JitDisasm="$filter" DOTNET_JitDisasmWithCodeBytes=1 DOTNET_JitStdOutFile="$out/$label.asm"
# The bench's own output goes to $label.bench.log: if it exits early, that log says why.
xctrace record --template 'Time Profiler' --output "$out/$label.trace" --time-limit "${SECONDS_LIMIT:-14}s" --no-prompt \
  --target-stdout "$out/$label.bench.log" --launch -- "$dotnet" "$BENCH_DLL" "$@" > "$out/$label.record.log" 2>&1 || true
# The perf map is named after the launched process; take the largest map written since the start (the launcher writes tiny ones).
map=""; size=0
for f in /tmp/perf-*.map; do
  [ -f "$f" ] && [ "$f" -nt "$marker" ] || continue
  n=$(wc -c < "$f"); if [ "$n" -gt "$size" ]; then size=$n; map=$f; fi
done
[ -n "$map" ] || { echo "no perf map written since the start; see $out/$label.bench.log and $label.record.log" >&2; exit 1; }
cp "$map" "$out/$label.map"; rm -f /tmp/perf-*.map /tmp/perfinfo-*.map 2>/dev/null || true
xctrace export --input "$out/$label.trace" --xpath '/trace-toc/run[@number="1"]/data/table[@schema="time-profile"]' > "$out/$label.xml" 2>/dev/null
python3 "$here/../deflate-bench/profile-annotate.py" "$out/$label.xml" "$out/$label.map" "$out/$label.asm" "${TOP_METHODS:-4}" "${CONTEXT:-5}" | tee "$out/$label.txt"
rm -rf "$out/$label.trace" "$out/$label.xml" "$marker"
