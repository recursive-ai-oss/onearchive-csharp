#!/usr/bin/env bash
# Deflate bar rows: our compressor against the reference tools, memory to memory.
#
#   bar.sh [--files "silesia.tar enwik8"] [--levels "1 2 ... 12"] [--reps 5] [--mode oneshot|stream|decode]
#
# oneshot rows compare against libdeflate-gzip at the same level (levels 1-12); stream rows compare ours, fed in 64 KiB pieces,
# against zlib-ng's minigzip (levels 1-9); decode rows time our decoder on each tool's own output (levels 1 6 9 12 by default).
# Environment: ONEARCHIVE_TOOLS (default ~/onearchive/tools/bin), ONEARCHIVE_CORPORA, BENCH_DLL (built DeflateBench.dll), DOTNET.
# It is a timing run: run it under the shared timing lock. Method: docs/bench-method.md, section Deflate.
set -euo pipefail
tools="${ONEARCHIVE_TOOLS:-$HOME/onearchive/tools/bin}"
corpora="${ONEARCHIVE_CORPORA:-$HOME/onearchive/corpora}"
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
echo "# reference tools: $(ls -l "$tools/libdeflate-gzip" "$tools/minigzip-ng" | sed 's/.*-> //' | sed 's/.*opt\///' | tr '\n' ' ')"
tmp="$(mktemp -d "${TMPDIR:-/tmp}/deflatebar.XXXXXX")"; trap 'rm -rf "$tmp"' EXIT
case "$mode" in
  oneshot) ref="libdeflate-gzip"; [ -n "$levels" ] || levels="1 2 3 4 5 6 7 8 9 10 11 12" ;;
  stream) ref="minigzip-ng"; [ -n "$levels" ] || levels="1 2 3 4 5 6 7 8 9" ;;
  decode) ref="both"; [ -n "$levels" ] || levels="1 6 9 12" ;;
  *) echo "unknown mode" >&2; exit 2 ;;
esac
if [ "$mode" = decode ]; then
  printf 'file\ttool\tlevel\tref_dec_MBps\tours_dec_MBps\tratio\n'
  for f in $files; do
    path="$(find_file "$f")"
    for L in $levels; do
      for t in libdeflate-gzip minigzip-ng; do
        [ "$t" = libdeflate-gzip ] && args="-$L -c" || args="-$L"
        [ "$t" = minigzip-ng ] && [ "$L" -gt 9 ] && continue
        "$tools/$t" $args < "$path" > "$tmp/in.gz"
        # The reference's decode time: its own decoder on its own output, same method as the encode rows.
        [ "$t" = libdeflate-gzip ] && dargs="-d -c" || dargs="-d"
        r="$("$dotnet" "$dll" ref "$tmp/in.gz" "$reps" "$tools/$t" $dargs | field MBps)"
        o="$("$dotnet" "$dll" dec "$tmp/in.gz" --reps "$reps" | field dec_MBps)"
        printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$f" "$t" "$L" "$r" "$o" "$(ratio "$r" "$o" 3)"
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
    [ "$ref" = libdeflate-gzip ] && args="-$L -c" || args="-$L"
    "$dotnet" "$dll" ref "$path" "$reps" "$tools/$ref" $args > "$tmp/ref.out"
    tv "$tools/$ref" $args < "$path" > /dev/null 2> "$tmp/ref.time"
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
