# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed

- Zstandard: levels 5-9 (and 6-8 for sources of 128-256 KiB) enter only every fourth position that a long match skips over into the match table, which saves writes to a table far larger than the cache. Compressed bytes change, within 0.2% in size, and stay identical on every platform.

## [0.5.0-preview.1] - 2026-10-29

Everything is still preview and carries `[Experimental]`. The README has the new measurements from both machines.

### Changed

- Deflate64: a fast decode loop; decompression now runs at 2.0-2.6 times the speed of 7-Zip's on both machines.
- PPMd: encoding and decoding are 1.4-1.6 times faster, with identical output. Decoding now runs at 0.89-0.95 of 7-Zip's speed and encoding at 0.85-0.91.
- LZMA, LZMA2, xz and lzip: faster decoding (fewer unpredictable branches per symbol), with identical output.
- Brotli: faster encoding at qualities 0-5 (quality 0 is about 1.5 times as fast as in 0.4). Qualities 0-2 now find matches and emit symbols in one pass, so their compressed bytes differ from 0.4 while staying within 1% of the `brotli` tool's size; qualities 3-11 are unchanged.
- Zstandard: levels 1-4 compress 2-10% faster, with identical output.

### Fixed

- Zstandard: negative compression levels could write invalid frames on large inputs. A sequence whose state and extra bits filled the encoder's 64-bit bit container exactly was written twice, so the `zstd` tool and this library's decoder rejected the frame or decoded garbage. The same defect could in principle affect any level; it showed first at negative levels on the Silesia corpus. Frames that were valid are unchanged byte for byte.

## [0.4.0-preview.1] - 2026-10-29

Everything new here is preview and carries `[Experimental]`. The README has the measurements against the reference tools on both machines, misses included.

### Added

- PPMd (`ONECOMPRESS010`): variants H and I as `PpmdH`/`PpmdI`, encoders, decoders and `PpmdStream`, byte-identical to 7-Zip's output; zip method 98 and the 7z PPMD coder, read and write. The codec is ported from the public-domain reference sources (see `THIRD-PARTY-NOTICES.md`). Levels 8 and 9 use 128 and 256 MiB of model memory, as 7-Zip does on large inputs. New limit `ArchiveLimits.MaxPpmdMemorySize` (256 MiB). See `docs/ppmd.md`.
- ISO 9660 read and write, with Joliet and Rock Ridge (`docs/iso.md`).
- RAR 5 (read only): list and metadata, stored entries, multi-volume sets; compressed entries raise a typed error. RAR 4 archives and header-encrypted archives are also refused with a typed error.
- Codec registration for the archive layer: by default every codec is available, and an app that wants a smaller trimmed or NativeAOT binary sets the `RecursiveAI.OneArchive.AllCodecs` feature switch to false and registers only the codecs it uses with `ArchiveCodecs`. A missing codec raises `ArchiveUnsupportedException` naming the format, the method and the call to add. A `.tar.zst`-only NativeAOT app shrank from 3.82 to 3.07 MB, without the brotli dictionary (`docs/archive-codecs.md`).
- Benchmarks against the reference tools for brotli, bzip2 compress, Deflate64, LZX and PPMd.

### Changed

- Brotli: faster decoder (a fast command loop) and a much faster encoder at every quality: a bucketed match finder, a fused meta-block writer for qualities 0-5, smaller tables. Qualities 0-9 are still below the tool's speed (see the README). Compressed bytes at qualities 0-9 differ from 0.3, still identical on every platform.
- bzip2 encoder: about twice as fast (two-phase block sort, vector move-to-front, incremental table choice); on x64 it now runs at 1.11-1.14 of `bzip2`. Compressed bytes are unchanged.
- Deflate64 encoder: levels 5-9 use an optimal parse with block splitting, and levels 1-4 share one fast class; output is smaller at levels 4-9.
- Deflate64, bzip2, PPMd, AES and ZipCrypto entries in zip, and the 7z Deflate64, PPMd and AES-256 coders (including encrypted headers), are covered by interop tests with 7-Zip and Info-ZIP. AES needs `System.Security.Cryptography.Aes`, which the browser does not provide; there only ZipCrypto reading works.

### Not supported

- BCJ2 in 7z is listed but refused with a typed "unsupported method" error. Quantum cabinets and cabinets that span files are refused with a typed error.

## [0.3.0-preview.1] - 2026-10-28

Everything new here is preview and carries `[Experimental]`. The LZMA family meets the size target and is below the reference speed in some rows; the README has the measurements from both machines.

### Added

- Brotli (RFC 7932, `ONECOMPRESS008`): decoder with the built-in dictionary and transforms, and encoder at qualities 0-11 (windows 10-24), in three layers. Single streams `.br` and `.tar.br` in the archive layer, detected by file name (brotli streams have no magic number).
- bzip2 encoder at levels 1-9 (`ONECOMPRESS004`), zip method 12 writing, and `.bz2`/`.tar.bz2` writing.
- Deflate64 decoder and encoder (`ONECOMPRESS003`), `Deflate64Stream`, zip method 9 read and write, and the 7z Deflate64 coder.
- LZX decoder (`ONECOMPRESS009`) with verbatim, aligned and uncompressed blocks and call translation.
- 7z writing: solid and non-solid folders, LZMA2, LZMA, Copy, Deflate and Zstandard coders, the branch and delta filters, and encoded headers.
- cpio (newc, crc and odc) and ar (common, GNU and BSD) read and write.
- cab read: stored, MSZIP and LZX folders, every header field, checksums, and a typed error for spanned cabinets and Quantum.
- xz and lzip single streams: `.xz`, `.lz`, `.tar.xz` and `.tar.lz`, read and write.
- Encryption (`ONEARCHIVE001`): zip WinZip AES-128/192/256 (AE-1 and AE-2) read and write, traditional zip encryption read; 7z AES-256 read and write, with encrypted headers. `ZipReaderOptions.Password`, `ZipWriterOptions.Password`, `SevenZipReaderOptions.Password`, `SevenZipWriterOptions.Password`, and `Password` on `ArchiveOpenOptions` and `ArchiveCreateOptions`. A missing or wrong password is an `ArchivePasswordException`. AES needs `System.Security.Cryptography.Aes`, which the browser does not provide.

### Changed

- LZMA-family speed: decoder fast paths and match-finder prefetching. Deflate speed: a faster decoder loop and table build, and faster encoder levels 1-7. Deflate levels 2-4 now hash four bytes, so their output differs from 0.2 (still identical on every platform); other compressed bytes are unchanged.
- 7z reading: empty-stream vectors in the order the format defines, and coders without streams.

### Not supported

- PPMd (zip method 98, 7z PPMD) and BCJ2 are listed but refused with a typed "unsupported method" error.

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

[Unreleased]: https://github.com/recursive-ai-oss/onearchive-csharp/compare/v0.5.0-preview.1...HEAD
[0.5.0-preview.1]: https://github.com/recursive-ai-oss/onearchive-csharp/releases/tag/v0.5.0-preview.1
[0.4.0-preview.1]: https://github.com/recursive-ai-oss/onearchive-csharp/releases/tag/v0.4.0-preview.1
[0.3.0-preview.1]: https://github.com/recursive-ai-oss/onearchive-csharp/releases/tag/v0.3.0-preview.1
[0.2.0-preview.1]: https://github.com/recursive-ai-oss/onearchive-csharp/releases/tag/v0.2.0-preview.1
[0.1.0-preview.1]: https://github.com/recursive-ai-oss/onearchive-csharp/releases/tag/v0.1.0-preview.1
