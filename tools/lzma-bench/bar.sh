#!/usr/bin/env bash
# xz, lzip and 7z bar rows: our encoders, decoders and 7z writer against the xz, lzip and 7-Zip command-line tools.
#
#   bar.sh --mode enc|dec|sevenz --codec xz|lzip [--files "silesia.tar enwik8"] [--levels "0 1 ... 9"] [--reps 3]
#
# enc:    ours (one call, in memory) against `xz -T1 -N -c` / `lzip -N -c` at the same level; compressed size and rate.
# dec:    ours against `xz -d -c` / `lzip -d -c`, each on the tool's own output and on ours, at the given levels (default 0 6 9).
# sevenz: sizes only: our SevenZipWriter at level N against `7zz a -mx=N -mmt=1` on a one-file archive (--codec is ignored).
# The tools are command-line programs, so their time is the wall time of a run with output to /dev/null (the first compress run
# writes the file the size and the decode rows use) minus the start-up of the same tool on a one-byte input, best of --reps.
# Compressed files are kept in $ONEARCHIVE_BENCH_TMP/lzma-cache for the length of the job, so later steps reuse them.
# Environment: BENCH_DLL (built LzmaBench.dll), ONEARCHIVE_CORPORA, XZ, LZIP, SEVENZ (default xz, lzip, 7zz then 7z on PATH),
# DOTNET, ONEARCHIVE_BENCH_TMP or TMPDIR (use RAM). A timing run: take the shared timing lock. Method: docs/bench-method.md, section LZMA.
# Pass rule: our size at most 1.01 times the tool's and the tool's rate at most 1.15 times ours.
set -euo pipefail
corpora="${ONEARCHIVE_CORPORA:-$HOME/onearchive/corpora}"
dll="${BENCH_DLL:?set BENCH_DLL to LzmaBench.dll}"
dotnet="${DOTNET:-dotnet}"
files="silesia.tar enwik8"; levels=""; reps=3; mode=enc; codec=xz
while [ $# -gt 0 ]; do
  case "$1" in
    --files) files="$2"; shift 2 ;;
    --levels) levels="$2"; shift 2 ;;
    --reps) reps="$2"; shift 2 ;;
    --mode) mode="$2"; shift 2 ;;
    --codec) codec="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
find_file() { [ -f "$1" ] && { echo "$1"; return; }; for c in "$corpora/$1" "$corpora/enwik8/$1" "$corpora/silesia/$1"; do [ -f "$c" ] && { echo "$c"; return; }; done; echo "missing $1" >&2; exit 1; }
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

xz="${XZ:-xz}"; lzip="${LZIP:-lzip}"
sz="${SEVENZ:-$(command -v 7zz || command -v 7z || true)}"
cache="${ONEARCHIVE_BENCH_TMP:-${TMPDIR:-/tmp}}/lzma-cache"; mkdir -p "$cache"
tmp="$(mktemp -d "${TMPDIR:-/tmp}/lzmabar.XXXXXX")"; trap 'rm -rf "$tmp"' EXIT
case "$codec" in xz) tool="$xz"; ext=xz; targs() { echo "-T1 -$1 -c"; } ;; lzip) tool="$lzip"; ext=lz; targs() { echo "-$1 -c"; } ;; *) echo "unknown codec" >&2; exit 2 ;; esac
if [ -z "$levels" ]; then
  case "$mode" in dec) levels="0 6 9" ;; *) levels="0 1 2 3 4 5 6 7 8 9" ;; esac
fi
verdict() { awk -v s="$1" -v t="$2" 'BEGIN { print (s <= 1.01 && t <= 1.15) ? "pass" : "FAIL" }'; }
startup() { printf 'x' > "$tmp/tiny"; if [ "$1" = enc ]; then WT_IN="$tmp/tiny" besttime 20 "$tool" $(targs 0); else "$tool" $(targs 0) < "$tmp/tiny" > "$tmp/tiny.c"; WT_IN="$tmp/tiny.c" besttime 20 "$tool" -d -c; fi; }

if [ "$mode" = sevenz ]; then
  [ -n "$sz" ] || { echo "no 7zz or 7z on PATH" >&2; exit 1; }
  echo "# cpu: $(cpu), cores: $(cores), load before: $(load), $("$dotnet" --version | head -1) SDK, mode=sevenz (sizes only), reference: $("$sz" 2>&1 | sed -n 2p)"
  printf 'file\tlevel\tref_size\tours_size\tsize_ratio\tverdict\n'
  for f in $files; do
    path="$(find_file "$f")"
    for L in ${levels_7z:-1 5 9}; do
      rm -f "$tmp/ref.7z"; "$sz" a -mx="$L" -mmt=1 -bd -bso0 -bsp0 "$tmp/ref.7z" "$path" > /dev/null
      "$dotnet" "$dll" sevenz "$path" "$L" --out "$tmp/ours.7z" > /dev/null
      rs="$(wc -c < "$tmp/ref.7z" | tr -d ' ')"; os="$(wc -c < "$tmp/ours.7z" | tr -d ' ')"
      sr="$(ratio "$os" "$rs" 4)"
      printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$f" "$L" "$rs" "$os" "$sr" "$(awk -v s="$sr" 'BEGIN { print (s <= 1.01) ? "pass" : "FAIL" }')"
    done
  done
  echo "# load after: $(load)"
  exit 0
fi

echo "# cpu: $(cpu), cores: $(cores), load before: $(load), $("$dotnet" --version | head -1) SDK, reps=$reps, mode=$mode, codec=$codec, reference: $("$tool" --version 2>&1 | head -1), start-up subtracted"
su="$(startup "$mode")"; echo "# start-up: $su s"
# ref_file <file> <level>: the tool's compressed file in the cache, made untimed when no compress step has made it yet.
ref_file() { local c="$cache/$codec-$(basename "$1")-$2.$ext"; [ -s "$c" ] || { WT_IN="$1" WT_OUT="$c" walltime "$tool" $(targs "$2") > /dev/null; }; echo "$c"; }
ours_file() { local c="$cache/ours-$codec-$(basename "$1")-$2.$ext"; [ -s "$c" ] || "$dotnet" "$dll" enc "$codec" "$1" "$2" --reps 0 --out "$c" > /dev/null; echo "$c"; }

if [ "$mode" = dec ]; then
  printf 'file\tcodec\tlevel\tsource\tref_dec_MBps\tours_dec_MBps\tours_dec_med\tours_dec_min\ttime_ratio\tref_peak_KB\tours_peak_KB\tverdict\n'
  for f in $files; do
    path="$(find_file "$f")"; size="$(wc -c < "$path" | tr -d ' ')"
    for L in $levels; do
      for src in ref ours; do
        if [ "$src" = ref ]; then in="$(ref_file "$path" "$L")"; else in="$(ours_file "$path" "$L")"; fi
        t="$(WT_IN="$in" besttime "$reps" "$tool" -d -c)"
        r="$(awk -v n="$size" -v t="$t" -v s="$su" 'BEGIN { d = t - s; if (d < 1e-6) d = 1e-6; printf "%.1f", n / 1e6 / d }')"
        WT_IN="$in" tv perl -e "$WT" "$tool" -d -c > /dev/null 2> "$tmp/ref.time"
        tv "$dotnet" "$dll" dec "$codec" "$in" --reps "$reps" > "$tmp/our.out" 2> "$tmp/our.time"
        o="$(field dec_MBps < "$tmp/our.out")"; om="$(field dec_MBps_med < "$tmp/our.out")"; on="$(field dec_MBps_min < "$tmp/our.out")"
        tr="$(ratio "$r" "$o" 3)"
        printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$f" "$codec" "$L" "$src" "$r" "$o" "$om" "$on" "$tr" "$(peak "$tmp/ref.time")" "$(peak "$tmp/our.time")" "$(awk -v t="$tr" 'BEGIN { print (t <= 1.15) ? "pass" : "FAIL" }')"
      done
    done
  done
  echo "# load after: $(load)"
  exit 0
fi

printf 'file\tcodec\tlevel\tref_size\tours_size\tsize_ratio\tref_MBps\tours_MBps\tours_med\tours_min\ttime_ratio\tref_peak_KB\tours_peak_KB\tverdict\n'
for f in $files; do
  path="$(find_file "$f")"; size="$(wc -c < "$path" | tr -d ' ')"
  for L in $levels; do
    c="$cache/$codec-$(basename "$path")-$L.$ext"; rm -f "$c"
    # The first run writes the cache file (and gives the peak memory); the other reps go to /dev/null.
    WT_IN="$path" WT_OUT="$c" tv perl -e "$WT" "$tool" $(targs "$L") > /dev/null 2> "$tmp/ref.time"
    t1="$(sed -n 's/^WALL //p' "$tmp/ref.time")"
    t="$t1"
    if [ "$reps" -gt 1 ]; then t2="$(WT_IN="$path" besttime $((reps - 1)) "$tool" $(targs "$L"))"; t="$(awk -v a="$t1" -v b="$t2" 'BEGIN { print (b < a) ? b : a }')"; fi
    rm_="$(awk -v n="$size" -v t="$t" -v s="$su" 'BEGIN { d = t - s; if (d < 1e-6) d = 1e-6; printf "%.2f", n / 1e6 / d }')"
    rs="$(wc -c < "$c" | tr -d ' ')"
    oc="$cache/ours-$codec-$(basename "$path")-$L.$ext"
    tv "$dotnet" "$dll" enc "$codec" "$path" "$L" --reps "$reps" --out "$oc" > "$tmp/our.out" 2> "$tmp/our.time"
    os="$(field size < "$tmp/our.out")"; om="$(field comp_MBps < "$tmp/our.out")"; omed="$(field comp_MBps_med < "$tmp/our.out")"; omin="$(field comp_MBps_min < "$tmp/our.out")"
    sr="$(ratio "$os" "$rs" 4)"; tr="$(ratio "$rm_" "$om" 3)"
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$f" "$codec" "$L" "$rs" "$os" "$sr" "$rm_" "$om" "$omed" "$omin" "$tr" "$(peak "$tmp/ref.time")" "$(peak "$tmp/our.time")" "$(verdict "$sr" "$tr")"
  done
done
echo "# load after: $(load)"
