# Zstandard decoder: behavior

Normative notes for `RecursiveAI.OneCompress.Zstd`'s decoder. The format itself is RFC 8878; this page records what the
format leaves to the decoder and the choices made, so another implementation can match them.

## What is decoded

- Zstandard frames (RFC 8878 §3.1.1) and skippable frames (§3.1.2), one after another. Skippable frames are handed to
  `ZstdDecompressionOptions.SkippableFrameHandler` (variant = low four bits of the magic number, payload as a span).
  Without a handler they are skipped without being read into memory.
- Raw, RLE and compressed blocks; raw, RLE, compressed (one and four streams) and treeless literals; predefined, RLE,
  FSE-compressed and repeat sequence tables; Huffman weights stored directly or FSE-compressed; repeat offsets with the
  literals-length-zero rule.
- Window descriptors and single-segment frames; the content size field in all four widths; the dictionary ID field; the
  content checksum (low 32 bits of XXH64, seed 0), verified unless `VerifyChecksum` is false.
- Dictionaries in the Zstandard dictionary format (entropy tables and repeat offsets, content) and raw-content dictionaries.

## Choices the format leaves open

| Matter | Choice |
|---|---|
| Maximum window | `MaxWindowSize`, default 8 MiB. A larger declared window raises `CompressionLimitException` before any buffer is reserved. For single-segment frames the window is the content size. |
| Maximum output | `MaxOutputSize`, default 1 GiB, counted across frames since the last `Reset`. A frame whose declared content size exceeds what remains is refused up front; for other frames the count is checked as blocks are produced. |
| Memory of a streaming decoder | The decoder keeps a window of up to `2 x window size` (the history plus one block), grown in steps as output appears, never reserved up front from the header. Frames above 1 GiB of window therefore need `Array.MaxLength / 2`; they raise a limit exception rather than a framework `OutOfMemoryException`. A caller that gives a buffer large enough for a whole frame whose size is declared bypasses the window entirely: the output buffer is the history. |
| Declared size on one-shot calls | `Zstd.Decompress` allocates the declared size only when it is at most `1 MiB + 255 x input length`; otherwise it starts small and grows, so a tiny input cannot make it reserve the whole limit. |
| Content size mismatch | A frame that decodes to a different size than it declares is corrupt. |
| Block sizes | A block (content and decoded size) larger than `min(window size, 128 KiB)` is corrupt. |
| Offsets | A match offset must be at most the window size and at most the bytes produced so far in the frame plus the dictionary content; the dictionary is reachable only while the frame's output is within its window (RFC 8878 §5). Offset codes above 31 are rejected. |
| FSE table descriptions | Accuracy log at most 9 (literals and match lengths), 8 (offsets), 6 (Huffman weights). A description that does not complete its probabilities, or names symbols beyond the table's alphabet, is corrupt. A table of a single symbol is accepted in FSE-compressed mode. |
| Huffman | Weights up to 11; the implied last weight must complete a power of two with at least one weight-1 symbol; the code depth is at most 11. A stream that does not end exactly at its first bit is corrupt. A four-stream block with fewer than four literals whose last stream would be negative is corrupt. |
| Sequence section | A sequence count of zero must end the block; reserved mode bits must be zero; the sequence bitstream must be consumed exactly. |
| Dictionary selection | A frame naming an ID is matched against `Dictionaries`, then `Dictionary` (a raw-content dictionary with ID 0 matches any ID). A frame naming no ID uses `Dictionary`. A frame naming an unknown ID raises `CompressionDictionaryException`. |
| Dictionary validation | Formatted dictionaries: magic, ID, tables, three repeat offsets (each at least 1 and at most the content size), content. Raw content: at least 8 bytes. |
| Empty input | Decoding zero bytes is an error (a stream needs at least one frame); it raises a truncated `CompressionFormatException`. |
| Trailing bytes | Anything after a frame must be another frame or a skippable frame; otherwise the magic number is reported as wrong. |
| Skippable payloads | Buffered only when a handler is set, up to `MaxSkippableFrameSize` (default 1 MiB). |

## Streaming

`ZstdDecoder.Decompress(source, destination, out consumed, out written)` accepts any split of input and output. It returns
`Done` at the end of each frame (and each skippable frame), `DestinationTooSmall` when output is pending, and `NeedMoreData`
when it used all input mid-frame. Compressed blocks (at most 128 KiB) are decoded only once complete, from the caller's
span when it already holds the whole block and from a reused internal buffer otherwise. Raw and RLE blocks stream through.

## Hot-path safety

The decoder is written on spans. Nothing in `src/` uses `Unsafe` or `MemoryMarshal` unless this page lists it.

`src/OneCompress/Zstd/Raw.cs` is the only file that skips bounds checks: `Get`, `Ref`, `ReadUInt64`, `Copy16`, `Copy8` and
`Set`. Each states the bound its caller guarantees (a table index below the table's size, a copy inside a buffer that was
checked or kept 32 bytes short of its end). With the `Checked` configuration every one of them is an ordinary checked access,
and the tests and the fuzzer run that way.

## Sequence loop

Blocks with fewer than 4 sequences run the plain loop over the tier-3 sequence decoder, because setting up the fast loops costs
more than they save. Blocks with 4 to 63 sequences (the size of game payloads) run `RunSmall`, one function that also handles
the last sequence and the stream's start. Larger blocks run `RunFast`, with a second instance that stops its refill short of
the stream's start so that only the last sequence needs the careful path.

- The three FSE tables' rows are expanded when the block's tables are known (`FseDecodingTable.FillSequenceEntries`) and
  stored in one array, so a state indexes it directly. A row is one 64-bit value: next-state baseline (16 bits), total bits
  (8), state bits (8), extra bits (8) and the baseline value (24).
- Each sequence reads the bitstream once into a 64-bit register and cuts the six fields from it with the bit counts held
  in the rows; sequences that need more than 56 bits take a slower read.
- Literals are copied with 16-byte moves. The literals buffer keeps 32 bytes of slack for that, and raw literals up to
  4096 bytes are copied into it.
- Matches with an offset of 8 or more copy 8 or 16 bytes at a time; offsets below 8 use a pattern fill; matches that reach
  into a dictionary are copied inline when 32 bytes of dictionary follow the match, otherwise by the careful path.
- Corrupt data, a copy that would pass the safe end of the output buffer and anything else unusual goes to `RunGeneral`, which
  checks every bound and is the only code that raises errors.

## Literals

The four-stream Huffman loop keeps one register per stream with a marker bit above the data, so the bits used come from a
trailing-zero count, and it refills in batches sized for the worst case. A single-stream section of 32 or more literals that
uses a dictionary's table decodes two symbols per lookup from a pair table built on first use (published with an
`Interlocked` exchange; the dictionary is shared between threads and the table is never changed after it is published).

## Speed

Bar rows are in `docs/zstd-decoder-bench.md` (method: memory to memory, warm, best of the pass, against `zstd -b<L> -e<L>`
on the same frames; `tools/zstd-decode-bench/run.sh` and `game.sh`). With the `Checked` configuration the decoder is about
27% slower than with the unchecked accessors (silesia.tar, level 3 frames, x64: 1787 against 1315 MB/s).
