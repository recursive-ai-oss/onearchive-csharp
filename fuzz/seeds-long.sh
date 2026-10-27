#!/usr/bin/env bash
# Writes seed recipes for the zstd-encode-long-roundtrip target into DIR (default: seeds-long/).
# Each seed is four option bytes and a recipe; see ZstdLongTargets.cs for what the bytes mean.
set -euo pipefail
dir="${1:-seeds-long}"
mkdir -p "$dir"
seed() { # name hex...
  local name="$1"; shift
  local hex="$*"; hex="${hex// /}"
  printf '%b' "$(echo "$hex" | sed 's/../\\x&/g')" > "$dir/$name"
}
# options: level byte, flags, more (ldm | job<<1 | workers<<4 | window<<6), tuning
# ops: 00 len16 seed = random run; 01 dist24 len16 = copy; 02 len16 value = run; 03 dist24 len16 every = near copy
seed ldm-far-copy    0a 00 a3 05  00 60ea 07  00 60ea 08  00 60ea 09  01 100000 6010  01 000200 9000
seed plain-jobs      0a 00 22 15  00 60ea 03  02 e803 41  01 010000 8800  00 f0d8 05  01 200000 0020
seed level3-ldm-jobs 0c 02 e5 20  00 60ea 01  00 60ea 02  01 0100c0 ffff  03 020000 ff7f 11  00 b0ad 33
seed stream-flush    0a 4e 23 4b  00 60ea 03  01 000100 ffff  00 60ea 04  01 000800 ffff  02 ff00 00
seed tiny            0a 00 a3 05  00 0500 01
seed dict-neg-level  02 11 62 03  00 60ea 03  01 010000 6000  01 000300 ff00
