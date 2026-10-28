#!/usr/bin/env python3
"""Writes a set of hostile tar archives into a directory.

    make-hostile.py OUTDIR

Every file is generated here from fixed recipes (no randomness), so the set is reproducible. They cover path traversal,
absolute and drive-letter names, links that escape or are written through, hard links to nowhere, device nodes, bombs
on each limit (entry count, declared size, path length and depth, pax header size, sparse size and region count) and
malformed headers. The unit tests build the same cases in C#; this script feeds the fuzz seeds and the hostile-set test.
"""
import os
import sys

BLOCK = 512


def octal(value, width):
    return ("%0*o" % (width - 1, value)).encode() + b"\0"


def header(name, typeflag=b"0", size=0, link=b"", mode=0o644, gnu=False, prefix=b"", checksum=True):
    h = bytearray(BLOCK)
    h[0:len(name)] = name[:100]
    h[100:108] = octal(mode, 8)
    h[108:116] = octal(1000, 8)
    h[116:124] = octal(1000, 8)
    h[124:136] = octal(size, 12)
    h[136:148] = octal(1700000000, 12)
    h[156:157] = typeflag
    h[157:157 + len(link)] = link[:100]
    h[257:265] = b"ustar  \0" if gnu else b"ustar\x0000"
    h[265:269] = b"user"
    h[297:302] = b"group"
    h[345:345 + len(prefix)] = prefix
    if checksum:
        h[148:156] = b" " * 8
        h[148:156] = ("%06o" % sum(h)).encode() + b"\0 "
    return bytes(h)


def padded(data):
    return data + b"\0" * (-len(data) % BLOCK)


def entry(name, typeflag=b"0", data=b"", link=b"", mode=0o644, size=None, gnu=False):
    return header(name, typeflag, len(data) if size is None else size, link, mode, gnu) + padded(data)


def record(key, value):
    body = b" " + key.encode() + b"=" + value + b"\n"
    n = len(body) + len(str(len(body)))
    if len(str(n)) != len(str(len(body))):
        n += 1
    return str(n).encode() + body


def pax(*records, typeflag=b"x"):
    data = b"".join(record(k, v if isinstance(v, bytes) else v.encode()) for k, v in records)
    return header(b"PaxHeader/x", typeflag, len(data)) + padded(data)


def gnu_long(name, typeflag=b"L"):
    data = name + b"\0"
    return header(b"././@LongLink", typeflag, len(data), gnu=True) + padded(data)


END = b"\0" * (2 * BLOCK)


def old_sparse(name, regions, real_size, data):
    h = bytearray(header(name, b"S", len(data), gnu=True, checksum=False))
    for i, (off, length) in enumerate(regions[:4]):
        h[386 + 24 * i:386 + 24 * i + 12] = octal(off, 12)
        h[398 + 24 * i:398 + 24 * i + 12] = octal(length, 12)
    h[483:495] = octal(real_size, 12)
    h[148:156] = b" " * 8
    h[148:156] = ("%06o" % sum(h)).encode() + b"\0 "
    return bytes(h) + padded(data)


def build():
    files = {}
    files["traversal"] = entry(b"../evil.txt", data=b"evil") + END
    files["traversal-deep"] = entry(b"a/b/../../../evil.txt", data=b"evil") + END
    files["traversal-backslash"] = entry(b"..\\..\\evil.txt", data=b"evil") + END
    files["absolute"] = entry(b"/tmp/onearchive-absolute-evil.txt", data=b"evil") + END
    files["unc"] = entry(b"//server/share/evil.txt", data=b"evil") + END
    files["drive"] = entry(b"C:\\Windows\\evil.txt", data=b"evil") + END
    files["nul-in-name"] = pax(("path", b"ok\0../../evil")) + entry(b"x", data=b"evil") + END
    files["symlink-escape"] = entry(b"l", b"2", link=b"../../outside") + END
    files["symlink-absolute"] = entry(b"l", b"2", link=b"/etc") + END
    files["symlink-write-through"] = (entry(b"d/", b"5", mode=0o755) + entry(b"l", b"2", link=b"d")
                                      + entry(b"l/evil.txt", data=b"evil") + END)
    files["symlink-escape-write-through"] = entry(b"l", b"2", link=b"../x") + entry(b"l/evil.txt", data=b"evil") + END
    files["symlink-dotdot-chain"] = entry(b"b", b"2", link=b".") + entry(b"a", b"2", link=b"b/..") + END
    files["hardlink-escape"] = entry(b"h", b"1", link=b"../../etc/passwd") + END
    files["hardlink-absolute"] = entry(b"h", b"1", link=b"/etc/passwd") + END
    files["hardlink-missing"] = entry(b"h", b"1", link=b"never-extracted") + END
    files["device-char"] = entry(b"null", b"3") + END
    files["device-block"] = entry(b"sda", b"4") + END
    files["fifo"] = entry(b"pipe", b"6") + END
    files["duplicate"] = entry(b"f", data=b"one") + entry(b"f", data=b"two") + END
    files["file-then-dir"] = entry(b"x", data=b"f") + entry(b"x/y", data=b"g") + END
    files["setuid"] = entry(b"suid", mode=0o6755, data=b"x") + END
    # Bombs.
    files["bomb-entry-count"] = b"".join(entry(b"d/%d" % i, b"5", mode=0o755) for i in range(5000)) + END
    files["bomb-declared-size"] = header(b"big", size=8 * 1024 ** 3) + END
    files["bomb-total-size"] = b"".join(header(b"big%d" % i, size=400 * 1024 ** 2) for i in range(4)) + END
    files["bomb-path-depth"] = gnu_long(b"/".join([b"d"] * 200) + b"/f") + entry(b"t", gnu=True) + END
    files["bomb-path-length"] = gnu_long(b"x" * 100000) + entry(b"t", gnu=True) + END
    files["bomb-pax-header"] = header(b"PaxHeader/x", b"x", 3 * 1024 ** 2) + END
    files["bomb-sparse-size"] = old_sparse(b"hole", [], 1 << 40, b"") + END
    regions = b"100000\n" + b"".join(b"%d\n1\n" % (2 * i) for i in range(100000))
    files["bomb-sparse-regions"] = (pax(("GNU.sparse.major", "1"), ("GNU.sparse.minor", "0"), ("GNU.sparse.name", "s"),
                                        ("GNU.sparse.realsize", "1000000"))
                                    + header(b"GNUSparseFile.0/s", size=len(padded(regions)) + 100000)
                                    + padded(regions) + padded(b"\0" * 100000) + END)
    # Malformed.
    files["liar-size"] = header(b"liar", size=10 * 1024 ** 2) + b"x" * 100
    files["bad-checksum"] = bytes(bytearray(entry(b"a", data=b"x"))[:148] + b"0000000\0" + bytearray(entry(b"a", data=b"x"))[156:]) + END
    files["negative-size"] = bytes(bytearray(entry(b"a"))[:124] + b"\xff" * 12 + bytearray(entry(b"a"))[136:]) + END
    files["huge-base256-time"] = bytes(bytearray(entry(b"a"))[:136] + b"\x80" + b"\xff" * 11 + bytearray(entry(b"a"))[148:]) + END
    files["pax-bad-length"] = header(b"PaxHeader/x", b"x", 12) + padded(b"99999 path=x\n") + entry(b"a") + END
    files["pax-no-newline"] = header(b"PaxHeader/x", b"x", 10) + padded(b"10 path=ab") + entry(b"a") + END
    files["sparse-overlap"] = old_sparse(b"s", [(0, 5), (3, 2)], 100, b"1234567") + END
    files["multivolume"] = entry(b"a", b"M") + END
    files["no-end-marker"] = entry(b"a", data=b"x")
    files["truncated-header"] = entry(b"a", data=b"x")[:300]
    # Valid but odd: handy as seeds.
    files["ok-sparse-old"] = old_sparse(b"s", [(100, 10), (500, 5)], 1000, b"ABCDEFGHIJxyz12") + END
    files["ok-pax-global"] = pax(("comment", "global"), typeflag=b"g") + pax(("mtime", "1.5")) + entry(b"a", data=b"x") + END
    return files


def main():
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    for name, data in sorted(build().items()):
        with open(os.path.join(out, "hostile-" + name + ".tar"), "wb") as f:
            f.write(data)


if __name__ == "__main__":
    main()
