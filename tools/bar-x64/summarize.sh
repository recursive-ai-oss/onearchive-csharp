#!/usr/bin/env bash
# Turns the TSV files of a bar run into one markdown report with PASS or MISS per row.
#
#   summarize.sh <results dir>
#
# Rule: compressed size within 1% of the reference and our rate at least 0.87 of the reference's rate (compress and
# decompress each). Rates are medians where the benchmark gives them. Peak memory is shown for both sides.
set -uo pipefail
dir="$1"
SIZE_MAX=1.0; SPEED_MIN=0.87

echo "# Bar results"
echo
echo '```'; cat "$dir/machine.txt" 2>/dev/null; echo '```'
echo
echo "Load during the run (every minute, with the busiest processes): see \`load.log\`. Steps: see \`steps.log\`."
[ -s "$dir/skipped.txt" ] && { echo; echo "**Skipped or timed out:**"; sed 's/^/- /' "$dir/skipped.txt"; }

# Whole-file zstd rows from the encoder bench (also long-distance matching and jobs).
# cols: file level ref_bytes ours_bytes delta_pct ref_c ours_c ours_c_med ours_c_min c_time_ratio ref_d ours_d ours_d_med ours_d_min d_time_ratio ref_peak ours_peak load
for f in "$dir"/enc-*.tsv "$dir"/ldm.tsv "$dir"/jobs.tsv; do
  [ -s "$f" ] || continue
  echo; echo "## $(basename "$f" .tsv)"; echo
  grep '^#' "$f" | sed 's/^# */- /' | head -12
  echo
  echo "| file | level | ref bytes | our bytes | size | compress ours/ref rate, median (min-max) | decode ours/ref rate, median (min-max) | peak MB: ref / ours | verdict |"
  echo "|---|---|---|---|---|---|---|---|---|"
  awk -F'\t' -v smax="$SIZE_MAX" -v smin="$SPEED_MIN" '
    /^#/ { next }
    $1 == "file" { next }
    NF < 17 { next }
    {
      cr = ($6 > 0) ? $8 / $6 : 0; dn = ($11 + 0 > 0 && $13 + 0 > 0); dr = dn ? $13 / $11 : 0
      cb = ($6 > 0) ? $7 / $6 : 0; db = dn ? $12 / $11 : 0
      cm = ($6 > 0) ? $9 / $6 : 0; dm = dn ? $14 / $11 : 0
      ok = ($5 + 0 <= smax && cr >= smin && (!dn || dr >= smin)); if (!dn) nd++
      n++; if (ok) p++; else { m++; worst[m] = sprintf("%s L%s (size %+.2f%%, c %.2f, d %s)", $1, $2, $5, cr, dn ? sprintf("%.2f", dr) : "n/a") }
      dtxt = dn ? sprintf("%.2f (%.2f-%.2f)", dr, dm, db) : "n/a (tool decode rate not captured)"
      printf "| %s | %s | %s | %s | %+.2f%% | %.2f (%.2f-%.2f) | %s | %d / %d | %s%s |\n", $1, $2, $3, $4, $5, cr, cm, cb, dtxt, $16 / 1024, $17 / 1024, ok ? "PASS" : "MISS", dn ? "" : " (size and compress only)"
    }
    END { printf "\n%d rows: %d PASS, %d MISS (%d without a tool decode rate)\n", n, p, m, nd; for (i = 1; i <= m; i++) printf "- MISS %s\n", worst[i] }
  ' "$f"
done

# Decode of the tool's frames and of ours: three repeats per row.
# cols: frames file level compressed rep ref_MB/s ours_oneshot ours_stream ours/ref ref_peak ours_peak load
if [ -s "$dir/dec-tool.tsv" ]; then
  echo; echo "## dec-tool (decode speed, ours/reference, median of repeats)"; echo
  grep '^#' "$dir/dec-tool.tsv" | sed 's/^# */- /' | head -6
  echo
  echo "| frames from | file | level | frame bytes | one-shot ours/ref median (min-max) | stream ours/ref median | peak MB: ref / ours | verdict |"
  echo "|---|---|---|---|---|---|---|---|"
  awk -F'\t' -v smin="$SPEED_MIN" '
    function med(a, n,   i, j, t, b) { for (i = 1; i <= n; i++) b[i] = a[i]
      for (i = 2; i <= n; i++) { t = b[i]; for (j = i - 1; j >= 1 && b[j] > t; j--) b[j + 1] = b[j]; b[j + 1] = t }
      return b[int((n + 1) / 2)] }
    /^#/ { next }
    $1 == "frames" { next }
    NF < 12 { next }
    $6 !~ /^[0-9.]+$/ { bad[$1 "\t" $2 "\t" $3] = 1; next }   # the tool line had no usable decode rate
    {
      key = $1 "\t" $2 "\t" $3
      if (!(key in cnt)) { order[++k] = key; cnt[key] = 0; bytes[key] = $4 }
      c = ++cnt[key]; one[key, c] = ($6 > 0) ? $7 / $6 : 0; st[key, c] = ($6 > 0) ? $8 / $6 : 0
      rp[key] = $10; op[key] = $11
    }
    END {
      for (i = 1; i <= k; i++) {
        key = order[i]; n = cnt[key]
        for (j = 1; j <= n; j++) { a[j] = one[key, j]; s[j] = st[key, j] }
        m = med(a, n); ms = med(s, n); lo = a[1]; hi = a[1]
        for (j = 2; j <= n; j++) { if (a[j] < lo) lo = a[j]; if (a[j] > hi) hi = a[j] }
        split(key, kk, "\t"); ok = (m >= smin)
        np++; if (ok) pp++; else { mm++; worst[mm] = sprintf("%s %s L%s (%.2f)", kk[1], kk[2], kk[3], m) }
        printf "| %s | %s | %s | %s | %.2f (%.2f-%.2f) | %.2f | %d / %d | %s |\n", kk[1], kk[2], kk[3], bytes[key], m, lo, hi, ms, rp[key] / 1024, op[key] / 1024, ok ? "PASS" : "MISS"
      }
      printf "\n%d rows: %d PASS, %d MISS\n", np, pp, mm; for (i = 1; i <= mm; i++) printf "- MISS %s\n", worst[i]
      for (key in bad) { split(key, kk, "\t"); printf "- no result (tool decode rate not captured): %s %s L%s\n", kk[1], kk[2], kk[3] }
    }' "$dir/dec-tool.tsv"
fi

# Game payloads, compress with trained dictionaries.
# cols: game kind dict level payloads bytes ref_bytes ours_bytes delta_pct ref_payloads/s ours_payloads/s
if [ -s "$dir/game-enc.tsv" ]; then
  echo; echo "## game-enc (small payloads with trained dictionaries, compress)"; echo
  echo "| game | kind | dict | level | payloads | ref bytes | our bytes | size | speed ours/ref | verdict |"
  echo "|---|---|---|---|---|---|---|---|---|---|"
  awk -F'\t' -v smax="$SIZE_MAX" -v smin="$SPEED_MIN" '
    /^#/ { next }
    $1 == "game" { next }
    NF < 11 { next }
    {
      sp = ($10 > 0) ? $11 / $10 : 0; ok = ($9 + 0 <= smax && sp >= smin)
      n++; if (ok) p++; else { m++; worst[m] = sprintf("%s/%s dict %s L%s (size %+.2f%%, speed %.2f)", $1, $2, $3, $4, $9, sp) }
      printf "| %s | %s | %s | %s | %s | %s | %s | %+.2f%% | %.2f | %s |\n", $1, $2, $3, $4, $5, $7, $8, $9, sp, ok ? "PASS" : "MISS"
    }
    END { printf "\n%d rows: %d PASS, %d MISS\n", n, p, m; for (i = 1; i <= m; i++) printf "- MISS %s\n", worst[i] }' "$dir/game-enc.tsv"
fi
# cols: game kind dict level files bytes ref_payloads/s ours_payloads/s ours/ref
if [ -s "$dir/game-dec.tsv" ]; then
  echo; echo "## game-dec (small payloads with trained dictionaries, decode)"; echo
  echo "| game | kind | dict | level | files | ref payloads/s | our payloads/s | ours/ref | verdict |"
  echo "|---|---|---|---|---|---|---|---|---|"
  awk -F'\t' -v smin="$SPEED_MIN" '
    /^#/ { next }
    $1 == "game" { next }
    NF < 9 { next }
    {
      ok = ($9 + 0 >= smin)
      n++; if (ok) p++; else { m++; worst[m] = sprintf("%s/%s dict %s L%s (%.2f)", $1, $2, $3, $4, $9) }
      printf "| %s | %s | %s | %s | %s | %s | %s | %.2f | %s |\n", $1, $2, $3, $4, $5, $7, $8, $9, ok ? "PASS" : "MISS"
    }
    END { printf "\n%d rows: %d PASS, %d MISS\n", n, p, m; for (i = 1; i <= m; i++) printf "- MISS %s\n", worst[i] }' "$dir/game-dec.tsv"
fi

# Training time and memory: reported, no gate.
# cols: set dict files tool_s tool_mb ours1_s ours1_mb ours_s ours_mb
if [ -s "$dir/train.tsv" ]; then
  echo; echo "## train (time in s and peak MB; ours with one thread and with the default thread count)"; echo
  echo "| set | dict | files | tool s / MB | ours 1 thread s / MB | ours default s / MB | time ours1/tool | time ours/tool |"
  echo "|---|---|---|---|---|---|---|---|"
  awk -F'\t' '/^#/ { next } $1 == "set" { next } NF < 9 { next }
    { printf "| %s | %s | %s | %s / %s | %s / %s | %s / %s | %.2f | %.2f |\n", $1, $2, $3, $4, $5, $6, $7, $8, $9, ($4 > 0) ? $6 / $4 : 0, ($4 > 0) ? $8 / $4 : 0 }' "$dir/train.tsv"
fi

# LZ4 whole files.
# cols: file level ref_size ours_size size_ratio ref_comp ours_comp comp_ratio ref_dec ours_dec dec_ratio ref_peak ours_peak load
if [ -s "$dir/lz4.tsv" ]; then
  echo; echo "## lz4 (whole files, 4 MiB independent blocks, median of repeats)"; echo
  echo "| file | level | ref size | our size | size ratio | compress MB/s min/med/max: ref | ours | ours/ref | decode ref | ours | ours/ref | peak KB ref / ours | verdict |"
  echo "|---|---|---|---|---|---|---|---|---|---|---|---|---|"
  awk -F'\t' -v smin="$SPEED_MIN" '
    /^#/ { next }
    $1 == "file" { next }
    NF < 13 { next }
    {
      ok = ($5 + 0 <= 1.01 && $8 + 0 >= smin && $11 + 0 >= smin)
      n++; if (ok) p++; else { m++; worst[m] = sprintf("%s %s (size %.4f, c %.2f, d %.2f)", $1, $2, $5, $8, $11) }
      printf "| %s | %s | %s | %s | %.4f | %s | %s | %.2f | %s | %s | %.2f | %s / %s | %s |\n", $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, ok ? "PASS" : "MISS"
    }
    END { printf "\n%d rows: %d PASS, %d MISS\n", n, p, m; for (i = 1; i <= m; i++) printf "- MISS %s\n", worst[i] }' "$dir/lz4.tsv"
fi
# cols: set dict level payloads in ref_out ours_out size_ratio ref_ps ours_ps speed_ratio
if [ -s "$dir/lz4-game.tsv" ]; then
  echo; echo "## lz4-game (small payloads, dictionary '-' means none)"; echo
  grep '^#' "$dir/lz4-game.tsv" | sed 's/^# */- /'
  echo
  echo "| set | dict KiB | level | payloads | ref bytes | our bytes | size ratio | speed ours/ref | verdict |"
  echo "|---|---|---|---|---|---|---|---|---|"
  awk -F'\t' -v smin="$SPEED_MIN" '
    /^#/ { next }
    $1 == "set" { next }
    NF < 11 { next }
    {
      ok = ($8 + 0 <= 1.01 && $11 + 0 >= smin)
      n++; if (ok) p++; else { m++; worst[m] = sprintf("%s dict %s L%s (size %.4f, speed %.2f)", $1, $2, $3, $8, $11) }
      printf "| %s | %s | %s | %s | %s | %s | %.4f | %.2f | %s |\n", $1, $2, $3, $4, $6, $7, $8, $11, ok ? "PASS" : "MISS"
    }
    END { printf "\n%d rows: %d PASS, %d MISS\n", n, p, m; for (i = 1; i <= m; i++) printf "- MISS %s\n", worst[i] }' "$dir/lz4-game.tsv"
fi

# cols: game kind payload dict_kb dict_by codec level payloads ref_out ours_out size_ratio ref_c_best ref_c_med ours_c_best ours_c_med
# ours_c_min c_best_ratio c_med_ratio ref_d_best ref_d_med ours_d_best ours_d_med ours_d_min d_best_ratio d_med_ratio alloc_c alloc_d
if [ -s "$dir/small.tsv" ]; then
  echo; echo "## small (synthetic payloads of 256 B to 16 KiB, reused encoder and decoder, pinned zstd and lz4 libraries)"; echo
  grep '^#' "$dir/small.tsv" | sed 's/^# */- /'
  echo
  echo "| game | kind | payload B | dict KiB (by) | codec | level | size ratio | compress ours/ref best (median) | decompress ours/ref best (median) | alloc B/payload c / d | verdict |"
  echo "|---|---|---|---|---|---|---|---|---|---|---|"
  awk -F'\t' -v smin="$SPEED_MIN" '
    /^#/ { next }
    $1 == "game" { next }
    NF < 27 { next }
    {
      ok = ($11 + 0 <= 1.01 && $18 + 0 >= smin && $25 + 0 >= smin && $26 + 0 < 0.5 && $27 + 0 < 0.5)
      n++; if (ok) p++; else { m++; worst[m] = sprintf("%s/%s %sB dict %s %s L%s (size %.3f, compress %.2f, decompress %.2f, alloc %s/%s)", $1, $2, $3, $4, $6, $7, $11, $18, $25, $26, $27) }
      printf "| %s | %s | %s | %s (%s) | %s | %s | %.3f | %.2f (%.2f) | %.2f (%.2f) | %s / %s | %s |\n", $1, $2, $3, $4, $5, $6, $7, $11, $17, $18, $24, $25, $26, $27, ok ? "PASS" : "MISS"
    }
    END { printf "\n%d rows: %d PASS, %d MISS\n", n, p, m; for (i = 1; i <= m; i++) printf "- MISS %s\n", worst[i] }' "$dir/small.tsv"
fi

# Deflate compress rows (one-shot against libdeflate, streaming against zlib-ng).
# cols: file level ref_size ours_size size_ratio ref_MBps ours_MBps ours_med ours_min time_ratio ref_peak_KB ours_peak_KB verdict
for f in "$dir/defl-enc.tsv" "$dir/defl-stream.tsv"; do
  [ -s "$f" ] || continue
  name="$(basename "$f" .tsv)"
  echo; echo "## $name (compress; ours/ref rate is best-of-repeats, with median and slowest of ours)"; echo
  grep '^# \(cpu\|reference\)' "$f" | sed 's/^# */- /' | head -3
  echo
  echo "| file | level | ref bytes | our bytes | size | ours/ref rate best (median, min) | ref MB/s | peak MB: ref / ours | verdict |"
  echo "|---|---|---|---|---|---|---|---|---|"
  awk -F'\t' -v smin="$SPEED_MIN" '
    /^#/ { next }
    $1 == "file" { next }
    NF < 13 { next }
    {
      sz = $5 + 0; rr = ($6 > 0) ? $7 / $6 : 0; rm = ($6 > 0) ? $8 / $6 : 0; rn = ($6 > 0) ? $9 / $6 : 0
      ok = (sz <= 1.01 && rr >= smin)
      n++; if (ok) p++; else { m++; worst[m] = sprintf("%s L%s (size %+.2f%%, rate %.2f)", $1, $2, (sz - 1) * 100, rr) }
      printf "| %s | %s | %s | %s | %+.2f%% | %.2f (%.2f, %.2f) | %s | %d / %d | %s |\n", $1, $2, $3, $4, (sz - 1) * 100, rr, rm, rn, $6, $11 / 1024, $12 / 1024, ok ? "PASS" : "MISS"
    }
    END { printf "\n%d rows: %d PASS, %d MISS\n", n, p, m; for (i = 1; i <= m; i++) printf "- MISS %s\n", worst[i] }' "$f"
done

# Deflate decode: the reference decoder against ours, on the reference's output and on ours.
# cols: file reference level source ref_MBps ours_MBps ours_med ours_min time_ratio ref_peak_KB ours_peak_KB verdict
if [ -s "$dir/defl-dec.tsv" ]; then
  echo; echo "## defl-dec (decode speed, ours/reference rate; source = whose compressed bytes are decoded)"; echo
  echo "| file | reference | level | source | ours/ref rate best (median, min) | ref MB/s | peak MB: ref / ours | verdict |"
  echo "|---|---|---|---|---|---|---|---|"
  awk -F'\t' -v smin="$SPEED_MIN" '
    /^#/ { next }
    $1 == "file" { next }
    NF < 12 { next }
    {
      rr = ($5 > 0) ? $6 / $5 : 0; rm = ($5 > 0) ? $7 / $5 : 0; rn = ($5 > 0) ? $8 / $5 : 0
      ok = (rr >= smin)
      n++; if (ok) p++; else { m++; worst[m] = sprintf("%s vs %s L%s on %s output (%.2f)", $1, $2, $3, $4, rr) }
      printf "| %s | %s | %s | %s | %.2f (%.2f, %.2f) | %s | %d / %d | %s |\n", $1, $2, $3, $4, rr, rm, rn, $5, $10 / 1024, $11 / 1024, ok ? "PASS" : "MISS"
    }
    END { printf "\n%d rows: %d PASS, %d MISS\n", n, p, m; for (i = 1; i <= m; i++) printf "- MISS %s\n", worst[i] }' "$dir/defl-dec.tsv"
fi

# bzip2 decode against `bzip2 -dc` (process start-up subtracted).
# cols: file block_size compressed out_bytes ref_MBps ours_MBps ours_med ours_min time_ratio ref_peak_KB ours_peak_KB verdict
if [ -s "$dir/bzip2-dec.tsv" ]; then
  echo; echo "## bzip2-dec (decode speed, ours/reference rate)"; echo
  grep '^#' "$dir/bzip2-dec.tsv" | sed 's/^# */- /' | head -3
  echo
  echo "| file | block size | compressed bytes | ours/ref rate best (median, min) | ref MB/s | peak MB: ref / ours | verdict |"
  echo "|---|---|---|---|---|---|---|"
  awk -F'\t' -v smin="$SPEED_MIN" '
    /^#/ { next }
    $1 == "file" { next }
    NF < 12 { next }
    {
      rr = ($5 > 0) ? $6 / $5 : 0; rm = ($5 > 0) ? $7 / $5 : 0; rn = ($5 > 0) ? $8 / $5 : 0
      ok = (rr >= smin)
      n++; if (ok) p++; else { m++; worst[m] = sprintf("%s -%s (%.2f)", $1, $2, rr) }
      printf "| %s | -%s | %s | %.2f (%.2f, %.2f) | %s | %d / %d | %s |\n", $1, $2, $3, rr, rm, rn, $5, $10 / 1024, $11 / 1024, ok ? "PASS" : "MISS"
    }
    END { printf "\n%d rows: %d PASS, %d MISS\n", n, p, m; for (i = 1; i <= m; i++) printf "- MISS %s\n", worst[i] }' "$dir/bzip2-dec.tsv"
fi

# LZMA family compress rows: xz against `xz -T1`, lzip against `lzip`.
# cols: file codec level ref_size ours_size size_ratio ref_MBps ours_MBps ours_med ours_min time_ratio ref_peak_KB ours_peak_KB verdict
for f in "$dir"/xz-enc*.tsv "$dir"/lzip-enc*.tsv; do
  [ -s "$f" ] || continue
  name="$(basename "$f" .tsv)"
  echo; echo "## $name (compress; ours/ref rate is best-of-repeats, with median and slowest of ours)"; echo
  grep '^# \(cpu\|reference\)' "$f" | sed 's/^# */- /' | head -3
  echo
  echo "| file | level | ref bytes | our bytes | size | ours/ref rate best (median, min) | ref MB/s | peak MB: ref / ours | verdict |"
  echo "|---|---|---|---|---|---|---|---|---|"
  awk -F'\t' -v smin="$SPEED_MIN" '
    /^#/ { next }
    $1 == "file" { next }
    NF < 14 { next }
    {
      sz = $6 + 0; rr = ($7 > 0) ? $8 / $7 : 0; rm = ($7 > 0) ? $9 / $7 : 0; rn = ($7 > 0) ? $10 / $7 : 0
      ok = (sz <= 1.01 && rr >= smin)
      n++; if (ok) p++; else { m++; worst[m] = sprintf("%s %s L%s (size %+.2f%%, rate %.2f)", $1, $2, $3, (sz - 1) * 100, rr) }
      printf "| %s | %s %s | %s | %s | %+.2f%% | %.2f (%.2f, %.2f) | %s | %d / %d | %s |\n", $1, $2, $3, $4, $5, (sz - 1) * 100, rr, rm, rn, $7, $12 / 1024, $13 / 1024, ok ? "PASS" : "MISS"
    }
    END { printf "\n%d rows: %d PASS, %d MISS\n", n, p, m; for (i = 1; i <= m; i++) printf "- MISS %s\n", worst[i] }' "$f"
done

# LZMA family decode: the tool against ours, on the tool's output and on ours.
# cols: file codec level source ref_MBps ours_MBps ours_med ours_min time_ratio ref_peak_KB ours_peak_KB verdict
for f in "$dir/xz-dec.tsv" "$dir/lzip-dec.tsv"; do
  [ -s "$f" ] || continue
  name="$(basename "$f" .tsv)"
  echo; echo "## $name (decode speed, ours/tool rate; source = whose compressed bytes are decoded)"; echo
  echo "| file | codec | level | source | ours/ref rate best (median, min) | ref MB/s | peak MB: ref / ours | verdict |"
  echo "|---|---|---|---|---|---|---|---|"
  awk -F'\t' -v smin="$SPEED_MIN" '
    /^#/ { next }
    $1 == "file" { next }
    NF < 12 { next }
    {
      rr = ($5 > 0) ? $6 / $5 : 0; rm = ($5 > 0) ? $7 / $5 : 0; rn = ($5 > 0) ? $8 / $5 : 0
      ok = (rr >= smin)
      n++; if (ok) p++; else { m++; worst[m] = sprintf("%s %s L%s on %s output (%.2f)", $1, $2, $3, $4, rr) }
      printf "| %s | %s | %s | %s | %.2f (%.2f, %.2f) | %s | %d / %d | %s |\n", $1, $2, $3, $4, rr, rm, rn, $5, $10 / 1024, $11 / 1024, ok ? "PASS" : "MISS"
    }
    END { printf "\n%d rows: %d PASS, %d MISS\n", n, p, m; for (i = 1; i <= m; i++) printf "- MISS %s\n", worst[i] }' "$f"
done

# 7z writer sizes against 7-Zip (sizes only).
# cols: file level ref_size ours_size size_ratio verdict
if [ -s "$dir/sevenz.tsv" ]; then
  echo; echo "## sevenz (size of a one-file 7z archive, ours / 7-Zip at -mx=N -mmt=1)"; echo
  grep '^# cpu' "$dir/sevenz.tsv" | sed 's/^# */- /' | head -1
  echo
  echo "| file | level | 7-Zip bytes | our bytes | size | verdict |"
  echo "|---|---|---|---|---|---|"
  awk -F'\t' '
    /^#/ { next }
    $1 == "file" { next }
    NF < 6 { next }
    {
      sz = $5 + 0; ok = (sz <= 1.01)
      n++; if (ok) p++; else { m++; worst[m] = sprintf("%s mx=%s (size %+.2f%%)", $1, $2, (sz - 1) * 100) }
      printf "| %s | %s | %s | %s | %+.2f%% | %s |\n", $1, $2, $3, $4, (sz - 1) * 100, ok ? "PASS" : "MISS"
    }
    END { printf "\n%d rows: %d PASS, %d MISS\n", n, p, m; for (i = 1; i <= m; i++) printf "- MISS %s\n", worst[i] }' "$dir/sevenz.tsv"
fi

if [ -d "$dir/bar-runner" ]; then
  echo; echo "## bar-runner"; echo; ls "$dir/bar-runner" | sed 's/^/- /'
fi
