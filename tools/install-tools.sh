#!/usr/bin/env bash
# Installs the pinned reference tools under $ONEARCHIVE_HOME/tools (default
# $HOME/onearchive/tools). User-local, no sudo, idempotent.
#
#   tools/install-tools.sh [id ...]     install the named tools (default: all)
#   tools/install-tools.sh --versions   print the version table of what is installed
#
# Release binaries are used where a project publishes them (7zz, cmake).
# Everything else is built from the pinned release tarball by an unattended
# configure/make; the sources are never opened. Layout:
#   tools/dl/            verified downloads
#   tools/build/         scratch build trees (removed after each build)
#   tools/opt/<id>-<v>/  one prefix per tool
#   tools/bin/           symlinks on the PATH the bench scripts use
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
home="${ONEARCHIVE_HOME:-$HOME/onearchive}"
T="$home/tools"
mkdir -p "$T/dl" "$T/build" "$T/opt" "$T/bin"
JOBS="${JOBS:-6}"

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) platform=linux-x64 ;;
  Darwin-arm64) platform=osx-arm64 ;;
  *) echo "unsupported platform $(uname -sm)" >&2; exit 2 ;;
esac

sha256_of() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

# pin_field <id> <column>: the row of pins.tsv for this id and platform.
pin_row() {
  awk -F'\t' -v id="$1" -v p="$platform" '$0 !~ /^#/ && $1 == id && ($6 == "all" || $6 == p) { print; exit }' "$here/pins.tsv"
}

fetch() { # id -> prints path of the verified artifact
  local row ver file url sha
  row="$(pin_row "$1")"; [ -n "$row" ] || { echo "no pin for $1 on $platform" >&2; return 1; }
  IFS=$'\t' read -r _ ver file url sha _ <<<"$row"
  if [ ! -s "$T/dl/$file" ] || [ "$(sha256_of "$T/dl/$file")" != "$sha" ]; then
    curl -fsSL --retry 3 -o "$T/dl/$file.part" "$url"
    mv "$T/dl/$file.part" "$T/dl/$file"
  fi
  [ "$(sha256_of "$T/dl/$file")" = "$sha" ] || { echo "SHA-256 mismatch for $file" >&2; rm -f "$T/dl/$file"; return 1; }
  echo "$T/dl/$file"
}

version_of() { pin_row "$1" | cut -f2; }
prefix_of() { echo "$T/opt/$1-$(version_of "$1")"; }
link() { ln -sf "$1" "$T/bin/$2"; }
mk() { nice -n 10 make -j"$JOBS" "$@"; }

# unpack <id>: extracts into a fresh build dir and cd's into the top directory.
unpack() {
  local art; art="$(fetch "$1")"
  rm -rf "$T/build/$1"; mkdir -p "$T/build/$1"
  tar -xf "$art" -C "$T/build/$1"
  cd "$T/build/$1"
  [ "$(ls | wc -l)" -eq 1 ] && cd "$(ls)"
  return 0
}

install_cmake() {
  local p; p="$(prefix_of cmake)"; rm -rf "$p"; mkdir -p "$p"
  tar -xf "$(fetch cmake)" -C "$p" --strip-components=1
  if [ -x "$p/bin/cmake" ]; then link "$p/bin/cmake" cmake; else link "$p/CMake.app/Contents/bin/cmake" cmake; fi
}
install_7zz() {
  local p; p="$(prefix_of 7zz)"; rm -rf "$p"; mkdir -p "$p"
  tar -xf "$(fetch 7zz)" -C "$p"
  link "$p/7zz" 7zz
}
install_rar() {
  # Binaries and their licence/readme text only.
  local p; p="$(prefix_of rar)"; rm -rf "$p"; mkdir -p "$p"
  tar -xzf "$(fetch rar)" -C "$p" --strip-components=1 rar/rar rar/unrar rar/default.sfx rar/rarfiles.lst rar/license.txt rar/readme.txt rar/rar.txt
  link "$p/rar" rar; link "$p/unrar" unrar
}
install_zstd() { local p; p="$(prefix_of zstd)"; unpack zstd; mk PREFIX="$p" install; link "$p/bin/zstd" zstd; }
install_lz4() { local p; p="$(prefix_of lz4)"; unpack lz4; mk PREFIX="$p" install; link "$p/bin/lz4" lz4; }
install_libdeflate() {
  local p; p="$(prefix_of libdeflate)"; unpack libdeflate
  PATH="$T/bin:$PATH" cmake -S . -B out -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$p" \
    -DLIBDEFLATE_BUILD_GZIP=ON -DLIBDEFLATE_BUILD_SHARED_LIB=OFF >/dev/null
  PATH="$T/bin:$PATH" cmake --build out -j "$JOBS" >/dev/null
  PATH="$T/bin:$PATH" cmake --install out >/dev/null
  link "$p/bin/libdeflate-gzip" libdeflate-gzip
}
install_zlib-ng() {
  local p; p="$(prefix_of zlib-ng)"; unpack zlib-ng
  # zlib-ng's own gzip-compatible CLI is minigzip; the native (non-compat) API build.
  PATH="$T/bin:$PATH" cmake -S . -B out -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$p" \
    -DZLIB_COMPAT=OFF -DBUILD_TESTING=ON -DWITH_GTEST=OFF -DWITH_BENCHMARKS=OFF >/dev/null
  PATH="$T/bin:$PATH" cmake --build out --target minigzip -j "$JOBS" >/dev/null \
    || PATH="$T/bin:$PATH" cmake --build out -j "$JOBS" >/dev/null
  mkdir -p "$p/bin"
  f="$(find out -name 'minigzip*' -type f -perm -u+x | head -1)"
  [ -n "$f" ] || { echo "zlib-ng: minigzip was not built" >&2; return 1; }
  cp "$f" "$p/bin/minigzip"
  link "$p/bin/minigzip" minigzip-ng
}
install_brotli() {
  local p; p="$(prefix_of brotli)"; unpack brotli
  PATH="$T/bin:$PATH" cmake -S . -B out -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$p" -DBUILD_SHARED_LIBS=OFF >/dev/null
  PATH="$T/bin:$PATH" cmake --build out -j "$JOBS" --target brotli >/dev/null
  mkdir -p "$p/bin"; cp out/brotli "$p/bin/brotli"
  link "$p/bin/brotli" brotli
}
install_xz() {
  local p; p="$(prefix_of xz)"; unpack xz
  ./configure --prefix="$p" --disable-nls --disable-shared >/dev/null; mk >/dev/null; make install >/dev/null
  link "$p/bin/xz" xz
}
install_lzip() {
  local p; p="$(prefix_of lzip)"; unpack lzip
  ./configure --prefix="$p" >/dev/null; mk >/dev/null; make install >/dev/null
  link "$p/bin/lzip" lzip
}
install_bzip2() {
  local p; p="$(prefix_of bzip2)"; unpack bzip2
  mk >/dev/null; make PREFIX="$p" install >/dev/null
  link "$p/bin/bzip2" bzip2
}
install_zlib() {
  local p; p="$(prefix_of zlib)"; unpack zlib
  ./configure --prefix="$p" --static >/dev/null; mk >/dev/null; make install >/dev/null
  mk minigzip >/dev/null; cp minigzip "$p/bin/" 2>/dev/null || { mkdir -p "$p/bin"; cp minigzip "$p/bin/"; }
  link "$p/bin/minigzip" minigzip-zlib
}
install_pigz() {
  local z; z="$(prefix_of zlib)"
  local p; p="$(prefix_of pigz)"; unpack pigz
  mk CFLAGS="-O3 -I$z/include" LDFLAGS="-L$z/lib" >/dev/null; mkdir -p "$p/bin"; cp pigz "$p/bin/"
  link "$p/bin/pigz" pigz
}
install_libarchive() {
  local p; p="$(prefix_of libarchive)"; unpack libarchive
  local z; z="$(prefix_of zlib)"
  CPPFLAGS="-I$z/include" LDFLAGS="-L$z/lib" ./configure --prefix="$p" --disable-shared --without-xml2 --without-expat --without-openssl --without-nettle >/dev/null
  mk >/dev/null; make install >/dev/null
  link "$p/bin/bsdtar" bsdtar
}
install_tar() {
  local p; p="$(prefix_of tar)"; unpack tar
  local libs=""; [ "$(uname -s)" = Darwin ] && libs="-liconv"
  ./configure --prefix="$p" --disable-nls LIBS="$libs" >/dev/null; mk >/dev/null; make install >/dev/null
  link "$p/bin/tar" gnutar
}
install_xorriso() {
  local p; p="$(prefix_of xorriso)"; unpack xorriso
  ./configure --prefix="$p" >/dev/null; mk >/dev/null; make install >/dev/null
  link "$p/bin/xorriso" xorriso
}
install_cabextract() {
  local p; p="$(prefix_of cabextract)"; unpack cabextract
  ./configure --prefix="$p" >/dev/null; mk >/dev/null; make install >/dev/null
  link "$p/bin/cabextract" cabextract
}

all_ids="cmake zlib zstd lz4 zlib-ng libdeflate brotli xz lzip bzip2 7zz pigz libarchive tar xorriso cabextract rar"

# Operating-system tools: recorded, not pinned.
link_system() {
  for t in zip unzip cpio ar; do
    p="$(command -v "$t" || true)"
    if [ -n "$p" ]; then link "$p" "$t"; else echo "warning: system tool $t not found" >&2; fi
  done
}

versions() {
  local tool
  line() { printf '%-14s %s\n' "$1" "$2"; }
  echo "platform: $platform  ($(uname -sr))"
  line zstd "$("$T/bin/zstd" --version 2>&1 | head -1)"
  line lz4 "$("$T/bin/lz4" --version 2>&1 | head -1)"
  line minigzip-ng "zlib-ng $(version_of zlib-ng) (minigzip, no version flag)"
  line minigzip-zlib "zlib $(version_of zlib)"
  line libdeflate-gzip "libdeflate $(version_of libdeflate) $("$T/bin/libdeflate-gzip" -V 2>&1 | head -1)"
  line brotli "$("$T/bin/brotli" --version 2>&1 | head -1)"
  line xz "$("$T/bin/xz" --version 2>&1 | head -1)"
  line lzip "$("$T/bin/lzip" --version 2>&1 | head -1)"
  line bzip2 "$("$T/bin/bzip2" --help 2>&1 | head -1 | sed 's/,.*//;s/^bzip2: //') (pin $(version_of bzip2))"
  line 7zz "$("$T/bin/7zz" 2>&1 | grep -m1 '7-Zip' | cut -c1-80)"
  line pigz "$("$T/bin/pigz" --version 2>&1 | head -1)"
  line bsdtar "$("$T/bin/bsdtar" --version 2>&1 | head -1)"
  line gnutar "$("$T/bin/gnutar" --version 2>&1 | head -1)"
  line xorriso "$("$T/bin/xorriso" -version 2>&1 | grep -m1 -i 'xorriso version\|^xorriso ' | cut -c1-80)"
  line cabextract "$("$T/bin/cabextract" --version 2>&1 | head -1)"
  line rar "$("$T/bin/rar" 2>&1 | grep -m1 -i '^RAR ' | cut -c1-70)"
  line unrar "$("$T/bin/unrar" 2>&1 | grep -m1 -i '^UNRAR ' | cut -c1-70)"
  line cmake "$("$T/bin/cmake" --version 2>&1 | head -1) (build only)"
  line zip "$("$T/bin/zip" -v 2>&1 | sed -n '2p') [system]"
  line unzip "$("$T/bin/unzip" -v 2>&1 | head -1) [system]"
  line cpio "$("$T/bin/cpio" --version 2>&1 | head -1) [system]"
  arv="$("$T/bin/ar" --version 2>&1 | head -1 | cut -c1-60)"; case "$arv" in usage*) arv="ar (no version flag; $(uname -sr))" ;; esac
  line ar "$arv [system]"
}

if [ "${1:-}" = "--versions" ]; then versions; exit 0; fi

ids="${*:-$all_ids}"
for id in $ids; do
  p="$(prefix_of "$id")"
  sha="$(pin_row "$id" | cut -f5)"
  if [ -f "$p/.installed" ] && [ "$(cat "$p/.installed")" = "$sha" ] && [ -z "${FORCE:-}" ]; then
    echo "ok       $id $(version_of "$id")"
    continue
  fi
  echo "install  $id $(version_of "$id")"
  # Not in an || list: set -e must stay active inside the recipe.
  set +e; ( set -e; "install_$id" ) > "$T/build/$id.log" 2>&1; rc=$?; set -e
  if [ $rc -ne 0 ]; then echo "FAILED   $id (see $T/build/$id.log)" >&2; tail -20 "$T/build/$id.log" >&2; exit 1; fi
  mkdir -p "$p"; echo "$sha" > "$p/.installed"
  rm -rf "$T/build/$id"
done
link_system
versions
