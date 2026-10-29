#!/usr/bin/env bash
# Full speed, ratio and memory bar for the Zstandard and LZ4 codecs against the reference tools, in chunks that each fit
# in about two hours on a shared benchmark host.
#
#   run.sh <1|2|3|0.2|0.3|0.4> [--only "step step"] [--list]
#
#   chunk 1: Zstandard levels -5..19 on whole files (compress, and decode of our own output), decode of the tool's frames,
#            small game snapshot payloads with trained dictionaries (compress and decode), LZ4 whole files and small payloads.
#   chunk 2: the decode rows again (the tool frames, LZ4 whole files, small payloads), Zstandard levels 20 and 22,
#            long-distance matching.
#   chunk 0.2 (output dir chunk0.2): the Deflate family and bzip2 decode. One-shot compression against libdeflate 1-12, streaming
#            compression against zlib-ng 1-9, decode of the references' output and of ours by libdeflate and zlib-ng and by us,
#            and bzip2 decode against `bzip2 -dc`; silesia.tar and enwik8. Also runs on the Mac (BAR_LOCK_FILE=~/onearchive/BENCH.lock).
#   chunk 0.3 (output dir chunk0.3): the LZMA family. xz presets 0-9 against `xz -T1` and lzip levels 0-9 against `lzip` (compress, whole
#            file, in memory against the CLI with start-up subtracted), decode of the tools' output and of ours by us and by the
#            tools, and the sizes of our 7z writer against `7zz -mx=1/5/9`; silesia.tar and enwik8. The cheap presets come first and
#            7-9 last with one repeat, so a deadline drops the slowest rows. Also runs on the Mac (BAR_LOCK_FILE=~/onearchive/BENCH.lock).
#   chunk 0.4 (output dir chunk0.4): brotli, bzip2, Deflate64 and LZX. brotli encode q0-11 (window 22) against `brotli` (tools/brotli-bench),
#            brotli decode of the tool's output and of ours, bzip2 encode against `bzip2` levels 1-9 with `bzip2 -d` reading ours back,
#            Deflate64 compress and decompress against 7-Zip `-tzip -mm=Deflate64 -mx=N -mmt=1`, and cab LZX decode against `cabextract`
#            on cabinets built by the test compressor (tools/bar04-bench/bar.sh describes each method and its caveats); silesia.tar and
#            enwik8. The cheap rows come first and brotli q10-11 last, so a deadline drops the slowest rows. Also runs on the Mac
#            (BAR_LOCK_FILE=~/onearchive/BENCH.lock).
#   chunk 3: multithreaded jobs, dictionary training time and memory, Zstandard level 19 on small payloads, the small
#            synthetic payload rows (256 B to 16 KiB, tools/small-bench), and the bar runner on a few files.
#
# Each chunk is one job: it takes the host's bench lock itself (exits 3 if the lock is held, without waiting), builds the
# benchmark programs in Release, runs its steps one after another, records load every minute next to a list of the busiest
# processes, and writes TSV files, a log and summary.md (PASS/MISS per row) to $BAR_OUT. A step that cannot finish inside the
# time budget is skipped and listed in skipped.txt; run it later with --only.
#
# Environment (defaults for the bench host):
#   ONEARCHIVE_HOME  state directory with tools/bin and corpora/ (default ~/onearchive)
#   BAR_OUT          results directory (default $ONEARCHIVE_HOME/bar-x64/chunk<N>)
#   BAR_DEADLINE     HH:MM today; overrides BAR_BUDGET_MIN
#   BAR_BUDGET_MIN   wall-clock budget for the steps in minutes (default 115)
#   BAR_LOCK_FILE    lock file (default ~/WORKER-BENCH.lock, ~/onearchive/BENCH.lock on macOS)
#   BAR_LOCK_LABEL   text at the start of the lock file entry (default "onearchive bar")
#   BAR_COMMIT       free text recorded in machine.txt (for example the commit hash)
#   BAR_FILES        whole-file inputs (default "silesia.tar enwik8")
#   BAR_QUICK        1: cut every input to its first 1 MiB with one repeat and divide the step times by 1000 (a run of the whole chunk
#                    in a few minutes to prove the scripts; chunk 0.4 only; the numbers mean nothing). Use a private BAR_LOCK_FILE and BAR_OUT.
#   BAR_SCALE        divides every step's expected duration (for dry runs on tiny inputs; default 1)
#   DOTNET_ROOT      dotnet install (~/dotnet10 when it exists, whatever the environment says)
# Pass/miss rule: compressed size within 1% of the reference and our rate at least 0.87 of the reference's rate.
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
chunk="${1:-}"; shift || true
only=""; list=0
while [ $# -gt 0 ]; do
  case "$1" in
    --only) only="$2"; shift 2 ;;
    --list) list=1; shift ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
case "$chunk" in 1|2|3|0.2|0.3|0.4) ;; *) echo "usage: run.sh <1|2|3|0.2|0.3|0.4> [--only \"step ...\"] [--list]" >&2; exit 2 ;; esac

home="${ONEARCHIVE_HOME:-$HOME/onearchive}"
[ -d "$HOME/dotnet10" ] && export DOTNET_ROOT="$HOME/dotnet10"
[ -z "${DOTNET_ROOT:-}" ] && [ -d "$home/dotnet" ] && export DOTNET_ROOT="$home/dotnet"
[ -n "${DOTNET_ROOT:-}" ] && export PATH="$DOTNET_ROOT:$PATH"
export PATH="$home/tools/bin:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
export ONEARCHIVE_CORPORA="${ONEARCHIVE_CORPORA:-$home/corpora}"
export ONEARCHIVE_ZSTD="${ONEARCHIVE_ZSTD:-$(command -v zstd || true)}" ONEARCHIVE_LZ4="${ONEARCHIVE_LZ4:-$(command -v lz4 || true)}"
export DOTNET="${DOTNET:-dotnet}"
out="${BAR_OUT:-$home/bar-x64/chunk$chunk}"
budget="${BAR_BUDGET_MIN:-115}"
# BAR_DEADLINE=HH:MM (today, local time) replaces the budget: steps stop at the deadline minus 5 minutes.
if [ -n "${BAR_DEADLINE:-}" ]; then
  # GNU date takes -d; BSD date (macOS) parses with -j -f.
  deadline=$(date -d "$BAR_DEADLINE" +%s 2>/dev/null || date -j -f '%H:%M' "$BAR_DEADLINE" +%s) || { echo "cannot parse BAR_DEADLINE=$BAR_DEADLINE" >&2; exit 2; }
  budget=$(( ( deadline - $(date +%s) ) / 60 - 5 ))
fi
scale="${BAR_SCALE:-1}"
[ -n "${BAR_QUICK:-}" ] && [ -z "${BAR_SCALE:-}" ] && scale=1000
export BAR_QUICK="${BAR_QUICK:-}"
files="${BAR_FILES:-silesia.tar enwik8}"
if [ "$(uname)" = Darwin ]; then lock="${BAR_LOCK_FILE:-$HOME/onearchive/BENCH.lock}"; else lock="${BAR_LOCK_FILE:-$HOME/WORKER-BENCH.lock}"; fi

# Step table: name, chunk, expected minutes, command. Expected minutes come from the per-row times in the bench docs and are
# deliberately generous; a step whose expected time no longer fits in the budget is skipped, not started.
zenc="$repo/tools/zstd-encode-bench"; zdec="$repo/tools/zstd-decode-bench"; ztrain="$repo/tools/zstd-train-bench"; l4="$repo/tools/lz4-bench"; sb="$repo/tools/small-bench"
steps() {
  if [ "$chunk" = 0.4 ]; then
    echo "bzip2-enc|12|b04_run bz2-enc '1 5 9' 3"
    echo "d64-dec|10|b04_run d64-dec '1 5 9' 5"
    echo "lzx-dec|20|b04_run lzx-dec '' 5"
    echo "d64-enc|25|b04_run d64-enc '1 5 9' 3"
    echo "brotli-dec|40|b04_run brotli-dec '1 5 9' 5"
    echo "brotli-enc-a|30|brotli_run '0 1 2 3 4' 3"
    echo "bzip2-enc-b|20|b04_run bz2-enc '2 3 4 6 7 8' 2"
    echo "brotli-enc-b|60|brotli_run '5 6 7 8 9' 2"
    echo "brotli-enc-c|90|brotli_run '10 11' 1"
  elif [ "$chunk" = 0.3 ]; then
    echo "xz-enc-a|25|lzma_run enc xz '0 1 2 3' 3"
    echo "xz-dec|25|lzma_run dec xz '0 6 9' 5"
    echo "lzip-enc-a|30|lzma_run enc lzip '0 1 2 3' 2"
    echo "lzip-dec|25|lzma_run dec lzip '0 6 9' 5"
    echo "sevenz|20|lzma_run sevenz xz '' 1"
    echo "xz-enc-b|40|lzma_run enc xz '4 5 6' 2"
    echo "lzip-enc-b|50|lzma_run enc lzip '4 5 6' 1"
    echo "xz-enc-c|40|lzma_run enc xz '7 8 9' 1"
    echo "lzip-enc-c|60|lzma_run enc lzip '7 8 9' 1"
  elif [ "$chunk" = 0.2 ]; then
    echo "defl-enc|45|defl_run oneshot"
    echo "defl-stream|15|defl_run stream"
    echo "defl-dec|15|defl_run decode"
    echo "bzip2-dec|8|defl_run bzip2"
  elif [ "$chunk" = 1 ]; then
    echo "enc-fast|8|enc_run --levels '-5 -1 1 2 3 4 5 6 7 9'"
    echo "dec-tool|15|dec_run"
    echo "lz4|12|lz4_run"
    echo "lz4-game|5|lz4_game"
    echo "enc-12-15|8|enc_run --levels '12 15'"
    echo "enc-16-19|25|enc_run --levels '16 17 18 19'"
    echo "game-enc|20|game_enc"
    echo "game-dec|15|game_dec"
  elif [ "$chunk" = 2 ]; then
    echo "dec-tool|20|dec_run"
    echo "lz4|18|lz4_run"
    echo "game-dec|14|game_dec"
    echo "enc-20-22|30|enc_run --levels '20 22'"
    echo "ldm|10|ldm_run"
  else
    echo "jobs|14|jobs_run"
    echo "train|15|train_run"
    echo "game-enc|5|game_enc19"
    echo "small|45|small_run"
    echo "bar-runner|15|bar_runner"
  fi
}
if [ "$list" = 1 ]; then steps | awk -F'|' '{ printf "%s\t%s min\n", $1, $2 }'; exit 0; fi

# ---- the lock: taken here, never waited for ----
mkdir -p "$(dirname "$lock")"
set -o noclobber
echo "${BAR_LOCK_LABEL:-onearchive bar} chunk$chunk $(date '+%F %T') end ${BAR_DEADLINE:-+$((budget + 10))min}" > "$lock" || { echo "bench lock held: $(cat "$lock" 2>/dev/null)" >&2; exit 3; }
set +o noclobber
mon_pid=""
cleanup() { [ -n "$mon_pid" ] && kill "$mon_pid" 2>/dev/null; rm -f "$lock"; }
trap cleanup EXIT
trap 'exit 130' INT TERM

mkdir -p "$out"
tmp="$out/../tmp$chunk"; mkdir -p "$tmp"; export ONEARCHIVE_BENCH_TMP="$tmp"
art="$out/../artifacts"; mkdir -p "$art"
start=$(date +%s)
if [ "$(uname)" = Darwin ]; then load() { uptime | sed 's/.*load averages*: //; s/,//g'; }; else load() { cut -d' ' -f1-3 /proc/loadavg; }; fi
elapsed_min() { echo $(( ($(date +%s) - start) / 60 )); }

# ---- machine record and the load monitor ----
{
  echo "date: $(date '+%F %T %Z')"
  echo "host: $(hostname -s 2>/dev/null)"
  if [ "$(uname)" = Darwin ]; then echo "cpu: $(sysctl -n machdep.cpu.brand_string)"; echo "cores: $(sysctl -n hw.ncpu)"
  else echo "cpu: $(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2 | xargs)"; echo "cores: $(nproc)"; fi
  echo "kernel: $(uname -sr)"
  echo "load before: $(load)"
  echo "running now (top by cpu):"; ps -eo pcpu,user,comm | sort -rn | head -6
  echo "sdk: $(dotnet --version)"
  if [ "$chunk" = 0.4 ]; then
    echo "brotli: $(brotli --version 2>&1 | head -1)"
    echo "bzip2: $(bzip2 --help 2>&1 | head -1 | sed 's/,.*//')"
    echo "7zz: $( (7zz 2>&1 || 7z 2>&1) | sed -n 2p)"
    echo "cabextract: $(cabextract --version 2>&1 | head -1)"
    [ -n "${BAR_QUICK:-}" ] && echo "QUICK: 1 MiB prefixes, one repeat, no timing claims"
  elif [ "$chunk" = 0.3 ]; then
    echo "xz: $(xz --version | head -1)"
    echo "lzip: $(lzip --version 2>&1 | head -1)"
    echo "7zz: $( (7zz 2>&1 || 7z 2>&1) | sed -n 2p)"
  elif [ "$chunk" = 0.2 ]; then
    echo "libdeflate: $(basename "$(ls -d "$home"/tools/opt/libdeflate-* 2>/dev/null | head -1)")"
    echo "zlib-ng: $(basename "$(ls -d "$home"/tools/opt/zlib-ng-* 2>/dev/null | head -1)")"
    echo "bzip2: $(bzip2 --help 2>&1 | head -1 | sed 's/,.*//')"
  else
    echo "zstd: $("$ONEARCHIVE_ZSTD" --version | head -1)"
    echo "lz4: $("$ONEARCHIVE_LZ4" --version | head -1)"
  fi
  echo "commit: ${BAR_COMMIT:-unknown}"
  echo "budget_min: $budget"
} > "$out/machine.txt"
(
  while :; do
    sleep 60
    echo "$(date +%T) load $(load) procs $(cut -d' ' -f4 /proc/loadavg 2>/dev/null) | $(ps -eo pcpu,user,comm | sort -rn | sed -n 1,3p | awk '{printf "%s%% %s/%s; ", $1, $2, $3}')"
  done >> "$out/load.log"
) &
mon_pid=$!
echo "$(date +%T) load $(load) (start)" > "$out/load.log"

# ---- build ----
build() {
  local name="$1" proj="$2"
  ONEARCHIVE_ARTIFACTS="$art" dotnet build "$proj" -c Release -v quiet -clp:NoSummary > "$out/build-$name.log" 2>&1 \
    || { echo "build failed: $name (see $out/build-$name.log)" >&2; exit 4; }
  echo "$art/bin/$name/release/$name.dll"
}
echo "# building ($(date +%T))"
if [ "$chunk" = 0.4 ]; then
  b04_dll="$(build Bar04Bench "$repo/tools/bar04-bench/Bar04Bench.csproj")" || exit 4
elif [ "$chunk" = 0.3 ]; then
  lzma_dll="$(build LzmaBench "$repo/tools/lzma-bench/LzmaBench.csproj")" || exit 4
elif [ "$chunk" = 0.2 ]; then
  defl_dll="$(build DeflateBench "$repo/tools/deflate-bench/DeflateBench.csproj")" || exit 4
  # refbench: in-memory libdeflate and zlib-ng timings against the pinned installs.
  refbench="$art/refbench"
  ONEARCHIVE_HOME="$home" bash "$repo/tools/deflate-bench/build.sh" "$refbench" > "$out/build-refbench.log" 2>&1 \
    || { echo "refbench build failed (see $out/build-refbench.log)" >&2; exit 4; }
else
enc_dll="$(build ZstdEncodeBench "$zenc/ZstdEncodeBench.csproj")" || exit 4
dec_dll="$(build ZstdDecodeBench "$zdec/ZstdDecodeBench.csproj")" || exit 4
train_dll="$(build ZstdTrainBench "$ztrain/ZstdTrainBench.csproj")" || exit 4
lz4_dll="$(build Lz4Bench "$l4/Lz4Bench.csproj")" || exit 4
small_dll="$(build SmallBench "$sb/SmallBench.csproj")" || exit 4
fi
echo "build done $(date +%T), elapsed $(elapsed_min) min" >> "$out/steps.log"

# ---- step bodies (stdout becomes the step's TSV) ----
# The Deflate and bzip2 rows: tools/deflate-bench/bar.sh, one mode per step. Eight repeats for the fast levels, fewer where one
# pass takes tens of seconds.
defl_run() {
  local mode="$1"
  export REFBENCH="$refbench" BENCH_DLL="$defl_dll" TMPDIR="$tmp"
  case "$mode" in
    oneshot) "$repo/tools/deflate-bench/bar.sh" --mode oneshot --files "$files" --levels "1 2 3 4 5 6 7 8 9" --reps 5
             "$repo/tools/deflate-bench/bar.sh" --mode oneshot --files "$files" --levels "10 11 12" --reps 3 | grep -v '^#\|^file' ;;
    stream) "$repo/tools/deflate-bench/bar.sh" --mode stream --files "$files" --reps 5 ;;
    decode) "$repo/tools/deflate-bench/bar.sh" --mode decode --files "$files" --levels "1 6 9 12" --reps 7 ;;
    bzip2) "$repo/tools/deflate-bench/bar.sh" --mode bzip2 --files "$files" --levels "1 9" --reps 5 ;;
  esac
}
# The LZMA-family rows: tools/lzma-bench/bar.sh, one mode per step (arguments: mode codec levels reps).
lzma_run() {
  local mode="$1" codec="$2" lv="$3" reps="$4"
  export BENCH_DLL="$lzma_dll" TMPDIR="$tmp"
  if [ "$mode" = sevenz ]; then "$repo/tools/lzma-bench/bar.sh" --mode sevenz --files "$files"
  else "$repo/tools/lzma-bench/bar.sh" --mode "$mode" --codec "$codec" --files "$files" --levels "$lv" --reps "$reps"; fi
}
# The 0.4 rows. b04_run: tools/bar04-bench/bar.sh, one mode per step (arguments: mode levels reps; empty levels = the mode's own).
b04_run() {
  local mode="$1" lv="$2" reps="$3"
  export BENCH_DLL="$b04_dll" TMPDIR="$tmp"
  "$repo/tools/bar04-bench/bar.sh" --mode "$mode" --files "$files" ${lv:+--levels "$lv"} --reps "$reps"
}
# brotli_run: tools/brotli-bench/bar.sh at the given qualities (arguments: qualities reps). It must not take the lock this job holds.
brotli_run() {
  TMPDIR="$tmp" ONEARCHIVE_ARTIFACTS="$art" BAR_LOCK_FILE= "$repo/tools/brotli-bench/bar.sh" --files "$files" --levels "$1" --reps "$2" --slow-files ""
}
enc_run() { BENCH_DLL="$enc_dll" "$zenc/run.sh" --files "$files" "$@"; }
dec_run() { BENCH_DLL="$dec_dll" OURS_MAX_LEVEL=19 "$zdec/run.sh" --files "$files" --levels "1 3 9 19" --reps 3; }
lz4_run() { BENCH_DLL="$lz4_dll" "$l4/bar.sh" --files "$files" --levels "fast8 1 2 3 6 9 12" --reps 3 --seconds 3; }
# The reference library is the pinned lz4 install (tools/pins.tsv), never the system's liblz4, which is whatever version the
# distribution ships (the 0.1 close rows were measured against 1.9.4 by mistake).
lz4_game() {
  local prefix lib refsmall="$tmp/refsmall" prep="$tmp/lz4-game"
  prefix="$(ls -d "$home"/tools/opt/lz4-* 2>/dev/null | head -1)"
  lib="$(ls "$prefix"/lib/liblz4.so "$prefix"/lib/liblz4.dylib 2>/dev/null | head -1)"
  [ -n "$lib" ] || { echo "# no pinned lz4 library under $home/tools/opt: small-payload LZ4 rows skipped"; return 0; }
  echo "# liblz4: pinned $(basename "$prefix") ($(readlink -f "$lib" | xargs basename))"
  cc -O2 "$l4/refsmall.c" -o "$refsmall" "$lib" -Wl,-rpath,"$prefix/lib" || { echo "# refsmall build failed"; return 1; }
  DICT_KB=16 "$l4/game-prep.sh" "$ONEARCHIVE_CORPORA/game" "$prep" 20 >&2 || return 1
  BENCH_DLL="$lz4_dll" REFSMALL="$refsmall" "$l4/game-bar.sh" "$prep" "-1 1 3 9"
}
# Small synthetic payloads (256 B to 16 KiB) with 16 and 112 KiB dictionaries, one at a time through reused contexts, against
# the pinned zstd and lz4 libraries; ../small-bench/run.sh describes the method and the columns.
small_run() { BENCH_DLL="$small_dll" ONEARCHIVE_HOME="$home" ONEARCHIVE_BENCH_TMP="$tmp/small" TRAIN_DLL="${train_dll:-}" "$sb/run.sh" --seconds 2 --ours-dict; }
game_enc() {
  local k lv
  for kl in "frames:1 3" "snapshots:3 9 19" "saves:3 9 19"; do
    k="${kl%%:*}"; lv="${kl#*:}"
    BENCH_DLL="$enc_dll" "$zenc/game.sh" --kinds "$k" --sizes "16384 114688" --levels "$lv" --train-files 5000 --test-files 5000 --timing
  done
}
game_enc19() {
  local k
  for k in snapshots saves; do
    BENCH_DLL="$enc_dll" "$zenc/game.sh" --kinds "$k" --sizes "16384 114688" --levels "19" --train-files 5000 --test-files 5000 --timing
  done
}
game_dec() {
  local k lv
  for kl in "frames:1 3" "snapshots:3 9 19" "saves:3 9 19"; do
    k="${kl%%:*}"; lv="${kl#*:}"
    for L in $lv; do
      BENCH_DLL="$dec_dll" "$zdec/game.sh" --kinds "$k" --sizes "16384 114688" --levels "$k=$L" --train-files 5000 --test-files 3000
    done
  done
}
ldm_run() {
  # Long-distance matching with a 128 MiB window on both sides, one thread.
  enc_run --levels "1 3" --ref-opts "-T1 --long=27" --ours-opts "--long 27"
}
jobs_run() {
  local n
  for n in 4 8 16; do
    echo "# workers=$n"
    enc_run --levels "1 3" --ref-opts "-T$n" --ours-opts "--workers $n --job auto"
  done
}
train_run() {
  local sil=""; [ -d "$ONEARCHIVE_CORPORA/silesia" ] && sil="$ONEARCHIVE_CORPORA/silesia"
  TRAIN_DLL="$train_dll" bash "$ztrain/timing.sh" --runs 3 ${sil:+--silesia "$sil"}
}
bar_runner() {
  BAR_LOCK="$out/bar.lock" BAR_OUT="$out/bar-runner" BAR_BUSY_MAX=32 BAR_WAIT_MIN=1 \
    "$repo/bench/run-bar.sh" zstd 1,3,19 silesia/dickens,silesia/mozilla --runs 3
}

# ---- run ----
: > "$out/skipped.txt"
while IFS='|' read -r name est cmd; do
  [ -n "$only" ] && ! echo " $only " | grep -q " $name " && continue
  est=$(awk -v e="$est" -v s="$scale" 'BEGIN { v = e / s; printf "%d", (v < 1) ? 1 : v }')
  left=$(( budget - $(elapsed_min) ))
  if [ "$est" -gt "$left" ]; then
    echo "$name: skipped, needs ~$est min and $left remain" | tee -a "$out/skipped.txt" >&2
    continue
  fi
  limit=$(( left < est * 2 ? left : est * 2 ))
  t0=$(date +%s)
  echo "$(date +%T) step $name start (expected $est min, limit $limit min) load $(load)" >> "$out/steps.log"
  ( eval "$cmd" ) > "$out/$name.tsv" 2> "$out/$name.err" &
  pid=$!
  waited=0
  while kill -0 "$pid" 2>/dev/null; do
    sleep 5; waited=$((waited + 5))
    if [ "$waited" -ge $((limit * 60)) ]; then
      pkill -TERM -P "$pid" 2>/dev/null; kill -TERM "$pid" 2>/dev/null; sleep 2; pkill -KILL -P "$pid" 2>/dev/null; kill -KILL "$pid" 2>/dev/null
      echo "# TIMED OUT after $limit min; rows above are complete rows only" >> "$out/$name.tsv"
      echo "$name: timed out after $limit min" >> "$out/skipped.txt"
      break
    fi
  done
  wait "$pid" 2>/dev/null; rc=$?
  echo "$(date +%T) step $name end rc=$rc after $(( ($(date +%s) - t0) / 60 )) min (total $(elapsed_min)) load $(load)" >> "$out/steps.log"
done < <(steps)

echo "load after: $(load)" >> "$out/machine.txt"
"$here/summarize.sh" "$out" > "$out/summary.md" 2> "$out/summarize.err"
rm -rf "$tmp"
echo "done: $(elapsed_min) min; results in $out"
