#!/usr/bin/env bash
# Rewrites every good vector and the Office files with CompoundFileWriter (versions 3 and 4) and has olefile (black box) list the
# result: names, kinds and per-stream SHA-256 must equal the originals'.
# Usage: check-written.sh OUTDIR VENV     (run from the repository root; needs the OneArchive test project built)
set -euo pipefail
out=${1:?output directory}
venv=${2:?python venv with olefile}
rm -rf "$out"; mkdir -p "$out"
ONEARCHIVE_CFB_WRITE_DIR=$out dotnet test --no-build --project tests/OneArchive.Tests/OneArchive.Tests.csproj --filter-method '*WritesFilesForTheOlefileCheck' >/dev/null
rc=0
# v3-names holds a lone surrogate, which olefile decodes to U+FFFD; the original vector differs in the same way.
"$venv/bin/python" tools/cfb/olefile_check.py "$out" || rc=1
"$venv/bin/python" - "$out" testdata/cfb/office <<'PY' || rc=1
import glob, os, sys
sys.path.insert(0, "tools/cfb")
import olefile_dump
out, orig = sys.argv[1:3]
bad = 0
for f in sorted(glob.glob(os.path.join(out, "office", "*"))):
    base = os.path.basename(f).rsplit(".v", 1)[0]
    want = olefile_dump.dump(os.path.join(orig, base))["entries"]
    got = olefile_dump.dump(f)["entries"]
    print(("ok      " if got == want else "DIFFER  "), os.path.basename(f), len(got), "entries (olefile", olefile_dump.olefile.__version__ + ")")
    bad += got != want
sys.exit(1 if bad else 0)
PY
exit $rc
