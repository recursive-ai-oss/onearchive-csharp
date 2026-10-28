#!/usr/bin/env python3
"""Regenerates the LZMA-family decoder test vectors in this directory (xz, .lzma and lzip files).

Usage: python3 generate.py [--xz PATH] [--lzip PATH] [--7z PATH]

Every vector is written by a reference tool run as a black box; the tool versions are recorded in manifest.tsv. The inputs come
from a fixed pseudo-random generator, so running this again with the same tools writes identical files. Hand-assembled vectors
(stream padding, joined streams and members) are built from the tools' output by concatenation.

manifest.tsv records the file name, the decompressed size, its SHA-256, the compressed size and how the file was made.
"""
import argparse
import hashlib
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
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


WORDS = ("the of and to in a is that for it as was with be by on not he this are or his from at which but have an had they "
         "you were their one all we can her has there been if more when will would who so no archive range coder match "
         "literal distance length dictionary probability state").split()


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


def structured(seed, size):
    # Records with a counter, a small enum and a float-ish field: matches at fixed distances, which delta and lp/pb like.
    r = SplitMix(seed)
    out = bytearray()
    i = 0
    while len(out) < size:
        out += i.to_bytes(4, "little") + bytes([r.next() % 7, 0, 0, 0]) + (1000 + (r.next() % 50)).to_bytes(4, "little")
        i += 1
    return bytes(out[:size])


def code_like(arch, seed, size):
    """Pseudo machine code: random bytes with the instruction patterns the branch filters convert."""
    r = SplitMix(seed)
    out = bytearray()
    while len(out) < size:
        v = r.next()
        k = v % 5
        rel = (v >> 8) & 0x3FFFF
        if arch == "x86":
            if k < 3:
                out += bytes([0xE8 if k & 1 else 0xE9]) + (rel if (v >> 40) & 1 else (-rel) & 0xFFFFFFFF).to_bytes(4, "little")[:4]
                out += bytes([(v >> 48) & 0xFF]) if (v >> 56) & 1 else b""
            else:
                out += (v >> 16).to_bytes(6, "little")[: 1 + (v >> 60) % 6]
        elif arch == "arm":
            out += (rel & 0xFFFFFF).to_bytes(3, "little") + (b"\xEB" if k < 3 else bytes([(v >> 56) & 0xFF]))
        elif arch == "arm64":
            if k < 2:
                out += (0x94000000 | (rel & 0x3FFFFFF)).to_bytes(4, "little")
            elif k == 2:
                out += (0x90000000 | ((v >> 8) & 3) << 29 | ((v >> 12) & 0x7FFFF) << 5 | (v & 31)).to_bytes(4, "little")
            else:
                out += (v >> 8 & 0xFFFFFFFF).to_bytes(4, "little")
        elif arch == "thumb":
            if k < 3:
                out += bytes([rel & 0xFF, 0xF0 | ((rel >> 8) & 7), (rel >> 11) & 0xFF, 0xF8 | ((rel >> 19) & 7)])
            else:
                out += (v >> 8 & 0xFFFF).to_bytes(2, "little")
        elif arch == "ppc":
            if k < 3:
                out += (0x48000001 | ((rel & 0xFFFFFF) << 2)).to_bytes(4, "big")
            else:
                out += (v >> 8 & 0xFFFFFFFF).to_bytes(4, "big")
        elif arch == "sparc":
            if k < 3:
                out += (0x40000000 | (rel & 0x3FFFFF)).to_bytes(4, "big")
            elif k == 3:
                out += (0x7FC00000 | (rel & 0x3FFFFF)).to_bytes(4, "big")
            else:
                out += (v >> 8 & 0xFFFFFFFF).to_bytes(4, "big")
        elif arch == "ia64":
            out += random_bytes(v, 16)
        elif arch == "riscv":
            if k == 0:
                out += (0x000000EF | ((rel & 0xFFFFF) << 12) | 0x80).to_bytes(4, "little")
            elif k == 1:
                out += (0x00000017 | ((v >> 8) & 0xFFFFF) << 12 | ((v >> 32) & 0x1F) << 7).to_bytes(4, "little")
                out += (0x00000067 | ((v >> 40) & 0xFFF) << 20 | ((v >> 32) & 0x1F) << 15).to_bytes(4, "little")
            elif k == 2:
                out += (0x00000017 | ((v >> 8) & 0xFFFFF) << 12 | 0x100).to_bytes(4, "little")
                out += (0x00000003 | ((v >> 40) & 0xFFF) << 20 | 0x8000).to_bytes(4, "little")
            else:
                out += (v >> 8 & 0xFFFFFFFF).to_bytes(4, "little")
    return bytes(out[:size])


def riscv_special(seed, size):
    """RISC-V words that exercise every branch of the filter: JAL with several registers, AUIPC pairs, markers and their escapes."""
    r = SplitMix(seed)
    out = bytearray()
    while len(out) < size:
        v = r.next()
        k = v % 9
        rd = [1, 5, 3, 2, 0, 31][(v >> 8) % 6]
        u = (v >> 16) & 0xFFFFF
        w2 = (v >> 32) & 0xFFFFFFFF
        if k == 0:
            out += (0x6F | (rd << 7) | (u << 12)).to_bytes(4, "little")
        elif k == 1:
            out += (0x17 | (rd << 7) | (u << 12)).to_bytes(4, "little") + ((w2 & ~0xF8000) | (rd << 15) | 3).to_bytes(4, "little")
        elif k == 2:
            out += (0x17 | (2 << 7) | ((u | 0x3) & 0xFFFFF) << 12 & 0xFFFFFFFF).to_bytes(4, "little") + w2.to_bytes(4, "little")
        elif k == 3:
            out += (0x17 | (2 << 7) | (((u & ~0x7FFF) | 0x8003) & 0xFFFFF) << 12).to_bytes(4, "little") + w2.to_bytes(4, "little")
        elif k == 4:
            out += (0x17 | (rd << 7) | (u << 12)).to_bytes(4, "little") + w2.to_bytes(4, "little")
        elif k == 5:
            out += (w2 & 0xFFFF).to_bytes(2, "little")
        else:
            out += w2.to_bytes(4, "little")
    return bytes(out[:size])


def inputs():
    big = text(1, 150000)
    return {
        "empty": b"",
        "onebyte": b"x",
        "text20k": text(2, 20000),
        "text150k": big,
        "random20k": random_bytes(3, 20000),
        "zeros300k": bytes(300000),
        "structured60k": structured(4, 60000),
        "mixed": text(5, 30000) + random_bytes(6, 3000) + bytes(5000) + structured(7, 20000) + text(8, 30000),
    }


def run(cmd, data):
    return subprocess.run(cmd, input=data, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True).stdout


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--xz", default="xz")
    ap.add_argument("--lzip", default="lzip")
    ap.add_argument("--7z", dest="sevenz", default="7z")
    a = ap.parse_args()
    os.makedirs(OUT, exist_ok=True)
    for f in os.listdir(OUT):
        os.remove(os.path.join(OUT, f))
    xzv = run([a.xz, "--version"], b"").decode().splitlines()[0]
    lzv = run([a.lzip, "--version"], b"").decode().splitlines()[0]
    szv = subprocess.run([a.sevenz], stdout=subprocess.PIPE, check=False).stdout.decode().splitlines()[1].split(":")[0].strip()
    rows = []
    sources = inputs()

    def add(name, plain, comp, how):
        with open(os.path.join(OUT, name), "wb") as f:
            f.write(comp)
        rows.append((name, len(plain), hashlib.sha256(plain).hexdigest(), len(comp), how))

    x = lambda args, data: run([a.xz, "-c", "-T1"] + args, data)

    # Presets 0..9 and 9e over a few inputs; the check defaults to CRC64.
    for lvl in range(10):
        add(f"text150k-p{lvl}.xz", sources["text150k"], x([f"-{lvl}"], sources["text150k"]), f"xz -{lvl}")
    add("text150k-p9e.xz", sources["text150k"], x(["-9e"], sources["text150k"]), "xz -9e")
    add("mixed-p6.xz", sources["mixed"], x(["-6"], sources["mixed"]), "xz -6")
    add("mixed-p1.xz", sources["mixed"], x(["-1"], sources["mixed"]), "xz -1")
    for k in ("empty", "onebyte", "random20k", "zeros300k", "structured60k", "text20k"):
        add(f"{k}-p6.xz", sources[k], x(["-6"], sources[k]), "xz -6")
    # Checks.
    for chk in ("none", "crc32", "crc64", "sha256"):
        add(f"text20k-check-{chk}.xz", sources["text20k"], x([f"--check={chk}"], sources["text20k"]), f"xz --check={chk}")
        add(f"empty-check-{chk}.xz", b"", x([f"--check={chk}"], b""), f"xz --check={chk}")
    # lc/lp/pb, dictionary size and small dictionaries.
    for name, opts in (("lc0lp4", "lc=0,lp=4"), ("lc4lp0pb0", "lc=4,lp=0,pb=0"), ("lc1lp3pb4", "lc=1,lp=3,pb=4"), ("dict4k", "dict=4KiB"), ("dict100k", "dict=100KiB"), ("nice273", "nice=273,mode=normal"), ("fast", "mode=fast,nice=8")):
        add(f"structured60k-{name}.xz", sources["structured60k"], x([f"--lzma2=preset=3,{opts}"], sources["structured60k"]), f"xz --lzma2=preset=3,{opts}")
    # Multiple blocks (fixed block size) and block lists, with sizes recorded in the headers or not.
    add("text150k-blocks20k.xz", sources["text150k"], x(["-3", "--block-size=20000"], sources["text150k"]), "xz -3 --block-size=20000")
    add("mixed-blocks7000-sha256.xz", sources["mixed"], x(["-1", "--block-size=7000", "--check=sha256"], sources["mixed"]), "xz -1 --block-size=7000 --check=sha256")
    add("text150k-blocklist.xz", sources["text150k"], x(["-2", "--block-list=1000,20000,5000,100000"], sources["text150k"]), "xz -2 --block-list=1000,20000,5000,100000")
    add("text150k-mt-blocks.xz", sources["text150k"], run([a.xz, "-c", "-T4", "-3", "--block-size=30000"], sources["text150k"]), "xz -T4 -3 --block-size=30000")
    # Filters.
    for fname, arch in (("x86", "x86"), ("arm", "arm"), ("armthumb", "thumb"), ("arm64", "arm64"), ("powerpc", "ppc"), ("ia64", "ia64"), ("sparc", "sparc"), ("riscv", "riscv")):
        data = code_like(arch, 11, 40000)
        add(f"code-{fname}.xz", data, x([f"--{fname}", "--lzma2=preset=4"], data), f"xz --{fname} --lzma2=preset=4")
        add(f"code-{fname}-start.xz", data, x([f"--{fname}=start=4096", "--lzma2=preset=4"], data), f"xz --{fname}=start=4096 --lzma2=preset=4")
        add(f"code-{fname}-text.xz", sources["text20k"], x([f"--{fname}", "--lzma2=preset=4"], sources["text20k"]), f"xz --{fname} --lzma2=preset=4")
    for fname, arch in ("x86", "x86"), ("arm", "arm"), ("armthumb", "thumb"), ("arm64", "arm64"), ("powerpc", "ppc"), ("ia64", "ia64"), ("sparc", "sparc"), ("riscv", "riscv"):
        data = random_bytes(21, 12000)
        add(f"random-{fname}.xz", data, x([f"--{fname}", "--lzma2=preset=1"], data), f"xz --{fname} --lzma2=preset=1")
    data = riscv_special(22, 30000)
    add("code-riscv-special.xz", data, x(["--riscv", "--lzma2=preset=3"], data), "xz --riscv --lzma2=preset=3")
    add("code-riscv-special-start.xz", data, x(["--riscv=start=2", "--lzma2=preset=3"], data), "xz --riscv=start=2 --lzma2=preset=3")
    for dist in (1, 2, 4, 7, 16, 256):
        add(f"structured60k-delta{dist}.xz", sources["structured60k"], x([f"--delta=dist={dist}", "--lzma2=preset=4"], sources["structured60k"]), f"xz --delta=dist={dist} --lzma2=preset=4")
    data = code_like("x86", 12, 30000)
    add("code-delta-x86.xz", data, x(["--delta=dist=4", "--x86", "--lzma2=preset=4"], data), "xz --delta=dist=4 --x86 --lzma2=preset=4")
    add("code-arm-delta-sparc.xz", data, x(["--arm", "--delta=dist=2", "--sparc", "--lzma2=preset=2"], data), "xz --arm --delta=dist=2 --sparc --lzma2=preset=2")
    # Concatenated streams, with and without stream padding.
    a1 = x(["-1"], sources["text20k"])
    a2 = x(["-6", "--check=crc32"], sources["structured60k"])
    a3 = x(["-0", "--check=sha256"], sources["onebyte"])
    both = sources["text20k"] + sources["structured60k"] + sources["onebyte"]
    add("concat3.xz", both, a1 + a2 + a3, "three xz streams, joined")
    add("concat3-padding.xz", both, a1 + bytes(4) + a2 + bytes(12) + a3 + bytes(8), "three xz streams with stream padding between and after")
    add("padding-end.xz", sources["text20k"], a1 + bytes(16), "xz stream followed by 16 padding bytes")
    # Made by 7-Zip.
    for name in ("text150k", "mixed", "structured60k"):
        tmp = os.path.join(HERE, "_tmp_in")
        with open(tmp, "wb") as f:
            f.write(sources[name])
        for variant, args in (("", []), ("-x9", ["-mx=9"]), ("-x1", ["-mx=1"]), ("-x5mt1", ["-mx=5", "-mmt=1"])):
            out = os.path.join(HERE, "_tmp_out.xz")
            if os.path.exists(out):
                os.remove(out)
            subprocess.run([a.sevenz, "a", "-txz", "-bd", "-bso0", "-bsp0"] + args + [out, tmp], check=True)
            add(f"{name}-7z{variant}.xz", sources[name], open(out, "rb").read(), f"7-Zip a -txz {' '.join(args)}".strip())
            os.remove(out)
        os.remove(tmp)
    # LZMA alone: written by xz (end marker, unknown size) and with the size known.
    for k in ("empty", "onebyte", "text20k", "text150k", "random20k", "zeros300k", "structured60k"):
        add(f"{k}.lzma", sources[k], run([a.xz, "-c", "--format=lzma", "-6"], sources[k]), "xz --format=lzma -6")
    for lvl in (0, 3, 9):
        add(f"text150k-p{lvl}.lzma", sources["text150k"], run([a.xz, "-c", "--format=lzma", f"-{lvl}"], sources["text150k"]), f"xz --format=lzma -{lvl}")
    add("structured60k-lc0lp4pb0.lzma", sources["structured60k"], run([a.xz, "-c", "--format=lzma", "--lzma1=preset=3,lc=0,lp=4,pb=0"], sources["structured60k"]), "xz --format=lzma --lzma1=preset=3,lc=0,lp=4,pb=0")
    add("structured60k-lc4lp0pb4.lzma", sources["structured60k"], run([a.xz, "-c", "--format=lzma", "--lzma1=preset=3,lc=4,lp=0,pb=4"], sources["structured60k"]), "xz --format=lzma --lzma1=preset=3,lc=4,lp=0,pb=4")
    # lzip: levels 0..9, member sizes, joined members.
    lz = lambda args, data: run([a.lzip, "-c"] + args, data)
    for lvl in range(10):
        add(f"text150k-l{lvl}.lz", sources["text150k"], lz([f"-{lvl}"], sources["text150k"]), f"lzip -{lvl}")
    for k in ("empty", "onebyte", "random20k", "zeros300k", "structured60k", "mixed"):
        add(f"{k}-l6.lz", sources[k], lz(["-6"], sources[k]), "lzip -6")
    add("text150k-members.lz", sources["text150k"], lz(["-3", "-b", "100000"], sources["text150k"]), "lzip -3 --member-size=100000")
    m1 = lz(["-1"], sources["text20k"])
    m2 = lz(["-9"], sources["structured60k"])
    m3 = lz(["-0"], sources["onebyte"])
    add("members3.lz", both, m1 + m2 + m3, "three lzip members, joined")
    add("members3-trailing.lz", both, m1 + m2 + m3 + b"\x00" * 7, "three lzip members and 7 trailing zero bytes (trailing data)")
    add("dict-small.lz", sources["structured60k"], lz(["-s", "4KiB"], sources["structured60k"]), "lzip -s 4KiB")

    with open(os.path.join(HERE, "manifest.tsv"), "w") as f:
        f.write(f"# Reference tools run as black boxes: {xzv}; {lzv}; {szv}.\n")
        f.write("# name\tsize\tsha256\tcompressed\tmade by\n")
        for r in rows:
            f.write("\t".join(str(c) for c in r) + "\n")
    total = sum(r[3] for r in rows)
    print(f"{len(rows)} vectors, {total} compressed bytes")


if __name__ == "__main__":
    sys.exit(main())
