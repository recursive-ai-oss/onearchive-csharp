# Fuzzing

Two ways to run the same targets (`FuzzTargets.All` in `FuzzTarget.cs`). A target feeds bytes to a decoder, or
to an encoder followed by a decoder that must give the input back. It either returns or throws one of the
library's own exceptions; anything else (a different exception, a mismatch, a hang, running out of memory)
is a finding.

## Quick smoke: the built-in mutational driver

Works anywhere .NET runs, including WebAssembly. It mutates seed files blindly, with no coverage feedback.

```
dotnet run --project fuzz -c Checked -- list
dotnet run --project fuzz -c Checked -- zstd-decode --seeds <dir> --seconds 60 --out findings
```

Findings are written to `--out`. Set `ZSTD_FUZZ_TESTDATA=testdata/zstd` so the dictionary targets find their
dictionaries.

## Coverage-guided: AFL++ with SharpFuzz

Linux x64 with gcc, make, curl and a .NET SDK.

```
tools/install-afl.sh                      # AFL++ (pinned release, built locally) and the SharpFuzz instrumenter -> ~/onearchive/tools/bin
fuzz/run-afl.sh                           # all targets, 30 minutes in total, 8 cores
fuzz/run-afl.sh -m 60 -c 4 zstd-decode lz4-block   # chosen targets, 60 minutes, 4 at a time
```

The runner builds the fuzz project in the `Checked` configuration (the unchecked accessors become
bounds-checked), instruments a copy of the codec assemblies, creates seed corpora (`fuzz/make-seeds.sh`: the
vectors in `testdata/`, output of the `zstd` and `lz4` tools when they are on PATH, and recipes for the
round-trip targets), then runs one `afl-fuzz` instance per target under `nice`, at most `-c` at a time, never
more than 8. The total time is split evenly across the waves.

Everything lives under `~/onearchive/fuzz` (change it with `-w`): `out/<target>/default/queue` is the corpus,
`crashes/` and `hangs/` hold findings, and `report.md` has a row per target with executions, edges found,
corpus size, findings and edge growth over the run. A hang is an execution over 10 s (a fixed limit: AFL++ would otherwise shrink it to a few milliseconds and report ordinary stalls on a busy machine as crashes); a heap above 3 GiB
fails as an out-of-memory crash.

To turn a finding into a test: minimize it (`afl-tmin -i <file> -o min -- dotnet <build>/OneArchive.Fuzz.dll afl <target>`),
fix the cause, and add the minimized input (under 1 MB) with a regression test that runs the target on it.

A target added as one line in `FuzzTargets.All` is picked up by both drivers, the seed directory
`seeds/<target>` is optional (a one-byte seed is used when absent), and `make-seeds.sh` is where its seeds go.

## Codec targets

Every decoder has a `<codec>-decode` target (`zstd`, `lz4`, `deflate`, `gzip`, `zlib`, `bzip2`, `lzma`, `xz`, `lzip`, `brotli`, `deflate64`, `ppmd`, `lzx`) and every encoder a `<codec>-encode-roundtrip` target (random input and options, then the decoder must return the input), including `zstd-train-roundtrip` for dictionary training. `fuzz list` prints them all. Seeds for `cab-read` and `lzx-decode` come from `seeds-cab.sh` (`fuzz seeds-cab DIR`).

## Archive targets

`tar-read` (the tar reader on a seekable and a forward-only stream, which must agree), `archive-open` (detection, `Archive.Open`,
listing, random access and extraction into a scratch directory, then a check that nothing was created outside it) and
`tar-roundtrip` (entries built from the input are written and read back). Seeds come from `seeds-tar.sh`: GNU tar and bsdtar
archives over a tree with long names, links and sparse files, and the hostile set from `testdata/tar/make-hostile.py`.

Also covered: `cpio-read`, `cpio-roundtrip`, `ar-read`, `ar-roundtrip`, `iso-read`, `iso-roundtrip`, `cfb-read`, `cfb-rewrite` and `cfb-write` (compound files; seeds from `seeds-cfb.sh`), `sevenzip-read`, `sevenzip-roundtrip` and `cab-read`, with seeds from the matching `seeds-*.sh`.

## Zip and single-stream targets

`zip-read` (the zip reader through the central directory, with entries re-opened out of order, and forward-only in odd pieces;
every entry read must have the length it declares), `zip-roundtrip` (entries built from the input are written with an option
mix - store or zstd, zip64, data descriptors, seekable or not - and read back both ways), `zip-recover` (the reader in recovery mode on a seekable
and a forward-only stream: damage records must lie inside the input, a failed entry must be in the damage list, and an archive the strict reader
accepts must come out of recovery with the same entries and no damage; seeds are the `zip-read` archives whole, cut in half and with text spliced in) and `single-stream-read`
(`Archive.Extract` over compressed single files and everything else, then the same nothing-outside check). Seeds come from
`seeds-zip.sh`: Info-ZIP, bsdtar and 7-Zip archives (streamed and zip64 variants too), gzip, zstd and lz4 files, and the hostile
set from `testdata/zip/make-hostile.py`.

## Encrypted archives

`zip-read-encrypted`, `zip-roundtrip-encrypted`, `sevenzip-read-encrypted` and `sevenzip-roundtrip-encrypted` are the zip and 7z
targets run with the password `fuzz-password`: WinZip AES and traditional headers, verifiers and authentication codes, 7z AES
properties, encrypted headers and the wrong-password paths. The 7z read target caps the key-derivation power at 8 so that a
mutated power is a limit error instead of a stall. Round trips write with the salt and IV hooks fixed, so two writes agree.

`rar-read` reads the input as a RAR 5 archive from a seekable and from a forward-only stream (which must agree), then again after the
header CRC-32 values along the block chain are repaired, so that mutated fields reach the parsers; later volumes, when asked for, are the
input again. Stored entries are read to their end and must have the length they declare. Seeds come from `seeds-rar.sh`: the archives
in `testdata/rar` (made by the RAR tool, see `tools/rar-testdata/generate.sh`).

Seeds come from `seeds-encrypted.sh` (`fuzz seeds-encrypted DIR` writes the library's own encrypted archives).
