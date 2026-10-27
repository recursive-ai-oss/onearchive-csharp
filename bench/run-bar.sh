#!/usr/bin/env bash
# Runs the bar for a codec: builds the bar runner, takes the bench-host lock, checks the
# machine is otherwise quiet, runs, writes CSV and markdown results.
#
#   bench/run-bar.sh <codec[,codec]> <levels|all> <file[,file]> [extra Bar run options]
#
#   bench/run-bar.sh zstd 1,3,19 silesia/dickens,silesia/mozilla
#   bench/run-bar.sh deflate all silesia/dickens --runs 3
#
# Files without a leading / are relative to $ONEARCHIVE_HOME/corpora. "all" uses each
# implementation's own level list. Results land in $BAR_OUT (default
# $ONEARCHIVE_HOME/results). Environment:
#   ONEARCHIVE_HOME   state directory (default $HOME/onearchive): tools/, corpora/, dotnet/
#   BAR_LOCK          lock file (default: ~/WORKER-BENCH.lock on a shared bench host,
#                     $ONEARCHIVE_HOME/BENCH.lock elsewhere); set BAR_LOCK_MODE=shared to use ~/WORKER-BENCH.lock
#   BAR_EXPECT_MIN    minutes written into the lock as the expected duration (default 30)
#   BAR_WAIT_MIN      minutes to wait for the lock and a quiet machine (default 120)
#   BAR_BUSY_MAX      most busy cores other work may use while we run (default 2)
set -euo pipefail

if [ $# -lt 3 ]; then sed -n '2,12p' "$0"; exit 2; fi
codec="$1"; levels="$2"; files="$3"; shift 3

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
home="${ONEARCHIVE_HOME:-$HOME/onearchive}"
out="${BAR_OUT:-$home/results}"
mkdir -p "$out"

dn="$home/dotnet"
if [ ! -x "$dn/dotnet" ]; then dn="${DOTNET_ROOT:-$HOME/.dotnet}"; fi
export DOTNET_ROOT="$dn" PATH="$dn:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
export ONEARCHIVE_HOME="$home" ONEARCHIVE_ARTIFACTS="${ONEARCHIVE_ARTIFACTS:-$home/artifacts-bar}"

# Resolve file names against the corpora directory.
resolved=""
IFS=',' read -ra fl <<<"$files"
for f in "${fl[@]}"; do
  case "$f" in /*) p="$f" ;; *) p="$home/corpora/$f" ;; esac
  [ -f "$p" ] || { echo "no such file: $p" >&2; exit 2; }
  resolved="${resolved:+$resolved,}$p"
done

# Build before taking the lock; building is not a measurement.
cd "$root"
dotnet build bench/Bar/Bar.csproj -c Release -v quiet -clp:NoSummary
dll="$ONEARCHIVE_ARTIFACTS/bin/Bar/release/OneArchive.Bar.dll"
[ -f "$dll" ] || { echo "Bar build output not found at $dll" >&2; exit 1; }

if [ -n "${BAR_LOCK:-}" ]; then lock="$BAR_LOCK"
elif [ "$(uname -s)" = Linux ] || [ "${BAR_LOCK_MODE:-}" = shared ]; then lock="$HOME/WORKER-BENCH.lock"
else lock="$home/BENCH.lock"; fi

now() { date '+%Y-%m-%dT%H:%M:%S%z'; }
expect_min="${BAR_EXPECT_MIN:-30}"
wait_s=$(( ${BAR_WAIT_MIN:-120} * 60 ))
have_lock=0
release() { if [ $have_lock -eq 1 ]; then rm -f "$lock"; fi; }
trap release EXIT

# Busy cores over a few seconds, from the whole-machine CPU counters.
busy_cores() {
  if [ -r /proc/stat ]; then
    read -r _ u1 n1 s1 i1 w1 q1 sq1 _ < /proc/stat; sleep 3; read -r _ u2 n2 s2 i2 w2 q2 sq2 _ < /proc/stat
    local t1=$((u1+n1+s1+i1+w1+q1+sq1)) t2=$((u2+n2+s2+i2+w2+q2+sq2)) cores
    cores=$(nproc)
    awk -v dt=$((t2-t1)) -v di=$(( (i2+w2)-(i1+w1) )) -v c="$cores" 'BEGIN{ if(dt<=0){print 0}else{printf "%.1f", c*(1-di/dt)} }'
  else
    local idle cores
    idle=$(top -l 2 -s 1 -n 0 | grep 'CPU usage' | tail -1 | sed 's/.* \([0-9.]*\)% idle.*/\1/')
    cores=$(sysctl -n hw.ncpu)
    awk -v i="$idle" -v c="$cores" 'BEGIN{ printf "%.1f", c*(1-i/100) }'
  fi
}

deadline=$(( $(date +%s) + wait_s ))
set -o noclobber
while :; do
  start="$(now)"
  end="$(date -d "+${expect_min} minutes" '+%Y-%m-%dT%H:%M:%S%z' 2>/dev/null || date -v+"${expect_min}"M '+%Y-%m-%dT%H:%M:%S%z')"
  if echo "onearchive bar $start $end" > "$lock" 2>/dev/null; then
    have_lock=1
    # The lock is ours; the machine must also be quiet (another project's heavy job may not hold the lock).
    busy="$(busy_cores)"
    if awk -v b="$busy" -v m="${BAR_BUSY_MAX:-2}" 'BEGIN{exit !(b<=m)}'; then break; fi
    echo "machine busy ($busy cores in use by other work); releasing the lock and waiting"
    rm -f "$lock"; have_lock=0
  else
    echo "lock held: $(cat "$lock" 2>/dev/null || echo '?')"
  fi
  [ "$(date +%s)" -lt "$deadline" ] || { echo "gave up waiting for the lock and a quiet machine" >&2; exit 3; }
  sleep 60
done
set +o noclobber

echo "lock: $lock = $(cat "$lock")"
echo "busy cores before: $busy; load: $(cat /proc/loadavg 2>/dev/null || sysctl -n vm.loadavg)"

lv=()
if [ "$levels" != all ]; then lv=(--levels "$levels"); fi
dotnet "$dll" run --codec "$codec" ${lv[@]+"${lv[@]}"} --files "$resolved" --out "$out" --pins "$root/tools/pins.tsv" "$@"

echo "busy cores after: $(busy_cores); load: $(cat /proc/loadavg 2>/dev/null || sysctl -n vm.loadavg)"
echo "results in $out"
