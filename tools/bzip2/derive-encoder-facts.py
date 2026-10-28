#!/usr/bin/env python3
"""Black-box experiments on what the reference bzip2 writes, for the encoder's derivation note
(docs/bzip2-encoder-derivation.md). Each experiment prints "Fn: ..." lines; the pinned facts are
the printed results. Needs `bzip2` on PATH (or pass a path). Inputs are generated here from fixed seeds.

Usage: derive-encoder-facts.py [path-to-bzip2] [experiment ...]   (experiments: e1 e2 e3 e4 e5 e6 e7; default all)
"""
import random
import subprocess
import sys
import zlib
from collections import Counter

sys.path.insert(0, __file__.rsplit("/", 1)[0])
from bzip2_inspect import parse, undo_rle1  # noqa: E402

tool = sys.argv[1] if len(sys.argv) > 1 and "/" in sys.argv[1] or (len(sys.argv) > 1 and sys.argv[1] == "bzip2") else "bzip2"
which = [a for a in sys.argv[1:] if a.startswith("e")] or ["e1", "e2", "e3", "e4", "e5", "e6", "e7"]


def comp(data, level=9):
    return subprocess.run([tool, f"-{level}", "-c"], input=data, capture_output=True, check=True).stdout


def no_runs(n, seed, alphabet=256):
    """random bytes in which no byte repeats twice in a row, so the first stage never fires"""
    rng = random.Random(seed)
    out = bytearray()
    prev = -1
    while len(out) < n:
        b = rng.randrange(alphabet)
        if b != prev:
            out.append(b)
            prev = b
    return bytes(out)


def crc_msb(data):
    """CRC-32 with polynomial 0x04C11DB7, MSB first, init all ones, complemented: written here from the format description"""
    table = []
    for i in range(256):
        c = i << 24
        for _ in range(8):
            c = ((c << 1) ^ 0x04C11DB7) & 0xFFFFFFFF if c & 0x80000000 else (c << 1) & 0xFFFFFFFF
        table.append(c)
    c = 0xFFFFFFFF
    for x in data:
        c = ((c << 8) & 0xFFFFFFFF) ^ table[(c >> 24) ^ x]
    return c ^ 0xFFFFFFFF


if "e1" in which:
    # F1: how many first-stage bytes the reference puts in a block at each level.
    for level in (1, 2, 5, 9):
        data = no_runs(level * 100000 * 2 + 12345, level)
        s = parse(comp(data, level))
        sizes = [len(k["rle1"]) for k in s["blocks"]]
        print(f"F1: level {level}: block sizes {sizes} (level*100000 = {level * 100000}; short by {level * 100000 - sizes[0]})")

if "e2" in which:
    # F2: first-stage run coding. Which bytes does the tool write for a run of n equal bytes between two other bytes?
    for n in (1, 3, 4, 5, 8, 258, 259, 260, 261, 263, 264, 300, 518, 519, 520):
        data = b"x" + b"a" * n + b"y"
        rle = parse(comp(data))["blocks"][0]["rle1"]
        print(f"F2: run of {n}: first-stage bytes {rle.hex()} (undone ok: {undo_rle1(rle) == data})")
    # F3: runs that straddle the block boundary of level 1: where does the tool end the block, and is the run coded as pieces?
    for h in (99960, 99970, 99975, 99977, 99978, 99979, 99980, 99981):
        data = no_runs(h, 7) + b"z" * 1000 + b"q"
        s = parse(comp(data, 1))
        info = [(len(k["rle1"]), len(undo_rle1(k["rle1"])), k["rle1"][-6:].hex()) for k in s["blocks"]]
        ok = b"".join(undo_rle1(k["rle1"]) for k in s["blocks"]) == data
        print(f"F3: level 1, {h} varied bytes then 1000 'z' and 'q': (first-stage length, input bytes, tail) per block {info}, round trip of the first stage {ok}")
    # F3b: block size at the boundary for input without runs
    for pad in (0, 1, 2, 10, 18, 19, 20, 38):
        n = 100000 - pad
        data = no_runs(n, 11) + b"ab"
        s = parse(comp(data, 1))
        print(f"F3b: level 1, {n + 2} input bytes without runs: block sizes {[len(k['rle1']) for k in s['blocks']]}")

if "e3" in which:
    # F4: table count and selector count against the number of coded symbols.
    rng = random.Random(3)
    words = [bytes(rng.choices(b"abcdefghijklmnopqrstuvwxyz", k=rng.randrange(2, 9))) for _ in range(500)]
    for n in (1, 10, 100, 199, 200, 201, 300, 500, 799, 800, 801, 1200, 1700, 1799, 1800, 1801, 2400, 2401, 3000, 5000, 20000, 200000):
        text = b" ".join(rng.choice(words) for _ in range(n // 4 + 2))[:n]
        k = parse(comp(text))["blocks"][0]
        print(f"F4: {n} bytes: {len(k['symbols'])} symbols, {k['table_count']} tables, {k['selector_count']} selectors, used {len(k['used'])}, max code length {max(max(l) for l in k['lengths'])}")

if "e4" in which:
    # F5: code lengths in the tool's tables: min/max over several inputs, and what unused symbols get.
    rng = random.Random(4)
    skew = bytes(min(255, int(rng.expovariate(0.15))) for _ in range(400000))
    for name, data in (("skewed", skew), ("uniform-random", no_runs(300000, 5)), ("two-symbols", bytes(rng.choice(b"ab") for _ in range(50000)))):
        k = parse(comp(data))["blocks"][0]
        allv = [x for l in k["lengths"] for x in l]
        print(f"F5: {name}: alphabet {len(k['used']) + 2}, lengths {min(allv)}..{max(allv)}, tables {k['table_count']}")
        for t, l in enumerate(k["lengths"]):
            kraft = sum(2.0 ** -x for x in l)
            print(f"F5:   table {t}: Kraft sum {kraft:.6f}, use count {k['selectors'].count(t)}")
    # F5b: do symbols that never occur in a table's groups still get a length? (every length printed is 1..20 by parse)
    # F5c: a long text in the worst case for lengths: Fibonacci-like frequencies force a deep tree
    fib = [1, 1]
    while len(fib) < 24:
        fib.append(fib[-1] + fib[-2])
    parts = []
    for i, f in enumerate(fib):
        parts.append(bytes([i + 1]) * (f // 6 + 1))
    # interleave so no 4-runs form: shuffle in 3-byte chunks
    chunks = [p[j:j + 1] for p in parts for j in range(len(p))]
    random.Random(9).shuffle(chunks)
    data = b"".join(chunks)
    k = parse(comp(data))["blocks"][0]
    allv = [x for l in k["lengths"] for x in l]
    print(f"F5c: skewed (Fibonacci) frequencies over {len(fib)} symbols, {len(data)} bytes: max length {max(allv)}, tables {k['table_count']}")

if "e5" in which:
    # F6: origin pointer and the sort. Compare with a plain sort of all rotations (cyclic, no sentinel).
    cases = [b"banana", b"abracadabra", b"mississippi", b"abab", b"abcabcabc", b"hello world", b"a", b"ab", b"ba", b"aaab", b"abaabaabaab"]
    for d in cases:
        rle1 = d  # none of these have a run of 4
        rots = sorted(range(len(d)), key=lambda i: d[i:] + d[:i])
        last = bytes(d[(i - 1) % len(d)] for i in rots)
        orig_rows = [r for r, i in enumerate(rots) if i == 0]
        k = parse(comp(d))["blocks"][0]
        # undo move-to-front gives the last column in k["rle1"] (first-stage bytes are the block, so rle1 here is the BWT output)
        ok = k["last_column"] == last
        print(f"F6: {d!r}: last column matches plain rotation sort: {ok}; tool origin {k['orig_ptr']}, rows holding the original rotation {orig_rows}")
    # With ties (periodic input) rotations are equal; the choice of origin is the writer's.
    for d in (b"abababab", b"abcabcabcabc", b"xyxyxyxyxyxy"):
        rots = sorted(range(len(d)), key=lambda i: (d[i:] + d[:i], i))
        orig_rows = [r for r, i in enumerate(rots) if i == 0]
        k = parse(comp(d))["blocks"][0]
        print(f"F6b: {d!r}: tool origin {k['orig_ptr']}; stable-by-index order puts rotation 0 at row {orig_rows}")

if "e6" in which:
    # F7: checksums and stream framing.
    for name, d in (("empty", b""), ("one byte", b"A"), ("text", b"hello hello hello hello, bzip2"), ("runs", b"a" * 5000 + b"b" * 7)):
        z = comp(d, 7)
        s = parse(z)
        crcs = [k["crc"] for k in s["blocks"]]
        combined = 0
        for c in crcs:
            combined = ((combined << 1) | (combined >> 31)) & 0xFFFFFFFF
            combined ^= c
        print(f"F7: {name}: header {z[:4]!r} blocks {len(crcs)} block crc matches CRC of the input: {[c == crc_msb(d) for c in crcs]}, combined matches: {s['combined_crc'] == combined}, length {len(z)}, end bit {s['end_bit']} (padding {(-s['end_bit']) % 8} bits, pad bits zero: {z[-1] & ((1 << ((-s['end_bit']) % 8)) - 1) == 0})")
    # level digit with small input
    for level in (1, 9):
        print(f"F7b: level {level} on 3 bytes writes header {comp(b'abc', level)[:4]!r}")
    # multiple blocks: the combined CRC
    d = no_runs(250000, 21)
    s = parse(comp(d, 1))
    combined = 0
    pos = 0
    ok = True
    for k in s["blocks"]:
        n = len(undo_rle1(k["rle1"]))
        ok &= k["crc"] == crc_msb(d[pos:pos + n])
        pos += n
        combined = (((combined << 1) | (combined >> 31)) & 0xFFFFFFFF) ^ k["crc"]
    print(f"F7c: 3 blocks: each block CRC is the CRC of its own input bytes: {ok}; combined as rotate-xor: {combined == s['combined_crc']}")

if "e7" in which:
    # F8: zero-run symbols: a run of equal bytes at the end of a block, and the symbol map when the block uses all 256 values.
    d = b"abcabc" + b"z" * 3
    k = parse(comp(d))["blocks"][0]
    print(f"F8: 'abcabczzz' symbols {k['symbols']} used {len(k['used'])}")
    d = bytes(range(256)) * 3
    k = parse(comp(d))["blocks"][0]
    print(f"F8b: all 256 byte values: alphabet {len(k['used']) + 2}, max symbol {max(k['symbols'])}")
    # F9: selector MTF: do the tool's selectors use the unary move-to-front value as described (0 = same as the last table)?
    rng = random.Random(8)
    text = bytes(rng.choice(b"abc ") if (i // 3000) % 2 == 0 else rng.choice(b"WXYZ0123") for i in range(60000))
    k = parse(comp(text))["blocks"][0]
    print(f"F9: selector mtf values (first 24): {k['selector_mtf'][:24]}, selectors {k['selectors'][:24]}")
