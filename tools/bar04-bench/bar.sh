#!/usr/bin/env bash
# Bar rows for the codecs of the 0.4 chunk that are not brotli's encoder: brotli decode, the bzip2 encoder, Deflate64 and cab LZX decode.
#
#   bar.sh --mode brotli-dec|bz2-enc|d64-enc|d64-dec|ppmd-enc|ppmd-dec|lzx-dec [--files "silesia.tar enwik8"] [--levels "1 5 9"] [--reps 3]
#
# brotli-dec: ours against `brotli -d -c`, each on the tool's output and on ours at quality N (window 22).
# bz2-enc:    ours (one call, in memory) against `bzip2 -N -c` (stdin to a file); size, rate, peak memory, and whether `bzip2 -dc`
#             reads our stream back to the original bytes (column interop).
# d64-enc:    ours (one call, in memory) against 7-Zip `a -tzip -mm=Deflate64 -mx=N -mmt=1`. 7-Zip has no raw-stream mode, so its time is
#             the whole archive run (read the file, CRC-32, compress, write the zip) minus the start-up of the same command on a one-byte
#             file; its size is the zip entry's compressed size from the central directory (the zip framing is not counted). The CRC-32 and
#             the I/O are small next to compression at -mx=5 and 9 and are a visible share at -mx=1, which favours the reference there.
#             Our stream is also wrapped in a zip (d64-zip) and read back by `7zz t` (column interop).
# d64-dec:    ours on the raw stream against `7zz e -so` on the zip (CRC-32 checked by 7-Zip, not by us; the difference is small), on the
#             stream 7-Zip wrote and on ours.
# ppmd-enc:   ours (one call, in memory, PPMd variant H) at the order and model memory 7-Zip uses for -mx=N (docs/ppmd.md) against 7-Zip
#             `a -t7z -m0=PPMd -mx=N -mmt=1`. As for d64-enc, 7-Zip's time is the whole archive run minus the start-up of the same command on a
#             one-byte file; its size is the PPMd pack stream's size from `7zz l -slt` (the 7z framing is not counted). Interop: 7-Zip must
#             test (`7zz t`) a one-entry 7z written by our SevenZipWriter at that level, and our decoder must read 7-Zip's pack stream
#             (bytes 32 onwards of its archive) back to the original bytes.
# ppmd-dec:   ours on the raw stream against `7zz e -so` on the 7z, on the stream 7-Zip wrote and on ours (decoded by 7-Zip from our 7z).
# lzx-dec:    a one-file cabinet of the whole input, LZX window 2^21, built by the test compressor in the test helpers (slow, not timed,
#             cached for the job); cabextract must extract it to the original bytes. Ours reads it through CabReader (checksums verified)
#             into a null sink; cabextract is timed as `cabextract -q -p` to /dev/null. Same bytes for both sides.
# The tools are command-line programs, so their time is the wall time of a run with output to /dev/null minus the start-up of the same
# command on a one-byte (or one-entry) input, best of --reps. Compressed files are cached in $ONEARCHIVE_BENCH_TMP/bar04-cache.
# Environment: BENCH_DLL (built Bar04Bench.dll), ONEARCHIVE_CORPORA, BROTLI, BZIP2, SEVENZ (7zz, then 7z), CABEXTRACT, DOTNET,
# ONEARCHIVE_BENCH_TMP or TMPDIR (use RAM), BAR_QUICK=1 (each input cut to its first 1 MiB and one repeat: proves the script, no timing
# claims). Method: docs/bench-method.md. Pass rule: our size at most 1.01 times the tool's, the tool's rate at most 1.15 times ours.
set -euo pipefail
corpora="${ONEARCHIVE_CORPORA:-$HOME/onearchive/corpora}"
dll="${BENCH_DLL:?set BENCH_DLL to Bar04Bench.dll}"
dotnet="${DOTNET:-dotnet}"
files="silesia.tar enwik8"; levels=""; reps=3; mode=""
while [ $# -gt 0 ]; do
  case "$1" in
    --files) files="$2"; shift 2 ;;
    --levels) levels="$2"; shift 2 ;;
    --reps) reps="$2"; shift 2 ;;
    --mode) mode="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
quick="${BAR_QUICK:-}"
[ -n "$quick" ] && reps=1
cache="${ONEARCHIVE_BENCH_TMP:-${TMPDIR:-/tmp}}/bar04-cache"; mkdir -p "$cache"
tmp="$(mktemp -d "${TMPDIR:-/tmp}/bar04.XXXXXX")"; trap 'rm -rf "$tmp"' EXIT
find_file() {
  local p=""
  if [ -f "$1" ]; then p="$1"; else for c in "$corpora/$1" "$corpora/enwik8/$1" "$corpora/silesia/$1" "$corpora/x/$1"; do [ -f "$c" ] && { p="$c"; break; }; done; fi
  [ -n "$p" ] || { echo "missing $1" >&2; exit 1; }
  if [ -n "$quick" ]; then head -c 1048576 "$p" > "$cache/q-$(basename "$p")"; p="$cache/q-$(basename "$p")"; fi
  echo "$p"
}
if [ "$(uname)" = Darwin ]; then
  tv() { /usr/bin/time -l "$@"; }
  peak() { awk '/maximum resident set size/ {printf "%d", $1 / 1024}' "$1"; }
  load() { uptime | sed 's/.*load averages*: //; s/,//g'; }
  cpu() { sysctl -n machdep.cpu.brand_string; }; cores() { sysctl -n hw.ncpu; }
else
  tv() { /usr/bin/time -v "$@"; }
  peak() { grep 'Maximum resident' "$1" | awk '{print $NF}'; }
  load() { cut -d' ' -f1-3 /proc/loadavg; }
  cpu() { grep -m1 'model name' /proc/cpuinfo | cut -d: -f2 | xargs; }; cores() { nproc; }
fi
ratio() { awk -v a="$1" -v b="$2" -v d="$3" 'BEGIN { printf "%.*f", d, a / b }'; }
field() { sed -n "s/.*$1=\\([0-9.]*\\).*/\\1/p" | head -1; }
# Wall time of one command, stdin from $WT_IN and stdout to $WT_OUT (default /dev/null). perl is on every bench host.
WT='use Time::HiRes qw(time); $t = time; $pid = fork; if (!$pid) { open STDIN, "<", $ENV{WT_IN} if $ENV{WT_IN}; open STDOUT, ">", $ENV{WT_OUT} || "/dev/null"; exec @ARGV or exit 127 } waitpid $pid, 0; printf STDERR "WALL %.6f\n", time - $t; exit($? >> 8)'
walltime() { perl -e "$WT" "$@" 2>&1 >/dev/null | sed -n 's/^WALL //p'; }
besttime() { local n="$1" b="" t; shift; for _ in $(seq 1 "$n"); do t="$(walltime "$@")"; b="$(awk -v a="$b" -v t="$t" 'BEGIN { print (a == "" || t < a) ? t : a }')"; done; echo "$b"; }
rate() { awk -v n="$1" -v t="$2" -v s="$3" 'BEGIN { d = t - s; if (d < 1e-6) d = 1e-6; printf "%.2f", n / 1e6 / d }'; }
fsize() { wc -c < "$1" | tr -d ' '; }
verdict_enc() { awk -v s="$1" -v t="$2" 'BEGIN { print (s <= 1.01 && t <= 1.15) ? "pass" : "FAIL" }'; }
header() { echo "# cpu: $(cpu), cores: $(cores), load before: $(load), $("$dotnet" --version | head -1) SDK, reps=$reps, mode=$mode, reference: $1${quick:+, QUICK (1 MiB prefix, no timing claims)}"; }

brotli="${BROTLI:-brotli}"; bzip2="${BZIP2:-bzip2}"; cabextract="${CABEXTRACT:-cabextract}"
sz="${SEVENZ:-$(command -v 7zz || command -v 7z || true)}"
sz_args=(-bd -bso0 -bsp0)

# Decode table: file codec level source ref_dec_MBps ours_dec_MBps ours_dec_med ours_dec_min time_ratio ref_peak_KB ours_peak_KB verdict
dec_header() { printf 'file\tcodec\tlevel\tsource\tref_dec_MBps\tours_dec_MBps\tours_dec_med\tours_dec_min\ttime_ratio\tref_peak_KB\tours_peak_KB\tverdict\n'; }
dec_row() { # file codec level source size ref_time startup ref_time_file ours_out ours_time_file
  local tr; tr="$(ratio "$(rate "$5" "$6" "$7")" "$(field dec_MBps < "$9")" 3)"
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$1" "$2" "$3" "$4" "$(rate "$5" "$6" "$7")" "$(field dec_MBps < "$9")" "$(field dec_MBps_med < "$9")" "$(field dec_MBps_min < "$9")" "$tr" "$(peak "$8")" "$(peak "${10}")" "$(awk -v t="$tr" 'BEGIN { print (t <= 1.15) ? "pass" : "FAIL" }')"
}

# A one-entry 7z written with PPMd has its pack stream right after the 32-byte signature header; its size is the entry's packed size.
ppmd_packed() { "$sz" l -slt -bd "$1" | sed -n 's/^Packed Size = //p' | head -1; }
# The coder's order and memory as 7-Zip reports them ("PPMD:o6:mem24", memory 2^24 bytes). 7-Zip shrinks the model for small inputs, so
# decode uses the stream's own parameters and the encode row fails when they differ from the level's table (docs/ppmd.md).
ppmd_props() { "$sz" l -slt -bd "$1" | sed -n 's/^Method = PPMD:o\([0-9]*\):mem\([0-9]*\)$/\1 \2/p' | head -1; }
ppmd_expect() { case "$1" in 1) echo "4 20" ;; 2) echo "4 21" ;; 3) echo "5 22" ;; 4) echo "5 23" ;; 5) echo "6 24" ;; 6) echo "8 25" ;; 7) echo "16 26" ;; 8) echo "24 26" ;; *) echo "32 26" ;; esac; }
ppmd_decargs() { local p; p="${2:-$(ppmd_expect "$1")}"; set -- $p; echo "--order $1 --memlog $(($2 - 20))"; }
ppmd_raw() { tail -c +33 "$1" | head -c "$(ppmd_packed "$1")" > "$2"; }

case "$mode" in
brotli-dec)
  [ -n "$levels" ] || levels="1 5 9"
  header "$("$brotli" --version 2>&1 | head -1), start-up subtracted"
  printf 'x' | "$brotli" -q 1 -c > "$tmp/tiny.br"; su="$(WT_IN="$tmp/tiny.br" besttime 20 "$brotli" -d -c)"; echo "# start-up: $su s"
  dec_header
  for f in $files; do
    path="$(find_file "$f")"; size="$(fsize "$path")"; b="$(basename "$path")"
    for L in $levels; do
      for src in ref ours; do
        in="$cache/$src-brotli-$b-$L.br"
        if [ ! -s "$in" ]; then
          if [ "$src" = ref ]; then "$brotli" -q "$L" -w 22 -c < "$path" > "$in"; else "$dotnet" "$dll" brotli-enc "$path" "$L" --reps 0 --out "$in" > /dev/null; fi
        fi
        t="$(WT_IN="$in" besttime "$reps" "$brotli" -d -c)"
        WT_IN="$in" tv perl -e "$WT" "$brotli" -d -c > /dev/null 2> "$tmp/ref.time"
        tv "$dotnet" "$dll" brotli-dec "$in" --reps "$reps" --expect "$path" > "$tmp/our.out" 2> "$tmp/our.time"
        dec_row "$f" brotli "$L" "$src" "$size" "$t" "$su" "$tmp/ref.time" "$tmp/our.out" "$tmp/our.time"
      done
    done
  done
  ;;
bz2-enc)
  [ -n "$levels" ] || levels="1 5 9"
  header "$("$bzip2" --help 2>&1 | head -1 | sed 's/,.*//'), start-up subtracted"
  printf 'x' > "$tmp/tiny"; su="$(WT_IN="$tmp/tiny" besttime 20 "$bzip2" -1 -c)"; echo "# start-up: $su s"
  printf 'file\tcodec\tlevel\tref_size\tours_size\tsize_ratio\tref_MBps\tours_MBps\tours_med\tours_min\ttime_ratio\tref_peak_KB\tours_peak_KB\tinterop\tverdict\n'
  for f in $files; do
    path="$(find_file "$f")"; size="$(fsize "$path")"; b="$(basename "$path")"
    for L in $levels; do
      c="$cache/bzip2-$b-$L.bz2"
      WT_IN="$path" WT_OUT="$c" tv perl -e "$WT" "$bzip2" -"$L" -c > /dev/null 2> "$tmp/ref.time"
      t="$(sed -n 's/^WALL //p' "$tmp/ref.time")"
      if [ "$reps" -gt 1 ]; then t2="$(WT_IN="$path" besttime $((reps - 1)) "$bzip2" -"$L" -c)"; t="$(awk -v a="$t" -v b="$t2" 'BEGIN { print (b < a) ? b : a }')"; fi
      oc="$cache/ours-bzip2-$b-$L.bz2"
      tv "$dotnet" "$dll" bz2-enc "$path" "$L" --reps "$reps" --out "$oc" > "$tmp/our.out" 2> "$tmp/our.time"
      "$bzip2" -dc < "$oc" | cmp -s - "$path" && interop=ok || interop=FAIL
      rs="$(fsize "$c")"; os="$(field size < "$tmp/our.out")"; rr="$(rate "$size" "$t" "$su")"; orate="$(field comp_MBps < "$tmp/our.out")"
      sr="$(ratio "$os" "$rs" 4)"; tr="$(ratio "$rr" "$orate" 3)"; v="$(verdict_enc "$sr" "$tr")"; [ "$interop" = ok ] || v=FAIL
      printf '%s\tbzip2\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$f" "$L" "$rs" "$os" "$sr" "$rr" "$orate" "$(field comp_MBps_med < "$tmp/our.out")" "$(field comp_MBps_min < "$tmp/our.out")" "$tr" "$(peak "$tmp/ref.time")" "$(peak "$tmp/our.time")" "$interop" "$v"
    done
  done
  ;;
d64-enc)
  [ -n "$sz" ] || { echo "no 7zz or 7z on PATH" >&2; exit 1; }
  [ -n "$levels" ] || levels="1 5 9"
  header "$("$sz" 2>&1 | sed -n 2p), 7-Zip -tzip -mm=Deflate64 -mmt=1, start-up subtracted"
  printf 'x' > "$tmp/tiny"; rm -f "$tmp/tiny.zip"
  su=""; for _ in $(seq 1 10); do rm -f "$tmp/tiny.zip"; t="$(walltime "$sz" a -tzip -mm=Deflate64 -mx=5 -mmt=1 "${sz_args[@]}" "$tmp/tiny.zip" "$tmp/tiny")"; su="$(awk -v a="$su" -v t="$t" 'BEGIN { print (a == "" || t < a) ? t : a }')"; done; echo "# start-up: $su s"
  printf 'file\tcodec\tlevel\tref_size\tours_size\tsize_ratio\tref_MBps\tours_MBps\tours_med\tours_min\ttime_ratio\tref_peak_KB\tours_peak_KB\tinterop\tverdict\n'
  for f in $files; do
    path="$(find_file "$f")"; size="$(fsize "$path")"; b="$(basename "$path")"
    for L in $levels; do
      z="$cache/7z-d64-$b-$L.zip"; rm -f "$z"
      tv perl -e "$WT" "$sz" a -tzip -mm=Deflate64 -mx="$L" -mmt=1 "${sz_args[@]}" "$z" "$path" > /dev/null 2> "$tmp/ref.time"
      t="$(sed -n 's/^WALL //p' "$tmp/ref.time")"
      for _ in $(seq 2 "$reps"); do rm -f "$tmp/r.zip"; t2="$(walltime "$sz" a -tzip -mm=Deflate64 -mx="$L" -mmt=1 "${sz_args[@]}" "$tmp/r.zip" "$path")"; t="$(awk -v a="$t" -v b="$t2" 'BEGIN { print (b < a) ? b : a }')"; done
      rm -f "$tmp/r.zip"
      rs="$("$dotnet" "$dll" d64-payload "$z" | sed -n 's/.*csize=\([0-9]*\).*/\1/p')"
      tv "$dotnet" "$dll" d64-enc "$path" "$L" --reps "$reps" --out "$cache/ours-d64-$b-$L.raw" > "$tmp/our.out" 2> "$tmp/our.time"
      "$dotnet" "$dll" d64-zip "$path" "$L" --out "$cache/ours-d64-$b-$L.zip" > /dev/null
      "$sz" e -so "${sz_args[@]}" "$cache/ours-d64-$b-$L.zip" 2> /dev/null | cmp -s - "$path" && interop=ok || interop=FAIL
      os="$(field size < "$tmp/our.out")"; rr="$(rate "$size" "$t" "$su")"; orate="$(field comp_MBps < "$tmp/our.out")"
      sr="$(ratio "$os" "$rs" 4)"; tr="$(ratio "$rr" "$orate" 3)"; v="$(verdict_enc "$sr" "$tr")"; [ "$interop" = ok ] || v=FAIL
      printf '%s\tdeflate64\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$f" "$L" "$rs" "$os" "$sr" "$rr" "$orate" "$(field comp_MBps_med < "$tmp/our.out")" "$(field comp_MBps_min < "$tmp/our.out")" "$tr" "$(peak "$tmp/ref.time")" "$(peak "$tmp/our.time")" "$interop" "$v"
    done
  done
  ;;
d64-dec)
  [ -n "$sz" ] || { echo "no 7zz or 7z on PATH" >&2; exit 1; }
  [ -n "$levels" ] || levels="1 5 9"
  header "$("$sz" 2>&1 | sed -n 2p), 7-Zip e -so, start-up subtracted"
  printf 'x' > "$tmp/tiny"; "$dotnet" "$dll" d64-zip "$tmp/tiny" 5 --out "$tmp/tiny.zip" > /dev/null
  su="$(besttime 20 "$sz" e -so "${sz_args[@]}" "$tmp/tiny.zip")"; echo "# start-up: $su s"
  dec_header
  for f in $files; do
    path="$(find_file "$f")"; size="$(fsize "$path")"; b="$(basename "$path")"
    for L in $levels; do
      for src in ref ours; do
        if [ "$src" = ref ]; then
          z="$cache/7z-d64-$b-$L.zip"
          [ -s "$z" ] || "$sz" a -tzip -mm=Deflate64 -mx="$L" -mmt=1 "${sz_args[@]}" "$z" "$path"
          raw="$tmp/ref.raw"; "$dotnet" "$dll" d64-payload "$z" --out "$raw" > /dev/null
        else
          z="$cache/ours-d64-$b-$L.zip"; raw="$cache/ours-d64-$b-$L.raw"
          if [ ! -s "$z" ]; then "$dotnet" "$dll" d64-enc "$path" "$L" --reps 0 --out "$raw" > /dev/null; "$dotnet" "$dll" d64-zip "$path" "$L" --out "$z" > /dev/null; fi
        fi
        t="$(besttime "$reps" "$sz" e -so "${sz_args[@]}" "$z")"
        tv perl -e "$WT" "$sz" e -so "${sz_args[@]}" "$z" > /dev/null 2> "$tmp/ref.time"
        tv "$dotnet" "$dll" d64-dec "$raw" --size "$size" --reps "$reps" --expect "$path" > "$tmp/our.out" 2> "$tmp/our.time"
        dec_row "$f" deflate64 "$L" "$src" "$size" "$t" "$su" "$tmp/ref.time" "$tmp/our.out" "$tmp/our.time"
      done
    done
  done
  ;;
ppmd-enc)
  [ -n "$sz" ] || { echo "no 7zz or 7z on PATH" >&2; exit 1; }
  [ -n "$levels" ] || levels="1 5 9"
  header "$("$sz" 2>&1 | sed -n 2p), 7-Zip -t7z -m0=PPMd -mmt=1, start-up subtracted"
  printf 'x' > "$tmp/tiny"
  su=""; for _ in $(seq 1 10); do rm -f "$tmp/tiny.7z"; t="$(walltime "$sz" a -t7z -m0=PPMd -mx=5 -mmt=1 "${sz_args[@]}" "$tmp/tiny.7z" "$tmp/tiny")"; su="$(awk -v a="$su" -v t="$t" 'BEGIN { print (a == "" || t < a) ? t : a }')"; done; echo "# start-up: $su s"
  printf 'file\tcodec\tlevel\tref_size\tours_size\tsize_ratio\tref_MBps\tours_MBps\tours_med\tours_min\ttime_ratio\tref_peak_KB\tours_peak_KB\tinterop\tverdict\n'
  for f in $files; do
    path="$(find_file "$f")"; size="$(fsize "$path")"; b="$(basename "$path")"
    for L in $levels; do
      z="$cache/7z-ppmd-$b-$L.7z"; rm -f "$z"
      tv perl -e "$WT" "$sz" a -t7z -m0=PPMd -mx="$L" -mmt=1 "${sz_args[@]}" "$z" "$path" > /dev/null 2> "$tmp/ref.time"
      t="$(sed -n 's/^WALL //p' "$tmp/ref.time")"
      for _ in $(seq 2 "$reps"); do rm -f "$tmp/r.7z"; t2="$(walltime "$sz" a -t7z -m0=PPMd -mx="$L" -mmt=1 "${sz_args[@]}" "$tmp/r.7z" "$path")"; t="$(awk -v a="$t" -v b="$t2" 'BEGIN { print (b < a) ? b : a }')"; done
      rm -f "$tmp/r.7z"
      rs="$(ppmd_packed "$z")"
      tv "$dotnet" "$dll" ppmd-enc "$path" "$L" --reps "$reps" --out "$cache/ours-ppmd-$b-$L.raw" > "$tmp/our.out" 2> "$tmp/our.time"
      "$dotnet" "$dll" ppmd-7z "$path" "$L" --out "$cache/ours-ppmd-$b-$L.7z" > /dev/null
      interop=ok
      "$sz" t "${sz_args[@]}" "$cache/ours-ppmd-$b-$L.7z" > /dev/null 2>&1 || interop=FAIL
      ppmd_raw "$z" "$tmp/ref.raw"
      [ "$(ppmd_props "$z")" = "$(ppmd_expect "$L")" ] || { echo "# $f -mx=$L: 7-Zip used order/memory '$(ppmd_props "$z")', the level table says '$(ppmd_expect "$L")' (7-Zip shrinks the model for small inputs); row fails" >&2; interop=FAIL; }
      "$dotnet" "$dll" ppmd-dec "$tmp/ref.raw" "$L" $(ppmd_decargs "$L" "$(ppmd_props "$z")") --size "$size" --reps 0 --expect "$path" > /dev/null 2>&1 || interop=FAIL
      os="$(field size < "$tmp/our.out")"; rr="$(rate "$size" "$t" "$su")"; orate="$(field comp_MBps < "$tmp/our.out")"
      sr="$(ratio "$os" "$rs" 4)"; tr="$(ratio "$rr" "$orate" 3)"; v="$(verdict_enc "$sr" "$tr")"; [ "$interop" = ok ] || v=FAIL
      printf '%s\tppmd7\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$f" "$L" "$rs" "$os" "$sr" "$rr" "$orate" "$(field comp_MBps_med < "$tmp/our.out")" "$(field comp_MBps_min < "$tmp/our.out")" "$tr" "$(peak "$tmp/ref.time")" "$(peak "$tmp/our.time")" "$interop" "$v"
    done
  done
  ;;
ppmd-dec)
  [ -n "$sz" ] || { echo "no 7zz or 7z on PATH" >&2; exit 1; }
  [ -n "$levels" ] || levels="1 5 9"
  header "$("$sz" 2>&1 | sed -n 2p), 7-Zip e -so, start-up subtracted"
  printf 'x' > "$tmp/tiny"; "$dotnet" "$dll" ppmd-7z "$tmp/tiny" 5 --out "$tmp/tiny.7z" > /dev/null
  su="$(besttime 20 "$sz" e -so "${sz_args[@]}" "$tmp/tiny.7z")"; echo "# start-up: $su s"
  dec_header
  for f in $files; do
    path="$(find_file "$f")"; size="$(fsize "$path")"; b="$(basename "$path")"
    for L in $levels; do
      for src in ref ours; do
        if [ "$src" = ref ]; then
          z="$cache/7z-ppmd-$b-$L.7z"
          [ -s "$z" ] || "$sz" a -t7z -m0=PPMd -mx="$L" -mmt=1 "${sz_args[@]}" "$z" "$path"
          raw="$tmp/ref.raw"; ppmd_raw "$z" "$raw"
        else
          z="$cache/ours-ppmd-$b-$L.7z"; raw="$cache/ours-ppmd-$b-$L.raw"
          if [ ! -s "$z" ]; then "$dotnet" "$dll" ppmd-enc "$path" "$L" --reps 0 --out "$raw" > /dev/null; "$dotnet" "$dll" ppmd-7z "$path" "$L" --out "$z" > /dev/null; fi
        fi
        t="$(besttime "$reps" "$sz" e -so "${sz_args[@]}" "$z")"
        tv perl -e "$WT" "$sz" e -so "${sz_args[@]}" "$z" > /dev/null 2> "$tmp/ref.time"
        tv "$dotnet" "$dll" ppmd-dec "$raw" "$L" $(ppmd_decargs "$L" "$([ "$src" = ref ] && ppmd_props "$z")") --size "$size" --reps "$reps" --expect "$path" > "$tmp/our.out" 2> "$tmp/our.time"
        dec_row "$f" ppmd7 "$L" "$src" "$size" "$t" "$su" "$tmp/ref.time" "$tmp/our.out" "$tmp/our.time"
      done
    done
  done
  ;;
lzx-dec)
  header "$("$cabextract" --version 2>&1 | head -1), cabextract -q -p, start-up subtracted"
  printf 'x' > "$tmp/tiny"; "$dotnet" "$dll" lzx-mkcab "$tmp/tiny" --out "$tmp/tiny.cab" > /dev/null
  su="$(besttime 20 "$cabextract" -q -p "$tmp/tiny.cab")"; echo "# start-up: $su s"
  dec_header
  for f in $files; do
    path="$(find_file "$f")"; size="$(fsize "$path")"; b="$(basename "$path")"
    cab="$cache/lzx-$b.cab"
    [ -s "$cab" ] || "$dotnet" "$dll" lzx-mkcab "$path" --out "$cab" > /dev/null
    # The reference must read the cabinet byte-exact before it is timed; otherwise the row is meaningless.
    if ! "$cabextract" -q -p "$cab" | cmp -s - "$path"; then echo "# $f: cabextract did not extract the test cabinet byte-exact" >&2; printf '%s\tlzx\t21\ttest-encoder\t0\t0\t0\t0\t0\t0\t0\tFAIL\n' "$f"; continue; fi
    t="$(besttime "$reps" "$cabextract" -q -p "$cab")"
    tv perl -e "$WT" "$cabextract" -q -p "$cab" > /dev/null 2> "$tmp/ref.time"
    tv "$dotnet" "$dll" lzx-dec "$cab" --reps "$reps" --expect "$path" > "$tmp/our.out" 2> "$tmp/our.time"
    dec_row "$f" lzx 21 test-encoder "$size" "$t" "$su" "$tmp/ref.time" "$tmp/our.out" "$tmp/our.time"
  done
  ;;
*) echo "unknown or missing --mode" >&2; exit 2 ;;
esac
echo "# load after: $(load)"
