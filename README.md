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
