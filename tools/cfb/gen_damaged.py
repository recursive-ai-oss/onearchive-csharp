#!/usr/bin/env python3
"""Writes damaged compound files for the recovery tests, with what a recovering reader should still find in each.

Each vector is one named kind of damage applied to a good file: either one of this repository's own vectors (testdata/cfb,
written by gen_cfb.py), a front-loaded rearrangement of a freshly built one (FAT and directory first, so cutting the tail off
costs only data), or a LibreOffice-made file from testdata/cfb/office. The expectations come from cfbmodel.py, a model of the
tables that are left, not from running the reader: per stream, how many leading bytes lie before the first break and the SHA-256
of exactly those bytes of the original content.

Usage: gen_damaged.py OUTDIR       (run from the repository root; writes the vectors and manifest.json; deterministic)
"""
import gzip
import hashlib
import json
import os
import struct
import sys

here = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, here)
import cfbmodel  # noqa: E402
import gen_cfb  # noqa: E402
from cfbmodel import Cfb, END, FREE, FATSECT, DIFSECT, NOSTREAM  # noqa: E402

repo = os.path.abspath(os.path.join(here, "..", ".."))
td = os.path.join(repo, "testdata", "cfb")
MAXREG = 0xFFFFFFFA


def sha(b):
    return hashlib.sha256(bytes(b)).hexdigest()


def hexname(n):
    return n.encode("utf-16-le", "surrogatepass").hex()


def content(c, e):
    """The full content of a stream of the pristine file."""
    size = e["size"]
    if size == 0:
        return b""
    if c.is_mini(e):
        ms = b"".join(bytes(c.data[c.off(s):c.off(s) + c.size]) for s in c.mini_stream)
        out, cur, n = b"", e["start"], 0
        while len(out) < size:
            out += ms[cur * 64:cur * 64 + 64]
            cur = c.mini_fat[cur]
        return out[:size]
    out = b"".join(bytes(c.data[c.off(s):c.off(s) + c.size]) for s in c.chain(e["start"]))
    return out[:size]


def set32(data, at, v):
    data[at:at + 4] = struct.pack("<I", v)


def fat_off(c, sector):
    return c.off(c.fat_sectors[sector // c.per]) + 4 * (sector % c.per)


def minifat_off(c, k):
    return c.off(c.mini_chain[k // c.per]) + 4 * (k % c.per)


def front_load(img):
    """Moves the FAT, DIFAT, directory, mini FAT and mini stream sectors to the front, so that a file cut short loses only data."""
    c = Cfb(img)
    nsec = len(c.data) // c.size - 1
    order = list(c.fat_sectors) + list(c.difat_sectors) + list(c.dir_sectors) + list(c.mini_chain) + list(c.mini_stream)
    order += [s for s in range(nsec) if s not in set(order)]
    new = {old: i for i, old in enumerate(order)}

    def m(v):
        return new.get(v, v) if v < nsec else v

    out = bytearray(c.data[:c.size])
    secs = [bytearray(c.data[c.off(old):c.off(old) + c.size]) for old in order]
    # FAT: positions and values move
    fat = [FREE] * len(c.fat)
    for old in range(min(nsec, len(c.fat))):
        fat[new[old]] = m(c.fat[old])
    for i, s in enumerate(c.fat_sectors):
        secs[new[s]][:] = struct.pack("<%dI" % c.per, *fat[i * c.per:(i + 1) * c.per])
    nf = len(c.fat_sectors)
    for i, s in enumerate(c.difat_sectors):
        base = secs[new[s]]
        for j in range(c.per):
            v = struct.unpack_from("<I", base, 4 * j)[0]
            struct.pack_into("<I", base, 4 * j, m(v) if v < nsec else v)
    # directory entries
    for e in c.entries:
        if e["type"] in (2, 5) and e["size"] > 0 and (e["type"] == 5 or not c.is_mini(e)):
            sec = c.dir_sectors[e["sid"] // (c.size // 128)]
            pos = (e["off"] - c.off(sec))
            struct.pack_into("<I", secs[new[sec]], pos + 116, m(e["start"]))
    # header
    hdr = out
    set32(hdr, 48, m(c.first_dir))
    if c.num_mini:
        set32(hdr, 60, m(c.first_mini))
    if c.num_dif:
        set32(hdr, 68, m(c.first_dif))
    for i in range(109):
        v = struct.unpack_from("<I", hdr, 76 + 4 * i)[0]
        if v != FREE:
            set32(hdr, 76 + 4 * i, m(v))
    for s in secs:
        out += s
    r = Cfb(bytes(out))
    for (p, e), (p2, e2) in zip(c.streams(), r.streams()):
        assert p == p2 and content(c, e) == content(r, e2), p
    return bytes(out)


class Vec:
    def __init__(self, name, base_name, base, damage):
        self.name, self.base_name, self.base, self.damage = name, base_name, base, damage
        self.data = bytearray(base.data)
        self.kinds = []
        self.fat = list(base.fat)
        self.mini_fat = list(base.mini_fat)
        self.file_len = len(base.data)
        self.sizes = {}      # sid -> declared size after the damage
        self.lost = set()    # sids that no longer exist
        self.by = "path"
        self.unrecoverable = False
        self.ms_fat_override = None


def expect(v):
    c = v.base
    streams = []
    ms_bytes = c.mini_bytes(v.fat, v.file_len) if c.root["size"] else 0
    names = {}
    for p, e in c.streams():
        if e["sid"] in v.lost or e["type"] != 2:
            continue
        e2 = dict(e)
        e2["size"] = v.sizes.get(e["sid"], e["size"])
        length = c.readable(e2, v.fat, v.mini_fat, ms_bytes, v.file_len)
        full = content(c, e)
        if length > len(full):
            full = full + b"\0" * (length - len(full))   # a declared size beyond the chain: only the chain's bytes exist
        streams.append({"path": [hexname(x) for x in p], "name": hexname(p[-1]), "declared": e2["size"], "length": length,
                        "sha256": sha(full[:length]), "damaged": length < e2["size"]})
        names[p[-1]] = names.get(p[-1], 0) + 1
    if v.by == "name":
        assert all(n == 1 for n in names.values()), "stream names must be unique to match by name"
    return streams


def big_stream(c, minsectors=3):
    for p, e in c.streams():
        if e["type"] == 2 and not c.is_mini(e) and e["size"] >= minsectors * c.size:
            return e
    raise SystemExit("no big stream in " + c.root["name"])


def mini_stream_entry(c, minsectors=3):
    for p, e in c.streams():
        if e["type"] == 2 and c.is_mini(e) and e["size"] > minsectors * 64:
            return e
    raise SystemExit("no multi-sector mini stream")


def entry_of_kind(c, typ):
    for p, e in c.streams():
        if e["type"] == typ:
            return e


# ---- the damages; each edits v.data, the model tables in v, and says what kinds of record it should produce ----

def d_truncate(cut_from_end):
    def f(v):
        c = v.base
        v.file_len = len(c.data) - cut_from_end
        v.data = v.data[:v.file_len]
        v.kinds = ["DamagedStream"]
    return f


def d_fat_set(value, pos=1, kinds=("DamagedStream",), stream=None):
    def f(v):
        c = v.base
        e = (stream(c) if stream else big_stream(c))
        ch = c.chain(e["start"])
        s = ch[pos]
        set32(v.data, fat_off(c, s), value if value is not None else ch[0])
        v.fat[s] = value if value is not None else ch[0]
        v.kinds = list(kinds)
    return f


def d_size_too_big(v):
    c = v.base
    e = big_stream(c)
    new = e["size"] * 3 + 100
    struct.pack_into("<I", v.data, e["off"] + 120, new)
    v.sizes[e["sid"]] = new
    v.kinds = ["DamagedStream"]


def d_difat_slot_bad(slot):
    def f(v):
        c = v.base
        # the FAT sector listed in this slot is unreadable, so the part of the FAT it holds is free
        at, sector = c.difat_slots[slot]
        set32(v.data, at, 0x00123456)
        for i in range(slot * c.per, (slot + 1) * c.per):
            if i < len(v.fat):
                v.fat[i] = FREE
        v.kinds = ["AllocationTableUnusable", "DamagedStream"]
    return f


def d_minifat_start_bad(v):
    c = v.base
    set32(v.data, 60, 0x00ABCDEF)
    v.mini_fat = []
    v.kinds = ["MiniStreamUnusable", "DamagedStream"]


def d_minifat_entry_bad(v):
    c = v.base
    e = mini_stream_entry(c)
    cur = e["start"]
    k = c.mini_fat[cur]
    set32(v.data, minifat_off(c, cur), 0x7FFF0000)
    v.mini_fat[cur] = 0x7FFF0000
    v.kinds = ["DamagedStream"]


def d_ministream_cut(v):
    c = v.base
    first = c.mini_stream[0]
    set32(v.data, fat_off(c, first), END)
    v.fat[first] = END
    v.kinds = ["MiniStreamUnusable", "DamagedStream"]


def d_dir_start_bad(v):
    set32(v.data, 48, 0x00F00000)
    v.kinds = ["DirectoryUnusable"]


def d_dir_chain_cut(v):
    c = v.base
    s = c.dir_sectors[0]
    set32(v.data, fat_off(c, s), FREE)
    v.kinds = ["DirectoryUnusable"]


def link_node(c, field):
    """An entry whose `field` (left or right) link is in use, for damaging the link."""
    for e in c.entries[1:]:
        if e["type"] in (1, 2) and e[field] != NOSTREAM:
            return e


def d_link_out_of_range(v):
    c = v.base
    e = link_node(c, "left") or link_node(c, "right")
    at = e["off"] + (0x44 if e["left"] != NOSTREAM else 0x48)
    set32(v.data, at, 0x00001234)
    v.kinds = ["BadLink", "OrphanedEntry"]
    v.by = "name"


def d_dir_cycle(v):
    c = v.base
    top = c.entries[0]["child"]
    # point an empty link of a deeper sibling at the top of its tree
    for e in c.entries[1:]:
        if e["type"] in (1, 2) and e["sid"] != top and e["left"] == NOSTREAM and c.entries[top]["type"] in (1, 2):
            if _same_tree(c, top, e["sid"]):
                set32(v.data, e["off"] + 0x44, top)
                v.kinds = ["BadLink"]
                return
    raise SystemExit("no node for the cycle")


def _same_tree(c, top, sid):
    stack, seen = [top], set()
    while stack:
        n = stack.pop()
        if n == NOSTREAM or n in seen:
            continue
        seen.add(n)
        stack += [c.entries[n]["left"], c.entries[n]["right"]]
    return sid in seen


def d_bad_type(v):
    c = v.base
    for p, e in c.streams():
        if e["type"] == 2 and e["size"] > 0 and e["left"] == NOSTREAM and e["right"] == NOSTREAM:
            v.data[e["off"] + 0x42] = 3
            v.lost.add(e["sid"])
            v.kinds = ["BadEntry", "BadLink"]
            v.by = "name"
            return
    raise SystemExit("no leaf stream")


def d_bad_color(v):
    c = v.base
    e = c.entries[1]
    v.data[e["off"] + 0x43] = 9
    v.kinds = ["BadEntry"]


def d_root_wrecked(v):
    v.data[v.base.entries[0]["off"] + 0x42] = 0
    v.kinds = ["BadEntry", "OrphanedEntry"]


def d_header(offset, fmt, value, kinds=("HeaderInconsistent",)):
    def f(v):
        struct.pack_into(fmt, v.data, offset, value)
        v.kinds = list(kinds)
    return f


def d_unrecoverable_header_only(v):
    v.data = v.data[:512]
    v.file_len = 512
    v.unrecoverable = True


def d_unrecoverable_no_fat(v):
    for i in range(109):
        set32(v.data, 76 + 4 * i, FREE)
    v.unrecoverable = True


def main(outdir):
    os.makedirs(outdir, exist_ok=True)
    pat = gen_cfb.pattern
    S, D = gen_cfb.S, gen_cfb.D

    def tree():
        return D("root", [
            S("one", pat(30000, 5)), S("two", pat(20000, 6)), S("three", pat(5000, 7)),
            D("Storage A", [S("a1", pat(100, 8)), S("a2", pat(900, 9)), D("Inner", [S("deepbig", pat(9000, 10)), S("deepsmall", pat(70, 11))])]),
            D("Storage B", [S("b1", pat(5000, 12)), S("b2", pat(333, 13))]),
            S("tiny", pat(10, 14)), S("note", pat(2000, 15)),
        ])

    def bigonly():
        return D("root", [D("S1", [S("p", pat(9000, 1)), S("q", pat(6000, 2))]), S("r", pat(12000, 3)), S("s", pat(8000, 4)), D("S2", [S("t", pat(5000, 5))])])

    bases = {}

    def own(name, root, version, **kw):
        img, _ = gen_cfb.build(root, version, **kw)
        bases[name] = Cfb(front_load(img))

    own("own3", tree(), 3, fragment=True)
    own("own4", tree(), 4, fragment=True)
    own("big3", bigonly(), 3)
    for name, path in [("msg", "v3-msg-like.cfb"), ("difat", "v3-difat.cfb.gz"), ("doc", "office/small.doc"), ("report", "office/report.doc"),
                       ("xls", "office/table.xls"), ("ppt", "office/deck.ppt"), ("smallxls", "office/small.xls")]:
        bases[name] = Cfb(cfbmodel.load(os.path.join(td, path)))

    plan = [
        # a file cut short: front-loaded bases keep the tables, so only the tail of the data goes
        ("truncate-sector", "own3", d_truncate(512 * 20)),
        ("truncate-mid-sector", "own3", d_truncate(512 * 20 + 137)),
        ("truncate-v4", "own4", d_truncate(4096 * 3 + 1000)),
        # allocation tables
        ("fat-out-of-range", "own3", d_fat_set(0x00FFFFF0)),
        ("fat-out-of-range-doc", "report", d_fat_set(0x00FFFFF0, pos=20)),
        ("fat-cycle", "own3", d_fat_set(None, pos=10)),
        ("fat-cycle-ppt", "ppt", d_fat_set(None, pos=300)),
        ("fat-through-free", "own4", d_fat_set(FREE, pos=2)),
        ("fat-ends-early", "own3", d_fat_set(END, pos=7)),
        ("size-beyond-chain", "own3", d_size_too_big),
        ("size-beyond-chain-xls", "xls", d_size_too_big),
        ("difat-slot-lost", "ppt", d_difat_slot_bad(3)),
        ("difat-slot-lost-doc", "report", d_difat_slot_bad(1)),
        ("difat-slot-lost-large", "difat", d_difat_slot_bad(5)),
        # mini FAT and mini stream
        ("minifat-lost", "msg", d_minifat_start_bad),
        ("minifat-entry-bad", "msg", d_minifat_entry_bad),
        ("minifat-entry-bad-doc", "doc", d_minifat_entry_bad),
        ("ministream-cut", "msg", d_ministream_cut),
        # directory
        ("dir-start-bad", "own3", d_dir_start_bad),
        ("dir-start-bad-doc", "report", d_dir_start_bad),
        ("dir-chain-cut", "msg", d_dir_chain_cut),
        ("dir-chain-cut-doc", "doc", d_dir_chain_cut),
        ("dir-link-out-of-range", "own3", d_link_out_of_range),
        ("dir-link-out-of-range-ppt", "ppt", d_link_out_of_range),
        ("dir-cycle", "own3", d_dir_cycle),
        ("dir-bad-type", "own3", d_bad_type),
        ("dir-bad-type-xls", "xls", d_bad_type),
        ("dir-bad-color", "own3", d_bad_color),
        ("dir-root-wrecked", "big3", d_root_wrecked),
        # header fields
        ("header-fat-count", "own3", d_header(44, "<I", 0xFFFFFF00)),
        ("header-fat-count-xls", "smallxls", d_header(44, "<I", 0)),
        ("header-minifat-count", "msg", d_header(64, "<I", 9)),
        ("header-difat-count", "difat", d_header(72, "<I", 0)),
        ("header-cutoff", "own3", d_header(56, "<I", 8192)),
        ("header-version", "own3", d_header(26, "<H", 9)),
        ("header-sector-shift", "own3", d_header(30, "<H", 13)),
        ("header-byte-order", "own3", d_header(28, "<H", 0xFEFF)),
        ("header-mini-shift", "own3", d_header(32, "<H", 7)),
        # nothing left to recover
        ("unrecoverable-header-only", "own3", d_unrecoverable_header_only),
        ("unrecoverable-no-fat", "own3", d_unrecoverable_no_fat),
    ]
    manifest = {"vectors": []}
    for name, base_name, damage in plan:
        v = Vec(name, base_name, bases[base_name], damage)
        damage(v)
        fn = name + ".cfb"
        payload = bytes(v.data)
        if len(payload) > 64 * 1024:
            fn += ".gz"
            payload = gzip.compress(payload, 9, mtime=0)
        with open(os.path.join(outdir, fn), "wb") as f:
            f.write(payload)
        entry = {"file": fn, "base": base_name, "kinds": v.kinds, "unrecoverable": v.unrecoverable, "match": v.by}
        if not v.unrecoverable:
            entry["streams"] = expect(v)
        manifest["vectors"].append(entry)
    with open(os.path.join(outdir, "manifest.json"), "w") as f:
        json.dump(manifest, f, indent=1, sort_keys=True)
        f.write("\n")
    print(len(manifest["vectors"]), "vectors")


if __name__ == "__main__":
    main(sys.argv[1])
