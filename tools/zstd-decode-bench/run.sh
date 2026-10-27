#!/usr/bin/env bash
# Decode speed of the Zstandard decoder against the zstd tool's own benchmark mode (`zstd -b`), memory to memory.
#
#   run.sh [--files "silesia.tar enwik8"] [--levels "1 3 9 19"]
#
# Environment: ONEARCHIVE_ZSTD (tool, default zstd), ONEARCHIVE_CORPORA (default ~/onearchive/corpora),
# ONEARCHIVE_BENCH_TMP (scratch), BENCH_DLL (built ZstdDecodeBench.dll), DOTNET (dotnet executable).
# The frames we decode are written with --no-check because `zstd -b` frames carry no checksum; checking costs extra.
# Prints one TSV row per measurement with the tool's version, load average before and after, and peak memory
# (from /usr/bin/time -v) of both sides. Run it when nothing else heavy is running; it is a timing run.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
zstd="${ONEARCHIVE_ZSTD:-zstd}"
corpora="${ONEARCHIVE_CORPORA:-$HOME/onearchive/corpora}"
work="${ONEARCHIVE_BENCH_TMP:-${TMPDIR:-/tmp}/zstd-decode-bench}"
dll="${BENCH_DLL:?set BENCH_DLL to ZstdDecodeBench.dll}"
dotnet="${DOTNET:-dotnet}"
files="silesia.tar enwik8"
levels="1 3 9 19"
while [ $# -gt 0 ]; do
  case "$1" in
    --files) files="$2"; shift 2 ;;
    --levels) levels="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
mkdir -p "$work"
find_file() { for c in "$corpora/$1" "$corpora/enwik8/$1" "$corpora/silesia/$1"; do [ -f "$c" ] && { echo "$c"; return; }; done; echo "missing corpus file $1" >&2; exit 1; }
peak() { grep 'Maximum resident' "$1" | awk '{print $NF}'; }
load() { cut -d' ' -f1-3 /proc/loadavg; }

echo "# $("$zstd" --version | head -1)"
echo "# cpu: $(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2 | xargs), cores: $(nproc), load before: $(load)"
echo "# $("$dotnet" --version | head -1) runtime; decode = ZstdDecodeBench; reference = zstd -b<L> -e<L> -i4 (decompression MB/s)"
printf 'kind\tfile\tlevel\tcompressed\tref_MB/s\tours_oneshot_MB/s\tours_stream_MB/s\tref_peak_KB\tours_peak_KB\tload_after\n'
for f in $files; do
  path="$(find_file "$f")"
  for L in $levels; do
    "$zstd" -"$L" --no-check -q -f -c "$path" > "$work/f.zst"
    /usr/bin/time -v "$zstd" -b"$L" -e"$L" -i4 "$path" > "$work/ref.out" 2> "$work/ref.time" || true
    ref="$(tr '\r' '\n' < "$work/ref.out" | grep 'MB/s' | tail -1 | sed 's/.*MB\/s, *//; s/ *MB\/s.*//')"
    /usr/bin/time -v "$dotnet" "$dll" big "$work/f.zst" --seconds 4 > "$work/ours.out" 2> "$work/ours.time"
    one="$(sed 's/.*oneshot=\([0-9]*\) MB.*/\1/' "$work/ours.out")"
    str="$(sed 's/.*stream=\([0-9]*\) MB.*/\1/' "$work/ours.out")"
    printf 'big\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$f" "$L" "$(stat -c %s "$work/f.zst")" "$ref" "$one" "$str" "$(peak "$work/ref.time")" "$(peak "$work/ours.time")" "$(load)"
  done
done

# Small frames with a 16 KiB trained dictionary.
rm -rf "$work/pl" "$work/frames"; mkdir -p "$work/frames"
python3 "$here/payloads.py" "$work/pl" 3000 7
"$zstd" --train -q --maxdict=16384 -o "$work/d16.zdict" "$work"/pl/p0* > /dev/null 2>&1
for p in "$work"/pl/p0*; do "$zstd" -3 --no-check -q -D "$work/d16.zdict" -c "$p" > "$work/frames/$(basename "$p").zst"; done
total=$(cat "$work"/pl/p0* | wc -c)
/usr/bin/time -v "$zstd" -b3 -e3 -i4 -D "$work/d16.zdict" "$work"/pl/p0* > "$work/ref.out" 2> "$work/ref.time" || true
ref="$(tr '\r' '\n' < "$work/ref.out" | grep 'MB/s' | tail -1 | sed 's/.*MB\/s, *//; s/ *MB\/s.*//')"
/usr/bin/time -v "$dotnet" "$dll" small "$work/d16.zdict" "$work/frames" --seconds 4 > "$work/ours.out" 2> "$work/ours.time"
echo "# small: 3000 payloads, $total bytes total; reference decode $ref MB/s => $(awk -v r="$ref" -v t="$total" 'BEGIN{printf "%.0f", r*1e6/(t/3000)}') frames/s"
echo "# small ours: $(cat "$work/ours.out"); peak KB ref $(peak "$work/ref.time") ours $(peak "$work/ours.time"); load after $(load)"
