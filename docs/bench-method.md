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
`rar`/`unrar` binaries are installed for interoperability tests and as references for later codecs.
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
- **game/**: reserved. Game payload rows (ratio against dictionary size per payload kind) are added when the
  game corpus exists.

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
