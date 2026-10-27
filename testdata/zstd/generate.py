#!/usr/bin/env python3
"""Regenerates the Zstandard decoder test vectors in this directory.

Usage: python3 generate.py [path-to-zstd]

The inputs are produced by a fixed pseudo-random generator, so running this again with the same zstd
release writes identical files. Each vector is a compressed file made by the zstd command line tool
(run as a black box); manifest.tsv records the input size and SHA-256, the exact command, and what the
decoder needs (a maximum window size, a dictionary) to decode it. The zstd version is in the manifest header.
"""
import hashlib
import os
import struct
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ZSTD = sys.argv[1] if len(sys.argv) > 1 else "zstd"
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
    return subprocess.run([ZSTD] + args, input=data, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True).stdout


rows = []


def add(name, data, args, cmd=None, maxwindow=0, dictionary="", pipe=False):
    """Compress data with the given arguments and record the vector."""
    os.makedirs(OUT, exist_ok=True)
    if pipe:
        comp = run(args + ["-c", "-q"], data)
        shown = "cat input | zstd " + " ".join(args) + " -c"
    else:
        tmp = os.path.join(OUT, name + ".tmp")
        with open(tmp, "wb") as f:
            f.write(data)
        comp = run(args + ["-c", "-q", tmp])
        os.remove(tmp)
        shown = "zstd " + " ".join(args) + " -c input"
    if cmd:
        shown = cmd
    with open(os.path.join(OUT, name + ".zst"), "wb") as f:
        f.write(comp)
    rows.append((name, len(data), sha(data), len(comp), maxwindow, dictionary, shown))


text300 = text(1, 300)
text300k = text(2, 300 * 1024)
bin200k = binary_records(3, 200 * 1024)
runs1m = runs(4, 1024 * 1024)
rand64k = random_bytes(5, 64 * 1024)

add("empty-l3", b"", ["-3"])
add("empty-pipe", b"", ["-3"], pipe=True)
add("onebyte", b"Z", ["-3"])
for lvl in (1, 3):
    add("text300-l%d" % lvl, text300, ["-%d" % lvl])
for lvl in (1, 3, 9, 19):
    add("text300k-l%d" % lvl, text300k, ["-%d" % lvl])
add("text300k-nocheck", text300k, ["-5", "--no-check"])
add("text300k-nosize-pipe", text300k, ["-5"], pipe=True)
add("text300k-nosize-flag", text300k, ["-5", "--no-content-size"], pipe=False)
add("text300k-long27", text300k, ["-19", "--long=27"], maxwindow=128 << 20)
add("text300k-ultra22-pipe", text300k, ["--ultra", "-22"], maxwindow=128 << 20, pipe=True)
add("bin200k-l3", bin200k, ["-3"])
add("bin200k-l12", bin200k, ["-12"])
add("runs1m-l3", runs1m, ["-3"])
add("runs1m-l19", runs1m, ["-19"])
add("random64k-l3", rand64k, ["-3"])
add("text300k-mt", text300k, ["-T4", "-3", "--zstd=wlog=18,strategy=2"], cmd="zstd -T4 -3 --zstd=wlog=18,strategy=2 -c input")

# Dictionaries trained on small payloads.
samples_dir = os.path.join(OUT, "samples.tmp")
os.makedirs(samples_dir, exist_ok=True)
for i in range(2000):
    with open(os.path.join(samples_dir, "s%04d" % i), "wb") as f:
        f.write(payload(7, i))
dicts = {}
for kib in (4, 16, 112):
    dpath = os.path.join(HERE, "dict%d.zdict" % kib)
    files = [os.path.join(samples_dir, "s%04d" % i) for i in range(2000)]
    subprocess.run([ZSTD, "--train", "-q", "--maxdict=%d" % (kib * 1024), "-o", dpath] + files, check=True,
                   stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    dicts[kib] = dpath
for f in os.listdir(samples_dir):
    os.remove(os.path.join(samples_dir, f))
os.rmdir(samples_dir)

for kib in (4, 16, 112):
    for lvl in (3, 19):
        for i in (2100, 2101):
            data = payload(7, i)
            add("dict%d-l%d-p%d" % (kib, lvl, i), data, ["-%d" % lvl, "-D", dicts[kib]], dictionary="dict%d.zdict" % kib)
# A frame made without the dictionary's ID in its header, and with several payloads concatenated.
add("dict16-noid", payload(7, 2102), ["-3", "-D", dicts[16], "--no-dictID"], dictionary="dict16.zdict")

# Raw-content dictionary and a patch-from style large reference.
raw_dict = text(9, 40000)
with open(os.path.join(HERE, "rawdict.bin"), "wb") as f:
    f.write(raw_dict)
add("rawdict-l3", text(10, 5000), ["-3", "-D", os.path.join(HERE, "rawdict.bin")], dictionary="rawdict.bin")
ref = text(11, 400 * 1024)
new = bytearray(ref)
r = SplitMix(12)
for _ in range(300):
    new[r.next() % len(new)] = 0x41 + r.next() % 20
with open(os.path.join(HERE, "patchref.bin.tmp"), "wb") as f:
    f.write(ref)
refpath = os.path.join(HERE, "patchref.bin.tmp")
add("patchfrom-l3", bytes(new), ["-3", "--patch-from=" + refpath], dictionary="patchref.bin", maxwindow=1 << 20,
    cmd="zstd -3 --patch-from=patchref.bin -c input")
os.replace(refpath, os.path.join(HERE, "patchref.bin"))

# Skippable frames between and around real frames, as a single file.
skip = lambda magic, body: struct.pack("<II", 0x184D2A50 + magic, len(body)) + body
frame_a = open(os.path.join(OUT, "text300-l3.zst"), "rb").read()
frame_b = open(os.path.join(OUT, "onebyte.zst"), "rb").read()
combo = skip(0, b"hello") + frame_a + skip(15, b"") + frame_b + skip(3, b"\x00" * 100)
with open(os.path.join(OUT, "concat-skippable.zst"), "wb") as f:
    f.write(combo)
both = text300 + b"Z"
rows.append(("concat-skippable", len(both), sha(both), len(combo), 0, "", "skippable frames (magic 0,15,3) around text300-l3 and onebyte"))

version = subprocess.run([ZSTD, "--version"], stdout=subprocess.PIPE, check=True).stdout.decode().strip()
with open(os.path.join(HERE, "manifest.tsv"), "w") as f:
    f.write("# %s\n" % version)
    f.write("# name\tsize\tsha256\tcompressed\tmaxwindow\tdictionary\tcommand\n")
    for r in rows:
        f.write("\t".join(str(x) for x in r) + "\n")
print("wrote %d vectors with %s" % (len(rows), version))
