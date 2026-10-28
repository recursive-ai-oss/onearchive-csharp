#!/usr/bin/env bash
# Seeds for the tar-read, archive-open and tar-roundtrip targets: archives written by GNU tar and bsdtar (run as black
# boxes, when they are on PATH) over a small tree with long names, links and sparse files, plus the hostile archives from
# testdata/tar/make-hostile.py.
#
#   fuzz/seeds-tar.sh OUT PAYLOADS
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$here/.."
out="${1:?usage: seeds-tar.sh OUT PAYLOADS}"; payloads="${2:?}"
mkdir -p "$out/tar-read" "$out/archive-open" "$out/tar-roundtrip"
tree="$out/.tartree"; rm -rf "$tree"; mkdir -p "$tree/dir/sub" "$tree/empty" "$tree/$(printf 'a%.0s' $(seq 1 90))/$(printf 'b%.0s' $(seq 1 90))"
for p in "$payloads"/*; do head -c 3000 "$p" > "$tree/dir/$(basename "$p")"; done
printf 'hello\n' > "$tree/hello.txt"
printf 'long' > "$tree/$(printf 'a%.0s' $(seq 1 90))/$(printf 'b%.0s' $(seq 1 90))/$(printf 'c%.0s' $(seq 1 120))"
ln -s hello.txt "$tree/link"; ln -s ../nowhere "$tree/dir/dangling"; ln "$tree/hello.txt" "$tree/dir/sub/hard"
printf 'a' > "$tree/sparse.bin"; truncate -s 3M "$tree/sparse.bin"; printf 'island' | dd of="$tree/sparse.bin" bs=1 seek=1048576 conv=notrunc 2>/dev/null
i=0
put() { i=$((i + 1)); cp "$1" "$out/tar-read/$2$i"; cp "$1" "$out/archive-open/$2$i"; }
if command -v tar >/dev/null && tar --version | grep -q 'GNU tar'; then
  for f in ustar pax gnu v7; do
    tar --format=$f -cf "$out/.t" -C "$tree" . 2>/dev/null || continue; put "$out/.t" "gnu-$f-"
  done
  for v in 0.0 0.1 1.0; do tar --format=pax --sparse-version=$v -S -cf "$out/.t" -C "$tree" . && put "$out/.t" "gnu-sparse$v-"; done
  tar --format=gnu -S -cf "$out/.t" -C "$tree" . && put "$out/.t" "gnu-oldsparse-"
  if command -v zstd >/dev/null; then tar --use-compress-program=zstd -cf "$out/archive-open/gnu-zst" -C "$tree" .; fi
  if command -v lz4 >/dev/null; then tar --use-compress-program=lz4 -cf "$out/archive-open/gnu-lz4" -C "$tree" .; fi
fi
if command -v bsdtar >/dev/null; then
  for f in ustar pax gnutar v7; do bsdtar --format $f -cf "$out/.t" -C "$tree" . 2>/dev/null || continue; put "$out/.t" "bsd-$f-"; done
fi
rm -f "$out/.t"; rm -rf "$tree"
python3 "$root/testdata/tar/make-hostile.py" "$out/archive-open" >/dev/null
python3 "$root/testdata/tar/make-hostile.py" "$out/tar-read" >/dev/null
find "$out/tar-read" "$out/archive-open" -size +200k -delete
# Round-trip seeds: option byte, then 8-byte entry descriptions.
for n in 1 2 3 4 5 6; do head -c $((1 + 8 * n * 2)) /dev/urandom > "$out/tar-roundtrip/rand$n"; done
