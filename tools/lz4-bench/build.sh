#!/usr/bin/env bash
# Builds the LZ4 encoder bench and prints the path of Lz4Bench.dll.
#
#   build.sh [artifacts-dir]
#
# Uses the dotnet on PATH (or DOTNET_ROOT). Output goes under the artifacts directory (default: ./artifacts/lz4-bench).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
art="${1:-$here/../../artifacts/lz4-bench}"
mkdir -p "$art"
ONEARCHIVE_ARTIFACTS="$art" MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  dotnet build "$here/Lz4Bench.csproj" -c Release -v quiet -clp:NoSummary >&2
echo "$art/bin/Lz4Bench/release/Lz4Bench.dll"
