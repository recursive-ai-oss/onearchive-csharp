#!/usr/bin/env bash
# Decode speed of the Zstandard decoder against the zstd tool's own benchmark mode (`zstd -b`), memory to memory.
#
#   run.sh [--files "silesia.tar enwik8"] [--levels "1 3 9 19"] [--reps 3]
#
# Works on Linux and macOS. Frames come from the tool ("tool" rows) and from this library's encoder ("ours" rows, for levels up
# to OURS_MAX_LEVEL, default 15). Each row repeats --reps times so the spread shows. The frames carry no checksum because
# `zstd -b` frames carry none; checking costs extra. Small payloads with dictionaries are in game.sh.
# Environment: ONEARCHIVE_ZSTD (tool, default zstd), ONEARCHIVE_CORPORA (default ~/onearchive/corpora),
# ONEARCHIVE_BENCH_TMP (scratch), BENCH_DLL (built ZstdDecodeBench.dll), DOTNET (dotnet executable).
# Prints one TSV row per measurement with the load average and the peak memory of both sides (from /usr/bin/time).
# It is a timing run: take the machine's bench lock first and run it when nothing else heavy is running.
set -euo pipefail
zstd="${ONEARCHIVE_ZSTD:-zstd}"
corpora="${ONEARCHIVE_CORPORA:-$HOME/onearchive/corpora}"
work="${ONEARCHIVE_BENCH_TMP:-${TMPDIR:-/tmp}/zstd-decode-bench}"
dll="${BENCH_DLL:?set BENCH_DLL to ZstdDecodeBench.dll}"
dotnet="${DOTNET:-dotnet}"
files="silesia.tar enwik8"
levels="1 3 9 19"
reps=3
while [ $# -gt 0 ]; do
  case "$1" in
    --files) files="$2"; shift 2 ;;
    --levels) levels="$2"; shift 2 ;;
    --reps) reps="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
mkdir -p "$work"
find_file() { for c in "$corpora/$1" "$corpora/enwik8/$1" "$corpora/silesia/$1"; do [ -f "$c" ] && { echo "$c"; return; }; done; echo "missing corpus file $1" >&2; exit 1; }
# Peak resident set in KB from /usr/bin/time: -v on Linux (KB), -l on macOS (bytes).
if [ "$(uname)" = Darwin ]; then
  timed() { local out="$1"; shift; /usr/bin/time -l "$@" 2> "$out.time" > "$out" || true; }
  peak() { awk '/maximum resident set size/ {printf "%d", $1/1024}' "$1"; }
  load() { uptime | sed 's/.*averages*: //'; }
  cpu() { sysctl -n machdep.cpu.brand_string; }
  cores() { sysctl -n hw.ncpu; }
  fsize() { stat -f %z "$1"; }
else
  timed() { local out="$1"; shift; /usr/bin/time -v "$@" 2> "$out.time" > "$out" || true; }
  peak() { grep 'Maximum resident' "$1" | awk '{print $NF}'; }
  load() { cut -d' ' -f1-3 /proc/loadavg; }
  cpu() { grep -m1 'model name' /proc/cpuinfo | cut -d: -f2 | xargs; }
  cores() { nproc; }
  fsize() { stat -c %s "$1"; }
fi
refrate() { tr '\r' '\n' < "$1" | grep 'MB/s' | tail -1 | sed 's/.*MB\/s, *//; s/ *MB\/s.*//'; }

echo "# $("$zstd" --version | head -1)"
echo "# cpu: $(cpu), cores: $(cores), load before: $(load)"
echo "# $("$dotnet" --version | head -1) SDK; ours = ZstdDecodeBench; reference = zstd -b<L> -e<L> -i4 (decompression MB/s); $reps reps per row"
printf 'frames\tfile\tlevel\tcompressed\trep\tref_MB/s\tours_oneshot_MB/s\tours_stream_MB/s\tours/ref\tref_peak_KB\tours_peak_KB\tload_after\n'
for f in $files; do
  path="$(find_file "$f")"
  for L in $levels; do
    for src in tool ours; do
      if [ $src = tool ]; then
        "$zstd" -"$L" --no-check -q -f -c "$path" > "$work/f.zst"
      else
        [ "$L" -le "${OURS_MAX_LEVEL:-15}" ] || continue
        "$dotnet" "$dll" compress "$path" "$work/f.zst" "$L"
      fi
      for r in $(seq 1 "$reps"); do
        timed "$work/ref.out" "$zstd" -b"$L" -e"$L" -i4 "$path"
        ref="$(refrate "$work/ref.out")"
        timed "$work/ours.out" "$dotnet" "$dll" big "$work/f.zst" --seconds 4
        one="$(sed 's/.*oneshot=\([0-9]*\) MB.*/\1/' "$work/ours.out")"
        str="$(sed 's/.*stream=\([0-9]*\) MB.*/\1/' "$work/ours.out")"
        printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$src" "$f" "$L" "$(fsize "$work/f.zst")" "$r" "$ref" "$one" "$str" \
          "$(awk -v r="$ref" -v o="$one" 'BEGIN{printf "%.3f", o/r}')" "$(peak "$work/ref.out.time")" "$(peak "$work/ours.out.time")" "$(load)"
      done
    done
  done
done
