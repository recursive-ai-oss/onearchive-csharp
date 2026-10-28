#!/usr/bin/env bash
# Seeds for the cab-read and lzx-decode targets: cabinets and LZX streams written by the library's test builders (every method,
# reserve areas, UTF-8 names, spanning flags, every block type) and, when gcab is on PATH, cabinets it writes (run as a black box).
#
#   fuzz/seeds-cab.sh OUT PATH-TO-OneArchive.Fuzz.dll
set -euo pipefail
out="${1:?usage: seeds-cab.sh OUT FUZZ_DLL}"
dll="${2:?usage: seeds-cab.sh OUT FUZZ_DLL}"
dotnet "$dll" seeds-cab "$out"
if command -v gcab >/dev/null; then
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT
  printf 'hello cabinet\n%.0s' $(seq 1 500) > "$tmp/a.txt"
  head -c 20000 /dev/urandom > "$tmp/b.bin"
  (cd "$tmp" && gcab -c "$out/cab-read/gcab-stored.cab" a.txt b.bin && gcab -c -z "$out/cab-read/gcab-mszip.cab" a.txt b.bin)
fi
