#!/usr/bin/env python3
"""Laboratory for the Deflate64 derivation: a bit writer, hand-built blocks, a zip wrapper that
7-Zip can be asked to test or extract, and a reference decoder whose Deflate64 parameters are
switches. Used by experiments.py; see docs/deflate64-derivation.md."""
import os, struct, subprocess, tempfile, zlib

# RFC 1951 section 3.2.5: length codes 257..284 and distance codes 0..29.
LEN_BASE = [3,4,5,6,7,8,9,10,11,13,15,17,19,23,27,31,35,43,51,59,67,83,99,115,131,163,195,227]
LEN_EXTRA = [0]*8 + [1]*4 + [2]*4 + [3]*4 + [4]*4 + [5]*4
DIST_BASE = [1,2,3,4,5,7,9,13,17,25,33,49,65,97,129,193,257,385,513,769,1025,1537,2049,3073,4097,6145,8193,12289,16385,24577]
DIST_EXTRA = [0,0,0,0] + [i//2 for i in range(2, 30)]
CL_ORDER = [16,17,18,0,8,7,9,6,10,5,11,4,12,3,13,2,14,1,15]


class BitWriter:
    def __init__(self):
        self.out = bytearray(); self.acc = 0; self.n = 0

    def bits(self, value, count):          # LSB first (extra bits, headers)
        self.acc |= (value & ((1 << count) - 1)) << self.n; self.n += count
        while self.n >= 8:
            self.out.append(self.acc & 255); self.acc >>= 8; self.n -= 8

    def code(self, value, count):          # Huffman codes go in MSB first
        for i in range(count - 1, -1, -1):
            self.bits((value >> i) & 1, 1)

    def align(self):
        if self.n:
            self.bits(0, 8 - self.n)

    def bytes(self):
        self.align(); return bytes(self.out)


def fixed_lit(w, sym):                      # RFC 1951 section 3.2.6
    if sym < 144: w.code(0x30 + sym, 8)
    elif sym < 256: w.code(0x190 + sym - 144, 9)
    elif sym < 280: w.code(sym - 256, 7)
    else: w.code(0xC0 + sym - 280, 8)


def stored_block(w, data, final=False):
    w.bits(1 if final else 0, 1); w.bits(0, 2); w.align()
    w.bits(len(data), 16); w.bits(~len(data) & 0xFFFF, 16)
    for b in data: w.bits(b, 8)


def fixed_block_start(w, final=False):
    w.bits(1 if final else 0, 1); w.bits(1, 2)


def fixed_literal(w, b): fixed_lit(w, b)
def fixed_eob(w): fixed_lit(w, 256)


def fixed_match(w, len_sym, len_extra_bits, len_extra, dist_sym, dist_extra_bits, dist_extra):
    """A match given as raw symbols and extra-bit fields, so an experiment can pose any hypothesis."""
    fixed_lit(w, len_sym); w.bits(len_extra, len_extra_bits)
    w.code(dist_sym, 5); w.bits(dist_extra, dist_extra_bits)


def make_zip(name, method, raw_stream, original, flags=0, version=21):
    crc = zlib.crc32(original) & 0xFFFFFFFF
    nm = name.encode()
    local = struct.pack('<IHHHHHIIIHH', 0x04034b50, version, flags, method, 0, 0x21, crc, len(raw_stream), len(original), len(nm), 0) + nm
    central = struct.pack('<IHHHHHHIIIHHHHHII', 0x02014b50, 63, version, flags, method, 0, 0x21, crc, len(raw_stream), len(original), len(nm), 0, 0, 0, 0, 0, 0) + nm
    eocd = struct.pack('<IHHHHIIH', 0x06054b50, 0, 0, 1, 1, len(central), len(local) + len(raw_stream), 0)
    return local + raw_stream + central + eocd


def seven_zip(args, **kw):
    return subprocess.run(['7z'] + args, capture_output=True, **kw)


def seven_test(zip_bytes, original=None):
    """Returns ('accepted' | 'rejected: <reason>', extracted bytes or None)."""
    with tempfile.TemporaryDirectory() as d:
        p = os.path.join(d, 't.zip')
        open(p, 'wb').write(zip_bytes)
        r = seven_zip(['e', '-so', '-y', p])
        txt = r.stderr.decode(errors='replace')
        if r.returncode == 0:
            return 'accepted', r.stdout
        reason = [l for l in txt.splitlines() if l.strip() and 'ERROR' in l.upper() or 'Data' in l or 'Headers' in l]
        return 'rejected (exit %d): %s' % (r.returncode, ' | '.join(l.strip() for l in txt.splitlines() if l.strip())[:160]), r.stdout


# ---- reference decoder with the Deflate64 parameters as switches -------------------------------

class Params:
    def __init__(self, deflate64=True, len285=(3, 16), dist30=(32769, 14), dist31=(49153, 14), window=65536, max_hdist=32):
        self.deflate64 = deflate64; self.len285 = len285; self.dist30 = dist30; self.dist31 = dist31
        self.window = window; self.max_hdist = max_hdist


DEFLATE = Params(deflate64=False, len285=(258, 0), dist30=None, dist31=None, window=32768, max_hdist=30)
DEFLATE64 = Params()


class Stream:
    def __init__(self, data): self.d = data; self.pos = 0; self.acc = 0; self.n = 0
    def bits(self, c):
        while self.n < c:
            if self.pos >= len(self.d): raise EOFError('truncated')
            self.acc |= self.d[self.pos] << self.n; self.pos += 1; self.n += 8
        v = self.acc & ((1 << c) - 1); self.acc >>= c; self.n -= c; return v
    def align(self): self.acc = 0; self.n = 0


def build_canon(lengths):
    maxl = max(lengths) if lengths else 0
    count = [0]*(maxl+2)
    for l in lengths:
        if l: count[l] += 1
    code = 0; nxt = [0]*(maxl+2)
    for l in range(1, maxl+1):
        code = (code + count[l-1]) << 1 if l > 1 else 0
        nxt[l] = code
    table = {}
    for s, l in enumerate(lengths):
        if l:
            table[(l, nxt[l])] = s; nxt[l] += 1
    return table, maxl


def read_sym(st, tab):
    table, maxl = tab; code = 0
    for l in range(1, maxl+1):
        code = (code << 1) | st.bits(1)
        if (l, code) in table: return table[(l, code)]
    raise ValueError('bad code')


def inflate(data, p=DEFLATE64, stats=None):
    st = Stream(data); out = bytearray()
    fixed_l = [8]*144 + [9]*112 + [7]*24 + [8]*8
    while True:
        final = st.bits(1); typ = st.bits(2)
        if typ == 0:
            st.align(); ln = st.bits(16); nl = st.bits(16)
            if ln != (~nl & 0xFFFF): raise ValueError('stored length')
            for _ in range(ln): out.append(st.bits(8))
        elif typ == 3:
            raise ValueError('block type 3')
        else:
            if typ == 1:
                lt = build_canon(fixed_l); dt = build_canon([5]*32)
            else:
                hlit = st.bits(5) + 257; hdist = st.bits(5) + 1; hclen = st.bits(4) + 4
                if stats is not None: stats.setdefault('hlit', set()).add(hlit); stats.setdefault('hdist', set()).add(hdist)
                cl = [0]*19
                for i in range(hclen): cl[CL_ORDER[i]] = st.bits(3)
                ct = build_canon(cl); ls = []
                while len(ls) < hlit + hdist:
                    s = read_sym(st, ct)
                    if s < 16: ls.append(s)
                    elif s == 16: ls += [ls[-1]] * (3 + st.bits(2))
                    elif s == 17: ls += [0] * (3 + st.bits(3))
                    else: ls += [0] * (11 + st.bits(7))
                lt = build_canon(ls[:hlit]); dt = build_canon(ls[hlit:hlit+hdist])
            while True:
                s = read_sym(st, lt)
                if s < 256: out.append(s); continue
                if s == 256: break
                if s > 285: raise ValueError('litlen symbol %d' % s)
                if s == 285: base, eb = p.len285
                else: base, eb = LEN_BASE[s-257], LEN_EXTRA[s-257]
                length = base + st.bits(eb)
                d = read_sym(st, dt)
                if d < 30: dbase, deb = DIST_BASE[d], DIST_EXTRA[d]
                elif d == 30 and p.dist30: dbase, deb = p.dist30
                elif d == 31 and p.dist31: dbase, deb = p.dist31
                else: raise ValueError('distance symbol %d' % d)
                dist = dbase + st.bits(deb)
                if stats is not None:
                    stats['maxlen'] = max(stats.get('maxlen', 0), length); stats['maxdist'] = max(stats.get('maxdist', 0), dist)
                    if s == 285: stats['len285'] = stats.get('len285', 0) + 1
                    if d >= 30: stats['dist3x'] = stats.get('dist3x', 0) + 1
                if dist > len(out) or dist > p.window: raise ValueError('distance %d too far (have %d)' % (dist, len(out)))
                for _ in range(length): out.append(out[-dist])
        if final: break
    return bytes(out)
