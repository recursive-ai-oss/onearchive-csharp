# onearchive-csharp

Compression codecs and archive formats for .NET, written from first principles in managed code.
No native dependencies; the compressed bytes are identical on every platform.

Two packages:

- `RecursiveAI.OneCompress` - codecs and checksums.
- `RecursiveAI.OneArchive` - archive containers and a simple `Archive` API, built on OneCompress.

**Status: 0.4.0-preview.1.** OneCompress has Zstandard (frames, skippable frames, dictionaries and
dictionary training, long-distance matching, multithreaded compression in fixed-size jobs, the seekable
format), LZ4 (block and frame), Deflate, zlib, gzip and Deflate64, LZMA, LZMA2, xz (with the branch and delta
filters) and lzip, bzip2, Brotli (with its built-in dictionary), the LZX decoder, and CRC32, CRC32C, CRC64,
Adler-32 and xxHash64, plus PPMd (variants H and I). OneArchive reads and writes tar, zip, 7z, cpio, ar and
ISO 9660 (with Joliet and Rock Ridge), with zip and 7z encryption, reads cab and RAR 5 (listing, metadata and stored
entries), and handles single compressed streams. Everything is preview: see below.

```csharp
byte[] packed = Zstd.Compress(data, new ZstdCompressionOptions { Level = 19 });
byte[] again = Zstd.Decompress(packed);
```

## Build

Requires the .NET 10 SDK (see `global.json`).

```sh
build/ci.sh              # build, test (Checked configuration), NativeAOT/trim smoke
build/ci.sh wasm         # browser-wasm build and a headless run under Node (needs the wasm-tools workload)
```

Build output goes to `artifacts/` (ignored by git). Set `ONEARCHIVE_ARTIFACTS` to put it elsewhere,
for example one directory per git worktree.

## Preview codecs

Zstandard, LZ4, Deflate, zlib, gzip and the LZMA family (LZMA, LZMA2, xz, lzip) are preview while their speed is
below the reference tools' in some rows. Their compression ratio and byte-identical output are already at the
target; speed is what is still being tuned. The bzip2 decoder and the lzip decoder meet their targets and stay
preview with the rest of this release. Brotli (compress is below the tool's speed at qualities 0 and 5, and quality 11 is
2% larger), PPMd (speed 0.57-0.72 of 7-Zip's, same sizes) are preview; the bzip2 encoder meets its target on both machines. Deflate64 and LZX meet the
targets on x64; Deflate64 decode is below it on arm64. They stay preview with the rest of this release. The archive layer
is preview while the formats' interop and fuzz coverage grows. BCJ2 is not supported: 7z entries that use it are listed,
and opening them is a typed "unsupported method" error. RAR 5 is read only: compressed entries raise a typed error.
Their public types carry `[Experimental]`, so using them is a compiler error until you opt in per codec:

| Codec | Diagnostic ID |
|---|---|
| Zstandard (including dictionary training and the seekable format) | `ONECOMPRESS001` |
| LZ4 | `ONECOMPRESS002` |
| Deflate, zlib, gzip and Deflate64 | `ONECOMPRESS003` |
| bzip2 | `ONECOMPRESS004` |
| LZMA and LZMA2 | `ONECOMPRESS005` |
| xz and the branch and delta filters | `ONECOMPRESS006` |
| lzip | `ONECOMPRESS007` |
| Brotli | `ONECOMPRESS008` |
| LZX (decoder) | `ONECOMPRESS009` |
| PPMd (variants H and I) | `ONECOMPRESS010` |
| OneArchive: `Archive`, the entry model, readers and writers, every container, encryption | `ONEARCHIVE001` |

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);ONECOMPRESS001;ONEARCHIVE001</NoWarn>
</PropertyGroup>
```

or `#pragma warning disable ONECOMPRESS001` around the code that uses it. The marker comes off once the codec
meets its speed target.

### Where they stand

The target for every codec: compressed size within 1% of the reference tool, and compress and decompress
speed at least 0.87 of the reference tool's, at every level. Measured memory to memory against
`zstd` 1.5.7, `lz4` 1.10.0, libdeflate 1.26 (one call), zlib-ng 2.3.3 (streaming), `bzip2` 1.0.8, `xz` 5.8.4, `lzip` 1.26 and
7-Zip `7zz` 26.05, single-threaded unless noted, on Silesia and enwik8 and on small game
snapshot payloads with trained dictionaries. Speed is ours divided by the reference (1.00 = same speed).
x64 is an AMD Ryzen 9 7950X3D on Linux; arm64 is an Apple M2 on macOS. .NET 10.0.402 with the JIT.

| Rows | x64 | arm64 |
|---|---|---|
| Zstandard compress, levels -5 to 15 | 0.56-0.84; size within +0.93% | 0.63-0.78 |
| Zstandard compress, levels 16-19 | 0.76-0.85 (enwik8 16-18 at the target) | 0.78-0.87 |
| Zstandard compress, levels 20 and 22 | 0.86-1.07; enwik8 level 22 size +1.17% | 0.90-1.02; enwik8 level 22 size +1.17% |
| Zstandard decompress | 12 of 16 rows at the target; the rest 0.86-0.87 | 5 of 8 at the target; the rest 0.86-0.87 |
| Zstandard long-distance matching | 0.60-0.75; Silesia level 3 size +1.04% | 0.66-0.78 |
| Zstandard multithreaded jobs (levels 1 and 3, 4 to 16 workers) | 0.62-0.86; size within +0.82% | 0.68-0.85 |
| Game payloads with dictionaries, compress | per-tick frames 1.0-1.4x faster; snapshots and saves 0.26-0.58 at level 3, 0.06-0.39 at level 19 (size -4.8% to +0.44%) | 10 of 48 rows at the target |
| Game payloads with dictionaries, decompress | 29 of 48 rows at the target; frames 1.02-1.44x faster; the rest 0.83-0.86 | not yet measured |
| LZ4 compress | 0.58-0.82; level 2 size +2.2% (Silesia) | 0.54-0.83 |
| LZ4 decompress | 0.80-0.90 | 0.82-0.89 |
| Small payloads (256 B to 16 KiB) with a dictionary, reused encoder and decoder, Zstandard levels 1 and 3 | compress 0.26-0.74, decompress 0.68-1.06; size 0.73-1.01; no allocation per payload | not yet measured |
| Small payloads with a dictionary, LZ4 | compress 0.27-0.86, decompress 0.45-1.31; size within +2.9% (level 3), +2.5% (fast levels) | not yet measured |
| Deflate compress, one call, levels 1-9 (against libdeflate) | 0.58-0.81; size -1.71% to +0.61% | 0.60-0.88 |
| Deflate compress, one call, levels 10-12 | Silesia 0.89-0.96, enwik8 0.84-0.86; size within +0.90% | at the target |
| Deflate compress, streaming in 64 KiB pieces, levels 1-9 (against zlib-ng) | levels 1-4 0.56-0.79, Silesia 5-8 and enwik8 6, 7, 9 at the target; output 0.5-26% smaller | levels 7 and 8 at the target, the rest 0.64-0.79 |
| Deflate decompress | 0.60-0.71 of libdeflate, 0.88-0.97 of zlib-ng | 0.72-0.78 of libdeflate, 1.14-1.29 of zlib-ng |
| bzip2 decompress (against `bzip2 -d`, start-up subtracted) | 1.28-1.47, at the target | 1.00-1.10, at the target |
| xz compress, presets 0-3 | 0.71-1.29; size -0.29% to +0.79%, except preset 1 (+2.29% Silesia, +1.44% enwik8) and enwik8 preset 3 (+1.28%) | 0.69-1.14; same sizes |
| xz compress, presets 4-9 | 0.87-0.96 (Silesia preset 4 0.87); size +0.23% to +0.72% | enwik8 0.88-0.93, Silesia 0.82-0.84; same sizes |
| xz decompress | 0.58-0.66 | 0.96-1.05, at the target |
| lzip compress, levels 0-3 | 0.37-0.85; size -4.3% to +0.02% | 0.44-0.84 |
| lzip compress, levels 4-9 | enwik8 0.91-0.97, Silesia 0.85-0.92 (levels 4, 8, 9 below 0.87); size -0.02% to +0.48% | enwik8 0.89-0.93, Silesia 0.81-0.89; same sizes |
| lzip decompress | 0.99-1.14, at the target | 1.28-1.44, at the target |
| 7z archive size (LZMA2, against `7zz -mx=1/5/9 -mmt=1`) | -0.89% to +0.91%, at the target | |
| bzip2 compress, levels 1 and 9 (against `bzip2`, start-up subtracted) | 1.11-1.14, at the target; size -0.02% to +0.01% | 1.09-1.10, at the target; same sizes |
| Brotli compress, quality 0 and 5 (window 22) | 0.37 and 0.59; size -9.84% and -0.11% | 0.32 and 0.56; same sizes |
| Brotli compress, quality 11 | 1.10; size +2.04% | 1.17; size +2.04% |
| Brotli decompress, quality 5 and 11 (against `brotli` 1.2.1) | 0.83-0.93 (0.83 on one stream written by the tool, quality 5); other streams at the target | 0.92-1.01, at the target |
| Deflate64 compress, levels 1, 5, 9 (against `7zz` zip Deflate64) | 1.03, 0.92 and 3.03, at the target; size -1.04% to +0.38% | 1.00, 0.92 and 3.22, at the target; same sizes |
| Deflate64 decompress | 0.94-1.07, at the target | 0.71-0.74 |
| LZX decompress (against `cabextract` 1.11, on a cabinet from our test encoder) | 1.06, at the target | 1.01, at the target |
| PPMd compress, 7z PPMD, mx 1 and 5 (against `7zz`) | 0.59 and 0.61; size equal | 0.57 and 0.59; size equal |
| PPMd decompress, mx 1 and 5 | 0.60-0.63 | 0.62-0.66 |
| PPMd compress, mx 8 and 9 | 0.65 and 0.67; size equal | 0.65 and 0.67; size equal |
| PPMd decompress, mx 8 and 9 | 0.68-0.70 | 0.72 |
| Dictionary training | 2.9-73x the tool's time on one thread, 1.4-26x with the default thread count; 5-30x its memory | 3-35x the tool's time, 20-40x its memory; output with our dictionaries within 1% of the tool's overall, up to 2.6% larger on tiny training sets and 1.1-2.1% larger with 64 KiB and larger dictionaries on Silesia |

Peak memory of the LZMA family's in-memory calls is 0.5-2 GB on these corpora, because they hold the whole input and
output; the tools stream (5-675 MB). Use the stream types to bound it.

Output is byte-identical across x64 and arm64, the JIT and NativeAOT, with and without hardware
intrinsics, at any thread count, and in the browser (WebAssembly).

## Archives

```csharp
Archive.Extract("data.tar.zst", "out");                         // safe by default: no writes outside "out", limits on
Archive.Create("backup.tar.zst").Add("assets/").Save();          // format and compression from the extension
using var archive = Archive.Open("data.tar");                  // entries, random access when the source can seek
```

Read and written: tar (ustar, pax, GNU, sparse files); zip (store, Deflate, Deflate64, bzip2, PPMd and Zstandard; LZMA and xz
entries read; zip64, data descriptors; WinZip AES, and traditional encryption for reading); 7z (Copy, Deflate,
Deflate64, BZip2, LZMA, LZMA2, PPMd, Delta, the branch filters, Zstandard; solid folders, encoded headers, AES-256); cpio
(newc, crc, odc), ar (common, GNU, BSD) and ISO 9660 with Joliet and Rock Ridge. Read only: cab (stored, MSZIP, LZX) and RAR 5 (list and metadata, stored entries, multi-volume sets; compressed entries raise a typed error). Single streams (`.gz`, `.zst`, `.lz4`,
`.bz2`, `.xz`, `.lz`, and `.br`, which is recognised by its name) and tar inside them (`.tar.gz`, `.tar.zst`, ...) are
one archive. AES needs `System.Security.Cryptography.Aes`, which the browser does not provide. An app that uses few codecs can trim the rest: set the `RecursiveAI.OneArchive.AllCodecs` feature switch to false and register what it needs with `ArchiveCodecs.AddX()` (`docs/archive-codecs.md`). The other containers plug into the same reader and writer interfaces. The model, the safety defaults and limits, and the tar coverage are in `docs/archive.md`.

## Layout

- `src/OneCompress`, `src/OneArchive` - the two packages.
- `tests/` - xUnit tests.
- `fuzz/` - in-process mutational fuzz driver and targets.
- `bench/` - benchmarks; the only project that may reference comparison libraries.
- `smoke/aot-trim`, `smoke/wasm` - NativeAOT/trim and browser-wasm smoke apps.
- `build/ci.sh` - the script CI and developers run.

## Licence

Licensed under either of [MIT](LICENSE-MIT) or [Apache-2.0](LICENSE-APACHE), at your option.
Copyright (c) Recursive AI, LLC.
