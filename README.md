# onearchive-csharp

Compression codecs and archive formats for .NET, written from first principles in managed code.
No native dependencies; the compressed bytes are identical on every platform.

Two packages:

- `RecursiveAI.OneCompress` - codecs and checksums.
- `RecursiveAI.OneArchive` - archive containers and a simple `Archive` API, built on OneCompress.

**Status: 0.2.0-preview.1.** OneCompress has Zstandard (frames, skippable frames, dictionaries and
dictionary training, long-distance matching, multithreaded compression in fixed-size jobs, the seekable
format), LZ4 (block and frame), Deflate, zlib and gzip, LZMA, LZMA2, xz (with the branch and delta filters)
and lzip, the bzip2 decoder, and CRC32, CRC32C, CRC64, Adler-32 and xxHash64. OneArchive reads and writes
tar and zip, reads 7z, and handles single compressed streams. Everything is preview: see below.

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

Zstandard, LZ4, Deflate, zlib and gzip are preview while their speed is below the reference tools'. Their
compression ratio and byte-identical output are already at the target; speed is what is still being tuned. The
bzip2 decoder meets its target and stays preview with the rest of this release. The LZMA family is preview until it
has been measured on the benchmark machines. The archive layer is preview while the containers are being added and
the formats' interop and fuzz coverage grows. PPMd, BCJ2, Deflate64 and encryption are not supported: archive
entries that use them are listed, and opening them is a typed "unsupported method" error. Their public types carry `[Experimental]`, so using them is a compiler error until you opt in per codec:

| Codec | Diagnostic ID |
|---|---|
| Zstandard (including dictionary training and the seekable format) | `ONECOMPRESS001` |
| LZ4 | `ONECOMPRESS002` |
| Deflate, zlib and gzip | `ONECOMPRESS003` |
| bzip2 (decoder) | `ONECOMPRESS004` |
| LZMA and LZMA2 | `ONECOMPRESS005` |
| xz and the branch and delta filters | `ONECOMPRESS006` |
| lzip | `ONECOMPRESS007` |
| OneArchive: `Archive`, the entry model, readers and writers, tar, zip, 7z | `ONEARCHIVE001` |

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
`zstd` 1.5.7, `lz4` 1.10.0, libdeflate 1.26 (one call), zlib-ng 2.3.3 (streaming) and `bzip2` 1.0.8, single-threaded unless noted, on Silesia and enwik8 and on small game
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
| Deflate compress, one call, levels 1-9 (against libdeflate) | 0.58-0.81; size -1.71% to +0.61% | 0.55-0.81 (level 8 at the target) |
| Deflate compress, one call, levels 10-12 | Silesia 0.89-0.96, enwik8 0.84-0.86; size within +0.90% | 0.89-1.07, at the target |
| Deflate compress, streaming in 64 KiB pieces, levels 1-9 (against zlib-ng) | levels 1-4 0.56-0.79, Silesia 5-8 and enwik8 6, 7, 9 at the target; output 0.5-26% smaller | levels 7 and 8 at the target, the rest 0.64-0.79 |
| Deflate decompress | 0.60-0.71 of libdeflate, 0.88-0.97 of zlib-ng | 0.61-0.70 of libdeflate, 1.01-1.15 of zlib-ng |
| bzip2 decompress (against `bzip2 -d`, start-up subtracted) | 1.28-1.47, at the target | 0.99-1.10, at the target |
| Dictionary training | 2.9-73x the tool's time on one thread, 1.4-26x with the default thread count; 5-30x its memory | 3-35x the tool's time, 20-40x its memory; output with our dictionaries within 1% of the tool's overall, up to 2.6% larger on tiny training sets and 1.1-2.1% larger with 64 KiB and larger dictionaries on Silesia |

Output is byte-identical across x64 and arm64, the JIT and NativeAOT, with and without hardware
intrinsics, at any thread count, and in the browser (WebAssembly).

## Archives

```csharp
Archive.Extract("data.tar.zst", "out");                         // safe by default: no writes outside "out", limits on
Archive.Create("backup.tar.zst").Add("assets/").Save();          // format and compression from the extension
using var archive = Archive.Open("data.tar");                  // entries, random access when the source can seek
```

tar (ustar, pax, GNU, sparse files) and zip (store, Deflate and Zstandard; zip64, data descriptors; LZMA, xz and bzip2
entries read) are read and written; 7z is read (Copy, Deflate, BZip2, LZMA, LZMA2, Delta, the branch filters, Zstandard;
solid folders, encoded headers). Single streams (`.gz`, `.zst`, `.lz4`, and `.bz2` for reading) and tar inside them
(`.tar.gz`, `.tar.zst`, ...) are one archive. The other containers plug into the same reader and writer interfaces. The model, the safety defaults and limits, and the tar coverage are in `docs/archive.md`.

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
