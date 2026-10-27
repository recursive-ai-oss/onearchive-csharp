#!/usr/bin/env bash
# Builds the seekable-format example programs that ship in the pinned zstd release tarball and links them as
# seekable-compress and seekable-decompress under $ONEARCHIVE_HOME/tools/bin. They are an independent seekable
# producer and reader for the interop tests, driven as black boxes: the build runs `make` in the example
# directory and no source file is opened or read.
#
#   tools/build-zstd-seekable-examples.sh
#
# The release tarball is the one pinned in tools/pins.tsv for the zstd reference tool, fetched and verified by
# tools/install-tools.sh into $ONEARCHIVE_HOME/tools/dl. User-local, no sudo, idempotent.
set -euo pipefail

home="${ONEARCHIVE_HOME:-$HOME/onearchive}"
T="$home/tools"
tarball="$T/dl/zstd-1.5.7.tar.gz"
want="eb33e51f49a15e023950cd7825ca74a4a2b43db8354825ac24fc1b7ee09e6fa3"

if [ ! -s "$tarball" ]; then
  mkdir -p "$T/dl"
  curl -fsSL --retry 3 -o "$tarball" https://github.com/facebook/zstd/releases/download/v1.5.7/zstd-1.5.7.tar.gz
fi
have="$( (sha256sum "$tarball" 2>/dev/null || shasum -a 256 "$tarball") | cut -d' ' -f1)"
[ "$have" = "$want" ] || { echo "SHA-256 mismatch for $tarball" >&2; exit 1; }

b="$T/build/zstd-seekable"
rm -rf "$b"; mkdir -p "$b"
tar -xf "$tarball" -C "$b"
cd "$b/zstd-1.5.7"
nice -n 10 make -j"${JOBS:-4}" -C lib libzstd.a >/dev/null
nice -n 10 make -j"${JOBS:-4}" -C contrib/seekable_format/examples seekable_compression seekable_decompression >/dev/null
mkdir -p "$T/bin" "$T/opt/zstd-seekable-1.5.7"
cp contrib/seekable_format/examples/seekable_compression "$T/opt/zstd-seekable-1.5.7/seekable-compress"
cp contrib/seekable_format/examples/seekable_decompression "$T/opt/zstd-seekable-1.5.7/seekable-decompress"
ln -sf "$T/opt/zstd-seekable-1.5.7/seekable-compress" "$T/bin/seekable-compress"
ln -sf "$T/opt/zstd-seekable-1.5.7/seekable-decompress" "$T/bin/seekable-decompress"
rm -rf "$b"
echo "installed seekable-compress and seekable-decompress in $T/bin"
