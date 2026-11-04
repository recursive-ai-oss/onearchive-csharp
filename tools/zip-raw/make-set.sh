#!/usr/bin/env bash
# Makes the zip archives for the raw-copy interop test with the reference tools, run as black boxes: Info-ZIP zip (stored, deflate,
# data descriptors through a pipe, forced zip64, traditional encryption), 7-Zip (deflate, deflate64, bzip2, lzma, ppmd, store, AES-256,
# traditional encryption) and LibreOffice (docx and xlsx, when soffice is on PATH).
#
#   tools/zip-raw/make-set.sh OUT
#
# File names are <tool>-<variant>.zip; encrypted ones use the password below (the test passes the same one).
set -euo pipefail
out="${1:?usage: make-set.sh OUT}"
PW=pw-123
Z7="${ONEARCHIVE_7Z:-7zz}"
mkdir -p "$out"; out="$(cd "$out" && pwd)"
tree="$out/.tree"; rm -rf "$tree"; mkdir -p "$tree/dir/nested" "$tree/empty"
python3 - "$tree" <<'PY'
import random, sys
root = sys.argv[1]
r = random.Random(7)
words = ["alpha", "beta", "gamma", "delta", "zip", "entry", "raw", "copy", "deflate", "stored"]
open(root + "/dir/nested/text.txt", "w").write(" ".join(r.choice(words) for _ in range(60000)))
open(root + "/dir/random.bin", "wb").write(bytes(r.getrandbits(8) for _ in range(20000)))
open(root + "/hello.txt", "w").write("hello\n")
open(root + "/zero", "w").close()
open(root + "/unicode-\u00fc\u00ef-\u65e5\u672c.txt", "w").write("unicode name\n")
open(root + "/dir/rows.csv", "w").write("id,name,value\n" + "".join(f"{i},row{i},{i*i}\n" for i in range(3000)))
PY
ln -s hello.txt "$tree/link"

if command -v zip >/dev/null; then
  (cd "$tree" && zip -qry -0 "$out/infozip-store.zip" .)
  (cd "$tree" && zip -qry -9 "$out/infozip-deflate.zip" .)
  (cd "$tree" && zip -qry - . > "$out/infozip-streamed.zip")
  (cd "$tree" && zip -qry -fz "$out/infozip-zip64.zip" .)
  (cd "$tree" && zip -qry -P "$PW" "$out/infozip-zipcrypto.zip" .)
  (cd "$tree" && zip -qry -P "$PW" - . > "$out/infozip-zipcrypto-streamed.zip")
  (cd "$tree" && zip -qry -z "$out/infozip-comment.zip" . <<< "an archive comment")
fi

sz() { (cd "$tree" && "$Z7" a -tzip -bd -bso0 "$@" .); }
rm -f "$out"/7z-*.zip
sz -mm=Deflate "$out/7z-deflate.zip"
sz -mm=Deflate64 "$out/7z-deflate64.zip"
sz -mm=BZip2 "$out/7z-bzip2.zip"
sz -mm=LZMA "$out/7z-lzma.zip"
sz -mm=PPMd "$out/7z-ppmd.zip"
sz -mm=Copy "$out/7z-store.zip"
sz -mm=Deflate -mx=9 -mtc=on -mta=on "$out/7z-times.zip"
sz -mm=Deflate -p"$PW" -mem=AES256 "$out/7z-aes256.zip"
sz -mm=LZMA -p"$PW" -mem=AES128 "$out/7z-aes128-lzma.zip"
sz -mm=Deflate -p"$PW" -mem=ZipCrypto "$out/7z-zipcrypto.zip"
sz -mm=Deflate -mmt=on -mpass=1 "$out/7z-deflate-mt.zip"
sz -mm=zstd "$out/7z-zstd.zip" 2>/dev/null || rm -f "$out/7z-zstd.zip"
sz -mm=xz "$out/7z-xz.zip" 2>/dev/null || rm -f "$out/7z-xz.zip"

if command -v soffice >/dev/null; then
  w="$out/.office"; rm -rf "$w"; mkdir -p "$w"
  cp "$tree/dir/nested/text.txt" "$w/doc.txt"; cp "$tree/dir/rows.csv" "$w/sheet.csv"
  (cd "$w" && HOME="$w" soffice --headless --convert-to docx:"MS Word 2007 XML" doc.txt >/dev/null 2>&1 || true)
  (cd "$w" && HOME="$w" soffice --headless --convert-to xlsx sheet.csv >/dev/null 2>&1 || true)
  [ -f "$w/doc.docx" ] && cp "$w/doc.docx" "$out/libreoffice-doc.zip"
  [ -f "$w/sheet.xlsx" ] && cp "$w/sheet.xlsx" "$out/libreoffice-sheet.zip"
  rm -rf "$w"
fi
rm -rf "$tree"
ls "$out"
