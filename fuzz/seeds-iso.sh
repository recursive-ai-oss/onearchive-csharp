#!/usr/bin/env bash
# Seeds for the iso-read and iso-roundtrip targets: the images testdata/iso/generate.py made with xorriso and genisoimage (run as
# black boxes), cut after their directories, and random bytes for the round-trip target.
#
#   fuzz/seeds-iso.sh OUT
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$here/.."
out="${1:?usage: seeds-iso.sh OUT}"
mkdir -p "$out/iso-read" "$out/iso-roundtrip"
for f in "$root"/testdata/iso/vectors/*.iso; do
  # The descriptors and directories sit in the first sectors; the rest is file data and padding.
  head -c 110000 "$f" > "$out/iso-read/$(basename "$f")"
done
for n in 1 2 3 4 5 6; do head -c $((2 + 3 * n * 9)) /dev/urandom > "$out/iso-roundtrip/rand$n"; done
