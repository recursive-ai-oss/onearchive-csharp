#!/usr/bin/env bash
# Seeds for the password-carrying targets (zip-read-encrypted, sevenzip-read-encrypted and the two round trips): archives written
# by the library with the fuzz password, plus the reference tools' encrypted zips.
#
#   fuzz/seeds-encrypted.sh OUT PATH-TO-OneArchive.Fuzz.dll
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="${1:?usage: seeds-encrypted.sh OUT FUZZ_DLL}"
dll="${2:?usage: seeds-encrypted.sh OUT FUZZ_DLL}"
dotnet "$dll" seeds-encrypted "$out"
# The tools' archives use the committed vectors' password, so they mostly end in a wrong-password error: useful for the shallow paths.
for f in "$here"/../testdata/encryption/vectors/*.zip; do cp "$f" "$out/zip-read-encrypted/tool-$(basename "$f")"; done
for f in "$here"/../testdata/encryption/vectors/*.7z; do cp "$f" "$out/sevenzip-read-encrypted/tool-$(basename "$f")"; done
