#!/usr/bin/env bash
# Seeds for the cpio-read, ar-read and round-trip targets: the archives that testdata/cpio/generate.py made with GNU cpio, GNU ar and
# bsdtar (run as black boxes), a few truncated copies, and random bytes for the round-trip targets.
#
#   fuzz/seeds-cpio-ar.sh OUT
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$here/.."
out="${1:?usage: seeds-cpio-ar.sh OUT}"
mkdir -p "$out/cpio-read" "$out/ar-read" "$out/cpio-roundtrip" "$out/ar-roundtrip"
for f in "$root"/testdata/cpio/*.cpio; do
  b="$(basename "$f")"
  if [ "$(stat -c %s "$f" 2>/dev/null || stat -f %z "$f")" -gt 40000 ]; then head -c 40000 "$f" > "$out/cpio-read/trunc-$b"; else cp "$f" "$out/cpio-read/$b"; fi
done
for f in "$root"/testdata/ar/*.a "$root"/testdata/ar/*.deb; do cp "$f" "$out/ar-read/$(basename "$f")"; done
for n in 1 2 3 4 5 6; do head -c $((2 + 4 * n * 12)) /dev/urandom > "$out/cpio-roundtrip/rand$n"; head -c $((2 + 4 * n * 12)) /dev/urandom > "$out/ar-roundtrip/rand$n"; done
