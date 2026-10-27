#!/usr/bin/env bash
# Coverage-guided fuzzing with AFL++ and SharpFuzz on a Linux box.
#
#   fuzz/run-afl.sh [-m MINUTES] [-c CORES] [-w WORKDIR] [target ...]
#
# Defaults: 30 minutes in total, 8 cores (never more), every target except "noop", work under
# $HOME/onearchive/fuzz. Targets run one afl-fuzz instance each, CORES at a time under nice; the total time
# is split evenly across the waves. Prerequisites: tools/install-afl.sh and a .NET SDK on PATH.
#
# Results: WORKDIR/out/<target>/default/{queue,crashes,hangs}, WORKDIR/out/<target>.errors (the exception text of crashes), and WORKDIR/report.md with one row per target:
# executions, edges found (of the map's reachable total), corpus size, findings, and edge growth over the run.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
minutes=30; cores=8; work="$HOME/onearchive/fuzz"
while getopts "m:c:w:" o; do
  case "$o" in m) minutes=$OPTARG ;; c) cores=$OPTARG ;; w) work=$OPTARG ;; *) exit 2 ;; esac
done
shift $((OPTIND - 1))
[ "$cores" -le 8 ] || { echo "at most 8 cores" >&2; exit 2; }
export PATH="$HOME/onearchive/tools/bin:$PATH"
command -v afl-fuzz >/dev/null && command -v sharpfuzz >/dev/null || { echo "run tools/install-afl.sh first" >&2; exit 1; }

mkdir -p "$work"
build="$work/build"
artifacts="${ONEARCHIVE_ARTIFACTS:-$work/artifacts}"
export ONEARCHIVE_ARTIFACTS="$artifacts" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1

# Checked build (unchecked accessors become bounds-checked), then instrument a copy of the output.
(cd "$root" && nice dotnet build fuzz -c Checked -v quiet -clp:NoSummary)
rm -rf "$build"; mkdir -p "$build"
cp -r "$artifacts"/bin/Fuzz/checked/. "$build/"
for dll in RecursiveAI.OneCompress.dll RecursiveAI.OneArchive.dll; do
  [ -f "$build/$dll" ] && sharpfuzz "$build/$dll" >/dev/null
done

if [ ! -d "$work/seeds" ]; then "$here/make-seeds.sh" "$work/seeds" >/dev/null; fi

if [ $# -eq 0 ]; then set -- $(dotnet "$artifacts/bin/Fuzz/checked/OneArchive.Fuzz.dll" list | grep -v '^noop$'); fi
targets=("$@")
waves=$(( (${#targets[@]} + cores - 1) / cores ))
slot=$(( minutes * 60 / waves ))
per=$(( (${#targets[@]} + waves - 1) / waves )) # balanced waves, never more than CORES wide
echo "${#targets[@]} targets, $per at a time, $waves wave(s) of $slot s"

export AFL_SKIP_BIN_CHECK=1 AFL_NO_UI=1 AFL_SKIP_CPUFREQ=1 AFL_I_DONT_CARE_ABOUT_MISSING_CRASHES=1 \
       AFL_NO_AFFINITY=1 AFL_AUTORESUME=1 AFL_FAST_CAL=1
# Environment for the target: a hard heap limit turns runaway allocation into a finding, and crash dumps stay off.
export DOTNET_GCHeapHardLimit=0xC0000000 DOTNET_DbgEnableMiniDump=0 DOTNET_EnableCrashReport=0 \
       DOTNET_TieredPGO=0 DOTNET_TC_QuickJitForLoops=1 DOTNET_gcConcurrent=0 \
       ZSTD_FUZZ_TESTDATA="$root/testdata/zstd"

rm -rf "$work/out"; mkdir -p "$work/out"
i=0
while [ "$i" -lt "${#targets[@]}" ]; do
  pids=()
  for t in "${targets[@]:$i:$per}"; do
    seeds="$work/seeds/$t"; [ -d "$seeds" ] && [ -n "$(ls -A "$seeds")" ] || { mkdir -p "$work/seeds/$t"; printf 'seed' > "$work/seeds/$t/empty-ish"; }
    ONEARCHIVE_FUZZ_ERRLOG="$work/out/$t.errors" nice -n 10 afl-fuzz -i "$seeds" -o "$work/out/$t" -V "$slot" -m none -t 10000 -- \
      dotnet "$build/OneArchive.Fuzz.dll" afl "$t" >"$work/out/$t.log" 2>&1 &
    pids+=($!)
  done
  for p in "${pids[@]}"; do wait "$p" || true; done
  i=$((i + per))
done

{
  echo "| target | execs | execs/s | edges | corpus | crashes | hangs | edges at 25% / 50% / 100% |"
  echo "|---|---|---|---|---|---|---|---|"
  for t in "${targets[@]}"; do
    d="$work/out/$t/default"
    [ -f "$d/fuzzer_stats" ] || { echo "| $t | no stats (see out/$t.log) | | | | | | |"; continue; }
    st() { awk -F' *: *' -v k="$1" '$1 == k { print $2 }' "$d/fuzzer_stats"; }
    growth=$(awk -F', ' -v n="$(wc -l < "$d/plot_data")" 'NR>1 { e[NR]=$13 } END { printf "%s / %s / %s", e[int(n*0.25)+1], e[int(n*0.5)+1], e[n] }' "$d/plot_data")
    echo "| $t | $(st execs_done) | $(st execs_per_sec) | $(st edges_found) | $(st corpus_count) | $(st saved_crashes) | $(st saved_hangs) | $growth |"
  done
} | tee "$work/report.md"
echo "findings: $(find "$work/out" -path '*/crashes/id*' -o -path '*/hangs/id*' | wc -l) (under $work/out/<target>/default/{crashes,hangs})"
