#!/usr/bin/env bash
# Makes real Office compound files with LibreOffice (run headless, as a black box) and records what the olefile package
# (also a black box) sees in them. The outputs go to testdata/cfb/office/: the .doc/.xls/.ppt files, olefile.json (every
# storage and stream with its SHA-256) and VERSIONS.txt. The reader's tests compare its own listing with olefile.json.
#
# Needs: soffice on PATH (a system package or an unpacked distribution), uv, python3.
# Usage: tools/cfb/make-office-vectors.sh WORKDIR
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
w=${1:?usage: make-office-vectors.sh WORKDIR}
mkdir -p "$w/src" "$w/out"
cd "$w"

uv venv -q --python 3.13 venv
uv pip install -q --python venv/bin/python -r "$here/requirements.txt"

venv/bin/python - <<'PY'
import random
random.seed(7)
words = 'alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma tau upsilon'.split()
paras = [' '.join(random.choice(words) for _ in range(40)) for _ in range(120)]
open('src/report.html', 'w').write('<html><head><meta charset="utf-8"><title>Quarterly report</title></head><body><h1>Quarterly report</h1>' + ''.join('<p>%s</p>' % p for p in paras) + '<table border=1>' + ''.join('<tr><td>%d</td><td>row %d</td></tr>' % (i, i) for i in range(30)) + '</table></body></html>')
rows = ['id,name,value,note'] + ['%d,item %d,%.3f,%s' % (i, i, random.random() * 1000, random.choice(words)) for i in range(400)]
open('src/table.csv', 'w').write('\n'.join(rows) + '\n')
open('src/small.csv', 'w').write('a,b\n1,2\n')
open('src/small.html', 'w').write('<html><body><p>hi</p></body></html>')
slides = ''.join('<draw:page draw:name="p%d" draw:master-page-name="Default"><draw:frame svg:width="20cm" svg:height="3cm" svg:x="2cm" svg:y="2cm"><draw:text-box><text:p>Slide %d: %s</text:p></draw:text-box></draw:frame></draw:page>' % (i, i, ' '.join(random.choice(words) for _ in range(12))) for i in range(1, 9))
open('src/deck.fodp', 'w').write('<?xml version="1.0" encoding="UTF-8"?><office:document xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0" office:version="1.3" office:mimetype="application/vnd.oasis.opendocument.presentation"><office:body><office:presentation>' + slides + '</office:presentation></office:body></office:document>')
PY

profile="file://$w/lo-profile"
soffice "-env:UserInstallation=$profile" --headless --convert-to 'doc:MS Word 97' --outdir out src/report.html src/small.html
soffice "-env:UserInstallation=$profile" --headless --convert-to 'xls:MS Excel 97' --outdir out src/table.csv src/small.csv
soffice "-env:UserInstallation=$profile" --headless --convert-to 'ppt:MS PowerPoint 97' --outdir out src/deck.fodp

dest="$repo/testdata/cfb/office"
mkdir -p "$dest"
cp out/*.doc out/*.xls out/*.ppt "$dest/"
(cd "$dest" && "$w/venv/bin/python" "$here/olefile_dump.py" *.doc *.xls *.ppt > olefile.json)
{
  echo "soffice: $(soffice --version | head -1)"
  echo "olefile: $("$w/venv/bin/python" -c 'import olefile; print(olefile.__version__)')"
  echo "files made from the sources in make-office-vectors.sh with the filters 'MS Word 97', 'MS Excel 97' and 'MS PowerPoint 97'"
} > "$dest/VERSIONS.txt"
cat "$dest/VERSIONS.txt"
