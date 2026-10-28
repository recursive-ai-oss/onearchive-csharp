# bzip2 encoder: derivation of the writer's choices

bzip2 has no official specification, and the decoder note (`bzip2-decoder.md`) already records what a reader must accept.
This note covers the other half: every choice the format leaves to the *writer* that changes the compression ratio or whether
a reader accepts the stream. For each one: where the hypothesis came from, the experiment, and the value pinned. A value goes
into the encoder only through a public description or an experiment recorded here.

**Sources.** The Burrows–Wheeler report (DEC SRC 124, 1994: block sorting, move-to-front, the idea of Huffman-coding the result);
Manber and Myers (suffix arrays, prefix doubling, 1990); Larsson and Sadakane ("Faster suffix sorting", 1999) and Sadakane (a
BWT-oriented suffix sorter, 1998) for the sorting design; Bentley and Sedgewick (multikey quicksort, 1997); the public
descriptions of the bzip2 stream layout (the encyclopedia article's file-format section, the bzip2 manual page); the decoder note. Experiments run
`bzip2` 1.0.8 (13-Jul-2019) as a black box. The reference compressor's source and its heuristics are not consulted: where the tool's *observed*
output is listed below, it is evidence of what a reader accepts and of what a ratio comparison is up against, not a design to copy.

**Experiments.** `tools/bzip2/derive-encoder-facts.py` generates its own inputs from fixed seeds, compresses them with the tool, parses the
streams with `tools/bzip2/bzip2_inspect.py` (a parser written from the format description) and prints one `F` line per fact.
`testdata/bzip2/encoder-facts.txt` is its recorded output (E-numbers below name the experiment groups in the script).

## Pinned facts

| # | Question | Hypothesis and source | Experiment | Pinned value |
|---|---|---|---|---|
| F1 | How many first-stage bytes fit in a block at level L? | The level digit is the block size in units of 100 000 bytes (format description; decoder note: the tool accepts exactly `L × 100 000` symbols). | E1: random bytes without runs, 2.2 blocks long, levels 1, 2, 5, 9 | Blocks of `L × 100 000 − 19` bytes (99 981, 199 981, 499 981, 899 981), the rest in the next block. The reader accepts up to `L × 100 000`, so any limit up to that is valid. |
| F2 | How are runs coded in the first stage? | After four equal bytes comes a count of further copies (format description). The largest count is the writer's choice. | E2: `x`, *n* × `a`, `y` for *n* = 1…520 | A run of *n* ≥ 4 is `aaaa` plus a count byte *n*−4, and a run is cut at **255 bytes** (count 251, `fb`): 259 `a` become `aaaa fb aaaa 00`. A run of exactly 4 still gets its count byte (`00`). The next run starts fresh after a count byte even when the next byte is the same. The reader accepts counts up to 255 (decoder note); we write at most 251, as the tool does, so that a reader that checks the count against the tool's range accepts our streams. |
| F3 | May a run span blocks? What happens at the boundary? | Decoder note: runs do not cross blocks; a block that ends right after four equal bytes is rejected. | E2/F3: level 1, 99 960…99 981 varied bytes followed by 1 000 `z` | The tool closes the block between two pieces of a long run (`… zzzz fb` ends block 1; block 2 starts a new run of `z`). A block that is full is closed even though the same byte continues. Each run piece ends with its count byte. The tool's blocks can exceed 99 981 by up to 4 (the piece that crosses the mark is finished); ours never exceed the cap (a unit that does not fit starts the next block). |
| F3b | Exact capacity with no runs | as F1 | E1/F3b: 99 982 … 100 002 bytes | the first block holds exactly 99 981 bytes; 99 981 bytes fill a block completely. |
| F4 | How many tables, how many selectors? | Decoder note: 2…6 tables are accepted, 1…32 767 selectors, one selector per group of 50 symbols. | E3: text inputs of 1…200 000 bytes | Selectors = ⌈symbols / 50⌉ where symbols counts the end-of-block symbol. The tool writes 2 tables for fewer than 200 symbols, 3 up to about 600, 4 up to about 1 200, 5 up to about 2 400 and 6 above that, and always at least 2 (a 1-byte input has 2). The format accepts 2…6 for any block size, so the count is ours to choose (see "Our choices"). |
| F5 | Code lengths | Decoder note: lengths 1…20, delta-coded from a 5-bit start, canonical codes. | E4: skewed, uniform and two-symbol inputs, and a Fibonacci-weighted input | Every symbol of the alphabet gets a length, used or not (the two-symbol input has 16 coded symbols all with a length; each table's Kraft sum is exactly 1). The longest length seen is **17**, on a skewed input that needs more (the Fibonacci-weighted input needs only 12); the tool limits to 17. The reader accepts up to 20 (decoder note; our interop test of 18…20 is in `BZip2EncoderTests`). |
| F6 | The sort and the origin pointer | The report: sort all cyclic rotations, send the last column and the index of the original. | E5: *banana*, *abracadabra*, *mississippi*, 8 more | The last column is that of the sorted **cyclic** rotations (no end marker), and the origin is the row holding rotation 0, in all 11 cases without ties. With periodic input (`abab…`) several rows hold equal rotations and the tool names a different one from the first; any of them decodes to the same bytes, so tie order is ours (we use the lowest rotation index first). |
| F7 | CRCs, framing, padding | Decoder note (CRC over the block's input bytes before the first stage; combined = rotate-left-1 xor). | E6: empty, 1 byte, text, runs, 3 blocks | The block CRC is the CRC of the block's *original* bytes (before the first stage); the combined CRC is the rotate-xor of the block CRCs, stored after the end marker (0 for no blocks). An empty input is 14 bytes: `BZh`+level, the end marker, a zero CRC, no padding bit. Padding to a byte boundary is zero bits. The level digit is the requested level even for 3 bytes of input. |
| F8 | Symbols | Decoder note (RUNA/RUNB, end of block = used + 1, list starts as used bytes ascending). | E7 | confirmed: `abcabczzz` → `[4,4,3,0,4,0,4,0,4,5]` with 4 used bytes; a block using all 256 values has alphabet 258. A zero run still open at the end is written before the end symbol. |
| F9 | Selector coding | Decoder note (unary move-to-front index over table numbers). | E7/F9 | confirmed: a repeated table is index 0, written as a single 0 bit. |

## Our choices (all [Agent default]; the reader accepts the whole range, the reason is ratio unless stated)

- **Block size.** `L × 100 000 − 19` first-stage bytes, never split a unit (a literal, or a run piece with its count byte). Same cut points as the tool's for inputs without runs (F1), so ratio comparisons are like for like.
- **First stage.** F2: pieces of at most 255 bytes. Runs are cut at block ends (F3).
- **Sort.** Cyclic rotation sort by prefix doubling over group ranks (Manber–Myers, as organised by Larsson–Sadakane: bucket by the first bytes, then refine only unsorted groups), ties broken by rotation index. The result is the same for any sorting algorithm that sorts rotations and breaks ties by index, so the sorter can change without changing bytes. Origin = the row of rotation 0 (lowest index among equal rotations).
- **Tables.** 2…6 chosen from the number of coded symbols; the thresholds, the number of refinement passes and the initial split are tuned by measurement against the tool's sizes and recorded in `bzip2-encoder.md`. Refinement is a k-means-style loop over groups of 50: assign each group to the table that codes it in the fewest bits, recount, rebuild the lengths. Integer arithmetic only.
- **Lengths.** Huffman with ties broken by symbol index; limited to 17 by flattening the counts and rebuilding (F5), unused symbols counted as 1 so that every symbol has a length. 17 is the tool's limit; 20 is the reader's. The effect on size of 17 versus 20 is measured and recorded in `bzip2-encoder.md`.
- **Selectors.** Exactly ⌈symbols / 50⌉, move-to-front coded (F4, F9).
- **Header.** Start length = the first symbol's length, then ±1 steps; the largest step between neighbours is 16, far below the reader's 40.
- **Concatenation.** Multiple streams are not produced by one encoder; callers concatenate encoder outputs (the decoder reads concatenated streams).
