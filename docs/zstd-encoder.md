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

Level numbers and meanings follow the `zstd` command line tool. Level 0 means 3. Levels 1 to 22 and every negative level down to
-131072 are implemented; the negative level is the acceleration (the fast strategy's target length). Higher levels throw
`ArgumentOutOfRangeException`. Levels 20 to 22 are the tool's ultra levels: their windows reach 128 MiB (window log 27), so a decoder
has to allow that window (`ZstdDecompressionOptions.MaxWindowSize` defaults to 8 MiB; `zstd -d` allows 128 MiB without a flag).

Parameters are looked up by the source size class, then shrunk to what the source can use. The size used is the content size plus
the dictionary's content size; it is unknown for a streaming encoder that was not told the content size, and then the largest class
applies.

| class (size) | level | window log | chain log | hash log | search log | min match | target length | strategy |
|---|---|---|---|---|---|---|---|---|
| > 256 KiB or unknown | negative | 19 | 12 | 13 | 1 | 6 | acceleration | fast |
|  | 1 | 19 | 13 | 14 | 1 | 7 | 0 | fast |
|  | 2 | 20 | 15 | 16 | 1 | 6 | 0 | fast |
|  | 3 | 21 | 16 | 17 | 1 | 5 | 0 | double fast |
|  | 4 | 21 | 18 | 18 | 1 | 5 | 0 | double fast |
|  | 5 | 21 | 18 | 19 | 3 | 5 | 2 | greedy |
|  | 6 | 21 | 18 | 19 | 3 | 5 | 4 | lazy |
|  | 7 | 21 | 19 | 20 | 4 | 5 | 8 | lazy |
|  | 8 | 21 | 19 | 20 | 4 | 5 | 16 | lazy2 |
|  | 9 | 22 | 20 | 21 | 4 | 5 | 16 | lazy2 |
|  | 10 | 22 | 21 | 22 | 5 | 5 | 16 | lazy2 |
|  | 11 | 22 | 21 | 22 | 6 | 5 | 16 | lazy2 |
|  | 12 | 22 | 22 | 23 | 6 | 5 | 32 | lazy2 |
|  | 13 | 22 | 22 | 22 | 4 | 5 | 32 | btlazy2 |
|  | 14 | 22 | 22 | 23 | 5 | 5 | 32 | btlazy2 |
|  | 15 | 22 | 23 | 23 | 6 | 5 | 32 | btlazy2 |
|  | 16 | 22 | 22 | 22 | 5 | 5 | 48 | btopt |
|  | 17 | 23 | 23 | 22 | 5 | 4 | 64 | btopt |
|  | 18 | 23 | 23 | 22 | 6 | 3 | 64 | btultra |
|  | 19 | 23 | 24 | 22 | 7 | 3 | 256 | btultra2 |
|  | 20 | 25 | 25 | 23 | 7 | 3 | 256 | btultra2 |
|  | 21 | 26 | 26 | 24 | 7 | 3 | 512 | btultra2 |
|  | 22 | 27 | 27 | 25 | 9 | 3 | 999 | btultra2 |
| <= 256 KiB | negative | 18 | 12 | 13 | 1 | 5 | acceleration | fast |
|  | 1 | 18 | 13 | 14 | 1 | 6 | 0 | fast |
|  | 2 | 18 | 14 | 14 | 1 | 5 | 0 | double fast |
|  | 3 | 18 | 16 | 16 | 1 | 4 | 0 | double fast |
|  | 4 | 18 | 16 | 17 | 3 | 5 | 2 | greedy |
|  | 5 | 18 | 17 | 18 | 5 | 5 | 2 | greedy |
|  | 6 | 18 | 18 | 19 | 3 | 5 | 4 | lazy |
|  | 7 | 18 | 18 | 19 | 4 | 4 | 4 | lazy |
|  | 8 | 18 | 18 | 19 | 4 | 4 | 8 | lazy2 |
|  | 9 | 18 | 18 | 19 | 5 | 4 | 8 | lazy2 |
|  | 10 | 18 | 18 | 19 | 6 | 4 | 8 | lazy2 |
|  | 11 | 18 | 18 | 19 | 5 | 4 | 12 | btlazy2 |
|  | 12 | 18 | 19 | 19 | 7 | 4 | 12 | btlazy2 |
|  | 13 | 18 | 18 | 19 | 4 | 4 | 16 | btopt |
|  | 14 | 18 | 18 | 19 | 4 | 3 | 32 | btopt |
|  | 15 | 18 | 18 | 19 | 6 | 3 | 128 | btopt |
|  | 16 | 18 | 19 | 19 | 6 | 3 | 128 | btultra |
|  | 17 | 18 | 19 | 19 | 8 | 3 | 256 | btultra |
|  | 18 | 18 | 19 | 19 | 6 | 3 | 128 | btultra2 |
|  | 19 | 18 | 19 | 19 | 8 | 3 | 256 | btultra2 |
|  | 20 | 18 | 19 | 19 | 10 | 3 | 512 | btultra2 |
|  | 21 | 18 | 19 | 19 | 12 | 3 | 512 | btultra2 |
|  | 22 | 18 | 19 | 19 | 13 | 3 | 999 | btultra2 |
| <= 128 KiB | negative | 17 | 12 | 12 | 1 | 5 | acceleration | fast |
|  | 1 | 17 | 12 | 13 | 1 | 6 | 0 | fast |
|  | 2 | 17 | 13 | 15 | 1 | 5 | 0 | fast |
|  | 3 | 17 | 15 | 16 | 2 | 5 | 0 | double fast |
|  | 4 | 17 | 17 | 17 | 2 | 4 | 0 | double fast |
|  | 5 | 17 | 16 | 17 | 3 | 4 | 2 | greedy |
|  | 6 | 17 | 16 | 17 | 3 | 4 | 4 | lazy |
|  | 7 | 17 | 16 | 17 | 3 | 4 | 8 | lazy2 |
|  | 8 | 17 | 16 | 17 | 4 | 4 | 8 | lazy2 |
|  | 9 | 17 | 16 | 17 | 5 | 4 | 8 | lazy2 |
|  | 10 | 17 | 16 | 17 | 6 | 4 | 8 | lazy2 |
|  | 11 | 17 | 17 | 17 | 5 | 4 | 8 | btlazy2 |
|  | 12 | 17 | 18 | 17 | 7 | 4 | 12 | btlazy2 |
|  | 13 | 17 | 18 | 17 | 3 | 4 | 12 | btopt |
|  | 14 | 17 | 18 | 17 | 4 | 3 | 32 | btopt |
|  | 15 | 17 | 18 | 17 | 6 | 3 | 256 | btopt |
|  | 16 | 17 | 18 | 17 | 6 | 3 | 128 | btultra |
|  | 17 | 17 | 18 | 17 | 8 | 3 | 256 | btultra |
|  | 18 | 17 | 18 | 17 | 10 | 3 | 512 | btultra |
|  | 19 | 17 | 18 | 17 | 5 | 3 | 256 | btultra2 |
|  | 20 | 17 | 18 | 17 | 7 | 3 | 512 | btultra2 |
|  | 21 | 17 | 18 | 17 | 9 | 3 | 512 | btultra2 |
|  | 22 | 17 | 18 | 17 | 11 | 3 | 999 | btultra2 |
| <= 16 KiB | negative | 14 | 12 | 13 | 1 | 5 | acceleration | fast |
|  | 1 | 14 | 14 | 15 | 1 | 5 | 0 | fast |
|  | 2 | 14 | 14 | 15 | 1 | 4 | 0 | fast |
|  | 3 | 14 | 14 | 15 | 2 | 4 | 0 | double fast |
|  | 4 | 14 | 14 | 14 | 4 | 4 | 2 | greedy |
|  | 5 | 14 | 14 | 14 | 3 | 4 | 4 | lazy |
|  | 6 | 14 | 14 | 14 | 4 | 4 | 8 | lazy2 |
|  | 7 | 14 | 14 | 14 | 6 | 4 | 8 | lazy2 |
|  | 8 | 14 | 14 | 14 | 8 | 4 | 8 | lazy2 |
|  | 9 | 14 | 15 | 14 | 5 | 4 | 8 | btlazy2 |
|  | 10 | 14 | 15 | 14 | 9 | 4 | 8 | btlazy2 |
|  | 11 | 14 | 15 | 14 | 3 | 4 | 12 | btopt |
|  | 12 | 14 | 15 | 14 | 4 | 3 | 24 | btopt |
|  | 13 | 14 | 15 | 14 | 5 | 3 | 32 | btultra |
|  | 14 | 14 | 15 | 15 | 6 | 3 | 64 | btultra |
|  | 15 | 14 | 15 | 15 | 7 | 3 | 256 | btultra |
|  | 16 | 14 | 15 | 15 | 5 | 3 | 48 | btultra2 |
|  | 17 | 14 | 15 | 15 | 6 | 3 | 128 | btultra2 |
|  | 18 | 14 | 15 | 15 | 7 | 3 | 256 | btultra2 |
|  | 19 | 14 | 15 | 15 | 8 | 3 | 256 | btultra2 |
|  | 20 | 14 | 15 | 15 | 8 | 3 | 512 | btultra2 |
|  | 21 | 14 | 15 | 15 | 9 | 3 | 512 | btultra2 |
|  | 22 | 14 | 15 | 15 | 10 | 3 | 999 | btultra2 |

The strategies above btlazy2 (btopt, btultra, btultra2) take levels 13 to 15 of the 256 KiB and 128 KiB classes, 11 to 15 of the 16 KiB class
and levels 16 to 22 of every class; they are the optimal parsers described below.

The values were read off the reference tool's own report (`zstd --show-default-cparams`) for sources of the class boundary sizes;
they are measured behaviour, not copied code. Shrinking: `windowLog = min(table, max(10, ceil(log2(size))))` when the size is
known, `hashLog = min(hashLog, windowLog + 1)`, `chainLog = min(chainLog, windowLog)` (`windowLog + 1` for the binary-tree strategies, whose table holds two links per position). For negative levels the target length
(acceleration) is `-level`. `ZstdCompressionOptions.WindowLog` replaces the window log (and tightens the other two logs).

**Literals of negative levels are stored raw** (the Huffman pass costs more time than it is worth at those speeds). This is part
of the level definition, `ZstdCompressionParameters.CompressLiterals`.

## Match finders (greedy, lazy, lazy2, btlazy2)

One finder, `ZstdLazyMatchFinder`, implements the four strategies. They differ in the search structure (rows of tagged entries or a binary tree)
and in how far the parse looks ahead.

- **Index:** every position with eight bytes after it inside the block is entered, in order, by the search that reaches it (positions
  skipped over by a match or a step are entered first). The hash is the fast finder's: the low `minMatch` bytes of an 8-byte read,
  multiplied by `0x9E3779B185EBCA87`, top `hashLog` bits. Tables hold window positions as `uint` with 0 for empty; the first byte of a
  window is never a candidate. A stream that slides its window rebases positions and, for the binary tree (indexed by position
  modulo its size), turns it around by the slide distance. Blocks continue each other: the last seven positions of a block are entered
  by the first search of the next one.
- **Rows** (greedy, lazy, lazy2; `chainLog` is not used by the frame's own table): the table holds `2^hashLog` entries in rows of `2^rowLog` entries, `rowLog = clamp(searchLog, 4, 6)`
  (16, 32 or 64), `2^(hashLog - rowLog)` rows (at least 2). The 64-bit hash picks the row with its top bits and a tag byte with the
  next 8. Each row is a ring: a new entry (tag and position) goes just below the row's head, which then moves to it, so walking
  from the head visits entries newest first. A search takes the entries whose tag equals its own, newest first, and stops at the first
  one that is empty or farther back than the window size; it visits at most `min(2^searchLog, 2^rowLog)` of them. Only the tags are
  compared before the match is looked at. A visited candidate is skipped without measuring when its four bytes ending at the current
  best length differ from the same bytes at the current position (a longer match must agree on them); otherwise the match length
  is measured and the candidate becomes the best if its gain is higher. With the current best shorter than `minMatch - 1` the
  skip test looks at the single byte at index `minMatch - 1` instead (`minMatch` 3 only).
- **Binary tree** (btlazy2): `2^(chainLog-1)` nodes, two links each (smaller and larger), one tree per hash value rooted at
  `head[hash]`. Entering a position compares it against nodes from the root down, splitting them into those below and those above
  it (the standard suffix-tree-like insertion of binary-tree match finders); the comparisons give every match met, in increasing
  length, with at most `2^searchLog` nodes visited. Comparison length is capped at 256 (the nice length): a node that matches that
  far is replaced by the new one, which takes its children. A node entered with fewer than 256 bytes left in its block was ordered
  by fewer bytes, so the shortcut that starts a comparison at the shorter of the two bounding prefix lengths is not used on it, and
  the bounds are forgotten after it. The search then extends the best match past the cap.
- **Gain:** a match of length `L` at offset `d` has gain `4 L - floor(log2 d)`; a repeat-offset match has `4 L - 1`. The search keeps
  the candidate of highest gain (earliest on ties). Repeat offsets are tried before the table: with a literal in front, the three
  repeat offsets; with none, the second, the third and the first minus one (the cases RFC 8878 section 3.1.1.5 gives codes 1 to 3 to).
  A repeat match needs 4 equal bytes.
- **Parse:** greedy takes the best match at the current position, after also trying the first repeat offset one byte further on, which
  wins if its gain is higher. Lazy (lazy2) takes the best match at `ip`, then looks at `ip + 1` (`ip + 2`): a later match replaces
  it when its gain is higher by more than 4 (7 for two positions on), and the check restarts from the new position. A match of 128
  bytes or more is not looked past. The match is extended backwards over equal bytes down to the last match end, emitted, and the
  search resumes at its end. With nothing found the position advances by `1 + (ip - anchor) / 256`.
- **With a dictionary:** the frame's own candidates are visited first; a search that has attempts left continues along the
  dictionary's hash chains (built once per dictionary and parameter set, for every position with 8 bytes after it, sized by
  `DictionaryHashLog` and `DictionaryChainLog` when set). The dictionary's candidates count only while the bytes produced do not
  exceed the window size, and the binary-tree strategy searches the dictionary through chains, not a tree. Repeat offsets that
  reach before the frame's first byte are tried against the dictionary content.

## Optimal parsing (btopt, btultra, btultra2)

`ZstdOptimalMatchFinder` implements the three strategies. They differ only in the parameters of their rows (search depth, target length,
minimum match) and in two details: btopt skips searching a position whose successor is already reachable at nearly the same price, and
btultra2 parses the first block of a frame twice.

- **Tree:** the binary tree of btlazy2 (one tree per hash of `minMatch` bytes, `2^(chainLog-1)` nodes, every position entered once, in order),
  compared up to 1024 bytes (the tree's "long enough"), at most `2^searchLog` nodes per search. The matches met on the way down, each longer
  than the one before, are the candidates; with a dictionary, the dictionary's hash chains add candidates longer than the tree's best.
  The chain walk has its own budget of 16 times the level's attempts (the frame's tree does not use it up): the dictionary has no tree,
  and its best match is often far down a chain. The three repeat offsets (with a literal in front; with none, the second, the third and the first minus
  one, RFC 8878 section 3.1.1.5) are candidates too, from four bytes (three when `minMatch` is 3). A candidate that reaches the
  target length ends the stretch at once: the longest candidate is measured to its end and taken.
- **Prices:** in 1/256 bit. A symbol costs `log2(total) - log2(count)` (counts below 1 are 1; `log2` is the integer table of `ZstdCost`), plus
  its extra bits. Four count sets: literals (256), literal length codes (36), match length codes (53) and offset codes (32; the offset
  value's base-2 logarithm, so the repeat codes 1 to 3 are codes 0 and 1). Counts of the first block start from the block's literal
  histogram and the predefined distributions (a distribution entry of -1 counts 1); with a dictionary in the Zstandard format they start from
  the dictionary's tables instead, since the first block can be coded with them: a literal counts `2^(maxBits - codeLength)` (1 if it has no code)
  and a sequence symbol its normalized count (1 for -1 and 0). Prices are rebuilt from the statistics alone at the start of each
  block, and again every 4096 bytes parsed (half the statistics at the block start plus the counts of this block so far). When a block
  finishes, its counts are added to half the old statistics. A match of length 3 costs two bits more than its symbols.
- **Path search:** from the start of a stretch (the first position with a candidate, with the literals since the last match pending) a
  cell per following position records the cheapest known price of reaching it, whether a literal or a match got there, the match's length,
  offset, starting cell and literals before it, and the repeat offsets after that path. The price of a cell includes the code of the literal
  run pending at it, so a literal extends a cell by its own price plus the difference between the run's code prices, and a match adds its
  offset, length and (for the next run) zero-length literal code prices. Cell by cell: the literal transition is applied (ties go to the
  literal), the cell's repeat offsets are derived from its path, candidates at the position are found (using that path's repeat offsets)
  and every length from the candidate's first new length to its length relaxes the cell it reaches. Offsets that equal a repeat offset
  of the path are priced as repeat codes (the same resolution the block writer uses). The stretch ends when the cell reached is the
  farthest any match got to; the cheapest path there is written out and the literals at its end stay pending, so a stretch is never forced
  to end with a match that costs more than literals. A stretch is at most 4096 positions; a candidate that would pass that, or that reaches
  the target length, is taken as the stretch's last match.
- **btultra2:** the first block of a frame is parsed once with the starting prices, the counts of that parse become the statistics, the
  tree is emptied, and the block is parsed again. (Blocks after the first use the statistics of the blocks before them.)
- **Window sliding:** as for btlazy2: stored positions are rebased, the tree is turned around by the distance moved.

## Block splitting

Blocks of the optimal strategies are cut again after parsing. `ZstdBlockSplitter` groups the block's sequences into chunks of about
4096 input bytes and estimates the cost of coding a run of chunks as one block: the entropy of its literals, literal length codes, match
length codes and offset codes (integer, from the counts), plus the extra bits, plus an allowance for the headers and table descriptions of
`12 + distinct literals / 2 + 3/4 * (distinct codes of the three sequence alphabets)` bytes (a literal alphabet is not counted when raw
literals are cheaper). A shortest-path search over the chunk boundaries (at most 32 blocks per 128 KiB) picks the cuts of least total
cost. Each part is written as a block of its own with its own tables; repeat offsets run on from part to part exactly as the decoder
sees them. Because every part may cost three header bytes more than the single block would have, the bound of `GetMaxCompressedLength`
allows 32 parts per 128 KiB.

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
  description's bytes included); larger blocks, and every block at the fast and double-fast strategies (levels up to 4 and the
  negative levels), use the histogram estimate `count * (accuracyLog - log2(probability))`. `log2` is
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
  changes the bytes). Each new job loads its overlap into the match finder again, so flushing after every few bytes costs time that
  grows with the square of the stream length (about 3.7 s for 80 KB written one byte per flush). Writers that flush often should leave
  jobs off.

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

### Block writer speed work (arm64, zstd 1.5.7)

The block writer prices the three sequence tables side by side (their state machines are independent, so the walks overlap), keeps
the table it priced instead of building it again, and keeps the Huffman bit container in registers; the fast and double-fast
strategies choose table modes from the histogram estimate at every block size (above). Output is unchanged except for one small
block in the matrix (same size). Apple M2, `time ratio` as above (reference speed divided by ours, bar 1.15), load 3 to 6 from other
work on the machine; the whole-file rows move by less than their run-to-run spread, because the match finders dominate there:

| file | level | before | after |
|---|---|---|---|
| silesia.tar | 1 / 3 / 6 / 9 | 1.37 / 1.25 / 1.46 / 1.47 | 1.41 / 1.30 / 1.46 / 1.42 |
| enwik8 | 1 / 3 / 6 / 9 | 1.49 / 1.29 / 1.57 / 1.52 | 1.49 / 1.28 / 1.44 / 1.50 |
| silesia.tar | 12 / 16 / 19 | | 1.35 / 1.18 / 1.20 |
| enwik8 | 12 / 16 / 19 | | 1.49 / 1.05 / 0.92 |

Small payloads with a 16 or 112 KiB trained dictionary (payloads per second divided by the tool's, geometric mean of three games and
two dictionary sizes; above 1 is faster than the tool; bar 0.87):

| payloads | level | before | after |
|---|---|---|---|
| frames | 1 / 3 | 1.20 / 1.06 | 1.26 / 1.07 |
| snapshots | 3 / 9 / 19 | 0.39 / 0.97 / 0.45 | 0.48 / 1.11 / 0.50 |
| saves | 3 / 9 / 19 | 0.39 / 0.81 / 0.42 | 0.54 / 0.89 / 0.45 |

At a few hundred bytes to a few kilobytes a block costs about a hundred matches and three table decisions, and the time is spread
over the match loop (about 45 ns a match), the literal section and the sequence section; no single step is more than a third.

## Results: long-distance matching and jobs (zstd 1.5.7)

Method as above, `run.sh --ref-opts ... --ours-opts ...` (`zstd -b<L> -e<L> -T<n> [--long=27]` against `ZstdEncodeBench big ... --workers n --job auto [--long 27]`, best of 3 s).
`time ratio` is our time over the tool's. x64: Ryzen 9 7950X3D, 32 cores, load 2 to 7 from other work; arm64: Apple M2, 8 cores, load 3 to 6.
Sizes are the same on both machines. Jobs cost 0.03% in size (silesia.tar level 1: 73,520,243 bytes as one job, 73,540,841 with jobs).

| file, level | size vs tool | x64 time ratio: 1 thread, long | 4 jobs at once, long | 8 | 16 | arm64: 1 thread, long | 4 | 8 |
|---|---|---|---|---|---|---|---|---|
| silesia.tar 1 | +0.57% | 1.44 | 1.13 | 0.67 | 0.62 | 1.38 | 1.06 | 0.85 |
| silesia.tar 3 | +1.04% | 1.28 | 1.22 | 0.88 | 0.71 | 1.31 | 1.26 | 1.16 |
| enwik8 1 | +0.68% | 1.57 | 1.83 | 1.04 | 0.93 | 1.53 | 1.65 | 1.22 |
| enwik8 3 | +0.69% | 1.44 | 1.76 | 1.19 | 1.12 | 1.39 | 1.85 | 1.46 |

The long-distance matcher alone scans 2.1 GB/s of silesia.tar (x64). On an input with far repeats (1.2 GB, three 32 MB pieces recurring
at about 96 MB with one byte in 64 KiB changed) the output is 47% smaller than the tool's at levels 1 and 3, at 1.18 and 0.90 times its time.
With jobs and no long matching the ratio of times is 1.25 to 1.5 (the single-thread gap) at 4 to 16 jobs, and size is +0.34% (level 1) and
+0.82% (level 3) on silesia.tar.

### Levels 16 to 22 (optimal parsing)

Sizes against `zstd --ultra -N` (1.5.7) on the full files, our size divided by the tool's minus one; the tool decodes every frame we write.
Level 21 is not measured on the big files. Times are from the Apple M2 (8 cores, load 2.4 to 4.2 from other work), `run.sh --seconds 1`; the
x64 timing rows wait for a quiet window.

| file, level | size vs tool | compress time ratio | decompress time ratio (our decoder, our output, against the tool's own) | peak memory ours / tool (MB) |
|---|---|---|---|---|
| silesia.tar 16 | -0.17% | 1.18 | 1.23 | 767 / 642 |
| silesia.tar 17 | -0.29% | | | |
| silesia.tar 18 | +0.41% | | | |
| silesia.tar 19 | +0.30% | 1.37 | 1.18 | 813 / 690 |
| silesia.tar 20 | +0.38% | | | |
| silesia.tar 22 | +0.29% | 1.36 | 1.32 | 1117 / 1315 |
| enwik8 16 | +0.02% | 1.22 | 1.28 | 303 / 321 |
| enwik8 17 | -0.30% | | | |
| enwik8 18 | +0.59% | | | |
| enwik8 19 | -0.01% | 1.26 | 1.14 | 337 / 369 |
| enwik8 20 | +0.50% | | | |
| enwik8 22 | +1.16% | 0.82 | 1.69 | 876 / 994 |

Compression time is over the 1.15 bar everywhere but enwik8 22 (where the size is also over the 1% bar), so levels 16 to 22 stay preview.
Outliers by file (first 4 MB of Silesia files, levels 16 and 19): mr +1.1% and +1.3%, x-ray 0.0% and +1.7%, nci +0.9% and +0.4%; the others
are within 0.4% (osdb is 1.3% smaller). x-ray at levels 18 and up is 1.7% larger than at level 17 for us but 1.2% smaller for the tool: skipping the search at a
position where a literal is almost as cheap (what level 17 does) brings it to +0.4% and costs 0.3% on text, so the other levels do not do it.
Inputs of 16 KB or less at levels 13 to 15 can be up to 1.8% larger than the tool's.

Small game payloads with a trained dictionary at levels 16 and 19 (seed 2, one frame per payload, dictionaries of 16 and 112 KiB trained on seed 1;
size against the tool's at the same level; worst cell, then the range over all 24 cells):
before the dictionary-aware starting prices and the dictionary's own chain-walk budget, L16 up to +8.7% and L19 up to +5.8%; after, L16 -1.5% to +1.2%
(worst: lanterns saves, 16 KiB dictionary) and L19 -4.8% to +0.4%. Levels 13 and 15 on the same payloads are -2.2% to +0.4%.
