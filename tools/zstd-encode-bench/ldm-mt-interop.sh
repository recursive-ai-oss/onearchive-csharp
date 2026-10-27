#!/usr/bin/env bash
# Round trips of long-distance matching and jobs against the zstd tool, both ways, on whole files.
#
#   ldm-mt-interop.sh [--files "silesia.tar enwik8"] [--levels "-1 1 3 4"] [--threads "0 1 2 8"] [--ldm "0 1"] [--job auto]
#
# For every file, level, long-distance setting and thread count: our frame is decoded by our decoder (ours to ours) and by
# `zstd -d --long=31` (ours to the tool); the tool's `zstd --long=27 -T<n>` frame is decoded by our decoder (tool to ours).
# Thread count 0 means no jobs at all; otherwise the jobs have the size given by --job (default auto). The frames of the same file and
# options must be equal for every thread count above 0, which is checked too.
# Environment: ONEARCHIVE_ZSTD, ONEARCHIVE_CORPORA, BENCH_DLL (ZstdEncodeBench.dll), DOTNET. Exit code 1 if anything failed.
set -uo pipefail
zstd="${ONEARCHIVE_ZSTD:-zstd}"
corpora="${ONEARCHIVE_CORPORA:-$HOME/onearchive/corpora}"
dll="${BENCH_DLL:?set BENCH_DLL to ZstdEncodeBench.dll}"
dotnet="${DOTNET:-dotnet}"
work="${ONEARCHIVE_BENCH_TMP:-${TMPDIR:-/tmp}/zstd-ldm-mt-interop}"
files="silesia.tar enwik8"; levels="-1 1 3 4"; threads="0 1 2 8"; ldms="0 1"; job="auto"
while [ $# -gt 0 ]; do
  case "$1" in
    --files) files="$2"; shift 2 ;;
    --levels) levels="$2"; shift 2 ;;
    --threads) threads="$2"; shift 2 ;;
    --ldm) ldms="$2"; shift 2 ;;
    --job) job="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
mkdir -p "$work"
find_file() { for c in "$1" "$corpora/$1" "$corpora/enwik8/$1" "$corpora/silesia/$1"; do [ -f "$c" ] && { echo "$c"; return; }; done; echo "missing $1" >&2; exit 1; }
fail=0
fsize() { stat -c %s "$1" 2>/dev/null || stat -f %z "$1" 2>/dev/null || echo -; }
hashfile() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -c1-16; else shasum -a 256 "$1" | cut -c1-16; fi; }
echo "# $("$zstd" --version | head -1)"
for f in $files; do
  path="$(find_file "$f")"
  for L in $levels; do
    if [ "${L#-}" != "$L" ]; then sel="--fast=${L#-}"; else sel="-$L"; fi
    for ldm in $ldms; do
      lo=""; ro=""
      if [ "$ldm" = 1 ]; then lo="--long 27"; ro="--long=27"; fi
      first_hash=""
      for T in $threads; do
        if [ "$T" = 0 ]; then oo="$lo"; else oo="$lo --workers $T --job $job"; fi
        res="ok"; rm -f "$work/o.zst" "$work/r.zst"
        line="$("$dotnet" "$dll" big "$path" "$L" --once $oo --out "$work/o.zst" 2>&1)" || res="OURS-ROUNDTRIP-FAILED"
        size="$(fsize "$work/o.zst")"
        "$zstd" -d --long=31 -c "$work/o.zst" 2>"$work/err" | cmp -s - "$path" || res="$res TOOL-DECODE-FAILED"
        # The tool's frame, decoded by us.
        tt="$T"; [ "$T" = 0 ] && tt=1
        "$zstd" $sel $ro -T"$tt" -q -f -c "$path" > "$work/r.zst" 2>>"$work/err" || res="$res TOOL-ENCODE-FAILED"
        "$dotnet" "$dll" verify "$work/r.zst" "$path" >/dev/null 2>&1 || res="$res TOOL-FRAME-OURS-DECODE-FAILED"
        if [ "$T" != 0 ]; then
          h="$(hashfile "$work/o.zst")"
          if [ -z "$first_hash" ]; then first_hash="$h"; elif [ "$h" != "$first_hash" ]; then res="$res BYTES-DIFFER-BETWEEN-THREAD-COUNTS"; fi
        fi
        printf '%s\tL%s\tldm=%s\tT=%s\tours=%s\ttool=%s\t%s\n' "$f" "$L" "$ldm" "$T" "$size" "$(fsize "$work/r.zst")" "$res"
        [ "$res" = ok ] || fail=1
      done
    done
  done
done
exit $fail
