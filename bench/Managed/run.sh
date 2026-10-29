#!/usr/bin/env bash
# Compares our codecs with the fastest implementations written entirely in managed code,
# prints one table, and writes the detail and summary files.
#
#   bench/Managed/run.sh [--only row[,row...]] [--skip-training]
#
# Environment:
#   MANAGED_BENCH_CORPORA  directory holding silesia.tar (default $HOME/onearchive/corpora)
#   MANAGED_BENCH_OUT      output directory (default ./managed-out); gets managed-detail.tsv,
#                          training.tsv, managed.tsv and managed.md
#   MANAGED_BENCH_CPUS     cpu list for taskset (default 0,1); runs are single-threaded
#                          except the two-worker zstd row
# Needs xz, zstd, bzip2 and brotli on PATH to make the decode inputs (run as black boxes).
# Timings are indicative: on a busy host the load is recorded next to each row, and the
# candidates of a row run interleaved, best of three (two for the slow rows).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="${MANAGED_BENCH_OUT:-$PWD/managed-out}"
cpus="${MANAGED_BENCH_CPUS:-0,1}"
export MANAGED_BENCH_CORPORA="${MANAGED_BENCH_CORPORA:-$HOME/onearchive/corpora}"
mkdir -p "$out"
only=""
training=1
while [ $# -gt 0 ]; do
  case "$1" in
    --only) only="$2"; shift 2 ;;
    --skip-training) training=0; shift ;;
    *) echo "unknown argument $1" >&2; exit 2 ;;
  esac
done

dotnet build "$here/Managed.csproj" -c Release -v quiet -clp:NoSummary
run() {
  local cmd=(dotnet run --no-build -c Release --project "$here/Managed.csproj" --)
  if command -v taskset >/dev/null; then taskset -c "$cpus" "${cmd[@]}" "$@"; else "${cmd[@]}" "$@"; fi
}

rm -f "$out/managed-detail.tsv"
if [ -n "$only" ]; then run --only "$only" --out "$out/managed-detail.tsv"; else run --out "$out/managed-detail.tsv"; fi

if [ "$training" = 1 ] && [ -z "$only" ]; then
  : > "$out/training.tsv"
  for i in 1 2; do
    for impl in ours zstdsharp; do run train "$impl" | tee -a "$out/training.tsv"; done
  done
fi

run table "$out/managed-detail.tsv" "$out/managed.tsv" | tee "$out/managed.md"
