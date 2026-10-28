# LZ4 encoder

How the LZ4 compressors in `RecursiveAI.OneCompress.Lz4` choose and write bytes. A later port reproduces the same
output from this page and the format documents (LZ4 Block Format and LZ4 Frame Format). The decoder is described in
`lz4-decoder.md`.

## Layers

| Tier | Types | Use |
|---|---|---|
| 1 | `Lz4.Compress`, `Lz4.TryCompress`, `Lz4Block.Compress`, `Lz4Block.TryCompress` | one call, whole input |
| 2 | `Lz4Encoder`, `Lz4Stream` (write mode) | input in pieces, frames one after another, reuse, flush |
| 3 | `Lz4BlockCompressor` (`Lz4FastBlockCompressor`, `Lz4HighBlockCompressor`), `Lz4BlockWriter`, `Lz4Dictionary` | match finders over a window the caller manages; the sequence writer |

Tier 1 calls tier 2, tier 2 calls tier 3. Every tunable is on `Lz4CompressionOptions`.

## Output depends on the input and the options only

- Integer arithmetic and fixed multiplicative hashes only. The cost model of the optimal parser is integer. No floating
  point, no `HashCode`, no timing, no thread count, no machine feature in any decision. Vector instructions are used only
  to copy bytes and to compare eight bytes at a time (the same count as a byte loop).
- A compressor instance gives the same bytes as a new one: starting a block without history moves the running position
  past everything seen (so older table entries are more than 64 KiB away and never used) instead of clearing tables.
  A stale entry and an empty one behave identically.
- The frame bytes do not depend on how the input is split between calls or on the sizes of output buffers. They do depend
  on where `Flush` is called (it ends the block).

## Levels

`Level` follows the `lz4` tool: `-N` is `--fast=N`, 1 is the default, 2 to 12 are high compression. Level 0 is level 1.
The range is -65536 to 12 (a level outside it is an `ArgumentOutOfRangeException`).

| Level | Finder | Parse | Parameters |
|---|---|---|---|
| 1, 0 | hash table | greedy | acceleration 1 |
| -N | hash table | greedy | acceleration N + 1 (so `--fast=1` skips like acceleration 2) |
| 2 | hash table (2^18 entries, every interior position indexed) | greedy | acceleration 1 |
| 3 to 6 | hash chain | lazy, two-byte lookahead | chain candidates 7, 14, 28, 56; stop at match length 256 |
| 7 to 9 | hash chain | optimal | chain candidates 32, 36, 40; stop at match length 128 |
| 10 to 12 | hash chain | optimal | chain candidates 56, 100, 128; stop at match length 256 |

The candidate counts were chosen so that, on Silesia's tar and enwik8, the output stays within about 0.9% of the `lz4`
tool's size at the same level, at the least search work that achieves it.

### Fast levels (hash table, greedy)

- One table of 4096 `uint` entries (16 KiB), indexed by `((eight bytes << 24) * 889523592379) >> 52` (a hash of five bytes,
  little-endian). An entry is the position of the last occurrence.
- At each probe the entry for the current position is read and replaced. A candidate is a match when it lies 1 to 65535
  bytes back and its first four bytes agree. The first byte of a block is only indexed.
- While nothing matches, the step between probes starts at 1 and grows by one every 64 failed probes, scaled by the
  acceleration: the step after `n` failures is `(n + acceleration * 64) >> 6` (the first probe step is 1).
- A found match is extended backwards over pending literals (not past the start of the window or the dictionary), then
  forwards up to five bytes before the end of the block. After a match, the position two bytes before its end is indexed
  and the position at its end is tried at once (no step).
- Block end rules (Block Format, "End of block conditions"): the last match starts at least 12 bytes before the end and
  ends at least 5 bytes before it; a block shorter than 13 bytes is literals only.

### High levels

- Hash chain: `head[hash]` holds the last position with that hash of four bytes (`(bytes * 2654435761) >> 16`, 65536
  slots); `chain[position & 0xFFFF]` holds the distance to the previous position with the same hash (0 ends the chain, and
  a distance of 65536 or more is stored as 0). Every position is inserted, in order, before it is searched, including the
  positions inside matches.
- A search at a position examines at most the level's number of chain candidates, nearest first. A candidate counts when it
  lies 1 to 65535 bytes back, agrees on the byte at the current best length, agrees on the first four bytes, and extends
  past the best length so far (ties keep the nearest). The search stops early when the best length reaches the level's stop
  length or the end of the block. Matches end at least 5 bytes before the end of the block and start at least 12 bytes before it.
- **Lazy parse (levels 3 to 6).** At a position with a match, the next position is searched for a match longer than
  the current one; one found replaces it (the bytes before it become literals) and the check repeats from there. When the
  next position has none, the position after it is searched for a match more than one byte longer than the current one,
  and one found replaces the current match the same way. The chosen match is then extended backwards over pending literals.
- **Optimal parse (levels 7 to 12).** Starting at a position with a match, the encoder plans the cheapest sequence of
  literals and matches for the stretch ahead, by dynamic programming over byte positions. Prices are bytes of output:
  a literal costs 1, plus 1 more when the run it extends reaches 15 literals and every 255 after; a match costs 3 (token
  and offset) plus length bytes when the length minus 4 reaches 15 and every 255 after. Every length from 4 up to the longest
  match found at a position is available at that position's offset. A position is not searched when the next position is no
  dearer to reach and a four-byte match here would not improve the price four bytes on (a match starting here could be
  replaced by one starting a byte later). The plan ends where no match reaches further, or at the first match of at least the
  stop length, or when a match would reach 4096 bytes ahead; matches are then written front to back.

## Dictionaries

A dictionary (`Lz4Dictionary`, at most the last 64 KiB) is history in front of a block that has no history of its own.

- Fast levels: the dictionary has its own table, built once per dictionary object (every position that has eight bytes
  after it, later positions replacing earlier ones). When the current table has no usable candidate, the dictionary table is
  tried. A match may start in the dictionary and run on into the block, because the history is the dictionary followed by the
  block. No dictionary bytes are copied per block.
- High levels: the dictionary and the block are placed in one buffer, and the chains are copied from a snapshot built once
  per dictionary (positions with four bytes after them). A block of at most 64 KiB without history leaves the dictionary's
  index in place between calls instead: it records the head and chain values each insertion replaces and puts them back
  afterwards, so many small blocks with one dictionary cost the block's own work. The bytes produced are the same either way.
- Frames: independent blocks all start from the dictionary; with linked blocks only the first block does. The frame header
  names the dictionary by `Lz4Dictionary.Id` when it is not zero.

## Frames

- Header: version 1, block independence from `BlocksIndependent`, block checksum, content size if known, content checksum,
  dictionary ID if the dictionary has one; block maximum size from `BlockSize` (default 4 MiB). The one-call
  `Lz4.Compress` and `Lz4Encoder.TryCompressFrame` write the content size unless `IncludeContentSize` is false.
- Blocks hold `BlockSize` bytes except the last (and blocks ended by `Flush`). A block that does not compress below its input
  size is stored (high bit of the size field). Block checksums are xxHash32 of the stored bytes; the content checksum is
  xxHash32 of all the input.
- Linked blocks keep the last 64 KiB of the data as history; the encoder moves it to the front of its window between blocks.
- An empty input is a frame with no blocks. After a frame is complete, the next call to `Lz4Encoder.Compress` starts a new one.

## Limits

- A single block holds at most 2,113,929,216 bytes (`Lz4BlockCompressor.MaxInputLength`). A destination for a block must
  hold `n + n / 255 + 16` bytes (`Lz4BlockWriter.GetMaxCompressedLength`); the one-call `Lz4Block.TryCompress` accepts less and
  reports whether the result fit.
- Position arithmetic is in 32 bits. After about two gigabytes through one instance the tables are cleared (a ratio cost
  only, and still deterministic).

## Memory

A fast compressor holds 16 KiB (level 2: 1 MiB); a high-level compressor holds 384 KiB (a 256 KiB head table and 128 KiB of
chain links), plus 64 KiB of plan state for levels 7 to 12. With a dictionary, a high-level compressor also keeps a copy
buffer (the dictionary and the block) and, for blocks up to 64 KiB, undo records of 6 bytes per input byte. A frame encoder
adds its staging buffer (one block) and, for independent blocks that arrive in pieces or linked blocks, a window (block
size, plus 64 KiB when linked).

## Measuring

`tools/lz4-bench` holds the encoder's measurements. All timing is memory to memory and warm, and runs one process at a time
under the shared timing lock, recording the machine's load before, during and after.

- `bar.sh` times block compression the way `lz4 -b<level>` does (independent 4 MiB blocks, one reused compressor, the best
  of several passes inside each process, `lz4 -b<level> -B7 -i<seconds>` for the reference), repeating each side `--reps`
  times in fresh processes. A row gives both compressed sizes, the min/median/max compression and decompression speeds of
  the repeats, the speed ratios from the medians, and both sides' peak resident memory (`/usr/bin/time`).
  The sizes are those of the blocks (the frame adds a few bytes).
- `game-prep.sh` samples the game snapshot data into per-game payload directories and builds raw 8 KiB and 64 KiB
  dictionaries from the training set. `game-bar.sh` compresses every payload as its own frame with a reused encoder and a
  dictionary prepared once, against the lz4 library's frame compressor with a prepared dictionary (`refsmall.c`, built
  against the pinned lz4 install's shared library, never the system one, and called as a black box), and prints sizes and payloads per second.
- `Lz4Bench size` prints frame sizes only, for tuning.

A row passes when our size is at most 1.01 times the reference's and our compression and decompression times are each at most
1.15 times the reference's (speed ratio at least 0.87).
