# Deflate64 encoder

Normative notes for `Deflate64Encoder`: what a later port must do to reproduce the same bytes. The symbols and limits are in
`deflate64-derivation.md`.

## Structure

- A buffer of 64 KiB of history plus a 128 KiB chunk. Input is parsed only when the buffer is full, on `Flush` and on the final
  call, so the output depends on the input bytes, the level and where flushes fall, never on how the caller splits its calls.
  When the buffer is full, the last 64 KiB stay as history and the rest is dropped.
- Matches: lengths 3 to 65538 (lengths of 258 and above always use symbol 285 with 16 extra bits), distances 1 to 65535.
  The encoder never emits distance 65536 or symbol 284 with extra 31 (both are accepted by the decoder, fact F3/F4).
- Match finders, by level (below):
  - Levels 1 to 4: a hash chain keyed by the first three bytes (multiplicative hash `(b0 | b1<<8 | b2<<16) * 2654435761 >> 16`,
    a 65536-entry head table, one `ushort` distance link per window position). A chain walk ends at distance 65535, at a
    zero link, or after the level's chain count. After a match, only the last 64 of its positions are entered into the finder.
  - Levels 5 to 9: the binary-tree finder of the Deflate encoder (`DeflateBinaryTreeMatchFinder`) over a 64 KiB window: trees
    keyed by a 16-bit hash of 4 bytes, plus a table of the latest position for each 16-bit hash of 3 bytes. A search reports,
    for every length from 3 up, the nearest distance that reaches it. Positions inside a taken long match are all entered.

## Levels

Level 0 stores (RFC 1951 stored blocks of at most 65535 bytes). Levels 1 to 9 are in four classes, which follow the classes of
the reference tool's `-mx` levels: 1 to 4, 5 and 6, 7 and 8, and 9 (7-Zip gives identical output within a class, and so do we).

| Levels | Parse | Depth | Nice length | Max lazy | Good length | Passes |
|---|---|---|---|---|---|---|
| 1 to 4 | lazy, hash chain | 16 | 32 | 16 | 8 | |
| 5, 6 (default 6) | optimal, binary tree | 16 | 64 | | | 1 |
| 7, 8 | optimal, binary tree | 32 | 128 | | | 1 |
| 9 | optimal, binary tree | 256 | 257 | | | 3 |

**Lazy parse (1 to 4).** Try the next position while the current match is shorter than Max lazy, searching with a quarter of
the chain when the current match is at least Good length; a longer match there wins and the current byte becomes a literal. A
search stops early at a match of at least Nice length. Matches of three bytes are taken at any distance.

**Optimal parse (5 to 9).** Work proceeds in runs of 4096 positions. Every match the finder reports at each position of the run
is collected (a search stops descending a tree at Nice length, and at Depth nodes). A match that reaches Nice length ends the
run: it is extended to its full length (up to 65538) and taken whole, and every position inside it is entered into the finder.
Otherwise the cheapest path through the run's positions (a shortest path over literals and matches of 3 to 257 bytes) is found
by dynamic programming over a price list in sixteenths of a bit. The first run is priced as the fixed Huffman code; later
runs are priced by `-log2` of the symbol's share of the statistics so far (a symbol with count c of total T costs
`(log2(2T + n) - log2(2c + 1))` bits, at least 1/16 bit, with `log2` by integer squaring to eight fractional bits and n the
alphabet size; the statistics are halved and the last run's counts added after each run). With more than one pass, the run is
parsed again with prices from the previous pass's choices (the run's counts plus a quarter of the earlier statistics). A match
length 258 or more is priced as symbol 285 with 16 extra bits only when taken whole as above.

The numbers are this library's choice, not 7-Zip's.

## Blocks

A block is closed at 32768 tokens, at the end of a parse round, or when the mix of symbols drifts: every 512 tokens (at the
end of each run for levels 5 to 9) the block's counts are given to `DeflateBlockSplitter` (at least 2048 tokens in a block, a
difference threshold of 300 per mille), which asks for a split when a new Huffman code would pay for its header. Each block is written as the cheapest of stored, fixed and
dynamic (a tie prefers fixed, then dynamic, then stored). Dynamic codes are length-limited Huffman codes from
`DeflateHuffmanBuilder` (limit 15 for literals/lengths and distances, 7 for code lengths), with HLIT at most 286 and HDIST at
most 32, at least two symbols in each alphabet, and the code lengths run-length coded with symbols 16, 17 and 18 greedily.
`Flush` ends the pending block, then writes an empty stored block (a sync flush, byte aligned). The final call writes the
final block and pads to a byte boundary; an empty input gives the two bytes `03 00` at levels 1 to 9 and one empty final stored block at level 0.

`GetMaxCompressedLength(n)` is `n + n/4096 + 64`.
