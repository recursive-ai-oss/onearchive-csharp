#!/usr/bin/env bash
# Installs a pinned Node.js LTS under a user-local prefix, for running the browser-wasm hash-matrix
# column headless. Idempotent: an install whose `node --version` matches the pin is left alone.
#
#   tools/node/install.sh [prefix]     default prefix: $HOME/onearchive/tools/node
#
# The download is checked against the SHA-256 pinned below (from the release's SHASUMS256.txt).
set -euo pipefail

version="v22.23.4"
prefix="${1:-$HOME/onearchive/tools/node}"

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64)  file="node-$version-linux-x64.tar.gz";    sha256="b8492284c76a54baf58d61668dd0e41ee0fc98adabf9503d30f05d08f55b4406" ;;
  Linux-aarch64) file="node-$version-linux-arm64.tar.xz";  sha256="6287aa46b6c375d816e63ae66d024fbbfd70404825aabdfdf898a970871d2f4b" ;;
  Darwin-arm64)  file="node-$version-darwin-arm64.tar.gz"; sha256="c9e1dbc748b33ed2d14f42445191ecaf14c56259c7a9a26d0042c49ec2b66428" ;;
  *) echo "no pinned Node.js build for $(uname -s)-$(uname -m)" >&2; exit 1 ;;
esac

if [ -x "$prefix/bin/node" ] && [ "$("$prefix/bin/node" --version)" = "$version" ]; then
  echo "node $version already installed at $prefix"
  exit 0
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
curl -fsSL -o "$tmp/$file" "https://nodejs.org/dist/$version/$file"
if command -v sha256sum >/dev/null; then actual="$(sha256sum "$tmp/$file" | cut -d' ' -f1)"; else actual="$(shasum -a 256 "$tmp/$file" | cut -d' ' -f1)"; fi
if [ "$actual" != "$sha256" ]; then
  echo "SHA-256 mismatch for $file: expected $sha256, got $actual" >&2
  exit 1
fi

rm -rf "$prefix"
mkdir -p "$prefix"
tar -xf "$tmp/$file" -C "$prefix" --strip-components=1
echo "installed node $("$prefix/bin/node" --version) at $prefix"
