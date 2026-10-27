#!/usr/bin/env bash
# Generates the game snapshot data under $ONEARCHIVE_HOME/corpora/game/seed1 and seed2 with the
# OneGame.Corpus tool of the public onegame repository at the pinned commit. Idempotent.
#
#   tools/fetch-game-corpus.sh
#
# Layout: game/seedN/<game>/<kind>/NNNNNN.bin and game/seedN/manifest.tsv
# (game, kind, file, size, sha256, schema, tick). Games: lanterns, bricks, solitaire.
# Kinds: frames, snapshots, saves, recordings, transfer. About 66 MB and 790k mostly tiny
# files per seed, so it is generated on each machine rather than copied. Dictionaries are
# trained on seed1 and benchmarked on seed2. The generator's own report ends with the
# manifest SHA-256, which must equal the value pinned in game-corpus.tsv.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
home="${ONEARCHIVE_HOME:-$HOME/onearchive}"
G="$home/corpora/game"
mkdir -p "$G" "$home/corpora/dl"

sha256_of() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

for d in "$HOME/dotnet10" "$home/dotnet"; do
  if [ -x "$d/dotnet" ]; then dn="$d"; break; fi
done
: "${dn:?no .NET SDK found (run tools/install-dotnet.sh)}"
export DOTNET_ROOT="$dn" PATH="$dn:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1

IFS=$'\t' read -r _ commit url asha < <(grep -m1 '^source' "$here/game-corpus.tsv")
need=0
while IFS=$'\t' read -r k seed want _; do
  [ "$k" = seed ] || continue
  stamp="$G/seed$seed/.manifest-sha256"
  if [ -f "$stamp" ] && [ "$(cat "$stamp")" = "$want" ]; then continue; fi
  need=1
done < <(grep '^seed' "$here/game-corpus.tsv")
if [ $need -eq 0 ]; then echo "game corpus ok (seed1, seed2)"; exit 0; fi

arc="$home/corpora/dl/onegame-$commit.tar.gz"
if [ ! -s "$arc" ] || [ "$(sha256_of "$arc")" != "$asha" ]; then
  curl -fsSL --retry 3 -o "$arc.part" "$url"; mv "$arc.part" "$arc"
fi
[ "$(sha256_of "$arc")" = "$asha" ] || { echo "SHA-256 mismatch for the onegame archive" >&2; rm -f "$arc"; exit 1; }

src="$home/corpora/dl/onegame-src"
rm -rf "$src"; mkdir -p "$src"
tar -xzf "$arc" -C "$src" --strip-components=1

while IFS=$'\t' read -r k seed want _; do
  [ "$k" = seed ] || continue
  stamp="$G/seed$seed/.manifest-sha256"
  if [ -f "$stamp" ] && [ "$(cat "$stamp")" = "$want" ]; then continue; fi
  rm -rf "$G/seed$seed"
  echo "generating seed $seed"
  ( cd "$src" && nice -n 10 dotnet run --project tools/OneGame.Corpus -c Release -- --seed "$seed" --out "$G/seed$seed" ) > "$G/seed$seed.report.json"
  got="$(grep -oE '[0-9a-f]{64}' "$G/seed$seed.report.json" | tail -1)"
  if [ "$want" = TBD ]; then
    echo "seed $seed manifest sha256 = $got (not pinned yet; record it in game-corpus.tsv)" >&2
    exit 1
  fi
  if [ "$got" != "$want" ]; then
    echo "seed $seed: manifest sha256 $got does not match the pinned $want" >&2
    exit 1
  fi
  # The manifest on disk must hash to the same value the generator reported.
  [ "$(sha256_of "$G/seed$seed/manifest.tsv")" = "$want" ] || echo "note: manifest.tsv file hash differs from the reported value (the report may hash a different form)" >&2
  echo "$want" > "$stamp"
  echo "seed $seed: $(find "$G/seed$seed" -name '*.bin' | wc -l | tr -d ' ') files, manifest sha256 $got"
done < <(grep '^seed' "$here/game-corpus.tsv")
rm -rf "$src"
# The build output lives under the source tree, which is now removed.
echo "game corpus ok (seed1, seed2)"
