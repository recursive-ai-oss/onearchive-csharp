#!/usr/bin/env python3
"""Lists a compound file with the olefile package (used as a black-box oracle) as JSON:
every storage and stream with its name path (hex of UTF-16LE code units), size and, for streams, SHA-256.

Usage: olefile_dump.py FILE [FILE...]   -> one JSON object per file on stdout, keyed by file name
"""
import hashlib
import json
import os
import sys

import olefile


def hx(name):
    return name.encode("utf-16-le", "surrogatepass").hex()


def dump(path):
    ole = olefile.OleFileIO(path)
    items = []
    try:
        for p in ole.listdir(streams=True, storages=True):
            kind = ole.get_type(p)
            item = {"path": [hx(c) for c in p]}
            if kind == olefile.STGTY_STREAM:
                data = ole.openstream(p).read()
                item.update(kind="stream", size=len(data), sha256=hashlib.sha256(data).hexdigest())
            else:
                item.update(kind="storage")
            items.append(item)
    finally:
        ole.close()
    items.sort(key=lambda i: i["path"])
    return {"olefile": olefile.__version__, "entries": items}


if __name__ == "__main__":
    out = {os.path.basename(f): dump(f) for f in sys.argv[1:]}
    json.dump(out, sys.stdout, indent=1, sort_keys=True)
    sys.stdout.write("\n")
