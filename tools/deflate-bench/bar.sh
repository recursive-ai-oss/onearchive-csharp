#!/usr/bin/env bash
# Deflate bar rows: our compressor and decoder against libdeflate and zlib-ng, memory to memory.
#
#   bar.sh [--files "silesia.tar enwik8"] [--levels "1 2 ... 12"] [--reps 5] [--mode oneshot|stream|decode]
#
# oneshot: ours (one call) against libdeflate at the same level, 1-12. stream: ours fed in 64 KiB pieces against zlib-ng's streaming
# deflate, 1-9. decode: our decoder against libdeflate's and zlib-ng's on their own output at the given levels (default 1 6 9 12).
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
field() { sed -n "s/.*$1=\\([0-9.]*\\).*/\\1/p" | head -1; }
echo "# cpu: $(cpu), cores: $(cores), load before: $(load), $("$dotnet" --version | head -1) SDK, reps=$reps, mode=$mode"
tmp="$(mktemp -d "${TMPDIR:-/tmp}/deflatebar.XXXXXX")"; trap 'rm -rf "$tmp"' EXIT
case "$mode" in
  oneshot) lib=libdeflate; [ -n "$levels" ] || levels="1 2 3 4 5 6 7 8 9 10 11 12" ;;
  stream) lib=zlibng; [ -n "$levels" ] || levels="1 2 3 4 5 6 7 8 9" ;;
  decode) lib=both; [ -n "$levels" ] || levels="1 6 9 12" ;;
  *) echo "unknown mode" >&2; exit 2 ;;
esac
if [ "$mode" = decode ]; then
  printf 'file\treference\tlevel\tref_dec_MBps\tours_dec_MBps\ttime_ratio\tverdict\n'
  for f in $files; do
    path="$(find_file "$f")"
    for L in $levels; do
      for lib in libdeflate zlibng; do
        [ "$lib" = zlibng ] && [ "$L" -gt 9 ] && continue
        "$ref" "$lib" enc "$L" "$path" 1 "$tmp/in.gz" > /dev/null
        r="$("$ref" "$lib" dec "$tmp/in.gz" "$reps" | field MBps)"
        o="$("$dotnet" "$dll" dec "$tmp/in.gz" --reps "$reps" | field dec_MBps)"
        tr="$(ratio "$r" "$o" 3)"
        printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$f" "$lib" "$L" "$r" "$o" "$tr" "$(awk -v t="$tr" 'BEGIN { print (t <= 1.15) ? "pass" : "FAIL" }')"
      done
    done
  done
  echo "# load after: $(load)"
  exit 0
fi
printf 'file\tlevel\tref_size\tours_size\tsize_ratio\tref_MBps\tours_MBps\ttime_ratio\tref_peak_KB\tours_peak_KB\tverdict\n'
for f in $files; do
  path="$(find_file "$f")"
  for L in $levels; do
    tv "$ref" "$lib" enc "$L" "$path" "$reps" > "$tmp/ref.out" 2> "$tmp/ref.time"
    sflag=""; [ "$mode" = stream ] && sflag="--stream"
    tv "$dotnet" "$dll" enc "$path" "$L" --reps "$reps" $sflag > "$tmp/our.out" 2> "$tmp/our.time"
    rs="$(field size < "$tmp/ref.out")"; rm_="$(field MBps < "$tmp/ref.out")"
    os="$(field size < "$tmp/our.out")"; om="$(field comp_MBps < "$tmp/our.out")"
    sr="$(ratio "$os" "$rs" 4)"; tr="$(ratio "$rm_" "$om" 3)"
    verdict=$(awk -v s="$sr" -v t="$tr" 'BEGIN { print (s <= 1.01 && t <= 1.15) ? "pass" : "FAIL" }')
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$f" "$L" "$rs" "$os" "$sr" "$rm_" "$om" "$tr" "$(peak "$tmp/ref.time")" "$(peak "$tmp/our.time")" "$verdict"
  done
done
echo "# load after: $(load)"
