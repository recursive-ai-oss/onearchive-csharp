# PPMd (variants H and I)

Two codecs from Dmitry Shkarin's PPMd family, byte-identical to what 7-Zip writes and reads:

| | Variant H | Variant I (revision 1) |
|---|---|---|
| Used by | 7z coder `03 04 01` | zip method 98 |
| Types | `PpmdH`, `PpmdHEncoder`, `PpmdHDecoder`, `PpmdHModel` | `PpmdI`, `PpmdIEncoder`, `PpmdIDecoder`, `PpmdIModel` |
| Order | 2 to 64 | 2 to 16 |
| Model memory | 2 KiB to 0x7FFFFF00 bytes | 64 KiB to 0x7FFFFF00 bytes |
| Range coder | 7z flavour (carry propagation, 5-byte start) | Subbotin carry-less coder |
| Restore method | restart only | restart (0) or cut-off (1) |

The model parameters are not in the compressed data. The decoder must be given the encoder's order, memory size and (for I) restore
method; the container carries them: the 7z coder properties (5 bytes: order, then memory as a little-endian 32-bit number), and the
two-byte header at the start of a zip method 98 entry (APPNOTE 5.10.4: bits 0–3 order − 1, bits 4–11 memory in MiB − 1,
bits 12–15 restore method). The zip header can express only whole MiB from 1 to 256.

## Origin

The model and coders are ported from the public-domain sources named in the header of each file in `src/OneCompress/Ppmd`
(see `THIRD-PARTY-NOTICES.md`). The model keeps the original's memory layout (12-byte units, 32-bit offsets) because the sequence of
allocations decides when memory runs out and the model restarts, so any other layout would write different bytes. The model is
a byte array with bounds-checked access; no unchecked accessors are used.

## Behaviour the formats leave open

- **End marker.** 7z data has none; the size is known from the folder. 7-Zip's zip writer appends an end marker (a symbol coded as
  escape at the root with no successor), and `PpmdIOptions.WriteEndMarker` defaults to true; `PpmdHOptions.WriteEndMarker` to false.
  A reader with a known size stops at the size and does not require the marker.
- **Levels** (the numbering of 7-Zip's `-mx`), pinned by `testdata/ppmd/levels` (a script and a note: 7-Zip 26.05 run on a 212 MB input, where no
  size reduction applies):

  | Level | 7z: order, memory | zip: order, memory (MiB), restore |
  |---|---|---|
  | 1 | 4, 1 MiB | 4, 1, restart |
  | 2 | 4, 2 MiB | 5, 2, restart |
  | 3 | 5, 4 MiB | 6, 4, restart |
  | 4 | 5, 8 MiB | 7, 8, restart |
  | 5 | 6, 16 MiB | 8, 16, restart |
  | 6 | 8, 32 MiB | 9, 32, restart |
  | 7 | 16, 64 MiB | 10, 64, cut-off |
  | 8 | 24, 128 MiB | 11, 128, cut-off |
  | 9 | 32, 256 MiB | 12, 256, cut-off |

  Levels above 9 behave as 9. The default level is 5.
- **Small inputs.** When the size is known, the model memory is reduced to the smallest power of two 2^k (k from 16 to 31) that is at
  least 16 times the size, if that is smaller than the requested memory; the zip writer then rounds up to whole MiB, minimum 1.
  The reduced value is what the header or properties record, and the decoder uses the recorded value.
- **Variant I minimum memory** is 64 KiB: with less, the cut-off restore cannot make room when the root context takes more than
  three quarters of memory.
- **Freeze restore method** (variant I's third method) is not supported; a header that asks for it is refused.
- The 7z encoder of the reference tool refuses orders above 32; the decoder here accepts up to 64 as the format allows.

## Limits and hostile input

- `PpmdHDecompressionOptions.MaxMemorySize` / `PpmdIDecompressionOptions.MaxMemorySize` (default 256 MiB) is checked before the model is
  allocated; a larger stated size raises `CompressionLimitException`. Archives use `ArchiveLimits.MaxPpmdMemorySize` (default 256 MiB).
- `MaxOutputSize` (default 1 GiB) bounds the output of the one-shot calls. The archive layer applies its own total-size and
  expansion-ratio limits.
- An order or restore method outside the ranges above, or a model memory below the minimum, is a `CompressionFormatException`
  naming the field and its offset in the properties or header.
- Truncated input raises `CompressionFormatException` with `IsTruncated`; a decoder given a damaged stream may produce wrong bytes before
  it notices (the format has no checksum), so archives rely on their own CRCs.

## Streaming

The span decoder (`OperationStatus Decompress(...)`) may be given input in pieces of any size. A symbol that needs bytes
beyond the end of the input is rolled back through a small journal (a few scalars, one binary-context probability and the
SEE sums) and the call returns `NeedMoreData` with that symbol's bytes unconsumed, so the next call re-reads them. Output
can be drained in pieces as well. `PpmdStream` wraps both directions over a `Stream`.

## Determinism

No SIMD and no floating point are used. The hash matrix has `ppmd-h` and `ppmd-i` entries (orders 2 to 64, memory from the
minimum to 16 MiB, both restore methods, end marker on and off) over the standard inputs; the manifests are equal on x64 JIT,
NativeAOT, scalar, reduced-AVX and browser-wasm.
