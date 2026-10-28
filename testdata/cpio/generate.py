#!/usr/bin/env python3
"""Generates the cpio and ar test vectors by running the reference tools as black boxes.

Run from the repository root:  python3 testdata/cpio/generate.py

Needs GNU cpio, GNU ar, bsdtar and a C compiler on PATH. The tree below is fixed (names, contents, modes,
times); inode numbers, owners and the dates in the ar headers that the tools choose are not, so the tests
compare what the tools promise to keep and not the raw bytes. Tool versions are written to versions.txt.
"""
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
AR_DIR = os.path.join(os.path.dirname(HERE), "ar")
MTIME = 1700000000


def run(args, cwd=None, stdin=None, stdout=None):
    return subprocess.run(args, cwd=cwd, stdin=stdin, stdout=stdout, check=True)


def big(n):
    return bytes(((i * 31) ^ (i >> 8)) & 255 for i in range(n))


def build_tree(root):
    def put(rel, data, mode):
        p = os.path.join(root, rel)
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "wb") as f:
            f.write(data)
        os.chmod(p, mode)

    put("a.txt", b"hello\n", 0o644)
    put("sub/b.bin", b"xyz", 0o644)
    put("big.bin", big(70000), 0o600)
    put("empty", b"", 0o644)
    put("exec.sh", b"#!/bin/sh\n", 0o755)
    put("\u00fcn\u00ef-\u65e5\u672c.txt", b"unicode", 0o644)
    put("sub/" + "x" * 120 + ".txt", b"long path", 0o644)
    os.link(os.path.join(root, "a.txt"), os.path.join(root, "hard1"))
    os.symlink("a.txt", os.path.join(root, "link"))
    os.chmod(os.path.join(root, "sub"), 0o755)
    for dirpath, dirnames, filenames in os.walk(root):
        for n in dirnames + filenames:
            os.utime(os.path.join(dirpath, n), (MTIME, MTIME), follow_symlinks=False)


def names(root):
    out = []
    for dirpath, dirnames, filenames in os.walk(root):
        for n in dirnames + filenames:
            out.append("./" + os.path.relpath(os.path.join(dirpath, n), root))
    return sorted(out)


def cpio(root, fmt, out):
    listing = "\n".join(names(root)).encode() + b"\n"
    with open(out, "wb") as f:
        subprocess.run(["cpio", "-o", "-H", fmt, "--quiet"], cwd=root, input=listing, stdout=f, check=True)


def main():
    work = tempfile.mkdtemp(prefix="onearchive-vectors-")
    try:
        tree = os.path.join(work, "tree")
        os.makedirs(tree)
        build_tree(tree)
        for fmt in ("newc", "crc", "odc"):
            cpio(tree, fmt, os.path.join(HERE, f"tree.gnu-{fmt}.cpio"))
        for fmt in ("newc", "odc"):
            run(["bsdtar", "--format", fmt, "-cf", os.path.join(HERE, f"tree.bsdtar-{fmt}.cpio"), "-C", tree, "."])

        # Device nodes cannot be created without privileges: start from a tar made here and let bsdtar convert it.
        import tarfile
        with tarfile.open(os.path.join(work, "dev.tar"), "w") as t:
            for n, ma, mi, ty in [("c1", 8, 1, tarfile.CHRTYPE), ("b1", 259, 3, tarfile.BLKTYPE), ("f1", 0, 0, tarfile.FIFOTYPE)]:
                i = tarfile.TarInfo(n)
                i.type, i.devmajor, i.devminor, i.mode, i.mtime = ty, ma, mi, 0o644, MTIME
                t.addfile(i)
        for fmt in ("newc", "odc"):
            run(["bsdtar", "--format", fmt, "-cf", os.path.join(HERE, f"devices.bsdtar-{fmt}.cpio"), f"@{os.path.join(work, 'dev.tar')}"])

        # ar: plain members, a long name and a name with a space, and an archive with a symbol table made from an object file.
        flat = os.path.join(work, "flat")
        os.makedirs(flat)
        files = {
            "a.txt": b"hello\n",
            "b.bin": b"xyz",
            "empty": b"",
            "a-very-long-member-name-here.txt": big(1001),
            "sp ace.txt": b"spaced",
            "sixteen-chars-ok": b"16",
        }
        for n, d in files.items():
            with open(os.path.join(flat, n), "wb") as f:
                f.write(d)
            os.utime(os.path.join(flat, n), (MTIME, MTIME))
        order = list(files)
        gnu = os.path.join(AR_DIR, "flat.gnu.a")
        if os.path.exists(gnu):
            os.remove(gnu)
        run(["ar", "rc", gnu] + order, cwd=flat)
        run(["bsdtar", "--format", "arbsd", "-cf", os.path.join(AR_DIR, "flat.bsd.a")] + order, cwd=flat)

        with open(os.path.join(work, "f.c"), "w") as f:
            f.write("int alpha(void){return 1;}\nint beta(void){return 2;}\n")
        run(["gcc", "-c", "-o", os.path.join(work, "f.o"), os.path.join(work, "f.c")])
        sym = os.path.join(AR_DIR, "symbols.gnu.a")
        if os.path.exists(sym):
            os.remove(sym)
        run(["ar", "rcs", sym, "f.o"], cwd=work)

        # A small .deb: an ar archive of debian-binary, control.tar.gz and data.tar.gz.
        deb = os.path.join(work, "deb")
        os.makedirs(os.path.join(deb, "ctl"))
        os.makedirs(os.path.join(deb, "data/usr/share/doc/mini"))
        with open(os.path.join(deb, "debian-binary"), "w") as f:
            f.write("2.0\n")
        with open(os.path.join(deb, "ctl/control"), "w") as f:
            f.write("Package: mini\nVersion: 1\nArchitecture: all\nMaintainer: nobody <nobody@example.invalid>\nDescription: test package\n")
        with open(os.path.join(deb, "data/usr/share/doc/mini/README"), "w") as f:
            f.write("mini package\n")
        run(["tar", "--sort=name", "--mtime=@%d" % MTIME, "--owner=0", "--group=0", "--numeric-owner", "-czf", os.path.join(deb, "control.tar.gz"), "-C", os.path.join(deb, "ctl"), "."])
        run(["tar", "--sort=name", "--mtime=@%d" % MTIME, "--owner=0", "--group=0", "--numeric-owner", "-czf", os.path.join(deb, "data.tar.gz"), "-C", os.path.join(deb, "data"), "."])
        mini = os.path.join(AR_DIR, "mini.deb")
        if os.path.exists(mini):
            os.remove(mini)
        run(["ar", "rc", mini, "debian-binary", "control.tar.gz", "data.tar.gz"], cwd=deb)

        with open(os.path.join(HERE, "versions.txt"), "w") as f:
            for cmd in (["cpio", "--version"], ["ar", "--version"], ["bsdtar", "--version"]):
                out = subprocess.run(cmd, capture_output=True, text=True, check=True).stdout.splitlines()[0]
                f.write(out + "\n")
        shutil.copyfile(os.path.join(HERE, "versions.txt"), os.path.join(AR_DIR, "versions.txt"))
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
