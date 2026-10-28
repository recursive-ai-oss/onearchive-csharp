#!/usr/bin/env bash
# Writes seed corpora for every coverage-guided target into DIR/<target>/.
#
#   fuzz/make-seeds.sh DIR
#
# Seeds come from the small vectors under testdata/, from the reference tools' own output when they are on
# PATH (zstd, lz4: black-box runs over repository text), and from hand-written recipes for the round-trip targets.
# Corpora are scratch data: keep them outside the checkout.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$here/.."
out="${1:?usage: make-seeds.sh DIR}"
rm -rf "$out"; mkdir -p "$out"
for t in zstd-decode zstd-decode-dict lz4-decode lz4-decode-dict lz4-block zstd-encode-roundtrip \
         zstd-encode-long-roundtrip zstd-train-roundtrip lz4-encode-frame lz4-encode-block \
         deflate-decode zlib-decode gzip-decode bzip2-decode tar-read archive-open tar-roundtrip; do
  mkdir -p "$out/$t"
done

small() { # files at most 40 KB
  for f in "$@"; do [ -f "$f" ] && [ "$(stat -c %s "$f")" -le 40000 ] && echo "$f"; done
}

# Text and binary payloads for the encoders and for reference-tool runs.
payloads="$out/.payloads"; mkdir -p "$payloads"
i=0
for f in "$root"/README.md "$root"/docs/*.md "$root"/THIRD-PARTY-NOTICES.md "$root"/src/OneCompress/Zstd/*.cs; do
  [ -f "$f" ] || continue
  i=$((i + 1)); head -c 20000 "$f" > "$payloads/text$i"
  [ "$i" -ge 14 ] && break
done
head -c 12000 /dev/zero > "$payloads/zeros"
head -c 6000 /dev/urandom > "$payloads/random" || true
printf 'abcabcabcabcabcabcabcabcabcabc%.0s' $(seq 1 300) > "$payloads/periodic"

# Decoders: vectors plus reference-tool output.
v=0
for f in $(small "$root"/testdata/zstd/vectors/*); do
  v=$((v + 1)); cp "$f" "$out/zstd-decode/v$v"
  case "$(basename "$f")" in
    dict*|rawdict*) printf '\x01' | cat - "$f" > "$out/zstd-decode-dict/f$v"; printf '\x02' | cat - "$f" > "$out/zstd-decode-dict/g$v" ;;
    *) printf '\x00' | cat - "$f" > "$out/zstd-decode-dict/f$v" ;;
  esac
done
if command -v zstd >/dev/null; then
  for p in "$payloads"/*; do
    for l in 1 3 9 19; do zstd -q -$l -c "$p" > "$out/zstd-decode/ref-$(basename "$p")-$l" || true; done
  done
  zstd -q --no-check -3 -c "$payloads/text1" > "$out/zstd-decode/ref-nocheck"
  zstd -q --long=20 -5 -c "$payloads/text2" > "$out/zstd-decode/ref-long"
fi
python3 "$root/testdata/lz4/make-fuzz-seeds.py" "$out/.lz4"
cp "$out"/.lz4/frames/* "$out/lz4-decode/"
cp "$out"/.lz4/prefixed/* "$out/lz4-decode-dict/"
cp "$out"/.lz4/blocks/* "$out/lz4-block/"
if command -v lz4 >/dev/null; then
  for p in "$payloads"/*; do
    for l in 1 4 9 12; do lz4 -q -$l -c "$p" > "$out/lz4-decode/ref-$(basename "$p")-$l" || true; done
  done
  lz4 -q -9 -BD -B5 --content-size -c "$payloads/text3" > "$out/lz4-decode/ref-blockdep"
fi

# Deflate, zlib and gzip: the vectors, then the reference tools' own output (gzip, pigz) over the payloads. The first byte of each
# seed is the option byte of the target (0: verify checksums, no dictionary).
for f in $(small "$root"/testdata/deflate/vectors/*); do
  b=$(basename "$f"); opt='\x00'
  case "$b" in *dict*) opt='\x02' ;; esac
  case "$b" in
    *.deflate) { printf "$opt"; cat "$f"; } > "$out/deflate-decode/$b" ;;
    *.zz) { printf "$opt"; cat "$f"; } > "$out/zlib-decode/$b" ;;
    *.gz) { printf "$opt"; cat "$f"; } > "$out/gzip-decode/$b" ;;
  esac
done
for tool in gzip pigz; do
  if command -v $tool >/dev/null; then
    for p in "$payloads"/*; do
      for l in 1 6 9; do { printf '\x00'; $tool -q -$l -c "$p"; } > "$out/gzip-decode/ref-$tool-$(basename "$p")-$l" || true; done
    done
  fi
done
if command -v pigz >/dev/null; then
  for p in "$payloads"/*; do { printf '\x00'; pigz -q -z -6 -c "$p"; } > "$out/zlib-decode/ref-pigz-$(basename "$p")" || true; done
fi

# bzip2: the vectors, and the tool's own output over the payloads at three levels. The first
# byte is the option byte of the target (0: verify checksums, no trailing data).
for f in $(small "$root"/testdata/bzip2/vectors/*); do { printf '\x00'; cat "$f"; } > "$out/bzip2-decode/$(basename "$f")"; done
if command -v bzip2 >/dev/null; then
  for p in "$payloads"/*; do
    for l in 1 5 9; do { printf '\x00'; bzip2 -$l -c "$p"; } > "$out/bzip2-decode/ref-bzip2-$(basename "$p")-$l" || true; done
  done
fi

# Round-trip targets: option bytes followed by a payload (see the target sources for what the bytes mean).
n=0
for p in "$payloads"/*; do
  n=$((n + 1)); b=$(basename "$p")
  for lv in 0 12 16; do # level bytes: 0 -> -9, 12 -> 3, 16 -> 7
    { printf "\\x$(printf %02x $((lv + n % 5)))\\x$(printf %02x $((n * 7 % 256)))\\x$(printf %02x $((8 + n % 8)))"; head -c 8000 "$p"; } > "$out/zstd-encode-roundtrip/$b-$lv"
  done
  { printf '\x05\x00\x00\x00'; head -c 6000 "$p"; } > "$out/lz4-encode-frame/$b"
  { printf '\x01\x00\x00\x00'; head -c 6000 "$p"; } > "$out/lz4-encode-block/$b"
  { printf "\\x$(printf %02x $((n % 4)))\\x$(printf %02x $((n * 5 % 8)))\\x02\\x03\\x04\\x40"; head -c 9000 "$p"; } > "$out/zstd-train-roundtrip/$b"
done
bash "$here/seeds-long.sh" "$out/zstd-encode-long-roundtrip" >/dev/null
bash "$here/seeds-seekable.sh" "$out/zstd-seekable-read" >/dev/null
bash "$here/seeds-tar.sh" "$out" "$payloads" >/dev/null
rm -rf "$out/.lz4" "$payloads"
for d in "$out"/*/; do printf '%s: %s seeds\n' "$(basename "$d")" "$(ls "$d" | wc -l)"; done
