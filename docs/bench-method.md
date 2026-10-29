# Benchmark method

Normative description of how the bar is measured. It is language-neutral: another implementation of the
runner must produce comparable numbers by following this text.

## What a row is

One row compares one implementation of a codec with one pinned reference tool at one level on one file:

| field | meaning |
|---|---|
| size ratio | our compressed size / reference compressed size |
| compress, decompress time ratio | our best time / reference best time (equivalently reference MB/s / ours) |
| MB/s | input bytes / 10^6 / seconds, for both directions, on the uncompressed size |
| peak memory | maximum resident set size of each side, in MB (see below) |
| verdict | `pass` when size ratio <= 1.01 and both time ratios <= 1.15, else `FAIL`; `-` for comparison rows |

A level "N" always means the reference tool's level N. A codec passes the bar only when every row for it passes
on both the x64 and the arm64 bench host.

Implementations are either **ours** (gated), including stand-ins used to prove the plumbing before a codec
exists, or **comparison** libraries (ZstdSharp, K4os.Compression.LZ4, the BCL codecs, and so on), which are
reported and never gated.

## Conditions

- **Memory to memory, warm.** The whole input is read into memory before timing; our side compresses into a
  pre-allocated buffer and decompresses into a pre-allocated buffer of the original size. Disk I/O and
  allocation are outside the timed region. Two untimed warm-up runs precede the timed runs so that the JIT has
  reached its final tier.
- **Best of N.** N is 5 unless stated. The row records the fastest run, which is the least disturbed by
  other activity.
- **One thread** unless the level is multithreaded. For a multithreaded level the reference is run with the
  same thread count as ours.
- **Fresh process per measurement** on our side: compression, decompression and the round-trip check each
  run in their own process, so the peak memory of each can be read from the operating system. The round-trip
  check compares the decompressed bytes with the input; a mismatch fails the run.
- **Reference files** live in RAM where the host allows (`/dev/shm` on Linux). On macOS, which has no
  user-mountable RAM file system without privileges, they are ordinary files that are warm in the page cache;
  the row records which was used.
- **Quiet machine.** The benchmark takes a host-wide lock (an atomically created file naming the project and the
  expected end time) and refuses to start while the other work on the host uses more than 2 cores, measured over
  3 seconds from the kernel's CPU counters. A run that overlapped another project's heavy job is discarded and
  repeated.
- **Load.** The 1-minute load average is recorded before, during (every 5 seconds; maximum and mean) and after.
  The run's own single busy thread contributes about 1.

## References

### Tools with an in-memory benchmark mode

`zstd -b<L> -e<L> -i<S> -T<threads> file` and `lz4 -b<L> -e<L> -i<S> -T<threads> file` (S seconds, default 3).
The tool's reported compressed size and best compression and decompression speeds are used as they are. The
tool holds the file in memory and times the codec only.

### Tools without one

The command is run through `sh` on files in the work directory with standard input and output redirected
(`tool -L < in > out`), so the output carries no file name:

1. The same compression and decompression commands run on an empty input, N times; the best time is the
   process **start-up cost** of that command.
2. The command runs on the real input once as a warm-up and then N times; the best time minus the start-up cost
   is the reference time. Decompression is timed the same way on the reference's own output. The decompressed
   length must equal the input length.

The reference's own timed region therefore includes reading the input and writing the output, which is
slightly in its disfavor against our in-memory timing; this is deliberate and stated in each row's method
column. Small inputs (under about 1 MB) make the start-up subtraction noisy; use larger files for gating.

### Container framing

Where the reference writes a framed format but the codec under test is a bare stream, the reference's size is
corrected: the gzip framing (10-byte header and 8-byte trailer, measured as the size of the reference's
output for an empty input minus the 2 bytes of an empty deflate stream) is removed for the `deflate` codec, and
the same with 6 bytes of zlib framing added back for `zlib`. For `gzip` no correction is made.

### Pinned tools

The references are the tools pinned in `tools/pins.tsv` (version, URL, SHA-256), installed user-locally by
`tools/install-tools.sh`:

| codec | reference |
|---|---|
| zstd | `zstd` |
| lz4 | `lz4` |
| deflate, zlib, gzip | zlib-ng's `minigzip` (streaming) and `libdeflate-gzip` (one-shot); both rows are reported |
| brotli | `brotli` |
| xz, lzma | `xz` (single-threaded) |
| bzip2 | `bzip2` |
| lzip | `lzip` |

7-Zip (`7zz`), pigz, zlib's `minigzip`, bsdtar, GNU tar, Info-ZIP, cpio, ar, xorriso, cabextract and the RARLAB
`rar`/`unrar` binaries are installed for interoperability tests and as references for the container and decoder rows.
Every row records the reference's version string.

## Peak memory

Peak memory is the maximum resident set size reported by `/usr/bin/time -v` (Linux, kilobytes) or
`/usr/bin/time -l` (macOS, bytes) for a run of the whole command. Our figure is the larger of the compression
and decompression processes and includes the .NET runtime itself, so the row also records a **baseline**: the same
process compressing an empty input. Net memory of the codec is peak minus baseline. For references with a
benchmark mode (zstd, lz4) the figure is one process that compresses and decompresses.

## Corpora

Fetched, never committed, by `tools/fetch-corpora.sh` into `$ONEARCHIVE_HOME/corpora`, each file pinned by
SHA-256 in `tools/corpora.sha256`:

- **Silesia** (12 files; the published per-file MD5s are checked too), plus `silesia.tar`: a ustar archive of
  the files sorted by name, with owner, group and time zeroed, so that every host builds identical bytes.
- **Canterbury**: the `cantrbry`, `large` and `artificl` sets.
- **enwik8**: the first 10^8 bytes of an English Wikipedia dump (MD5 `a1fa5ffddb56f4953e226637dabbb36a`).
- **Game snapshot data** (`game/seed1`, `game/seed2`): generated, not downloaded, by the `OneGame.Corpus`
  tool of the public onegame repository (MIT OR Apache-2.0) at the commit pinned in `tools/game-corpus.tsv`,
  with `tools/fetch-game-corpus.sh`. Each seed is about 66 MB in about 800,000 mostly tiny files:
  `<game>/<kind>/NNNNNN.bin` plus `manifest.tsv` (game, kind, file, size, sha256, schema, tick); games are
  lanterns, bricks and solitaire, kinds are frames, snapshots, saves, recordings and transfer. The generator is
  deterministic, and the manifest SHA-256 it prints is pinned per seed, so every host builds identical data.
  Dictionaries are trained on seed 1 and benchmarked on seed 2, per (game, kind). Rows for these payload sets
  (ratio against dictionary size, speed on small frames) are measured by `tools/zstd-encode-bench`, `tools/zstd-decode-bench`, `tools/lz4-bench` and `tools/small-bench`.

## Deflate, zlib and gzip

The references for this family have no in-process benchmark mode, so `tools/deflate-bench/refbench.c` (built by
`tools/deflate-bench/build.sh` against the pinned libdeflate and zlib-ng installs) drives their public APIs as a
black box on a file held in memory and times only the library call:

- `libdeflate enc <level>`: one-shot gzip compression into a buffer sized by the library's bound. Our side compresses the
  same file in one call (`DeflateBench enc <file> <level>`), levels 1 to 12.
- `zlibng enc <level>`: streaming deflate fed in 64 KiB pieces. Our side feeds the same pieces through the encoder
  (`--stream`), levels 1 to 9.
- `dec`: decompression of the other tool's own gzip output and of ours (written with `DeflateBench enc --out`), by the
  reference library and by us, at levels 1, 6, 9 and 12 (zlib-ng up to 9).
- `bzip2`: our bzip2 decoder against `bzip2 -dc` on the tool's own `-1` and `-9` output. The CLI has no in-memory mode, so
  its wall time (output to the null device, best of N) has the process start-up subtracted, measured the same way on a
  one-byte stream.

Both sides use the best of N passes after warm-up (N is `--reps`, default 5); ours also reports the median and slowest pass. The gzip wrapper is included on both
sides, so sizes compare directly. `tools/deflate-bench/bar.sh --mode oneshot|stream|decode` prints one row per file and
level with sizes, MB/s of uncompressed data, the time ratio (the reference's MB/s divided by ours),
peak resident memory of each process, and pass or FAIL against the usual limits (size at most 1.01, time at most 1.15).
The row header records CPU, core count, SDK and load before and after. Corpora are silesia.tar and enwik8 for the
bar; the Canterbury and single Silesia files are used for direction.

## LZMA family (xz, lzip, 7z)

References: `xz` (pinned 5.8.4, `-T1`), `lzip` (pinned 1.26) and `7zz` (pinned), all run as black-box command-line programs.
Ours: `Xz.TryCompress` at preset N, `Lzip.TryCompress` at level N, `Xz.TryDecompress`/`Lzip.TryDecompress`, and `SevenZipWriter`
at level N, all one call in memory over a buffer provided by the caller (`tools/lzma-bench/LzmaBench`).

- **Compress and decode rows** (`tools/lzma-bench/bar.sh --mode enc|dec`). The tools have no in-memory benchmark mode, so a
  tool's time is the wall time of one run with the file on standard input and the output to a file or `/dev/null`, minus the
  wall time of the same tool on a one-byte input (best of 20, about a millisecond), best of the repeats. Our time is the best
  of the repeats of the in-process call. A first pass of ours longer than 8 s counts as a repeat; a shorter one is a warm-up and
  is dropped. Rates are MB/s of uncompressed data. Peak memory comes from `/usr/bin/time -v` (`-l` on macOS) around the tool
  and around our process; ours holds the whole input and output, as the in-memory API does, the tool streams.
- **Levels.** xz presets 0-9 and lzip levels 0-9, the same number on both sides; the extreme variants (`-9e`) are not run. Both
  sides use one thread (`xz -T1`; lzip has no threads).
- **Decode rows** take the tool's own output at the level and ours at the level, and decode each with the tool (`-d -c`) and
  with ours; the default levels are 0, 6 and 9. The compressed files are made once per job and shared between steps.
- **Round trip.** Every compressed output of ours that is written is decoded by our decoder and compared with the input, outside
  the timed region. The reference tool's decode of ours is covered by the interop tests.
- **7z rows** compare sizes only: a one-file archive written by `SevenZipWriter` at level N against `7zz a -mx=N -mmt=1` (LZMA2,
  solid, compressed header on both sides), N = 1, 5, 9.
- **Pass rule** as for the other codecs: size at most 1.01 times the tool's, and the tool's rate at most 1.15 times ours.
  Chunk `0.3` of `tools/bar-x64/run.sh` runs the steps in order of cost (presets 0-3, decode, lzip 0-3, 7z sizes, then presets
  4-6, then 7-9 with a single repeat) so that a deadline drops the slowest rows first.

## brotli, bzip2, Deflate64 and LZX (chunk 0.4)

References: `brotli` 1.2.1, `bzip2` 1.0.8, `7zz` 26.05 and `cabextract` 1.11, all pinned and run as command-line programs. The tools' time
is the wall time of one run with output to `/dev/null` (or a RAM file for 7-Zip archives) minus the wall time of the same command on a
one-byte input, best of the repeats; ours is the best of the repeats of the in-process call (`tools/bar04-bench`, and
`tools/brotli-bench` for the brotli encoder). Rates are MB/s of uncompressed data; peak memory comes from `/usr/bin/time` around both
sides (for the brotli encoder, around the tool inside `BrotliBench` and from the process's own peak for ours).

- **brotli encode** (`tools/brotli-bench/bar.sh`): qualities 0 and 5 (3 repeats) and 11 (1 repeat) at window 22, `brotli -q N -w 22`.
  Chunk 0.4 measures each codec at its fastest, default and maximum level on silesia.tar, to keep a run under about 45 minutes.
- **brotli decode** (`bar04-bench/bar.sh --mode brotli-dec`): the tool's stream and ours at qualities 5 and 11, decoded by `brotli -d -c`
  and by `Brotli.TryDecompress` into a buffer that is already allocated. Our output is compared with the original after the timing.
- **bzip2 encode** (`--mode bz2-enc`): levels 1 and 9 against `bzip2 -N -c` on stdin. Our stream is decoded
  by `bzip2 -dc` and compared with the input (column `interop`; a mismatch is a MISS).
- **Deflate64** (`--mode d64-enc|d64-dec`): levels 1, 5, 9 against `7zz a -tzip -mm=Deflate64 -mx=N -mmt=1`. 7-Zip has no raw-stream
  mode, so its compress time includes reading the file, CRC-32 and writing the zip (visible at `-mx=1`, which favours the reference
  there), and its size is the entry's compressed size from the central directory. Decode compares our raw-stream decode with
  `7zz e -so` on a zip (which also verifies the CRC-32); the zip around our stream is written by `d64-zip`, which `7zz` must read
  back to the original bytes (column `interop`).
- **LZX decode** (`--mode lzx-dec`): there are no Microsoft-made cabinets of corpus size, so a one-file cabinet (window 2^21, 32 KiB
  frames, verbatim and aligned blocks) is made by the test compressor in `tests/OneCompress.Tests/Lzx`, compiled into the bench. `cabextract -q -p`
  must extract it byte-exact before it is timed. Both sides read the same bytes; ours goes through `CabReader` with checksums verified.
  The rate depends on how the test compressor parses (greedy, no optimal parsing), so it says how fast the decoder is on such streams only.
- **Quick mode.** `BAR_QUICK=1` (with `tools/bar-x64/run.sh 0.4` or either `bar.sh`) cuts each input to its first 1 MiB and uses one
  repeat, to prove the scripts run; its numbers mean nothing. Use a private `BAR_LOCK_FILE` and `BAR_OUT`.
- **Pass rule** as above. `run.sh 0.4 --list` shows the steps and their expected minutes.

## Managed .NET comparison

`bench/Managed/run.sh` compares our codecs with the fastest implementations written entirely in managed code (ZstdSharp, K4os, SharpCompress, SharpZipLib, DotNetZip, BrotliSharpLib and others, as packages used only from `bench/`). It is single-threaded (except one two-worker Zstandard row), memory to memory, best of three interleaved runs, and decodes each candidate's compressed output with our decoder to prove it is a real stream. Rows give speed and size of each candidate; the native tool is shown for information. It is not part of the pass rule above.

## Running

```
bench/run-bar.sh <codec[,codec]> <levels|all> <file[,file]> [--runs N] [--ref-seconds S] [--threads N]
```

The script builds the runner, takes the lock, checks the machine is quiet, runs, and writes
`bar-<timestamp>.csv` and `.md` into `$ONEARCHIVE_HOME/results`. The CSV is self-contained: each row repeats the
machine, CPU, core count, architecture, SDK, runtime, load figures, reference version, the reference method and
the file-system kind of the work directory.

Machines: the x64 bench host (Linux) and the arm64 bench host (macOS). Timings from different machines are
never compared with each other, only each machine's ours-versus-reference ratio.

## Adding a codec

Implement `IBarCodec` (compress and decompress memory to memory, the level list, whether a level is
multithreaded) and add it to `Registry`. If its reference is not yet in `References`, add the reference with
its command line. Nothing else changes.
