#!/usr/bin/env python3
"""Regenerates the bzip2 decoder test vectors in this directory.

Usage: python3 generate.py [path-to-bzip2]

The inputs come from a fixed pseudo-random generator, so running this again with the same tool writes identical files.
Every vector is made by the bzip2 command line tool, run as a black box (its version is in the manifest header). Two kinds
are assembled by hand around the tool's output:

  * concatenated streams (what `cat a.bz2 b.bz2` makes), and
  * a block with the legacy "randomised" flag: the tool no longer writes those, so the input is first flipped at the scheduled
    positions (the schedule is in src/OneCompress/BZip2/BZip2RandomizationTable.g.cs), compressed, the flag set, and the
    block CRC and combined CRC rewritten for the unflipped data. The tool must then decompress it without complaint.

manifest.tsv records each vector's output size and SHA-256.
"""
import base64
import hashlib
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
TOOL = sys.argv[1] if len(sys.argv) > 1 else "bzip2"
OUT = os.path.join(HERE, "vectors")
MASK = (1 << 64) - 1
ROOT = os.path.dirname(os.path.dirname(HERE))


class SplitMix:
    def __init__(self, seed):
        self.s = seed & MASK

    def next(self):
        self.s = (self.s + 0x9E3779B97F4A7C15) & MASK
        z = self.s
        z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & MASK
        z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & MASK
        return z ^ (z >> 31)


WORDS = ("the of and to in a is that for it as was with be by on not he this are or his from at which but have an had they "
         "you were their one all we can her has there been if more when will would who so no archive block sorting table "
         "symbol run length transform").split()


def text(seed, size):
    r = SplitMix(seed)
    out = bytearray()
    while len(out) < size:
        out += WORDS[r.next() % len(WORDS)].encode()
        out += b"\n" if r.next() % 13 == 0 else b" "
    return bytes(out[:size])


def random_bytes(seed, size):
    r = SplitMix(seed)
    out = bytearray()
    while len(out) < size:
        out += r.next().to_bytes(8, "little")
    return bytes(out[:size])


def runs(seed, size):
    # Runs of one byte whose lengths sweep through 1..300, so runs of 3, 4, 5, 255, 256, 259 ... all occur.
    r = SplitMix(seed)
    out = bytearray()
    n = 1
    while len(out) < size:
        out += bytes([r.next() % 5 + 97]) * n
        n = n % 300 + 1
    return bytes(out[:size])


def zero_runs(size):
    # After the block sort a single byte repeated many times becomes one long run of the front symbol (RUNA/RUNB digits).
    return b"a" * size


def all_bytes():
    return bytes(range(256)) * 3


def bzip2(data, level):
    return subprocess.run([TOOL, f"-{level}", "-c"], input=data, capture_output=True, check=True).stdout


def tool_version():
    return subprocess.run([TOOL, "--help"], capture_output=True, text=True).stderr.splitlines()[0].strip()


def read_gaps():
    src = open(os.path.join(ROOT, "src/OneCompress/BZip2/BZip2RandomizationTable.g.cs")).read()
    return [int(v) for v in re.findall(r"\b\d+\b", src.split("GapData")[1].split("];")[0])]


def flips(length):
    gaps = read_gaps()
    assert len(gaps) == 512
    at = gaps[0] - 2
    i = 0
    out = []
    while at < length:
        out.append(at)
        i += 1
        at += gaps[i % 512]
    return out


# bzip2's block CRC: polynomial 0x04C11DB7, most significant bit first.
def crc32_msb(data):
    table = []
    for i in range(256):
        c = i << 24
        for _ in range(8):
            c = ((c << 1) ^ 0x04C11DB7) & 0xFFFFFFFF if c & 0x80000000 else (c << 1) & 0xFFFFFFFF
        table.append(c)
    crc = 0xFFFFFFFF
    for b in data:
        crc = ((crc << 8) & 0xFFFFFFFF) ^ table[(crc >> 24) ^ b]
    return crc ^ 0xFFFFFFFF


def set_bits(buf, bit_offset, nbits, value):
    for i in range(nbits):
        bit = (value >> (nbits - 1 - i)) & 1
        pos = bit_offset + i
        if bit:
            buf[pos >> 3] |= 0x80 >> (pos & 7)
        else:
            buf[pos >> 3] &= ~(0x80 >> (pos & 7)) & 0xFF


def randomized_vector(size):
    # No byte repeats and no two neighbours differ only in the lowest bit, so neither the data nor its flipped copy has runs.
    r = SplitMix(77)
    data = bytearray()
    prev = -1
    while len(data) < size:
        b = r.next() & 0xFF
        if prev >= 0 and (b >> 1) == (prev >> 1):
            continue
        data.append(b)
        prev = b
    data = bytes(data)
    flipped = bytearray(data)
    for p in flips(len(data)):
        flipped[p] ^= 1
    packed = bytearray(bzip2(bytes(flipped), 9))
    # "BZh9", block signature (6 bytes), block CRC (4 bytes), then the randomised flag.
    assert packed[4:10] == bytes.fromhex("314159265359")
    set_bits(packed, 14 * 8, 1, 1)
    crc = crc32_msb(data)
    set_bits(packed, 10 * 8, 32, crc)
    # One block: the end of stream marker is the last 80 bits, padded to a byte; find it by its signature.
    total_bits = len(packed) * 8
    for pad in range(8):
        end = total_bits - pad - 80
        probe = int.from_bytes(packed, "big") >> (total_bits - end - 48) & ((1 << 48) - 1)
        if probe == 0x177245385090:
            set_bits(packed, end + 48, 32, crc)  # combined CRC of a single block equals its block CRC
            break
    else:
        raise SystemExit("end of stream marker not found")
    return data, bytes(packed)


def main():
    os.makedirs(OUT, exist_ok=True)
    version = tool_version()
    rows = []

    def add(name, original, packed, made_by):
        with open(os.path.join(OUT, name + ".bz2"), "wb") as f:
            f.write(packed)
        rows.append((name, len(original), hashlib.sha256(original).hexdigest(), len(packed), made_by))

    def tool(name, data, level):
        add(name, data, bzip2(data, level), f"bzip2 -{level}")

    tool("empty", b"", 9)
    tool("onebyte", b"x", 9)
    tool("tworuns", b"aaaa", 9)
    for level in (1, 2, 5, 9):
        tool(f"text20k-l{level}", text(1, 20000), level)
    tool("text300k-l1", text(2, 300000), 1)          # three blocks
    tool("random50k-l9", random_bytes(3, 50000), 9)
    tool("runs100k-l9", runs(4, 100000), 9)
    tool("zeros1m-l9", zero_runs(1_000_000), 9)      # one RLE1-compressed run of 1 MB: long RUNA/RUNB digit strings
    tool("zeros4-l9", zero_runs(4), 9)
    tool("zeros5-l9", zero_runs(5), 9)
    tool("zeros259-l9", zero_runs(259), 9)
    tool("zeros260-l9", zero_runs(260), 9)
    tool("allbytes-l9", all_bytes(), 9)
    tool("level1-small-l1", b"hello hello hello", 1)

    first = text(5, 3000)
    second = runs(6, 3000)
    add("concat2", first + second, bzip2(first, 9) + bzip2(second, 3), "bzip2 -9 and -3, joined")
    third = b"x"
    add("concat3-empty-middle", first + third, bzip2(first, 9) + bzip2(b"", 9) + bzip2(third, 1), "bzip2, joined, with an empty stream in the middle")

    data, packed = randomized_vector(20000)
    check = subprocess.run([TOOL, "-dc"], input=packed, capture_output=True)
    assert check.returncode == 0 and check.stdout == data, "the tool rejects the randomised vector"
    add("randomized20k", data, packed, "bzip2 -9 of the flipped data, flag set and CRCs rewritten")

    matrix = ["empty", "onebyte", "tworuns", "text20k-l1", "random50k-l9", "runs100k-l9", "zeros1m-l9", "zeros259-l9", "allbytes-l9",
              "concat2", "concat3-empty-middle", "randomized20k"]
    with open(os.path.join(ROOT, "tools", "hashmatrix", "Host", "Entries", "BZip2Vectors.g.cs"), "w", newline="\n") as f:
        f.write("// <auto-generated> by testdata/bzip2/generate.py from streams written by the bzip2 tool.\n")
        f.write("// Do not edit; run the generator again.\n#nullable enable\nnamespace RecursiveAI.HashMatrix;\n\ninternal static class BZip2Vectors\n{\n")
        f.write("    public static readonly (string Name, string Data)[] Streams =\n    [\n")
        for n in matrix:
            with open(os.path.join(OUT, n + ".bz2"), "rb") as v:
                f.write('        ("%s", "%s"),\n' % (n, base64.b64encode(v.read()).decode()))
        f.write("    ];\n}\n")

    with open(os.path.join(HERE, "manifest.tsv"), "w", newline="\n") as m:
        m.write(f"# {version}\n")
        m.write("# name\tsize\tsha256\tcompressed\tmade by\n")
        for name, size, digest, csize, made in rows:
            m.write(f"{name}\t{size}\t{digest}\t{csize}\t{made}\n")


main()
