# Branch and delta filters

Reversible transforms that make code and sampled data easier to compress. They are used inside xz and 7z and are public so
that other containers can use them. Source: the xz file format specification, section 5.3.2 (ids, alignment, start offset).
The rules for each instruction set, including how the RISC-V filter was derived, are in `lzma-decoder.md`.

| | |
|---|---|
| Types | `BranchFilter` (with `BranchArchitecture`), `DeltaFilter`; both in `RecursiveAI.OneCompress.Filters` |
| Architectures | x86 (`0x04`), PowerPC, IA-64, ARM, ARM-Thumb, SPARC, ARM64, RISC-V |
| Use | `Convert(Span<byte>)` works in place in both directions and returns how many leading bytes are final; present the rest again with more data behind it |
| Start offset | an optional 32-bit position aligned to the architecture |
| Delta | distance 1 to 256, `out[i] = in[i] + out[i - distance]` |
| In 7z | the filter coders are read and written (`7z-reader.md`, `7z-writer.md`); BCJ2, which has four input streams, is listed but not decoded |
