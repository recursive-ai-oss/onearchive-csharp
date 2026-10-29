#!/usr/bin/env bash
# Brotli encoder rows: our encoder (one call, in memory) against the brotli command-line tool at the same quality and window.
#
#   bar.sh [--files "dickens xml x-ray mr ooffice enwik8"] [--levels "0 1 ... 9"] [--reps 3] [--slow-files "xml"]
#
# For each file and quality it prints the compressed sizes, the best rates in MB/s, both peak memories and the verdict (our size at most 1.01 times the
# tool's and the tool's rate at most 1.15 times ours). The tool is timed end to end (file to file) minus its start-up on an empty file;
# the method is in tools/brotli-bench/Program.cs. Qualities 10 and 11 run on --slow-files only, with one repeat.
# Environment: ONEARCHIVE_CORPORA (default ~/onearchive/corpora), BROTLI (the tool; default brotli on PATH), DOTNET, BAR_LOCK_FILE
# (a timing run takes the shared timing lock itself and exits 3 if it is held; BAR_LOCK_FILE= to run without one).
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
corpora="${ONEARCHIVE_CORPORA:-$HOME/onearchive/corpora}"
dotnet="${DOTNET:-dotnet}"
tool="${BROTLI:-$(command -v brotli)}"
files="dickens xml x-ray mr ooffice enwik8"; levels="0 1 2 3 4 5 6 7 8 9"; reps=3; slow="xml"
while [ $# -gt 0 ]; do
  case "$1" in
    --files) files="$2"; shift 2 ;;
    --levels) levels="$2"; shift 2 ;;
    --reps) reps="$2"; shift 2 ;;
    --slow-files) slow="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
lock="${BAR_LOCK_FILE-$HOME/WORKER-BENCH.lock}"
if [ -n "$lock" ]; then
  set -o noclobber
  echo "onearchive brotli bar $(date +%H:%M) $(whoami)" > "$lock" || { echo "timing lock held: $(cat "$lock")" >&2; exit 3; }
  set +o noclobber
  trap 'rm -f "$lock"; echo "RELEASED $(date +%H:%M:%S)"' EXIT
fi
# BAR_QUICK=1 cuts each input to its first 1 MiB and uses one repeat: it proves the script runs, and says nothing about speed.
quick_dir=""
if [ -n "${BAR_QUICK:-}" ]; then reps=1; quick_dir="$(mktemp -d "${TMPDIR:-/tmp}/brotlibar.XXXXXX")"; fi
find_file() {
  for c in "$corpora/$1" "$corpora/x/$1" "$corpora/silesia/$1" "$corpora/enwik8/$1"; do
    if [ -f "$c" ]; then
      if [ -n "$quick_dir" ]; then head -c 1048576 "$c" > "$quick_dir/$(basename "$c")"; echo "$quick_dir/$(basename "$c")"; else echo "$c"; fi
      return
    fi
  done
  echo "missing $1" >&2; return 1
}
load() { cut -d' ' -f1-3 /proc/loadavg; }
dll="$("$here/build.sh")" || exit 1
echo "# cpu: $(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2 | xargs), cores: $(nproc), load before: $(load), $("$dotnet" --version) SDK, brotli $("$tool" --version 2>&1 | head -1)"
printf 'file\tq\tours_size\ttool_size\tsize_ratio\tours_MBps\ttool_MBps\tspeed_ratio\tours_peak_KB\ttool_peak_KB\tverdict\n'
row() { # file path q reps
  "$dotnet" "$dll" "$2" "$3" --reps "$4" --tool "$tool" | awk -v f="$1" -v q="$3" '
    function peak(l,   a) { if (match(l, /peak_KB=[0-9]+/)) return substr(l, RSTART + 8, RLENGTH - 8); return 0 }
    /^ours/ { os = $5; orate = $9; op = peak($0) }
    /^tool/ { ts = $5; trate = $9; tp = peak($0) }
    END { sr = os / ts; sp = trate / orate; printf "%s\t%s\t%s\t%s\t%.4f\t%s\t%s\t%.2f\t%s\t%s\t%s\n", f, q, os, ts, sr, orate, trate, sp, op, tp, (sr <= 1.01 && sp <= 1.15) ? "pass" : "FAIL" }'
}
for f in $files; do
  path="$(find_file "$f")" || continue
  for q in $levels; do row "$f" "$path" "$q" "$reps"; done
done
for f in $slow; do
  path="$(find_file "$f")" || continue
  for q in 10 11; do row "$f" "$path" "$q" 1; done
done
[ -n "$quick_dir" ] && rm -rf "$quick_dir"
echo "# load after: $(load)"
