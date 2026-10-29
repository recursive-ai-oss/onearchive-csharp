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
   - *FastCover* has `2^b` ids with `b = clamp(floor(log2(N)) + 3, 12, HashBits)` for `N` total sample bytes (a table much larger
     than the data only costs time; `b` comes from the whole sample set, whichever part of it is trained on),
     `id = (key * 0x9E3779B97F4A7C15 mod 2^64) >> (64 - b)` (collisions share an id), and only positions whose offset within their
     sample is a multiple of `Acceleration` start a d-mer.
2. **Frequency.** The frequency of an id is the number of samples that contain a d-mer with that id (a sample counts once). It is
   counted per *group* of samples (see "Held-out parts") and saturates at 65535 in each group; the frequency over a part of the
   samples is the sum over its groups, capped at 65535 (so a d-mer found in 65535 or more samples counts as exactly that many).
3. **Weight.** A frequency below 2 has weight 0 (a d-mer in one sample only cannot help any other). Otherwise `Frequency` score:
   the frequency. `SquareRoot` score: `isqrt(frequency << 16)`, the floor of the integer square root (the paper's ℓ0.5 norm up to
   the final monotone square, with 8 fractional bits). Weights are 16 bits.
4. **Epochs.** `E = clamp(ceil(D / k), 1, max(1, N / k))` where `N` is the number of bytes trained on (all the samples, or all
   but a held-out part); epoch `e` covers positions `[e * (N / E), (e + 1) * (N / E))` of those bytes laid end to end, the last
   one up to `N`.
5. **Segment.** In epoch `e` with room `R` left of the budget, the window is `w = min(k, R, epoch length)`; epochs with `w < d` are
   skipped. The score of a window placement is the sum of the weights of the *distinct* ids of the d-mers starting inside it
   (positions `s .. s + w - d`). The best-scoring placement wins; among equal scores the earliest. A best score of 0 adds nothing.
   The window is `w` bytes from the winning start. The weights of all ids of its d-mers are then set to 0 for later epochs.
6. **Order.** The chosen segments are written by increasing score, ties by increasing epoch, so the most useful content ends up
   last, nearest the data it serves and with the smallest offsets. If nothing scored, the content is the first
   `min(D, N)` bytes of the samples.

## Parameter search

With `Optimize` (the default), `ZstdCoverTrainer.Optimize` looks for the combination of `DmerSizeCandidates`, `ScoreCandidates` and
`SegmentSizeCandidates` (distinct values, `k >= d`) with the smallest total. Candidates are numbered in the order d ascending, then
score (`SquareRoot` before `Frequency`, when both are given), then k ascending; a *variant* is one pair of d and score and its
candidates differ in k. A candidate is scored by the total size of the held-out samples compressed at `ScoringLevel` by this
library's encoder with the dictionary the candidate's content makes (a raw-content dictionary: the content only, no entropy tables,
so that only the content is judged); a held-out sample is a frame of its own. **The smallest total wins, and the first of equal
totals** (lowest candidate number). Scoring runs on `MaxDegreeOfParallelism` threads (0: the processor count, at most 8); each total
is independent of the others, so the choice does not depend on scheduling.

- **`SearchMode.Exhaustive`** scores every candidate.
- **`SearchMode.Guided`** (the default) scores a subset, chosen from totals only (the same totals whatever the thread count):
  1. If there are more than 6 candidates and the samples exceed 4 MiB, only the first variant is searched (below).
  2. The *first variant* is the one with the largest d up to 8 (else the first variant). Its segment sizes `k_0 < ... < k_(m-1)`
     are scored at positions `(m-1)/6`, `3(m-1)/6` and `5(m-1)/6` (integer division) of the list.
  3. From the best candidate so far, its two neighbours in the list (previous and next k) are scored, unless already scored. If a
     neighbour beat the best by more than a quarter of a percent (`(best - new) * 400 >= best`), this repeats from it; otherwise
     it stops. The smallest total scored so far stays the best.
  4. Each other variant is scored at the segment size nearest the best one (the smaller on a tie).
  5. With 6 or fewer candidates all are scored.
- **Held-out parts.** `TrainingPercent` (default 75) gives `F = ceil(100 / (100 - TrainingPercent))` folds (4 for 75; 1 and
  train = test when 100). The samples are cut into blocks of `max(1, count / 16)` consecutive samples; block `j` belongs to *group*
  `j mod F`, and fold `f` holds out group `f` and trains on the rest. Blocks, not single samples, are held out because neighbouring
  samples (ticks of one game, pieces of one file) are alike. If a group would be empty or the other groups hold fewer than 8 bytes
  (very few samples), there is one fold that trains and is scored on everything. The training samples of a fold are a few stretches
  of the original data and are never copied; the d-mers are counted once for all folds.
- **Folds scored.** Sample sets up to 1 MiB score the first `min(F, 4)` folds and add their totals; up to 4 MiB the first
  `min(F, 2)`; larger ones the first fold only. A fold is scored on at most `MaxScoringSamples` (default 2000) of its held-out samples
  and 1 MiB of them: every n-th, where `n = max(ceil(samples / MaxScoringSamples), ceil(bytes / 1 MiB))`.

Defaults: `d` in {6, 8}; `k` in {32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 1536, 2048}; the `SquareRoot` score only (26
combinations; a guided search scores about 6 to 9 of them). Without `Optimize`, `SegmentSize` (256), `DmerSize` (8) and `Score`
(`SquareRoot`) are used.

## The dictionary file

`ZstdDictionaryBuilder.Build(content, samples, id)` writes: magic `0xEC30A437`, the ID, the Huffman table for literals, the FSE
tables for offsets, match lengths and literal lengths, three repeat offsets (1, 4, 8) and the content (RFC 8878 §5). Total size
is at most `DictionarySize`: content for the held-out parts is chosen with 350 bytes reserved for the tables, the tables are
measured, and the content from all the samples is then chosen with the real size of the tables (a whole-set choice of the same
length as the held-out content is kept as it is); if the final tables are bigger, the oldest (first) content bytes are dropped.

- **Statistics.** The tables are measured on held-out frames. With one fold (see "Held-out parts"), the content chosen from all
  the samples is used and every sample is parsed. Otherwise, for each fold `f` of the first `min(F, 4)` (all of them when the
  samples total at most 24 MiB, else the first only), content of the reserved size is chosen from the samples outside the fold with
  the final `k`, `d` and score, and the fold's samples (every n-th when more than 20000 or 8 MiB, as for scoring) are parsed
  against it. The histograms are added. Measuring against the final content itself would parse samples that the content was cut
  from, so almost every sample would be one long match and the tables would describe nothing like new data; the effect grows as
  the dictionary approaches the size of the samples. Each sample is parsed by the level-3 fast match finder with the content as
  history (one sample is one frame; blocks of 128 KiB). Every sequence is counted in the literal length code, match length code and
  offset code histograms (offset *value*: 1 to 3 for repeat offsets as the decoder resolves them, otherwise offset + 3; code =
  floor(log2 value)), starting each sample from the repeat offsets 1, 4, 8. All literals, including those after the last match, go
  into the byte histogram. The header is as large as the first fold's content and these histograms need.
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

## Time and memory

The d-mers of the samples are counted once per d-mer size, in one pass that keeps the counts of each group apart; a fold's
weights are the sum of the other groups' counts, so holding out a part costs no second pass. Working arrays come from a pool and are
reused by the next candidate; none of this affects the output, which does not depend on what the arrays held before.
`tools/zstd-train-bench/timing.sh` measures time and peak memory against `zstd --train`, and `bench/Managed` against the fastest
managed implementation; method and figures are in the changelog and `docs/bench-method.md`.

## Measured against `zstd --train`

`tools/zstd-train-bench/quality.sh` compresses held-back payloads with zstd's own compressor and a dictionary from each trainer, so
only the dictionary differs. On game snapshot data (trained on one run, judged on another) the total over all kinds and dictionary
sizes is within 0.3% of `zstd --train` (smaller), with most rows within 1%.
Known gaps, left for later tuning: sample sets of a few hundred small payloads against a dictionary larger than the data (the
reference writes a dictionary smaller than the data; ours keeps nearly all of it), and heterogeneous bulk data cut into 4 KiB
chunks with dictionaries of 64 KiB and up (about 2% larger totals; the entropy tables are not the cause, the chosen content is).
