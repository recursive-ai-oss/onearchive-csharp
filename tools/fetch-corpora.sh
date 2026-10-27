#!/usr/bin/env bash
# Fetches the benchmark corpora into $ONEARCHIVE_HOME/corpora (default
# $HOME/onearchive/corpora), verifies them, and builds silesia.tar.
# Idempotent; nothing here is ever committed.
#
#   tools/fetch-corpora.sh
#
# Layout:
#   silesia/<12 files>   silesia.tar   canterbury/{cantrbry,large,artificl}/...
#   enwik8/enwik8        game/ (empty slot, filled by a separate generator)
#
# Archives are pinned by SHA-256 in corpora-sources.tsv; every extracted file
# (and silesia.tar) is pinned in corpora.sha256. Silesia's published per-file
# MD5s are checked too (silesia.md5).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
home="${ONEARCHIVE_HOME:-$HOME/onearchive}"
C="$home/corpora"
mkdir -p "$C/dl" "$C/silesia" "$C/enwik8" "$C/game" "$C/canterbury"

sha256_of() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
md5_of() { if command -v md5sum >/dev/null; then md5sum "$1" | cut -d' ' -f1; else md5 -q "$1"; fi; }

# Archives
while IFS=$'\t' read -r name url sha; do
  case "$name" in ''|'#'*) continue ;; esac
  f="$C/dl/$name"
  if [ ! -s "$f" ] || [ "$(sha256_of "$f")" != "$sha" ]; then
    curl -fsSL --retry 3 -o "$f.part" "$url"; mv "$f.part" "$f"
  fi
  [ "$(sha256_of "$f")" = "$sha" ] || { echo "SHA-256 mismatch: $name" >&2; rm -f "$f"; exit 1; }
done < "$here/corpora-sources.tsv"

# Extraction (skipped when the files already verify)
verify_all() { ( cd "$C" && while read -r sha path; do
    [ -f "$path" ] && [ "$(sha256_of "$path")" = "$sha" ] || exit 1
  done < "$here/corpora.sha256" ); }

if ! verify_all 2>/dev/null; then
  unzip -qo "$C/dl/silesia.zip" -d "$C/silesia"
  unzip -qo "$C/dl/enwik8.zip" -d "$C/enwik8"
  for n in cantrbry large artificl; do
    mkdir -p "$C/canterbury/$n"; tar -xzf "$C/dl/$n.tar.gz" -C "$C/canterbury/$n"
  done
  # silesia.tar: ustar, names sorted, owner/time zeroed, so every machine builds the same bytes.
  python3 - "$C" <<'PY'
import os, sys, tarfile
c = sys.argv[1]
d = os.path.join(c, "silesia")
with tarfile.open(os.path.join(c, "silesia.tar"), "w", format=tarfile.USTAR_FORMAT) as t:
    for n in sorted(os.listdir(d)):
        ti = tarfile.TarInfo(n)
        ti.size = os.path.getsize(os.path.join(d, n))
        ti.mode = 0o644
        ti.mtime = 0
        with open(os.path.join(d, n), "rb") as f:
            t.addfile(ti, f)
PY
fi

# Verification
fail=0
while read -r md5 name; do
  [ -n "$md5" ] || continue
  [ "$(md5_of "$C/silesia/$name")" = "$md5" ] || { echo "Silesia MD5 mismatch: $name" >&2; fail=1; }
done < "$here/silesia.md5"
[ "$(md5_of "$C/enwik8/enwik8")" = "a1fa5ffddb56f4953e226637dabbb36a" ] || { echo "enwik8 MD5 mismatch" >&2; fail=1; }
verify_all || { echo "corpora.sha256 verification failed" >&2; fail=1; }
[ $fail -eq 0 ] || exit 1
echo "corpora ok: $(wc -l < "$here/corpora.sha256" | tr -d ' ') files verified under $C"
