#!/usr/bin/env bash
# Seeds for the rar-read target: the archives that tools/rar-testdata/generate.sh made with the RAR tool (a black box), at most 60 KB
# each (larger ones are cut), a few volumes of the multi-volume set, and a self-extracting-style copy with a prefix.
#
#   fuzz/seeds-rar.sh OUT
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="${1:?usage: seeds-rar.sh OUT}/rar-read"
mkdir -p "$out"
for f in "$here"/../testdata/rar/*.rar "$here"/../testdata/rar/volumes/vol.part1.rar "$here"/../testdata/rar/volumes/vol.part8.rar; do
  head -c 60000 "$f" > "$out/$(basename "$f")"
done
{ head -c 777 /dev/zero; cat "$here/../testdata/rar/stored-comment.rar"; } | head -c 60000 > "$out/sfx-prefixed.rar"
