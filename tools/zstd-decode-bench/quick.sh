#!/usr/bin/env bash
# Quick direction-only decode comparison against `zstd -b` (any Unix). Usage: quick.sh [files] [levels] [seconds]
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$here/../.."
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/onearchive/dotnet}"
export PATH="$HOME/onearchive/tools/bin:$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
work="$HOME/onearchive/u14/work"; mkdir -p "$work"
files="${1:-silesia.tar enwik8}"; levels="${2:-1 3 9 19}"; secs="${3:-3}"
export ONEARCHIVE_ARTIFACTS="$HOME/onearchive/u14/artifacts"
if [ -z "${NOBUILD:-}" ]; then
  dotnet build -c Release -v quiet -clp:NoSummary "$here/ZstdDecodeBench.csproj" > "$work/build.log" 2>&1 || { tail -20 "$work/build.log"; exit 1; }
fi
dll=$(find "$ONEARCHIVE_ARTIFACTS" -name ZstdDecodeBench.dll -not -path '*/obj/*' -not -path '*/ref/*' | head -1)
echo "# $(uname -m) load $(uptime | sed 's/.*load average[s]*: //')"
for f in $files; do
  for p in "$HOME/onearchive/corpora/$f" "$HOME/onearchive/corpora/enwik8/$f"; do [ -f "$p" ] && path="$p"; done
  for L in $levels; do
    [ -f "$work/$f.$L.zst" ] || zstd -"$L" --no-check -q -f -c "$path" > "$work/$f.$L.zst"
    ref=$(zstd -b"$L" -e"$L" -i"$secs" "$path" 2>&1 | tr '\r' '\n' | grep 'MB/s' | tail -1 | sed 's/.*MB\/s, *//; s/ *MB\/s.*//')
    ours=$(dotnet "$dll" big "$work/$f.$L.zst" --seconds "$secs" | sed 's/.*oneshot=\([0-9]*\) MB.*stream=\([0-9]*\) MB.*/\1 \2/')
    set -- $ours
    echo "$f L$L ref=$ref ours=$1 stream=$2 ratio(ref/ours time)=$(awk -v r="$ref" -v o="$1" 'BEGIN{printf "%.2f", r/o}')"
  done
done
