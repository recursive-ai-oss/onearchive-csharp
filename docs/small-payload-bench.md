# Small-payload rows

How the per-message path is measured: payloads of a few hundred bytes to a few KiB, each compressed or decompressed on
its own with a trained dictionary, through one reused encoder or decoder. Two numbers matter: payloads per second, and
bytes allocated per payload (which must be zero).

## Inputs

Synthetic payloads are cut from the game snapshot data (see the corpus notes): for a game and a kind (`frames` or
`snapshots`) the bench set's payloads are concatenated in manifest order and cut into pieces of exactly 256, 1,024, 4,096 or
16,384 bytes (2,000 pieces unless the data runs out). Dictionaries of 16 and 112 KiB come from the training set:

- zstd: `zstd --train --maxdict=N` on the first 5,000 payloads of the training set (the reference's trainer). One extra row
  per case set uses a dictionary of 16 KiB trained by this library's trainer on the first 2,000 payloads.
- LZ4: a raw dictionary of evenly spaced training payloads concatenated (LZ4 uses the last 64 KiB of a dictionary).

## Reference

The reference is the pinned zstd and lz4 *libraries* (the install prefixes under `tools/opt/` named in `tools/pins.tsv`),
driven by `tools/small-bench/refsmall.c` through their documented APIs: `ZSTD_compress_usingCDict` and
`ZSTD_decompress_usingDDict` with one context each and the dictionary prepared once; `LZ4F_compressFrame_usingCDict` and
`LZ4F_decompress_usingDict` with one context each. The program is compiled against the pinned prefix, never the system
library. It writes the frames it produced; our side decodes exactly those frames, so both decoders do the same work, and our
output is only checked to round-trip through our own decoder. No checksums and no content size in the LZ4 frames; zstd
frames carry the content size, as the library writes them.

## Method

Each side makes repeated passes over the whole payload set after a warm-up pass; a pass is timed as a whole and a payload
rate is payloads divided by pass time. Rows report the best, the median and the worst pass. A row passes when the output is
at most 1.01 times the reference's size, the median compress and decompress rates are each at least 0.87 of the
reference's, and the reused paths allocate nothing (under half a byte per payload; the counter is the thread's allocated
bytes over all timed passes). Runs go under the bench lock on the bench hosts, with load recorded before and after; the row
set and the tool versions are written at the top of the output.

LZ4 levels: `-8` and `-1` are the fast levels with acceleration 9 and 2 (`--fast=8`, `--fast=1`), `1` and `3` the default
fast level and the first high-compression level.

## Running

```
BENCH_DLL=<SmallBench.dll> tools/small-bench/run.sh [--games ...] [--kinds ...] [--sizes ...] [--dicts ...] [--ours-dict]
```

The bar driver runs it as the `small` step of chunk 3 and summarizes it as PASS or MISS per row.

## Browser-wasm

`smoke/wasm` run with `small` measures the same path under Node, ours only, on generated record-shaped payloads (the host
has no files and Node has no reference tool): `node smoke/wasm/run.mjs <wwwroot> small [seconds]`. A reported number, not a
gate. Build it with `dotnet publish smoke/wasm -c Release -p:RunAOTCompilation=true` for ahead-of-time compiled code; a plain
`dotnet build` runs the interpreter, about eight times slower.
