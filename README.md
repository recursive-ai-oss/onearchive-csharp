# onearchive-csharp

Compression codecs and archive formats for .NET, written from first principles in managed code.
No native dependencies; the compressed bytes are identical on every platform.

Two packages:

- `RecursiveAI.OneCompress` - codecs and checksums.
- `RecursiveAI.OneArchive` - archive containers and a simple `Archive` API, built on OneCompress.

**Status: 0.1.0-preview.1.** OneCompress has Zstandard (frames, skippable frames, dictionaries and
dictionary training, long-distance matching, multithreaded compression in fixed-size jobs, the seekable
format), LZ4 (block and frame), and CRC32, CRC32C, CRC64, Adler-32 and xxHash64. OneArchive has no
containers yet. Both codecs are preview: see below.

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

Zstandard and LZ4 are preview while their speed is below the reference tools'. Their compression ratio and
byte-identical output are already at the target; speed is what is still being tuned. The Deflate, zlib and gzip
decoder is preview until it has been measured against the reference tools; its encoder is not written yet.
The archive layer (`Archive`, the tar reader and writer) is preview while the containers are being added and the
formats' interop and fuzz coverage grows. Their public types carry `[Experimental]`, so using them is a compiler error until you opt in per codec:

| Codec | Diagnostic ID |
|---|---|
| Zstandard (including dictionary training and the seekable format) | `ONECOMPRESS001` |
| LZ4 | `ONECOMPRESS002` |
| Deflate, zlib and gzip (decoder) | `ONECOMPRESS003` |
| OneArchive: `Archive`, the entry model, readers and writers, tar | `ONEARCHIVE001` |

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
`zstd` 1.5.7 and `lz4` 1.10.0, single-threaded unless noted, on Silesia and enwik8 and on small game
snapshot payloads with trained dictionaries. Speed is ours divided by the reference (1.00 = same speed).
x64 is an AMD Ryzen 9 7950X3D on Linux; arm64 is an Apple M2 on macOS. .NET 10.0.402 with the JIT.

| Rows | x64 | arm64 |
|---|---|---|
| Zstandard compress, levels -5 to 15 | 0.56-0.84; size within +0.93% | 0.63-0.78 |
| Zstandard compress, levels 16-19 | 0.76-0.85 (enwik8 16-18 at the target) | 0.78-0.87 |
| Zstandard compress, levels 20 and 22 | 0.86-1.07; enwik8 level 22 size +1.17% | 0.90-1.02; enwik8 level 22 size +1.17% |
| Zstandard decompress | 12 of 16 rows at the target; the rest 0.86-0.87 | 5 of 8 at the target; the rest 0.86-0.87 |
| Zstandard long-distance matching | 0.60-0.75; Silesia level 3 size +1.04% | 0.66-0.78 |
| Zstandard multithreaded jobs | not yet measured | 0.68-0.85 |
| Game payloads with dictionaries, compress | per-tick frames 1.0-1.4x faster; snapshots and saves 0.26-0.58 | 10 of 48 rows at the target |
| Game payloads with dictionaries, decompress | 29 of 48 rows at the target; frames 1.02-1.44x faster; the rest 0.83-0.86 | not yet measured |
| LZ4 compress | 0.58-0.82; level 2 size +2.2% (Silesia) | 0.54-0.83 |
| LZ4 decompress | 0.80-0.90 | 0.82-0.89 |
| Dictionary training | not yet measured | 3-35x the tool's time, 20-40x its memory; output with our dictionaries within 1% of the tool's overall, up to 2.6% larger on tiny training sets and 1.1-2.1% larger with 64 KiB and larger dictionaries on Silesia |

Output is byte-identical across x64 and arm64, the JIT and NativeAOT, with and without hardware
intrinsics, at any thread count, and in the browser (WebAssembly).

## Archives

```csharp
Archive.Extract("data.tar.zst", "out");                         // safe by default: no writes outside "out", limits on
Archive.Create("backup.tar.zst").Add("assets/").Save();          // format and compression from the extension
using var archive = Archive.Open("data.tar");                  // entries, random access when the source can seek
```

tar (ustar, pax, GNU, sparse files) is implemented, plain and as `.tar.zst`/`.tar.lz4`; the other containers plug into the same
reader and writer interfaces. The model, the safety defaults and limits, and the tar coverage are in `docs/archive.md`.

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
