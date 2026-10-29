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
         deflate-decode zlib-decode gzip-decode deflate-encode-roundtrip deflate64-decode deflate64-encode-roundtrip bzip2-decode bzip2-encode-roundtrip ppmd-decode ppmd-roundtrip brotli-decode brotli-encode-roundtrip lzma-decode xz-decode lzip-decode lzma-encode-roundtrip xz-encode-roundtrip tar-read archive-open tar-roundtrip zip-read zip-roundtrip single-stream-read sevenzip-read cpio-read ar-read cpio-roundtrip ar-roundtrip rar-read iso-read iso-roundtrip; do
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

# Deflate64: the vectors (a leading option byte: 0 means no trailing data and a 16 MiB output limit) and the round-trip recipes
# (level, flags, write size, output size, flush plan, then the program that expands into data).
for f in $(small "$root"/testdata/deflate64/vectors/*.d64); do { printf '\x00'; cat "$f"; } > "$out/deflate64-decode/$(basename "$f")"; done
n=0
for p in "$payloads"/*; do
  n=$((n + 1)); lv=$((n % 10))
  { printf "\x$(printf %02x $lv)\x00\x$(printf %02x $((n * 3 % 256)))\x$(printf %02x $((n * 11 % 256)))\x$(printf %02x $((n % 256)))"; head -c 6000 "$p"; } > "$out/deflate64-encode-roundtrip/$(basename "$p")-$lv"
done

# bzip2: the vectors, and the tool's own output over the payloads at three levels. The first
# byte is the option byte of the target (0: verify checksums, no trailing data).
for f in $(small "$root"/testdata/bzip2/vectors/*); do { printf '\x00'; cat "$f"; } > "$out/bzip2-decode/$(basename "$f")"; done
if command -v bzip2 >/dev/null; then
  for p in "$payloads"/*; do
    for l in 1 5 9; do { printf '\x00'; bzip2 -$l -c "$p"; } > "$out/bzip2-decode/ref-bzip2-$(basename "$p")-$l" || true; done
  done
fi

# bzip2 round trip: (level, longest code length, write size, output size, spare, then the program that expands into data).
n=0
for p in "$payloads"/*; do
  n=$((n + 1))
  { printf "\x$(printf %02x $((n % 9)))\x$(printf %02x $((n * 5 % 12)))\x$(printf %02x $((n * 3 % 256)))\x$(printf %02x $((n * 11 % 256)))\x00"; head -c 6000 "$p"; } > "$out/bzip2-encode-roundtrip/$(basename "$p")-$n"
done
# ppmd: the round-trip seeds are (flags, order, memory selector, then the data); the decoder seeds come from
# fuzz/seeds-ppmd.sh. Flag bit 0 selects variant I, bit 2 the cut-off restore, bit 3 an end marker, and bit 1 (decoder) a known output size.
n=0
for p in "$payloads"/*; do
  n=$((n + 1))
  { printf "\x$(printf %02x $((n % 16)))\x$(printf %02x $((n * 5 % 64)))\x$(printf %02x $((n * 3 % 9)))"; head -c 6000 "$p"; } > "$out/ppmd-roundtrip/$(basename "$p")-$n"
done
# brotli round trip: (quality, window, write size, output size, flush plan, then the program that expands into data).
n=0
for p in "$payloads"/*; do
  n=$((n + 1))
  { printf "\x$(printf %02x $((n % 12)))\x$(printf %02x $((n * 5 % 15)))\x$(printf %02x $((n * 3 % 256)))\x$(printf %02x $((n * 11 % 256)))\x$(printf %02x $((n * 7 % 256)))"; head -c 6000 "$p"; } > "$out/brotli-encode-roundtrip/$(basename "$p")-$n"
done
# brotli: the vectors (a leading option byte: 0 means no trailing data, default window limit), and the tool's output
# over the payloads at three qualities when it is on PATH.
for f in $(small "$root"/testdata/brotli/vectors/*); do { printf '\x00'; cat "$f"; } > "$out/brotli-decode/$(basename "$f")"; done
if command -v brotli >/dev/null; then
  for p in "$payloads"/*; do
    for q in 1 5 11; do { printf '\x00'; brotli -q $q -c "$p"; } > "$out/brotli-decode/ref-brotli-$(basename "$p")-$q" || true; done
  done
fi

# lzma, xz, lzip: the vectors (a leading option byte; for lzma-decode 0 reads the .lzma alone format, 2 raw LZMA2 behind
# a dictionary-property byte, 4 raw LZMA behind a properties byte), and the tools' own output when they are on PATH.
for f in $(small "$root"/testdata/lzma/vectors/*); do
  b=$(basename "$f")
  case "$b" in
    *.lzma) { printf '\x00'; cat "$f"; } > "$out/lzma-decode/$b" ;;
    *.xz) { printf '\x00'; cat "$f"; } > "$out/xz-decode/$b" ;;
    *.lz) { printf '\x00'; cat "$f"; } > "$out/lzip-decode/$b" ;;
  esac
done
printf '\x02\x10\x01\x00\x04hello\x00' > "$out/lzma-decode/lzma2-stored"
printf '\x02\x10\x01\x00\x04hello\x02\x00\x02ab\x00' > "$out/lzma-decode/lzma2-stored-twice"
for p in "$payloads"/*; do
  if command -v xz >/dev/null; then
    for l in 0 6; do { printf '\x00'; xz -$l -c "$p"; } > "$out/xz-decode/ref-xz-$(basename "$p")-$l" || true; done
    { printf '\x00'; xz --format=lzma -6 -c "$p"; } > "$out/lzma-decode/ref-lzma-$(basename "$p")" || true
  fi
  if command -v lzip >/dev/null; then
    { printf '\x00'; lzip -6 -c "$p"; } > "$out/lzip-decode/ref-lzip-$(basename "$p")" || true
  fi
done

# Round-trip targets: option bytes followed by a payload (see the target sources for what the bytes mean).
n=0
for p in "$payloads"/*; do
  n=$((n + 1)); b=$(basename "$p")
  for lv in 0 12 16; do # level bytes: 0 -> -9, 12 -> 3, 16 -> 7
    { printf "\\x$(printf %02x $((lv + n % 5)))\\x$(printf %02x $((n * 7 % 256)))\\x$(printf %02x $((8 + n % 8)))"; head -c 8000 "$p"; } > "$out/zstd-encode-roundtrip/$b-$lv"
  done
  { printf '\x05\x00\x00\x00'; head -c 6000 "$p"; } > "$out/lz4-encode-frame/$b"
  for lv in 1 6 12; do # level byte, format and strategy flags, piece and output sizes, flush plan
    { printf "\x$(printf %02x $lv)\x$(printf %02x $((n * 5 % 128)))\x$(printf %02x $((n * 3 % 256)))\x$(printf %02x $((n * 11 % 256)))\x$(printf %02x $((n % 8)))"; head -c 6000 "$p"; } > "$out/deflate-encode-roundtrip/$b-$lv"
  done
  { printf '\x01\x00\x00\x00'; head -c 6000 "$p"; } > "$out/lz4-encode-block/$b"
  for fmt in 0 1 2; do # format, preset, pb, lc/lp, dictionary and nice, match finder and mode, piece and output sizes
    { printf "\\x$(printf %02x $fmt)\\x$(printf %02x $((n * 3 % 10)))\\x02\\x03\\x$(printf %02x $((n * 7 % 256)))\\x00\\x$(printf %02x $((n % 256)))\\x$(printf %02x $((n * 5 % 256)))"; head -c 6000 "$p"; } > "$out/lzma-encode-roundtrip/$b-$fmt"
  done
  { printf "\\x$(printf %02x $((n % 32)))\\x$(printf %02x $((n * 3 % 10)))\\x02\\x03\\x00\\x00\\x$(printf %02x $((n * 11 % 256)))\\x$(printf %02x $((n * 5 % 256)))"; head -c 6000 "$p"; } > "$out/xz-encode-roundtrip/$b"
  { printf "\\x$(printf %02x $((n % 4)))\\x$(printf %02x $((n * 5 % 8)))\\x02\\x03\\x04\\x40"; head -c 9000 "$p"; } > "$out/zstd-train-roundtrip/$b"
done
bash "$here/seeds-long.sh" "$out/zstd-encode-long-roundtrip" >/dev/null
bash "$here/seeds-seekable.sh" "$out/zstd-seekable-read" >/dev/null
bash "$here/seeds-tar.sh" "$out" "$payloads" >/dev/null
bash "$here/seeds-cpio-ar.sh" "$out" >/dev/null
bash "$here/seeds-cfb.sh" "$out" >/dev/null
bash "$here/seeds-iso.sh" "$out" >/dev/null
bash "$here/seeds-rar.sh" "$out" >/dev/null
bash "$here/seeds-zip.sh" "$out" "$payloads" >/dev/null
bash "$here/seeds-7z.sh" "$out" >/dev/null
rm -rf "$out/.lz4" "$payloads"
for d in "$out"/*/; do printf '%s: %s seeds\n' "$(basename "$d")" "$(ls "$d" | wc -l)"; done
