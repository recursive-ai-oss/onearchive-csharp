#!/usr/bin/env bash
# Dictionary quality: dictionaries trained by this library against the zstd tool's, judged by the tool's own compressor so that only the
# dictionary differs.
#
#   quality.sh [--games "lanterns bricks solitaire"] [--kinds "frames snapshots saves recordings transfer"] [--sizes "4096 16384 65536 114688"]
#              [--levels "1 3"] [--train-files 20000] [--test-files 5000] [--silesia DIR]
#
# Game data: the first --train-files payloads of seed 1 train, the first --test-files of seed 2 are compressed one frame each.
# --silesia DIR: DIR holds the Silesia files; every file is cut into 4 KiB chunks and even chunks train, odd chunks are compressed.
# For each row the tool trains with `--train` (its default), with `--train-fastcover` and with `--train-cover` at the k and d that
# this library's search chose, and this library trains with its defaults. Columns are the compressed totals in bytes; delta_pct is
# ours against the tool's `--train`. Environment: ONEARCHIVE_ZSTD, ONEARCHIVE_CORPORA, ONEARCHIVE_BENCH_TMP, TRAIN_DLL
# (ZstdTrainBench.dll), DOTNET. Sizes only, no timing, so no bench lock is needed (single-threaded tool runs, one process at a time).
set -euo pipefail
export LC_ALL=C
zstd="${ONEARCHIVE_ZSTD:-zstd}"
corpora="${ONEARCHIVE_CORPORA:-$HOME/onearchive/corpora}"
work="${ONEARCHIVE_BENCH_TMP:-${TMPDIR:-/tmp}/zstd-quality}"
dll="${TRAIN_DLL:?set TRAIN_DLL to ZstdTrainBench.dll}"
dotnet="${DOTNET:-dotnet}"
games="lanterns bricks solitaire"; kinds="frames snapshots saves recordings transfer"; sizes="4096 16384 65536 114688"; levels="1 3"
train_files=20000; test_files=5000; silesia=""
while [ $# -gt 0 ]; do
  case "$1" in
    --games) games="$2"; shift 2 ;;
    --kinds) kinds="$2"; shift 2 ;;
    --sizes) sizes="$2"; shift 2 ;;
    --levels) levels="$2"; shift 2 ;;
    --train-files) train_files="$2"; shift 2 ;;
    --test-files) test_files="$2"; shift 2 ;;
    --silesia) silesia="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
mkdir -p "$work"

total() { # dictionary level testlist -> compressed bytes (the tool's own compressor, one frame per file)
  local dict="$1" level="$2" list="$3" out
  if [ -n "$dict" ]; then
    out=$("$zstd" -b"$level" -e"$level" -i0 -T1 -D "$dict" $(cat "$list") 2>&1 | tr '\r' '\n' | grep 'MB/s' | tail -1)
  else
    out=$("$zstd" -b"$level" -e"$level" -i0 -T1 $(cat "$list") 2>&1 | tr '\r' '\n' | grep 'MB/s' | tail -1)
  fi
  echo "$out" | awk '{for (i = 1; i <= NF; i++) if ($i == "->") print $(i + 1)}'
}

# row <label-game> <label-kind> <train.list> <test.list> <train-dir-for-ours>
row() {
  local g="$1" k="$2" trainlist="$3" testlist="$4" traindir="$5" d params kk dd
  local n; n=$(wc -l < "$testlist" | tr -d ' ')
  for d in $sizes; do
    local ours_line; ours_line=$("$dotnet" "$dll" "$work/ours.dict" --size "$d" --dir "$traindir" --files "$train_files" ${TRAIN_EXTRA:-})
    kk=$(echo "$ours_line" | sed 's/.*k=\([0-9]*\).*/\1/'); dd=$(echo "$ours_line" | sed 's/.*d=\([0-9]*\).*/\1/')
    "$zstd" --train -q --maxdict="$d" -o "$work/ref.dict" $(cat "$trainlist") > /dev/null 2>&1 || { echo "train failed $g $k $d" >&2; continue; }
    "$zstd" --train-fastcover=k="$kk",d="$dd" -q --maxdict="$d" -o "$work/reffast.dict" $(cat "$trainlist") > /dev/null 2>&1 || cp "$work/ref.dict" "$work/reffast.dict"
    "$zstd" --train-cover=k="$kk",d="$dd" -q --maxdict="$d" -o "$work/refcover.dict" $(cat "$trainlist") > /dev/null 2>&1 || cp "$work/ref.dict" "$work/refcover.dict"
    for L in $levels; do
      local none ref fast cover ours
      none=$(total "" "$L" "$testlist"); ref=$(total "$work/ref.dict" "$L" "$testlist")
      fast=$(total "$work/reffast.dict" "$L" "$testlist"); cover=$(total "$work/refcover.dict" "$L" "$testlist")
      ours=$(total "$work/ours.dict" "$L" "$testlist")
      printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$g" "$k" "$d" "$L" "$n" "$none" "$ref" "$fast" "$cover" "$ours" \
        "$(awk -v a="$ours" -v b="$ref" 'BEGIN{printf "%+.2f", (a-b)*100/b}')" "$kk" "$dd"
    done
  done
}

echo "# $("$zstd" --version | head -1); train_files=$train_files test_files=$test_files"
printf 'game\tkind\tdict\tlevel\tpayloads\tno_dict\ttool_train\ttool_fastcover\ttool_cover\tours\tdelta_pct\tk\td\n'
if [ -n "$silesia" ]; then
  rm -rf "$work/sil"; mkdir -p "$work/sil/train" "$work/sil/test"
  i=0
  for f in "$silesia"/*; do
    b=$(basename "$f"); split -b 4096 -d -a 6 "$f" "$work/sil/all_$b."
  done
  for c in "$work"/sil/all_*; do
    if [ $((i % 2)) = 0 ]; then mv "$c" "$work/sil/train/"; else mv "$c" "$work/sil/test/"; fi
    i=$((i + 1))
  done
  train_files=1000000
  ls "$work/sil/train" | sort | sed "s#^#$work/sil/train/#" > "$work/train.list"
  ls "$work/sil/test" | sort | sed "s#^#$work/sil/test/#" > "$work/test.list"
  row silesia chunks-4k "$work/train.list" "$work/test.list" "$work/sil/train"
  rm -rf "$work/sil"
  exit 0
fi
for g in $games; do for k in $kinds; do
  train="$corpora/game/seed1/$g/$k"; test="$corpora/game/seed2/$g/$k"
  [ -d "$train" ] && [ -d "$test" ] || continue
  [ "$(ls "$train" | wc -l)" -gt 0 ] || continue
  { ls "$train" | sort | head -n "$train_files" | sed "s#^#$train/#" > "$work/train.list"; } || true
  { ls "$test" | sort | head -n "$test_files" | sed "s#^#$test/#" > "$work/test.list"; } || true
  row "$g" "$k" "$work/train.list" "$work/test.list" "$train"
done; done
