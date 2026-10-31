# xz

The xz container (`.xz`) around LZMA2, with checks and filters. Source: the xz file format specification (sections 2 to 5);
the LZMA2 layer is in `lzma-decoder.md` and `lzma-encoder.md`, which hold the normative detail.

| | |
|---|---|
| Types | `Xz` (tier 1: `Compress`, `Decompress`, `TryCompress`, `TryDecompress`), `XzEncoder`, `XzDecoder`, `XzStream` (tier 2), `XzCompressionOptions`, `XzDecompressionOptions`, `XzFilter`, `XzCheck` |
| Checks | none, CRC-32, CRC-64 (default), SHA-256 on write; the same on read, other check ids only under `AllowUnsupportedChecks` |
| Filters | the branch converters for x86, PowerPC, IA-64, ARM, ARM-Thumb, SPARC, ARM64 and RISC-V, and Delta, at most three before LZMA2 (`filters.md`) |
| Blocks | one block by default; `BlockSize` cuts blocks at exact multiples of it, each restarting the dictionary |
| Presets | 0 to 9 and the extreme variant, as the reference tool numbers them (`LzmaOptions.Preset`, `Extreme`) |
| Decoder limits | `MaxDictionarySize` 64 MiB, `MaxOutputSize` 1 GiB, both checked before memory is reserved; multiple streams and stream padding are read; trailing bytes are an error unless `AllowTrailingData` |

```csharp
byte[] xz = Xz.Compress(data, new XzCompressionOptions { Lzma = new LzmaOptions { Preset = 9 }, Check = XzCheck.Sha256 });
byte[] fromXz = Xz.Decompress(xz);
```
