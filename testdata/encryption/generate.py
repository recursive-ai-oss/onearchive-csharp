#!/usr/bin/env python3
"""Writes the encryption test archives with reference tools run as black boxes (7-Zip and Info-ZIP zip).

Usage (from the repository root): python3 testdata/encryption/generate.py [path-to-7z-binary]

The source files are deterministic; tests/OneArchive.Tests/EncryptionFixtures.cs rebuilds the same bytes.
Password for every archive: fake-password-1. Tool versions go to vectors/TOOL.txt.
"""
import os, shutil, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "vectors")
SEVENZ = sys.argv[1] if len(sys.argv) > 1 else shutil.which("7zz") or shutil.which("7z")
ZIP = shutil.which("zip")
PASSWORD = "fake-password-1"
MTIME = 1700000000


def lcg_bytes(seed, n):
    x = seed
    out = bytearray(n)
    for i in range(n):
        x = (x * 1103515245 + 12345) & 0x7FFFFFFF
        out[i] = (x >> 16) & 0xFF
    return bytes(out)


def files():
    return {
        "text.txt": b"The quick brown fox jumps over the lazy dog.\n" * 40,
        "random.bin": lcg_bytes(7, 3000),
        "empty.txt": b"",
    }


def run(cmd, cwd):
    subprocess.run(cmd, cwd=cwd, check=True, stdout=subprocess.DEVNULL)


def main():
    os.makedirs(OUT, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        for name, data in files().items():
            path = os.path.join(tmp, name)
            with open(path, "wb") as f:
                f.write(data)
            os.utime(path, (MTIME, MTIME))
        names = sorted(files())

        def sz(args, out):
            target = os.path.join(OUT, out)
            if os.path.exists(target):
                os.remove(target)
            run([SEVENZ, "a", "-bd", "-mtc=off", "-mta=off", f"-p{PASSWORD}"] + args + [target] + names, tmp)

        sz(["-tzip", "-mem=AES256"], "zip-aes256.zip")
        sz(["-tzip", "-mem=AES192"], "zip-aes192.zip")
        sz(["-tzip", "-mem=AES128"], "zip-aes128.zip")
        sz(["-tzip", "-mem=ZipCrypto"], "zip-zipcrypto-7z.zip")
        sz(["-tzip", "-mem=AES256", "-mm=Copy"], "zip-aes256-store.zip")
        sz(["-t7z", "-mhe=off"], "7z-aes.7z")
        sz(["-t7z", "-mhe=on"], "7z-aes-headers.7z")
        sz(["-t7z", "-mhe=on", "-ms=off", "-mx0"], "7z-aes-copy-nonsolid.7z")
        target = os.path.join(OUT, "zip-zipcrypto-infozip.zip")
        if os.path.exists(target):
            os.remove(target)
        run([ZIP, "-q", "-X", "-P", PASSWORD, target] + names, tmp)
        target = os.path.join(OUT, "zip-zipcrypto-infozip-stream.zip")
        if os.path.exists(target):
            os.remove(target)
        # Written to a pipe, Info-ZIP sets bit 3 and puts the sizes after the data.
        with open(target, "wb") as f:
            subprocess.run([ZIP, "-q", "-X", "-P", PASSWORD, "-"] + names, cwd=tmp, check=True, stdout=f)
    with open(os.path.join(OUT, "TOOL.txt"), "w") as f:
        f.write(subprocess.run([SEVENZ], capture_output=True, text=True).stdout.splitlines()[1] + "\n")
        f.write(subprocess.run([ZIP, "-v"], capture_output=True, text=True).stdout.splitlines()[1] + "\n")


main()
