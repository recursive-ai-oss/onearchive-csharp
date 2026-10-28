# Deflate64: derivation and pinned facts

Deflate64 (zip method 9) has no specification we may implement from. This note records, for every difference from
Deflate (RFC 1951), the hypothesis, where it came from, the experiment that tested it, and the value the codec uses.
A value enters the code only through a public description cited here or an experiment committed beside it.

Sources:

- **A.** PKWARE APPNOTE 6.3.10, sections 4.4.3.2 (version needed 2.1 for Deflate64), 4.4.5 (method 9, "Enhanced Deflating
  using Deflate64") and 5.6.1 ("similar to Deflate but uses a sliding dictionary of up to 64K"). It states nothing else.
- **B.** Public description of Deflate64 in the encyclopedia article "Deflate" (section Deflate64): the dictionary grows from
  32 KB to 64 KB, "an extension of the distance codes to 16 bits so that they may address a range of 64 KB", and the length
  code "extended to 16-bit, so that it may define lengths of three to 65,538 bytes". It gives no symbol numbers or extra-bit
  counts; those are hypotheses H1 and H2 below, derived from the stated ranges and then tested.
- **C.** RFC 1951 for everything shared (block types, Huffman code construction, code-length alphabet, tables of length and
  distance codes 0 to 29 and 257 to 284).
- **D.** Black-box runs of the local 7-Zip 26.00 (`7z`): streams it writes, and hand-built streams it accepts or rejects.

The experiments are `testdata/deflate64/experiments.py` (using the laboratory `d64lab.py`: a bit writer, a zip wrapper and a
reference decoder with the Deflate64 parameters as switches); `experiments-output.txt` is its output, tagged E1 to E7 below.
Hand-built streams are wrapped in a zip entry with method 9 and run through `7z e -so`; "accepted, output identical" means
exit code 0 and the extracted bytes equal what the hand-built stream means under the hypothesis.

## Pinned facts

| # | Fact | Hypothesis from | Experiment | Value in the codec |
|---|---|---|---|---|
| F1 | History window | A (64K), B | E4: a match at distance 65536 after exactly 65536 bytes is accepted; E4c: after 65535 bytes it is rejected | Window 65536. A distance larger than the output so far (or the preset history) is an error |
| F2 | Length symbol 285 | B (lengths 3 to 65538 with 16 bits) | E3: lengths 258, 259, 1000, 40000, 65538 as symbol 285 plus 16 extra bits (length − 3) are accepted and decode identically; E3c: symbol 285 with no extra bits is rejected | Symbol 285 = base 3, 16 extra bits (3 to 65538) |
| F3 | Length symbols 257 to 284 | C (unchanged) | E1: 7-Zip's own streams parse with RFC 1951 tables for them; E3d: symbol 284 with extra 31 (length 258) is accepted | Unchanged: RFC 1951 bases and extra bits. 284 + 31 = 258 is accepted |
| F4 | Distance symbols 30 and 31 | B (distances reach 64 KB; the ranges that follow symbol 29) | E2: distances 32769, 40000, 49152 as symbol 30 and 49153, 60000, 65535, 65536 as symbol 31, each with 14 extra bits (distance − base), are accepted and decode identically | Symbol 30 = base 32769, symbol 31 = base 49153, 14 extra bits each (maximum 65536) |
| F5 | Wrong extra-bit count for symbols 30/31 | negative control | E2c: 13 and 15 extra bits for a distance that needs 14 are rejected or give a CRC failure | 14, as F4 |
| F6 | Distance symbols 0 to 29 | C (unchanged) | E1 (7-Zip streams), E2d (32768 as symbol 29) | Unchanged |
| F7 | Symbols 286 and 287 (literal/length) | C | E6: used in a fixed block, rejected | Still invalid when decoded |
| F8 | Block structure: stored, fixed, dynamic, BTYPE 3 | C | E6: BTYPE 3 rejected; E6: a lone empty fixed block (`03 00`) accepted; E1: 7-Zip's streams use the RFC 1951 headers and code-length alphabet | Unchanged |
| F9 | Dynamic header limits | none in A or B: tested directly | E5: HDIST 2, 30, 31 and 32 accepted; HLIT 258, 286, 287 and 288 accepted; E5b: lengths given to symbols 286 and 287, unused, accepted. E1: 7-Zip itself writes HDIST = 32 (and 23 to 31 at levels 7 and 9) | The decoder accepts HLIT up to 288 and HDIST up to 32, so symbols 30 and 31 can be coded; a code that gives 286/287 a length is accepted and the symbols fail only if decoded. The encoder writes HLIT at most 286 and HDIST at most 32 |
| F10 | Distance symbols 30/31 in a dynamic block | B | E5: symbols 30 and 31 used with HDIST 31 and 32 after 40000 and 60000 bytes of history, accepted | As F4 |
| F11 | Fixed-code block | C (fixed distance codes are 5 bits) | E2 to E4 use fixed blocks with distance symbols 30 and 31 as 5-bit codes 30 and 31 | Distance symbols 30 and 31 are valid in fixed blocks |
| F12 | Zip headers for method 9 | A (version needed 2.1) | E7: 7-Zip writes version needed 21, general purpose flags 0 at `-mx=1` and `-mx=9`, no extra field; made-by 0x033F; an empty file is stored (method 0), not method 9 | The writer sets version needed 21 and flags 0 for method 9 |
| F13 | Longest match an encoder may use | F2 | E3 | 65538. (7-Zip's own encoder was seen to emit lengths of at most 257, E1; that is its choice, not a limit) |

## What 7-Zip's encoder does (observed, not required)

E1 parses `7z a -tzip -mm=Deflate64 -mx=1,3,5,7,9` output for four inputs with the pinned parameters and with plain
Deflate parameters. On text and on data with far matches the Deflate64 parameters reproduce the input exactly and plain Deflate
fails on symbol 30 or 31; on long runs (all distances at most 2) both parameter sets succeed. Levels 1 and 3 give identical
output on the text input. Random data is stored (method 0). The longest length seen is 257 and the farthest distance 65534;
symbol 285 was not emitted by 7-Zip in these inputs, so F2 rests on E3 (7-Zip's decoder) and B, not on 7-Zip's output.

## Gaps

None open. F2's 16 extra bits and F4's 14 are fixed by E2 and E3 together with the ranges in B: 3 + 65535 = 65538, and
49153 + 16383 = 65536. Details not tested (and so not relied on): behaviour of 7-Zip when a stream continues past its final
block, and a Deflate64 stream in a gzip or zlib wrapper (not a format we offer).

## Decisions beyond the experiments [Agent default]

- The decoder follows 7-Zip's acceptance in F9 (HLIT up to 288, HDIST up to 32) because the interoperability target is 7-Zip's
  decoder; the encoder stays inside the narrower range.
- The Deflate64 decoder shares the Deflate decoder's structure (bit reader, Huffman table builder, window) with the Deflate64
  tables in place of the Deflate ones; no behaviour of the plain Deflate decoder changes.
