#!/usr/bin/env bash
# Seeds for the cfb-read target: the compound file vectors made by tools/cfb/gen_cfb.py (the damaged ones too), the LibreOffice-made
# documents, and one copy of each cut short.
#
#   fuzz/seeds-cfb.sh OUT
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$here/.."
out="${1:?usage: seeds-cfb.sh OUT}"
mkdir -p "$out/cfb-read"
for f in "$root"/testdata/cfb/*.cfb "$root"/testdata/cfb/office/*.doc "$root"/testdata/cfb/office/*.xls; do
  b="$(basename "$f")"
  size="$(stat -c %s "$f" 2>/dev/null || stat -f %z "$f")"
  if [ "$size" -gt 70000 ]; then head -c 70000 "$f" > "$out/cfb-read/trunc-$b"; else cp "$f" "$out/cfb-read/$b"; fi
done
