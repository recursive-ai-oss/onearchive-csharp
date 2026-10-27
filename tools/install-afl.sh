#!/usr/bin/env bash
# Installs the coverage-guided fuzz engine (AFL++, built from its pinned release tarball) and the
# SharpFuzz instrumenter under $ONEARCHIVE_HOME/tools (default $HOME/onearchive/tools). User-local,
# no sudo, idempotent. Needs gcc, make, curl and a .NET SDK on PATH.
#
#   tools/install-afl.sh
#
# Result: $T/bin/afl-fuzz and $T/bin/sharpfuzz.
set -euo pipefail

home="${ONEARCHIVE_HOME:-$HOME/onearchive}"
T="$home/tools"
VER=4.40c
SHA=3343796f0b69b0bec07e44033608280c360e0e90b4ddb6bdda263d598fc3e472
URL="https://github.com/AFLplusplus/AFLplusplus/archive/refs/tags/v$VER.tar.gz"
mkdir -p "$T/dl" "$T/build" "$T/opt" "$T/bin"

if [ ! -x "$T/opt/aflplusplus-$VER/afl-fuzz" ]; then
  f="$T/dl/aflplusplus-$VER.tar.gz"
  [ -s "$f" ] || curl -fsSL --retry 3 -o "$f" "$URL"
  [ "$(sha256sum "$f" | cut -d' ' -f1)" = "$SHA" ] || { echo "SHA-256 mismatch for $f" >&2; exit 1; }
  rm -rf "$T/build/afl"; mkdir -p "$T/build/afl"
  tar -xf "$f" -C "$T/build/afl" --strip-components=1
  # Only the fuzzer itself is needed: SharpFuzz instruments .NET assemblies, so no compiler plugins.
  (cd "$T/build/afl" && nice -n 10 make -j4 afl-fuzz afl-showmap afl-tmin NO_PYTHON=1 NO_NYX=1 >/dev/null)
  rm -rf "$T/opt/aflplusplus-$VER"; mkdir -p "$T/opt/aflplusplus-$VER"
  cp "$T/build/afl/afl-fuzz" "$T/build/afl/afl-showmap" "$T/build/afl/afl-tmin" "$T/opt/aflplusplus-$VER/"
  rm -rf "$T/build/afl"
fi
for b in afl-fuzz afl-showmap afl-tmin; do ln -sf "$T/opt/aflplusplus-$VER/$b" "$T/bin/$b"; done

if [ ! -x "$T/opt/sharpfuzz/sharpfuzz" ]; then
  dotnet tool install --tool-path "$T/opt/sharpfuzz" SharpFuzz.CommandLine --version 2.3.0 >/dev/null
fi
ln -sf "$T/opt/sharpfuzz/sharpfuzz" "$T/bin/sharpfuzz"
echo "afl-fuzz $VER, sharpfuzz 2.3.0 installed in $T/bin"
