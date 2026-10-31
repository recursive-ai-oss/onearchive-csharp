# onearchive

**Compression and archives for .NET, written from first principles in managed code: Zstandard, LZ4, Deflate, Brotli, LZMA/xz, bzip2, PPMd and more, plus zip, tar, 7z, ISO and other containers behind one safe `Archive` API, with the same compressed bytes on every operating system, CPU, runtime and thread count.** Every codec is ours: no native libraries, no P/Invoke, nothing but the .NET base class library at runtime. Extraction never writes outside the target directory, limits on size, expansion and entry count are on by default, and malformed input ends in a typed exception that names the format, the byte offset and what was expected.

[![CI](https://github.com/recursive-ai-oss/onearchive-csharp/actions/workflows/ci.yml/badge.svg)](https://github.com/recursive-ai-oss/onearchive-csharp/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](#install)
[![License](https://img.shields.io/badge/license-MIT%20OR%20Apache--2.0-blue)](#licence)

**Status: 1.0.** Every codec and container works, is fuzzed and is covered by interop tests against the reference tools. See [Stability](#stability) for what 1.x promises, the speed section below for where each codec stands, and [CHANGELOG.md](CHANGELOG.md).

## Packages

| Package | What it holds |
|---|---|
| [`RecursiveAI.OneCompress`](https://www.nuget.org/packages/RecursiveAI.OneCompress) | The codecs and checksums: Zstandard (dictionaries, dictionary training, long-distance matching, multithreaded compression in fixed-size jobs, the seekable format), LZ4 (block and frame), Deflate, zlib, gzip, Deflate64, Brotli (with its built-in dictionary), LZMA, LZMA2, xz (CRC-32, CRC-64 and SHA-256 checks, branch and delta filters), lzip, bzip2, PPMd (variants H and I), the LZX decoder, and CRC-32, CRC-32C, CRC-64, Adler-32, xxHash32 and xxHash64. |
| [`RecursiveAI.OneArchive`](https://www.nuget.org/packages/RecursiveAI.OneArchive) | The containers and the `Archive` happy path: tar (ustar, pax, GNU, sparse files), zip (zip64, data descriptors, WinZip AES, ZipCrypto for reading, recovery of damaged files), 7z (solid blocks, encoded headers, AES-256), cpio, ar, ISO 9660 with Joliet and Rock Ridge, cab, the RAR 5 container (listing and stored entries), Microsoft compound files, and single compressed files and `.tar.*` combinations. Depends on OneCompress. |

Neither package has a dependency beyond the .NET base class library (OneArchive references OneCompress). Trimming and NativeAOT drop what an app does not use: no static registry roots the codecs, and Brotli's dictionary is static data reachable only from Brotli, so an app that uses only Zstandard does not carry it. `Archive` registers every codec on first use by default; a trimmed app sets the `RecursiveAI.OneArchive.AllCodecs` feature switch to false and registers the few it needs with `ArchiveCodecs.AddX()` ([docs/archive-codecs.md](docs/archive-codecs.md)).

## Install

```
dotnet add package RecursiveAI.OneCompress
dotnet add package RecursiveAI.OneArchive
```

Both target `net10.0` on Windows, Linux and macOS (x64 and arm64), are NativeAOT- and trim-compatible, and run in browser WebAssembly (AES in zip and 7z needs `System.Security.Cryptography.Aes`, which the browser does not provide).

## The three tiers

Each tier is built on the one below it; nothing in an upper tier reaches past the tier under it. Every tunable is a property of an options object, never an overload.

**One-liners.** Static methods for the common case.

```csharp
byte[] packed = Zstd.Compress(data, new ZstdCompressionOptions { Level = 19 });
byte[] again = Zstd.Decompress(packed);
```

**Encoder, decoder and stream.** Types shaped like `BrotliEncoder` and `BrotliStream`: `OperationStatus` over spans for allocation-free steady-state use, and a `Stream` with synchronous and asynchronous paths.

```csharp
var encoder = new ZstdEncoder(new ZstdCompressionOptions { Level = 5 });
byte[] buffer = new byte[ZstdEncoder.GetMaxCompressedLength(data.Length)];
OperationStatus status = encoder.Compress(data, buffer, out int consumed, out int written, isFinalBlock: true);
// status is Done once the frame is complete: buffer[..written] holds it.

using (var stream = new ZstdStream(output, new ZstdCompressionOptions { Level = 3 }, leaveOpen: true))
{
    stream.Write(data);
}
```

**Raw parts.** The pieces the codecs are made of, public and documented, for bespoke use: Zstandard's FSE and Huffman tables, sequence coding, block writer, match finders and dictionary trainers; LZMA's range coder, match finders and price model; Deflate's Huffman builder and block splitter; the bzip2, Brotli, PPMd and LZX building blocks.

```csharp
// Optimal length-limited Huffman code lengths, the part deflate's block writer uses.
uint[] frequencies = new uint[286];
foreach (byte b in data)
{
    frequencies[b]++;
}

byte[] lengths = new byte[286];
new DeflateHuffmanBuilder().BuildLengths(frequencies, maxBits: 15, lengths);

uint crc = Crc32.Compute(data);
```

## Quick start

The snippets on this page are compiled and run by `tests/OneArchive.Tests/ReadmeSnippetTests.cs`.

**Zstandard with a trained dictionary.** Small, similar messages (game state, log lines, RPC payloads) compress far better against a dictionary trained on samples of them.

```csharp
var samples = new List<ReadOnlyMemory<byte>>();
for (int i = 0; i < 400; i++)
{
    samples.Add(Encoding.ASCII.GetBytes($"{{\"id\":{i},\"kind\":\"move\",\"x\":{i * 7 % 100},\"y\":{i * 13 % 100},\"owner\":\"player-{i % 4}\"}}"));
}

ZstdCompressionDictionary dictionary = ZstdCompressionDictionary.Train(samples, new ZstdDictionaryTrainingOptions { DictionarySize = 2048 });

byte[] message = Encoding.ASCII.GetBytes("{\"id\":9001,\"kind\":\"move\",\"x\":42,\"y\":17,\"owner\":\"player-1\"}");
byte[] packed = Zstd.Compress(message, new ZstdCompressionOptions { Level = 3, Dictionary = dictionary });
byte[] again = Zstd.Decompress(packed, new ZstdDecompressionOptions { Dictionary = dictionary.ForDecompression });
```

Training uses the COVER and fast-COVER algorithms ([docs/zstd-dictionary-training.md](docs/zstd-dictionary-training.md)). Dictionaries are the standard format, so the `zstd` tool reads them.

**LZ4, gzip and xz.**

```csharp
byte[] lz4 = Lz4.Compress(data, new Lz4CompressionOptions { Level = 9 });
byte[] fromLz4 = Lz4.Decompress(lz4);

byte[] gz = GZip.Compress(data, new DeflateOptions { Level = 6 });
byte[] fromGz = GZip.Decompress(gz);

byte[] xz = Xz.Compress(data, new XzCompressionOptions { Lzma = new LzmaOptions { Preset = 6 }, Check = XzCheck.Crc64 });
byte[] fromXz = Xz.Decompress(xz);
```

**Archives.** The format is found from the bytes, then the extension. `Archive.Create` picks format and compression from the file name.

```csharp
Archive.Create(archivePath).Add(source, "assets").Save();    // format and compression come from the extension

using (Archive archive = Archive.Open(archivePath))           // entries; random access when the source can seek
{
    foreach (ArchiveEntry entry in archive.Entries)
    {
        Console.WriteLine($"{entry.Name} {entry.Size}");
    }
}

ArchiveExtractResult result = Archive.Extract(archivePath, outDir);   // safe by default: nothing is written outside outDir
```

**Zip with AES.** WinZip AES-256 (AE-2) on write; the same password on read.

```csharp
Archive.Create(zipPath, new ArchiveCreateOptions
{
    Zip = new ZipWriterOptions { Password = "correct horse", AesStrength = ZipAesStrength.Aes256 },
}).Add(source, "secret").Save();

Archive.Extract(zipPath, outDir, new ArchiveExtractOptions { Password = "correct horse" });
```

**Reading a damaged zip.** Recovery mode is off by default. When on, the reader resynchronises on the next local header, falls back to the local headers when the central directory is gone, and lists everything it skipped ([docs/zip-recovery.md](docs/zip-recovery.md)).

```csharp
using FileStream file = File.OpenRead(zipPath);
using var reader = new ZipReader(file, new ZipReaderOptions { Recovery = true });
while (reader.GetNextEntry() is { } entry)
{
    using Stream content = reader.OpenEntryStream();
    // read what survives
}

foreach (ZipDamage damage in reader.Damage)
{
    Console.WriteLine($"{damage.Kind} at {damage.Offset}: {damage.Reason}");
}
```

## Safety

Readers are parsers of hostile input, so safety is the default and each loosening is an option.

- **Extraction stays inside the target directory.** Absolute paths, drive letters, UNC paths, `..`, backslash separators in zip names, NUL in names, symbolic links and hard links that escape or that a later entry would write through, and device and FIFO entries are refused with an `ArchiveSafetyException` that names the rule and the entry. Each has an option on `ArchiveExtractOptions` to loosen it ([docs/archive.md](docs/archive.md)).
- **Limits are on by default** (`ArchiveLimits`): 1 GiB of total output, an expansion ratio of 1000 once more than 32 MiB were produced, 100,000 entries, 4096-character paths, 64 levels of nesting, an 8 MiB Zstandard window or xz/lzip dictionary. A limit hit raises a `CompressionLimitException`, never a truncated result. Overlapping zip entries and nested zip bombs hit them.
- **Decoder memory is bounded** by the declared window and checked against a configurable maximum before anything is allocated.
- **One typed hierarchy.** Everything derives from `CompressionException`: `CompressionFormatException` (format, byte offset, what was expected), `CompressionLimitException`, `ArchiveSafetyException`, `ArchiveUnsupportedException`, `ArchivePasswordException`. Only these, argument exceptions, `OperationCanceledException` and I/O exceptions from your own streams leave a public method.

```csharp
var options = new ArchiveExtractOptions
{
    Limits = new ArchiveLimits { MaxTotalSize = 100L << 20, MaxEntryCount = 1000 },
};

try
{
    Archive.Extract(archivePath, safeDir, options);
}
catch (CompressionException e)
{
    Console.WriteLine(e.Message);   // names the format, the offset and what was expected
}
```

Every decoder and container parser has a fuzz target, and the unchecked-memory accessors that make the hot loops fast are bounds-checked in the `Checked` build configuration that the tests and fuzzers run in.

## Determinism

Compressed output depends only on the input and the options: never on the operating system, the CPU or its SIMD features, the runtime version, the JIT tier, native versus WebAssembly, or the number of threads. SIMD code computes the same function as its scalar path, never changing which candidates a match finder tries; encoder cost models are integer or fixed point; multithreaded compression cuts the input into jobs whose size comes from the options alone and writes them in order, so one thread and thirty-two give the same bytes. That makes the output usable as a content address and safe in lockstep or replay systems.

A hash matrix compresses fixed inputs at every level and option set of every codec and compares SHA-256 manifests across x64 and arm64, the JIT and NativeAOT, with hardware intrinsics on, off and reduced, at different thread counts, and in the browser ([docs/hash-matrix.md](docs/hash-matrix.md)).

## What it does not do

- **Compressed RAR entries.** The RAR 5 container is read (listing, metadata, stored entries); compressed entries raise a typed unsupported-method error. RAR's compression is proprietary and a decoder is not planned.
- **Quantum folders in cab.** There is no public specification; they raise a typed unsupported-method error. Stored, MSZIP and LZX folders are read.
- **7z's BCJ2 filter.** Entries that use it are listed and raise a typed unsupported-method error when opened.
- **AES in the browser.** Zip and 7z encryption need the BCL's `Aes`, which browser WebAssembly lacks; ZipCrypto reading works there.
- **Spanned zip archives and PKWARE strong encryption.**
- **Formats left for later:** UDF, WIM, xar, LHA, WARC, dmg and vhd.

## Documentation

[docs/README.md](docs/README.md) indexes one page per codec family and per container: what is supported, the options, the defaults and limits, and the specification sections. The pages are normative and language-neutral, so another implementation can reproduce the same bytes from them. The API reference is the XML documentation shipped in both packages; every public type and member has one. Benchmark method: [docs/bench-method.md](docs/bench-method.md).

## Stability

From 1.0 the public API follows [semantic versioning](https://semver.org/): breaking changes only in a major version. Compressed output is part of the contract: for a given input and options, each codec and level writes the same bytes on every platform and in every 1.x release, and a change to those bytes is a major version. Decoders may become faster or stricter about malformed input, never less compatible with valid streams. The 1.0 speed results against the fastest managed .NET libraries are in the table below.

## Speed against the fastest managed .NET libraries

The goal for 1.0 is to be the fastest managed .NET implementation of each codec, with output no more than 1% larger. Each table row is our speed divided by the speed of the **fastest library written entirely in managed code** (1.00 = same speed, above 1.00 = faster), single-threaded, memory to memory, best of three interleaved runs, on `silesia.tar`. Slow levels run on a sample of it (four equally spaced pieces, 10 to 64 MiB in total) so a row finishes in reasonable time; the same sample feeds every candidate. A row passes at 0.87 or better with output within +1%. The native tools (`zstd`, `xz`, `brotli`, zlib-ng) and the .NET base class library's native-backed codecs are measured and kept for information.

Level numbers of different libraries are not comparable (one library's "level 6" can trade far more size for speed than another's), so a compression row is also **size-matched**: it is met when some level of ours writes at most 1.01 times the library's bytes at no less than 0.87 times its speed. The notes name the level that meets it. Method, candidates and the size-matched rule: [docs/bench-method.md](docs/bench-method.md).

- **x64:** AMD Ryzen 9 7950X3D (16 cores, 32 threads), Linux, .NET 10.0.402.
- **arm64:** Apple M2 (8 cores), macOS, .NET 10.0.402.
- **Candidates** (pinned versions): ZstdSharp.Port 0.8.8, K4os.Compression.LZ4 1.3.8, SharpCompress 1.0.0, SharpZipLib 1.4.2, DotNetZip 1.16.0, BrotliSharpLib 0.3.3, BrotliManagedFast 1.1.0, Lzma.Net 5.8.4.3, LZMA-SDK 22.1.1, System.IO.Hashing 10.0.0. Used only from `bench/`, as packages; never shipped.
- **Measured** 2026-10-30; rows for work done since (Zstandard levels 1-18, xz presets 0-3, LZ4 dictionary decode) are remeasured as noted. Rows are refreshed at each release. Rows below 0.87 are shown as they are.

### Zstandard (against ZstdSharp.Port)

| Row | x64 | arm64 | Size vs ZstdSharp | Notes |
|---|---:|---:|---:|---|
| Compress level 1 | 0.84 | 0.96 | +0.5% | x64 below the bar |
| Compress level 2 | 0.81 | 0.93 | +0.4% | x64 below the bar |
| Compress levels 3-4 | 0.96 | 0.92-0.94 | +0.9% | |
| Compress levels 5-9 | 0.89-0.94 | 1.04-1.09 | -0.5% to +0.8% | |
| Compress level 12 | 1.06 | 1.20 | +0.4% | |
| Compress levels 13-15 | 0.98-1.10 | 0.81 (13), 0.87 (14), 0.97 (15) | within 0.5% | arm64 level 13 below the bar |
| Compress levels 16-18 | 1.02-1.07 | 0.96-1.07 | -0.1% | |
| Compress level 19 | 0.91 | 0.90 | +0.4% | |
| Compress level 22 | 1.06 | 0.97 | +0.5% | |
| Decompress, levels 1 / 3 / 19 | 0.94 / 0.91 / 1.00-1.03 | 0.97 / 1.01 / 1.01 | | |
| Long-distance matching, level 3 | 0.85-0.91 | 0.89 | +0.7% | |
| Two workers, level 3 | 0.94 | 0.92 | +0.8% | |
| With a trained dictionary, small game payloads, compress (frames, level 1 / 3) | 1.65 / 1.27 | 1.52 / 1.23 | -0.4% / -0.8% | |
| Same, snapshots, compress (level 1 / 3) | 0.95 / 0.96 | 0.76 / 0.73 | -1.2% / -1.0% | arm64 below the bar |
| Same, decompress (frames / snapshots) | 0.94 / 0.91 | 0.94 / 0.98 | | |

### LZ4 (against K4os.Compression.LZ4)

| Row | x64 | arm64 | Size vs K4os |
|---|---:|---:|---:|
| Compress level 1 | 1.40 | 1.23 | 0.0% |
| Compress level 3 | 1.31 | 1.28 | +0.2% |
| Compress level 9 | 1.37 | 1.28 | +0.8% |
| Compress level 12 | 2.05 | 2.38 | +0.6% |
| Decompress | 1.13 | 1.12 | |
| Decompress with a dictionary (game payloads) | 0.94-1.00 | 0.90-0.96 | |

No managed library compresses with an LZ4 dictionary, so those rows have no comparison.

### Deflate, zlib and gzip (against DotNetZip, SharpZipLib and SharpCompress; the fastest of them per row)

| Row | x64 | arm64 | Size vs the library |
|---|---:|---:|---:|
| Compress level 1 | 2.62 | 2.52 | -4.3% |
| Compress level 3 | 2.61 | 2.38 | -1.4% |
| Compress level 6 | 3.22 | 2.60 | -1.1% |
| Compress level 9 | 3.54 | 3.11 | -1.3% |
| Decompress | 3.14 | 2.60 | |
| Streaming compress level 6 (64 KiB pieces) | 3.21 | 2.56 | -1.1% |
| Streaming decompress | 2.89 | 2.45 | |
| Deflate64 decompress (against SharpCompress) | 3.19 | 2.73 | |

For information, against the .NET base class library (zlib-ng underneath), x64: compress 0.61 (level 1), 1.01 (3), 0.91 (6), 1.35 (9) and decompress 1.28.

### Brotli (against BrotliManagedFast and BrotliSharpLib)

| Row | x64 | arm64 | Size vs the fastest |
|---|---:|---:|---:|
| Compress quality 0 | 1.07 | 0.91 | +0.9% |
| Compress quality 1 | 1.13 | 0.99 | +1.1% |
| Compress quality 2 | 0.75 | 0.66 | -2.2% |
| Compress quality 4 | 0.47 | 0.42 | -6.8% |
| Compress quality 5 | 0.69 | 0.67 | -7.0% |
| Compress quality 7 | 0.32 | 0.29 | -11.6% |
| Compress quality 9 | 1.11 | 0.72 | -6.2% |
| Compress quality 11 | 0.90 | 0.94 | +1.3% |
| Decompress (quality 5 stream) | 0.79 | 0.89 | |

Brotli is the codec furthest from the bar. Against BrotliSharpLib, every quality is met on both machines by size-matching (for example quality 5 by our quality 5 at 1.51 / 1.46 with 2.1% smaller output), but BrotliManagedFast is faster at qualities 2 to 7 and writes larger output, so those rows are not met.

### LZMA, LZMA2, xz and lzip (against Lzma.Net, SharpCompress and the LZMA SDK package)

| Row | x64 | arm64 | Notes |
|---|---:|---:|---|
| xz decompress (preset 6 stream) | 1.33 | 1.31 | |
| LZMA decompress | 1.34 | 1.31 | |
| LZMA2 decompress | 1.51 | 1.47 | against SharpCompress |
| xz compress preset 0 | 1.06 | 1.05 | |
| xz compress preset 1 | 0.93 | 0.87 | |
| xz compress preset 3 | met by our preset 2 (0.96) | met by preset 2 (0.91) | Lzma.Net's preset 3 writes 5% larger output |
| xz compress preset 6 | met by our preset 3 (1.24) | met by preset 3 (1.21) | our preset 6 is 14.6% smaller but 0.29 as fast |
| xz compress preset 9 | 1.16 | 1.31 | +0.4% |
| LZMA compress (our preset 7 vs SharpCompress) | 1.15 | 1.12 | +0.3% |
| lzip decompress | 1.50 | 1.47 | against SharpCompress |
| lzip compress | met by levels 5 and 6 (1.05, 0.94) | met by levels 5 and 6 (0.96, 0.92) | SharpCompress ignores the level and writes 6-10% more than our levels 5 and 6 |

No managed library compresses LZMA2 on its own, so that row has no comparison.

### bzip2, PPMd and checksums

| Row | x64 | arm64 | Against |
|---|---:|---:|---|
| bzip2 compress level 9 (+0.3%) | 2.03 | 1.83 | SharpZipLib |
| bzip2 decompress | 1.47 | 1.39 | SharpZipLib |
| PPMd variant I compress / decompress (same size) | 2.11 / 2.13 | 2.07 / 2.00 | SharpCompress |
| PPMd variant H decompress | 3.81 | 3.41 | SharpCompress |
| CRC-32 | 1.71 | 1.63 | System.IO.Hashing |
| CRC-64 | 1.75 | 0.95 | System.IO.Hashing |
| xxHash64 | 1.46 | 1.70 | System.IO.Hashing |
| Adler-32 | 8.56 | 8.17 | DotNetZip |

No managed implementation exists for PPMd variant H compression, CRC-32C, LZX, MSZIP or the Zstandard seekable format, so those pass by being the only ones.

## Layout

| Path | What |
|---|---|
| `src/OneCompress/`, `src/OneArchive/` | the two packages |
| `tests/OneCompress.Tests/`, `tests/OneArchive.Tests/` | unit tests (xUnit v3), including the compiled README snippets |
| `smoke/` | NativeAOT and trim smoke apps (full, Zstandard-only, compound-file-only) and a browser-wasm smoke app |
| `bench/` | the bar runner, micro-benchmarks, and `bench/Managed`, the comparison against managed .NET libraries (the only projects that reference comparison packages) |
| `fuzz/` | in-process fuzz driver and targets for every decoder and container parser |
| `tools/` | corpus and reference-tool scripts, table generators, the hash-matrix runner and the bench drivers |
| `docs/` | normative notes on each codec and container; start at [docs/README.md](docs/README.md) |
| `build/ci.sh` | the script CI and developers run |

## Building

Requires the .NET 10 SDK (see `global.json`).

```sh
build/ci.sh              # build, test (Checked configuration), NativeAOT/trim smoke
build/ci.sh wasm         # browser-wasm build and a headless run under Node (needs the wasm-tools workload)
```

Build output goes outside the source tree: `artifacts/` (ignored by git), or the directory in `ONEARCHIVE_ARTIFACTS`, for example one per git worktree. Configurations are `Debug`, `Release` and `Checked` (optimized, with the internal unchecked accessors bounds-checked; tests and fuzzing run in it). Warnings are errors, and a public member without an XML doc comment fails the build.

## Licence

Licensed under either of [MIT](LICENSE-MIT) or [Apache-2.0](LICENSE-APACHE), at your option. Copyright (c) Recursive AI, LLC. Tables transcribed from IETF RFCs, the PPMd public-domain sources and Microsoft's open specifications are listed with their terms in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
