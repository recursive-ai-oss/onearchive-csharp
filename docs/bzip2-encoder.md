# bzip2 encoder

`RecursiveAI.OneCompress.BZip2`: compression to bzip2 streams. The format is described in `bzip2-decoder.md`; every choice the format
leaves to the writer, with the experiments that pinned or bounded it, is in `bzip2-encoder-derivation.md` (fact numbers F1…F9 below). A later
port must reproduce the same bytes from these two documents.

## Options

- `Level` 1–9, default 9: the block size level in the header; blocks hold at most `Level × 100 000 − 19` first-stage bytes (F1).
- `MaxCodeLength` 9–20, default 17: the longest Huffman code (F5). The format allows 20; the tool writes at most 17.

The output depends only on the input bytes and these two options, never on how the input is cut into calls, the output buffer size,
the platform, vector support or the number of threads (there is one).

## The stream written

`BZh` + the level digit; one block per full block of input and one for the remainder; the end marker with the combined CRC; zero
bits to a byte boundary. Empty input gives the 14-byte stream with no block (F7). Several streams are made by concatenating outputs.

## Per block

1. **First stage** (`BZip2RunLengthEncoder`). Runs of equal bytes: a run of 1–3 bytes is written as is; a run of 4–255 is the byte four
times and a count byte of the length minus 4 (F2). Longer runs are cut at 255 and the rest starts a new run, even in the same byte. The block
capacity is `Level × 100 000 − 19` bytes of output; a unit (a literal, or a run with its count) that does not fit starts the next block, so a
run in progress at the end of a block is written out with its count and the next block begins afresh (F3). The block CRC is the CRC of the input
bytes of the block (F7); the combined CRC is `rotl(combined, 1) xor blockCrc`.
2. **Sort** (`BZip2BlockSorter`). The last column of the sorted cyclic rotations of the block, and the row of rotation 0 (F6). Ties between
equal rotations (a periodic block) go by rotation index, so the lowest index comes first and the origin is its row. Method: prefix doubling
over group ranks, starting from the first two bytes, sorting each unsorted group by (rank of the rotation `h` bytes on, rotation index);
see the class remarks. Any correct sort with the same tie rule gives the same bytes.
3. **Move-to-front and zero runs** (`BZip2BlockWriter`). The list starts as the bytes in use, ascending. A move-to-front position 0 repeated
*n* times is *n* in bijective base 2, least significant digit first, `RUNA` (0) for digit 1 and `RUNB` (1) for digit 2; any other position *p*
is symbol *p* + 1; the block ends with symbol `used + 1` (F8). A run still open at the end is written before it.
4. **Tables** (`BZip2TableChooser`, `BZip2HuffmanBuilder`). Symbols are split in groups of 50 (the last may be shorter), one selector per
group, ceil(symbols / 50) in all (F4). The number of tables is 2 for fewer than 200 symbols, 3 below 600, 4 below 1 200, 5 below 2 400, else 6.
The groups start dealt to the tables in runs of consecutive groups (group *g* of *G* to table *g × k / G*, integer division), each table's
lengths are built from its groups' symbol counts, and six passes follow: each group goes to the table with the least total code length for
its symbols (ties to the lower number), then each table is rebuilt from the groups it received. Tables that no group uses are removed and the
remaining tables keep their order; if only one is left it is written twice. Lengths: Huffman's construction with every count raised to at
least 1, leaves ordered by (count, symbol), a merge taking the leaf on equal weights; if a length passes `MaxCodeLength`, all counts
become `(count + 1) / 2` and the tree is rebuilt. Codes are canonical (`BZip2HuffmanTable`).
5. **Header and data.** Block magic, CRC, a zero randomised bit, the origin row (24 bits), the symbol map, the table count (3 bits), the
selector count (15 bits), the selectors as unary move-to-front indices over the table numbers (F9), each table as a 5-bit start length (the
first symbol's) and for every symbol `10` per +1 and `11` per −1 step then `0` (F5), then the symbols.

## Tiers

- **One call:** `BZip2.Compress`, `BZip2.TryCompress`. There is no tight bound on the output size ahead of time; incompressible input grows by about an eighth.
- **Spans:** `BZip2Encoder.Compress(source, destination, out consumed, out written, isFinalBlock)` and `Flush`. A call may consume
much and write nothing; a full block is coded in one go and its output (up to about 1 MB) is handed out over as many calls as the
destination needs. `Flush` hands out what is already coded and does not end the block in progress: ending a block early would change the output.
- **Stream:** `BZip2Stream(stream, BZip2Options, leaveOpen)` for writing; disposing ends the stream.
- **Parts:** `BZip2RunLengthEncoder`, `BZip2BlockSorter`, `BZip2BlockWriter` (move-to-front, runs, header, symbols), `BZip2TableChooser`,
`BZip2HuffmanBuilder`, `BZip2BitWriter`, plus the decoder's `BZip2Crc`, `BZip2MoveToFront` and `BZip2HuffmanTable`.

## Memory

At level 9: the block (900 000 bytes), the last column (900 000), the sorter (two `int` and one `long` per block byte, 14.4 MB), the
symbols (1.8 MB) and the output of one block (about 1 MB) — about 20 MB, allocated on first use and kept for later blocks.

## Checked against

`bzip2` 1.0.8 decodes our output at levels 1–9 over generated inputs (empty, one byte, runs of 3, 4, 5, 255, 256, 259, 260, 1 000 bytes,
text, random, periodic, 2.5 MB of zeros, inputs of capacity − 1 … 2 × capacity + 7 and a run across a block end), over Silesia files, and
concatenated with other streams; 7-Zip, Info-ZIP `unzip`, GNU tar and bsdtar read our zip entries (method 12) and `.tar.bz2` files. A table with
lengths up to 20 is accepted by the tool. Sizes against the tool: see the unit's report; within about 0.3% on dickens, xml, mr, reymont and
ooffice at levels 1, 5 and 9.

## Speed

On one core of a desktop x64 machine our compression of dickens takes about 1.0 s per 10 MB at any level (a quick, unscrubbed reading, not a
bar run); the sort dominates. Highly repetitive input (a period of five bytes over 3 MB) takes 1.4 s because the doubling needs many rounds.
