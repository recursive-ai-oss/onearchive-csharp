#!/usr/bin/env bash
# Brotli encoder rows: our encoder (one call, in memory) against the brotli command-line tool at the same quality and window.
#
#   bar.sh [--files "dickens xml x-ray mr ooffice enwik8"] [--levels "0 1 ... 9"] [--reps 3] [--slow-files "xml"]
#
# For each file and quality it prints the compressed sizes, the best rates in MB/s and the verdict (our size at most 1.01 times the
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
find_file() { for c in "$corpora/$1" "$corpora/x/$1" "$corpora/silesia/$1" "$corpora/enwik8/$1"; do [ -f "$c" ] && { echo "$c"; return; }; done; echo "missing $1" >&2; return 1; }
load() { cut -d' ' -f1-3 /proc/loadavg; }
dll="$("$here/build.sh")" || exit 1
echo "# cpu: $(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2 | xargs), cores: $(nproc), load before: $(load), $("$dotnet" --version) SDK, brotli $("$tool" --version 2>&1 | head -1)"
printf 'file\tq\tours_size\ttool_size\tsize_ratio\tours_MBps\ttool_MBps\tspeed_ratio\tverdict\n'
row() { # file path q reps
  "$dotnet" "$dll" "$2" "$3" --reps "$4" --tool "$tool" | awk -v f="$1" -v q="$3" '
    /^ours/ { os = $5; orate = $9 }
    /^tool/ { ts = $5; trate = $9 }
    END { sr = os / ts; sp = trate / orate; printf "%s\t%s\t%s\t%s\t%.4f\t%s\t%s\t%.2f\t%s\n", f, q, os, ts, sr, orate, trate, sp, (sr <= 1.01 && sp <= 1.15) ? "pass" : "FAIL" }'
}
for f in $files; do
  path="$(find_file "$f")" || continue
  for q in $levels; do row "$f" "$path" "$q" "$reps"; done
done
for f in $slow; do
  path="$(find_file "$f")" || continue
  for q in 10 11; do row "$f" "$path" "$q" 1; done
done
echo "# load after: $(load)"
