#!/usr/bin/env python3
"""Writes the 7z reader's test archives with the reference 7-Zip as a black box.

Usage (from the repository root): python3 testdata/7z/generate.py [path-to-7z-binary]

The source tree is deterministic (the same bytes and times every run; the C# tests rebuild the expected content with
the same formulas, see tests/OneArchive.Tests/SevenZip/SevenZipFixtures.cs). The archives are written to
testdata/7z/vectors/. The tool and its version are recorded in testdata/7z/vectors/TOOL.txt.
"""
import os, shutil, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "vectors")
TOOL = sys.argv[1] if len(sys.argv) > 1 else shutil.which("7zz") or shutil.which("7z")
MTIME = 1700000000  # 2023-11-14T22:13:20Z


def lcg_bytes(seed, n):
    # x' = x * 1103515245 + 12345 (mod 2^31); output bits 16..23.
    x = seed
    out = bytearray(n)
    for i in range(n):
        x = (x * 1103515245 + 12345) & 0x7FFFFFFF
        out[i] = (x >> 16) & 0xFF
    return bytes(out)


def source_files():
    files = {
        "a.txt": b"hello 7z\n",
        "empty.txt": b"",
        "dir/b.bin": lcg_bytes(7, 20000),
        "dir/sub/c.txt": b"the quick brown fox jumps over the lazy dog\n" * 800,
        "\u00fcn\u00ef/\u65e5\u672c\u8a9e.txt": "unicode name \u2603\n".encode("utf-8"),
        "z.txt": b"last file\n",
    }
    for i in range(120):
        files["many/f%04d.txt" % i] = ("file number %d\n" % i).encode() * (i % 7 + 1)
    return files


def build_tree(root):
    for rel, data in source_files().items():
        p = os.path.join(root, rel)
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "wb") as f:
            f.write(data)
    os.makedirs(os.path.join(root, "emptydir"), exist_ok=True)
    os.makedirs(os.path.join(root, "dir", "emptysub"), exist_ok=True)
    os.chmod(os.path.join(root, "a.txt"), 0o640)
    os.chmod(os.path.join(root, "dir"), 0o750)
    os.symlink("a.txt", os.path.join(root, "link-to-a"))
    for dp, dn, fn in os.walk(root):
        for n in dn + fn:
            os.utime(os.path.join(dp, n), (MTIME, MTIME), follow_symlinks=False)
        os.utime(dp, (MTIME, MTIME))


# name -> extra 7z switches. -mtc=on -mta=on adds creation and access times.
CONFIGS = {
    "copy-solid": ["-m0=Copy", "-ms=on", "-mhc=off"],
    "copy-nonsolid": ["-m0=Copy", "-ms=off", "-mhc=off"],
    "copy-multifolder": ["-m0=Copy", "-ms=e", "-mhc=off"],
    "copy-times": ["-m0=Copy", "-ms=off", "-mhc=off", "-mtc=on", "-mta=on"],
    "deflate-solid": ["-m0=Deflate", "-ms=on", "-mhc=off"],
    "deflate-nonsolid": ["-m0=Deflate", "-ms=off", "-mhc=off"],
    "bzip2-solid": ["-m0=BZip2", "-ms=on", "-mhc=off"],
    "bzip2-nonsolid": ["-m0=BZip2", "-ms=off", "-mhc=off"],
    "copy-chain": ["-m0=Copy", "-m1=Copy", "-ms=on", "-mhc=off"],
}
# Archives that need the LZMA-family decoders (added to the list once they exist).
CONFIGS_LZMA = {
    "default": [],
    "lzma-solid": ["-m0=LZMA", "-ms=on", "-mhc=off"],
    "lzma-header": ["-m0=LZMA", "-ms=on", "-mhc=on"],
    "lzma2-nonsolid": ["-m0=LZMA2", "-ms=off"],
    "bcj-lzma": ["-m0=BCJ", "-m1=LZMA", "-ms=on", "-mhc=off"],
    "delta-lzma2": ["-m0=Delta:4", "-m1=LZMA2", "-ms=on", "-mhc=off"],
    "arm64-lzma2": ["-mf=ARM64", "-m1=LZMA2", "-ms=on", "-mhc=off"],
}


def main():
    configs = dict(CONFIGS)
    if os.environ.get("SEVENZIP_LZMA") == "1":
        configs.update(CONFIGS_LZMA)
    os.makedirs(OUT, exist_ok=True)
    version = subprocess.run([TOOL], capture_output=True, text=True).stdout.splitlines()
    with open(os.path.join(OUT, "TOOL.txt"), "w") as f:
        f.write(next((l for l in version if "7-Zip" in l), "unknown").strip() + "\n")
    with tempfile.TemporaryDirectory() as tmp:
        tree = os.path.join(tmp, "tree")
        os.makedirs(tree)
        build_tree(tree)
        for name, switches in sorted(configs.items()):
            dest = os.path.join(OUT, name + ".7z")
            if os.path.exists(dest):
                os.remove(dest)
            cmd = [os.path.abspath(TOOL), "a", "-t7z", "-snl", "-bso0", "-bsp0", "-mmt=1", "-mx=5"] + switches + [dest, "."]
            r = subprocess.run(cmd, cwd=tree, capture_output=True, text=True)
            if r.returncode != 0:
                print("FAILED", name, r.stdout[-300:], r.stderr[-300:])
                sys.exit(1)
            print(name, os.path.getsize(dest))
        # Zip archives with LZMA (method 14) and xz (method 95) entries.
        for name, mm in ("zip-lzma", "LZMA"), ("zip-lzma-eos", "LZMA:eos"), ("zip-xz", "xz"):
            dest = os.path.join(OUT, name + ".zip")
            if os.path.exists(dest):
                os.remove(dest)
            r = subprocess.run([os.path.abspath(TOOL), "a", "-tzip", "-snl", "-bso0", "-bsp0", "-mmt=1", "-mm=" + mm, dest, "."], cwd=tree, capture_output=True, text=True)
            if r.returncode != 0:
                print("FAILED", name, r.stdout[-300:], r.stderr[-300:])
                sys.exit(1)
            print(name, os.path.getsize(dest))
        # A BCJ2 folder (four coders, four packed streams) from the first 60000 bytes of an x86-64 executable named by
        # SEVENZIP_EXE; the reader tests only look at its coder graph, so any machine code works.
        exe = os.environ.get("SEVENZIP_EXE")
        if exe:
            bcj = os.path.join(tmp, "b")
            os.makedirs(bcj)
            with open(exe, "rb") as f, open(os.path.join(bcj, "exe.bin"), "wb") as g:
                g.write(f.read(60000))
            dest = os.path.join(OUT, "bcj2-graph.7z")
            if os.path.exists(dest):
                os.remove(dest)
            subprocess.run([os.path.abspath(TOOL), "a", "-t7z", "-bso0", "-bsp0", "-m0=BCJ2", "-m1=LZMA:d1m", "-m2=LZMA:d1m", "-m3=LZMA:d64k", "-mb0:1", "-mb0s1:2", "-mb0s2:3", "-mhc=off", dest, "exe.bin"], cwd=bcj, check=True)
            print("bcj2-graph", os.path.getsize(dest))
        # An empty archive and a lone empty file.
        empty = os.path.join(tmp, "e")
        os.makedirs(empty)
        open(os.path.join(empty, "only-empty.txt"), "wb").close()
        dest = os.path.join(OUT, "one-empty-file.7z")
        if os.path.exists(dest):
            os.remove(dest)
        subprocess.run([os.path.abspath(TOOL), "a", "-t7z", "-bso0", "-bsp0", "-m0=Copy", "-mhc=off", dest, "only-empty.txt"], cwd=empty, check=True)
        print("one-empty-file", os.path.getsize(dest))


main()
