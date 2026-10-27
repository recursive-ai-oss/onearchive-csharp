#!/usr/bin/env bash
# Compares the manifests in DIR. The reference is x64-jit.tsv when present, else the first file by
# name. Prints, per other manifest, "equal" or the first entry that differs; exits non-zero on any mismatch.
#
#   tools/hashmatrix/compare.sh DIR [--dotnet DIR]
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
dir="${1:?usage: compare.sh DIR [--dotnet DIR]}"
if [ "${2:-}" = "--dotnet" ]; then export DOTNET_ROOT="$3" PATH="$3:$PATH"; fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
art="${ONEARCHIVE_ARTIFACTS:-$root/artifacts}"

files=()
[ -f "$dir/x64-jit.tsv" ] && files+=("$dir/x64-jit.tsv")
for f in "$dir"/*.tsv; do
  [ "$f" = "$dir/x64-jit.tsv" ] || files+=("$f")
done

ONEARCHIVE_ARTIFACTS="$art/jit" dotnet build "$root/tools/hashmatrix/Host" -c Release -v quiet -clp:NoSummary
dotnet "$art/jit/bin/HashMatrix/release/HashMatrix.dll" compare "${files[@]}"
