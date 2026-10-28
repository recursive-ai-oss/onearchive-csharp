#!/usr/bin/env bash
# Seeds for the zip-read, zip-roundtrip and single-stream-read targets: archives written by Info-ZIP zip, bsdtar and 7-Zip
# (run as black boxes, when they are on PATH) over a small tree, streamed and zip64 variants, compressed single files from
# gzip, zstd and lz4, plus the hostile archives from testdata/zip/make-hostile.py.
#
#   fuzz/seeds-zip.sh OUT PAYLOADS
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$here/.."
out="${1:?usage: seeds-zip.sh OUT PAYLOADS}"; payloads="${2:?}"
mkdir -p "$out/zip-read" "$out/zip-roundtrip" "$out/single-stream-read"
tree="$out/.ziptree"; rm -rf "$tree"; mkdir -p "$tree/dir/sub" "$tree/empty"
for p in "$payloads"/*; do head -c 3000 "$p" > "$tree/dir/$(basename "$p")"; done
printf 'hello\n' > "$tree/hello.txt"; printf 'x' > "$tree/sub-x"; : > "$tree/dir/sub/zero"
ln -s hello.txt "$tree/link"
i=0
put() { i=$((i + 1)); cp "$1" "$out/zip-read/$2$i"; }
if command -v zip >/dev/null; then
  for opt in "-qry" "-qry -0" "-qry -9" "-qry -fz" "-qr -X"; do
    (cd "$tree" && zip $opt "$out/.z.zip" . >/dev/null) && put "$out/.z.zip" "infozip-" && rm -f "$out/.z.zip"
  done
  (cd "$tree" && zip -qry - . > "$out/.z.zip") && put "$out/.z.zip" "infozip-streamed-" && rm -f "$out/.z.zip"
fi
if command -v bsdtar >/dev/null; then
  bsdtar --format zip -cf "$out/.z.zip" -C "$tree" . 2>/dev/null && put "$out/.z.zip" "bsdtar-" && rm -f "$out/.z.zip"
fi
if command -v 7zz >/dev/null || command -v 7z >/dev/null; then
  s7=$(command -v 7zz || command -v 7z)
  (cd "$tree" && "$s7" a -tzip -bd "$out/.z.zip" . >/dev/null) && put "$out/.z.zip" "7z-" && rm -f "$out/.z.zip"
fi
python3 "$root/testdata/zip/make-hostile.py" "$out/zip-read" >/dev/null
cp "$out"/zip-read/hostile-* "$out/single-stream-read/" 2>/dev/null || true
find "$out/zip-read" "$out/single-stream-read" -size +200k -delete
for p in "$payloads"/*; do
  b=$(basename "$p")
  if command -v gzip >/dev/null; then head -c 8000 "$p" | gzip -9 > "$out/single-stream-read/gz-$b"; fi
  if command -v zstd >/dev/null; then head -c 8000 "$p" | zstd -q -3 > "$out/single-stream-read/zst-$b"; fi
  if command -v lz4 >/dev/null; then head -c 8000 "$p" | lz4 -q > "$out/single-stream-read/lz4-$b"; fi
done
# Round-trip seeds: option byte, entry count byte, then payload.
for n in 1 2 3 4 5 6 7 8; do head -c $((2 + 600 * n)) /dev/urandom > "$out/zip-roundtrip/rand$n"; done
rm -rf "$tree" "$out/.z.zip"
