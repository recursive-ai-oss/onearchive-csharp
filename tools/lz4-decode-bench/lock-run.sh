#!/usr/bin/env bash
# Runs the decode timing comparison under the shared timing lock: waits for the lock, takes it atomically, and removes it
# on exit. Usage: lock-run.sh <expected-minutes> <command...>
set -u
minutes="$1"; shift
for i in $(seq 1 360); do
  if ( set -o noclobber; echo "onearchive lz4-decode $(date -Is) end +${minutes}min" > "$HOME/WORKER-BENCH.lock" ) 2>/dev/null; then
    trap 'rm -f "$HOME/WORKER-BENCH.lock"' EXIT
    echo "# lock taken $(date -Is) after $((i - 1)) waits; load $(cut -d' ' -f1-3 /proc/loadavg)"
    "$@"
    status=$?
    echo "# lock released $(date -Is); load $(cut -d' ' -f1-3 /proc/loadavg)"
    exit $status
  fi
  sleep 30
done
echo "gave up waiting for the lock" >&2
exit 1
