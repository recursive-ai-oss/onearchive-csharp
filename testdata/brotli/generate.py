#!/usr/bin/env python3
"""Regenerates the brotli decoder test vectors in this directory.

Usage: python3 generate.py [path-to-brotli]

The inputs come from fixed pseudo-random generators (and, for the dictionary vectors, from the dictionary and the word
transformations that tools/brotli/generate-tables.py transcribes from RFC 7932), so running this again with the same tool
writes identical files. Every vector is made by the brotli command line tool, run as a black box (its version is in the
manifest header). manifest.tsv records each vector's output size and SHA-256.

Two kinds of vector are written by hand, from the format description in RFC 7932 sections 9.2 and 11.1: a stream of
uncompressed meta-blocks and a stream with metadata meta-blocks. The tool must decompress them without complaint.
"""
import hashlib
import importlib.util
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
TOOL = sys.argv[1] if len(sys.argv) > 1 else "brotli"
OUT = os.path.join(HERE, "vectors")
MASK = (1 << 64) - 1

spec = importlib.util.spec_from_file_location("tables", os.path.join(ROOT, "tools", "brotli", "generate-tables.py"))
tables = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tables)


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
         "you were their one all we can her has there been if more when will would who so no archive block context window "
         "symbol literal command distance prefix code dictionary transform brotli").split()


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


def runs(seed, size):
    r = SplitMix(seed)
    out = bytearray()
    n = 1
    while len(out) < size:
        out += bytes([r.next() % 5 + 97]) * n
        n = n % 300 + 1
    return bytes(out[:size])


def structured(seed, size):
    # Records of little-endian integers with slowly changing fields: exercises the Signed and LSB6 context modes and
    # non-text literal statistics.
    r = SplitMix(seed)
    out = bytearray()
    a = 1000
    while len(out) < size:
        a += r.next() % 7 - 3
        out += a.to_bytes(4, "little", signed=True) + (r.next() % 16).to_bytes(2, "little") + bytes([0, 0])
    return bytes(out[:size])


def utf8_text(seed, size):
    r = SplitMix(seed)
    pieces = ["café ", "naïve ", "日本語 ", "привет ", "мир ", "the ", "über ", "データ ", "word ", "\n"]
    out = bytearray()
    while len(out) < size:
        out += pieces[r.next() % len(pieces)].encode()
    return bytes(out[:size])


def dictionary_words(seed, size):
    # Dictionary words under many transformations, with filler between them, so that a good encoder refers to the
    # static dictionary with every transform id.
    words_data = tables.dictionary(RFC_LINES)
    offsets = [0] * 26
    for length in range(25):
        offsets[length + 1] = offsets[length] + (length * (1 << tables.NDBITS[length]) if length >= 4 else 0)
    blob = tables.transforms(RFC_LINES)
    entries = []
    pos = 0
    for _ in range(121):
        p = blob.index(b"\0", pos)
        prefix = blob[pos:p]
        tid = blob[p + 1]
        s = blob.index(b"\0", p + 2)
        suffix = blob[p + 2:s]
        pos = s + 1
        entries.append((prefix, tid, suffix))

    def ferment_at(w, i):
        b = w[i]
        if b < 192:
            if 97 <= b <= 122:
                w[i] = b ^ 32
            return 1
        if b < 224:
            if i + 1 < len(w):
                w[i + 1] ^= 32
            return 2
        if i + 2 < len(w):
            w[i + 2] ^= 5
        return 3

    def transform(word, entry):
        prefix, tid, suffix = entry
        w = bytearray(word)
        if tid == 1 and w:
            ferment_at(w, 0)
        elif tid == 2:
            i = 0
            while i < len(w):
                i += ferment_at(w, i)
        elif 3 <= tid <= 11:
            w = w[tid - 2:] if len(w) > tid - 2 else bytearray()
        elif tid >= 12:
            k = tid - 11
            w = w[:len(w) - k] if len(w) > k else bytearray()
        return prefix + bytes(w) + suffix

    r = SplitMix(seed)
    out = bytearray()
    while len(out) < size:
        length = 4 + r.next() % 21
        count = 1 << tables.NDBITS[length]
        index = r.next() % count
        word = words_data[offsets[length] + index * length: offsets[length] + (index + 1) * length]
        out += transform(word, entries[r.next() % 121])
        out += text(r.next(), 1 + r.next() % 12)
    return bytes(out[:size])


def brotli(data, quality, lgwin):
    return subprocess.run([TOOL, "-q", str(quality), "-w", str(lgwin), "-c"], input=data, capture_output=True, check=True).stdout


def tool_version():
    return subprocess.run([TOOL, "--version"], capture_output=True, text=True).stdout.strip()


# Hand-assembled streams (RFC 7932 sections 9.2 and 11.1).
def bits_to_bytes(fields):
    # fields: (value, nbits) in stream order, least significant bit first.
    acc = 0
    n = 0
    for value, nbits in fields:
        acc |= value << n
        n += nbits
    return acc.to_bytes((n + 7) // 8, "little")


def uncompressed_stream(chunks, wbits_fields=((0, 1),)):
    # WBITS = 16 (one 0 bit), then for each chunk: ISLAST=0, MNIBBLES, MLEN-1, ISUNCOMPRESSED=1, pad to a byte, the bytes.
    out = bytearray()
    bitbuf = list(wbits_fields)
    first = True
    for chunk in chunks:
        fields = list(bitbuf) if first else []
        first = False
        nib = 4 if len(chunk) <= 65536 else 5
        fields += [(0, 1), (nib - 4, 2), (len(chunk) - 1, 4 * nib), (1, 1)]
        out += bits_to_bytes(fields)
        out += chunk
    # Final empty last meta-block: ISLAST=1, ISLASTEMPTY=1.
    out += bits_to_bytes([(1, 1), (1, 1)])
    return bytes(out)


def metadata_stream(payload_chunks, data):
    # WBITS=16, a metadata meta-block carrying each payload, then the data as one uncompressed meta-block, a last empty one.
    out = bytearray()
    fields = [(0, 1)]
    for payload in payload_chunks:
        nbytes = 1 if len(payload) <= 256 else 2
        fields += [(0, 1), (3, 2), (0, 1), (nbytes, 2), (len(payload) - 1, 8 * nbytes)]
        out += bits_to_bytes(fields)
        out += payload
        fields = []
    fields += [(0, 1), (0, 2), (len(data) - 1, 16), (1, 1)]
    out += bits_to_bytes(fields)
    out += data
    out += bits_to_bytes([(1, 1), (1, 1)])
    return bytes(out)


RFC_LINES = None


def main():
    global RFC_LINES
    rfc = os.environ.get("RFC7932")  # path to rfc7932.txt; fetched from the pinned URL when unset
    RFC_LINES = tables.load_rfc(rfc)
    os.makedirs(OUT, exist_ok=True)
    rows = []

    def add(name, original, packed, made_by):
        with open(os.path.join(OUT, name + ".br"), "wb") as f:
            f.write(packed)
        rows.append((name, len(original), hashlib.sha256(original).hexdigest(), len(packed), made_by))

    def tool(name, data, quality, lgwin):
        add(name, data, brotli(data, quality, lgwin), f"brotli -q{quality} -w{lgwin}")

    t20 = text(1, 20000)
    for q in range(12):
        tool(f"text20k-q{q}", t20, q, 22)
    tool("empty-q5", b"", 5, 22)
    tool("onebyte-q5", b"x", 5, 22)
    tool("twobytes-q1", b"ab", 1, 22)
    tool("zeros100k-q5", b"\0" * 100000, 5, 22)
    t60 = text(2, 60000)
    for w in (10, 16, 22, 24):
        for q in (5, 11):
            tool(f"text60k-q{q}-w{w}", t60, q, w)
    r40 = random_bytes(3, 40000)
    for q in (1, 5, 11):
        tool(f"random40k-q{q}", r40, q, 22)
    runs50 = runs(4, 50000)
    for q in (2, 9, 11):
        tool(f"runs50k-q{q}", runs50, q, 22)
    st = structured(5, 60000)
    for q in (4, 9, 11):
        tool(f"structured60k-q{q}", st, q, 22)
    u8 = utf8_text(6, 40000)
    for q in (5, 9, 11):
        tool(f"utf8-40k-q{q}", u8, q, 22)
    dw = dictionary_words(7, 60000)
    for q in (5, 9, 11):
        tool(f"dictwords60k-q{q}", dw, q, 22)
    # Larger than a meta-block of the tool's choosing, so that several meta-blocks and block switches occur.
    mixed = text(8, 150000) + random_bytes(9, 30000) + structured(10, 100000) + utf8_text(11, 100000) + text(12, 150000)
    for q in (5, 11):
        tool(f"mixed530k-q{q}", mixed, q, 22)
    # Hand-assembled streams the tool does not write.
    pieces = [text(13, 5000), random_bytes(14, 300), text(15, 70000)]
    add("uncompressed-3chunks", b"".join(pieces), uncompressed_stream(pieces), "assembled by hand")
    meta = metadata_stream([b"hello metadata", bytes(range(256)) * 2], text(16, 3000))
    add("metadata-then-data", text(16, 3000), meta, "assembled by hand")
    version = tool_version()
    with open(os.path.join(HERE, "manifest.tsv"), "w", newline="\n") as f:
        f.write(f"# brotli {version}\n# name\tsize\tsha256\tcompressed\tmade by\n")
        for row in rows:
            f.write("\t".join(str(v) for v in row) + "\n")
    # The tool must accept the hand-assembled streams.
    for name in ("uncompressed-3chunks", "metadata-then-data"):
        data = open(os.path.join(OUT, name + ".br"), "rb").read()
        decoded = subprocess.run([TOOL, "-d", "-c"], input=data, capture_output=True, check=True).stdout
        expected = next(r for r in rows if r[0] == name)
        assert hashlib.sha256(decoded).hexdigest() == expected[2], name
    print(f"wrote {len(rows)} vectors, {sum(r[3] for r in rows)} bytes")


if __name__ == "__main__":
    main()
