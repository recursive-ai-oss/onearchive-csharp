#!/usr/bin/env bash
# Writes the RAR5 test archives under testdata/rar/ by running the pinned `rar` 7.23 (a black box: it is only executed)
# on a small tree of files this script creates. The archives are committed; this script is how they were made, with exact
# commands, and it can be re-run to refresh them (timestamps inside archives are the files' times, which the script fixes).
#
#   tools/rar-testdata/generate.sh [OUT_DIR]        (default: testdata/rar)
#
# Needs `rar` 7.23 on PATH (tools/install-tools.sh rar), GNU coreutils and python3 (only to write fixed-content files).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="${1:-$here/../../testdata/rar}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
work="$(mktemp -d "${TMPDIR:-/tmp}/rar-gen.XXXXXX")"
trap 'rm -rf "$work"' EXIT

# The archives record the owner and group of each file. Running inside a user namespace that maps the current user to root makes
# them "root:root" instead of the name of whoever ran this script.
if unshare -Ur true 2>/dev/null; then RAR=(unshare -Ur rar); else RAR=(rar); echo "warning: no user namespace; owner names will be yours" >&2; fi

rar_version="$(rar 2>&1 | sed -n 2p | awk '{print $2}')"
[ "$rar_version" = "7.23" ] || { echo "expected rar 7.23, found $rar_version" >&2; exit 1; }

# ---- source tree -------------------------------------------------------------------------------------------------
src="$work/src"
mkdir -p "$src/docs/sub" "$src/empty-dir"
cd "$src"
python3 - <<'PY'
import random
r = random.Random(20260101)
open("hello.txt", "wb").write(b"Hello, RAR5 container!\n" * 40)
open("docs/sub/notes.txt", "wb").write(b"line of text for the notes file\n" * 100)
open("random.bin", "wb").write(bytes(r.randrange(256) for _ in range(50_000)))
open("empty.txt", "wb").close()
open("ünï-cødé-文件-🙂.txt", "wb").write("Unicode name content\n".encode() * 5)
open("big.bin", "wb").write(bytes(r.randrange(256) for _ in range(300_000)))
PY
chmod 0644 hello.txt docs/sub/notes.txt empty.txt "ünï-cødé-文件-🙂.txt"
chmod 0755 docs docs/sub
chmod 0600 random.bin
chmod 4755 big.bin 2>/dev/null || chmod 0755 big.bin
ln -s hello.txt link-to-hello
ln -s ../../outside/target escaping-link
ln hello.txt hard-to-hello
touch -d '2024-03-05 06:07:08.123456789 UTC' hello.txt hard-to-hello
touch -d '2023-11-12 13:14:15.987654321 UTC' docs/sub/notes.txt random.bin empty.txt "ünï-cødé-文件-🙂.txt" big.bin
touch -h -d '2022-02-03 04:05:06.5 UTC' link-to-hello escaping-link
touch -d '2021-01-02 03:04:05 UTC' docs docs/sub empty-dir
printf 'An archive comment.\nSecond line.\n' > "$work/comment.txt"

# ---- the archives ------------------------------------------------------------------------------------------------
# -ma5 RAR5 format, -ep1 store paths relative to the listed names, -ol/-oh keep symlinks and hard links as links,
# -tsm4 -tsc4 -tsa4 keep modification, creation and access times at the highest precision, -ow keeps owner and group.
base=(-ma5 -ep1 -ol -oh -ow -tsm4 -tsc4 -tsa4 -r -idq)
members=(hello.txt docs empty.txt random.bin ünï-cødé-文件-🙂.txt empty-dir link-to-hello escaping-link hard-to-hello)

make() { # name, extra rar switches...
  local name="$1"; shift
  rm -f "$out/$name"
  "${RAR[@]}" a "${base[@]}" "$@" "$out/$name" "${members[@]}" >/dev/null
}

make stored.rar -m0
make stored-blake2.rar -m0 -htb
make stored-solid.rar -m0 -s
make stored-comment.rar -m0 "-z$work/comment.txt"
make stored-recovery.rar -m0 -rr5%
make stored-quickopen.rar -m0 -qo+
make stored-locked.rar -m0 -k
for m in 1 2 3 4 5; do make "m$m.rar" "-m$m"; done
make m3-solid.rar -m3 -s
make m3-blake2.rar -m3 -htb
make encrypted-headers.rar -m0 -hpsecret
make encrypted-data.rar -m0 -psecret

# Volumes: a file larger than the volume size spans volumes. -v40k with a 300 KB incompressible file.
rm -rf "$out/volumes"; mkdir -p "$out/volumes"
"${RAR[@]}" a -ma5 -m0 -ep1 -v40k -idq "$out/volumes/vol.rar" big.bin hello.txt >/dev/null
# (Old-style volume names name.rar, name.r00, ... are the same bytes under other names; the tests rename the parts.)
rm -rf "$out/volumes-oldname"

# Hash manifest of the sources, so tests can compare extracted bytes without shipping the tree twice.
( cd "$src" && find . -type f -printf '%P\n' | LC_ALL=C sort | while IFS= read -r f; do printf '%s  %s\n' "$(sha256sum < "$f" | cut -d' ' -f1)" "$f"; done ) > "$out/sources.sha256"
echo "rar $rar_version" > "$out/VERSION"
ls -l "$out" | tail -n +2
