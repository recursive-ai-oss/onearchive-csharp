#!/usr/bin/env python3
"""Writes compound file (MS-CFB) test vectors from the format specification alone.

This is a small, independent writer used only to make test input: it lays out a tree of storages and streams as sectors, a
FAT, a DIFAT, a mini FAT and a directory of red-black trees, exactly as [MS-CFB] sections 2.1 to 2.6 describe, and can damage
a file in one named way so that a reader's checks have something to reject.

Usage: gen_cfb.py OUTDIR      (writes the vectors and manifest.json; the output is deterministic)
"""
import gzip
import hashlib
import json
import os
import struct
import sys

END = 0xFFFFFFFE
FREE = 0xFFFFFFFF
FATSECT = 0xFFFFFFFD
DIFSECT = 0xFFFFFFFC
NOSTREAM = 0xFFFFFFFF
SIG = bytes([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1])


def units(name):
    raw = name.encode("utf-16-le", "surrogatepass")
    return list(struct.unpack("<%dH" % (len(raw) // 2), raw))


def up(u):
    c = chr(u)
    if 0xD800 <= u <= 0xDFFF:
        return u
    s = c.upper()
    return ord(s) if len(s) == 1 and ord(s) < 0x10000 else u


def sort_key(name):
    u = units(name)
    return (len(u), [up(x) for x in u])


class Node:
    def __init__(self, name, data=None, children=None, clsid=b"\0" * 16, state=0, ctime=0, mtime=0):
        self.name = name
        self.data = data
        self.children = children
        self.clsid = clsid
        self.state = state
        self.ctime = ctime
        self.mtime = mtime
        self.sid = 0
        self.left = self.right = self.child = NOSTREAM
        self.color = 1
        self.start = 0

    @property
    def is_stream(self):
        return self.data is not None


def S(name, data):
    return Node(name, data=data)


def D(name, children, **kw):
    return Node(name, children=children, **kw)


def build(root, version=3, fragment=False, garbage_high=False, size_override=None):
    """Returns (bytes, layout). `root` is a Node storage."""
    ss = 512 if version == 3 else 4096
    per = ss // 4
    entries = []

    def bst(nodes, depth, maxd, perfect):
        if not nodes:
            return NOSTREAM
        mid = len(nodes) // 2
        n = nodes[mid]
        n.color = 0 if (depth == maxd and not perfect) else 1
        n.left = bst(nodes[:mid], depth + 1, maxd, perfect)
        n.right = bst(nodes[mid + 1:], depth + 1, maxd, perfect)
        return n.sid

    def add(node):
        node.sid = len(entries)
        entries.append(node)
        if node.children is not None:
            kids = sorted(node.children, key=lambda c: sort_key(c.name))
            for k in kids:
                add(k)
            if kids:
                node.child = bst(kids, 1, len(kids).bit_length(), (len(kids) + 1) & len(kids) == 0)

    add(root)
    root.color = 1

    # ---- allocate sectors ----
    streams = [e for e in entries if e.is_stream]
    mini = [e for e in streams if 0 < len(e.data) < 4096]
    big = [e for e in streams if len(e.data) >= 4096]
    ministream = b""
    minifat = []
    for e in mini:
        e.start = len(minifat)
        cnt = (len(e.data) + 63) // 64
        for i in range(cnt):
            minifat.append(len(minifat) + 1 if i < cnt - 1 else END)
        ministream += e.data + b"\0" * (cnt * 64 - len(e.data))
    mini_sectors = (len(ministream) + ss - 1) // ss
    minifat_sectors = (len(minifat) + per - 1) // per
    dir_sectors = (len(entries) * 128 + ss - 1) // ss

    nxt = 0
    layout = {"sector_size": ss}

    def take(n):
        nonlocal nxt
        r = list(range(nxt, nxt + n))
        nxt += n
        return r

    dir_ids = take(dir_sectors)
    minifat_ids = take(minifat_sectors)
    ministream_ids = take(mini_sectors)
    data_ids = {}
    if fragment and len(big) >= 2:
        counts = [(len(e.data) + ss - 1) // ss for e in big]
        ids = [[] for _ in big]
        pool = take(sum(counts))
        pi = 0
        remaining = counts[:]
        while any(remaining):
            for i in range(len(big)):
                if remaining[i]:
                    ids[i].append(pool[pi])
                    pi += 1
                    remaining[i] -= 1
        for i, e in enumerate(big):
            data_ids[e.sid] = ids[i]
    else:
        for e in big:
            data_ids[e.sid] = take((len(e.data) + ss - 1) // ss)
    other = nxt
    nfat = 0
    ndif = 0
    while True:
        total = other + nfat + ndif
        nf = (total + per - 1) // per
        nd = (max(0, nf - 109) + per - 2) // (per - 1)
        if nf == nfat and nd == ndif:
            break
        nfat, ndif = nf, nd
    fat_ids = take(nfat)
    dif_ids = take(ndif)
    fat = [FREE] * (nfat * per)

    def chain(ids):
        for i, s in enumerate(ids):
            fat[s] = ids[i + 1] if i + 1 < len(ids) else END

    chain(dir_ids)
    chain(minifat_ids)
    chain(ministream_ids)
    for sid, ids in data_ids.items():
        chain(ids)
    for s in fat_ids:
        fat[s] = FATSECT
    for s in dif_ids:
        fat[s] = DIFSECT
    for e in big:
        e.start = data_ids[e.sid][0]

    # ---- directory ----
    out = bytearray()
    for e in entries:
        if e is root:
            name = "Root Entry"
            typ = 5
            start = ministream_ids[0] if ministream_ids else END
            size = len(ministream)
        elif e.is_stream:
            name = e.name
            typ = 2
            size = len(e.data)
            start = e.start if size else END
        else:
            name = e.name
            typ = 1
            start = 0
            size = 0
        u = units(name)
        assert len(u) <= 31
        nm = struct.pack("<%dH" % len(u), *u) + b"\0\0"
        rec = nm + b"\0" * (64 - len(nm))
        rec += struct.pack("<HBBIII", len(nm), typ, e.color, e.left, e.right, e.child)
        rec += e.clsid + struct.pack("<I", e.state) + struct.pack("<QQ", e.ctime, e.mtime)
        if size_override and e.name in size_override and e.is_stream:
            size = size_override[e.name]
        hi = 0xDEADBEEF if (garbage_high and version == 3) else 0
        rec += struct.pack("<IQ", start, size | (hi << 32))
        assert len(rec) == 128
        out += rec
    dirbytes = bytes(out)
    free_entry = b"\0" * 68 + struct.pack("<III", NOSTREAM, NOSTREAM, NOSTREAM) + b"\0" * 48
    assert len(free_entry) == 128
    dirbytes += free_entry * (dir_sectors * ss // 128 - len(entries))

    # ---- header, DIFAT ----
    hdr_dif = (fat_ids + [FREE] * 109)[:109]
    hdr = SIG + b"\0" * 16 + struct.pack("<HHHHH", 0x3E, version, 0xFFFE, 9 if version == 3 else 12, 6) + b"\0" * 6
    hdr += struct.pack("<IIIIIIIII", dir_sectors if version == 4 else 0, nfat, dir_ids[0], 0, 4096,
                       minifat_ids[0] if minifat_ids else END, minifat_sectors,
                       dif_ids[0] if dif_ids else END, ndif)
    hdr += struct.pack("<109I", *hdr_dif)
    assert len(hdr) == 512
    img = bytearray(hdr + b"\0" * (ss - 512))

    secs = {}

    def put(sid, data):
        assert len(data) <= ss
        secs[sid] = data + b"\0" * (ss - len(data))

    for i, s in enumerate(dir_ids):
        put(s, dirbytes[i * ss:(i + 1) * ss])
    mf = minifat + [FREE] * (minifat_sectors * per - len(minifat))
    for i, s in enumerate(minifat_ids):
        put(s, struct.pack("<%dI" % per, *mf[i * per:(i + 1) * per]))
    for i, s in enumerate(ministream_ids):
        put(s, ministream[i * ss:(i + 1) * ss])
    for sid, ids in data_ids.items():
        data = next(e.data for e in entries if e.sid == sid)
        for i, s in enumerate(ids):
            put(s, data[i * ss:(i + 1) * ss])
    for i, s in enumerate(fat_ids):
        put(s, struct.pack("<%dI" % per, *fat[i * per:(i + 1) * per]))
    rest = fat_ids[109:]
    for i, s in enumerate(dif_ids):
        part = rest[i * (per - 1):(i + 1) * (per - 1)]
        part = part + [FREE] * (per - 1 - len(part))
        put(s, struct.pack("<%dI" % per, *(part + [dif_ids[i + 1] if i + 1 < len(dif_ids) else END])))
    for s in range(nxt):
        img += secs.get(s, b"\xff" * ss)
    layout.update(dir_ids=dir_ids, fat_ids=fat_ids, dif_ids=dif_ids, minifat_ids=minifat_ids, ministream_ids=ministream_ids,
                  data_ids=data_ids, entries=entries, per=per)
    return bytes(img), layout


# ---- damage helpers (operate on a finished image) ----

def off(layout, sector):
    return (sector + 1) * layout["sector_size"]


def patch32(img, at, value):
    b = bytearray(img)
    b[at:at + 4] = struct.pack("<I", value)
    return bytes(b)


def fat_at(layout, sector):
    per = layout["per"]
    return off(layout, layout["fat_ids"][sector // per]) + (sector % per) * 4


def dir_at(layout, sid):
    n = layout["sector_size"] // 128
    return off(layout, layout["dir_ids"][sid // n]) + (sid % n) * 128


def pattern(n, seed):
    out = bytearray()
    x = seed & 0xFFFFFFFF
    while len(out) < n:
        x = (x * 1664525 + 1013904223) & 0xFFFFFFFF
        out += bytes([97 + (x >> 24) % 26])
    return bytes(out[:n])


def sha(b):
    return hashlib.sha256(b).hexdigest()


def describe(root):
    # Names are written as hex of their UTF-16 code units: some are deliberately not valid Unicode, so they cannot be JSON strings.
    items = []

    def walk(n, path):
        for c in sorted(n.children, key=lambda c: sort_key(c.name)):
            p = path + [c.name.encode("utf-16-le", "surrogatepass").hex()]
            if c.is_stream:
                items.append({"path": p, "kind": "stream", "size": len(c.data), "sha256": sha(c.data)})
            else:
                items.append({"path": p, "kind": "storage", "clsid": c.clsid.hex(), "state": c.state, "mtime": c.mtime})
                walk(c, p)

    walk(root, [])
    return items


def main(outdir):
    os.makedirs(outdir, exist_ok=True)
    manifest = {"good": [], "bad": []}
    clsid = bytes(range(1, 17))
    mtime = 133000000000000000  # a FILETIME in 2022

    def sample():
        return D("root", [
            D("Storage 1", [S("Stream 1", pattern(544, 1)), S("empty", b"")], clsid=clsid, state=7, mtime=mtime),
            S("small", pattern(100, 2)),
            S("big", pattern(9000, 3)),
            D("deep", [D("a", [D("b", [D("c", [S("leaf", pattern(70, 4))])])])]),
        ])

    def good(name, root, version=3, gz=False, **kw):
        img, lay = build(root, version, **kw)
        fn = name + (".cfb.gz" if gz else ".cfb")
        with open(os.path.join(outdir, fn), "wb") as f:
            if gz:
                f.write(gzip.compress(img, 9, mtime=0))
            else:
                f.write(img)
        manifest["good"].append({"file": fn, "version": version, "length": len(img), "entries": describe(root)})
        return img, lay

    def bad(name, img, contains, limit=None):
        fn = name + ".cfb"
        with open(os.path.join(outdir, fn), "wb") as f:
            f.write(img)
        manifest["bad"].append({"file": fn, "contains": contains, "limit": limit})

    # -- good vectors --
    good("v3-basic", sample(), 3)
    good("v4-basic", sample(), 4)
    good("v3-high-size-garbage", sample(), 3, garbage_high=True)
    bounds = [S("s%d" % n, pattern(n, n)) for n in (0, 1, 63, 64, 65, 511, 512, 513, 4095, 4096, 4097, 8192, 20000)]
    good("v3-boundaries", D("root", bounds), 3)
    good("v4-boundaries", D("root", bounds), 4)
    good("v3-fragmented", D("root", [S("one", pattern(30000, 5)), S("two", pattern(20000, 6)), S("three", pattern(5000, 7))]), 3, fragment=True)
    names = ["a b", "tab\there", "ctl\x01\x1f", "slash/inside", "back\\slash", "colon:star*q?", "quo\"te<lt>gt|pipe", "100%", "%0041",
             "dot.", "space ", ".", "..", "\x05SummaryInformation", "caf\u00e9", "CAF\u00c9", "\u65e5\u672c\u8a9e", "a\ud800b", "x" * 31, "Z", "z2", "\U0001F600"[0:0] + "\ud83d\ude00"]
    uniq = {}
    for n in names:
        uniq[tuple(up(u) for u in units(n))] = n
    good("v3-names", D("root", [S(n, pattern(10 + len(n), 9)) for n in uniq.values()]), 3)
    msg = D("root", [
        D("__nameid_version1.0", [S("__substg1.0_00020102", pattern(40, 11)), S("__substg1.0_00030102", pattern(80, 12))]),
        D("__recip_version1.0_#00000000", [S("__properties_version1.0", pattern(200, 13)), S("__substg1.0_3001001F", pattern(60, 14))]),
        D("__attach_version1.0_#00000000", [S("__properties_version1.0", pattern(300, 15)), S("__substg1.0_37010102", pattern(6000, 16)),
                                            D("__substg1.0_3701000D", [S("inner", pattern(5000, 17))])]),
        S("__properties_version1.0", pattern(64, 18)),
        S("__substg1.0_0037001F", pattern(50, 19)),
    ], clsid=bytes.fromhex("0b0d020000000000c000000000000046"))
    good("v3-msg-like", msg, 3)
    many = D("root", [S("e%04d" % i, pattern(i % 90, i)) for i in range(300)])
    good("v3-many-entries", many, 3)
    good("v4-many-entries", many, 4)
    # more than 109 FAT sectors: the DIFAT holds the rest. 14,100 sectors of 512 bytes is just over 7 MB, so the vector is compressed.
    good("v3-difat", D("root", [S("large", (b"cfb difat vector " * 450000)[:7_200_000]), S("note", pattern(300, 22))]), 3, gz=True)
    good("v3-empty", D("root", []), 3)
    good("v4-empty", D("root", []), 4)

    # -- damaged vectors --
    base3 = D("root", [S("a", pattern(9000, 1)), S("b", pattern(9000, 2)), S("m", pattern(200, 3)), D("st", [S("x", b"hi")])])
    img, lay = build(base3, 3)
    ents = {e.name: e for e in lay["entries"]}
    a_first = ents["a"].start
    bad("bad-fat-cycle", patch32(img, fat_at(lay, a_first), a_first), "no other chain uses")
    a_ids = lay["data_ids"][ents["a"].sid]
    bad("bad-fat-cycle-back", patch32(img, fat_at(lay, a_ids[-1]), a_ids[0]), "no other chain uses")
    bad("bad-chain-past-file", patch32(img, fat_at(lay, a_ids[1]), 0x00FFFFF0), "stay inside the file")
    bad("bad-chain-through-free", patch32(img, fat_at(lay, a_ids[1]), FREE), "stay inside the file")
    bad("bad-chain-ends-early", patch32(img, fat_at(lay, a_ids[0]), END), "sector chain of at least")
    b_first = ents["b"].start
    bad("bad-sector-shared", patch32(img, dir_at(lay, ents["b"].sid) + 0x74, a_first), "no other chain uses")
    bad("bad-stream-size-beyond-chain", build(base3, 3, size_override={"a": 90000})[0], "sector chain of at least")
    bad("bad-mini-size-beyond-chain", build(base3, 3, size_override={"m": 4000})[0], "sector chain of at least")
    bad("bad-mini-cycle", patch32(img, off(lay, lay["minifat_ids"][0]), 0), "no other chain uses")
    bad("bad-mini-start-out-of-range", patch32(img, dir_at(lay, ents["m"].sid) + 0x74, 5000), "stay inside the mini stream")
    b = bytearray(img)
    b[56:60] = struct.pack("<I", 4097)
    bad("bad-mini-cutoff", bytes(b), "mini stream cutoff")
    b = bytearray(img)
    b[30:32] = struct.pack("<H", 12)
    bad("bad-sector-shift", bytes(b), "sector shift")
    b = bytearray(img)
    b[26:28] = struct.pack("<H", 5)
    bad("bad-major-version", bytes(b), "major version")
    b = bytearray(img)
    b[0] = 0
    bad("bad-signature", bytes(b), "signature")
    b = bytearray(img)
    b[32:34] = struct.pack("<H", 7)
    bad("bad-mini-shift", bytes(b), "mini sector shift")
    b = bytearray(img)
    b[28:30] = struct.pack("<H", 0xFEFF)
    bad("bad-byte-order", bytes(b), "byte order")
    # directory tree damage
    st = ents["st"]
    bad("bad-dir-cycle", patch32(img, dir_at(lay, st.sid) + 0x4C, st.sid), "reachable once")
    bad("bad-dir-child-root", patch32(img, dir_at(lay, st.sid) + 0x4C, 0), "link to a storage or stream")
    bad("bad-dir-sibling-out-of-range", patch32(img, dir_at(lay, ents["a"].sid) + 0x44, 5000), "directory entry ID below")
    bad("bad-dir-sibling-reserved", patch32(img, dir_at(lay, ents["a"].sid) + 0x48, 0xFFFFFFFA), "directory entry ID below")
    free_sid = len(lay["entries"])  # first unused entry in the sector
    if free_sid < lay["sector_size"] // 128:
        bad("bad-dir-link-unallocated", patch32(img, dir_at(lay, ents["a"].sid) + 0x48, free_sid), "link to a storage or stream")
    bad("bad-dir-shared-subtree", patch32(img, dir_at(lay, ents["a"].sid) + 0x44, ents["b"].sid), "reachable once")
    x = ents["x"]
    bad("bad-stream-has-child", patch32(img, dir_at(lay, ents["a"].sid) + 0x4C, x.sid), "child of a stream")
    b = bytearray(img)
    b[dir_at(lay, ents["a"].sid) + 0x42] = 9
    bad("bad-object-type", bytes(b), "object type")
    b = bytearray(img)
    b[dir_at(lay, ents["a"].sid) + 0x43] = 2
    bad("bad-color", bytes(b), "color flag")
    b = bytearray(img)
    b[dir_at(lay, ents["a"].sid) + 0x40:dir_at(lay, ents["a"].sid) + 0x42] = struct.pack("<H", 3)
    bad("bad-name-length-odd", bytes(b), "even name length")
    b = bytearray(img)
    b[dir_at(lay, ents["a"].sid) + 0x40:dir_at(lay, ents["a"].sid) + 0x42] = struct.pack("<H", 66)
    bad("bad-name-length-long", bytes(b), "even name length")
    b = bytearray(img)
    b[dir_at(lay, 0) + 0x42] = 1
    bad("bad-root-type", bytes(b), "root storage")
    b = bytearray(img)
    b[dir_at(lay, ents["a"].sid) + 0x42] = 5
    bad("bad-second-root", bytes(b), "root storage only as the first")
    # sibling order: swap the stored names of two siblings
    imgs = bytearray(img)
    pa, pb = dir_at(lay, ents["a"].sid), dir_at(lay, ents["m"].sid)
    imgs[pa:pa + 64], imgs[pb:pb + 64] = bytes(img[pb:pb + 64]), bytes(img[pa:pa + 64])
    imgs[pa + 0x40:pa + 0x42], imgs[pb + 0x40:pb + 0x42] = bytes(img[pb + 0x40:pb + 0x42]), bytes(img[pa + 0x40:pa + 0x42])
    bad("bad-sibling-order", bytes(imgs), "order of [MS-CFB] 2.6.4")
    imgs = bytearray(img)
    pa, pb = dir_at(lay, ents["a"].sid), dir_at(lay, ents["b"].sid)
    imgs[pb:pb + 64] = bytes(img[pa:pa + 64])
    bad("bad-sibling-duplicate-name", bytes(imgs), "unique names")
    # FAT / DIFAT header damage
    bad("bad-fat-count-zero", patch32(img, 44, 0), "at least one FAT sector")
    bad("bad-fat-count-huge", patch32(img, 44, 100000), "no more FAT sectors")
    bad("bad-fat-location", patch32(img, 76, 0x0000FFFF), "inside the file")
    bad("bad-dir-start", patch32(img, 48, 0x0000FFFF), "stay inside the file")
    bad("bad-dir-start-free", patch32(img, 48, FREE), "stay inside the file")
    bad("bad-minifat-count", patch32(img, 64, 5), "mini FAT sectors")
    bad("bad-difat-count", patch32(img, 72, 100000), "no more DIFAT sectors")
    bad("bad-difat-missing", patch32(img, 44, 120), "FAT sector")
    # oversized directory: a normal file read with a small MaxDirectoryEntries
    bad("limit-directory", build(many, 3)[0], "MaxDirectoryEntries", limit={"MaxDirectoryEntries": 100})
    # a real DIFAT chain that loops back on itself
    big_img, big_lay = build(D("root", [S("large", (b"cfb difat vector " * 450000)[:7_200_000])]), 3)
    d0 = big_lay["dif_ids"][0]
    loop = patch32(big_img, off(big_lay, d0) + 508, d0)
    for fn, img2, why in (
        ("bad-difat-unterminated.cfb.gz", loop, "ENDOFCHAIN"),
        ("bad-difat-loop.cfb.gz", patch32(loop, 72, 2), "no other chain uses"),
    ):
        manifest["bad"].append({"file": fn, "contains": why, "limit": None})
        with open(os.path.join(outdir, fn), "wb") as f:
            f.write(gzip.compress(img2, 9, mtime=0))
    # too many entries for the default depth limit is tested in code (a 70-level chain)
    deep = D("root", [])
    cur = deep
    for i in range(70):
        n = D("d%d" % i, [])
        cur.children.append(n)
        cur = n
    cur.children.append(S("leaf", b"x"))
    good("v3-deep-70", deep, 3)

    with open(os.path.join(outdir, "manifest.json"), "w") as f:
        json.dump(manifest, f, indent=1, sort_keys=True)
        f.write("\n")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "cfb-vectors")
