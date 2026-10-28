# Deflate64 encoder

Normative notes for `Deflate64Encoder`: what a later port must do to reproduce the same bytes. The symbols and limits are in
`deflate64-derivation.md`.

## Structure

- A buffer of 64 KiB of history plus a 128 KiB chunk. Input is parsed only when the buffer is full, on `Flush` and on the final
  call, so the output depends on the input bytes, the level and where flushes fall, never on how the caller splits its calls.
  When the buffer is full, the last 64 KiB stay as history and the rest is dropped.
- Matches: lengths 3 to 65538 (lengths of 258 and above always use symbol 285 with 16 extra bits), distances 1 to 65535.
  The encoder never emits distance 65536 or symbol 284 with extra 31 (both are accepted by the decoder, fact F3/F4).
- Match finder: a hash chain keyed by the first three bytes (multiplicative hash `(b0 | b1<<8 | b2<<16) * 2654435761 >> 16`,
  a 65536-entry head table, one `ushort` distance link per window position). A chain walk ends at distance 65535, at a
  zero link, or after the level's chain count. A three-byte match farther than 4096 bytes is not taken.
- After a match, only the last 512 of its positions are entered into the finder.

## Levels

Level 0 stores (RFC 1951 stored blocks of at most 65535 bytes). Levels 1 to 9:

| Level | Parse | Chain | Nice length | Max lazy | Good length |
|---|---|---|---|---|---|
| 1 | greedy | 2 | 16 | | |
| 2 | greedy | 4 | 32 | | |
| 3 | greedy | 16 | 64 | | |
| 4 | lazy | 8 | 32 | 16 | 8 |
| 5 | lazy | 16 | 64 | 32 | 16 |
| 6 (default) | lazy | 32 | 128 | 64 | 32 |
| 7 | lazy | 128 | 258 | 128 | 64 |
| 8 | lazy | 512 | 258 | 258 | 128 |
| 9 | lazy | 2048 | 1024 | 1024 | 256 |

Lazy parsing tries the next position while the current match is shorter than Max lazy, searching with a quarter of the chain
when the current match is at least Good length; a longer match there wins and the current byte becomes a literal. A search
stops early at a match of at least Nice length. The numbers are this library's choice, not 7-Zip's level semantics; the level
numbers do not correspond to 7-Zip's `-mx` values.

## Blocks

A block is closed at 32768 tokens or at the end of a parse round. Each block is written as the cheapest of stored, fixed and
dynamic (a tie prefers fixed, then dynamic, then stored). Dynamic codes are length-limited Huffman codes from
`DeflateHuffmanBuilder` (limit 15 for literals/lengths and distances, 7 for code lengths), with HLIT at most 286 and HDIST at
most 32, at least two symbols in each alphabet, and the code lengths run-length coded with symbols 16, 17 and 18 greedily.
`Flush` ends the pending block, then writes an empty stored block (a sync flush, byte aligned). The final call writes the
final block and pads to a byte boundary; an empty input gives the two bytes `03 00` at levels 1 to 9 and one empty final stored block at level 0.

`GetMaxCompressedLength(n)` is `n + n/4096 + 64`.
