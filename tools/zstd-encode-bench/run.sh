#!/usr/bin/env bash
# Compression and decompression speed, ratio and peak memory of the Zstandard encoder against the zstd tool's benchmark mode
# (`zstd -b<L> -e<L> -i4 -T1`), memory to memory, whole file as one block.
#
#   run.sh [--files "silesia.tar enwik8"] [--levels "-5 -1 1 2 3 4"] [--seconds 4] [--ref-opts "-T1"] [--ours-opts ""]
#
# --ref-opts are extra options for the tool (default -T1), --ours-opts for ZstdEncodeBench, for the same configuration: for example
# `--ref-opts "-T8 --long=27" --ours-opts "--workers 8 --job auto --long 27"` compares long-distance matching on 8 jobs at a time.
#
# Environment: ONEARCHIVE_ZSTD, ONEARCHIVE_CORPORA, BENCH_DLL (ZstdEncodeBench.dll), DOTNET. "Decompress" is our decoder on our
# output against the tool decoding its own output; both are printed. Takes the machine's time: run it when nothing else heavy is.
set -euo pipefail
zstd="${ONEARCHIVE_ZSTD:-zstd}"
corpora="${ONEARCHIVE_CORPORA:-$HOME/onearchive/corpora}"
dll="${BENCH_DLL:?set BENCH_DLL to ZstdEncodeBench.dll}"
dotnet="${DOTNET:-dotnet}"
work="${ONEARCHIVE_BENCH_TMP:-${TMPDIR:-/tmp}/zstd-encode-bench}"
files="silesia.tar enwik8"; levels="-5 -1 1 2 3 4"; secs=4; ref_opts="-T1"; ours_opts=""
while [ $# -gt 0 ]; do
  case "$1" in
    --files) files="$2"; shift 2 ;;
    --levels) levels="$2"; shift 2 ;;
    --seconds) secs="$2"; shift 2 ;;
    --ref-opts) ref_opts="$2"; shift 2 ;;
    --ours-opts) ours_opts="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
mkdir -p "$work"
find_file() { for c in "$1" "$corpora/$1" "$corpora/enwik8/$1" "$corpora/silesia/$1"; do [ -f "$c" ] && { echo "$c"; return; }; done; echo "missing $1" >&2; exit 1; }
if [ "$(uname)" = Darwin ]; then
  timev() { /usr/bin/time -l "$@"; }
  peak() { grep 'maximum resident' "$1" | awk '{printf "%d", $1/1024}'; }
  load() { uptime | sed 's/.*load averages*: *//'; }
  cpu() { sysctl -n machdep.cpu.brand_string; }
  cores() { sysctl -n hw.ncpu; }
else
  timev() { /usr/bin/time -v "$@"; }
  peak() { grep 'Maximum resident' "$1" | awk '{print $NF}'; }
  load() { cut -d' ' -f1-3 /proc/loadavg; }
  cpu() { grep -m1 'model name' /proc/cpuinfo | cut -d: -f2 | xargs; }
  cores() { nproc; }
fi
echo "# $("$zstd" --version | head -1)"
echo "# cpu: $(cpu), cores: $(cores), load before: $(load)"
echo "# dotnet $("$dotnet" --version | head -1)"
printf 'file\tlevel\tref_bytes\tours_bytes\tdelta_pct\tref_c_MB/s\tours_c_MB/s\tours_c_med\tours_c_min\tc_time_ratio\tref_d_MB/s\tours_d_MB/s\tours_d_med\tours_d_min\td_time_ratio\tref_peak_KB\tours_peak_KB\tload\n'
for f in $files; do
  path="$(find_file "$f")"
  for L in $levels; do
    if [ "${L#-}" != "$L" ]; then sel="-b --fast=${L#-}"; elif [ "$L" -gt 19 ]; then sel="--ultra -b$L -e$L"; else sel="-b$L -e$L"; fi
    timev "$zstd" $sel -i"$secs" $ref_opts "$path" > "$work/ref.out" 2> "$work/ref.time" || true
    # The last line with both the compress and the decompress rate: the tool's progress lines can end on a single-rate update.
    line="$(tr '\r' '\n' < "$work/ref.out" | grep -E 'MB/s, *[0-9.]+ *MB/s' | tail -1)"
    rb="$(echo "$line" | awk '{for (i = 1; i <= NF; i++) if ($i == "->") print $(i + 1)}')"
    rc="$(echo "$line" | grep -o '[0-9.]* MB/s' | sed -n 1p | cut -d' ' -f1)"
    rd="$(echo "$line" | grep -o '[0-9.]* MB/s' | sed -n 2p | cut -d' ' -f1)"
    timev "$dotnet" "$dll" big "$path" "$L" --seconds "$secs" $ours_opts > "$work/ours.out" 2> "$work/ours.time"
    kv() { tr '\t' '\n' < "$work/ours.out" | sed -n "s/^$1=\([0-9.]*\).*/\1/p" | head -1; }
    ob="$(kv compressed)"; oc="$(kv compress)"; ocm="$(kv compress_median)"; ocn="$(kv compress_min)"
    od="$(kv decompress)"; odm="$(kv decompress_median)"; odn="$(kv decompress_min)"
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$f" "$L" "$rb" "$ob" \
      "$(awk -v a="$ob" -v b="$rb" 'BEGIN{printf "%+.2f",(a-b)*100/b}')" "$rc" "$oc" "$ocm" "$ocn" \
      "$(awk -v a="$oc" -v b="$rc" 'BEGIN{printf "%.2f",b/a}')" "$rd" "$od" "$odm" "$odn" "$(awk -v a="$od" -v b="$rd" 'BEGIN{printf "%.2f",b/a}')" \
      "$(peak "$work/ref.time")" "$(peak "$work/ours.time")" "$(load)"
  done
done
echo "# load after: $(load)"
