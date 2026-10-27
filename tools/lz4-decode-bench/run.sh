#!/usr/bin/env bash
# Decode speed of the LZ4 decoder against the lz4 tool's own benchmark mode (`lz4 -b`), memory to memory.
#
#   run.sh [--files "silesia.tar enwik8"] [--levels "1 9 fast1"]
#
# Environment: ONEARCHIVE_LZ4 (tool, default lz4), ONEARCHIVE_CORPORA (default ~/onearchive/corpora),
# ONEARCHIVE_BENCH_TMP (scratch), BENCH_DLL (built Lz4DecodeBench.dll), DOTNET (dotnet executable).
# The frames we decode are written with -B7 (4 MiB independent blocks) and --no-frame-crc, because `lz4 -b` checks
# nothing and works on independent blocks; checksums cost extra and are reported separately by the unit tests' notes.
# Prints one TSV row per measurement with the tool's version, load average before and after, and peak memory
# (from /usr/bin/time -v) of both sides. Run it when nothing else heavy is running; it is a timing run.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
lz4="${ONEARCHIVE_LZ4:-lz4}"
corpora="${ONEARCHIVE_CORPORA:-$HOME/onearchive/corpora}"
work="${ONEARCHIVE_BENCH_TMP:-${TMPDIR:-/tmp}/lz4-decode-bench}"
dll="${BENCH_DLL:?set BENCH_DLL to Lz4DecodeBench.dll}"
dotnet="${DOTNET:-dotnet}"
files="silesia.tar enwik8"
levels="1 9 fast1"
while [ $# -gt 0 ]; do
  case "$1" in
    --files) files="$2"; shift 2 ;;
    --levels) levels="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
mkdir -p "$work"
find_file() { for c in "$corpora/$1" "$corpora/enwik8/$1" "$corpora/silesia/$1"; do [ -f "$c" ] && { echo "$c"; return; }; done; echo "missing corpus file $1" >&2; exit 1; }
# Peak memory in KB from /usr/bin/time (-v on Linux, -l on macOS, where the figure is in bytes).
if [ "$(uname)" = Darwin ]; then
  tv() { /usr/bin/time -l "$@"; }
  peak() { awk '/maximum resident set size/ {printf "%d", $1 / 1024}' "$1"; }
  load() { uptime | sed 's/.*load averages*: //; s/,//g'; }
  cores() { sysctl -n hw.ncpu; }
  cpu() { sysctl -n machdep.cpu.brand_string; }
else
  tv() { /usr/bin/time -v "$@"; }
  peak() { grep 'Maximum resident' "$1" | awk '{print $NF}'; }
  load() { cut -d' ' -f1-3 /proc/loadavg; }
  cores() { nproc; }
  cpu() { grep -m1 'model name' /proc/cpuinfo | cut -d: -f2 | xargs; }
fi
# The tool's -b mode prints "... MB/s, <decompression> MB/s" as its last figures.
refspeed() { tr '\r' '\n' < "$1" | grep 'MB/s' | tail -1 | sed 's/.*MB\/s, *//; s/ *MB\/s.*//'; }

echo "# $("$lz4" --version | head -1)"
echo "# cpu: $(cpu), cores: $(cores), load before: $(load)"
echo "# $("$dotnet" --version | head -1) SDK; decode = Lz4DecodeBench; reference = lz4 -b<L> -i4 (decompression MB/s)"
printf 'kind\tfile\tlevel\tcompressed\tref_MB/s\tours_frame_MB/s\tours_stream_MB/s\tours_blocks_MB/s\tref_peak_KB\tours_peak_KB\tload_after\n'
for f in $files; do
  path="$(find_file "$f")"
  for L in $levels; do
    case "$L" in
      fast*) c="--fast=${L#fast}"; b="--fast=${L#fast}" ;;
      *) c="-$L"; b="-$L" ;;
    esac
    "$lz4" $c -B7 --no-frame-crc -q -f -c "$path" > "$work/f.lz4"
    tv "$lz4" -b $b -i4 "$path" > "$work/ref.out" 2> "$work/ref.time" || true
    ref="$(refspeed "$work/ref.out")"
    tv "$dotnet" "$dll" big "$work/f.lz4" --seconds 4 > "$work/ours.out" 2> "$work/ours.time"
    fr="$(sed 's/.*\tframe=\([0-9]*\) MB.*/\1/' "$work/ours.out")"
    str="$(sed 's/.*\tstream=\([0-9]*\) MB.*/\1/' "$work/ours.out")"
    blk="$(sed 's/.*\tblocks=\([0-9]*\) MB.*/\1/' "$work/ours.out")"
    printf 'big\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$f" "$L" "$(wc -c < "$work/f.lz4" | tr -d " ")" "$ref" "$fr" "$str" "$blk" "$(peak "$work/ref.time")" "$(peak "$work/ours.time")" "$(load)"
  done
done

# Small frames with an 8 KiB dictionary (the tail of a text sample).
rm -rf "$work/pl" "$work/frames"; mkdir -p "$work/frames"
python3 "$here/payloads.py" "$work/pl" 3000 7
cat "$work"/pl/p000* | tail -c 8192 > "$work/d8.bin"
for p in "$work"/pl/p0*; do "$lz4" -1 --no-frame-crc -q -D "$work/d8.bin" -c "$p" > "$work/frames/$(basename "$p").lz4"; done
total=$(cat "$work"/pl/p0* | wc -c)
tv "$lz4" -b1 -i4 -D "$work/d8.bin" "$work"/pl/p0* > "$work/ref.out" 2> "$work/ref.time" || true
ref="$(refspeed "$work/ref.out")"
tv "$dotnet" "$dll" small "$work/d8.bin" "$work/frames" --seconds 4 > "$work/ours.out" 2> "$work/ours.time"
echo "# small: 3000 payloads, $total bytes total; reference decode $ref MB/s => $(awk -v r="$ref" -v t="$total" 'BEGIN{printf "%.0f", r*1e6/(t/3000)}') frames/s"
echo "# small ours: $(cat "$work/ours.out"); peak KB ref $(peak "$work/ref.time") ours $(peak "$work/ours.time"); load after $(load)"
