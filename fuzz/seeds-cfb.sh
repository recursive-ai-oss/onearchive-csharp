#!/usr/bin/env bash
# Seeds for the cfb-read, cfb-rewrite and cfb-recover targets (the same files, plus the damaged vectors for cfb-recover) and cfb-write (slices of a vector, as scripts): the compound file vectors made by tools/cfb/gen_cfb.py (the damaged ones too), the LibreOffice-made
# documents, and one copy of each cut short.
#
#   fuzz/seeds-cfb.sh OUT
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$here/.."
out="${1:?usage: seeds-cfb.sh OUT}"
mkdir -p "$out/cfb-read" "$out/cfb-rewrite" "$out/cfb-write" "$out/cfb-recover"
for f in "$root"/testdata/cfb/*.cfb "$root"/testdata/cfb/office/*.doc "$root"/testdata/cfb/office/*.xls; do
  b="$(basename "$f")"
  size="$(stat -c %s "$f" 2>/dev/null || stat -f %z "$f")"
  if [ "$size" -gt 70000 ]; then head -c 70000 "$f" > "$out/cfb-read/trunc-$b"; else cp "$f" "$out/cfb-read/$b"; fi
done
cp "$out"/cfb-read/* "$out/cfb-rewrite/"
cp "$out"/cfb-read/* "$out/cfb-recover/"
# The damaged vectors from tools/cfb/gen_damaged.py, cut to 70,000 bytes, are the seeds that matter most for cfb-recover.
for f in "$root"/testdata/cfb/damaged/*.cfb "$root"/testdata/cfb/damaged/*.cfb.gz; do
  b="$(basename "$f" .gz)"
  case "$f" in *.gz) { gzip -dc "$f" || true; } | head -c 70000 > "$out/cfb-recover/$b" ;; *) head -c 70000 "$f" > "$out/cfb-recover/$b" ;; esac
done
for n in 40 400 3000; do
  head -c "$n" "$root/testdata/cfb/v3-many-entries.cfb" > "$out/cfb-write/script-$n"
done
