# Zstandard decoder speed

Method: memory to memory, warm, best pass over 4 s, one thread. The reference is `zstd -b<L> -e<L> -i4` (decompression MB/s)
on the same file. Frames come from the tool (`--no-check`, as `zstd -b` makes them) and from this library's encoder. Our side is
`tools/zstd-decode-bench` (`run.sh` for whole files, `game.sh` for small payloads with trained dictionaries; the dictionary is
trained by the tool on the first 5000 payloads of one game-corpus seed and the payloads decoded come from another). Each row
repeats three times. The pass line is ours ≥ 0.87 × the reference's rate (time ≤ 1.15×).

## Apple M2 (8 cores), zstd 1.5.7, .NET 10.0.402, load 4–11 during the whole-file rows

Ours ÷ reference decode rate, whole files, tool-made frames (three repetitions, range):

| file | L1 | L3 | L9 | L19 |
|---|---|---|---|---|
| silesia.tar | 0.87 | 0.90 | 0.91–0.92 | 0.84–0.90 |
| enwik8 | 0.87–0.89 | 0.93–0.94 | 0.86–0.97 | 0.90–0.92 |

Frames made by this library's encoder (L1–L9) decode at 0.82–0.95 of the tool's rate on the same file; the spread between
repetitions is as large as the difference, so those rows are not a separate result. Peak memory: ours 331 MB on silesia.tar
and 190 MB on enwik8, against 640–980 MB and 309–577 MB for the tool's benchmark mode. NativeAOT decode on silesia.tar
(oneshot, MB/s): L1 1470–1545, L3 1406–1480, L9 1680–1686, L19 1413–1440, close to the JIT numbers.

Game payloads, dictionary 16 KiB and 112 KiB, load 3–4, ours ÷ reference payload rate (two passes):

| game | frames L3 | snapshots L9 | saves L19 |
|---|---|---|---|
| lanterns | 1.74–1.79 | 1.12–1.14 | 0.93–0.97 |
| bricks | 1.89–2.01 | 1.01–1.05 | 1.00–1.02 |
| solitaire | 1.08–1.10 | 0.88–0.89 | 0.90–0.91 |

All game rows are at or above 0.87; the closest are solitaire snapshots (0.88-0.89) and solitaire saves (0.90). Whole-file rows at or near the limit: silesia.tar L1 (0.87) and one L19 repetition (0.84), enwik8 L9 (one 0.86); these were taken under load and need the quiet-window rerun.

## Pending

x64 rows on the benchmark worker and a second Apple M2 pass in a quiet window (the whole-file rows above ran at load 4–11).
