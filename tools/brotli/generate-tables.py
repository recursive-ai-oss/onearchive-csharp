#!/usr/bin/env python3
"""Transcribes the brotli static tables from RFC 7932 into C# source.

Reads the RFC text (fetched from the pinned URL and checked against the pinned SHA-256 unless a local
copy is given) and writes, into src/OneCompress/Brotli/:

  BrotliDictionaryData.g.cs   RFC 7932 Appendix A: the 122,784-byte static dictionary and NDBITS
  BrotliTransformData.g.cs    RFC 7932 Appendix B: the 121 word transformations
  BrotliContextTables.g.cs    RFC 7932 section 7.1: the literal context lookup tables Lut0, Lut1, Lut2

Every value is checked against the lengths and CRC-32 values the RFC states for it. Nothing is read from any
implementation. Usage:

  tools/brotli/generate-tables.py [--rfc path/to/rfc7932.txt] [--out src/OneCompress/Brotli]
"""
import argparse
import hashlib
import os
import re
import sys
import urllib.request
import zlib

RFC_URL = "https://www.rfc-editor.org/rfc/rfc7932.txt"
RFC_SHA256 = "394abd1879e016f4eac89512c2acaf92f7b2d67feb8ab58b6b118c213be07146"

DICT_SIZE = 122784
DICT_CRC = 0x5136CB04
TRANSFORM_BLOB_SIZE = 648
TRANSFORM_CRC = 0x3D965F81
LUT_CRC = {"Lut0": 0x8E91EFB7, "Lut1": 0xD01A32F4, "Lut2": 0x0DD7A0D6}
NDBITS = [0, 0, 0, 0, 10, 10, 11, 11, 10, 10, 10, 10, 10, 9, 9, 8, 7, 7, 8, 7, 7, 6, 6, 5, 5]


def crc32(data: bytes) -> int:
    # RFC 7932 Appendix C is the standard reflected CRC-32.
    return zlib.crc32(data) & 0xFFFFFFFF


def load_rfc(path):
    if path:
        text = open(path, "rb").read()
    else:
        text = urllib.request.urlopen(RFC_URL).read()
    digest = hashlib.sha256(text).hexdigest()
    if digest != RFC_SHA256:
        sys.exit(f"RFC text has SHA-256 {digest}, expected {RFC_SHA256}")
    return text.decode("ascii").replace("\r\n", "\n").split("\n")


def section(lines, start_marker, end_marker):
    start = next(i for i, l in enumerate(lines) if start_marker in l)
    end = next(i for i in range(start + 1, len(lines)) if end_marker in lines[i])
    return lines[start + 1 : end]


def dictionary(lines):
    body = section(lines, "The hexadecimal form of the DICT array", "The number of words for each length")
    hexdata = "".join(m.group(1) for l in body if (m := re.fullmatch(r" {6}([0-9a-f]+)", l)))
    data = bytes.fromhex(hexdata)
    if len(data) != DICT_SIZE:
        sys.exit(f"dictionary has {len(data)} bytes, RFC says {DICT_SIZE}")
    if crc32(data) != DICT_CRC:
        sys.exit(f"dictionary CRC-32 is {crc32(data):#010x}, RFC says {DICT_CRC:#010x}")
    # RFC 7932 section 8: DICTSIZE = DOFFSET[24] + 24 * NWORDS[24].
    size = sum(length * (1 << NDBITS[length]) for length in range(4, 25))
    if size != DICT_SIZE:
        sys.exit(f"NDBITS imply {size} dictionary bytes")
    return data


def ndbits_from_text(lines):
    start = next(i for i, l in enumerate(lines) if l.strip().startswith("NDBITS :="))
    text = " ".join(lines[start : start + 4])
    values = [int(v) for v in re.findall(r"\d+", text.split(":=")[1])[:25]]
    if values != NDBITS:
        sys.exit("NDBITS in the RFC text differ from the built-in copy")


def unescape(literal):
    # The strings in Appendix B are C string literals.
    s = literal[1:-1]
    out = bytearray()
    i = 0
    while i < len(s):
        c = s[i]
        if c != "\\":
            out.append(ord(c))
            i += 1
            continue
        n = s[i + 1]
        if n == "x":
            out.append(int(s[i + 2 : i + 4], 16))
            i += 4
        else:
            out.append({"n": 10, "t": 9, '"': 34, "\\": 92, "'": 39}[n])
            i += 2
    return bytes(out)


def transform_id(name):
    if name == "Identity":
        return 0
    if name == "FermentFirst":
        return 1
    if name == "FermentAll":
        return 2
    m = re.fullmatch(r"OmitFirst([1-9])", name)
    if m:
        return 2 + int(m.group(1))
    m = re.fullmatch(r"OmitLast([1-9])", name)
    if m:
        return 11 + int(m.group(1))
    sys.exit(f"unknown elementary transform {name}")


def transforms(lines):
    start = next(i for i, l in enumerate(lines) if l.startswith("Appendix B."))
    end = next(i for i in range(start, len(lines)) if lines[i].startswith("Appendix C."))
    pat = re.compile(r'^\s+(\d+)\s+("(?:[^"\\]|\\.)*")\s+(\w+)\s+("(?:[^"\\]|\\.)*")\s*$')
    rows = []
    for l in lines[start:end]:
        m = pat.match(l)
        if m:
            rows.append((int(m.group(1)), unescape(m.group(2)), transform_id(m.group(3)), unescape(m.group(4))))
    if [r[0] for r in rows] != list(range(121)):
        sys.exit(f"found {len(rows)} transforms, expected ids 0..120 in order")
    blob = b"".join(p + b"\0" + bytes([t]) + s + b"\0" for _, p, t, s in rows)
    if len(blob) != TRANSFORM_BLOB_SIZE:
        sys.exit(f"transform blob has {len(blob)} bytes, RFC says {TRANSFORM_BLOB_SIZE}")
    if crc32(blob) != TRANSFORM_CRC:
        sys.exit(f"transform CRC-32 is {crc32(blob):#010x}, RFC says {TRANSFORM_CRC:#010x}")
    return blob


def luts(lines):
    out = {}
    for name in ("Lut0", "Lut1", "Lut2"):
        start = next(i for i, l in enumerate(lines) if l.strip() == f"{name} :=")
        values = []
        i = start + 1
        while len(values) < 256:
            l = lines[i]
            if re.fullmatch(r"\s+[0-9, ]+", l):
                values += [int(v) for v in re.findall(r"\d+", l)]
            i += 1
        if len(values) != 256:
            sys.exit(f"{name} has {len(values)} values")
        data = bytes(values)
        if crc32(data) != LUT_CRC[name]:
            sys.exit(f"{name} CRC-32 is {crc32(data):#010x}, RFC says {LUT_CRC[name]:#010x}")
        out[name] = data
    return out


HEADER = """// <auto-generated>
// Generated by tools/brotli/generate-tables.py from RFC 7932 (SHA-256 of the RFC text: {sha}).
// Do not edit; rerun the generator. {what}
// </auto-generated>
"""


def byte_rows(data, per_line=32, indent="        "):
    rows = []
    for i in range(0, len(data), per_line):
        rows.append(indent + ", ".join(f"0x{b:02x}" for b in data[i : i + per_line]) + ",")
    return "\n".join(rows)


def write(path, text):
    with open(path, "w", newline="\n") as f:
        f.write(text)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rfc")
    ap.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "..", "..", "src", "OneCompress", "Brotli"))
    args = ap.parse_args()
    lines = load_rfc(args.rfc)
    ndbits_from_text(lines)
    data = dictionary(lines)
    blob = transforms(lines)
    lut = luts(lines)
    out = args.out

    write(
        os.path.join(out, "BrotliDictionaryData.g.cs"),
        HEADER.format(sha=RFC_SHA256, what="RFC 7932 Appendix A.")
        + f"""
namespace RecursiveAI.OneCompress.Brotli;

/// <summary>The bytes of the brotli static dictionary (RFC 7932 Appendix A), {DICT_SIZE:,} bytes with CRC-32 0x{DICT_CRC:08x}.</summary>
internal static class BrotliDictionaryData
{{
    /// <summary>The number of address bits (log2 of the word count) for each word length 0..24 (RFC 7932 section 8).</summary>
    internal static ReadOnlySpan<byte> NDBits => new byte[] {{ {", ".join(str(v) for v in NDBITS)} }};

    /// <summary>The dictionary words, shortest length first, each length's words one after the other.</summary>
    internal static ReadOnlySpan<byte> Words => new byte[]
    {{
{byte_rows(data)}
    }};
}}
""",
    )
    write(
        os.path.join(out, "BrotliTransformData.g.cs"),
        HEADER.format(sha=RFC_SHA256, what="RFC 7932 Appendix B.")
        + f"""
namespace RecursiveAI.OneCompress.Brotli;

/// <summary>
/// The 121 word transformations of RFC 7932 Appendix B in the layout the RFC uses for its check value: for each transform the prefix
/// bytes and a zero byte, the elementary transform id, then the suffix bytes and a zero byte. {TRANSFORM_BLOB_SIZE} bytes, CRC-32 0x{TRANSFORM_CRC:08x}.
/// The id is 0 for Identity, 1 for FermentFirst, 2 for FermentAll, 3 to 11 for OmitFirst1 to OmitFirst9 and 12 to 20 for OmitLast1 to OmitLast9.
/// </summary>
internal static class BrotliTransformData
{{
    internal static ReadOnlySpan<byte> Blob => new byte[]
    {{
{byte_rows(blob)}
    }};
}}
""",
    )
    write(
        os.path.join(out, "BrotliContextTables.g.cs"),
        HEADER.format(sha=RFC_SHA256, what="RFC 7932 section 7.1.")
        + f"""
namespace RecursiveAI.OneCompress.Brotli;

/// <summary>The lookup tables Lut0, Lut1 and Lut2 of RFC 7932 section 7.1, used by the UTF8 and Signed literal context modes.</summary>
internal static class BrotliContextTables
{{
    /// <summary>Lut0, 256 bytes, CRC-32 0x{LUT_CRC['Lut0']:08x}.</summary>
    internal static ReadOnlySpan<byte> Lut0 => new byte[]
    {{
{byte_rows(lut['Lut0'], 16)}
    }};

    /// <summary>Lut1, 256 bytes, CRC-32 0x{LUT_CRC['Lut1']:08x}.</summary>
    internal static ReadOnlySpan<byte> Lut1 => new byte[]
    {{
{byte_rows(lut['Lut1'], 16)}
    }};

    /// <summary>Lut2, 256 bytes, CRC-32 0x{LUT_CRC['Lut2']:08x}.</summary>
    internal static ReadOnlySpan<byte> Lut2 => new byte[]
    {{
{byte_rows(lut['Lut2'], 16)}
    }};
}}
""",
    )
    print("wrote brotli tables to", os.path.abspath(out))


if __name__ == "__main__":
    main()
