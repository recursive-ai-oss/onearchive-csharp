# Zstandard encoder

What the encoder does where the format leaves a choice. Compressed bytes depend only on the input and the options, so a later
port that follows this text and the format (RFC 8878) writes the same bytes. Source: `src/OneCompress/Zstd/`.

## Layers

| tier | types |
|---|---|
| 1, one call | `Zstd.Compress`, `Zstd.TryCompress`, `Zstd.GetMaxCompressedLength` |
| 2, encoder and stream | `ZstdEncoder` (`CompressFrame`, `Compress` with `isFinalBlock`, `Flush`, `Reset`), `ZstdStream` in write mode, `ZstdCompressionOptions`, `ZstdCompressionDictionary` |
| 3, raw parts | `FseEncodingTable` (normalize, build, describe, price), `HuffmanEncodingTable` (length-limited tree, description, 1 and 4 streams), `ZstdBlockWriter` (literals and sequences section writer, table-mode choice, repeat offsets), `ZstdFrameWriter` (header, checksum, skippable frames), `ZstdMatchFinder` (the seam every strategy plugs into), `ZstdSequenceStore`, `ZstdSequence`, `ZstdRepeatOffsets`, `ZstdCompressionParameters` |

A match finder turns a window into sequences with **real offsets**; the block writer decides which of them are written as
repeat codes (RFC 8878 section 3.1.1.5), updates the repeat offsets, and chooses the entropy modes. A finder is given the offsets
in force at the start of each block so it can look for cheap matches. A block stored raw or as RLE leaves the offsets and the
carried tables untouched, as the decoder sees it. A frame writer can take blocks from any source between a header and a checksum.

## Levels

Level numbers and meanings follow the `zstd` command line tool. Level 0 means 3. Levels 1 to 4 and every negative level down to
-131072 are implemented; the negative level is the acceleration (the fast strategy's target length). Higher levels throw
`ArgumentOutOfRangeException`.

Parameters are looked up by the source size class, then shrunk to what the source can use. The size used is the content size plus
the dictionary's content size; it is unknown for a streaming encoder that was not told the content size, and then the largest class
applies.

| class (size) | level | window log | chain log | hash log | search log | min match | strategy |
|---|---|---|---|---|---|---|---|
| > 256 KiB or unknown | negative | 19 | 12 | 13 | 1 | 6 | fast |
| | 1 | 19 | 13 | 14 | 1 | 7 | fast |
| | 2 | 20 | 15 | 16 | 1 | 6 | fast |
| | 3 | 21 | 16 | 17 | 1 | 5 | double fast |
| | 4 | 21 | 18 | 18 | 1 | 5 | double fast |
| <= 256 KiB | negative | 18 | 12 | 13 | 1 | 5 | fast |
| | 1 | 18 | 13 | 14 | 1 | 6 | fast |
| | 2 | 18 | 14 | 14 | 1 | 5 | double fast |
| | 3 | 18 | 16 | 16 | 1 | 4 | double fast |
| | 4 | 18 | 16 | 17 | 3 | 5 | double fast (the reference uses a greedy parser here; until that band exists the double-fast parser stands in) |
| <= 128 KiB | negative | 17 | 12 | 12 | 1 | 5 | fast |
| | 1 | 17 | 12 | 13 | 1 | 6 | fast |
| | 2 | 17 | 13 | 15 | 1 | 5 | fast |
| | 3 | 17 | 15 | 16 | 2 | 5 | double fast |
| | 4 | 17 | 17 | 17 | 2 | 4 | double fast |
| <= 16 KiB | negative | 14 | 12 | 13 | 1 | 5 | fast |
| | 1 | 14 | 14 | 15 | 1 | 5 | fast |
| | 2 | 14 | 14 | 15 | 1 | 4 | fast |
| | 3 | 14 | 14 | 15 | 2 | 4 | double fast |
| | 4 | 14 | 14 | 14 | 4 | 4 | double fast (the reference uses greedy) |

The values were read off the reference tool's own report (`zstd --show-default-cparams`) for sources of the class boundary sizes;
they are measured behaviour, not copied code. Shrinking: `windowLog = min(table, max(10, ceil(log2(size))))` when the size is
known, `hashLog = min(hashLog, windowLog + 1)`, `chainLog = min(chainLog, windowLog)`. For negative levels the target length
(acceleration) is `-level`. `ZstdCompressionOptions.WindowLog` replaces the window log (and tightens the other two logs).

**Literals of negative levels are stored raw** (the Huffman pass costs more time than it is worth at those speeds). This is part
of the level definition, `ZstdCompressionParameters.CompressLiterals`.

## Frames

- Content size is written when it is known (one-shot calls, or `ContentSize` for a stream) unless `WriteContentSize` is false.
- A frame with a known content size no larger than the window is **single-segment**: no window descriptor, the content size is the
  window. Content size field: 1 byte below 256 (single-segment only), 2 bytes (value minus 256) below 65,792, 4 bytes up to 2^32-1,
  else 8.
- The dictionary ID is written when the dictionary has one and `WriteDictionaryId` is true, in the fewest of 1, 2, 4 bytes.
- The checksum (`Checksum`, default false) is the low 32 bits of XXH64 of the content.
- Empty input: header plus one empty raw last block.
- Blocks are cut every 128 KiB of input. A one-shot call and a stream with the same content size make the same bytes however the
  input is split across calls; `Flush` ends a block early (and so changes the bytes). A stream without a content size uses the
  largest class and writes no content size, whether or not its first call happened to carry the whole input.

## Blocks

- A block whose bytes are all equal (two or more) is an RLE block. Otherwise the block is compressed, and stored raw when the
  compressed body would not be smaller than the block.
- **Literals section:** fewer than 8 literals, or raw literals mode, are stored raw; one distinct byte value is RLE. Otherwise the
  smallest of raw, a new Huffman tree, and the previous tree (treeless) is written, by exact size. One stream below 256
  literals, four streams from 256. A tree description uses 4-bit weights or FSE-compressed weights, whichever is smaller; the
  compressed form is read back with the decoder's own routine and dropped for the direct form if it does not decode to the same
  weights. Codes are limited to 11 bits: the Huffman tree is built by a two-queue merge, and too-long codes are shortened by
  lengthening the least frequent of the longest codes until the Kraft sum fits, then spare code space is given back to the most
  frequent symbols.
- **Sequences section:** for each of literal lengths, offsets and match lengths the cheapest of RLE (one symbol), repeat (the
  previous table from the frame or the dictionary), predefined and a new FSE table is chosen. Cost is in 1/256 bit, integer:
  blocks with at most 1,500 sequences price each choice by running the encoder's state machine over the symbols (the table
  description's bytes included); larger blocks use the histogram estimate `count * (accuracyLog - log2(probability))`. `log2` is
  a table built with integer arithmetic. After an RLE or predefined block the repeat mode is not offered (the next block offers
  a new table or the predefined one at the same cost).
- **Normalization:** symbols that occur get at least one state; symbols with at most `total >> accuracyLog` occurrences get the
  "less than one" probability; the rest are scaled with rounding, then states are removed from or given to the symbols that lose
  or gain the fewest bits (ties: lower symbol first).
- **Accuracy log:** `clamp(min(maxLog, sampleLog - 2) raised to min(sampleLog + 1, log2(maxSymbol) + 2), 5, maxLog)` with
  `sampleLog = floor(log2(sequenceCount - 1))`; maximum 9 for literal and match lengths, 8 for offsets, 6 for Huffman weights.

## Match finders (fast, double fast)

- Hash: the low `minMatch` bytes of an 8-byte little-endian read, shifted up, multiplied by `0x9E3779B185EBCA87`, top `log` bits.
  The double-fast long table hashes 8 bytes. Tables hold window positions as `uint`, 0 meaning empty (position 0 is never a
  candidate). Candidates must be within the window size; the candidate is accepted when its first 4 bytes (double fast long: 8)
  equal the current bytes.
- **Fast:** each round hashes two neighbouring positions, `ip` and `ip + 1`, writes both into the table, and tests (in this order)
  the first repeat offset at `ip + step`, the candidate of `ip`, the candidate of `ip + 1`. With no match, `ip += step`. The
  step starts at `targetLength + 1` (2 when the target length is 0) and grows by one each 128 bytes since the last match. A match
  is extended backward over equal bytes (not before the last match end) and forward. After a match two positions are indexed (the
  match start + 2 and the position 2 before its end) and the second repeat offset is tried repeatedly at the end of each match.
- **Double fast:** one position per round, `ip += 1 + (ip - anchor) / 256`. Tests the first repeat offset at `ip + 1`, the long
  candidate, the short candidate (then also the long candidate at `ip + 1`, which wins if it matches 8 bytes). Both tables are
  filled at the same positions after a match.
- **With a dictionary:** the dictionary content sits logically right before the first byte. A match into it has offset
  `bytes produced + dictionary length - position in dictionary`, and is allowed while the bytes produced so far do not exceed
  the window size (RFC 8878 section 3.1.1.1.2). Repeat offsets that reach before the frame start are tried against the
  dictionary. A match in the dictionary may run on into the start of the frame. The dictionary's search tables are built once per
  (minimum match, hash log, chain log, strategy) with an entry for every position (a later position replaces an earlier one with
  the same hash). Without the dictionary-aware repeat check and the single-position loop, ratio on small payloads is up to 4%
  worse. With a dictionary and the fast strategy, one position is tried per round (repeat at `ip + 1`, own table, dictionary table).
- Window sliding (streams): the window buffer keeps the last `windowSize + 1` bytes; stored positions are rebased by the
  amount moved and those that fall below become 0. Matches never reach past the window size, so a stream and a one-shot call
  find the same matches.

## Dictionaries

`ZstdCompressionDictionary.Parse` accepts the Zstandard dictionary format (entropy tables read back into encoder tables, repeat
offsets, content) and, for anything else, raw content (8 bytes or more; ID 0 unless given). The Huffman tree, the three FSE tables
and the repeat offsets start the frame's carried state, so the first block can use repeat modes and treeless literals.

## Measuring

- Ratio: compressed bytes of the whole file in one frame, no checksum, against `zstd -N --no-check` (negative levels
  `--fast=N`), on the same machine.
- Speed: `zstd -b<level>` in-memory modes against `tools/zstd-encode-bench` (`big`): both warm, memory to memory, single thread,
  best of several seconds. Decompression compares our decoder on our frame with the tool decoding its own frame.
- Peak memory: `/usr/bin/time -v` on both sides.
- Game snapshot data: `tools/zstd-encode-bench/game.sh` trains with `zstd --train` on seed 1 and compresses seed 2 one frame per
  payload.
