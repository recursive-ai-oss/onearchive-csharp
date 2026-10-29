#!/usr/bin/env bash
# Seeds for the ppmd-decode target: streams written by the library behind the target's three parameter bytes.
#
#   fuzz/seeds-ppmd.sh OUT PATH-TO-OneArchive.Fuzz.dll
set -euo pipefail
out="${1:?usage: seeds-ppmd.sh OUT FUZZ_DLL}"
dll="${2:?usage: seeds-ppmd.sh OUT FUZZ_DLL}"
dotnet "$dll" seeds-ppmd "$out"
