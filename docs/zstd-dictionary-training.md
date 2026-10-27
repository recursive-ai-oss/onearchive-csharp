# Zstandard dictionary training

`ZstdDictionary.Train(samples, options)` turns a set of sample payloads into a dictionary in the Zstandard dictionary format
(RFC 8878 §5). The pieces are public so that they can be composed: `ZstdCoverTrainer` (content selection, parameter search, and the
whole pipeline), `ZstdDictionaryBuilder` (content and samples to a complete dictionary) and `ZstdDictionaryTrainingOptions`.

The output depends only on the samples and the options: not on the platform, vector width, runtime, or the number of threads the
parameter search uses. All scoring is integer arithmetic. This page is normative: a port must produce the same bytes from it.

## Samples

- Empty samples are dropped. The samples are laid end to end in the order given.
- If their total exceeds `MaxTrainingBytes` (default 64 MiB), every `n`-th sample is kept (sample indexes `0, n, 2n, ...`) where
  `n = ceil(total / MaxTrainingBytes)`, and what is still over the limit is cut off at the limit (the sample that crosses it is
  truncated, later ones are dropped).
- Fewer than 8 bytes in total is a `CompressionDictionaryException`.

## Content selection (COVER)

Source: Liao, Petri, Moffat and Wirth, "Effective Construction of Relative Lempel-Ziv Dictionaries", WWW 2016, §3 (local
segments chosen per epoch by the frequencies of their k-mers, covered k-mers set to zero, ℓ0.5 scoring). Parameters: `k`
(segment size), `d` (d-mer size, the paper's k-mer), `D` (the content budget).

1. **d-mers.** A d-mer is the `d` bytes starting at a position, wholly inside one sample. Its key is the little-endian value of its
   bytes (`d <= 8`), or for `d` of 9 to 16 the mix `(a * 0xC2B2AE3D27D4EB4F) xor rotl(b * 0x165667B19E3779F9, 31)` of the first and
   last eight bytes read as little-endian 64-bit values (`a`, `b`), computed modulo 2^64.
   - *Cover* gives each distinct key its own id (exact).
   - *FastCover* has `2^HashBits` ids, `id = (key * 0x9E3779B97F4A7C15 mod 2^64) >> (64 - HashBits)` (collisions share an id), and
     only positions whose offset within their sample is a multiple of `Acceleration` start a d-mer.
2. **Frequency.** The frequency of an id is the number of samples that contain a d-mer with that id (a sample counts once).
3. **Weight.** `Frequency` score: the frequency. `SquareRoot` score: `isqrt(frequency << 16)`, the floor of the integer square
   root (the paper's ℓ0.5 norm up to the final monotone square, with 8 fractional bits).
4. **Epochs.** `E = clamp(ceil(D / k), 1, max(1, N / k))` where `N` is the total sample bytes; epoch `e` covers positions
   `[e * (N / E), (e + 1) * (N / E))`, the last one up to `N`.
5. **Segment.** In epoch `e` with room `R` left of the budget, the window is `w = min(k, R, epoch length)`; epochs with `w < d` are
   skipped. The score of a window placement is the sum of the weights of the *distinct* ids of the d-mers starting inside it
   (positions `s .. s + w - d`). The best-scoring placement wins; among equal scores the earliest. A best score of 0 adds nothing.
   The window is `w` bytes from the winning start. The weights of all ids of its d-mers are then set to 0 for later epochs.
6. **Order.** The chosen segments are written by increasing score, ties by increasing epoch, so the most useful content ends up
   last, nearest the data it serves and with the smallest offsets. If nothing scored, the content is the first
   `min(D, N)` bytes of the samples.

## Parameter search

With `Optimize` (the default), `ZstdCoverTrainer.Optimize` tries every combination of `DmerSizeCandidates`, `ScoreCandidates` and
`SegmentSizeCandidates` (distinct values, `k >= d`), in the order d ascending, then score (`SquareRoot` before `Frequency`), then k
ascending. A combination is scored by the total size of the held-out samples compressed with the dictionary it makes at
`ScoringLevel` by this library's encoder; the **smallest total wins, and the first of equal totals** (the rule above). Scoring runs
on `MaxDegreeOfParallelism` threads (0: the processor count, at most 8); each combination's total is independent of the others,
so the choice does not depend on scheduling.

- **Held-out samples.** `TrainingPercent` (default 75) gives `F = ceil(100 / (100 - TrainingPercent))` folds (4 for 75; 1 and
  train = test when 100). The samples are cut into blocks of `max(1, count / 64)` consecutive samples; block `j` belongs to fold
  `j mod F`. Fold `f` is scored by training on the samples outside it and compressing those inside it (at most
  `MaxScoringSamples`, default 2000, thinned by taking every n-th). Sample sets up to 24 MiB add up the totals of the first
  `min(F, 4)` folds; larger ones use fold 0 only. Blocks, not single samples, are held out because neighbouring samples (ticks of one
  game, pieces of one file) are alike.
- **Candidate dictionaries** are built exactly as the final one, with the tables measured on at most 2000 training samples
  and 4 MiB (every n-th).

Defaults: `d` in {6, 8}; `k` in {32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 1536, 2048}; both scores. Without `Optimize`,
`SegmentSize` (256), `DmerSize` (8) and `Score` (`SquareRoot`) are used.

## The dictionary file

`ZstdDictionaryBuilder.Build(content, samples, id)` writes: magic `0xEC30A437`, the ID, the Huffman table for literals, the FSE
tables for offsets, match lengths and literal lengths, three repeat offsets (1, 4, 8) and the content (RFC 8878 §5). Total size
is at most `DictionarySize`: the content is chosen with 350 bytes reserved for the tables, then again with the real size of the
tables; if the final tables are bigger, the oldest (first) content bytes are dropped.

- **Statistics.** Each sample is parsed by the level-3 fast match finder with the content as history (one sample is one frame;
  blocks of 128 KiB). Every sequence is counted in the literal length code, match length code and offset code histograms
  (offset *value*: 1 to 3 for repeat offsets as the decoder resolves them, otherwise offset + 3; code = floor(log2 value)), starting
  each sample from the repeat offsets 1, 4, 8. All literals, including those after the last match, go into the byte histogram.
- **Huffman.** Every byte gets a code: weight counts are `max(count, 1)`, built with the library's length-limited builder (11
  bits). If that cannot be described (no samples; a perfectly even spread) a text-like prior replaces the counts: space 64,
  `a`-`z` 32, `A`-`Z` and digits 16, other printable 8, control bytes 2, others 1.
- **FSE.** Counts are `max(count, 1)` for every symbol of the range: literal lengths 0..35, match lengths 0..52, offsets 0..max(17,
  floor(log2(content + 3))) (at most 31). The accuracy log is `OptimalAccuracyLog` (max 9; offsets 8), counts are normalized
  (counts at or below `total >> accuracyLog` become probability -1). With no sequences at all the format's default
  distributions are written.
- **ID.** The given `DictionaryId`, or if 0 the xxHash64 (seed 0) of the whole dictionary written with ID 0, folded as
  `32768 + hash mod (2^31 - 32768)`. IDs 1 to 32767 and 2^31 and up are refused (RFC 8878 §5).
- Passing a whole dictionary (magic first) as the content keeps its content and ID and replaces its tables: this is how
  dictionaries made by another tool get tables measured on your samples.

## Limits and errors

`DictionarySize` 256 to 2^30; `d` 2 to 16 and `k >= d` (at most 16 MiB); `HashBits` 8 to 24; `Acceleration` 1 to 10;
`TrainingPercent` 1 to 100; `ScoringLevel` 1 to 22 (a level above the encoder's highest is scored at its highest). Bad options throw `ArgumentException`; samples that cannot give a dictionary throw
`CompressionDictionaryException`; cancellation throws `OperationCanceledException`.

## Measured against `zstd --train`

`tools/zstd-train-bench/quality.sh` compresses held-back payloads with zstd's own compressor and a dictionary from each trainer, so
only the dictionary differs; `timing.sh` measures training time and peak memory. On game snapshot data (trained on one run, judged
on another) the total over all kinds and dictionary sizes is within 0.3% of `zstd --train` (smaller), with most rows within 1%.
Known gaps, left for later tuning: sample sets of a few hundred small payloads against a dictionary larger than the data (the
reference writes a dictionary smaller than the data; ours keeps nearly all of it), and heterogeneous bulk data cut into 4 KiB
chunks with dictionaries of 64 KiB and up (about 2% larger totals; the entropy tables are not the cause, the chosen content is).
