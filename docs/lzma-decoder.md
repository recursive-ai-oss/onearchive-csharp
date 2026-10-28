# LZMA, LZMA2, xz and lzip decoders

`RecursiveAI.OneCompress.Lzma`, `.Xz`, `.Lzip` and `.Filters`. The formats are specified by the LZMA specification
document (`lzma-specification.txt`), the xz file format specification (`xz-file-format.txt`) and the lzip manual's format
chapter. This document records only what those texts leave open, and how the decoders behave. A later port must reproduce
the decoder's accept/reject behaviour from this text and the specifications.

## Layers

| Tier | Types |
|---|---|
| 3 (raw) | `LzmaRangeDecoder`, `LzmaModel` (probability layout, state machine), `LzmaDecoderCore` (symbol decoder and window), `BranchFilter`, `DeltaFilter` |
| 2 | `LzmaDecoder` (`.lzma` alone format, and `CreateRaw` for a bare stream with known properties), `Lzma2Decoder`, `XzDecoder`, `LzipDecoder`: `OperationStatus Decompress(source, destination, out consumed, out written)`; `LzmaStream`, `XzStream`, `LzipStream` |
| 1 | `Lzma.Decompress`, `Lzma2.Decompress`, `Xz.Decompress`, `Lzip.Decompress` and the `TryDecompress` forms |

The filters are standalone span transforms that work in both directions, so an encoder shares them.

## Defaults and limits

| Option | Default | Meaning |
|---|---|---|
| `MaxDictionarySize` | 64 MiB | A header declaring more raises `CompressionLimitException` before memory is reserved. 64 MiB is the dictionary of the reference tool's `-9`. Larger streams (`xz --lzma2=dict=1536MiB`) need it raised. |
| `MaxOutputSize` | 1 GiB | Total output of a decoder instance (all streams and members). A declared size above it is rejected before decoding; output reaching it raises `CompressionLimitException`. |
| `AllowTrailingData` | false | Bytes after the last stream, member or `.lzma` end: error when false. The span decoders never look at them and report `Done` with the bytes unconsumed. |
| `VerifyChecks` (xz) / `VerifyChecksum` (lzip) | true | Verify CRC32, CRC64, SHA-256 / the lzip CRC-32. When false the check is still skipped over and the sizes in the trailer and index are still verified. |
| `AllowUnsupportedChecks` (xz) | false | Check IDs other than 0, 1, 4 and 10 are reserved. They are rejected unless this is set; then the block checks, whose size follows from the ID (4 bytes for 1-3, 8 for 4-6, 16 for 7-9, 32 for 10-12, 64 for 13-15), are skipped unverified. |

Memory is the declared dictionary at most (the window grows with the output, starting at 64 KiB and doubling), plus 32 spare bytes behind the ring (a match copy rounds up to whole 16-byte steps; the ring is the dictionary size plus 32, so the overshoot lands on bytes too old for any match to reach), plus the
probability tables (`0x300 << (lc + lp)` 16-bit entries plus a fixed part; at most about 6 MiB, since the `.lzma` format allows
lc up to 8 and lp up to 4), plus 64 KiB of
chain buffer when an xz block has filters. Steady-state decoding allocates nothing.

## LZMA

- The `.lzma` header is 13 bytes: properties byte `(pb * 5 + lp) * 9 + lc`, a 32-bit little-endian dictionary size, and a 64-bit
  size (all ones = unknown). A dictionary size below 4096 is raised to 4096. A properties byte above 224 is an error.
- With an unknown size the stream must end with the end marker (distance 2^32 - 1). With a known size an end marker before
  that size is an error. At the declared size the stream ends if the range decoder is finished (code value zero); otherwise an
  end marker must follow (the specification allows both), and anything else is an error. A match that would run past the
  declared size is an error.
- A match whose distance exceeds the bytes produced so far, or the dictionary size, is `CompressionFormatException` at the
  input offset of the symbol.
- The range decoder's first byte must be 0, as the specification requires.
- **Split input.** A symbol reads at most about 20 bytes. With 32 bytes of input available the symbol decodes directly; with
  less, the decoder journals the probability updates, and if the input ends inside a symbol it rolls the symbol back and
  carries the unread bytes inside the decoder, so `NeedMoreData` means all input was consumed. Any split of the input gives the
  same output and the same errors. A match that does not fit in the destination is finished by the next call.

- **Fast loop.** While at least 32 bytes of input remain at the start of a symbol, `LzmaDecoderCore` decodes with the range
  coder state in locals and no undo log (`LzmaDecoderCore.Fast.cs`); literal and tree bits are decoded without a branch on the
  bit. The last few symbols of an input piece use the journalling loop. Both loops make the same decisions, so the output and
  the errors do not depend on where the input is cut. Probability and input reads in this loop and the wide match copy skip bounds checks behind the accessors in `LzmaRaw`
  (bounds-checked in the `Checked` build): a symbol starts only with 32 input bytes left and consumes at most 25, and every probability
  index is a fixed offset of the model layout plus a state or tree node inside its table. Literals are decoded in their own methods and
  the bit trees are unrolled so that the range coder state stays in registers; none of this changes a decision.

## LZMA2

- Chunks as in the xz specification's LZMA2 description: control 0 ends; 1 and 2 are uncompressed chunks (1 resets the
  dictionary); 0x80 and above are LZMA chunks, bits 5-6 choosing the reset level (none, state, state + new properties,
  all + dictionary). Sizes are 21 bits of unpacked and 16 bits of packed size, each plus one.
- Rules the decoder enforces (violations are `CompressionFormatException` naming the offset): the first chunk resets the
  dictionary; after a dictionary reset the first LZMA chunk carries properties (control >= 0xC0); an LZMA chunk that follows
  an uncompressed chunk needs at least a state reset; control values 3-0x7F are invalid; `lc + lp <= 4`; the LZMA data of a chunk
  uses exactly its packed size and produces exactly its unpacked size, and the chunk's range decoder must end clean.
- The dictionary property byte is 0-40: size `(2 | (b & 1)) << (b / 2 + 11)`, and 40 means 4 GiB - 1. Above 40 is an error.

## xz

- Stream header and footer: magic, flags (check ID in the low nibble of the second flag byte; all other bits zero), CRC-32 of
  the flags; the footer repeats the flags and holds the backward size. Both are compared.
- Block header: size byte `(n / 4) - 1` (a zero size byte is the index), flags (filter count 1-4, bits 6 and 7 mark the
  optional compressed and uncompressed sizes), variable-length integers (at most 9 bytes, no redundant zero continuation), filter
  IDs and property sizes, zero padding to a multiple of 4, CRC-32. A reserved flag bit, a non-zero padding byte or a bad CRC is an error.
- Filter chain rules: the last filter is LZMA2 (0x21) and no other filter may be last; LZMA2 may appear only last; at most
  three non-last filters; the non-last filters must be branch (0x04-0x0B) or delta (0x03) filters. Anything else is
  `CompressionFormatException("unsupported filter")`.
- Filters: x86 0x04, PowerPC 0x05, IA-64 0x06, ARM 0x07, ARM-Thumb 0x08, SPARC 0x09, ARM64 0x0A, RISC-V 0x0B. A branch filter's
  properties are empty (start offset 0) or a 4-byte little-endian start offset aligned to the architecture. Delta's property
  is one byte, distance - 1. Filters run in reverse order of the chain, in place over a 64 KiB buffer.
- A block's compressed size field (when present) must match what LZMA2 consumed, and the uncompressed size field must match the
  output. After the LZMA2 end marker come 0-3 zero padding bytes to a multiple of 4, then the check.
- The index lists the (unpadded size, uncompressed size) record of each block. The decoder keeps the same pairs as it decodes and
  compares them with the index, and the backward size with the index's real size. The index's own CRC-32 is verified. (The
  pairs are hashed with CRC-64 for the comparison; this is internal and affects nothing a caller can see.)
- Multiple streams may follow each other, each optionally followed by zero padding in multiples of 4 bytes. Anything else
  after a stream is trailing data (error unless `AllowTrailingData`).
- Checks: none (0), CRC-32 (1), CRC-64 (4, ECMA-182 polynomial reflected), SHA-256 (10, from the BCL). CRC-32 and CRC-64 come from the
  library's checksum primitives.

## Branch and delta filters

Both are exact inverses of the reference tool's encode direction and work on any split of the data: `Convert` returns the
number of leading bytes that are final and the caller presents the rest again with more data behind it. At the end of the
data the last bytes (under 4, 8, 16 depending on the architecture) stay unconverted, which is what the reference encoder does.
The x86 filter carries its previous-mask state across calls; the others are stateless apart from the position.

- x86: E8/E9 with a 32-bit operand whose top byte is 00 or FF, and the prefix mask of the previous 5 bytes, as in the public
  descriptions of the BCJ x86 filter.
- PowerPC, ARM, ARM-Thumb, SPARC, ARM64, IA-64: as in the public descriptions; ARM64 converts BL (26-bit word offset) and ADRP
  (21-bit page offset, only when it is small enough to look like code).
- **RISC-V** has no public description other than the xz format list. The rules were derived by running `xz --riscv` as a black box over
  hand-made instruction sequences (the derivation is described in the comment above `BranchFilter.RiscV`): JAL with a link
  register x1 or x5 becomes an absolute target; AUIPC followed by an instruction using the same register is converted as a
  pair, stored as a marker AUIPC (register x2) holding the low bits of the second instruction followed by a big-endian
  absolute target; an original AUIPC that looks like a marker is stored escaped. Decoding reverses both.
- Delta: `out[i] = in[i] + out[i - distance]` modulo 256, distance 1-256, history kept across calls.

## lzip

- Header: `LZIP`, version 1 (version 0 is rejected as unsupported), dictionary-size byte: `2^(b & 31)` minus
  `(2^(b & 31) / 16) * (b >> 5)`; the size must be 4 KiB to 512 MiB.
- Body: LZMA with lc 3, lp 0, pb 2, ending with the end marker. Trailer: CRC-32 of the data, data size, member size (each little
  endian, 4 + 8 + 8 bytes); all three are checked, the member size even with checks off.
- Members follow each other. Trailing bytes that are not a member header are an error unless `AllowTrailingData`.

## Errors

`CompressionFormatException` (format name, input offset, what was expected), `CompressionLimitException` (the limit's name,
its value, the requested value), both under `CompressionException`. Offsets are absolute input offsets of the field
that failed, counted from the start of the data given to the decoder. Truncated input ends in a format error naming the offset
where more data was expected, through `ThrowIfIncomplete` or the one-call methods and the streams.

## Tests

`testdata/lzma/generate.py` writes the vectors with the xz, lzip and 7-Zip tools (versions recorded in `manifest.tsv`). The
hash matrix (`lzma-decode`, `lzma-filter` entries) decodes the smaller vectors one-shot, through a stream, in pieces and with bit
flips, and runs both filter directions; the outputs and error texts must be equal on every platform. Fuzz targets: `lzma-decode`,
`xz-decode`, `lzip-decode`.
