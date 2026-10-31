# Documentation

Normative, language-neutral notes on what each codec and container does: the choices the formats leave open, the defaults
and limits, and the benchmark method. A port should be able to reproduce the same bytes from these pages. The XML
documentation shipped in both packages is the API reference. Specification sections are cited by number; the specifications
themselves are not reproduced.

Every codec has the same three tiers (`conventions.md`): static one-liners, encoder/decoder structs with
`OperationStatus` over spans plus a stream, and the raw parts underneath.

## Codecs (`RecursiveAI.OneCompress`)

| Family | Specification | Decoder | Encoder | Notes |
|---|---|---|---|---|
| Zstandard | RFC 8878 | [zstd-decoder](zstd-decoder.md) | [zstd-encoder](zstd-encoder.md) | [dictionary training](zstd-dictionary-training.md), [seekable format](zstd-seekable.md), [small-payload method](small-payload-bench.md) |
| LZ4 | LZ4 block and frame format documents | [lz4-decoder](lz4-decoder.md) | [lz4-encoder](lz4-encoder.md) | block and frame, dictionaries |
| Deflate | RFC 1951 | [deflate-decoder](deflate-decoder.md) | [deflate-encoder](deflate-encoder.md) | |
| zlib and gzip | RFC 1950, RFC 1952 | [gzip-zlib](gzip-zlib.md) | [gzip-zlib](gzip-zlib.md) | wrappers over Deflate |
| Deflate64 | APPNOTE 6.3.10, section 4.4.5 | [deflate64-decoder](deflate64-decoder.md) | [deflate64-encoder](deflate64-encoder.md) | [derivation by experiment](deflate64-derivation.md) |
| Brotli | RFC 7932 | [brotli-decoder](brotli-decoder.md) | [brotli-encoder](brotli-encoder.md) | built-in dictionary |
| LZMA and LZMA2 | LZMA specification | [lzma-decoder](lzma-decoder.md) | [lzma-encoder](lzma-encoder.md) | |
| xz | xz file format specification | [xz](xz.md) | [xz](xz.md) | checks, filters, blocks |
| lzip | the lzip manual | [lzip](lzip.md) | [lzip](lzip.md) | |
| Branch and Delta filters | xz file format specification, 5.3.2 | [filters](filters.md) | [filters](filters.md) | x86, ARM, ARM64, RISC-V and others |
| bzip2 | public descriptions, no official specification | [bzip2-decoder](bzip2-decoder.md) | [bzip2-encoder](bzip2-encoder.md) | [derivation by experiment](bzip2-encoder-derivation.md) |
| PPMd (variants H and I) | Shkarin's papers and public-domain reference sources | [ppmd](ppmd.md) | [ppmd](ppmd.md) | |
| LZX and MSZIP | [MS-PATCH], the cabinet format | [lzx-decoder](lzx-decoder.md) | not provided | used by cab |
| Checksums | RFC 1952, RFC 3720, ECMA-182, xxHash description | [checksums](checksums.md) | | CRC-32, CRC-32C, CRC-64, Adler-32, xxHash32, xxHash64 |

Also: [message mode](message-mode.md) (compressing many small messages with a trained dictionary and reused state).

## Containers (`RecursiveAI.OneArchive`)

| Format | Specification | Page |
|---|---|---|
| The shared model, safety defaults, limits, `Archive` | | [archive](archive.md) |
| Codec registration and trimming | | [archive-codecs](archive-codecs.md) |
| tar | POSIX ustar and pax, GNU tar manual | [tar](tar.md) |
| zip | APPNOTE 6.3.10, WinZip AES | [zip](zip.md), [encryption](zip-encryption.md), [recovery](zip-recovery.md) |
| Single compressed files, `.tar.*` | | [single-streams](single-streams.md) |
| 7z | `7zFormat.txt` | [reader](7z-reader.md), [writer](7z-writer.md), [AES key derivation](7z-aes-derivation.md) |
| cpio and ar | | [cpio-ar](cpio-ar.md) |
| ISO 9660 with Joliet and Rock Ridge | ECMA-119, Joliet, IEEE P1281 and P1282 | [iso](iso.md) |
| cab | the cabinet file format | [cab-reader](cab-reader.md) |
| RAR 5 (listing and stored entries) | the RAR 5 technote | [rar-reader](rar-reader.md) |
| Microsoft compound files | [MS-CFB] | [cfb](cfb.md) |

## Measurement and determinism

- [bench-method](bench-method.md): how speed, size and memory are measured, against the native tools and against the fastest
  managed .NET libraries, including the size-matched rows.
- [hash-matrix](hash-matrix.md): how byte-identical output is checked across operating systems, CPUs, runtimes, thread counts
  and WebAssembly.
