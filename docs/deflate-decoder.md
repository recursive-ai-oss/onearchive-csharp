# Deflate, zlib and gzip decoder

What the decoder accepts, rejects and bounds. The format sources are RFC 1951 (Deflate), RFC 1950 (zlib) and RFC 1952
(gzip); section names below refer to them. A later port should reproduce these choices from this page alone.

## Layers

| Tier | Types | Use |
|---|---|---|
| 1 | `Deflate.Decompress`, `ZLib.Decompress`, `GZip.Decompress` and their `TryDecompress` | one call, whole input |
| 2 | `DeflateDecoder` (`DeflateFormat` Raw, ZLib or GZip), `DeflateCodecStream` (read mode) | any split of input and output; reuse across units |
| 3 | `DeflateHuffmanTable`, `DeflateBitReader`, `DeflateTables` | table building, bit input, the constant tables of RFC 1951 |

Tier 1 calls tier 2, tier 2 calls tier 3. The stream is `DeflateCodecStream` because `DeflateStream` is the BCL's name.

## Units and `Done`

A unit is one raw Deflate stream, one zlib stream or one gzip member. `Decompress` returns `Done` when a unit is complete and
fully written, and then stops; the next call starts a new unit (a gzip file with several members is decoded by calling again with
the bytes not consumed; tier 1 and the stream do this). Each unit starts with an empty window. Tier 1 and the stream refuse bytes
after the end of a raw or zlib stream unless `AllowTrailingData` is set; for gzip, bytes after a member are the next member
(trailing bytes that do not start with 1F 8B are ignored under `AllowTrailingData` and an error otherwise).

A call that returns `NeedMoreData` has consumed all of its input: bits and partial header fields are kept inside the decoder,
so the caller never holds input back. `consumed` and `written` are set even when the call throws.

## Blocks (RFC 1951)

- BTYPE 3 is an error. Stored blocks require NLEN to be the one's complement of LEN. The bits up to the byte boundary after a
  block header, and after the final block, are padding and are ignored.
- Dynamic blocks (3.2.7): **HLIT above 286 and HDIST above 30 are errors** (the fields can encode 288 and 32; zlib refuses
  them and no encoder writes them). The code-length code must be a complete code (or have no symbols, in which case the first
  code-length symbol fails). Repeat code 16 with no previous length, and any repeat running past HLIT + HDIST, are errors. The
  literal/length code must give the end-of-block symbol (256) a length.
- Huffman codes (`DeflateHuffmanTable.TryBuild`): over-subscribed codes are errors; incomplete codes are errors **except** a
  single code of length 1 and a code with no symbols (a block with no distance codes is legal while it has no matches).
  Code lengths above 15 cannot occur (3.2.7) and are refused by the public builder. Literal/length symbols 286 and 287 and
  distance symbols 30 and 31 may take part in a code but are errors when decoded.
- A match distance reaching before the start of the unit's output (or its preset dictionary) is an error.

## zlib (RFC 1950)

- CM must be 8, CINFO at most 7 (window up to 32 KiB), and CMF * 256 + FLG a multiple of 31.
- FDICT: the stream names its dictionary by the Adler-32 of the dictionary bytes. The options' `Dictionary` is used when its
  Adler-32 matches; no dictionary, or a different one, raises `CompressionDictionaryException` with the ID. The last 32 KiB of
  the dictionary are the history.
- The trailing Adler-32 (big-endian) is checked against the output unless `VerifyChecksum` is false.

## gzip (RFC 1952)

- ID1 ID2 = 1F 8B, CM = 8, and **reserved flag bits (0xE0) must be zero**. FEXTRA, FNAME and FCOMMENT are read in bulk. FNAME and
  FCOMMENT are kept as bytes (ISO 8859-1 by the RFC) without their zero terminator; each field is limited to `MaxHeaderFieldSize`
  bytes (default 65535; `CompressionLimitException`). FHCRC is the low 16 bits of the CRC-32 of all header bytes before it and is
  checked unless `VerifyChecksum` is false.
- The trailer's CRC-32 and ISIZE (length modulo 2^32) are checked unless `VerifyChecksum` is false.
- The header fields are available through `DeflateDecoder.CurrentGZipHeader` (and the stream's) once the header is consumed.

## Limits

- `MaxOutputSize` (default 1 GiB) counts output since construction or `Reset`. The decoder is given a destination shortened to
  what the limit still allows and throws `CompressionLimitException` the moment it needs to write one more byte; nothing is
  written past the limit. Tier 1 grows its output buffer by doubling from a size taken from the input (the gzip ISIZE only when it is at most
  1100 times the input) and never allocates more than the limit lets through.
- Memory: a decoder owns at most a 64 KiB window, a few hundred bytes of tables and the gzip header fields; all of it is allocated
  on first need and reused. No allocation per call in steady state.
- No recursion; work is linear in input and output.

## Errors

`CompressionFormatException` with `Format` "deflate", "zlib" or "gzip"; `Offset` is the byte offset in the compressed input
of the position when the problem was found (for truncation, the end of the input); `IsTruncated` is set when the input ended inside
a unit. After an exception the decoder refuses calls until `Reset`.

## Window and speed notes

The decoder writes into the caller's buffer and keeps the last 32 KiB of each call's output in a window (a copy of at most 32 KiB
per call) so matches can reach into earlier calls. The hot loop (`DeflateFastLoop`) runs while 16 input and 280 output bytes remain:
one 8-byte load tops the bit buffer up to at least 56 bits per symbol, enough for a literal/length code (15 bits), its extra
bits (5), a distance code (15) and its extra bits (13) together. Entries of the lookup tables carry the length or distance base
and extra-bit count, so a symbol is one or two table reads. The low byte of an entry is the code length plus its extra bits, so one
shift drops both and the extra bits are read from the bits before the shift (the code length sits in bits 8 to 11). Tables have a
10-bit (literal/length), 8-bit (distance) or 7-bit (code-length) first level and second-level tables for longer codes.

Choices that make the loop fast without changing what it decodes:

- **The next symbol's refill and table read are issued before a match is copied**, since they do not depend on the copy; the loop
  therefore needs 16 input bytes in hand (two refills) rather than 8.
- **The literal/length table keeps a second copy for the loop** (`FastEntries`). Its kind bits are one flag per kind (literal 8,
  length 4, link 2, end of block 1 in bits 12 to 15), so each test is one bit test; the plain table and `Lookup` keep
  `DeflateSymbolKind`. In the copy, a first-level literal entry whose code is followed, within the same ten index bits, by a complete
  literal code stands for both literals (one two-byte store). After the first literal of a refill the loop decodes up to two more
  first-level literals before refilling. Making these follow-on literals branch-free (masking a non-literal entry to nothing) was
  measured slower on binary data (silesia) and faster only by 1% on text, so they are ordinary branches.
- **Matches copy 16 bytes at a time** when the distance is at least 16 (two unconditional 16-byte copies, a loop beyond 32 bytes), eight
  at a time for distances 8 to 15, and by pattern for shorter distances. The output margin is the longest match plus the width of
  the last copy that may run past it.
- **Table building** visits only the first-level prefixes that have codes longer than the first level (a list is kept while sizing),
  and fills unused entries only for an incomplete code, since a complete code leaves none.
- A first-level size of 9 or 11 bits for the literal/length table was measured slower on binary data or on all data respectively; 10 stays.

A match that reaches back before the call's output ends the loop, and the caller copies it from the window and re-enters, so the loop makes no
calls. None of this changes what a caller can observe, since the slow path and `Lookup` read the plain table.

## Tests and data

`testdata/deflate/generate.py` writes the vectors (zlib through Python, gzip and pigz) with a manifest of output hashes;
the bit-flip, prefix and limit tests, the BCL cross-checks and the optional interop tests (`ONEARCHIVE_INTEROP=1`) live in
`tests/OneCompress.Tests/Deflate/`.
