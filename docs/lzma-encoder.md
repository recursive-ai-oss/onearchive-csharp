# LZMA, LZMA2, xz and lzip encoders

Normative and language-neutral: a port that follows this text produces the same bytes. The LZMA specification, the xz file format
specification and the lzip manual fix the streams a decoder accepts; everything they leave to the encoder (the match finders, the
parse, the price model, the chunking, the preset parameters) is fixed here. Output depends only on the input bytes, the options and,
for LZMA2 and xz, where flushes fall. It never depends on the CPU, the runtime, thread counts or how the caller splits the input into
calls. Byte-identical output with the `xz` and `lzip` tools is not a goal: the streams are valid and the ratio is at parity.

## API shape

- Tier 1: `Lzma.Compress/TryCompress` (the `.lzma` format), `Lzma2.Compress` (raw LZMA2 plus its dictionary property byte),
  `Xz.Compress/TryCompress`, `Lzip.Compress/TryCompress`.
- Tier 2: `LzmaEncoder`, `Lzma2Encoder`, `XzEncoder`, `LzipEncoder` with
  `OperationStatus Compress(source, destination, out consumed, out written, bool isFinalBlock)` (`Lzma2Encoder` also has `Flush`), and
  the write-only streams `LzmaEncoderStream`, `XzEncoderStream`, `LzipEncoderStream` (`Flush()` flushes the inner stream only; it does
  not end a chunk, because that costs ratio).
- Tier 3: `LzmaRangeEncoder`, `LzmaPrices`, `LzmaMatchFinder` with `LzmaHashChainMatchFinder` and `LzmaBinaryTreeMatchFinder`,
  `LzmaEncoderCore` (window, probabilities, the fast and the normal parser, symbol coding) and `Lzma2Chunker`. The encoders are built
  from exactly these. `XzFilter` describes the filters in front of LZMA2.

## Options

`LzmaOptions` (used by every encoder; zero or null means "the preset's"):

| Option | Default | Meaning |
|---|---|---|
| `Preset` | 6 | 0 to 9. |
| `Extreme` | false | The slower variant of the preset (below). |
| `Lc`, `Lp`, `Pb` | 3, 0, 2 | Literal context bits (0 to 8, LZMA2: 0 to 4), literal position bits (0 to 4), position bits (0 to 4). LZMA2 requires `Lc + Lp <= 4`. |
| `DictionarySize` | preset's | 4096 to 768 MiB (a binary-tree finder keeps two 32-bit links per dictionary byte, which must fit one array). |
| `NiceLength` | preset's | 2 to 273, raised to the match finder's minimum (2, 3 or 4). |
| `Depth` | 0 = automatic | Candidates examined per position. |
| `Mode` | preset's | `Fast` or `Normal`. |
| `MatchFinder` | preset's | `HashChain3`, `HashChain4`, `BinaryTree2`, `BinaryTree3`, `BinaryTree4`. |
| `WriteEndMarker` | false | `.lzma` only; a stream of unknown length always gets the marker. |

`XzCompressionOptions`: `Lzma`, `Check` (default CRC-64), `BlockSize` (default 0 = one block), `Filters` (at most three before LZMA2).
`LzipOptions`: `Level` (6), `DictionarySize`, `MatchLength`, `MemberSize` (0 = one member).

## Presets

The dictionary sizes, match finders and modes of presets 0 to 9 follow the `xz` manual (dictionary 256 KiB, 1, 2, 4, 4, 8, 8, 16, 32
and 64 MiB; `hc4` for 0 to 3 (the manual says `hc3` for 0; `hc4` is as fast there and 4% smaller), `bt4` for 4 to 9; fast mode for 0 to 3, normal for 4 to 9). The manual leaves the nice
lengths and depths to the implementation; they were set by measuring size and speed against `xz` on the corpus subset below. On a
mixed 32 MiB Silesia sample the fast presets 0 to 3 write 0.2%, 5.8%, 1.9% and 0.9% more than the tool at the same preset; presets 4
to 9 are within 1%. Presets 1 and 2 trade size for speed on purpose: preset 1 does not look ahead past a match of 4 bytes (about 3%
more output, 28% faster), and preset 2 searches 12 candidates rather than 16 (about 1% more output, 10% faster).

| Preset | Dictionary | Match finder | Mode | Nice length | Depth |
|---|---|---|---|---|---|
| 0 | 256 KiB | HashChain4 | Fast | 6 | 6 |
| 1 | 1 MiB | HashChain4 | Fast | 8 | 8 |
| 2 | 2 MiB | HashChain4 | Fast | 24 | 12 |
| 3 | 4 MiB | HashChain4 | Fast | 48 | 24 |
| 4 | 4 MiB | BinaryTree4 | Normal | 16 | 24 |
| 5 | 8 MiB | BinaryTree4 | Normal | 32 | 32 |
| 6 | 8 MiB | BinaryTree4 | Normal | 64 | 48 |
| 7 | 16 MiB | BinaryTree4 | Normal | 64 | 48 |
| 8 | 32 MiB | BinaryTree4 | Normal | 64 | 48 |
| 9 | 64 MiB | BinaryTree4 | Normal | 64 | 48 |

Depth when not given: `16 + nice / 2` for binary trees, `4 + nice / 4` for hash chains (presets 0 to 3 use the table's depths).
`Extreme` switches presets 0 to 3 to `BinaryTree4` and `Normal` with nice length 64 and the binary-tree depth, and presets 4 to 9 to nice
length 273; the depth is at least 512 (binary tree) or 100 (hash chain).

Lzip levels (`lzip` manual, "Invoking lzip"): the dictionary size and match length of level 0 to 9 are 64 KiB/16, 1 MiB/5, 1.5 MiB/6,
2 MiB/8, 3 MiB/12, 4 MiB/20, 8 MiB/36, 16 MiB/68, 24 MiB/132 and 32 MiB/273. Level 0 uses `HashChain4` and the fast parser, the
others `BinaryTree4` and the normal parser. lc, lp and pb are 3, 0, 2.

## Window and determinism

The encoder copies input into a window with index 0 reserved (a zero byte before the stream, so the first literal's previous byte is 0).
Stream position of window index `i` is `i - 1 + slid`, where `slid` counts the bytes dropped when the window moved down.

- Capacity is `1 + history + slack + 4385`, with `history = max(dictionary, 65536) + 16`, `slack = clamp(dictionary, 64 KiB, 8 MiB)`.
  When the window is full it moves down so that `history` bytes before the next position stay; every stored position moves with it.
  Positions at or below the shift become "empty" (0).
- Parsing starts only when 4385 bytes (`4096` parse window, `273` longest match, `16` margin) follow the position, unless the caller
  drains (final block, `Flush`). So a decision never depends on how the input was cut into calls. A drain shortens the parse window near
  the end of the data, which is why flush positions are part of an LZMA2 stream's definition.
- `inputLength` and block/member sizes only change memory: a known input shortens the dictionary used for distances and for the cyclic
  arrays to the input length (no distance can exceed it), and an xz block or lzip member size caps both the dictionary and the hash table
  size to that size. Hash table sizes otherwise depend on the options' dictionary size, never on the input length.

## Match finders

All positions are window indexes; a stored 0 means empty. Distances are `position - stored`; a candidate farther than the dictionary
size is rejected. `available` is the number of bytes from the position to the end of the data; positions with fewer than the hash
width available are not inserted (the cyclic slot is cleared).

- **Hashes.** `hash2` is the first two bytes as a 16-bit number indexing a table of 65536 entries. `hash3` (finders with a 4-byte main
  hash) is `((v & 0xFFFFFF) * 2654435761) >> 14` into 2^18 entries, where `v` is the next four bytes little endian (computed in 32-bit
  unsigned arithmetic). The main hash is `(v * 2654435761) >> (32 - bits)` for 4 bytes, `((v & 0xFFFFFF) * 2654435761) >> (32 - bits)` for 3,
  and the 16-bit direct value for `BinaryTree2`; `bits = clamp(32 - lzcnt(dictionary - 1), 16, 24)`.
- **Short matches.** From `hash2`: if the stored position is within the dictionary and its first two bytes equal, a match of length 2
  is reported. From `hash3`: the common length with that position is measured (it can be 0 to 273) and reported if longer than what is
  known so far.
- **Hash chain.** A cyclic array of `dictionary + 1` links; the new position links to the old head of its main hash. A search walks
  `depth` links, skipping candidates whose bytes at the current best length differ, and reports each candidate that is longer than the best
  so far. It stops at the nice length (or `available`).
- **Binary tree.** Two links per position (smaller and greater subtree) in a cyclic array of `2 * (dictionary + 1)`. Inserting a
  position walks down from the main hash's old head for at most `depth` nodes, comparing from the shorter of the two matched prefixes
  `len0` and `len1`, reporting every node that beats the best length, and re-hanging the visited nodes on the new node's two sides. When
  the match reaches the nice length, the new node takes over the found node's two subtrees and the walk stops. `Skip` does the same
  walk without reporting.
- The lengths reported are strictly increasing; each is the closest distance found for it.

## Symbol coding

The probability layout is `LzmaModel`'s; everything is as in the LZMA specification's decoder, run backwards. The range encoder keeps a
33-bit `low`, a 32-bit `range`, a cache byte and a pending count: the first byte written is always 0, and `Flush` shifts out five
bytes. An end marker is a match of length 2 with distance field `0xFFFFFFFF`. Matched literals are used after a match or repeated match
(state at least 7) with the byte at `rep0`. Repeated-distance indexes rotate as in the specification.

## Prices

Prices are in 1/16 bit. The table has 128 entries, entry `i` for a probability `p` in `16 i` to `16 i + 15`: `round(16 * log2(2048 / (16 i + 8)))`,
computed by integer repeated squaring (the binary logarithm in fixed point with 8 fraction bits, rounded half up when scaled to 1/16);
the price of a 1 bit is the table entry of `2048 - p`. No floating point is used. The generator is `LzmaPrices.BuildTable`.

Derived tables are rebuilt from the current probabilities when `_age` (the number of symbols coded since the last rebuild) reaches 2048
in fast mode or 256 in normal mode, and after every state reset: 4 x 64 slot prices; 4 x 128 prices of distances below 128 (slot plus
low bits); 16 align prices; for the match and the repeated-match length coders and each position state, 272 length prices. A distance
of 128 or more costs its slot price plus 16 per direct bit plus its align price.

## Parsers

Both parsers run on a *horizon*: at most 4096 bytes, at most the bytes left, at most the room left in the chunk (below). The choice of
symbols for those bytes is made, coded, and the next decision starts after them.

**Fast.** At position `p`: the repeated lengths of the four distances and the finder's matches are known. If neither exists, a literal
is coded (or a one-byte repeated match when the byte equals the one at `rep0`). Otherwise each candidate (every repeated distance with
length 2 or more, every finder match with length clamped to the horizon) is priced as a fresh symbol, and the one with the largest
saving `length * literal - price` wins (a later match wins a tie, a repeated distance only wins by being strictly better), where
`literal` is the price of a literal at `p` including the is-match flag. A winner of at least the nice length is coded at once. Otherwise
the same choice is made at `p + 1` (after a literal; matches at `p + 1` come from the finder, repeated lengths use the current distances);
if coding a literal and then that candidate is cheaper than the first candidate over the same span (the shorter plan is padded with
literals at `literal` each), a literal is coded now and the decision is retaken at `p + 1`. That second look is made only when the
first winner is shorter than the *lazy length*: 3 for preset 0, 4 for preset 1, and the nice length (no limit of its own) for every other
setting. A winner of the lazy length or more is coded at once, which saves the finder call and the pricing at `p + 1`.

**Normal.** A shortest-path search over the horizon. Node `i` holds the cheapest price to reach `p0 + i`, its predecessor, the symbol
that was used, and the state and four repeated distances after it. Edges from node `i`: a literal (matched-literal pricing when the
state is 7 or more); a one-byte repeated match if the byte equals the one at `rep0`; for each repeated distance with length `l >= 2`,
every length 2 to `l`; for each finder match, every length from the previous match's length plus one up to its own. The search visits
nodes in order, asking the finder for matches at each, until the last reachable node is passed. A repeated or finder match that reaches
the nice length ends the search: that edge is taken unconditionally and its target is the end. Nodes beyond the horizon are never created. The path to the end node is coded
symbol by symbol and the finder is advanced (skipping) to the end. Pricing the same symbol twice never uses information that depends on
how many bytes were appended: all lengths are clamped to the horizon.

## Fast and normal modes with budgets

The caller (the LZMA2 chunker) limits how much output a call may produce. Before each decision the horizon is reduced to
`(limit - pending - 24) / 10` input bytes, where `pending` is the range encoder's delayed-plus-flush size. 10 is more than the worst
output per input byte (a 2-byte match with the least probable bits throughout is about 20 bytes), so a chunk can never pass its
limit. Coding stops when that horizon is below 1 (the budget is exhausted).

## LZMA2 chunks

A chunk is closed when it holds 2 MiB of input, when the budget above is exhausted (64 KiB of output), or when the caller drains. A
chunk left open when input runs out stays open: the next call continues it, so chunk boundaries do not depend on how input arrives.
When a chunk is closed, the range encoder is flushed. If `coded + header >= input + 3 * ceil(input / 65536)` the input is written as
stored chunks of at most 64 KiB (the first of the stream with control 1, the rest 2), and the next LZMA chunk resets the state. The window keeps at least 64 KiB of
history so the stored bytes are always available. Otherwise it is an LZMA chunk with control `0x80 | reset << 5 | (size - 1) >> 16` where
`reset` is 3 for the first chunk of a stream (dictionary, state and properties), 2 for the first LZMA chunk after a stored first
chunk (properties), 1 after a stored chunk (state), else 0; then the low 16 bits of the size minus one, the compressed size minus one, and
the properties byte when `reset >= 2`. A reset of the state restores the initial probabilities, state 0 and repeated distances 0. The
stream ends with control byte 0. The dictionary property byte is the smallest of 0 to 40 whose size is at least the dictionary size.

## `.lzma`

13-byte header: properties byte `(pb * 5 + lp) * 9 + lc`, dictionary size (the options' size, as a 32-bit little-endian number),
uncompressed size (64-bit; all ones when unknown). With a known size the end marker is written only if `WriteEndMarker`; the encoder
throws `InvalidOperationException` if the input differs from the declared length.

## xz

Stream header, blocks, index, stream footer as in the xz file format specification. A block header never carries the optional
sizes. Filter flags are written in the order of `Filters` (applied first to last when compressing), then LZMA2 (ID 0x21, one property
byte). Branch filters write their start offset (four bytes) only if it is not 0; delta writes `distance - 1`. Each block restarts the
dictionary, the filters (positions count from the filter's start offset) and the check. Bytes a branch filter holds back at the end of a
block are passed on unconverted. The check covers the block's original data. Block padding is zeros to a multiple of 4, and the index
lists `(header + compressed + check size, uncompressed size)` per block. Empty input gives a stream with no blocks. `BlockSize` fixes
the block boundaries (they are at exact multiples of it); with 0 the whole input is one block.

## lzip

Header `LZIP`, version 1, dictionary byte; the LZMA stream with lc/lp/pb 3/0/2 ends with the end marker; trailer CRC-32, data size and
member size (little endian). The dictionary byte is `log2 | wedges << 5` for the smallest `2^log2 - wedges * 2^(log2 - 4)` (log2
12 to 29, wedges 0 to 7) that is at least the dictionary size. `MemberSize` cuts the input into members of that many uncompressed bytes;
empty input is one empty member.

## Results against the reference tools

Measured with `xz 5.8.3` and `lzip 1.21` as black boxes (one-process CPU time for speed, so direction only), compressed size of this
library divided by the tool's, on dickens, mr, xml, ooffice, osdb, reymont, x-ray (Silesia) and the first 10 MB of enwik8.

`.lzma` against `xz --format=lzma -N` (preset N, `e` is extreme):

| Level | Ours (bytes) | Tool (bytes) | Size ratio | CPU time ratio |
|---|---|---|---|---|
| 0 | 25,143,280 | 25,284,236 | 0.9944 | 1.53 |
| 1 | 23,732,924 | 23,550,183 | 1.0078 | 1.40 |
| 2 | 22,968,553 | 22,881,358 | 1.0038 | 1.00 |
| 3 | 22,683,224 | 22,525,190 | 1.0070 | 0.79 |
| 4 | 20,475,572 | 20,489,845 | 0.9993 | 1.16 |
| 5 | 19,920,755 | 19,928,660 | 0.9996 | 1.13 |
| 6 | 19,837,931 | 19,838,748 | 1.0000 | 1.16 |
| 7 | 19,835,425 | 19,835,719 | 1.0000 | 1.12 |
| 8 | 19,835,425 | 19,835,719 | 1.0000 | 1.21 |
| 9 | 19,835,425 | 19,835,719 | 1.0000 | 1.08 |
| 0e | 21,033,266 | 21,057,324 | 0.9989 | 1.13 |
| 3e | 19,911,504 | 19,887,305 | 1.0012 | 1.10 |
| 6e | 19,811,459 | 19,816,691 | 0.9997 | 1.26 |
| 9e | 19,808,375 | 19,813,460 | 0.9997 | 1.17 |

`.lzip` against `lzip -N`:

| Level | Ours (bytes) | Tool (bytes) | Size ratio | CPU time ratio |
|---|---|---|---|---|
| 0 | 24,244,297 | 24,947,330 | 0.9718 | 2.69 |
| 1 | 24,076,232 | 24,078,459 | 0.9999 | 1.60 |
| 2 | 22,941,796 | 22,942,576 | 1.0000 | 1.36 |
| 3 | 21,907,588 | 21,888,925 | 1.0009 | 1.24 |
| 4 | 20,906,976 | 20,919,118 | 0.9994 | 1.12 |
| 5 | 20,256,806 | 20,257,304 | 1.0000 | 1.15 |
| 6 | 19,899,103 | 19,888,561 | 1.0005 | 1.09 |
| 7 | 19,831,810 | 19,811,712 | 1.0010 | 1.13 |
| 8 | 19,815,436 | 19,788,519 | 1.0014 | 1.18 |
| 9 | 19,809,609 | 19,780,671 | 1.0015 | 1.14 |

Presets 7 to 9 give one size here because no file in the subset is larger than 8 MiB. The CPU time ratio is one process each, cold start included, on a shared machine: it shows the direction only (below 1 is faster than the tool). The bar timings are taken separately on the benchmark machine. Levels 0 and 1 are the slowest relative to the tool (1.4 to 2.7 times).

## Experiments

- Preset parameters: the `xz` manual names the match finder, mode and dictionary of each preset; nice length and depth were tuned by
  running both encoders over the corpus subset and keeping the cheapest setting within 1% of the tool's size (fast presets: nice
  4 to 48 and depth 4 to 32 were compared; 0.9 to 1.04 of the tool's size across settings).
- Fast parser: a first version that took the longest match with a preference for repeated distances was 2.5% to 5.6% larger than `xz`
  on presets 1 to 3; pricing the candidates (above) brought it to within 1%.

## Speed notes (no effect on output)

The match finders issue prefetch hints for the hash buckets of positions 16 ahead and for the node a bucket 6 positions ahead holds,
and the tree walk prefetches both children of the node it is at. Hints change no table and no result. Reads in the walk, the
hash updates and the chain steps skip bounds checks behind `LzmaMatchRaw` (bounds-checked in the `Checked` build); every entry
point first checks that `position + min(available, 273)` lies inside the buffer, and the walk accepts a stored position only when
it lies behind the current one and within the dictionary.
