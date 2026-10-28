#!/usr/bin/env bash
# Training time and peak memory: this library against `zstd --train`, on the same sample files.
#
#   timing.sh [--runs 3] [--sizes "16384 112640"] [--silesia DIR]
#
# Rows: three game sets of seed 1 (the first 20000 payloads) and Silesia cut into 4 KiB chunks. For each row and dictionary size the
# tool and this library (one thread, and the default thread count) run --runs times; the best wall time and the largest peak
# resident size (from /usr/bin/time) are printed. Environment: ONEARCHIVE_ZSTD, ONEARCHIVE_CORPORA, ONEARCHIVE_BENCH_TMP, TRAIN_DLL, DOTNET.
# A timing run: take the machine's bench lock first and record the load before and after.
set -eu
export LC_ALL=C
zstd="${ONEARCHIVE_ZSTD:-zstd}"
corpora="${ONEARCHIVE_CORPORA:-$HOME/onearchive/corpora}"
work="${ONEARCHIVE_BENCH_TMP:-${TMPDIR:-/tmp}/zstd-train-timing}"
dll="${TRAIN_DLL:?set TRAIN_DLL to ZstdTrainBench.dll}"
dotnet="${DOTNET:-dotnet}"
runs=3; sizes="16384 112640"; silesia=""
while [ $# -gt 0 ]; do
  case "$1" in
    --runs) runs="$2"; shift 2 ;;
    --sizes) sizes="$2"; shift 2 ;;
    --silesia) silesia="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
mkdir -p "$work"

# Portable helpers: Linux has /proc/loadavg, nproc and GNU time; macOS has sysctl and BSD time (-l, bytes).
load() { if [ -r /proc/loadavg ]; then cut -d' ' -f1-3 /proc/loadavg; else sysctl -n vm.loadavg | tr -d '{}' | awk '{print $1, $2, $3}'; fi; }
cores() { if command -v nproc > /dev/null; then nproc; else sysctl -n hw.ncpu; fi; }
# best_of <label...> -- command: prints "best_seconds max_peak_mb"
best_of() {
  local best=999999 peak=0 i s kb
  for i in $(seq 1 "$runs"); do
    if [ "$(uname)" = Darwin ]; then
      /usr/bin/time -l -o "$work/time.out" "$@" > /dev/null 2>&1 || true
      s=$(awk '/ real /{print $1}' "$work/time.out" | tail -1); kb=$(awk '/maximum resident set size/{print $1 / 1024}' "$work/time.out" | tail -1)
    else
      /usr/bin/time -f '%e %M' -o "$work/time.out" "$@" > /dev/null 2>&1 || true
      s=$(awk '{print $1}' "$work/time.out" | tail -1); kb=$(awk '{print $2}' "$work/time.out" | tail -1)
    fi
    best=$(awk -v a="$best" -v b="$s" 'BEGIN{print (b < a) ? b : a}')
    peak=$(awk -v a="$peak" -v b="$kb" 'BEGIN{print (b > a) ? b : a}')
  done
  echo "$best $((${peak%.*} / 1024))"
}

row() { # name train.list train-dir
  local name="$1" list="$2" dir="$3" d t o1 on
  for d in $sizes; do
    t=$(best_of "$zstd" --train -q --maxdict="$d" -o "$work/t.dict" --filelist="$list")
    o1=$(best_of "$dotnet" "$dll" "$work/o.dict" --size "$d" --dir "$dir" --files 1000000 --threads 1)
    on=$(best_of "$dotnet" "$dll" "$work/o.dict" --size "$d" --dir "$dir" --files 1000000)
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t# load %s\n' "$name" "$d" "$(wc -l < "$list" | tr -d ' ')" ${t} ${o1} ${on} "$(load)"
  done
}

echo "# $("$zstd" --version | head -1); runs=$runs; cores=$(cores); load before: $(load)"
printf 'set\tdict\tfiles\ttool_s\ttool_mb\tours1_s\tours1_mb\tours_s\tours_mb\n'
for gk in "bricks frames" "lanterns saves" "solitaire snapshots"; do
  set -- $gk
  dir="$corpora/game/seed1/$1/$2"
  { ls "$dir" | sort | head -n 20000 | sed "s#^#$dir/#" > "$work/list"; } || true
  # Only the first 20000 files of the directory are used by both sides: link them into one place.
  rm -rf "$work/set"; mkdir -p "$work/set"
  while read -r f; do ln -s "$f" "$work/set/$(basename "$f")"; done < "$work/list"
  row "$1/$2" "$work/list" "$work/set"
done
if [ -n "$silesia" ]; then
  rm -rf "$work/sil"; mkdir -p "$work/sil"
  for f in "$silesia"/*; do split -b 4096 -d -a 6 "$f" "$work/sil/$(basename "$f")."; done
  ls "$work/sil" | sort | sed "s#^#$work/sil/#" > "$work/list"
  row silesia-4k "$work/list" "$work/sil"
  rm -rf "$work/sil"
fi
echo "# load after: $(load)"
