#!/usr/bin/env bash
# Small-payload rows: zstd (levels 1 and 3) and LZ4 frames (fast acceleration levels, 1, 3) on payloads of about 256 B, 1 KiB,
# 4 KiB and 16 KiB cut from the game snapshot data (seed 2: frames and snapshots) with trained dictionaries of 16 and 112 KiB,
# one payload at a time through a reused encoder or decoder. The reference is the pinned zstd and lz4 libraries driven by
# refsmall.c through their library APIs (prepared CDict/DDict, reused contexts), built against the pinned install prefixes
# (not the system libraries, which may be older). Both sides decode the reference's frames. Rates are payloads per second.
#
#   run.sh [--games "lanterns bricks solitaire"] [--kinds "frames snapshots"] [--sizes "256 1024 4096 16384"]
#          [--dicts "16 112"] [--zstd-levels "1 3"] [--lz4-levels "-8 -1 1 3"] [--count 2000] [--seconds 3] [--ours-dict]
#
# zstd dictionaries come from `zstd --train` on the first 5000 payloads of seed 1 (--ours-dict adds one row trained by our
# trainer for the first game and kind, 16 KiB). LZ4 dictionaries are raw: evenly spaced seed 1 payloads concatenated (LZ4 uses
# the last 64 KiB). Prints one TSV row per case: ratio columns are ours / reference, so for rates 1.0 is parity and the bar is
# 0.87; size_ratio is ours / reference output and the bar is 1.01. alloc_* are bytes allocated per payload by our reused path,
# which must be 0. A timing run: take the bench lock first.
# Environment: ONEARCHIVE_HOME (default ~/onearchive; tools/opt/zstd-* and lz4-* prefixes and corpora/game), BENCH_DLL
# (SmallBench.dll), TRAIN_DLL (ZstdTrainBench.dll, for --ours-dict), ZSTD_PREFIX, LZ4_PREFIX, ONEARCHIVE_BENCH_TMP, DOTNET.
set -euo pipefail
export LC_ALL=C
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
home="${ONEARCHIVE_HOME:-$HOME/onearchive}"
zprefix="${ZSTD_PREFIX:-$(ls -d "$home"/tools/opt/zstd-* | head -1)}"
lprefix="${LZ4_PREFIX:-$(ls -d "$home"/tools/opt/lz4-* | head -1)}"
work="${ONEARCHIVE_BENCH_TMP:-${TMPDIR:-/tmp}/small-bench}"
dll="${BENCH_DLL:?set BENCH_DLL to SmallBench.dll}"
dotnet="${DOTNET:-dotnet}"
games="lanterns bricks solitaire"; kinds="frames snapshots"; sizes="256 1024 4096 16384"; dicts="16 112"
zlevels="1 3"; llevels="-8 -1 1 3"; count=2000; seconds=3; oursdict=0
while [ $# -gt 0 ]; do
  case "$1" in
    --games) games="$2"; shift 2 ;;
    --kinds) kinds="$2"; shift 2 ;;
    --sizes) sizes="$2"; shift 2 ;;
    --dicts) dicts="$2"; shift 2 ;;
    --zstd-levels) zlevels="$2"; shift 2 ;;
    --lz4-levels) llevels="$2"; shift 2 ;;
    --count) count="$2"; shift 2 ;;
    --seconds) seconds="$2"; shift 2 ;;
    --ours-dict) oursdict=1; shift ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
corpora="$home/corpora/game"
mkdir -p "$work"
load() { if [ -r /proc/loadavg ]; then cut -d' ' -f1-3 /proc/loadavg; else uptime | sed 's/.*load averages*: //'; fi; }

cc -O2 "$here/refsmall.c" -I"$zprefix/include" -I"$lprefix/include" -L"$zprefix/lib" -L"$lprefix/lib" -lzstd -llz4 \
  -Wl,-rpath,"$zprefix/lib" -Wl,-rpath,"$lprefix/lib" -o "$work/refsmall"
zstd_bin="$zprefix/bin/zstd"
echo "# zstd $("$zstd_bin" --version | head -1 | sed 's/.*v\([0-9.]*\).*/\1/') ($zprefix), lz4 library from $lprefix; $(nproc 2>/dev/null || sysctl -n hw.ncpu) cpus; load before: $(load)"

field() { tr '\t' '\n' <<< "$1" | sed -n "s/^$2=//p"; }
ratio() { awk -v a="$1" -v b="$2" 'BEGIN { if (b > 0) printf "%.3f", a / b; else print "NA" }'; }

printf 'game\tkind\tpayload\tdict_kb\tdict_by\tcodec\tlevel\tpayloads\tref_out\tours_out\tsize_ratio\tref_c_best\tref_c_med\tours_c_best\tours_c_med\tours_c_min\tc_best_ratio\tc_med_ratio\tref_d_best\tref_d_med\tours_d_best\tours_d_med\tours_d_min\td_best_ratio\td_med_ratio\talloc_c\talloc_d\n'
row() { # codec level dict dict_by game kind size dkb
  local codec="$1" L="$2" dict="$3" by="$4" g="$5" k="$6" s="$7" dk="$8" ref ours
  ref=$("$work/refsmall" "$codec" "$L" "$dict" "$work/pay.bin" "$work/ref.frames" "$seconds")
  ours=$("$dotnet" "$dll" run "$codec" "$L" "$dict" "$work/pay.bin" "$work/ref.frames" --seconds "$seconds")
  local ro oo; ro=$(field "$ref" out); oo=$(field "$ours" out)
  local rcb rcm rdb rdm ocb ocm ocn odb odm odn
  rcb=$(field "$ref" compress_best); rcm=$(field "$ref" compress_median); rdb=$(field "$ref" decompress_best); rdm=$(field "$ref" decompress_median)
  ocb=$(field "$ours" compress_best); ocm=$(field "$ours" compress_median); ocn=$(field "$ours" compress_min)
  odb=$(field "$ours" decompress_best); odm=$(field "$ours" decompress_median); odn=$(field "$ours" decompress_min)
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
    "$g" "$k" "$s" "$dk" "$by" "$codec" "$L" "$(field "$ours" payloads)" "$ro" "$oo" "$(ratio "$oo" "$ro")" \
    "$rcb" "$rcm" "$ocb" "$ocm" "$ocn" "$(ratio "$ocb" "$rcb")" "$(ratio "$ocm" "$rcm")" \
    "$rdb" "$rdm" "$odb" "$odm" "$odn" "$(ratio "$odb" "$rdb")" "$(ratio "$odm" "$rdm")" \
    "$(field "$ours" alloc_compress_per_payload)" "$(field "$ours" alloc_decompress_per_payload)"
}

first=1
for g in $games; do for k in $kinds; do
  [ -d "$corpora/seed1/$g/$k" ] && [ -d "$corpora/seed2/$g/$k" ] || continue
  awk -F'\t' -v g="$g" -v k="$k" -v root="$corpora/seed1" -v n=5000 '$1 == g && $2 == k && c < n { c++; print root "/" $3 }' "$corpora/seed1/manifest.tsv" > "$work/train.list"
  for dk in $dicts; do
    "$zstd_bin" --train -q --maxdict=$((dk * 1024)) -o "$work/zdict.$dk" --filelist="$work/train.list" > /dev/null 2>&1 || { echo "# train failed $g/$k $dk" >&2; continue; }
    "$dotnet" "$dll" rawdict "$corpora/seed1/manifest.tsv" "$corpora/seed1" "$g" "$k" $((dk * 1024)) "$work/rdict.$dk"
    for s in $sizes; do
      "$dotnet" "$dll" pack "$corpora/seed2/manifest.tsv" "$corpora/seed2" "$g" "$k" "$s" "$count" "$work/pay.bin" > /dev/null || continue
      for L in $zlevels; do row zstd "$L" "$work/zdict.$dk" zstd-train "$g" "$k" "$s" "$dk"; done
      for L in $llevels; do row lz4 "$L" "$work/rdict.$dk" raw "$g" "$k" "$s" "$dk"; done
      if [ "$oursdict" = 1 ] && [ $first = 1 ] && [ "$dk" = 16 ]; then
        # The same case with a dictionary trained by this library, once.
        if [ ! -s "$work/odict" ]; then
          tdll="${TRAIN_DLL:?set TRAIN_DLL to ZstdTrainBench.dll for --ours-dict}"
          "$dotnet" "$tdll" "$work/odict" --size $((dk * 1024)) --chunks 1073741824 $(head -n 2000 "$work/train.list") > /dev/null 2>&1 || echo "# our trainer failed" >&2
        fi
        [ -s "$work/odict" ] && for L in $zlevels; do row zstd "$L" "$work/odict" our-train "$g" "$k" "$s" "$dk"; done
      fi
    done
  done
  first=0
done; done
echo "# load after: $(load)"
