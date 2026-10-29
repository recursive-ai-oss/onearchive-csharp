# PPMd levels as 7-Zip applies them

A black-box experiment: `run.sh` runs the `7zz` command-line tool (7-Zip 26.05, linux-x64 release binary, SHA-256
`d32657a7a0e4cdc0a8aa57cafe474746944d76e861e753e9c902b9c9356fa886`) at `-mx=1..9` for a 7z archive (`-m0=PPMd`, variant H) and a zip
archive (`-mm=PPMd`, method 98, variant I), always with `-mmt=1`, and records the order and model memory it chose. `results.tsv` is the
output (213 measured rows). Nothing about 7-Zip's source was read; the numbers come only from its output.

Command used:

    run.sh 7zz silesia.tar workdir results.tsv        # silesia.tar: 211957760 bytes, SHA-256 0b5ec70c...724d (the pinned corpus file)

How the numbers are read:

- 7z: `7zz l -slt` prints `Method = PPMD:o<order>:mem<log2 of bytes>` for the entry.
- zip: `7zz l -slt` prints only `PPMd`, so the two-byte header at the start of the method-98 data is decoded (APPNOTE 5.10.4:
  order−1 in bits 0–3, MiB−1 in bits 4–11, restore method in bits 12–15).
- Inputs: the whole 212 MB file (above the reduction threshold at every level), and `head -c N` slices of it at N = 2^j and 2^j+1 for
  j in 12, 14, 16, 18..24 (levels 1, 5, 9), at 1, 100, 4096, 4097, 1 MiB and 1 MiB+1 (all levels), to bracket every threshold.

## Pinned level table (large input)

| Level | 7z: order, memory | zip: order, memory, restore |
|---|---|---|
| 1 | 4, 1 MiB | 4, 1 MiB, restart (0) |
| 2 | 4, 2 MiB | 5, 2 MiB, restart |
| 3 | 5, 4 MiB | 6, 4 MiB, restart |
| 4 | 5, 8 MiB | 7, 8 MiB, restart |
| 5 | 6, 16 MiB | 8, 16 MiB, restart |
| 6 | 8, 32 MiB | 9, 32 MiB, restart |
| 7 | 16, 64 MiB | 10, 64 MiB, cut-off (1) |
| 8 | 24, **128 MiB** | 11, **128 MiB**, cut-off |
| 9 | 32, **256 MiB** | 12, **256 MiB**, cut-off |

Only the memory of levels 8 and 9 differs from the table that was in `docs/ppmd.md` (64 MiB for both, measured on inputs small enough
to be reduced). The order, and for zip the restore method, did not change. Levels above 9 were not run (the tool takes 0–9).

## Pinned reduction rule

For an input of known size S, the recorded memory is `min(M, 2^k)`, where M is the level's memory and 2^k is the smallest power of two,
k ≥ 16, with `16 × S ≤ 2^k`. The boundary is inclusive: S = 4096 gives 64 KiB, S = 4097 gives 128 KiB; S = 8388608 gives 128 MiB,
S = 8388609 gives 256 MiB. Order and restore method never depend on the size. zip rounds the result up to a whole MiB (minimum 1 MiB).
All 213 rows satisfy the rule (checked by a script over `results.tsv`). The 7z archive of a one-byte file records 64 KiB.

A zip entry of 1 byte is stored (method 0) at every level, so there is no PPMd header to read (rows marked `stored`).

## Several files (one spot check, not in `run.sh`)

Two files of 3000 and 5000 bytes, 7z, `-m0=PPMd -mx=9 -mmt=1`: both `-ms=on` and `-ms=off` record `PPMD:o32:mem17` (128 KiB) for
both files, so the reduction follows the total size of everything being compressed (8000 bytes: 16 × 8000 ≤ 2^17), not the size of each
file or folder. The 7z writer here reduces by the size of the folder it is writing, and cannot know the total when it streams, so for
several files its recorded memory can be larger than 7-Zip's. The model contents only differ if memory runs out, so the bytes of a
large-memory stream are the same either way.
