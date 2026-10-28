#!/usr/bin/env python3
"""Reads the structure of a bzip2 stream: a small parser written from the public format description
(see docs/bzip2-decoder.md), used by the encoder experiments to look at what the reference tool writes.

parse(data) returns {"level": int, "blocks": [...], "combined_crc": int, "end_bit": int}; each block holds
crc, randomized, orig_ptr, used (the bytes in the block's symbol map), table_count, selector_count, selectors,
lengths (per table), symbols (the coded symbols including the end of block), rle1 (the bytes after undoing the
move-to-front and zero-run stages), and bit offsets of the block start.
"""
import sys


class Bits:
    def __init__(self, data):
        self.data = data
        self.pos = 0  # bit position

    def get(self, n):
        v = 0
        for _ in range(n):
            byte = self.data[self.pos >> 3]
            v = (v << 1) | ((byte >> (7 - (self.pos & 7))) & 1)
            self.pos += 1
        return v


def canonical(lengths):
    """code table {(length, code): symbol} for canonical codes: same-length codes consecutive in symbol order."""
    table = {}
    code = 0
    for ln in range(1, 21):
        for sym, l in enumerate(lengths):
            if l == ln:
                table[(ln, code)] = sym
                code += 1
        code <<= 1
    return table


def parse(data, first_only=False):
    b = Bits(data)
    assert b.get(24) == 0x425A68, "signature"
    level = b.get(8) - 0x30
    blocks = []
    while True:
        magic = b.get(48)
        if magic == 0x177245385090:
            combined = b.get(32)
            return {"level": level, "blocks": blocks, "combined_crc": combined, "end_bit": b.pos}
        assert magic == 0x314159265359, "block magic"
        blk = {"start_bit": b.pos - 48}
        blk["crc"] = b.get(32)
        blk["randomized"] = b.get(1)
        blk["orig_ptr"] = b.get(24)
        groups = b.get(16)
        used = []
        for g in range(16):
            if groups & (0x8000 >> g):
                bits = b.get(16)
                for k in range(16):
                    if bits & (0x8000 >> k):
                        used.append(g * 16 + k)
        blk["used"] = used
        ntab = b.get(3)
        nsel = b.get(15)
        sel_mtf = []
        for _ in range(nsel):
            j = 0
            while b.get(1):
                j += 1
            sel_mtf.append(j)
        order = list(range(ntab))
        sels = []
        for j in sel_mtf:
            t = order.pop(j)
            order.insert(0, t)
            sels.append(t)
        blk["table_count"] = ntab
        blk["selector_count"] = nsel
        blk["selectors"] = sels
        blk["selector_mtf"] = sel_mtf
        alpha = len(used) + 2
        tables = []
        for _ in range(ntab):
            cur = b.get(5)
            lens = []
            for _ in range(alpha):
                while b.get(1):
                    cur += -1 if b.get(1) else 1
                lens.append(cur)
            tables.append(lens)
        blk["lengths"] = tables
        codes = [canonical(l) for l in tables]
        eob = alpha - 1
        symbols = []
        i = 0
        while True:
            t = codes[sels[i // 50]]
            code = 0
            ln = 0
            while True:
                code = (code << 1) | b.get(1)
                ln += 1
                assert ln <= 20, "no code"
                if (ln, code) in t:
                    s = t[(ln, code)]
                    break
            symbols.append(s)
            i += 1
            if s == eob:
                break
        blk["symbols"] = symbols
        # undo zero-run coding and move-to-front
        mtf = list(used)
        out = bytearray()
        run = 0
        weight = 1
        for s in symbols[:-1]:
            if s <= 1:
                run += weight << s
                weight <<= 1
                continue
            if run:
                out += bytes([mtf[0]]) * run
                run = 0
                weight = 1
            v = mtf.pop(s - 1)
            mtf.insert(0, v)
            out.append(v)
        if run:
            out += bytes([mtf[0]]) * run
        blk["last_column"] = bytes(out)
        blk["rle1"] = inverse_bwt(bytes(out), blk["orig_ptr"])
        blk["end_bit"] = b.pos
        blocks.append(blk)
        if first_only:
            return {"level": level, "blocks": blocks}


def inverse_bwt(last, orig):
    """the block's bytes from the last column of the sorted rotations and the origin row (docs/bzip2-decoder.md)"""
    n = len(last)
    count = [0] * 257
    for c in last:
        count[c + 1] += 1
    for i in range(256):
        count[i + 1] += count[i]
    t = [0] * n
    for i, c in enumerate(last):
        t[count[c]] = i
        count[c] += 1
    row = t[orig]
    out = bytearray()
    for _ in range(n):
        out.append(last[row])
        row = t[row]
    return bytes(out)


def undo_rle1(rle1):
    out = bytearray()
    i = 0
    run = 0
    prev = -1
    while i < len(rle1):
        c = rle1[i]
        i += 1
        out.append(c)
        if c == prev:
            run += 1
        else:
            run = 1
            prev = c
        if run == 4:
            if i >= len(rle1):
                raise ValueError("missing count")
            out += bytes([c]) * rle1[i]
            i += 1
            run = 0
            prev = -1
    return bytes(out)


if __name__ == "__main__":
    s = parse(open(sys.argv[1], "rb").read())
    print("level", s["level"], "blocks", len(s["blocks"]))
    for k in s["blocks"]:
        print({x: (v if not isinstance(v, (bytes, list)) else len(v)) for x, v in k.items()})
