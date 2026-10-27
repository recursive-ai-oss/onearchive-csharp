#!/usr/bin/env python3
"""Regenerates the LZ4 decoder test vectors in this directory.

Usage: python3 generate.py [path-to-lz4]

The inputs come from a fixed pseudo-random generator, so running this again with the same lz4 release writes
identical files. Each vector is a compressed file made by the lz4 command line tool (run as a black box);
manifest.tsv records the input size and SHA-256, the dictionary it needs and the command that made it. The lz4
version is in the manifest header. A second output, tools/hashmatrix/Host/Entries/Lz4Vectors.g.cs, embeds a few
of the vectors for the cross-platform hash matrix.
"""
import base64
import hashlib
import os
import struct
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
LZ4 = sys.argv[1] if len(sys.argv) > 1 else "lz4"
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
    """Repeats of 2 to 7 byte patterns: the matches whose offset is smaller than their length."""
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


def payload(seed, i):
    """A small game-state style payload of 150-300 bytes."""
    r = SplitMix(seed * 1000003 + i)
    fields = [
        '{"tick":%d,"entity":%d,"pos":[%d,%d,%d],' % (1000 + i, r.next() % 64, r.next() % 512, r.next() % 64, r.next() % 512),
        '"health":%d,"state":"%s","inventory":[' % (r.next() % 100, ["idle", "walking", "attacking", "falling"][r.next() % 4]),
    ]
    for _ in range(3 + r.next() % 4):
        fields.append('{"item":"%s","count":%d},' % (WORDS[r.next() % len(WORDS)], r.next() % 20))
    fields.append('],"chunk":[%d,%d]}' % (r.next() % 32, r.next() % 32))
    return "".join(fields).encode()


def sha(b):
    return hashlib.sha256(b).hexdigest()


def run(args, data=None):
    return subprocess.run([LZ4] + args, input=data, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True).stdout


rows = []
blobs = {}


def add(name, data, args, pipe=False, dictionary=""):
    """Compress data with the given arguments and record the vector."""
    os.makedirs(OUT, exist_ok=True)
    if pipe:
        comp = run(args + ["-c", "-q"], data)
        shown = "cat input | lz4 " + " ".join(args) + " -c"
    else:
        tmp = os.path.join(OUT, name + ".tmp")
        with open(tmp, "wb") as f:
            f.write(data)
        comp = run(args + ["-c", "-q", tmp])
        os.remove(tmp)
        shown = "lz4 " + " ".join(args) + " -c input"
    with open(os.path.join(OUT, name + ".lz4"), "wb") as f:
        f.write(comp)
    blobs[name] = comp
    rows.append((name, len(data), sha(data), len(comp), dictionary, shown))


def add_raw(name, comp, data, shown, dictionary=""):
    """Record a vector assembled from other vectors."""
    with open(os.path.join(OUT, name + ".lz4"), "wb") as f:
        f.write(comp)
    blobs[name] = comp
    rows.append((name, len(data), sha(data), len(comp), dictionary, shown))


text300 = text(1, 300)
text100k = text(2, 100 * 1024)
bin150k = binary_records(3, 150 * 1024)
runs300k = runs(4, 300 * 1024)
period100k = short_period(5, 100 * 1024)
rand70k = random_bytes(6, 70 * 1024)
runs5m = runs(8, 5 * 1024 * 1024)

add("empty", b"", [])
add("empty-pipe", b"", [], pipe=True)
add("onebyte", b"Z", [])
for lvl in ("-1", "-9", "-12"):
    add("text300%s" % lvl, text300, [lvl])
for lvl in ("-1", "-9", "-12", "--fast=1", "--fast=5", "--fast=40"):
    add("text100k%s" % lvl, text100k, [lvl])
for b in (4, 5, 6, 7):
    add("text100k-B%d" % b, text100k, ["-B%d" % b, "-3"])
add("text100k-B4-BD", text100k, ["-B4", "-BD", "-3"])
add("text100k-B4-BD-BX", text100k, ["-B4", "-BD", "-BX", "-9"])
add("text100k-B5-BD-nocrc", text100k, ["-B5", "-BD", "--no-frame-crc", "-1"])
add("text100k-BX", text100k, ["-BX", "-1"])
add("text100k-content-size", text100k, ["--content-size", "-1"])
add("text100k-pipe", text100k, ["-1"], pipe=True)
add("text100k-pipe-B4-BD", text100k, ["-B4", "-BD", "-9"], pipe=True)
add("text100k-legacy", text100k, ["-l", "-9"])
add("bin150k-1", bin150k, ["-1"])
add("bin150k-12-B4-BD", bin150k, ["-12", "-B4", "-BD"])
add("runs300k-1", runs300k, ["-1"])
add("runs300k-12-B4-BD", runs300k, ["-12", "-B4", "-BD"])
add("runs5m-B7", runs5m, ["-B7", "-1"])
add("runs5m-B7-BD", runs5m, ["-B7", "-BD", "-12"])
add("period100k-1", period100k, ["-1"])
add("period100k-9-B4-BD", period100k, ["-9", "-B4", "-BD"])
add("random70k-1", rand70k, ["-1"])
add("random70k-B4-BX-BD", rand70k, ["-1", "-B4", "-BX", "-BD"])
add("random70k-legacy", rand70k, ["-l", "-1"])

# Dictionaries: the lz4 tool uses the last 64 KiB of the file, and writes no dictionary ID.
dict_small = text(20, 8000)
dict_big = text(21, 100 * 1024)
with open(os.path.join(HERE, "dict8k.bin"), "wb") as f:
    f.write(dict_small)
with open(os.path.join(HERE, "dict100k.bin"), "wb") as f:
    f.write(dict_big)
for i in (2100, 2101):
    add("payload%d-dict8k" % i, payload(7, i), ["-1", "-D", os.path.join(HERE, "dict8k.bin")], dictionary="dict8k.bin")
    add("payload%d-dict8k-hc" % i, payload(7, i), ["-12", "-D", os.path.join(HERE, "dict8k.bin")], dictionary="dict8k.bin")
add("payload2102-nodict", payload(7, 2102), ["-1"])
add("text100k-dict100k", text100k[:70000] + dict_big[-30000:], ["-3", "-B4", "-D", os.path.join(HERE, "dict100k.bin")], dictionary="dict100k.bin")
add("text100k-dict8k-BD", text100k[:80000], ["-BD", "-B4", "-3", "-D", os.path.join(HERE, "dict8k.bin")], dictionary="dict8k.bin")

# Concatenated frames and skippable frames.
skip = lambda magic, body: struct.pack("<II", 0x184D2A50 + magic, len(body)) + body
combo = skip(0, b"hello") + blobs["text300-1"] + skip(15, b"") + blobs["onebyte"] + blobs["empty"] + skip(3, b"\x00" * 100)
add_raw("concat-skippable", combo, text300 + b"Z", "skippable frames (magic 0, 15, 3) around text300-1, onebyte and empty")
add_raw("concat-mixed", blobs["text100k-legacy"] + blobs["text300-9"] + blobs["text100k-B4-BD"], text100k + text300 + text100k,
        "text100k-legacy, text300-9 and text100k-B4-BD concatenated")

version = subprocess.run([LZ4, "--version"], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True).stdout.strip()
with open(os.path.join(HERE, "manifest.tsv"), "w") as f:
    f.write("# %s\n" % version)
    f.write("# name\tsize\tsha256\tcompressed\tdictionary\tcommand\n")
    for r in rows:
        f.write("\t".join(str(x) for x in r) + "\n")

# Frames for the cross-platform hash matrix, embedded in a C# source file so the matrix host needs no files.
MATRIX = os.path.join(HERE, "..", "..", "tools", "hashmatrix", "Host", "Entries", "Lz4Vectors.g.cs")
chosen = ["empty", "text300-9", "text100k-B4-BD-BX", "text100k-B5-BD-nocrc", "text100k-legacy",
          "runs300k-12-B4-BD", "period100k-9-B4-BD", "random70k-B4-BX-BD", "payload2100-dict8k", "payload2101-dict8k-hc", "concat-skippable"]
dict_for = {r[0]: r[4] for r in rows}
with open(MATRIX, "w") as f:
    f.write("// <auto-generated> by testdata/lz4/generate.py from frames written by the lz4 tool (%s).\n" % version)
    f.write("// Do not edit; run the generator again.\n#nullable enable\nnamespace RecursiveAI.HashMatrix;\n\n")
    f.write("internal static class Lz4Vectors\n{\n")
    f.write("    public static readonly (string Name, string Frame, string? Dictionary)[] Frames =\n    [\n")
    for name in chosen:
        d = dict_for[name]
        f.write('        ("%s", "%s", %s),\n' % (name, base64.b64encode(blobs[name]).decode(), '"%s"' % d if d else "null"))
    f.write("    ];\n\n    public static readonly (string Name, string Data)[] Dictionaries =\n    [\n")
    for name, data in (("dict8k.bin", dict_small), ("dict100k.bin", dict_big[-65536:])):
        f.write('        ("%s", "%s"),\n' % (name, base64.b64encode(data).decode()))
    f.write("    ];\n}\n")
