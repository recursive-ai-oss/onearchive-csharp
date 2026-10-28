#!/usr/bin/env bash
# Builds refbench (in-memory reference timings through libdeflate and zlib-ng) against the pinned installs.
#   build.sh [output-path]      ONEARCHIVE_HOME as in tools/install-tools.sh (default ~/onearchive)
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
T="${ONEARCHIVE_HOME:-$HOME/onearchive}/tools/opt"
ld="$(ls -d "$T"/libdeflate-*)"; ng="$(ls -d "$T"/zlib-ng-*)"
out="${1:-$here/refbench}"
# zlib-ng installs a static library on some platforms and only shared ones on others.
if [ -f "$ng/lib/libz-ng.a" ]; then nglib="$ng/lib/libz-ng.a"; else nglib="-L$ng/lib -lz-ng -Wl,-rpath,$ng/lib"; fi
${CC:-cc} -O2 -o "$out" "$here/refbench.c" -I"$ld/include" -I"$ng/include" "$ld/lib/libdeflate.a" $nglib
echo "$out"
