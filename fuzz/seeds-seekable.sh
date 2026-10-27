#!/usr/bin/env bash
# Writes seed files for the zstd-seekable-read target into DIR (default: seeds-seekable/): seekable files made by the
# seekable-format example producer (run as a black box; see tools/build-zstd-seekable-examples.sh) over generated text,
# at several frame sizes and levels, plus the same data compressed without a seek table and a lone seek table.
set -euo pipefail
dir="${1:-seeds-seekable}"
bin="${ONEARCHIVE_TOOLS:-$HOME/onearchive/tools/bin}"
mkdir -p "$dir"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
for n in 300 5000 60000; do
  seq 1 "$n" > "$work/t$n"
  for frame in 512 4096 20000; do
    cp "$work/t$n" "$work/s"
    "$bin/seekable-compress" "$work/s" "$frame" 3 >/dev/null
    mv "$work/s.zst" "$dir/ref-$n-$frame.zst"
  done
  "$bin/zstd" -q -c "$work/t$n" > "$dir/plain-$n.zst"
done
