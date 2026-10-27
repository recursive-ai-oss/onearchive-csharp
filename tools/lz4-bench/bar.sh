#!/usr/bin/env bash
# Encoder bar rows: our compressor against `lz4 -b<level> -i<seconds>`, memory to memory, 4 MiB independent blocks.
#
#   bar.sh [--files "silesia.tar enwik8"] [--levels "fast1 1 3 6 9 12"] [--reps 3] [--seconds 3]
#
# Environment: ONEARCHIVE_LZ4 (tool), ONEARCHIVE_CORPORA, BENCH_DLL (built Lz4Bench.dll), DOTNET.
# Each side runs --reps times (the tool and our bench both report their best pass, so a repeat is a fresh process);
# the table gives min/median/max compress and decompress MB/s of the repeats for both sides, the compressed size of the
# blocks, the size ratio, the speed ratios (ours / reference, from the medians) and peak memory of both sides.
# It is a timing run: run it under the shared timing lock (see lock-run.sh in tools/lz4-decode-bench).
set -euo pipefail
lz4="${ONEARCHIVE_LZ4:-lz4}"
corpora="${ONEARCHIVE_CORPORA:-$HOME/onearchive/corpora}"
dll="${BENCH_DLL:?set BENCH_DLL to Lz4Bench.dll}"
dotnet="${DOTNET:-dotnet}"
files="silesia.tar enwik8"; levels="fast1 1 3 6 9 12"; reps=3; secs=3
while [ $# -gt 0 ]; do
  case "$1" in
    --files) files="$2"; shift 2 ;;
    --levels) levels="$2"; shift 2 ;;
    --reps) reps="$2"; shift 2 ;;
    --seconds) secs="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
tmp="$(mktemp -d "${TMPDIR:-/tmp}/lz4bar.XXXXXX")"; trap 'rm -rf "$tmp"' EXIT
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
# min/median/max of numbers on stdin
stats() { tr -d ' ' | sort -g | awk '{a[NR]=$1} END {printf "%s/%s/%s", a[1], a[int((NR+1)/2)], a[NR]}'; }
med() { tr -d ' ' | sort -g | awk '{a[NR]=$1} END {print a[int((NR+1)/2)]}'; }

echo "# $("$lz4" --version | head -1)"
echo "# cpu: $(cpu), cores: $(cores), load before: $(load), $("$dotnet" --version | head -1) SDK, reps=$reps, seconds=$secs"
printf 'file\tlevel\tref_size\tours_size\tsize_ratio\tref_comp(min/med/max)\tours_comp\tcomp_ratio\tref_dec\tours_dec\tdec_ratio\tref_peak_KB\tours_peak_KB\tload_after\n'
for f in $files; do
  path="$(find_file "$f")"
  for L in $levels; do
    case "$L" in fast*) arg="--fast=${L#fast}"; ol="-${L#fast}" ;; *) arg="-$L"; ol="$L" ;; esac
    : > "$tmp/rc"; : > "$tmp/rd"; : > "$tmp/oc"; : > "$tmp/od"; : > "$tmp/rm"; : > "$tmp/om"
    rsize=0; osize=0
    for r in $(seq 1 "$reps"); do
      tv "$lz4" -b $arg -B7 -i"$secs" "$path" > "$tmp/ref.out" 2> "$tmp/ref.time" || true
      line="$(tr '\r' '\n' < "$tmp/ref.out" | grep 'MB/s' | tail -1)"
      rsize="$(echo "$line" | sed 's/.*-> *\([0-9]*\).*/\1/' | tr -d ' ')"
      echo "$line" | sed "s/.*), *\([0-9.]*\) MB\/s, *\([0-9.]*\) MB\/s.*/\1/" >> "$tmp/rc"
      echo "$line" | sed "s/.*), *\([0-9.]*\) MB\/s, *\([0-9.]*\) MB\/s.*/\2/" >> "$tmp/rd"
      peak "$tmp/ref.time" >> "$tmp/rm"; echo >> "$tmp/rm"
      tv "$dotnet" "$dll" big "$path" "$ol" --seconds "$secs" > "$tmp/our.out" 2> "$tmp/our.time"
      osize="$(sed 's/.*blocks=\([0-9]*\).*/\1/' "$tmp/our.out" | tr -d ' ')"
      sed 's/.*compress=\([0-9.]*\) MB.*/\1/' "$tmp/our.out" >> "$tmp/oc"
      sed 's/.*decode=\([0-9.]*\) MB.*/\1/' "$tmp/our.out" >> "$tmp/od"
      peak "$tmp/our.time" >> "$tmp/om"; echo >> "$tmp/om"
    done
    rcm="$(med < "$tmp/rc")"; ocm="$(med < "$tmp/oc")"; rdm="$(med < "$tmp/rd")"; odm="$(med < "$tmp/od")"
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$f" "$L" "$rsize" "$osize" \
      "$(ratio "$osize" "$rsize" 4)" \
      "$(stats < "$tmp/rc")" "$(stats < "$tmp/oc")" "$(ratio "$ocm" "$rcm" 3)" \
      "$(stats < "$tmp/rd")" "$(stats < "$tmp/od")" "$(ratio "$odm" "$rdm" 3)" \
      "$(grep -v '^$' "$tmp/rm" | med)" "$(grep -v '^$' "$tmp/om" | med)" "$(load)"
  done
done
echo "# load after: $(load)"
