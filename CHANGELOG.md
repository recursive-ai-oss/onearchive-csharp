# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- `ZstdCompressionOptions.SizeHint`: an estimate of a stream's length that sizes the window and tables without being written to the frame.

### Changed

- Zstandard encoder memory: a stream of unknown length no longer reserves its whole window and binary-tree tables up front (level 22 took 384 MiB for a 1 KiB frame, 640 MiB with a small dictionary; now 129 MiB), and a dictionary's search index is sized by the dictionary, not the source (4 KiB at level 22: 256 MiB, now under 1 MiB). Compressed bytes are unchanged.

## [0.1.0-preview.1] - 2026-10-28

First preview. Both codecs are preview and their public types carry `[Experimental]` (`ONECOMPRESS001` for Zstandard, `ONECOMPRESS002` for LZ4): compressed sizes are at or near the reference tools', and speed is still below them. The README has the measurements.

### Added

- Zstandard (RFC 8878): every level from -7 to 22, frames and skippable frames, dictionaries (raw and formatted), dictionary training, long-distance matching, multithreaded compression in fixed-size jobs (the same bytes at any thread count), and the seekable format. One-liners on `Zstd`; `ZstdEncoder`/`ZstdDecoder` over spans with `OperationStatus`, and `ZstdStream`; the parts beneath (FSE and Huffman tables, match finders, block writer) are public.
- LZ4 block and frame formats, fast and high-compression levels, with dictionaries and linked blocks, in the same three layers.
- Checksums: CRC32, CRC32C, CRC64, Adler-32 and xxHash64, with vector paths.
- Input is treated as untrusted: the Zstandard decoder caps the window it will allocate (8 MiB by default), malformed input is a typed error, and every decoder has a fuzz target.
- NativeAOT, trimming and browser WebAssembly are supported; output is byte-identical on every platform.

[Unreleased]: https://github.com/recursive-ai-oss/onearchive-csharp/compare/v0.1.0-preview.1...HEAD
[0.1.0-preview.1]: https://github.com/recursive-ai-oss/onearchive-csharp/releases/tag/v0.1.0-preview.1
