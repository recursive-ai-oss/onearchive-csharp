#!/usr/bin/env bash
# Installs a .NET SDK under a user-local directory with Microsoft's dotnet-install script.
# Idempotent and version-checked: an install that already lists the pinned SDK is left alone.
#
#   tools/hashmatrix/install-dotnet.sh [dir]     default: $HOME/onearchive/dotnet
set -euo pipefail

version="10.0.402"
dir="${1:-$HOME/onearchive/dotnet}"

if [ -x "$dir/dotnet" ] && "$dir/dotnet" --list-sdks | grep -q "^$version "; then
  echo "dotnet SDK $version already installed at $dir"
  exit 0
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
curl -fsSL -o "$tmp/dotnet-install.sh" https://dot.net/v1/dotnet-install.sh
bash "$tmp/dotnet-install.sh" --version "$version" --install-dir "$dir" --no-path
echo "installed dotnet SDK $version at $dir"
