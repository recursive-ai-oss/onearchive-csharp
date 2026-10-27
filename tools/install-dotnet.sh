#!/usr/bin/env bash
# Installs the pinned .NET SDK user-locally (default $HOME/onearchive/dotnet).
# Idempotent and version-checked: does nothing when the SDK is already there,
# and is safe to run from two jobs at once (the second waits for the first).
#
#   tools/install-dotnet.sh [version]      default: the version below
set -euo pipefail

version="${1:-10.0.402}"
home="${ONEARCHIVE_HOME:-$HOME/onearchive}"
D="${ONEARCHIVE_DOTNET:-$home/dotnet}"
mkdir -p "$home"

have() { [ -x "$D/dotnet" ] && "$D/dotnet" --list-sdks 2>/dev/null | grep -q "^$version "; }

if have; then echo "dotnet $version already installed at $D"; exit 0; fi

# mkdir is atomic: whoever creates the lock directory installs.
lock="$home/.dotnet-install.lock"
waited=0
until mkdir "$lock" 2>/dev/null; do
  if have; then echo "dotnet $version installed by another job"; exit 0; fi
  sleep 5; waited=$((waited + 5))
  if [ $waited -gt 900 ]; then echo "timed out waiting for $lock" >&2; exit 1; fi
done
trap 'rmdir "$lock"' EXIT
if have; then echo "dotnet $version already installed at $D"; exit 0; fi

script="$home/dotnet-install.sh"
curl -fsSL --retry 3 -o "$script" https://dot.net/v1/dotnet-install.sh
chmod +x "$script"
DOTNET_CLI_TELEMETRY_OPTOUT=1 "$script" --version "$version" --install-dir "$D" --no-path
have || { echo "dotnet $version is not in $D after install" >&2; exit 1; }
"$D/dotnet" --list-sdks
