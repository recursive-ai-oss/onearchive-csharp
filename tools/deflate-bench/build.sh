#!/usr/bin/env bash
# Builds refbench (in-memory reference timings through libdeflate and zlib-ng) against the pinned installs.
#   build.sh [output-path]      ONEARCHIVE_HOME as in tools/install-tools.sh (default ~/onearchive)
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
T="${ONEARCHIVE_HOME:-$HOME/onearchive}/tools/opt"
ld="$(ls -d "$T"/libdeflate-*)"; ng="$(ls -d "$T"/zlib-ng-*)"
out="${1:-$here/refbench}"
${CC:-cc} -O2 -o "$out" "$here/refbench.c" -I"$ld/include" -I"$ng/include" "$ld/lib/libdeflate.a" "$ng/lib/libz-ng.a"
echo "$out"
