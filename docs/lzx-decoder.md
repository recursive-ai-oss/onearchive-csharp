# LZX decoder

Normative notes for `RecursiveAI.OneCompress.Lzx`: LZX as cabinet folders store it, decode only. A port should decode the same
streams from this text. No encoder exists.

Sources: Microsoft's [MS-PATCH] (LZX DELTA Compression and Decompression) 2.1 to 2.7, which describes the bit stream, the block
types, the trees and the call translation, and [MS-CAB] 2.4 for the data blocks that carry the frames. Where the two differ, or where
the wording is open, the reference extractor cabextract 1.11 was run as a black box on cabinets whose LZX streams came from a small
test compressor (`tests/OneCompress.Tests/Lzx/LzxTestEncoder.cs`, written from the same documents). Each choice below marked
**[tool]** was settled by building the stream both ways and seeing which one cabextract extracts byte for byte; the checks are
`CabInteropTests.FormatChoicesPinnedByTheReferenceTool` and the cabinets in `CabInteropTests`.

## Framing

- The stream is a sequence of **frames**. Every frame decodes to 32768 bytes except possibly the last. A cabinet keeps one frame in each
  data block (`cbUncomp` 32768; `cbData` the compressed size). There is no chunk-size prefix (that belongs to LZX DELTA).
- The bit stream is 16-bit little-endian words consumed from the most significant bit. After the last token of a frame the stream
  is padded with zero bits to a 16-bit boundary, and the next frame starts at the next word.
- Blocks, trees, the three repeated offsets and the window carry over from frame to frame; a block may span frames. A match never
  crosses a frame boundary or the end of its block (the decoder rejects one that does).
- The window is 2^15 to 2^21 bytes (the cabinet folder's `typeCompress >> 8 & 31`). It starts empty: an offset larger than the bytes
  decoded so far is an error. The decoder allocates the window up front.
- First bit of the first frame: 1 = x86 call translation on, followed by the 32-bit translation size (high 16 bits first); 0 = off.

## Blocks

Each block starts with a 3-bit type (1 verbatim, 2 aligned offset, 3 uncompressed; anything else is an error) and a 24-bit size, the
number of uncompressed bytes the block represents. A zero-size block is accepted and ignored.

- **Verbatim and aligned offset** blocks carry (aligned only) eight 3-bit code lengths for the aligned-offset tree, then the main
  tree as two parts (256 literals; then 8 x slots match symbols) and the length tree (249 symbols), each preceded by a 20-entry pretree
  of 4-bit lengths. Lengths are sent as differences `(previous - symbol + 17) mod 17` from the previous block's lengths (zero for the
  first block); the persisted lengths survive uncompressed blocks. Pretree symbols 17 and 18 are runs of zero lengths (4 + 4 bits,
  20 + 5 bits), 19 is a run of 4 or 5 (1 bit) equal lengths followed by one more pretree symbol giving their common value.
  **[tool]** The value of a run (19) is one absolute length for the whole run, computed from the previous length of the run's first
  element; it is not a run of equal differences.
- Codes are canonical: shorter codes first, equal lengths in symbol order. A set of lengths that oversubscribes the code space is an
  error. An incomplete set is accepted and fails only when an unassigned bit pattern is read (the reference tool also refuses an
  *empty* aligned-offset tree, so the test compressor never writes one; the decoder is more lenient).
- **Uncompressed** blocks: after the 27 bits of header the stream is padded with zero bits to a word boundary, **[tool]** and a
  header that already ends on a word boundary is followed by a whole zero word (1 to 16 bits of padding, as [MS-PATCH] 2.3.2.1 says).
  Then R0, R1 and R2 as 32-bit little-endian values (they replace the repeated offsets), then the raw bytes; an odd byte count is
  followed by one zero byte. An uncompressed block may span frames: each frame's compressed bytes hold that frame's part, with no
  padding word between.

## Matches

- A main symbol below 256 is a literal. Otherwise `m = symbol - 256`: length header `m & 7`, position slot `m >> 3`. Header 7 means
  the length tree gives `symbol + 9`; otherwise the length is `header + 2`.
- Slots 0, 1, 2 reuse R0, R1, R2 (slot 1 and 2 swap with R0). Other slots give formatted offset
  `base[slot] + footer`, the real offset is `formatted - 2`, and `R2, R1, R0 = R1, R0, offset`.
- Footer bits per slot: 0 for slots 0 to 3, then `min(17, slot / 2 - 1)`; bases are the running sums (`LzxPositionSlots`). A window of
  2^15, 2^16, ... 2^21 uses 30, 32, 34, 36, 38, 42, 50 slots.
- In an aligned-offset block a footer of 3 or more bits is split: the low 3 bits come from the aligned-offset tree, the rest are read
  verbatim, most significant first.
- **[tool]** Cabinet matches are at most 257 bytes. The extra-length field of [MS-PATCH] 2.6.6 does not exist in cabinet LZX: a
  stream written with it is not extracted correctly by the reference tool, and the decoder does not read it.

## Call translation

When enabled, each frame is processed after decoding with `frameOffset` the number of stream bytes before it ([MS-PATCH] 2.2.2):
nothing is done to frames starting at or beyond 2^30 or to frames of 10 bytes or fewer; otherwise for each `E8` byte at `i < length -
10`, with `value` the next four bytes as a signed little-endian integer and `current = frameOffset + i`, if `-current <= value <
size` the operand becomes `value - current` (when `value >= 0`) or `value + size`; then `i` advances by 5 in every case.
**[tool]** The four operand bytes are skipped after every `E8`, translated or not. The window keeps the untranslated bytes.

## Errors and limits

`CompressionFormatException` names the format `lzx` and the byte offset (the compressed bytes given so far plus the position in the
current frame): bad block type, oversubscribed tree, a code no symbol owns, a pretree run past the end of a tree, a match that ends
beyond its block or frame, an offset below 1 or beyond the decoded bytes or the window, and input that ends inside a frame. Output
beyond `LzxDecompressionOptions.MaxOutputSize` (default 1 GiB) raises `CompressionLimitException` before the frame is decoded.
After an error the decoder refuses frames until `Reset`.

## Speed

Direction only, one loaded x64 host, memory to memory, best of 7, streams from the test compressor at window 2^21, against
`cabextract -p` to `/dev/null` with its process startup (3.5 ms) subtracted: dickens (Silesia, 10.2 MB) 357 MB/s against about 384 MB/s;
the first 20 MB of mozilla (with call translation) 408 MB/s against about 304 MB/s. Not a bar run.
