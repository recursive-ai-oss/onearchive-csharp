#!/usr/bin/env python3
"""Writes fuzz seeds from the vectors in vectors/: python3 make-fuzz-seeds.py <out-dir>

Creates <out-dir>/frames (whole frames, for lz4-decode), <out-dir>/prefixed (the same behind the one-byte
selector lz4-decode-dict reads) and <out-dir>/blocks (the first blocks of standard frames behind the selector
lz4-block reads).
"""
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
V = os.path.join(HERE, "vectors")
S = sys.argv[1]
for sub in ("frames", "prefixed", "blocks"):
    os.makedirs(os.path.join(S, sub), exist_ok=True)
for f in sorted(os.listdir(V)):
    d = open(os.path.join(V, f), "rb").read()
    if len(d) > 40000 and not f.startswith("text100k-B4"):
        continue
    n = f[:-4]
    open(os.path.join(S, "frames", n), "wb").write(d)
    open(os.path.join(S, "prefixed", n), "wb").write(bytes([1]) + d)
    open(os.path.join(S, "prefixed", n + "-v"), "wb").write(bytes([3]) + d)
    if d[:4] == b"\x04\x22\x4d\x18":
        flg = d[4]
        pos = 4 + 2 + (8 if flg & 8 else 0) + (4 if flg & 1 else 0) + 1
        for k in range(3):
            size = struct.unpack("<I", d[pos:pos + 4])[0]
            if size == 0:
                break
            raw = size & 0x80000000
            size &= 0x7FFFFFFF
            if not raw:
                open(os.path.join(S, "blocks", "%s-%d" % (n, k)), "wb").write(bytes([k % 2]) + d[pos + 4:pos + 4 + size])
            pos += 4 + size + (4 if flg & 0x10 else 0)
