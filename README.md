# onearchive-csharp

Compression codecs and archive formats for .NET, written from first principles in managed code.
No native dependencies; the compressed bytes are identical on every platform.

Two packages (names are provisional):

- `RecursiveAI.OneCompress` - codecs and checksums.
- `RecursiveAI.OneArchive` - archive containers and a simple `Archive` API, built on OneCompress.

**Status: pre-release.** The repository is a skeleton; no codec or container is implemented yet.

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
byte-identical output are already at the target; speed is what is still being tuned. The archive layer
(`Archive`, the tar reader and writer) is preview while the containers are being added and the formats' interop and fuzz
coverage grows. Their public types
carry `[Experimental]`, so using them is a compiler error until you opt in per codec:

| Codec | Diagnostic ID |
|---|---|
| Zstandard (including dictionary training and the seekable format) | `ONECOMPRESS001` |
| LZ4 | `ONECOMPRESS002` |
| OneArchive: `Archive`, the entry model, readers and writers, tar | `ONEARCHIVE001` |

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);ONECOMPRESS001;ONEARCHIVE001</NoWarn>
</PropertyGroup>
```

or `#pragma warning disable ONECOMPRESS001` around the code that uses it. The marker comes off once the codec
meets its speed target.

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
