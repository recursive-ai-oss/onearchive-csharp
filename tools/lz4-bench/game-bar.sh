#!/usr/bin/env bash
# Small-payload rows: our frame encoder (reused, dictionary prepared once) against the lz4 library's frame compressor with a
# prepared dictionary, per game and payload kind, per dictionary size (none, then each of DICT_KB, default 16 KiB) and level. Inputs from game-prep.sh.
#
#   game-bar.sh <prep dir> [levels]     environment: BENCH_DLL, REFSMALL (the built refsmall), DOTNET
set -euo pipefail
prep="$1"; levels="${2:--1 1 3 9}"
DICT_KB="${DICT_KB:-16}"
dotnet="${DOTNET:-dotnet}"
printf 'set\tdict\tlevel\tpayloads\tin\tref_out\tours_out\tsize_ratio\tref_pay/s\tours_pay/s\tspeed_ratio\n'
for dir in "$prep"/*-frames "$prep"/*-snapshots; do
  [ -d "$dir" ] || continue
  set_name="$(basename "$dir")"
  for dict in - $DICT_KB; do
    df="-"; [ "$dict" != "-" ] && df="$prep/$set_name.dict$dict"
    for L in $levels; do
      ref="$("$REFSMALL" "$L" "$df" "$dir" 2)"
      ours="$("$dotnet" "$BENCH_DLL" small "$df" "$dir" "$L" --seconds 2)"
      rin=$(echo "$ref" | sed 's/.*in=\([0-9]*\).*/\1/'); rout=$(echo "$ref" | sed 's/.*out=\([0-9]*\).*/\1/')
      rps=$(echo "$ref" | sed 's/.*payloads\/s=\([0-9.]*\).*/\1/'); n=$(echo "$ref" | sed 's/.*payloads=\([0-9]*\).*/\1/')
      oout=$(echo "$ours" | sed 's/.*frames=\([0-9]*\).*/\1/'); ops=$(echo "$ours" | sed 's/.*frame\/s=\([0-9.]*\).*/\1/')
      printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$set_name" "$dict" "$L" "$n" "$rin" "$rout" "$oout" \
        "$(awk -v a="$oout" -v b="$rout" 'BEGIN { printf "%.4f", a / b }')" "$rps" "$ops" "$(awk -v a="$ops" -v b="$rps" 'BEGIN { printf "%.3f", a / b }')"
    done
  done
done
