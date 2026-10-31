# RecursiveAI.OneCompress

Compression codecs for .NET, written from first principles in managed code: no native libraries, and the same compressed bytes on every operating system, CPU, runtime and thread count, WebAssembly included. The only runtime dependency is the .NET base class library.

**Stable from 1.0:** semantic versioning, and compressed bytes per codec and level stay the same within 1.x (see the [README](https://github.com/recursive-ai-oss/onearchive-csharp#stability)).

- **Zstandard:** every level, dictionaries and dictionary training (COVER, fast-COVER), long-distance matching, multithreaded compression in fixed-size jobs, the seekable format.
- **LZ4:** block and frame, with dictionaries. **Deflate, zlib, gzip and Deflate64.** **Brotli** with its built-in dictionary.
- **LZMA, LZMA2, xz** (CRC-32, CRC-64 and SHA-256 checks; x86, ARM, ARM64, ARM-Thumb, PowerPC, SPARC, IA-64 and RISC-V branch filters; Delta) and **lzip.**
- **bzip2, PPMd** (variants H and I), and the **LZX** decoder.
- **CRC-32, CRC-32C, CRC-64, Adler-32, xxHash32 and xxHash64**, with SIMD paths.
- **Three tiers:** one-liners (`Zstd.Compress(data, new ZstdCompressionOptions { Level = 19 })`), encoder/decoder structs and streams shaped like `BrotliEncoder` and `BrotliStream`, and the raw parts (tables, match finders, range coders) public and documented.
- **Safe on hostile input:** every length and table is validated, decoder memory is bounded by the declared window and checked against a configurable maximum, and errors are one typed hierarchy naming the format, the offset and what was expected.
- **Trim and NativeAOT friendly:** no static registry roots the codecs, so an app that uses one trims the rest; Brotli's dictionary is only in apps that use Brotli.

```csharp
byte[] packed = Zstd.Compress(data, new ZstdCompressionOptions { Level = 19 });
byte[] again = Zstd.Decompress(packed);
```

See the [README](https://github.com/recursive-ai-oss/onearchive-csharp#readme) for the quick start, the speed tables against the fastest managed .NET libraries, and the safety and determinism notes; [docs](https://github.com/recursive-ai-oss/onearchive-csharp/blob/main/docs/README.md) has one page per codec. Archives (zip, tar, 7z and more) are in [`RecursiveAI.OneArchive`](https://www.nuget.org/packages/RecursiveAI.OneArchive).

Licensed under MIT OR Apache-2.0. Third-party material is listed in `THIRD-PARTY-NOTICES.md`, included in the package.
