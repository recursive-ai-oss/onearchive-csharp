# Deflate, zlib and gzip encoder

Normative and language-neutral: a port that follows this text produces the same bytes. RFC 1951 leaves the parse, the block
boundaries and the Huffman code construction to the encoder; everything it leaves open is fixed here. Output depends only on the
input bytes, the options, and where flushes fall. It never depends on the CPU, the runtime or how the caller splits the input
into calls.

## API shape

- Tier 1: `Deflate`, `ZLib`, `GZip` each have `Compress`, `TryCompress`, `GetMaxCompressedLength` taking a `DeflateOptions`.
- Tier 2: `DeflateEncoder` (`OperationStatus Compress(source, destination, out consumed, out written, DeflateFlush)`, a
  `bool isFinalBlock` overload, `Flush`, `Reset`) and `DeflateEncoderStream` (write-only stream, `Flush()` is a sync flush).
- Tier 3: `DeflateHashChainMatchFinder`, `DeflateBinaryTreeMatchFinder`, `DeflateSequenceBuffer`, `DeflateBlockSplitter`,
  `DeflateHuffmanBuilder`, `DeflateBlockWriter`. The encoder is built from exactly these.

## Options

| Option | Default | Meaning |
|---|---|---|
| `Level` | 6 | 0 to 12, table below. |
| `Strategy` | `Default` | `HuffmanOnly` (no matches), `RunLength` (matches of distance 1 only), `FixedCodes` (fixed Huffman codes or stored, never dynamic), `Default`. |
| `Dictionary` | empty | Preset dictionary. zlib: sets FDICT and writes the Adler-32 of the dictionary; raw: used silently; gzip: rejected. Only the last 32768 bytes are used. |
| `GZipFileName`, `GZipComment`, `GZipExtra`, `GZipHeaderCrc`, `GZipIsText`, `GZipModificationTime`, `GZipOperatingSystem` | none, none, none, false, false, 0, 255 | RFC 1952 section 2.3 header fields. Names are written as Latin-1 with a terminating 0. |

## Levels

Level 0 stores. Levels 1 to 9 follow the zlib scale (1 fastest, 9 smallest); 10 to 12 are optimal parses and correspond to
libdeflate 10 to 12. Levels 1 to 9 are compared with zlib-ng 1 to 9 (streaming), and 1 to 12 with libdeflate 1 to 12 (one-shot);
the mapping is by number.

| Level | Parser | Hash | Chain | Nice | MaxLazy | Tail | Good | Passes |
|---|---|---|---|---|---|---|---|---|
| 0 | stored blocks of at most 65535 bytes | | | | | | | |
| 1 | greedy, one probe, 2-way bucket | 4 bytes, 17 bits | 1 | 32 | 16 | 4 | | |
| 2 | greedy | 4 bytes, 15 bits | 10 | 40 | 32 | 4 | | |
| 3 | greedy | 4 bytes, 15 bits | 18 | 48 | 48 | 4 | | |
| 4 | lazy | 4 bytes, 15 bits | 8 | 32 | 16 | | 8 | |
| 5 | lazy | 3 bytes, 15 bits | 10 | 48 | 24 | | 12 | |
| 6 | lazy | 3 bytes, 15 bits | 20 | 64 | 64 | | 8 | |
| 7 | lazy | 3 bytes, 15 bits | 32 | 100 | 100 | | 16 | |
| 8 | lazy, look-ahead 2 | 3 bytes, 15 bits | 48 | 160 | 200 | | 16 | |
| 9 | lazy, look-ahead 2 | 3 bytes, 15 bits | 64 | 258 | 258 | | 16 | |
| 10 | optimal, binary tree | 4 bytes 16 bits + 3 bytes 16 bits | 24 | 64 | | | | 1 |
| 11 | optimal | as 10 | 64 | 128 | | | | 2 |
| 12 | optimal | as 10 | 256 | 258 | | | | 3 |

(The table above is the single source; the code's `LevelParameters.For` must match it.)

### Hashing and match finding

- Every hash is `(bytes_little_endian * 2654435761) mod 2^32`, shifted right to the table size. The 4-byte hash reads 4
  bytes, the 3-byte hash the low 3 of them.
- Window: 32768 bytes of history. Candidates farther than 32767 back are never taken. A match of length 3 farther than 4096 is
  not taken (it costs more bits than three literals).
- Hash chain finder: `head[hash]` holds the latest position, `previous[position & 32767]` the one before. Candidates are visited
  newest first, at most `Chain` of them; a candidate must beat the best length so far; the search ends at length `Nice`. Once a
  previous match is at least `Good` long, the next search uses a quarter of the chain. A match is the longest found; at equal
  length the newest (smallest distance) wins.
- Level 1 probes one bucket of two entries (newest and older) of the 4-byte hash (the bucket index is the hash with its low bit
  cleared) and takes the longest, ties to the newer.
- Levels 2 and 3 are greedy: take the match found at a position, then insert the positions inside it into the finder: all of
  them when the match is at most `MaxLazy` long, otherwise only its last `Tail` positions. Level 1 does the same.
  Levels 5 to 9 also keep a table of the latest position per 3-byte hash (15 bits), consulted when no match was found yet; levels 2
  to 4 hash 4 bytes only, find no 3-byte matches, and so skip that table (a measured trade: the table costs a store and a load
  per position, and at these levels the 3-byte matches it finds are few and mostly too far to pay).
- Dry spells (levels 1 to 7): the parser counts the positions it searched in a row without finding a match (a match resets the
  count; bytes it steps over are not counted). Once the count exceeds 16, each further failed search is followed by
  `(count - 16) / 8` bytes emitted as literals without searching or entering them in the finder (never past the end of the run being
  parsed, and never past the point where the sequence buffer must be checked), so data that does not compress costs little time.
  The count restarts for every call of the parser, as the other parser state does.
- Lazy (4 to 9): after finding a match shorter than `MaxLazy`, look at the next position. Matches are compared by a score,
  `4 * length - bsr(distance)` (`bsr` = index of the highest set bit; a nearer match costs fewer bits). The next position's match
  replaces the current one when its length is at least the current length and its score is higher by more than 1: one literal is
  emitted and the longer or nearer match is carried forward. A match of length 3 farther than 8192 is never taken (levels 2 and 3
  keep 4096). Repeat. Positions inside a taken match are inserted. Levels 8 and 9 also look two positions ahead when the next
  position did not replace the match: a match there that is longer and scores higher by more than 4 replaces it, with two literals
  emitted. The search at the next position asks for a match as long as the current one (`length - 1` is the length to beat) so
  that an equal-length nearer match is found.
- Binary tree finder (10 to 12): a binary tree per 4-byte hash plus a 16-bit 3-byte table; the search follows the tree for at
  most `Chain` nodes, returning every candidate that is longer than the previous one (so lengths ascend and distances ascend).
- A position with fewer than `Nice` bytes of input after it is searched but not inserted into the binary tree: its comparisons are cut
  short by the end of the data, bytes that agree up to the cut would be treated as equal for ever, and a flush followed by more
  input could then make the tree report matches that are not there. (The hash chain finder has no ordering to break and inserts them.)
- The optimal parser closes a block when the sequence buffer is within one run (16384 entries) of its nominal capacity of 65536
  entries, whatever size the buffer really has, so a one-call encoder (whose buffers are sized to the input) and a streaming one
  agree.
- Positions near the end of the buffered input, where fewer than 4 bytes remain, are not hashed and find no match until more
  input arrives; at the end of the stream the remaining bytes are literals.

### Optimal parse (10 to 12)

The input is parsed in runs of up to 16384 positions. For each run: collect matches at every position, then price a shortest path
by dynamic programming over costs in 1/16 bit, then emit the path. Costs come from the symbol statistics so far: literal and
a symbol with count `f` costs `(log2(2*total + symbols) - log2(2*f + 1))` in 1/16 bit, at least 1, where `log2` is computed by
integer squaring with 8 fractional bits (no floating point); match lengths add their extra bits. Before any statistics exist the
costs are those of the fixed codes (literal 8.5 bits, length 7 bits plus extra bits, distance slot 5 bits). The statistics are
updated with each emitted run and halved after every run. With `Passes` > 1 the run is parsed again with prices from the previous pass
and the cheapest result is kept. Matches longer than `Nice` end the search at that position and are taken whole.

### Sequence storage and writing

The parsers of levels 1 to 9 (and the two strategies that bypass the finder) do not store literals. A block holds the matches in
order, each with the input position it starts at; the literals between two matches are the input bytes between them, and the writer
reads them back from the input. The entry count that decides when a block is checked still counts literals and matches together,
so block boundaries and the bytes written are the same as if every literal had been stored. Levels 10 to 12 store every entry.

## Blocks

- Parsed entries accumulate in a sequence buffer of at most 65536 entries (literal or match). A block ends when the buffer is
  full, when the splitter asks, at a flush, or when the 256 KiB input window is processed.
- Splitter: every 512 entries (from 4096 entries into the block) the new entries' mix over 11 classes (literals in eight groups of
  32 values, then matches of length 3 to 5, 6 to 9, and longer) is compared with the block so far. When the sum of absolute
  differences of the class shares exceeds 450/1000, the block ends. All integer arithmetic.
- Writing: the writer prices stored, fixed and dynamic exactly (header bits included) and writes the cheapest; a tie prefers
  fixed, then dynamic, then stored. `FixedCodes` chooses between fixed and stored only. Dynamic codes are built by the length-limited Huffman
  builder (maximum 15 bits, 7 for the code-length alphabet): standard Huffman lengths from frequencies sorted by (frequency,
  symbol), then, if any length exceeds the limit, overlong codes are shortened and the freed space is given to the most frequent
  symbols. At least two literal/length and two distance symbols are always coded so every code is complete. Code lengths are
  sent with run-length symbols 16, 17 and 18 chosen greedily. Codes are assigned canonically (RFC 1951 section 3.2.2).
- The final block carries BFINAL. An empty stream is one empty fixed block (zlib and gzip: header, `03 00`, trailer).

## Window and slide

The encoder buffers up to 262144 bytes. When the buffer is full it parses everything except the last 266 bytes, closes the block,
and slides the buffer by a multiple of 32768 so that at least 32768 bytes of history remain. Output does not depend on how the
caller splits its input among calls, because parsing waits for a full window or a flush.

## Flush

- `None`: nothing forced.
- `Sync`: parse everything, close the block, then write an empty stored block (`00 00 FF FF` after byte alignment). Matches may
  still reach earlier data.
- `Full`: as `Sync`, and later matches may not point before the flush point (the stream can be decoded from there on).
- `Finish`: parse, close the final block, align, write the trailer. A second flush with nothing new written in between writes
  nothing more.

## Wrappers

- zlib header: CMF `78`; FLEVEL 0 when the level is below 2 or the strategy is HuffmanOnly or RunLength, 1 for levels 2 to 5,
  2 for level 6, 3 above; FDICT as above; FCHECK makes the pair a multiple of 31. Trailer: Adler-32 big endian.
- gzip header: `1F 8B 08`, FLG from the options, MTIME, XFL 2 for level 9 and above, 4 for level 1, else 0, OS (default 255);
  trailer CRC-32 and ISIZE (mod 2^32), little endian.
- `GetMaxCompressedLength(n)` = n + n/512 + 64 plus the wrapper size. A destination of that size never reports
  `DestinationTooSmall` for a one-call compression.

## Memory

A reused encoder owns about 2 MiB (window, sequence buffer, writer buffer, finders; the optimal parser a few MiB more) and
allocates nothing per call once warm.
