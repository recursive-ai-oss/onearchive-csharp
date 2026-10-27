#!/usr/bin/env bash
# Runs a timing command under the shared timing lock: waits for the lock, takes it atomically, and removes it on exit.
# Usage: lock-run.sh <expected-minutes> <command...>
# The lock file is $BENCH_LOCK, default ~/WORKER-BENCH.lock (use ~/onearchive/BENCH.lock on the Mac).
set -u
minutes="$1"; shift
lock="${BENCH_LOCK:-$HOME/WORKER-BENCH.lock}"
load() { if [ -r /proc/loadavg ]; then cut -d' ' -f1-3 /proc/loadavg; else uptime | sed 's/.*load averages*: //; s/,//g'; fi; }
for i in $(seq 1 360); do
  if ( set -o noclobber; echo "onearchive lz4-enc $(date) end +${minutes}min" > "$lock" ) 2>/dev/null; then
    trap 'rm -f "$lock"' EXIT
    echo "# lock taken $(date) after $((i - 1)) waits; load $(load)"
    "$@"
    status=$?
    echo "# lock released $(date); load $(load)"
    exit $status
  fi
  sleep 30
done
echo "gave up waiting for the lock" >&2
exit 1
