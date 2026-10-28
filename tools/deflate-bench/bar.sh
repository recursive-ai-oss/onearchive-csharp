#!/usr/bin/env bash
# Deflate bar rows: our compressor and decoder against libdeflate and zlib-ng, memory to memory.
#
#   bar.sh [--files "silesia.tar enwik8"] [--levels "1 2 ... 12"] [--reps 5] [--mode oneshot|stream|decode|bzip2]
#
# oneshot: ours (one call) against libdeflate at the same level, 1-12. stream: ours fed in 64 KiB pieces against zlib-ng's streaming
# deflate, 1-9. decode: our decoder against libdeflate's and zlib-ng's on their own output at the given levels (default 1 6 9 12).
# bzip2: our bzip2 decoder against `bzip2 -dc` (the CLI, output to /dev/null, with process start-up measured on a tiny file and
# subtracted) on the tool's own output at block sizes -1 and -9 (levels default "1 9"). The compressed files stay out of the timing.
# The references run through refbench (refbench.c, built by build.sh), which times only the library call on a file held in memory.
# Environment: REFBENCH (built refbench), ONEARCHIVE_CORPORA, BENCH_DLL (built DeflateBench.dll), DOTNET, TMPDIR (use RAM).
# It is a timing run: run it under the shared timing lock. Method: docs/bench-method.md, section Deflate.
set -euo pipefail
corpora="${ONEARCHIVE_CORPORA:-$HOME/onearchive/corpora}"
ref="${REFBENCH:?set REFBENCH to the built refbench}"
dll="${BENCH_DLL:?set BENCH_DLL to DeflateBench.dll}"
dotnet="${DOTNET:-dotnet}"
files="silesia.tar enwik8"; levels=""; reps=5; mode=oneshot
while [ $# -gt 0 ]; do
  case "$1" in
    --files) files="$2"; shift 2 ;;
    --levels) levels="$2"; shift 2 ;;
    --reps) reps="$2"; shift 2 ;;
    --mode) mode="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
find_file() { [ -f "$1" ] && { echo "$1"; return; }; for c in "$corpora/$1" "$corpora/enwik8/$1" "$corpora/silesia/$1"; do [ -f "$c" ] && { echo "$c"; return; }; done; echo "missing $1" >&2; exit 1; }
if [ "$(uname)" = Darwin ]; then
  tv() { /usr/bin/time -l "$@"; }
  peak() { awk '/maximum resident set size/ {printf "%d", $1 / 1024}' "$1"; }
  load() { uptime | sed 's/.*load averages*: //; s/,//g'; }
  cpu() { sysctl -n machdep.cpu.brand_string; }; cores() { sysctl -n hw.ncpu; }
else
  tv() { /usr/bin/time -v "$@"; }
  peak() { grep 'Maximum resident' "$1" | awk '{print $NF}'; }
  load() { cut -d' ' -f1-3 /proc/loadavg; }
  cpu() { grep -m1 'model name' /proc/cpuinfo | cut -d: -f2 | xargs; }; cores() { nproc; }
fi
ratio() { awk -v a="$1" -v b="$2" -v d="$3" 'BEGIN { printf "%.*f", d, a / b }'; }
# Wall time in seconds of one command with output to /dev/null (perl is on every bench host, `date +%N` is not on macOS).
walltime() { perl -MTime::HiRes=time -e '$t = time; $pid = fork; if (!$pid) { open STDOUT, ">", "/dev/null"; exec @ARGV or exit 127 } waitpid $pid, 0; printf "%.6f\n", time - $t; exit($? >> 8)' "$@"; }
besttime() { local n="$1" b="" t; shift; for _ in $(seq 1 "$n"); do t="$(walltime "$@")"; b="$(awk -v a="$b" -v t="$t" 'BEGIN { print (a == "" || t < a) ? t : a }')"; done; echo "$b"; }
field() { sed -n "s/.*$1=\\([0-9.]*\\).*/\\1/p" | head -1; }
echo "# cpu: $(cpu), cores: $(cores), load before: $(load), $("$dotnet" --version | head -1) SDK, reps=$reps, mode=$mode"
tmp="$(mktemp -d "${TMPDIR:-/tmp}/deflatebar.XXXXXX")"; trap 'rm -rf "$tmp"' EXIT
case "$mode" in
  oneshot) lib=libdeflate; [ -n "$levels" ] || levels="1 2 3 4 5 6 7 8 9 10 11 12" ;;
  stream) lib=zlibng; [ -n "$levels" ] || levels="1 2 3 4 5 6 7 8 9" ;;
  decode) lib=both; [ -n "$levels" ] || levels="1 6 9 12" ;;
  bzip2) lib=bzip2; [ -n "$levels" ] || levels="1 9" ;;
  *) echo "unknown mode" >&2; exit 2 ;;
esac
if [ "$mode" = decode ]; then
  # source = whose compressed bytes are decoded: the reference library's own output at that level, or ours at that level.
  printf 'file\treference\tlevel\tsource\tref_dec_MBps\tours_dec_MBps\tours_dec_med\tours_dec_min\ttime_ratio\tref_peak_KB\tours_peak_KB\tverdict\n'
  for f in $files; do
    path="$(find_file "$f")"
    for L in $levels; do
      BENCH_NODECODE=1 "$dotnet" "$dll" enc "$path" "$L" --reps 1 --out "$tmp/ours.gz" > /dev/null
      for lib in libdeflate zlibng; do
        [ "$lib" = zlibng ] && [ "$L" -gt 9 ] && continue
        "$ref" "$lib" enc "$L" "$path" 1 "$tmp/ref.gz" > /dev/null
        for src in ref ours; do
          in="$tmp/$src.gz"
          tv "$ref" "$lib" dec "$in" "$reps" > "$tmp/ref.out" 2> "$tmp/ref.time"
          tv "$dotnet" "$dll" dec "$in" --reps "$reps" > "$tmp/our.out" 2> "$tmp/our.time"
          r="$(field MBps < "$tmp/ref.out")"; o="$(field dec_MBps < "$tmp/our.out")"
          om="$(field dec_MBps_med < "$tmp/our.out")"; on="$(field dec_MBps_min < "$tmp/our.out")"
          tr="$(ratio "$r" "$o" 3)"
          printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$f" "$lib" "$L" "$src" "$r" "$o" "$om" "$on" "$tr" "$(peak "$tmp/ref.time")" "$(peak "$tmp/our.time")" "$(awk -v t="$tr" 'BEGIN { print (t <= 1.15) ? "pass" : "FAIL" }')"
        done
      done
    done
  done
  echo "# load after: $(load)"
  exit 0
fi
if [ "$mode" = bzip2 ]; then
  # `bzip2 -dc` is a CLI, so its time includes process start-up: measure that on a tiny stream and subtract it. Best of $reps.
  bz="${BZIP2:-bzip2}"
  echo "# reference: $("$bz" --help 2>&1 | head -1 | sed 's/,.*//'), start-up subtracted, best of $reps"
  printf 'x' | "$bz" -9 -c > "$tmp/tiny.bz2"
  startup="$(besttime 20 "$bz" -dc "$tmp/tiny.bz2")"
  echo "# start-up: $startup s"
  printf 'file\tblock_size\tcompressed\tout_bytes\tref_dec_MBps\tours_dec_MBps\tours_dec_med\tours_dec_min\ttime_ratio\tref_peak_KB\tours_peak_KB\tverdict\n'
  for f in $files; do
    path="$(find_file "$f")"
    size="$(wc -c < "$path" | tr -d ' ')"
    for L in $levels; do
      "$bz" -"$L" -c < "$path" > "$tmp/in.bz2"
      t="$(besttime "$reps" "$bz" -dc "$tmp/in.bz2")"
      r="$(awk -v n="$size" -v t="$t" -v s="$startup" 'BEGIN { d = t - s; if (d < 1e-6) d = 1e-6; printf "%.1f", n / 1e6 / d }')"
      tv "$bz" -dc "$tmp/in.bz2" > /dev/null 2> "$tmp/ref.time"
      tv "$dotnet" "$dll" bz2dec "$tmp/in.bz2" --reps "$reps" > "$tmp/our.out" 2> "$tmp/our.time"
      o="$(field dec_MBps < "$tmp/our.out")"; om="$(field dec_MBps_med < "$tmp/our.out")"; on="$(field dec_MBps_min < "$tmp/our.out")"
      tr="$(ratio "$r" "$o" 3)"
      printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$f" "$L" "$(wc -c < "$tmp/in.bz2" | tr -d ' ')" "$size" "$r" "$o" "$om" "$on" "$tr" "$(peak "$tmp/ref.time")" "$(peak "$tmp/our.time")" "$(awk -v t="$tr" 'BEGIN { print (t <= 1.15) ? "pass" : "FAIL" }')"
    done
  done
  echo "# load after: $(load)"
  exit 0
fi
printf 'file\tlevel\tref_size\tours_size\tsize_ratio\tref_MBps\tours_MBps\tours_med\tours_min\ttime_ratio\tref_peak_KB\tours_peak_KB\tverdict\n'
for f in $files; do
  path="$(find_file "$f")"
  for L in $levels; do
    tv "$ref" "$lib" enc "$L" "$path" "$reps" > "$tmp/ref.out" 2> "$tmp/ref.time"
    sflag=""; [ "$mode" = stream ] && sflag="--stream"
    tv "$dotnet" "$dll" enc "$path" "$L" --reps "$reps" $sflag > "$tmp/our.out" 2> "$tmp/our.time"
    rs="$(field size < "$tmp/ref.out")"; rm_="$(field MBps < "$tmp/ref.out")"
    os="$(field size < "$tmp/our.out")"; om="$(field comp_MBps < "$tmp/our.out")"; omed="$(field comp_MBps_med < "$tmp/our.out")"; omin="$(field comp_MBps_min < "$tmp/our.out")"
    sr="$(ratio "$os" "$rs" 4)"; tr="$(ratio "$rm_" "$om" 3)"
    verdict=$(awk -v s="$sr" -v t="$tr" 'BEGIN { print (s <= 1.01 && t <= 1.15) ? "pass" : "FAIL" }')
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$f" "$L" "$rs" "$os" "$sr" "$rm_" "$om" "$omed" "$omin" "$tr" "$(peak "$tmp/ref.time")" "$(peak "$tmp/our.time")" "$verdict"
  done
done
echo "# load after: $(load)"
