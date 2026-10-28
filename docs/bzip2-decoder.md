# bzip2 decoder

`RecursiveAI.OneCompress.BZip2`: decoding of bzip2 data. bzip2 has no official specification. This document records the
format as the decoder reads it, how each point was established, and every choice the format leaves open. A later port
must reproduce the decoder's behaviour from this text.

## Sources and how gaps were closed

Public descriptions of the format (the Wikipedia article's file-format section, the bzip2 manual page), the Burrows–Wheeler
paper (DEC SRC report 124, 1994) and black-box runs of `bzip2` 1.0.8 (and 7-Zip's and pbzip2's encoders for streams
written by other implementations). Two things were not in any description and were measured:

- **The randomisation schedule** (see below): `tools/bzip2/derive-randomization-table.py` recovers it from the tool.
- **Which malformed streams the tool rejects** (see "Strictness"): hand-built streams, written by the test builder in
  `tests/OneCompress.Tests/BZip2/BZip2TestBuilder.cs`, were fed to the tool and the outcomes recorded.

## Stream layout

All fields are most significant bit first. Bytes are not aligned except at the start and end of a stream.

```
stream   = "BZh" level  block*  end-of-stream
level    = ASCII '1'..'9'         block size limit = level * 100000 bytes (after the first run-length stage)
block    = 0x314159265359 (48 bits)  block-crc (32)  randomised (1)  original-row (24)
           symbol-map  table-count (3)  selector-count (15)  selectors  tables  symbols
end      = 0x177245385090 (48 bits)  combined-crc (32)  zero padding to a byte boundary
```

- **Symbol map:** 16 bits, one per group of 16 byte values, then 16 bits for each group present. The set bits, in order, are
  the bytes used in the block (`used`, 1 to 256).
- **Table count** 2 to 6. **Selector count** 1 to 18002. Each selector is a unary number (that many 1 bits, then a 0 bit)
  below the table count, undone with move-to-front over the table numbers `0..tables-1`. Selector `i` names the table for
  symbols `50 i` to `50 i + 49`.
- **Tables:** per table, a 5-bit start length, then for each of the `used + 2` symbols: while the next bit is 1, one more bit
  (0: length + 1, 1: length - 1); a 0 bit ends the symbol. A length must be 1 to 20 whenever it is examined.
- **Symbols:** RUNA = 0, RUNB = 1, 2 to `used` = move-to-front position 1 to `used - 1`, `used + 1` = end of block. A run of RUNA
  and RUNB is a bijective base-2 number, least significant digit first (RUNA = 1, RUNB = 2, so the digits `A B` mean 1 + 2×2 = 5),
  and repeats the front of the move-to-front list that many times. The list starts as the used bytes in increasing order.
- **Codes:** canonical. Codes of the same length are consecutive in symbol order; a length's first code is twice the
  previous length's first code plus its count.
- **After the symbols:** the sequence is the last column of the sorted rotations; the original-row number picks the row that
  is the original block. Inverse transform: `T[count[b]++] = i` for each last-column byte `b` at position `i` (counts are
  prefix sums of byte frequencies); start at `row = T[original-row]` and repeat `n` times: output `L[row]`, `row = T[row]`.
- **First stage run-length coding:** after four equal bytes in a row comes a count byte, 0 to 255, of further copies; the
  byte after the count starts a new run. Runs do not cross blocks.
- **Block CRC:** CRC-32, polynomial 0x04C11DB7, MSB first, initial 0xFFFFFFFF, final complement, over the block's output
  bytes. The check value of `123456789` is 0xFC891918. **Combined CRC:** `c = rotl(c, 1) xor block_crc` over the blocks, from 0.
- **Randomised blocks** (written only by versions before 0.9.5): after the inverse transform, before the first-stage run-length
  decoding, the lowest bit of the bytes at scheduled positions is flipped. The first flip is at position `gap[0] - 2` and each
  next flip comes `gap[(k + 1) mod 512]` bytes after flip `k`. The 512 gaps are in `BZip2RandomizationTable.g.cs`, measured by
  `tools/bzip2/derive-randomization-table.py`: set the flag of a one-block stream of 899 000 bytes in which no byte repeats and
  no two neighbours differ only in the lowest bit, let the tool decode it (it delivers the bytes before it reports the CRC
  error), and read the positions where the output differs. The gaps repeat with period 512 (three full cycles are
  observed). The tool accepts a hand-made randomised stream with correct CRCs (`testdata/bzip2/vectors/randomized20k.bz2`).

## Decoder behaviour (choices)

**Strictness.** The decoder rejects, with `CompressionFormatException` naming the byte offset and what was expected:

| Input | Why |
|---|---|
| a level digit outside '1'..'9'; a wrong signature | format |
| an empty symbol map; fewer than 2 or more than 6 tables; 0 selectors; a selector at or above the table count | format; the tool rejects the same (0 selectors, 1 table) |
| a code length outside 1..20, or lengths that oversubscribe the code space | format |
| a symbol with a length walk of more than 40 steps | choice: an encoder needs at most 19; the tool has no limit. 40 bounds the header size at about 40 KB |
| a code not assigned by the table (lengths whose Kraft sum is below 1 are accepted) | the tool accepts incomplete tables used legitimately; so do we |
| more than `level × 100000` symbols in a block, or a run that would pass it (exactly that many is accepted) | the tool rejects 100 001 symbols at level 1 and accepts 100 000 |
| a block with no symbols; an original row at or above the block length | the tool rejects both |
| a block ending right after four equal bytes (no count byte) | the tool rejects it |
| more than 18002 selectors | **choice, stricter than the tool**: 1.0.8 accepts any count up to 32767 and ignores those past 18002, and its own encoder never writes more than 18000 |
| a block or combined CRC mismatch | format (off with `VerifyChecksum = false`, and then the stored values are still folded into the combined CRC) |

A block with more selectors than it needs is fine. A block that needs more selectors than it has is an error.

**Limits** (`BZip2DecompressionOptions`): `MaxOutputSize` (default 1 GiB) is checked before bytes are written, against the real
output, so a hostile stream (a 900 000-byte block can expand to about 46 MB, and a stream may hold many blocks) stops at the
limit with `CompressionLimitException`; no output is written past it. `VerifyChecksum` default true. `AllowTrailingData`
default false: input after the last stream is an error (the tool ignores it with a warning); when allowed, input that does not
start with `BZh` ends the data.

**Memory.** One 32-bit word per byte of the stream's block size limit (up to 3.6 MB), a 64 KiB staging buffer, a 4 KiB output
chunk and six tables of about 3 KB. All are allocated when first needed and kept across blocks, streams and `Reset`.

**Streaming.** `BZip2Decoder.Decompress` ends each stream with `Done` and consumes exactly its bytes. It reads the symbols
straight from the caller's buffer through a 64-bit accumulator; block headers are parsed from a staging buffer so that a header
split across calls is re-parsed from its start once more input arrives (a header is at most about 40 KB, so feeding one byte at
a time costs time quadratic in the header size, not in the stream size). A symbol is decoded only with 20 bits at hand, which
never stalls a valid stream because at least 80 bits (the next block header or the end marker) follow every block.

**Concatenated streams.** The one-shot and stream APIs decode streams one after the other and join their outputs, as
`cat a.bz2 b.bz2 | bzip2 -d` does; an empty stream (header and end marker only) contributes nothing.

## Tier 3 parts

`BZip2Crc` (block and combined CRC), `BZip2Bwt` (inverse transform: `BuildVector`, `Next`, `Inverse`), `BZip2MoveToFront`
(symbols and selectors, both directions), `BZip2HuffmanTable` (canonical tables with the format's limits, both directions),
`BZip2RunLengthDecoder` (first stage, resumable), `BZip2Randomization` (the schedule). An encoder reuses the CRC, the move-to-front
list, the table codes and the run-length rule.

## Checked against

`bzip2` 1.0.8 at -1 to -9 over the Canterbury files, Silesia and enwik8 (see the test class `BZip2InteropTests` for the
environment variables), 7-Zip's bzip2 encoder at several levels, passes and thread counts, pbzip2 1.1.13 output (several
streams), concatenated streams, runs that exercise the run-length stages (4, 5, 255, 259, 260 bytes; 1 MB and 50 MB of one byte), a randomised
block over a full cycle of the schedule with and without runs, and a bit flip in every byte of a small stream (the tool and the decoder agree
on which are rejected).
