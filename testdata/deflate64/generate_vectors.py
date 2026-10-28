#!/usr/bin/env python3
"""Writes the committed Deflate64 test vectors (testdata/deflate64/vectors/*.d64) and manifest.tsv.

Hand-built streams use the parameters pinned in docs/deflate64-derivation.md; streams from 7-Zip come from running `7z a -tzip
-mm=Deflate64` as a black box (the tool version is recorded in the manifest header). Every vector's expected output is given by
its length and SHA-256, computed here, not by any implementation under test. Deterministic: seeded inputs only.
Usage: generate_vectors.py   (run from anywhere; needs 7z on PATH only for the 7-Zip vectors)"""
import hashlib, os, random, struct, subprocess, sys, tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from d64lab import *

here = os.path.dirname(os.path.abspath(__file__))
vdir = os.path.join(here, 'vectors'); os.makedirs(vdir, exist_ok=True)
rows = []

def emit(name, stream, expected, kind, note):
    open(os.path.join(vdir, name + '.d64'), 'wb').write(stream)
    rows.append((name, kind, len(expected) if expected is not None else '', hashlib.sha256(expected).hexdigest() if expected is not None else '', note))

def dist_symbol(d):
    for s in range(31, -1, -1):
        base = DIST_BASE[s] if s < 30 else (32769 if s == 30 else 49153)
        if d >= base: return s, (DIST_EXTRA[s] if s < 30 else 14), d - base

def len_symbol(n):
    if n >= 258: return 285, 16, n - 3
    for s in range(27, -1, -1):
        if n >= LEN_BASE[s]: return 257 + s, LEN_EXTRA[s], n - LEN_BASE[s]

class Builder:
    """A fixed-Huffman block builder that tracks the output it means."""
    def __init__(self, final=True):
        self.w = BitWriter(); self.out = bytearray(); fixed_block_start(self.w, final)
    def lit(self, b): fixed_literal(self.w, b); self.out.append(b)
    def match(self, length, dist):
        ls, leb, le = len_symbol(length); ds, deb, de = dist_symbol(dist)
        fixed_match(self.w, ls, leb, le, ds, deb, de)
        for _ in range(length): self.out.append(self.out[-dist])
    def end(self): fixed_eob(self.w); return self.w.bytes(), bytes(self.out)

rng = random.Random(64)
def rb(n): return bytes(rng.randrange(256) for _ in range(n))

# A 65554-byte history built from 16 random literals and one maximum-length match, so streams stay a few dozen bytes.
def history_builder():
    b = Builder()
    for x in rb(16): b.lit(x)
    b.match(65538, 16)
    return b

b = history_builder()
for d in (32769, 40001, 49152, 49153, 60001, 65535, 65536):
    b.match(10, d)
b.lit(90)
s, o = b.end(); emit('far-distances', s, o, 'ok', 'a 65538-byte match, then 10-byte matches at distances 32769 to 65536 (symbols 30 and 31)')

b = Builder(); b.lit(97)
for n in (258, 259, 1000, 40000, 65538): b.match(n, 1)
b.lit(98)
s, o = b.end(); emit('long-lengths', s, o, 'ok', 'matches of 258, 259, 1000, 40000 and 65538 bytes (symbol 285 with 16 extra bits)')

b = Builder(); b.lit(97); b.lit(98)
fixed_match(b.w, 284, 5, 31, 1, 0, 0)      # length 258 as symbol 284 + 31; distance symbol 1 = distance 2
for _ in range(258): b.out.append(b.out[-2])
b.lit(99)
s, o = b.end(); emit('length-258-via-284', s, o, 'ok', 'length 258 written as symbol 284 plus extra 31 (accepted by 7-Zip)')

# Dynamic block with HDIST = 32 and HLIT = 288, symbols 286/287 and distance symbols 30/31 given lengths.
w = BitWriter(); hist = rb(40000); stored_block(w, hist); out = bytearray(hist)
lit = [0]*288; lit[97] = lit[98] = lit[256] = 2; lit[257] = 3; lit[287] = 3
dist = [0]*32; dist[0] = 1; dist[30] = 1
w.bits(1, 1); w.bits(2, 2); w.bits(288 - 257, 5); w.bits(32 - 1, 5); w.bits(19 - 4, 4)
cl = [0]*19
for sym in range(16): cl[sym] = 4
for i in range(19): w.bits(cl[CL_ORDER[i]], 3)
for l in lit + dist: w.code(l, 4)
def codes(lengths):
    maxl = max(lengths); count = [0]*(maxl+1)
    for l in lengths:
        if l: count[l] += 1
    code = 0; nxt = [0]*(maxl+2)
    for l in range(1, maxl+1): code = (code + count[l-1]) << 1 if l > 1 else 0; nxt[l] = code
    r = {}
    for sym, l in enumerate(lengths):
        if l: r[sym] = (nxt[l], l); nxt[l] += 1
    return r
lc, dc = codes(lit), codes(dist)
w.code(*lc[97]); out.append(97)
w.code(*lc[257]); w.code(*dc[30]); w.bits(5, 14)       # length 3 at distance 32774
for _ in range(3): out.append(out[-32774])
w.code(*lc[256])
emit('dynamic-hlit288-hdist32', w.bytes(), bytes(out), 'ok', 'dynamic block declaring 288 literal/length and 32 distance codes; uses distance symbol 30')

emit('empty-final-block', bytes([3, 0]), b'', 'ok', 'a single empty fixed block')

# Hostile streams: each must be refused with a format error.
b = Builder(); b.lit(1); b.lit(2); fixed_match(b.w, 257, 0, 0, 30, 14, 0)
emit('bad-distance-before-start', b.end()[0], None, 'bad', 'distance 32769 after 2 bytes of output')
b = Builder(); b.lit(1); fixed_lit(b.w, 286)
emit('bad-symbol-286', b.end()[0], None, 'bad', 'literal/length symbol 286 in a fixed block')
bw = BitWriter(); bw.bits(1, 1); bw.bits(3, 2)
emit('bad-block-type-3', bw.bytes(), None, 'bad', 'reserved block type 3')
bw = BitWriter(); stored_block(bw, b'hello', True); bad = bytearray(bw.bytes()); bad[3] ^= 0xFF
emit('bad-stored-nlen', bytes(bad), None, 'bad', 'stored block whose NLEN is not the complement of LEN')
b = history_builder(); full, _ = b.end()
emit('truncated-far-distances', full[:len(full) // 2], None, 'truncated', 'a valid stream cut in half')

# Streams written by 7-Zip.
ver = subprocess.run(['7z'], capture_output=True).stdout.decode().splitlines()[1].strip()
def corpus_text(seed, n):
    r = random.Random(seed)
    words = [bytes(r.randrange(97, 123) for _ in range(r.randrange(2, 9))) for _ in range(300)]
    return b' '.join(r.choice(words) for _ in range(n))
def corpus_far():
    r = random.Random(2); far = bytes(r.randrange(256) for _ in range(50000))
    return far + bytes(r.randrange(256) for _ in range(3000)) + far[:20000] + bytes(r.randrange(256) for _ in range(100)) + far[:40000]
def corpus_runs():
    r = random.Random(5)
    return b'a' * 100000 + bytes(r.randrange(256) for _ in range(10)) + b'xy' * 7000 + b'\0' * 70000
inputs = [('text', corpus_text(11, 20000)[:100000], (1, 9)), ('far', corpus_far(), (5,)), ('runs', corpus_runs(), (3,)), ('tiny', b'abcabcabcabcabcabc hello hello hello', (1, 9))]
for name, data, levels in inputs:
    for mx in levels:
        with tempfile.TemporaryDirectory() as d:
            src = os.path.join(d, name); open(src, 'wb').write(data)
            z = os.path.join(d, 'o.zip')
            subprocess.run(['7z', 'a', '-tzip', '-mm=Deflate64', '-mx=%d' % mx, z, src], capture_output=True, check=True)
            zb = open(z, 'rb').read()
        sig, v, fl, meth, _, _, crc, csz, usz, nl, el = struct.unpack('<IHHHHHIIIHH', zb[:30])
        assert meth == 9 and usz == len(data)
        emit('7z-%s-mx%d' % (name, mx), zb[30+nl+el:30+nl+el+csz], data, 'ok', '%s -mm=Deflate64 -mx=%d' % (ver, mx))

with open(os.path.join(here, 'manifest.tsv'), 'w') as f:
    f.write('# Deflate64 vectors: name, kind (ok = decodes to the length and SHA-256; bad = format error; truncated = format error with IsTruncated), output length, output SHA-256, note\n')
    f.write('# 7-Zip vectors written by: %s\n' % ver)
    for r in rows: f.write('\t'.join(str(x) for x in r) + '\n')
print(len(rows), 'vectors,', sum(os.path.getsize(os.path.join(vdir, x)) for x in os.listdir(vdir)), 'bytes')
