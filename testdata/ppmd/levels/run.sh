#!/usr/bin/env bash
# Black-box experiment: which PPMd order and model memory 7-Zip picks for each -mx level, for a 7z archive (variant H) and
# for a zip archive (variant I, method 98), on a large input and on inputs around the size where the model memory is reduced.
#
# usage: run.sh <7zz> <large-input> <workdir> <out.tsv>
#   <7zz>          the 7-Zip command-line tool, version recorded in the output
#   <large-input>  a file big enough that no reduction applies (silesia.tar, 211957760 bytes, was used)
#   <workdir>      scratch directory (archives are written and deleted here)
#
# Output rows: format, level, input size, order, memory (bytes), restore method (zip only; "-" for 7z).
#   7z:  read from `7zz l -slt` ("Method = PPMD:o<order>:mem<log2>"; the 7z coder properties hold the same numbers).
#   zip: the two-byte header at the start of the method-98 data (order-1 in bits 0-3, memory MiB-1 in bits 4-11, restore in 12-15);
#        `7zz l -slt` prints only "PPMd" for zip entries.
# Small inputs are slices of the large input (head -c), so every size has the same kind of content.
set -euo pipefail
Z=$1 IN=$2 W=$3 OUT=$4
mkdir -p "$W"

one() { # format level size
  local fmt=$1 lvl=$2 size=$3
  local f="$W/in.$fmt.$lvl.$size" a="$W/a.$fmt.$lvl.$size"
  if [ "$size" = full ]; then f=$IN; size=$(stat -c %s "$IN"); else head -c "$size" "$IN" > "$f"; fi
  rm -f "$a.$fmt"
  if [ "$fmt" = 7z ]; then
    "$Z" a -t7z -m0=PPMd -mx="$lvl" -mmt=1 "$a.7z" "$f" > /dev/null
    m=$("$Z" l -slt "$a.7z" | awk '/^Method = PPMD:/ {print $3}')
    o=${m#PPMD:o}; o=${o%%:*}; lg=${m##*:mem}
    if [[ "$lg" == *m ]]; then mem=$(( ${lg%m} << 20 )); else mem=$(( 1 << lg )); fi
    printf '7z\t%s\t%s\t%s\t%s\t-\n' "$lvl" "$size" "$o" "$mem"
  else
    "$Z" a -tzip -mm=PPMd -mx="$lvl" -mmt=1 "$a.zip" "$f" > /dev/null
    python3 - "$a.zip" "$lvl" "$size" <<'PY'
import struct, sys
d = open(sys.argv[1], 'rb').read()
assert d[:4] == b'PK\x03\x04'
meth, = struct.unpack_from('<H', d, 8)
n, e = struct.unpack_from('<HH', d, 26)
if meth != 98:  # 7-Zip stores an entry this small instead of compressing it
    print('zip', sys.argv[2], sys.argv[3], '-', '-', 'stored (method %d)' % meth, sep='\t'); sys.exit(0)
h, = struct.unpack_from('<H', d, 30 + n + e)
print('zip', sys.argv[2], sys.argv[3], (h & 15) + 1, (((h >> 4) & 255) + 1) << 20, (h >> 12), sep='\t')
PY
  fi
  rm -f "$a.7z" "$a.zip"; [ "$f" = "$IN" ] || rm -f "$f"
}
export -f one; export Z IN W

{
  # Large input: every level, both formats.
  for fmt in 7z zip; do for l in 1 2 3 4 5 6 7 8 9; do echo "$fmt $l full"; done; done
  # Around the reduction threshold: 2^j and 2^j+1 bytes at levels 1, 5, 9 (and 7-9 differ in order only).
  for fmt in 7z zip; do for l in 1 5 9; do for j in 12 14 16 18 19 20 21 22 23 24; do
    echo "$fmt $l $((1<<j))"; echo "$fmt $l $(((1<<j)+1))"; done; done; done
  # The other levels, at the first thresholds and at tiny sizes (1 and 100 bytes), to show the rule does not depend on the level.
  for fmt in 7z zip; do for l in 2 3 4 6 7 8; do for s in 1 100 4096 4097 1048576 1048577; do echo "$fmt $l $s"; done; done; done
  for fmt in 7z zip; do for l in 1 5 9; do for s in 1 100; do echo "$fmt $l $s"; done; done; done
} | xargs -P 4 -L 1 bash -c 'one $0 $1 $2' > "$OUT.unsorted"
{
  printf '# %s\n' "$("$Z" | sed -n 2p)"
  printf '# format\tlevel\tinput_bytes\torder\tmemory_bytes\trestore\n'
  sort -k1,1 -k2,2n -k3,3n "$OUT.unsorted"
} > "$OUT"
rm -f "$OUT.unsorted"
