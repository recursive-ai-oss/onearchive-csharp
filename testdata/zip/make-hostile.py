#!/usr/bin/env python3
"""Writes a set of hostile zip archives into a directory.

    make-hostile.py OUTDIR

Every file is generated from fixed recipes (no randomness), so the set is reproducible. They cover path traversal (also with
backslashes, drive letters and UNC prefixes), NUL in names, symbolic links that escape or are written through, bombs on each
limit (entry count, declared size, expansion ratio, data descriptors, entries that share data), disagreements between the
central directory and the local headers, bad zip64 records and truncations. The unit tests build the same cases in C#; this
script feeds the fuzz seeds and the hostile-set test.
"""
import os
import struct
import sys
import zlib

UNIX_LINK = (0xA1FF << 16)
UNIX_FILE = (0x81A4 << 16)


def deflate(data):
    c = zlib.compressobj(9, zlib.DEFLATED, -15)
    return c.compress(data) + c.flush()


class Entry:
    def __init__(self, name, data=b"", method=0, plain=None, flags=0, attr=0, made=20, extra=b"", cextra=b"",
                 crc=None, csize=None, usize=None, offset=None, descriptor=False, raw_name=None, comment=b""):
        self.name = raw_name if raw_name is not None else (name.encode() if isinstance(name, str) else name)
        self.data = data
        self.method = method
        self.plain = data if plain is None else plain
        self.flags = flags | (8 if descriptor else 0)
        self.attr = attr
        self.made = made
        self.extra = extra
        self.cextra = cextra
        self.crc = zlib.crc32(self.plain) if crc is None else crc
        self.csize = len(data) if csize is None else csize
        self.usize = len(self.plain) if usize is None else usize
        self.offset_override = offset
        self.descriptor = descriptor
        self.comment = comment
        self.local_offset = 0


def stored(name, data=b"", **kw):
    return Entry(name, data, 0, **kw)


def deflated(name, plain, **kw):
    return Entry(name, deflate(plain), 8, plain=plain, **kw)


def build(entries, count=None, dir_offset=None, dir_size=None, comment=b"", dir_extra=b"", truncate=None):
    out = bytearray()
    for e in entries:
        e.local_offset = len(out)
        h = struct.pack("<IHHHHHIIIHH", 0x04034B50, 20, e.flags, e.method, 0, 0x21,
                        0 if e.descriptor else e.crc, 0 if e.descriptor else e.csize, 0 if e.descriptor else e.usize,
                        len(e.name), len(e.extra))
        out += h + e.name + e.extra + e.data
        if e.descriptor:
            out += struct.pack("<IIII", 0x08074B50, e.crc, len(e.data), len(e.plain))
    start = len(out)
    for e in entries:
        off = e.local_offset if e.offset_override is None else e.offset_override
        out += struct.pack("<IHHHHHHIIIHHHHHII", 0x02014B50, e.made, 20, e.flags, e.method, 0, 0x21, e.crc, e.csize,
                           e.usize, len(e.name), len(e.cextra), len(e.comment), 0, 0, e.attr, off & 0xFFFFFFFF)
        out += e.name + e.cextra + e.comment
    size = len(out) - start
    out += dir_extra
    n = len(entries) if count is None else count
    out += struct.pack("<IHHHHIIH", 0x06054B50, 0, 0, n & 0xFFFF, n & 0xFFFF,
                       (size if dir_size is None else dir_size) & 0xFFFFFFFF,
                       (start if dir_offset is None else dir_offset) & 0xFFFFFFFF, len(comment)) + comment
    return bytes(out if truncate is None else out[:truncate])


def link(name, target, **kw):
    return stored(name, target, made=(3 << 8) | 20, attr=UNIX_LINK, **kw)


def zip64_extra(*values):
    return struct.pack("<HH", 1, 8 * len(values)) + b"".join(struct.pack("<Q", v) for v in values)


def files():
    f = {}
    x = b"evil"
    f["traversal"] = build([stored("../evil.txt", x)])
    f["traversal-deep"] = build([stored("a/b/../../../evil.txt", x)])
    f["traversal-backslash"] = build([stored("..\\..\\evil.txt", x)])
    f["traversal-mixed"] = build([stored("a\\..\\../evil.txt", x)])
    f["absolute"] = build([stored("/tmp/onearchive-absolute-evil.txt", x)])
    f["absolute-backslash"] = build([stored("\\tmp\\onearchive-absolute-evil.txt", x)])
    f["unc"] = build([stored("\\\\server\\share\\evil.txt", x)])
    f["drive"] = build([stored("C:\\Windows\\evil.txt", x)])
    f["nul-in-name"] = build([stored(b"ok\0../../evil", x)])
    f["empty-name"] = build([stored(b"", x)])
    f["dot-name"] = build([stored("./", b"", attr=0x10)])
    f["symlink-escape"] = build([link("l", b"../../outside")])
    f["symlink-absolute"] = build([link("l", b"/etc")])
    f["symlink-empty-target"] = build([link("l", b"")])
    f["symlink-write-through"] = build([stored("d/", b"", attr=0x10), link("l", b"d"), stored("l/evil.txt", x)])
    f["symlink-escape-write-through"] = build([link("l", b"../x"), stored("l/evil.txt", x)])
    f["symlink-dotdot-chain"] = build([link("b", b"."), link("a", b"b/..")])
    f["symlink-huge-target"] = build([link("l", b"x" * 200000)])
    f["device"] = build([stored("null", b"", made=(3 << 8) | 20, attr=0x21B6 << 16)])
    f["fifo"] = build([stored("pipe", b"", made=(3 << 8) | 20, attr=0x11A4 << 16)])
    f["duplicate"] = build([stored("f", b"one"), stored("f", b"two")])
    f["file-then-dir"] = build([stored("x", b"f"), stored("x/y", b"g")])
    f["setuid"] = build([stored("suid", b"x", made=(3 << 8) | 20, attr=0x89ED << 16)])
    # Bombs.
    f["bomb-entry-count"] = build([stored("d/%d" % i, b"") for i in range(5000)])
    f["bomb-count-lie"] = build([stored("a", b"x")], count=65535)
    f["bomb-declared-size"] = build([stored("big", b"x", usize=0xFFFFFFFF, csize=1, cextra=zip64_extra(8 * 1024 ** 3), extra=zip64_extra(8 * 1024 ** 3, 1))])
    f["bomb-ratio"] = build([deflated("zeros", bytes(200 << 20))])
    f["bomb-ratio-descriptor"] = build([deflated("zeros", bytes(100 << 20), descriptor=True)])
    one = deflated("blob", bytes(20 << 20))
    twins = [one] + [Entry("n%d" % i, one.data, 8, plain=one.plain, offset=0) for i in range(300)]
    f["bomb-shared-data"] = build(twins)
    f["bomb-overlap-size"] = build([stored("first", b"x" * 100, csize=100000, usize=100000), stored("second", b"y" * 100)])
    f["bomb-path-length"] = build([stored("x" * 60000, b"")])
    f["bomb-path-depth"] = build([stored("/".join(["d"] * 300) + "/f", b"x")])
    inner = build([stored("a", b"1"), deflated("b", bytes(1 << 20))])
    f["zip-in-zip"] = build([stored("inner.zip", inner), stored("inner2.zip", inner)])
    # Disagreements and malformed structure.
    f["local-name-differs"] = build([stored("name", b"abcd")]).replace(b"name", b"nome", 1)
    e = stored("m", b"abcd")
    e.method = 0
    ok = bytearray(build([e]))
    ok[8] = 8
    f["local-method-differs"] = bytes(ok)
    f["local-size-differs"] = build([stored("s", b"abcd", usize=99)])
    f["crc-wrong"] = build([stored("c", b"abcd", crc=1)])
    f["deflate-garbage"] = build([Entry("g", b"\xff\xff\xff\xff\xff\xff\xff\xff", 8, plain=b"x" * 8)])
    f["deflate-extra-bytes"] = build([Entry("t", deflate(b"hello") + b"\0\0\0", 8, plain=b"hello")])
    f["method-unknown"] = build([Entry("u", b"abc", 77, plain=b"abc")])
    f["method-lzma"] = build([Entry("u", b"abc", 14, plain=b"abc")])
    f["encrypted"] = build([stored("e", b"abc", flags=1)])
    f["aes"] = build([Entry("e", b"abc", 99, plain=b"abc", flags=1)])
    f["dir-offset-wild"] = build([stored("a", b"x")], dir_offset=0x7FFFFFFF)
    f["dir-size-wild"] = build([stored("a", b"x")], dir_size=0x7FFFFFFF)
    f["entry-offset-wild"] = build([stored("a", b"x", offset=0x7FFFFFF0)])
    f["spanned"] = build([stored("a", b"x")]).replace(b"PK\x05\x06\x00\x00\x00\x00", b"PK\x05\x06\x01\x00\x00\x00")
    f["extra-overrun"] = build([stored("a", b"x", cextra=struct.pack("<HH", 0x1234, 500) + b"zz")])
    f["zip64-missing-field"] = build([stored("a", b"x", usize=0xFFFFFFFF, csize=0xFFFFFFFF, cextra=zip64_extra(1))])
    f["zip64-huge"] = build([stored("a", b"x", usize=0xFFFFFFFF, csize=0xFFFFFFFF, cextra=zip64_extra(1 << 62, 1))])
    f["zip64-marker-no-record"] = build([stored("a", b"x")], count=0xFFFF, dir_offset=0xFFFFFFFF, dir_size=0xFFFFFFFF)
    base = build([stored("a", b"hello"), stored("b", b"world")])
    f["truncated-mid-data"] = base[:40]
    f["truncated-mid-directory"] = base[:-60]
    f["truncated-no-end"] = base[:-22]
    f["only-local-headers"] = base[: base.index(b"PK\x01\x02")]
    f["end-record-only"] = struct.pack("<IHHHHIIH", 0x06054B50, 0, 0, 0, 0, 0, 0, 0)
    f["end-record-comment-lie"] = struct.pack("<IHHHHIIH", 0x06054B50, 0, 0, 0, 0, 0, 0, 60000) + b"PK\x05\x06"
    f["garbage-prefix"] = b"MZ" + bytes(1000) + base
    f["descriptor-wrong"] = build([Entry("d", deflate(b"payload"), 8, plain=b"payload", descriptor=True, crc=7)])
    return f


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    for name, data in files().items():
        with open(os.path.join(out, "hostile-%s.zip" % name), "wb") as fh:
            fh.write(data)


if __name__ == "__main__":
    main()
