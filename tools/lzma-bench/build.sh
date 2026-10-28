#!/usr/bin/env bash
# Builds LzmaBench in Release and prints the path of LzmaBench.dll.
#   build.sh      ONEARCHIVE_ARTIFACTS names the output directory (default: artifacts/ at the repository root)
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
art="${ONEARCHIVE_ARTIFACTS:-$root/artifacts}"
ONEARCHIVE_ARTIFACTS="$art" "${DOTNET:-dotnet}" build "$here/LzmaBench.csproj" -c Release -v quiet -clp:NoSummary >&2
echo "$art/bin/LzmaBench/release/LzmaBench.dll"
