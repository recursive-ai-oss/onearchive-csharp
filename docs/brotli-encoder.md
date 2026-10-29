# Brotli encoder

Normative and language-neutral: a port that follows this text produces the same bytes. RFC 7932 fixes what a brotli stream
means and leaves the parse, the block boundaries, the context modelling and the prefix-code construction to the encoder;
everything it leaves open is fixed here. Output depends only on the input bytes, the options, and where flushes fall. It never
depends on the CPU, the runtime, the thread count or how the caller splits the input into calls. Nothing that decides a byte
uses floating point: costs are integers or fixed-point.

The design is this library's own, built from the RFC and from the published description of brotli (Alakuijala et al., "Brotli:
A General-Purpose Data Compressor", ACM TOIS 2019) and the literature on cost-based (shortest-path) parsing. The numbers
below are measured, not copied from another encoder. Its streams decode with `brotli` 1.2.1 and with this library's decoder.

## API shape

- Tier 1: `Brotli.Compress(data, BrotliOptions?)`, `Brotli.TryCompress`, `Brotli.GetMaxCompressedLength`.
- Tier 2: `BrotliEncoder` (`OperationStatus Compress(source, destination, out consumed, out written, bool isFinalBlock)`,
  `Flush`, `Reset`) and `BrotliStream(Stream, BrotliOptions?, leaveOpen)` in write mode (`Flush` is the flush below).
- Tier 3, public: `BrotliBitWriter` (LSB-first bit writer, RFC 7932 section 1.5.1), `BrotliHuffmanCode` (length-limited
  prefix-code lengths and canonical codes from a histogram) and `BrotliPrefixCodeWriter` (the simple and complex prefix-code
  descriptions of section 3.4 and 3.5). Together they are enough to write a meta-block by hand.
- Internal on purpose: the match finders, the block splitter, the clusterer, the dictionary index and the meta-block writer. They
  share one buffer and one set of tables with the engine, and their interfaces still move with the tuning; making them public
  would freeze that. They are described below so that a port can reproduce them.

## Options

| Option | Default | Meaning |
|---|---|---|
| `Quality` | 11 | 0 to 11, table below. Same numbering and meaning as the `brotli` tool's `-q`. |
| `WindowBits` | 22 | 10 to 24. A copy reaches back at most `2^WindowBits - 16` bytes (RFC 7932 section 9.1). |

Large-window brotli (window above 24) is not part of RFC 7932 and is not produced.

## Meta-blocks and flushing

- Input is gathered in a buffer holding the window followed by the block being filled. A block is coded when it holds
  `BlockSize` bytes (table below), on a flush, and at the end. Block boundaries therefore depend on the input length and the
  flush positions only, never on how the input was cut into `Compress` calls.
- Each block is one meta-block. The stream header is the WBITS field of section 9.1 (one bit for 16, otherwise the shortest of
  the three forms that can name the window).
- **Flush** codes the pending bytes as a (non-last) meta-block, then writes an empty metadata meta-block (ISLAST 0, MNIBBLES
  code 3, reserved bit 0, MSKIPBYTES 0, padding to a byte), which ends the output on a byte boundary. A flush with nothing new
  since the last one writes nothing.
- **End** codes the pending bytes as the last meta-block; with no pending bytes it writes the empty last meta-block
  (ISLAST, ISLASTEMPTY).
- **Uncompressed fallback:** if a meta-block's compressed form takes more bits than the uncompressed one (header, padding and the
  bytes), it is written uncompressed. A last meta-block cannot be uncompressed, so it is written as a non-last uncompressed one followed by
  an empty last meta-block.
- State carried across blocks: the window, the match finder tables and the four last distances (so a block may begin with a
  copy that repeats the previous block's last distance).

## Levels

| Q | Block | Table | Hash bytes | Depth | Long table | Parse | Contexts, splitting | Dictionary |
|---|---|---|---|---|---|---|---|---|
| 0 | 128 KiB | 2^15 heads | 5 | 1 | | greedy, one probe | none | no |
| 1 | 128 KiB | 2^17 heads | 6 | 1 | | greedy, one probe | none | no |
| 2 | 128 KiB | 2^16 x 2 | 5 | 2 | | greedy | none | no |
| 3 | 256 KiB | 2^15 x 8 | 6 | 4 | | lazy 1 | literal contexts | no |
| 4 | 1 MiB | 2^15 x 8 | 6 | 8 | | lazy 1 | + block splitting | no |
| 5 | 4 MiB | 2^16 x 16 | 6 | 8 | | lazy 1 | + context mode search | no |
| 6 | 4 MiB | 2^18 x 16 | 6 | 16 | 2^16 x 8, 8 deep | lazy 1 | + distance parameter search | identity |
| 7 | 4 MiB | 2^18 x 32 | 6 | 24 | 2^16 x 16, 16 deep | lazy 2 | same, finer literal splitting | identity |
| 8 | 4 MiB | 2^18 x 32 | 6 | 32 | 2^17 x 16, 16 deep | lazy 2 | same | identity |
| 9 | 4 MiB | 2^17 x 64 | 6 | 48 | 2^17 x 32, 32 deep | lazy 2 | same | identity |
| 10 | 1 MiB | 2^20 binary tree | 4 | 64 | | shortest path, 2 passes | same, finest literal splitting | all transforms |
| 11 | 1 MiB | 2^20 binary tree | 4 | 128 | | shortest path, 3 passes | same | all transforms |

"Table" is the number of hash values times the slots each owns ("x 16"); "Depth" is the number of entries of a bucket (or tree nodes at q10
and q11) visited per position. Qualities 0 to 5 skip ahead on incompressible data: after a miss the position advances by
`1 + (bytes since the last copy >> S)`, with S = 6 at qualities 0 to 2 and 5 at qualities 3 to 5 (the positions skipped over are still
entered in the table at q4 and q5; at q2 and q3 see "Sparse entry" below). Qualities 6 to 11 do not skip. The nice length (a match this long ends the search) is 64, and 128 at qualities 10 and 11. The
table is the single source; `BrotliQualityPlan.For` must match it.

## Match finding

- **Hash of a position**: for 4 bytes, `(little-endian 4 bytes * 0x1E35A7BD mod 2^32) >> (32 - HashBits)`; for 5 to 8 bytes, the little-endian
  64-bit word with all but its first `n` bytes cleared (shifted up so the kept bytes are the high ones), times `0x9E3779B185EBCA87 mod 2^64`,
  shifted down to `HashBits`. (The tables of q0 to q2 do not shift the kept bytes up: their hash is `((word & (2^(8n) - 1)) * 0x9E3779B185EBCA87 mod 2^64) >> (64 - HashBits)`.) A position is entered only if every byte its hash reads is inside the block; the others are entered with
  the next block, because bytes past the end of the block are not input yet.
- **Hash head** (q0, q1): one candidate per bucket, the latest position, stored with the four bytes found there (a slot is two
  ints) so that a candidate that does not agree on them is refused without reading the input at its position. Hashes are of 5 bytes at q0 and 6 at q1,
  taken from the 64-bit word at the position, so a table candidate always agrees on that many bytes before its length is measured and
  copies found through the table are at least that long (`FastHashLength` in the plan). Only positions with eight bytes inside the
  block are probed or entered; the last seven bytes of a block are literals.
- **Two-way table** (q2): each hash value (5 bytes, taken the same way) owns two slots, the two latest positions, each with its four
  bytes; entering a position moves the newer slot to the older and writes the new one. Both are candidates, newest first. This is the
  bucketed table of q2 to q9 with a bucket size of 2 and a depth of 2, minus the entry counts.
- **Bucketed table** (q2 to q9): each hash value owns `2^BucketBits` slots holding the latest positions with that hash, the oldest slot
  overwritten first (a count of entries made per bucket says where the next goes). Candidates are visited newest first, at most Depth
  of them; a candidate must agree with the position on the hashed bytes and on the byte just past the best length so far; the search ends
  at the nice length. Every position that is not skipped is entered before the next one is searched, so a lazy probe sees the
  positions the main probe entered.
- **Long table** (q6 to q9): a second bucketed table keyed by a hash of the first twelve bytes
  (`(word0 * 0x9E3779B185EBCA87) xor (word1 * 0xC2B2AE3D27D4EB4F) >> (64 - bits)`, where word0 is the first eight bytes and word1 the next four).
  It is searched first, newest first and at most its own depth, so that long matches in repetitive input are found even when many newer
  positions share the shorter hash. Positions enter it together with the main table, only when twelve bytes are inside the block.
- **Binary tree** (q10, q11): each bucket is the root of a tree of positions keyed by the string starting there, re-rooted at
  each new position (each position stores a smaller and a larger link). The nodes met on the way down are the candidates, with
  ever longer common prefixes. Comparisons stop at the nice length, and a node that agrees that far is replaced by the new
  position, which inherits its subtrees. A position is entered only if `position + niceLength` is inside the block; positions
  nearer the end are searched but entered with the next block, because the order of a string against the others must not be
  decided by bytes that are not there yet. A candidate's length is checked by comparing bytes before it is used.
- The four last distances are always tried first (any length from 3 in the greedy and lazy parses, from 2 in the shortest-path
  parse), then the finder's candidates. Distances beyond the window or the data so far are never used.
- **Static dictionary** (q6 and up, only when the best match found is shorter than 12): words are indexed by a hash (18 bits) of
  their first four bytes, the lowest word ids first. A hit is accepted only after the whole transformed word has been compared
  with the input. q6 to q9 try the identity transform, q10 and q11 all 121. A dictionary copy uses the distance
  `maxDistance + 1 + word id` where `maxDistance = min(window, bytes so far)` (RFC 7932 section 8) and does not enter the
  last-distance ring. A word is looked for only if it could beat the match already found: its copy costs at least
  `224 + 16 * (extra bits of maxDistance + 1)` in the score below, so words covering fewer bytes than `(best score + that cost) / 90`
  (rounded up) are skipped; this changes no choice, it only saves work. The all-transforms search of q8 and q9 in earlier
  versions gained under 0.3% in size on text and XML at 6 to 10 times the time, so those qualities use the identity transform only. At q5 the identity search gained under 0.1% on text, XML and mixed data and cost
  a fifth of the time, so q5 does not search the dictionary.

## Greedy and lazy parse (q0 to q9)

Costs are in 1/16 bit. A match's score is `covered * 90 - cost`, where cost is 144 for a copy at the last distance, 200 for one of
the other three recent distances, and `224 + 16 * (extra bits of the distance)` for a new distance or a dictionary word. A match
is taken only if its score is positive; ties go to the one covering more bytes. With Lazy of 1 or 2, a match is postponed while
a match one (or two) positions later scores more by over 90 (180) than this one, for matches covering under 64 bytes; the probe
for the later positions uses half the depth (and half the long table's depth).

**q0 and q1** use one loop. At each position with eight bytes inside the block it reads the table slot and replaces it with the
position. If the slot's four bytes agree with the position's and the distance is within the window, the table has a candidate. Only
then is anything else done, and the candidate is the only copy considered: a position with no table candidate is a miss, whether or
not some other distance would have matched. The copy's length is the number of equal bytes from the position and the candidate,
limited by the end of the block; it is taken only if it is at least `FastHashLength` and its score is positive. A copy at the last
distance used (a distance changes only through a copy with an explicit distance) is coded with short code 0 and scores with a cost of
144; any other distance is coded in full (RFC 7932 section 4, distance code 16 and up) and scores with `224 + 16 * (extra bits)`. The
other short codes (the three older recent distances and the nearest-neighbour codes) are not used. They save little on these inputs
and testing for them costs more than the bits they save. After a copy, its second byte and its last two enter the table (only when the
copy ends at least eight bytes inside the block). After a miss the position advances by `1 + (bytes since the last copy >> 6)`.

**q2** looks at the two table slots at a position with eight bytes inside the block. A slot is a candidate when it agrees on the
first four bytes and lies within the window. Each candidate's copy is measured as at q0 and scored with the q0 costs; copies shorter
than `FastHashLength` are not candidates. The better score wins; on equal scores the newer slot wins. Only the last distance has a short
code, as at q0. A position with no candidate whose score is positive is a miss. Entry follows "Sparse entry".

**q3 to q9** try the four last distances first (any length from 3), then the bucket candidates, then the dictionary. After a copy
every position it covers is entered, except at q3 (below).

**Sparse entry (q2 and q3).** After a copy of 32 bytes or more only its last two positions are entered, and positions stepped over by
the skip-ahead are not entered; the position just before the next probe is. The positions are a small share of what a later search
finds, and entering them costs a hash and two stores each. The measured price is under 0.2% of size on text.

**Command records (q0 to q2).** The parse computes each command's insert-and-copy symbol, extra bits, distance symbol and distance
context at the moment it finds the command, and counts the symbols into the histograms the writer builds its prefix codes from. The
literal histogram is counted by the writer over the input between copies. In a block whose commands carry four literals or more on average,
the writer counts and writes the first eight literals of every run through lookups that read eight bytes from the start of the run
whatever its length and give zero bits for those past its end; other blocks use plain loops. Neither changes a bit written. The buffer
keeps sixteen bytes beyond the data for those reads.

## Shortest-path parse (q10, q11)

Per block (1 MiB):

1. **Candidates.** For every position the binary tree (and, where the tree's best candidate is under 12 long, the dictionary)
   lists candidates `(length, distance)`.
2. **Costs.** Costs are in 1/256 bit and path costs are 64-bit. The first pass uses the block's byte histogram for literals
   (`log2(total / count)`, unseen bytes `+ 3` bits, at least 1/8 bit), 9 bits per command symbol, 4 bits for distance symbols
   below 16 and 6 for the others. Later passes use the symbol statistics of the previous pass's commands (unseen symbols
   priced 3 bits, literals 4 bits above the seen ones).
3. **Path.** A forward dynamic programme over positions, one node each, holding the cheapest cost, its predecessor, the number
   of literals pending and the four last distances. Edges from a node: one literal; each of the four recent distances at lengths
   `max(2, len - 6)` to `len`; for each tree candidate its lengths from just above the next shorter candidate (at least 4,
   and at most 10 below its own length) using the nearest distance among candidates at least that long; each dictionary
   candidate at its covered length. A command's price is its command-symbol cost, its insert and copy extra bits, and its distance
   symbol cost (none if the distance is the last one and the command can leave it implicit) plus distance extra bits. A candidate
   of at least the nice length is taken whole and its interior is skipped.
4. **Distances in the model** are priced as NPOSTFIX 0, NDIRECT 0; the writer may then choose better parameters.

## Entropy coding of a block

Done by the meta-block writer, in this order (each choice by estimated size, integer arithmetic, ties to the lower index):

1. **Distance parameters.** q6 and up try `(NPOSTFIX, NDIRECT)` = (0,0), (0,8), (1,0), (1,8), (2,0), (2,12) and keep the one with
   the smallest distance entropy; below q6 it is (0,0). Distances are mapped onto the short codes (RFC 7932 section 4) first.
2. **Block splitting** (q4 and up) separately for literals, commands and distances (section 6). The sequence is cut into equal
   segments: for commands and distances at least 256 symbols and at most 128 segments (at q4: 32, merged pair by pair up to 16 of them;
   the pairwise merging of long histograms is the largest cost of the splitter, and the estimated gain of the extra segments is
   small); for literals at least 1024 symbols and at most
   128 segments at q4 to q6, at least 512 and at most 256 at q7 to q9, at least 256 and at most 2048 at q10 and q11.
   Segment histograms are clustered into at most 256 block types, then refined up to four times (stopping early when nothing
   moves) by a Viterbi search over the segments where a type change costs 14 bits. The split is kept only if its estimated cost, headers and switches included,
   beats a single type.
3. **Clustering** (`BrotliClusterer`): the cost of a histogram is its entropy; merging two costs the entropy of the sum minus the
   parts'. A merge is taken when the increase is less than the estimated cost of one more prefix code (4 bits per used symbol
   plus 20). Up to 160 histograms (64 for the literal block types at q4 to q6, 96 at q7 to q9) the cheapest pair is merged repeatedly;
   beyond that histograms are taken largest first, each joining the nearest group or opening one, then two rounds move each histogram to the group that codes it best. Entropy is
   computed with an integer table of `x log2 x` in 1/65536 bit.
4. **Literal contexts** (q3 and up): each literal block type gets a context mode (RFC 7932 section 7.1): LSB6, MSB6, UTF8 or
   Signed. q5 and up try all four per type, by entropy plus an estimate of the header; below that UTF8 is used. The 64 contexts of
   each type are clustered onto trees (at most 256 trees in total). Distance contexts (4 per type, section 7.2) are clustered
   the same way. q0 to q2 use one tree and one context.
5. **Prefix codes.** Lengths from `BrotliHuffmanCode` (plain Huffman: leaves ordered by (count, symbol), two merged queues,
   maximum length 15, a code longer than the limit is rebuilt with every count raised to a floor of 1, 2, 4, ... until it fits). Codes with one used symbol are written as
   simple codes with zero bits per symbol; codes of up to four symbols use the simple form, the rest the complex form with
   run-length codes 16 and 17 for repeats, and a code-length code of at least two symbols.
6. **Commands** are written with insert-and-copy symbols (an implicit last distance when allowed), then the extra bits, the
   literals, and the distance.

## Determinism notes

- Hashes are the fixed multiplicative hash above. No `HashCode`, no `Dictionary` order, no timing.
- Every cost is an integer; logarithms come from a table built by integer digit-by-digit squaring (1/65536 bit).
- SIMD is not used in the encoder; the only vectorised helper is the common-prefix length, which computes the same function. Cache
  prefetch hints in the match finders (the tables of q1 and q2, and the bucketed tables) load table lines early and change no result. The meta-block writer packs a command's
  codes into a bit accumulator held in locals; that only changes how the bits are put out, never which bits.
- Block size depends on the quality alone, so one thread or many (the encoder is single-threaded) gives the same bytes.

## Measured

Sizes against `brotli` 1.2.1 at `-w 22` are in the unit report and the bar results, not here: a number that moves with tuning
does not belong in a normative document.
