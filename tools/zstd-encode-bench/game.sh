#!/usr/bin/env bash
# Game snapshot data with trained dictionaries: our encoder against `zstd -b -D`, per game, payload kind, dictionary size and level.
#
#   game.sh [--games "lanterns bricks solitaire"] [--kinds "frames snapshots saves"] [--sizes "4096 16384 65536 114688"]
#           [--levels "1 2 3"] [--train-files 5000] [--test-files 5000] [--timing]
#
# Dictionaries are trained with `zstd --train` on the first --train-files payloads of seed 1; the payloads of seed 2 (the first
# --test-files in name order) are compressed one frame each. Environment: ONEARCHIVE_ZSTD, ONEARCHIVE_CORPORA (holds game/seed1 and
# game/seed2), ONEARCHIVE_BENCH_TMP, BENCH_DLL (ZstdEncodeBench.dll), DOTNET. Without --timing only sizes are compared (one pass);
# with it both sides run for several seconds and payloads per second are reported - take the bench lock first.
set -euo pipefail
zstd="${ONEARCHIVE_ZSTD:-zstd}"
corpora="${ONEARCHIVE_CORPORA:-$HOME/onearchive/corpora}"
work="${ONEARCHIVE_BENCH_TMP:-${TMPDIR:-/tmp}/zstd-game}"
dll="${BENCH_DLL:?set BENCH_DLL to ZstdEncodeBench.dll}"
dotnet="${DOTNET:-dotnet}"
games="lanterns bricks solitaire"; kinds="frames snapshots saves"; sizes="4096 16384 65536 114688"; levels="1 2 3"
train_files=2000; test_files=2000; timing=0
while [ $# -gt 0 ]; do
  case "$1" in
    --games) games="$2"; shift 2 ;;
    --kinds) kinds="$2"; shift 2 ;;
    --sizes) sizes="$2"; shift 2 ;;
    --levels) levels="$2"; shift 2 ;;
    --train-files) train_files="$2"; shift 2 ;;
    --test-files) test_files="$2"; shift 2 ;;
    --timing) timing=1; shift ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
mkdir -p "$work"
secs=0; [ "$timing" = 1 ] && secs=2
echo "# $("$zstd" --version | head -1); timing=$timing; train_files=$train_files test_files=$test_files"
printf 'game\tkind\tdict\tlevel\tpayloads\tbytes\tref_bytes\tours_bytes\tdelta_pct\tref_payloads/s\tours_payloads/s\n'
for g in $games; do for k in $kinds; do
  train="$corpora/game/seed1/$g/$k"; test="$corpora/game/seed2/$g/$k"
  [ -d "$train" ] && [ -d "$test" ] || continue
  { ls "$train" | sort | head -n "$train_files" | sed "s#^#$train/#" > "$work/train.list"; } || true
  { ls "$test" | sort | head -n "$test_files" | sed "s#^#$test/#" > "$work/test.list"; } || true
  for d in $sizes; do
    "$zstd" --train -q --maxdict="$d" -o "$work/dict" $(cat "$work/train.list") > /dev/null 2>&1 || { echo "train failed $g $k $d" >&2; continue; }
    for L in $levels; do
      out=$("$zstd" -b"$L" -e"$L" -i"$secs" -T1 -D "$work/dict" $(cat "$work/test.list") 2>&1 | tr '\r' '\n' | grep 'MB/s' | tail -1)
      bytes=$(echo "$out" | awk '{for (i = 1; i <= NF; i++) if ($i == "->") print $(i - 1)}')
      refc=$(echo "$out" | awk '{for (i = 1; i <= NF; i++) if ($i == "->") print $(i + 1)}')
      mbs=$(echo "$out" | grep -o '[0-9.]* MB/s' | sed -n 1p | cut -d' ' -f1); n=$(wc -l < "$work/test.list" | tr -d ' ')
      refps=$(awk -v b="$bytes" -v m="$mbs" -v n="$n" 'BEGIN{ if (m > 0 && b > 0) printf "%.0f", n/(b/(m*1e6)); else print 0 }')
      # Our encoder takes the directory; copy the chosen files' names into a limit.
      ours=$("$dotnet" "$dll" small "$work/dict" "$test" "$L" --seconds "$secs" --limit "$test_files")
      oc=$(echo "$ours" | sed 's/.*compressed=\([0-9]*\).*/\1/'); ops=$(echo "$ours" | sed 's/.*payloads\/s=\([0-9]*\).*/\1/')
      printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$g" "$k" "$d" "$L" "$n" "$bytes" "$refc" "$oc" "$(awk -v a="$oc" -v b="$refc" 'BEGIN{printf "%+.2f", (a-b)*100/b}')" "$refps" "$ops"
    done
  done
done; done
