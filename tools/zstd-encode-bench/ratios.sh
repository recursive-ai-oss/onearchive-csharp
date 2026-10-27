#!/usr/bin/env bash
# Compressed sizes of the encoder against the zstd tool (no timing): one row per file and level.
#
#   ratios.sh [--files "silesia.tar enwik8"] [--levels "-5 -1 1 2 3 4"]
#
# Environment: ONEARCHIVE_ZSTD (tool, default zstd), ONEARCHIVE_CORPORA (default ~/onearchive/corpora), ONEARCHIVE_BENCH_TMP
# (scratch), BENCH_DLL (built ZstdEncodeBench.dll), DOTNET (dotnet executable). Each of our frames is also decoded by the tool.
set -euo pipefail
zstd="${ONEARCHIVE_ZSTD:-zstd}"
corpora="${ONEARCHIVE_CORPORA:-$HOME/onearchive/corpora}"
work="${ONEARCHIVE_BENCH_TMP:-${TMPDIR:-/tmp}/zstd-encode-ratios}"
dll="${BENCH_DLL:?set BENCH_DLL to ZstdEncodeBench.dll}"
dotnet="${DOTNET:-dotnet}"
files="silesia.tar enwik8"
levels="-5 -1 1 2 3 4"
while [ $# -gt 0 ]; do
  case "$1" in
    --files) files="$2"; shift 2 ;;
    --levels) levels="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
mkdir -p "$work"
find_file() { for c in "$corpora/$1" "$corpora/enwik8/$1" "$corpora/silesia/$1" "$corpora/canterbury/$1"; do [ -f "$c" ] && { echo "$c"; return; }; done; echo "missing corpus file $1" >&2; exit 1; }
echo "# $("$zstd" --version | head -1)"
printf 'file\tlevel\tref_bytes\tours_bytes\tdelta_pct\ttool_decodes_ours\n'
for f in $files; do
  path="$(find_file "$f")"
  for L in $levels; do
    if [ "${L#-}" != "$L" ]; then flag="--fast=${L#-}"; elif [ "$L" -gt 19 ]; then flag="--ultra -$L"; else flag="-$L"; fi
    ref=$("$zstd" $flag --no-check -q -c "$path" | wc -c)
    "$dotnet" "$dll" big "$path" "$L" --seconds 0 --out "$work/ours.zst" > "$work/ours.out"
    ours=$(stat -c %s "$work/ours.zst")
    if "$zstd" -d -q -c "$work/ours.zst" | cmp -s - "$path"; then dec=ok; else dec=FAIL; fi
    printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$f" "$L" "$ref" "$ours" "$(awk -v a="$ours" -v b="$ref" 'BEGIN{printf "%+.3f", (a-b)*100/b}')" "$dec"
  done
done
