#!/usr/bin/env bash
# Decode speed on game snapshot data with trained dictionaries, against the zstd tool's own benchmark mode (`zstd -b -D`).
#
#   game.sh [--games "lanterns bricks solitaire"] [--kinds "frames snapshots saves"] [--sizes "16384 114688"]
#           [--levels "frames=3 snapshots=9 saves=19"] [--train-files 5000] [--test-files 3000] [--seconds 4]
#
# For each game, kind and dictionary size: the tool trains a dictionary on the first --train-files payloads of seed 1, compresses
# the first --test-files payloads of seed 2 one frame per file (`zstd -L -D dict --no-check`, the form `zstd -b` makes), then both
# decoders run over the same frames with the dictionary loaded: `zstd -b -D dict files` and ZstdDecodeBench `small`.
# Prints one TSV row per case: payloads/s for both, and ours/ref payload rate. A timing run: take the bench lock first.
# Environment: ONEARCHIVE_ZSTD, ONEARCHIVE_CORPORA, ONEARCHIVE_BENCH_TMP, BENCH_DLL, DOTNET.
set -euo pipefail
export LC_ALL=C
zstd="${ONEARCHIVE_ZSTD:-zstd}"
corpora="${ONEARCHIVE_CORPORA:-$HOME/onearchive/corpora}"
work="${ONEARCHIVE_BENCH_TMP:-${TMPDIR:-/tmp}/zstd-game-bench}"
dll="${BENCH_DLL:?set BENCH_DLL to ZstdDecodeBench.dll}"
dotnet="${DOTNET:-dotnet}"
games="lanterns bricks solitaire"; kinds="frames snapshots saves"; sizes="16384 114688"
levels="frames=3 snapshots=9 saves=19"; train_files=5000; test_files=3000; seconds=4
while [ $# -gt 0 ]; do
  case "$1" in
    --games) games="$2"; shift 2 ;;
    --kinds) kinds="$2"; shift 2 ;;
    --sizes) sizes="$2"; shift 2 ;;
    --levels) levels="$2"; shift 2 ;;
    --train-files) train_files="$2"; shift 2 ;;
    --test-files) test_files="$2"; shift 2 ;;
    --seconds) seconds="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
mkdir -p "$work"
level_of() { for kv in $levels; do [ "${kv%%=*}" = "$1" ] && { echo "${kv#*=}"; return; }; done; echo 3; }
load() { if [ -r /proc/loadavg ]; then cut -d' ' -f1-3 /proc/loadavg; else uptime | sed 's/.*load averages*: //'; fi; }

echo "# $("$zstd" --version | head -1); load before: $(load)"
printf 'game\tkind\tdict\tlevel\tfiles\tbytes\tref_payloads/s\tours_payloads/s\tours/ref\n'
for g in $games; do
  for k in $kinds; do
    L=$(level_of "$k")
    # The first payloads of the manifest's order (a directory can hold hundreds of thousands of files).
    awk -F'\t' -v g="$g" -v k="$k" -v root="$corpora/game/seed1" -v n="$train_files" '$1 == g && $2 == k && c < n { c++; print root "/" $3 }' "$corpora/game/seed1/manifest.tsv" > "$work/train.list"
    awk -F'\t' -v g="$g" -v k="$k" -v root="$corpora/game/seed2" -v n="$test_files" '$1 == g && $2 == k && c < n { c++; print root "/" $3 }' "$corpora/game/seed2/manifest.tsv" > "$work/test.list"
    [ -s "$work/train.list" ] && [ -s "$work/test.list" ] || { echo "# missing $g/$k" >&2; continue; }
    files=$(wc -l < "$work/test.list" | tr -d ' ')
    bytes=$(xargs cat < "$work/test.list" | wc -c | tr -d ' ')
    for d in $sizes; do
      "$zstd" --train -q --maxdict="$d" -o "$work/d.zdict" --filelist="$work/train.list" > /dev/null 2>&1 || { echo "# train failed $g/$k $d" >&2; continue; }
      rm -rf "$work/frames"; mkdir -p "$work/frames"
      i=0
      while read -r f; do
        i=$((i + 1))
        "$zstd" -"$L" --no-check -q -D "$work/d.zdict" -c "$f" > "$work/frames/$(printf '%06d' "$i").zst"
      done < "$work/test.list"
      ref=$("$zstd" -b"$L" -e"$L" -i"$seconds" -D "$work/d.zdict" --filelist="$work/test.list" 2>&1 | tr '\r' '\n' | grep 'MB/s' | tail -1 | sed 's/.*MB\/s, *//; s/ *MB\/s.*//')
      ours=$("$dotnet" "$dll" small "$work/d.zdict" "$work/frames" --seconds "$seconds" | awk -F'\t' '{ split($4, a, " "); print a[1] }')
      awk -v g="$g" -v k="$k" -v d="$d" -v L="$L" -v n="$files" -v b="$bytes" -v r="$ref" -v o="$ours" \
        'BEGIN { rp = r * 1e6 / (b / n); printf "%s\t%s\t%s\t%s\t%d\t%d\t%.0f\t%.0f\t%.2f\n", g, k, d, L, n, b, rp, o, o / rp }'
    done
  done
done
echo "# load after: $(load)"
