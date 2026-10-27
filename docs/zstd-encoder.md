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
- **Skipping work that cannot pay (small blocks):** a new Huffman tree is built only when the literals number at least 64 and an estimate of
  its size (the entropy of the literals, rounded down by the integer logarithm's error, plus `8 + 3/8` bytes per distinct symbol for the
  description) is below both the raw size and the cost of the previous tree. A new FSE table is tried only when the block has at least 64
  sequences and the entropy of its codes plus 16 bits is below the cost of the best of repeat and predefined; with neither available a
  table is always built. The estimates are integers; they decide bytes, so they are part of the format of our output.
- **Sequences section:** for each of literal lengths, offsets and match lengths the cheapest of RLE (one symbol), repeat (the
  previous table from the frame or the dictionary), predefined and a new FSE table is chosen. Cost is in 1/256 bit, integer:
  blocks with at most 1,500 sequences price each choice by running the encoder's state machine over the symbols (the table
  description's bytes included); larger blocks use the histogram estimate `count * (accuracyLog - log2(probability))`. `log2` is
  a table built with integer arithmetic. After an RLE or predefined block the repeat mode is not offered (the next block offers
  a new table or the predefined one at the same cost).
- **Normalization:** symbols that occur get at least one state; symbols with at most `total >> accuracyLog` occurrences get the
  "less than one" probability; the rest are scaled with rounding, then states are removed from or given to the symbols that lose
  or gain the fewest bits (ties: lower symbol first).
- **Accuracy log** of a new FSE table, from the number of symbols coded (`sampleLog = floor(log2(count - 1))`): start from
  `min(maxLog, sampleLog - 2)`, raise it to at least `min(sampleLog + 1, floor(log2(maxSymbol)) + 2)`, and clamp to 5 up to
  `maxLog`. `maxLog` is 9 for literal and match lengths, 8 for offsets, 6 for Huffman weights. These values were fitted to the
  reference tool's table sizes by black-box comparison, not derived from a specification.

## Match finders (fast, double fast)

- Hash: the low `minMatch` bytes of an 8-byte little-endian read, shifted up, multiplied by `0x9E3779B185EBCA87`, top `log` bits.
  The double-fast long table hashes 8 bytes. Tables hold window positions as `uint`; an empty slot reads as position 0, which is checked like any
  other candidate. Candidates must be within the window size; the candidate is accepted when its first 4 bytes (double fast long: 8)
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
  the same hash). With a dictionary and the fast strategy, one position is tried per round (repeat at `ip + 1`, own table, dictionary table).
- **Tables of a small source with a dictionary:** the window log, minimum match and strategy come from the size class of source plus
  dictionary, but the hash and chain tables a frame fills are sized for the source alone (`min(table, sourceWindowLog + 1)`, and
  `sourceWindowLog` for the chain), so a 100-byte payload does not clear 100 KiB of table. The dictionary's own search index keeps the
  sizes its length calls for (`DictionaryHashLog`, `DictionaryChainLog` in the parameters).
- Window sliding (streams): the window buffer keeps the last `windowSize + 1` bytes; stored positions are rebased by the
  amount moved and those that fall below become 0. Matches never reach past the window size, so a stream and a one-shot call
  find the same matches.

## Dictionaries

`ZstdCompressionDictionary.Parse` accepts the Zstandard dictionary format (entropy tables read back into encoder tables, repeat
offsets, content) and, for anything else, raw content (8 bytes or more; ID 0 unless given). The Huffman tree, the three FSE tables
and the repeat offsets start the frame's carried state, so the first block can use repeat modes and treeless literals.

## Windows

`WindowLog` (10 to 31) replaces the level's window log. The header states it; matches never reach back further than 2^30 bytes, which is
the most a window buffer can hold, so 31 is a declaration only (`zstd -d --long=31` or `--memory` is needed to decode it). A log below
17 asked for explicitly is written as 17 in the header, because a block (128 KiB) may not exceed the window (RFC 8878 section 3.1.1.2.3);
matching keeps to the smaller window. A frame whose content size is known and fits the window is single-segment and needs no window
descriptor.

What a decoder accepts: this library's `ZstdDecompressionOptions.MaxWindowSize` defaults to 8 MiB and is raised by the caller (it
plays the part of `zstd -d --long=N` and `--memory`); it holds up to two windows, so it refuses windows of 2^30 and above. The `zstd`
command line decodes up to 2^27 by default.

## Long-distance matching

`LongDistanceMatching` is `zstd --long`: with it on and no `WindowLog`, the window is 2^27 (smaller when the content size is known to be
smaller: the log of the size, at least 10). A second matcher finds long repeats anywhere in the window; the level's own match finder
parses the rest, whatever its strategy. The matcher is `ZstdLongDistanceMatcher` and the glue is `ZstdLongSequencer`, both public.

- **Anchoring.** A gear hash runs over every byte: `h = (h << 1) + G[byte]`, with 256 constants from the SplitMix64 sequence seeded with
  `0x2545F4914F6CDD1D` (each step adds `0x9E3779B97F4A7C15`, then `z = (z ^ z >> 30) * 0xBF58476D1CE4E5B9`, `z = (z ^ z >> 27) *
  0x94D049BB133111EB`, `G = z ^ z >> 31`). The hash after byte `j` covers bytes `j-63` to `j`; position `i = j - 63` is a checkpoint when the
  top `HashRateLog` bits of the hash are zero (content-defined sampling, so both copies of a repeat are anchored at the same place).
- **Table.** `2^HashLog` entries of 8 bytes in buckets of `2^BucketSizeLog`: `check << 32 | (position + 1)`, newest first, the oldest falls
  out. With `mixed = (h ^ h >> 29) * 0xBF58476D1CE4E5B9`, the bucket is the top `HashLog - BucketSizeLog` bits of `mixed` and the check
  is its low 32 bits. Defaults: `HashLog = clamp(min(windowLog, sizeLog) - 7, 6, 24)`, `BucketSizeLog = 2`,
  `HashRateLog = clamp(min(windowLog, sizeLog) - HashLog, 1, 24)`, `MinMatch = 64` (16 to 4096), where `sizeLog` is the log of the content
  size when it is known.
- **Search.** At a checkpoint at or after the end of the previous long match and the block start, each bucket entry with the same check
  and a distance of at most the window is verified byte by byte, extended forward to the block end and backward to the previous match end
  or block start; the longest wins, the newest on a tie, and it is taken when at least `MinMatch` long. Then the checkpoint is stored.
  Positions scanned inside a match are stored but not searched from. The dictionary content is not indexed.
- **Blocks.** Long matches are found per 128 KiB block (matches stay inside a block). In a block, each long match becomes a sequence; the
  match finder parses the stretches between them, told the repeat offsets that follow from what came before, and the last 256 bytes of a
  long match are added to its tables so that what follows can refer back into it. Hash tables hold position and check only, never
  pointers, so the matches do not depend on the platform.
- Sliding the stream's window buffer drops entries older than the window and changes nothing else: a stream and a one-shot call make the
  same matches.

## Jobs and threads

`JobSize` (bytes, 0 = off) cuts the input into jobs; `Workers` (0 = one per processor, default 1) is how many are compressed at once.
The bytes depend on the job size, the overlap and the other options, never on `Workers`, the processor count, the platform or the order
in which jobs finish; a browser runs the jobs one after another. With `JobSize` 0 there is one job and `Workers` is ignored.

- **Job size.** `AutoJobSize` (-1) is `max(1 MiB, 4 * min(windowSize, 4 MiB))`. Any size is rounded up to a multiple of 128 KiB (so the
  jobs add no blocks of their own and `GetMaxCompressedLength` holds) and capped at 1 GiB. Job `k` covers input bytes `k * size` to
  `(k + 1) * size`. An input no longer than one job is compressed exactly as without jobs.
- **Overlap.** The bytes before a job are history for matching. `OverlapSize` (default `min(windowSize / 8, 1 MiB)`, at most the window)
  of them are added to the match finder's tables when the job starts (`ZstdMatchFinder.Prime`: every position of the overlap, hashed as
  during parsing); with long-distance matching the whole window before the job can be matched into, found by the one long-distance
  matcher, which sees the whole input in order whichever thread runs which job (jobs take their turn at it in job order, then
  compress in parallel).
- **Job frames.** The header and checksum are written once. The jobs' blocks follow one another; every job writes its own blocks from
  fresh tables: its first block uses no table of the previous job and no repeat offset code until it has coded three offsets of its own
  (the decoder's offsets come from the end of the previous job; `ZstdBlockWriter.ForgetRepeatOffsets` makes the writer assume three
  offsets above any real one). Only the job that starts the frame uses the dictionary. The last block of the last job is the frame's
  last block.
- **Streaming.** `ZstdEncoder.Compress` buffers the window (or overlap) behind the next batch of `Workers` jobs and compresses a batch when
  more input than a batch has arrived, or at the end; the bytes do not depend on how the input is split across calls. Memory is
  the window plus `Workers * JobSize` plus the batch's output. `Flush` compresses what is buffered as a short job and ends it (and so
  changes the bytes).

## Measuring

- Ratio: compressed bytes of the whole file in one frame, no checksum, against `zstd -N --no-check` (negative levels
  `--fast=N`), on the same machine.
- Speed: `zstd -b<level>` in-memory modes against `tools/zstd-encode-bench` (`big`): both warm, memory to memory, single thread,
  best of several seconds. Decompression compares our decoder on our frame with the tool decoding its own frame.
- Peak memory: `/usr/bin/time -v` on both sides.
- Game snapshot data: `tools/zstd-encode-bench/game.sh` trains with `zstd --train` on seed 1 and compresses seed 2 one frame per
  payload.

## Results (zstd 1.5.7, one thread, memory to memory)

Method as above (`tools/zstd-encode-bench/run.sh`, `game.sh --timing`); `time ratio` is our time divided by the tool's, so the bar is 1.15.
Sizes are identical on both machines (the output does not depend on the processor). x64: Ryzen 9 7950X3D, 32 cores, .NET 10.0.402, load 10 to 12
from other work on the machine; arm64: Apple M2, 8 cores, load 2 to 4.

| file, level | size vs tool | compress time ratio x64 | arm64 |
|---|---|---|---|
| silesia.tar -5 | -3.86% | 1.38 | 1.32 |
| silesia.tar -1 | +0.20% | 1.43 | 1.33 |
| silesia.tar 1 | +0.45% | 1.52 | 1.38 |
| silesia.tar 2 | +0.36% | 1.46 | 1.30 |
| silesia.tar 3 | +0.93% | 1.33 | 1.26 |
| silesia.tar 4 | +0.93% | 1.49 | 1.32 |
| enwik8 -5 | -0.23% | 1.35 | 1.33 |
| enwik8 -1 | +0.36% | 1.34 | 1.36 |
| enwik8 1 | +0.63% | 1.51 | 1.44 |
| enwik8 2 | +0.41% | 1.74 | 1.33 |
| enwik8 3 | +0.67% | 1.34 | 1.28 |
| enwik8 4 | +0.69% | 1.37 | 1.33 |

Sizes meet the bar at every level; compression speed does not yet (1.26 to 1.74 times the tool's time). Where the time goes at level 1 on
silesia.tar (per 212 MB pass, x64): match finding about 290 ms, literals about 90 ms, sequence coding about 115 ms; the sequence
coding took twice that before its loop was split out and its state kept in registers. Dynamic profile guidance in
the JIT matters: without it the search loop is 30% slower.

Game payloads with trained dictionaries (seed 2, one frame per payload, our size and payloads per second against the tool's; geometric
means over three games, levels 1 to 3): frames (tens of bytes) are 1.0 to 1.4 times the tool's rate on x64 and 1.0 to 1.2 on arm64 with
sizes 0.2 to 1.1% smaller; snapshots and saves (hundreds of bytes to 6 KB) run at 0.32 to 0.42 times the tool's rate with sizes
2 to 10% smaller.

The unchecked accessors (`Raw`) are worth 5 to 7% of the encoder's speed: the same code built in the `Checked` configuration (every accessor
bounds-checked) runs at 564/598 (level -5), 400/430 (level 1) and 259/273 MB/s (level 3), match finding 10% slower and block writing level.
