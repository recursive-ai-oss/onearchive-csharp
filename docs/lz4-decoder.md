# LZ4 decoder

What the LZ4 decoder accepts, rejects and bounds. The format sources are the LZ4 Block Format and Frame Format documents
(`lz4_Block_format.md`, `lz4_Frame_format.md`); section names below refer to them. A later port should reproduce these
choices from this page alone.

## Layers

| Tier | Types | Use |
|---|---|---|
| 1 | `Lz4.Decompress`, `Lz4.TryDecompress`, `Lz4Block.Decompress`, `Lz4Block.TryDecompress` | one call, whole input |
| 2 | `Lz4Decoder`, `Lz4Stream` | any split of input and output; reuse across frames |
| 3 | `Lz4BlockDecoder`, `Lz4FrameHeader`, `Lz4Dictionary` | the block decoder over spans, the frame descriptor parser |

Tier 1 calls tier 2, tier 2 calls tier 3. `Lz4Block` decodes raw blocks whose compressed size and a bound on the decoded size
come from outside the data; `Lz4` decodes frames.

## Blocks (Block Format)

- A block is a run of sequences: token, literals, 2-byte little-endian offset, match. The last sequence has literals only and
  no offset. A block ends exactly where its input span ends, so the caller supplies the block's compressed length.
- Length fields: the high nibble of the token is the literal count, the low nibble the match length minus 4; 15 means more
  bytes follow, each added, and a byte of 255 means another follows. There is no limit on the number of bytes; sums are
  taken in 64 bits, and a literal count larger than the remaining input is a truncation error.
- Matches copy from `offset` bytes back, byte by byte in effect, so a match may be longer than its offset and repeats the
  last `offset` bytes.
- **Offset 0 is an error**, and so is an offset reaching before the start of the history (the first
  bytes of the destination, or the dictionary). The decoder never reads or writes outside the destination.
- **End of block conditions are enforced.** Unless the block is a single sequence, the last sequence must hold at least 5
  literals and the last match must start at least 12 bytes before the end of the block. The format allows a decoder to reject
  other blocks; this one does. An empty block (no bytes) is an error; the empty output is the single byte `0x00`.
- Errors carry the offset in the block's input: the offset field for offset errors, the end of the input for truncation and
  for the end rules, and the position after the length bytes for output overflow.
- `Lz4BlockDecoder.TryDecode(source, destination, historyLength, out written)`: the first `historyLength` bytes of
  `destination` are history (dictionary or earlier blocks); output follows. It returns false (no exception) when the output
  does not fit. `GetDecompressedSize` validates the block's structure and returns the exact output size without writing.
- `Lz4Block.Decompress(source, options)` measures first, checks the size against `Lz4BlockOptions.MaxOutputSize` (default
  64 MiB) before allocating, then decodes. `Lz4Block.Decompress(source, destination)` throws `CompressionLimitException`
  (`LimitName` = `destination.Length`) if the output does not fit. A dictionary is copied in front of the output for each call.

## Frames (Frame Format)

- Magic `0x184D2204`, then the descriptor: FLG (version 01 in bits 7-6, block independence, block checksum, content size,
  content checksum, reserved bit 1, dictionary ID), BD (block maximum size code 4 to 7 = 64 KiB, 256 KiB, 1 MiB, 4 MiB; bits
  7 and 3-0 reserved), optional 8-byte content size, optional 4-byte dictionary ID, and the header checksum (second byte of
  xxHash32 of the descriptor, seed 0). Wrong version, set reserved bit, block size code below 4, a content size of 2^63 or
  more, or a wrong header checksum is an error at that field's offset. The header checksum is checked even when
  `VerifyChecksum` is false.
- Data blocks: 4-byte size; bit 31 set means stored (uncompressed). A size of 0 is the EndMark. A size above the block
  maximum is an error at the size field. A stored block of size 0 is valid. With block checksums each block, empty ones
  included, is followed by xxHash32 (seed 0) of its raw bytes. A compressed block decodes to at most the block maximum bytes
  and, when the content size is declared, at most what remains of it.
- After the EndMark: the content checksum (xxHash32 of the decoded content) when flagged. When the content size is declared,
  the decoded size must equal it, checked at the EndMark.
- **Linked blocks** (independence flag clear) may refer to the previous 64 KiB of the same frame's output; a new frame never
  sees an earlier frame's output. Independent blocks refer to nothing before their own start, except the dictionary.
- **Dictionaries.** With independent blocks every block starts with the dictionary as history; with linked blocks the
  dictionary is history once, at the start of the frame. Only the last 64 KiB of a dictionary count. The frame's dictionary ID
  selects a dictionary: a nonzero ID is looked up in `Dictionaries` by `Lz4Dictionary.Id`, then `Dictionary` is used if its
  ID is 0 or equal; no match raises `CompressionDictionaryException`. A frame with no ID (or ID 0) uses `Dictionary` if set.
  The lz4 tool writes no dictionary ID when given `-D`, so its frames need `Dictionary`.
- **Concatenated frames** decode in sequence; the decoder returns `Done` after each. **Skippable frames** (magic
  `0x184D2A50` to `0x184D2A5F`) are skipped, or passed to `SkippableFrameHandler(variant, data)` when set; a payload larger
  than `MaxSkippableFrameSize` (1 MiB) raises `CompressionLimitException` before it is buffered.
- **Legacy frames** (magic `0x184C2102`): blocks of at most 8 MiB decoded, each preceded by its 4-byte compressed size
  (at most 8 MiB + 8 MiB / 255 + 16). The frame ends at the next frame's magic number (standard, skippable or legacy), at a
  zero size (the Linux kernel's end marker, accepted), or where the input ends. A size above the bound is an error.
  Dictionaries and checksums do not apply to legacy frames.
- **Empty input is an error** (a truncated frame at offset 0), unlike the lz4 tool, which accepts it: a network payload that
  is empty is not an LZ4 frame.
- Trailing bytes after the last frame that are not a frame are an error at their offset.

## Limits (`Lz4DecompressionOptions`)

| Option | Default | Meaning |
|---|---|---|
| `MaxBlockSize` | 8 MiB | largest block maximum a frame may declare (legacy frames count as 8 MiB); checked at the header, before buffers are allocated |
| `MaxOutputSize` | 1 GiB | total output between resets; a declared content size that would exceed it is refused at the header; output that exceeds it mid-frame stops the decoder with the limit error |
| `VerifyChecksum` | true | block and content checksums |
| `Dictionary`, `Dictionaries` | none | see above |
| `SkippableFrameHandler`, `MaxSkippableFrameSize` | none, 1 MiB | see above |

Memory: a decoder that decodes straight into the caller's buffer allocates nothing beyond a small header buffer. Otherwise it
holds a window of `dictionary + block size` bytes (independent blocks) or `64 KiB + 2 x block size` (linked blocks), where
the block size is the smaller of the frame's block maximum and its declared content size, plus a buffer for a compressed
block that arrives in pieces. Buffers grow only and are reused; a dictionary stays in the window between frames that use
the same one.

## Streaming (`Lz4Decoder`)

`Decompress(source, destination, out consumed, out written)` follows the shared decoder shape (`docs/conventions.md`).
- A compressed block is decoded only after the whole block and its checksum have arrived and been verified. A stored block is
  handed out as it arrives and its checksum is checked at its end.
- `Done` comes at the end of each standard or skippable frame, with its output written. A legacy frame has no end mark, so its
  `Done` comes with the next frame's magic number; at the end of the input it simply ends (`IsAtFrameBoundary`).
- `TryDecompress(source, destination, out written)` (whole input, whole output) lets blocks decode directly into
  `destination` when no dictionary is involved: linked blocks then reach their history in place. `Decompress` with a
  destination at least as large as a block does the same for independent blocks without a dictionary.

## Unchecked accessors

`Lz4Raw` holds the only unchecked code: 16-byte and 8-byte wide copies. A caller uses them only after comparing the room in the
destination and the remaining input with the copy width plus its overshoot (at most 15 bytes, overwritten by the following
sequence). With the `Checked` configuration they are ordinary bounds-checked copies, and the tests and fuzzers run that way.
