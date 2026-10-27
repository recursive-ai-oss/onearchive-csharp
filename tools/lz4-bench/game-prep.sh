#!/usr/bin/env bash
# Builds the small-payload bench inputs from the game snapshot data: for each game, a sample of the bench set's frames
# (every STEP-th file of seed 2) and raw dictionaries of 8 KiB and 64 KiB made by concatenating evenly spaced frames of the
# training set (seed 1).
#
#   game-prep.sh <corpora/game dir> <out dir> [step]
set -euo pipefail
src="$1"; out="$2"; step="${3:-20}"
mkdir -p "$out"
for game in lanterns bricks solitaire; do
  for kind in frames snapshots; do
    [ -d "$src/seed2/$game/$kind" ] || continue
    dir="$out/$game-$kind"; rm -rf "$dir"; mkdir -p "$dir"
    ls "$src/seed2/$game/$kind" | sort | awk -v s="$([ "$kind" = snapshots ] && echo 1 || echo "$step")" 'NR % s == 0' | while read -r f; do cp "$src/seed2/$game/$kind/$f" "$dir/$f"; done
    for kb in 8 64; do
      n=$(ls "$src/seed1/$game/$kind" | wc -l)
      : > "$out/$game-$kind.dict$kb"
      ls "$src/seed1/$game/$kind" | sort | awk -v tot="$(du -sb "$src/seed1/$game/$kind" | cut -f1)" -v k=$((kb * 1024)) 'BEGIN { step = int(tot / k); if (step < 1) step = 1 } NR % step == 0' | while read -r f; do
        cat "$src/seed1/$game/$kind/$f" >> "$out/$game-$kind.dict$kb"
        [ "$(stat -c %s "$out/$game-$kind.dict$kb")" -ge $((kb * 1024)) ] && break
      done || true
      head -c $((kb * 1024)) "$out/$game-$kind.dict$kb" > "$out/$game-$kind.dict$kb.tmp" && mv "$out/$game-$kind.dict$kb.tmp" "$out/$game-$kind.dict$kb"
    done
    echo "$game/$kind: $(ls "$dir" | wc -l) payloads, $(du -sb "$dir" | cut -f1) bytes; dicts $(stat -c %s "$out/$game-$kind.dict8") $(stat -c %s "$out/$game-$kind.dict64")"
  done
done
