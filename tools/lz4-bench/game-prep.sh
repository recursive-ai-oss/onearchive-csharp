#!/usr/bin/env bash
# Builds the small-payload bench inputs from the game snapshot data: for each game, a sample of the bench set's frames
# (every STEP-th file of seed 2) and raw dictionaries of 8 KiB and 64 KiB made by concatenating evenly spaced frames of the
# training set (seed 1). DICT_KB (default 16) lists the dictionary sizes in KiB.
#
#   game-prep.sh <corpora/game dir> <out dir> [step]
set -euo pipefail
src="$1"; out="$2"; step="${3:-20}"
DICT_KB="${DICT_KB:-16}"
# Sizes of a directory's files in bytes, portable across GNU and BSD tools.
total_bytes() { find "$1" -type f -print0 | xargs -0 cat | wc -c | tr -d ' '; }
file_bytes() { wc -c < "$1" | tr -d ' '; }
mkdir -p "$out"
for game in lanterns bricks solitaire; do
  for kind in frames snapshots; do
    [ -d "$src/seed2/$game/$kind" ] || continue
    dir="$out/$game-$kind"; rm -rf "$dir"; mkdir -p "$dir"
    ls "$src/seed2/$game/$kind" | sort | awk -v s="$([ "$kind" = snapshots ] && echo 1 || echo "$step")" 'NR % s == 0' | while read -r f; do cp "$src/seed2/$game/$kind/$f" "$dir/$f"; done
    for kb in $DICT_KB; do
      n=$(ls "$src/seed1/$game/$kind" | wc -l)
      : > "$out/$game-$kind.dict$kb"
      ls "$src/seed1/$game/$kind" | sort | awk -v tot="$(total_bytes "$src/seed1/$game/$kind")" -v k=$((kb * 1024)) 'BEGIN { step = int(tot / k); if (step < 1) step = 1 } NR % step == 0' | while read -r f; do
        cat "$src/seed1/$game/$kind/$f" >> "$out/$game-$kind.dict$kb"
        [ "$(file_bytes "$out/$game-$kind.dict$kb")" -ge $((kb * 1024)) ] && break
      done || true
      head -c $((kb * 1024)) "$out/$game-$kind.dict$kb" > "$out/$game-$kind.dict$kb.tmp" && mv "$out/$game-$kind.dict$kb.tmp" "$out/$game-$kind.dict$kb"
    done
    echo "$game/$kind: $(ls "$dir" | wc -l) payloads, $(total_bytes "$dir") bytes; dicts $DICT_KB KiB"
  done
done
