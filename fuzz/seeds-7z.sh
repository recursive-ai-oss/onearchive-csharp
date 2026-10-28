#!/usr/bin/env bash
# Seeds for the sevenzip-read target: the committed vectors under testdata/7z/vectors.
#
#   fuzz/seeds-7z.sh OUT
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="${1:?usage: seeds-7z.sh OUT}"
mkdir -p "$out/sevenzip-read"
for f in "$here"/../testdata/7z/vectors/*.7z; do cp "$f" "$out/sevenzip-read/$(basename "$f")"; done
