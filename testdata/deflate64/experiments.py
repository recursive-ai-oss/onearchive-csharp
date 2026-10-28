#!/usr/bin/env python3
"""Deflate64 derivation experiments (docs/deflate64-derivation.md). Needs `7z` on PATH (tested with 7-Zip 26.00).
Writes its findings to stdout; the committed copy is experiments-output.txt. Deterministic: seeded inputs only."""
import os, random, struct, subprocess, sys, tempfile, zlib
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from d64lab import *

results = []
def report(tag, text):
    print('%-6s %s' % (tag, text)); results.append((tag, text))

def rnd(n, seed): r = random.Random(seed); return bytes(r.randrange(256) for _ in range(n))

def zip_of(raw, orig): return make_zip('x.bin', 9, raw, orig)

def run(tag, desc, build, expect_out):
    """build() -> (raw stream, expected output). Runs 7z and compares."""
    w = BitWriter(); orig = build(w); raw = w.bytes()
    verdict, out = seven_test(zip_of(raw, orig), orig)
    ok = verdict == 'accepted' and out == orig
    report(tag, '%-62s -> %s%s' % (desc, verdict, '' if verdict != 'accepted' else (' (output identical)' if ok else ' (output DIFFERS, got %d bytes want %d)' % (len(out), len(orig)))))
    return verdict, ok

# ---------------------------------------------------------------------------------------------------
print('== E1 7-Zip -tzip -mm=Deflate64 output parses under the hypothesised parameters (and not under plain Deflate)')
def corpus():
    r = random.Random(7)
    words = [bytes(r.randrange(97, 123) for _ in range(r.randrange(2, 9))) for _ in range(300)]
    text = b' '.join(r.choice(words) for _ in range(40000))[:200000]
    far = rnd(50000, 1); far = far + rnd(3000, 2) + far[:20000] + rnd(100, 3) + far[:40000]   # matches 50000+ bytes back
    runs = b'a' * 100000 + rnd(10, 4) + b'xy' * 70000 + b'\0' * 70000 + rnd(1000, 5)
    return {'text200k': text, 'farmatches': far, 'longruns': runs, 'random20k': rnd(20000, 6)}
files = corpus()
for name, data in files.items():
    for mx in (1, 3, 5, 7, 9):
        with tempfile.TemporaryDirectory() as d:
            src = os.path.join(d, name); open(src, 'wb').write(data)
            z = os.path.join(d, 'o.zip')
            subprocess.run(['7z', 'a', '-tzip', '-mm=Deflate64', '-mx=%d' % mx, z, src], capture_output=True, check=True)
            zb = open(z, 'rb').read()
        sig, ver, flags, meth, _, _, crc, csz, usz, nl, el = struct.unpack('<IHHHHHIIIHH', zb[:30])
        raw = zb[30+nl+el:30+nl+el+csz]
        st = {}
        try: ok64 = inflate(raw, DEFLATE64, st) == data
        except Exception as e: ok64 = 'ERR ' + str(e)
        try: ok32 = inflate(raw, DEFLATE, {}) == data
        except Exception as e: ok32 = 'fails (%s)' % str(e)[:40]
        report('E1', '%-11s -mx%d method=%d ver=%d flags=0x%04x csize=%d | d64 params: %s maxlen=%s maxdist=%s len285=%s dist30/31=%s hdist=%s | deflate params: %s' % (
            name, mx, meth, ver, flags, csz, ok64, st.get('maxlen'), st.get('maxdist'), st.get('len285', 0), st.get('dist3x', 0), sorted(st.get('hdist', [])), ok32))

# ---------------------------------------------------------------------------------------------------
print('== E2 distance codes 30 and 31: crafted streams (stored block of random bytes, then one fixed-Huffman match)')
def dist_case(history, dist, symbol, extra_bits, extra, length=10):
    """Match of `length` at distance `dist` written as the given symbol and extra-bit field, then a literal 'Z' so that a wrong
    extra-bit count shows up as damage to what follows. The expected output assumes the true distance `dist`."""
    def b(w):
        h = rnd(history, 11)
        stored_block(w, h[:65535]); rest = h[65535:]
        out = bytearray(h[:65535])
        fixed_block_start(w, True)
        for x in rest: fixed_literal(w, x); out.append(x)
        fixed_match(w, 257 + 7, 0, 0, symbol, extra_bits, extra)       # symbol 264 = length 10
        for _ in range(length): out.append(out[-dist] if len(out) >= dist else 0)
        fixed_literal(w, 90); out.append(90)
        fixed_eob(w); return bytes(out)
    return b
for dist, sym, base in [(32769, 30, 32769), (40000, 30, 32769), (49152, 30, 32769), (49153, 31, 49153), (60000, 31, 49153), (65535, 31, 49153), (65536, 31, 49153)]:
    run('E2', 'dist %d = symbol %d + 14 extra bits (%d)' % (dist, sym, dist - base), dist_case(max(dist, 1000), dist, sym, 14, dist - base), True)
for eb in (13, 15):
    run('E2c', 'control: dist 42769 = symbol 30 with %d extra bits' % eb, dist_case(42769, 42769, 30, eb, (42769 - 32769) & ((1 << eb) - 1)), False)
run('E2c', 'control: dist 40000 = symbol 30 but only 30000 bytes of history', dist_case(30000, 40000, 30, 14, 40000 - 32769), False)
run('E2d', 'dist 32768 via symbol 29 (plain Deflate code) still works', dist_case(40000, 32768, 29, 13, 32768 - 24577), True)

print('== E3 length code 285 and the other length codes')
def len_case(length, symbol, extra_bits, extra):
    def b(w):
        w2 = bytearray()
        fixed_block_start(w, True)
        fixed_literal(w, 97); out = bytearray(b'a')
        fixed_match(w, symbol, extra_bits, extra, 0, 0, 0)           # distance symbol 0 = distance 1
        out += b'a' * length
        fixed_eob(w); return bytes(out)
    return b
for L in (258, 259, 1000, 40000, 65538):
    run('E3', 'length %d = symbol 285 + 16 extra bits (%d)' % (L, L - 3), len_case(L, 285, 16, L - 3), True)
run('E3c', 'control: length 258 = symbol 285 with no extra bits', len_case(258, 285, 0, 0), False)
run('E3d', 'length 257 = symbol 284 + 5 extra bits (30)', len_case(257, 284, 5, 30), True)
run('E3d', 'length 258 = symbol 284 + 5 extra bits (31)', len_case(258, 284, 5, 31), True)
run('E3d', 'length 227 = symbol 284 + 5 extra bits (0)', len_case(227, 284, 5, 0), True)

print('== E4 window size')
def window_case(history, dist):
    def b(w):
        h = rnd(history, 12)
        stored_block(w, h[:65535]); rest = h[65535:]
        fixed_block_start(w, True); out = bytearray(h[:65535])
        for x in rest: fixed_literal(w, x); out.append(x)
        sym, base = (31, 49153) if dist >= 49153 else (30, 32769)
        fixed_match(w, 264, 0, 0, sym, 14, dist - base)
        for _ in range(10): out.append(out[-dist] if len(out) >= dist else 0)
        fixed_literal(w, 90); out.append(90)
        fixed_eob(w); return bytes(out)
    return b
run('E4', 'distance 65536 with exactly 65536 bytes of history', window_case(65536, 65536), True)
run('E4c', 'control: distance 65536 with 65535 bytes of history', window_case(65535, 65536), False)

print('== E5 dynamic blocks: HLIT / HDIST limits and use of distance symbols 30 and 31')
def dynamic_block(w, final, lit_lengths, dist_lengths, hlit, hdist, body):
    """lengths lists cover the declared HLIT / HDIST; code-length alphabet coded with all 19 symbols length 5 (complete-ish via 32 codes unused)."""
    # code-length code: use symbols 0..15 only with length 4 each (16 symbols, complete)
    w.bits(1 if final else 0, 1); w.bits(2, 2)
    w.bits(hlit - 257, 5); w.bits(hdist - 1, 5); w.bits(19 - 4, 4)
    cl = [0]*19
    for s in range(16): cl[s] = 4
    for i in range(19): w.bits(cl[CL_ORDER[i]], 3)
    for l in lit_lengths + dist_lengths: w.code(l, 4)        # canonical 4-bit codes: symbol s has code s
    litcodes = canon_codes(lit_lengths); distcodes = canon_codes(dist_lengths)
    body(w, litcodes, distcodes)
def canon_codes(lengths):
    maxl = max(lengths); count = [0]*(maxl+1)
    for l in lengths:
        if l: count[l] += 1
    code = 0; nxt = [0]*(maxl+2)
    for l in range(1, maxl+1): code = (code + count[l-1]) << 1 if l > 1 else 0; nxt[l] = code
    codes = {}
    for s, l in enumerate(lengths):
        if l: codes[s] = (nxt[l], l); nxt[l] += 1
    return codes
def put(w, codes, s): w.code(*codes[s])
def dyn_case(hlit, hdist, history=0, use_dist=None, litlen_tail=0, dist_tail_lengths=None):
    # literals 97,98 and symbols 256, 257 at length 2: a complete code; distance: two 1-bit codes at symbols (0, use_dist or 1)
    def b(w):
        out = bytearray()
        if history:
            h = rnd(history, 13); stored_block(w, h); out += h
        lit = [0]*hlit; lit[97] = lit[98] = lit[256] = lit[257] = 2
        if litlen_tail: lit[hlit-1] = 0
        d2 = use_dist if use_dist is not None else 1
        dist = [0]*hdist; dist[0] = 1; dist[d2] = 1
        def body(w, lc, dc):
            put(w, lc, 97); out.append(97)
            put(w, lc, 257)                                       # length 3
            put(w, dc, 0); out.extend(out[-1:] * 3)
            if use_dist is not None and history:
                dd = [32769, 49153][use_dist - 30] + 5
                put(w, lc, 257); put(w, dc, use_dist); w.bits(5, 14)
                for _ in range(3): out.append(out[-dd])
            put(w, lc, 256)
        dynamic_block(w, True, lit, dist, hlit, hdist, body)
        return bytes(out)
    return b
run('E5', 'dynamic HLIT=258 HDIST=2 (plain Deflate-sized, sanity)', dyn_case(258, 2), True)
run('E5', 'dynamic HLIT=258 HDIST=30', dyn_case(258, 30), True)
run('E5', 'dynamic HLIT=258 HDIST=31', dyn_case(258, 31), True)
run('E5', 'dynamic HLIT=258 HDIST=32', dyn_case(258, 32), True)
run('E5', 'dynamic HLIT=286', dyn_case(286, 2), True)
run('E5', 'dynamic HLIT=287', dyn_case(287, 2), True)
run('E5', 'dynamic HLIT=288', dyn_case(288, 2), True)
run('E5', 'dynamic uses distance symbol 30 (HDIST=31, 40000 bytes history)', dyn_case(258, 31, 40000, 30), True)
run('E5', 'dynamic uses distance symbol 31 (HDIST=32, 60000 bytes history)', dyn_case(258, 32, 60000, 31), True)

print('== E6 symbols that stay invalid')
def bad_lit(sym):
    def b(w):
        fixed_block_start(w, True); fixed_literal(w, 97); fixed_lit(w, sym); fixed_eob(w); return b'a'
    return b
run('E6', 'fixed block uses literal/length symbol 286', bad_lit(286), False)
run('E6', 'fixed block uses literal/length symbol 287', bad_lit(287), False)
def block3(w):
    w.bits(1, 1); w.bits(3, 2); return b''
run('E6', 'block type 3', block3, False)
def empty_fixed(w):
    fixed_block_start(w, True); fixed_eob(w); return b''
run('E6', 'single empty fixed block (03 00)', empty_fixed, True)
def fixed_dist_sym(dsym):
    def b(w):
        fixed_block_start(w, True); fixed_literal(w, 97)
        fixed_match(w, 257, 0, 0, dsym, 0, 0); fixed_eob(w); return b'a'
    return b
run('E6', 'fixed block: distance symbol 30 with 14 extra bits=0 but only 1 byte of history', lambda w: (fixed_block_start(w, True), fixed_literal(w, 97), fixed_match(w, 257, 0, 0, 30, 14, 0), fixed_eob(w), b'a')[-1], False)

print('== E7 what 7-Zip writes around the stream')
with tempfile.TemporaryDirectory() as d:
    src = os.path.join(d, 'empty'); open(src, 'wb').close()
    z = os.path.join(d, 'o.zip')
    subprocess.run(['7z', 'a', '-tzip', '-mm=Deflate64', z, src], capture_output=True, check=True)
    zb = open(z, 'rb').read()
    sig, ver, flags, meth, _, _, crc, csz, usz, nl, el = struct.unpack('<IHHHHHIIIHH', zb[:30])
    report('E7', 'empty file with -mm=Deflate64: method=%d ver=%d flags=0x%04x csize=%d usize=%d' % (meth, ver, flags, csz, usz))
    big = os.path.join(d, 'big'); open(big, 'wb').write(files['text200k'])
    for flagsrc in ('-mx=1', '-mx=9'):
        z2 = os.path.join(d, 'o2.zip'); 
        if os.path.exists(z2): os.remove(z2)
        subprocess.run(['7z', 'a', '-tzip', '-mm=Deflate64', flagsrc, z2, big], capture_output=True, check=True)
        zb = open(z2, 'rb').read()
        sig, ver, flags, meth, _, _, crc, csz, usz, nl, el = struct.unpack('<IHHHHHIIIHH', zb[:30])
        cen = zb.index(b'PK\x01\x02'); cv = struct.unpack('<HHHH', zb[cen+4:cen+12])
        report('E7', '%s local: method=%d version-needed=%d flags=0x%04x extra-len=%d | central: made-by=%d version-needed=%d flags=0x%04x method=%d' % (flagsrc, meth, ver, flags, el, cv[0], cv[1], cv[2], cv[3]))

print('== E5b symbols 286 and 287 given code lengths in a dynamic block but never used')
def assigned_286(hlit):
    def b(w):
        lit = [0]*hlit; lit[97] = lit[98] = lit[256] = 2; lit[257] = 3; lit[hlit-1] = 3
        dist = [1, 1]
        out = bytearray()
        def body(w, lc, dc):
            put(w, lc, 97); out.append(97); put(w, lc, 257); put(w, dc, 0); out.extend(b'aaa'); put(w, lc, 256)
        dynamic_block(w, True, lit, dist, hlit, 2, body); return bytes(out)
    return b
run('E5b', 'dynamic HLIT=287, symbol 286 has a code length, unused', assigned_286(287), True)
run('E5b', 'dynamic HLIT=288, symbol 287 has a code length, unused', assigned_286(288), True)
