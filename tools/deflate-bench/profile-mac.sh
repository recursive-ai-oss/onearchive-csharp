#!/usr/bin/env bash
# CPU profile of a DeflateBench run on macOS with xctrace (Time Profiler), with hot instructions of the JIT-compiled methods.
#
#   profile-mac.sh <label> <method-name-filter> <DeflateBench arguments...>
#   e.g.  profile-mac.sh enc6 'ParseLazy' enc ~/onearchive/corpora/silesia.tar 6 --reps 2
#
# xctrace shows JIT frames as bare addresses, so the run also writes a perf map (DOTNET_PerfMapEnabled=1) and a JitDisasm listing
# (of the methods matching the filter); profile-annotate.py joins the three. Results go to $PROFILE_DIR (default ./profile-out).
# Environment: BENCH_DLL (built DeflateBench.dll), DOTNET (dotnet), SECONDS_LIMIT (default 8). Light work: one process, no timing claims.
set -euo pipefail
label="$1"; filter="$2"; shift 2
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="${PROFILE_DIR:-./profile-out}"; mkdir -p "$out"; out="$(cd "$out" && pwd)"
dll="${BENCH_DLL:?set BENCH_DLL to DeflateBench.dll}"; dotnet="${DOTNET:-$(command -v dotnet)}"
rm -rf "$out/$label.trace" "$out/$label.asm"
export DOTNET_PerfMapEnabled=1 DOTNET_JitDisasm="$filter" DOTNET_JitDisasmWithCodeBytes=1 DOTNET_JitStdOutFile="$out/$label.asm"
xctrace record --template 'Time Profiler' --output "$out/$label.trace" --time-limit "${SECONDS_LIMIT:-8}s" --no-prompt --launch -- "$dotnet" "$dll" "$@" > "$out/$label.record.log" 2>&1 || true
pid="$(xctrace export --input "$out/$label.trace" --toc 2>/dev/null | grep -o 'name="dotnet" pid="[0-9]*"' | head -1 | grep -o '[0-9]*')"
cp "/tmp/perf-$pid.map" "$out/$label.map"
xctrace export --input "$out/$label.trace" --xpath '/trace-toc/run[@number="1"]/data/table[@schema="time-profile"]' > "$out/$label.xml" 2>/dev/null
python3 "$here/profile-annotate.py" "$out/$label.xml" "$out/$label.map" "$out/$label.asm" "${TOP_METHODS:-3}" "${CONTEXT:-4}" | tee "$out/$label.txt"
