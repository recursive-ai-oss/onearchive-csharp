# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Encryption (`ONEARCHIVE001`): zip WinZip AES-128/192/256 (AE-1 and AE-2) read and write, traditional zip encryption read; 7z AES-256 read and write, with encrypted headers. `ZipReaderOptions.Password`, `ZipWriterOptions.Password`, `SevenZipReaderOptions.Password`, `SevenZipWriterOptions.Password`, and `Password` on `ArchiveOpenOptions` and `ArchiveCreateOptions`. A missing or wrong password is an `ArchivePasswordException`. AES needs `System.Security.Cryptography.Aes`, which the browser does not provide.

## [0.2.0-preview.1] - 2026-10-28

Everything new here is preview and carries `[Experimental]`. Deflate, zlib and gzip meet the size target and are below the reference speed; the bzip2 decoder meets both. The README has the measurements.

### Added

- Deflate (RFC 1951), zlib (RFC 1950) and gzip (RFC 1952): decoder and encoder at levels 0-12 with strategies, preset dictionaries and flushes, in three layers (`ONECOMPRESS003`).
- bzip2 decoder: multi-stream and randomized blocks, with limits for hostile input (`ONECOMPRESS004`).
- LZMA and LZMA2 (`ONECOMPRESS005`), xz with CRC32, CRC64 and SHA-256 checks, the branch filters (x86, ARM, ARM-Thumb, ARM64, PowerPC, SPARC, IA-64, RISC-V) and Delta (`ONECOMPRESS006`), and lzip (`ONECOMPRESS007`): decoders and encoders at every preset.
- OneArchive (`ONEARCHIVE001`): `Archive.Open`, `Archive.Extract` and `Archive.Create`, with detection by content, extraction that never writes outside the target, and limits on size, ratio, entry count, path length and nesting.
  - tar: v7, ustar, pax and GNU (long names, sparse files), read and write.
  - zip: read and write with store, Deflate and Zstandard, zip64 and data descriptors; LZMA, xz and bzip2 entries read.
  - 7z: read, with Copy, Deflate, BZip2, LZMA, LZMA2, Delta, the branch filters and Zstandard, solid folders and encoded headers.
  - Single streams: gz, zst and lz4 (read and write) and bz2 (read), and tar inside them.
- `Lz4BlockEncoder`, a reusable block compressor, and a length-prefixed block form read back by `Lz4PrefixedBlock` (not an LZ4 frame). `Lz4Dictionary.Train`.
- `ZstdCompressionDictionary.ForDecompression` and `ToArray`, so one trained dictionary serves both directions.
- `ZstdCompressionOptions.SizeHint`: an estimate of a stream's length that sizes the window and tables without being written to the frame.

### Changed

- Zstandard encoder memory: a stream of unknown length no longer reserves its whole window and binary-tree tables up front (level 22 took 384 MiB for a 1 KiB frame, 640 MiB with a small dictionary; now 129 MiB), and a dictionary's search index is sized by the dictionary, not the source (4 KiB at level 22: 256 MiB, now under 1 MiB). Compressed bytes are unchanged.
- Zstandard and Deflate speed work; compressed bytes of Zstandard are unchanged.

### Not supported

- PPMd (zip method 98, 7z PPMD), BCJ2, Deflate64 and encryption are listed but refused with a typed "unsupported method" error.

## [0.1.0-preview.1] - 2026-10-28

First preview. Both codecs are preview and their public types carry `[Experimental]` (`ONECOMPRESS001` for Zstandard, `ONECOMPRESS002` for LZ4): compressed sizes are at or near the reference tools', and speed is still below them. The README has the measurements.

### Added

- Zstandard (RFC 8878): every level from -7 to 22, frames and skippable frames, dictionaries (raw and formatted), dictionary training, long-distance matching, multithreaded compression in fixed-size jobs (the same bytes at any thread count), and the seekable format. One-liners on `Zstd`; `ZstdEncoder`/`ZstdDecoder` over spans with `OperationStatus`, and `ZstdStream`; the parts beneath (FSE and Huffman tables, match finders, block writer) are public.
- LZ4 block and frame formats, fast and high-compression levels, with dictionaries and linked blocks, in the same three layers.
- Checksums: CRC32, CRC32C, CRC64, Adler-32 and xxHash64, with vector paths.
- Input is treated as untrusted: the Zstandard decoder caps the window it will allocate (8 MiB by default), malformed input is a typed error, and every decoder has a fuzz target.
- NativeAOT, trimming and browser WebAssembly are supported; output is byte-identical on every platform.

[Unreleased]: https://github.com/recursive-ai-oss/onearchive-csharp/compare/v0.2.0-preview.1...HEAD
[0.2.0-preview.1]: https://github.com/recursive-ai-oss/onearchive-csharp/releases/tag/v0.2.0-preview.1
[0.1.0-preview.1]: https://github.com/recursive-ai-oss/onearchive-csharp/releases/tag/v0.1.0-preview.1
