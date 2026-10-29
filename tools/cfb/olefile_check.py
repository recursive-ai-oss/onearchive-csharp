#!/usr/bin/env python3
"""Checks that olefile (black box) lists and reads every generated vector exactly as gen_cfb.py described it.

Usage: olefile_check.py VECTORDIR      (exit status 1 on any difference)
"""
import gzip
import io
import json
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
import olefile_dump  # noqa: E402


def main(d):
    manifest = json.load(open(os.path.join(d, "manifest.json")))
    bad = 0
    for g in manifest["good"]:
        raw = open(os.path.join(d, g["file"]), "rb").read()
        if g["file"].endswith(".gz"):
            raw = gzip.decompress(raw)
        tmp = os.path.join(os.environ.get("TMPDIR", "."), "olefile-check.cfb")
        open(tmp, "wb").write(raw)
        got = olefile_dump.dump(tmp)["entries"]
        want = sorted(
            ({"path": e["path"], "kind": e["kind"], **({"size": e["size"], "sha256": e["sha256"]} if e["kind"] == "stream" else {})}
             for e in g["entries"]), key=lambda i: i["path"])
        if got == want:
            print("ok      ", g["file"], len(want), "entries")
        else:
            bad += 1
            print("DIFFER  ", g["file"])
            gp = {tuple(i["path"]): i for i in got}
            wp = {tuple(i["path"]): i for i in want}
            for k in sorted(set(gp) | set(wp)):
                if gp.get(k) != wp.get(k):
                    print("   ", k, gp.get(k), wp.get(k))
    sys.exit(1 if bad else 0)


main(sys.argv[1])
