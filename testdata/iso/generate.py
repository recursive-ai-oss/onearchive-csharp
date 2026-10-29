#!/usr/bin/env python3
"""Generates the ISO 9660 test vectors by running xorriso and genisoimage as black boxes.

Run from the repository root:  python3 testdata/iso/generate.py

Needs xorriso and genisoimage on PATH. The tree below is fixed (names, contents, modes, times, links). The tools stamp the
volume dates and choose the order of the file data themselves, so the tests compare names, sizes, contents, modes, link targets and
times, never the raw image bytes. The exact commands are in IMAGES and DEEP_IMAGE; tool versions are written to vectors/versions.txt.
"""
import os
import shutil
import subprocess
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "vectors")
MTIME = 1700000000


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
    put("big.bin", big(5000), 0o600)
    put("empty", b"", 0o644)
    put("exec.sh", b"#!/bin/sh\n", 0o755)
    put("\u00fcn\u00ef-\u65e5\u672c.txt", "unicode".encode(), 0o644)
    put("sub/" + "x" * 120 + ".txt", b"long path", 0o644)
    put("n" * 250 + ".txt", b"name of 254 bytes", 0o644)
    put("private/p.txt", b"secret", 0o600)
    os.chmod(os.path.join(root, "private"), 0o700)
    deep = "/".join("d%d" % i for i in range(1, 10))
    put(deep + "/f.txt", b"deep", 0o644)
    os.symlink("a.txt", os.path.join(root, "link"))
    os.symlink("../a.txt", os.path.join(root, "sub", "up"))
    os.symlink("/etc/passwd", os.path.join(root, "abs"))
    os.symlink("/".join(["segment-%03d" % i for i in range(60)]) + "/end", os.path.join(root, "longlink"))
    for dirpath, dirnames, filenames in os.walk(root, topdown=False):
        for n in filenames + dirnames:
            os.utime(os.path.join(dirpath, n), (MTIME, MTIME), follow_symlinks=False)
        os.utime(dirpath, (MTIME, MTIME))


def build_deep_tree(root):
    # Ten levels: past the eight ECMA-119 allows, so genisoimage moves the tenth directory to RR_MOVED.
    deep = os.path.join(root, *["d%d" % i for i in range(1, 11)])
    os.makedirs(deep)
    with open(os.path.join(deep, "f.txt"), "wb") as f:
        f.write(b"deep")
    with open(os.path.join(root, "a.txt"), "wb") as f:
        f.write(b"hi\n")
    os.symlink("a.txt", os.path.join(root, "link"))
    for dirpath, dirnames, filenames in os.walk(root, topdown=False):
        for n in filenames + dirnames:
            os.utime(os.path.join(dirpath, n), (MTIME, MTIME), follow_symlinks=False)
        os.utime(dirpath, (MTIME, MTIME))


def native(name, rockridge, joliet, level, volume):
    # xorriso's mkisofs emulation always writes Rock Ridge, so the images without it are made with its native commands.
    return (name, lambda out, tree: ["xorriso", "-outdev", out, "-rockridge", rockridge, "-joliet", joliet, "-compliance", "iso_9660_level=%d" % level,
                                    "-padding", "0", "-volid", volume, "-map", tree, "/", "-commit"])


def mkisofs(name, options, volume):
    return (name, lambda out, tree: ["xorriso", "-as", "mkisofs"] + options + ["-no-pad", "-V", volume, "-o", out, tree])


IMAGES = [
    native("plain1.iso", "off", "off", 1, "PLAIN1"),
    native("plain3.iso", "off", "off", 3, "PLAIN3"),
    native("joliet.iso", "off", "on", 3, "JOLIET"),
    mkisofs("rockridge.iso", ["-R", "-iso-level", "3"], "ROCKRIDGE"),
    mkisofs("rockridge-joliet.iso", ["-R", "-J", "-iso-level", "3"], "RRJOLIET"),
]

# Made from the separate, deep tree; genisoimage moves directories below the eighth level to RR_MOVED.
DEEP_IMAGE = ("relocated.iso", lambda out, tree: ["genisoimage", "-R", "-J", "-iso-level", "3", "-no-pad", "-V", "RELOCATED", "-o", out, tree])


def main():
    shutil.rmtree(OUT, ignore_errors=True)
    os.makedirs(OUT)
    work = tempfile.mkdtemp()
    try:
        tree = os.path.join(work, "tree")
        os.makedirs(tree)
        build_tree(tree)
        for name, argv in IMAGES:
            subprocess.run(argv(os.path.join(OUT, name), tree), check=True, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=120)
        deep = os.path.join(work, "deep")
        os.makedirs(deep)
        build_deep_tree(deep)
        name, argv = DEEP_IMAGE
        subprocess.run(argv(os.path.join(OUT, name), deep), check=True, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=120)
        with open(os.path.join(OUT, "versions.txt"), "w") as f:
            for tool in ("xorriso", "genisoimage"):
                r = subprocess.run([tool, "-version"], capture_output=True, text=True)
                f.write(tool + ": " + (r.stdout + r.stderr).strip().splitlines()[0] + "\n")
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()
