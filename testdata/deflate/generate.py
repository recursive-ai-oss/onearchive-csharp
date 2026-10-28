#!/usr/bin/env python3
"""Regenerates the Deflate, zlib and gzip decoder test vectors in this directory.

Usage: python3 generate.py [path-to-gzip] [path-to-pigz]

The inputs come from a fixed pseudo-random generator, so running this again with the same tools writes identical files.
Each vector is a compressed file made by a reference implementation run as a black box: Python's zlib module (which
binds the zlib library; its version is in the manifest header) for levels, strategies, memory levels and preset
dictionaries, and the gzip and pigz command line tools. A few headers are assembled by hand from RFC 1952 to cover
FEXTRA, FNAME, FCOMMENT and FHCRC. manifest.tsv records each vector's output size and SHA-256, and a second output,
tools/hashmatrix/Host/Entries/DeflateVectors.g.cs, embeds a few vectors for the cross-platform hash matrix.
"""
import base64
import hashlib
import os
import struct
import subprocess
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
GZIP = sys.argv[1] if len(sys.argv) > 1 else "gzip"
PIGZ = sys.argv[2] if len(sys.argv) > 2 else "pigz"
OUT = os.path.join(HERE, "vectors")
MASK = (1 << 64) - 1


class SplitMix:
    def __init__(self, seed):
        self.s = seed & MASK

    def next(self):
        self.s = (self.s + 0x9E3779B97F4A7C15) & MASK
        z = self.s
        z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & MASK
        z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & MASK
        return z ^ (z >> 31)


WORDS = ("the of and to in a is that for it as was with be by on not he this are or his from at which but "
         "have an had they you were their one all we can her has there been if more when will would who so no "
         "archive window sequence literal offset match block frame table state entropy symbol stream buffer "
         "player tick snapshot position health inventory entity chunk delta").split()


def text(seed, size):
    r = SplitMix(seed)
    out = bytearray()
    while len(out) < size:
        out += WORDS[r.next() % len(WORDS)].encode()
        out += b"\n" if r.next() % 11 == 0 else b" "
    return bytes(out[:size])


def binary_records(seed, size):
    r = SplitMix(seed)
    out = bytearray()
    n = 0
    while len(out) < size:
        n += 1
        out += struct.pack("<IHHIQ", n, r.next() % 50, r.next() % 7, 0xC0FFEE, r.next() % 1000)
    return bytes(out[:size])


def runs(seed, size):
    r = SplitMix(seed)
    out = bytearray()
    while len(out) < size:
        out += bytes([r.next() & 0xFF]) * (1 + r.next() % 5000)
    return bytes(out[:size])


def short_period(seed, size):
    """Repeats of 2 to 7 byte patterns: the matches whose distance is smaller than their length."""
    r = SplitMix(seed)
    out = bytearray()
    while len(out) < size:
        period = 2 + r.next() % 6
        pattern = bytes((r.next() & 0xFF) for _ in range(period))
        out += pattern * (3 + r.next() % 200)
        out += bytes((r.next() & 0xFF) for _ in range(r.next() % 9))
    return bytes(out[:size])


def random_bytes(seed, size):
    r = SplitMix(seed)
    out = bytearray()
    while len(out) < size:
        out += struct.pack("<Q", r.next())
    return bytes(out[:size])


def far_matches(seed, size):
    """Random 40 KiB then a copy of its start and pieces of it: matches at distances up to the 32 KiB limit."""
    base = random_bytes(seed, 40000)
    out = bytearray(base)
    r = SplitMix(seed + 1)
    while len(out) < size:
        start = r.next() % (len(base) - 300)
        out += base[start:start + 20 + r.next() % 250]
        out += bytes((r.next() & 0xFF) for _ in range(r.next() % 5))
    return bytes(out[:size])


def sha(b):
    return hashlib.sha256(b).hexdigest()


rows = []
blobs = {}
EXT = {"raw": "deflate", "zlib": "zz", "gzip": "gz"}


def record(name, fmt, comp, data, dictionary, shown):
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, name + "." + EXT[fmt]), "wb") as f:
        f.write(comp)
    blobs[name] = comp
    rows.append((name, fmt, len(data), sha(data), len(comp), dictionary, shown))


def zlib_add(name, fmt, data, level=6, strategy=zlib.Z_DEFAULT_STRATEGY, mem=8, zdict=None, wbits=15, chunk=None, dictname=""):
    w = {"raw": -wbits, "zlib": wbits, "gzip": 16 + wbits}[fmt]
    kw = {"zdict": zdict} if zdict is not None else {}
    c = zlib.compressobj(level, zlib.DEFLATED, w, mem, strategy, **kw)
    if chunk:
        comp = b"".join(c.compress(data[i:i + chunk]) for i in range(0, len(data), chunk)) + c.flush()
    else:
        comp = c.compress(data) + c.flush()
    shown = "zlib.compressobj(level=%d, wbits=%d, memLevel=%d, strategy=%d%s)" % (level, w, mem, strategy, ", zdict" if zdict is not None else "")
    if chunk:
        shown += " fed in %d byte pieces" % chunk
    record(name, fmt, comp, data, dictname, shown)


def flush_add(name, fmt, data):
    """Sync-flush and full-flush points: empty stored blocks in the middle of the stream."""
    w = {"raw": -15, "zlib": 15, "gzip": 31}[fmt]
    c = zlib.compressobj(6, zlib.DEFLATED, w)
    parts = []
    n = len(data) // 3
    parts.append(c.compress(data[:n]) + c.flush(zlib.Z_SYNC_FLUSH))
    parts.append(c.compress(data[n:2 * n]) + c.flush(zlib.Z_FULL_FLUSH))
    parts.append(c.compress(data[2 * n:]) + c.flush())
    record(name, fmt, b"".join(parts), data, "", "zlib.compressobj level 6 with Z_SYNC_FLUSH and Z_FULL_FLUSH between thirds")


def tool(args, data=None):
    return subprocess.run(args, input=data, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True).stdout


def gzip_tool_add(name, data, args, exe=None, named=False, fmt="gzip"):
    exe = exe or GZIP
    if named:
        tmp = os.path.join(OUT, "input.txt")
        os.makedirs(OUT, exist_ok=True)
        with open(tmp, "wb") as f:
            f.write(data)
        os.utime(tmp, (1700000000, 1700000000))
        comp = tool([exe] + args + ["-c", tmp])
        os.remove(tmp)
        shown = os.path.basename(exe) + " " + " ".join(args) + " -c input.txt (name and mtime stored)"
    else:
        comp = tool([exe] + args + ["-c"], data)
        shown = "cat input | " + os.path.basename(exe) + " " + " ".join(args) + " -c"
    record(name, fmt, comp, data, "", shown)


def crc16_header(h):
    return zlib.crc32(h) & 0xFFFF


def gzip_header_member(data, extra=None, fname=None, comment=None, fhcrc=False, mtime=1700000000, xfl=2, os_=3, text_flag=False):
    flg = (1 if text_flag else 0) | (2 if fhcrc else 0) | (4 if extra is not None else 0) | (8 if fname is not None else 0) | (16 if comment is not None else 0)
    h = struct.pack("<BBBBIBB", 0x1F, 0x8B, 8, flg, mtime, xfl, os_)
    if extra is not None:
        h += struct.pack("<H", len(extra)) + extra
    if fname is not None:
        h += fname + b"\0"
    if comment is not None:
        h += comment + b"\0"
    if fhcrc:
        h += struct.pack("<H", crc16_header(h))
    c = zlib.compressobj(6, zlib.DEFLATED, -15)
    body = c.compress(data) + c.flush()
    return h + body + struct.pack("<II", zlib.crc32(data) & 0xFFFFFFFF, len(data) & 0xFFFFFFFF)


text300 = text(1, 300)
text100k = text(2, 100 * 1024)
bin150k = binary_records(3, 150 * 1024)
runs300k = runs(4, 300 * 1024)
period100k = short_period(5, 100 * 1024)
rand70k = random_bytes(6, 70 * 1024)
far200k = far_matches(7, 200 * 1024)
runs3m = runs(8, 3 * 1024 * 1024)

for fmt in ("raw", "zlib", "gzip"):
    zlib_add("empty-%s" % fmt, fmt, b"", 6)
    zlib_add("empty-l0-%s" % fmt, fmt, b"", 0)
    zlib_add("onebyte-%s" % fmt, fmt, b"Z", 6)
    for lvl in (0, 1, 6, 9):
        zlib_add("text300-l%d-%s" % (lvl, fmt), fmt, text300, lvl)
    for lvl in (1, 4, 6, 9):
        zlib_add("text100k-l%d-%s" % (lvl, fmt), fmt, text100k, lvl)
    zlib_add("text100k-l0-%s" % fmt, fmt, text100k, 0)
    for sname, strat in (("filtered", zlib.Z_FILTERED), ("huffman", zlib.Z_HUFFMAN_ONLY), ("rle", zlib.Z_RLE), ("fixed", zlib.Z_FIXED)):
        zlib_add("bin150k-%s-%s" % (sname, fmt), fmt, bin150k, 6, strat)
    zlib_add("bin150k-l9-%s" % fmt, fmt, bin150k, 9)
    zlib_add("runs300k-l6-%s" % fmt, fmt, runs300k, 6)
    zlib_add("runs300k-rle-%s" % fmt, fmt, runs300k, 6, zlib.Z_RLE)
    zlib_add("period100k-l6-%s" % fmt, fmt, period100k, 6)
    zlib_add("period100k-fixed-%s" % fmt, fmt, period100k, 6, zlib.Z_FIXED)
    zlib_add("random70k-l6-%s" % fmt, fmt, rand70k, 6)
    zlib_add("far200k-l9-%s" % fmt, fmt, far200k, 9)
    zlib_add("text100k-l1-mem1-%s" % fmt, fmt, text100k, 1, mem=1)
    zlib_add("text100k-l6-w9-%s" % fmt, fmt, text100k, 6, wbits=9)
    zlib_add("text100k-pieces-%s" % fmt, fmt, text100k, 6, chunk=997)
    flush_add("text100k-flushes-%s" % fmt, fmt, text100k)
    zlib_add("runs3m-l6-%s" % fmt, fmt, runs3m, 6)

# Preset dictionaries (zlib's deflateSetDictionary): zlib streams name them by Adler-32, raw streams cannot.
zdict = text(30, 6000)
with open(os.path.join(HERE, "dictionary.bin"), "wb") as f:
    f.write(zdict)
big_zdict = random_bytes(31, 40000)
with open(os.path.join(HERE, "dictionary-big.bin"), "wb") as f:
    f.write(big_zdict)
zlib_add("text300-dict-zlib", "zlib", text(31, 300), 6, zdict=zdict, dictname="dictionary.bin")
zlib_add("text100k-dict-zlib", "zlib", text100k[:50000], 9, zdict=zdict, dictname="dictionary.bin")
zlib_add("text300-dict-raw", "raw", text(31, 300), 6, zdict=zdict, dictname="dictionary.bin")
zlib_add("far-dict-big-zlib", "zlib", big_zdict[-20000:] + big_zdict[:5000] + far200k[:30000], 6, zdict=big_zdict, dictname="dictionary-big.bin")
zlib_add("far-dict-big-raw", "raw", big_zdict[-20000:] + big_zdict[:5000] + far200k[:30000], 6, zdict=big_zdict, dictname="dictionary-big.bin")

# gzip and pigz tools.
gzip_tool_add("text100k-gzip-1", text100k, ["-1"])
gzip_tool_add("text100k-gzip-9", text100k, ["-9"])
gzip_tool_add("bin150k-gzip-6-n", bin150k, ["-6", "-n"])
gzip_tool_add("empty-gzip-tool", b"", ["-6"])
gzip_tool_add("text100k-pigz-6", text100k, ["-6"], PIGZ)
gzip_tool_add("bin150k-pigz-9-b32", bin150k[:60000], ["-9", "-b", "32"], PIGZ)
gzip_tool_add("text100k-pigz-11", text100k[:30000], ["-11"], PIGZ)
gzip_tool_add("runs300k-pigz-i", runs300k, ["-6", "-i"], PIGZ)
gzip_tool_add("text100k-pigz-zlib", text100k, ["-6", "-z"], PIGZ, fmt="zlib")
gzip_tool_add("text100k-gzip-named", text100k[:5000], ["-6"], named=True)

# Several members, and members with every header field.
record("multi-3", "gzip", blobs["text300-l6-gzip"] + blobs["empty-gzip"] + blobs["onebyte-gzip"] + blobs["bin150k-gzip-6-n"],
       text300 + b"" + b"Z" + bin150k, "", "four gzip members concatenated: text300-l6, empty, onebyte, bin150k")
record("header-all-fields", "gzip", gzip_header_member(text300, extra=b"AB\x04\x00abcdxy\x00\x01\x02", fname=b"report.txt", comment=b"made by a test", fhcrc=True, text_flag=True),
       text300, "", "hand-assembled header: FTEXT FHCRC FEXTRA FNAME FCOMMENT")
record("header-extra-only", "gzip", gzip_header_member(text300, extra=b"", mtime=0, xfl=0, os_=255), text300, "", "hand-assembled header: empty FEXTRA")
record("header-name-empty", "gzip", gzip_header_member(text300, fname=b"", comment=b""), text300, "", "hand-assembled header: empty FNAME and FCOMMENT")
record("header-fhcrc", "gzip", gzip_header_member(text100k[:5000], fhcrc=True), text100k[:5000], "", "hand-assembled header: FHCRC only")

with open(os.path.join(HERE, "manifest.tsv"), "w") as f:
    f.write("# zlib %s via Python %s; %s; %s\n" % (zlib.ZLIB_RUNTIME_VERSION, sys.version.split()[0],
                                                 subprocess.run([GZIP, "--version"], stdout=subprocess.PIPE).stdout.decode().splitlines()[0],
                                                 subprocess.run([PIGZ, "--version"], stdout=subprocess.PIPE, stderr=subprocess.STDOUT).stdout.decode().strip()))
    f.write("# name\tformat\tsize\tsha256\tcompressed\tdictionary\tmade by\n")
    for r in rows:
        f.write("\t".join(str(x) for x in r) + "\n")

# A few small vectors for the hash matrix.
MATRIX = ["empty-gzip", "text300-l6-raw", "text100k-l9-zlib", "bin150k-fixed-raw", "bin150k-rle-gzip", "text100k-l0-zlib",
          "period100k-l6-raw", "far200k-l9-gzip", "text100k-flushes-raw", "multi-3", "header-all-fields", "text100k-pigz-11",
          "text100k-pieces-gzip", "text300-dict-zlib", "text300-dict-raw", "far-dict-big-zlib"]
with open(os.path.join(HERE, "..", "..", "tools", "hashmatrix", "Host", "Entries", "DeflateVectors.g.cs"), "w") as f:
    f.write("// <auto-generated> by testdata/deflate/generate.py from streams written by zlib %s and the gzip and pigz tools.\n" % zlib.ZLIB_RUNTIME_VERSION)
    f.write("// Do not edit; run the generator again.\n#nullable enable\nnamespace RecursiveAI.HashMatrix;\n\ninternal static class DeflateVectors\n{\n")
    f.write("    public static readonly (string Name, string Format, string Data, string? Dictionary)[] Streams =\n    [\n")
    by = {r[0]: r for r in rows}
    for n in MATRIX:
        r = by[n]
        f.write('        ("%s", "%s", "%s", %s),\n' % (n, r[1], base64.b64encode(blobs[n]).decode(), '"%s"' % r[5] if r[5] else "null"))
    f.write("    ];\n\n    public static readonly (string Name, string Data)[] Dictionaries =\n    [\n")
    for n, d in (("dictionary.bin", zdict), ("dictionary-big.bin", big_zdict)):
        f.write('        ("%s", "%s"),\n' % (n, base64.b64encode(d).decode()))
    f.write("    ];\n}\n")
print(len(rows), "vectors", sum(r[4] for r in rows), "bytes")
