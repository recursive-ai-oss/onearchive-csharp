#!/usr/bin/env bash
# sweep.sh game kind size [extra tool args...]: ours with fixed k/d grid vs tool fastcover at the same k/d (level 3 totals)
set -eu
export LC_ALL=C
EXTRA="${EXTRA:-}"; g=$1; k=$2; sz=$3; shift 3
zstd="$ONEARCHIVE_ZSTD"; dll="$TRAIN_DLL"; work="$ONEARCHIVE_BENCH_TMP"; mkdir -p "$work"
c=~/onearchive/corpora/game
ls $c/seed1/$g/$k | sort | head -n 20000 | sed "s#^#$c/seed1/$g/$k/#" > $work/tr.list
ls $c/seed2/$g/$k | sort | head -n 5000 | sed "s#^#$c/seed2/$g/$k/#" > $work/te.list
tot() { "$zstd" -b3 -e3 -i0 -T1 -D "$1" $(cat $work/te.list) 2>&1 | tr '\r' '\n' | grep MB/s | tail -1 | awk '{for (i=1;i<=NF;i++) if ($i=="->") print $(i+1)}'; }
"$zstd" --train -q --maxdict=$sz -o $work/ref.dict $(cat $work/tr.list) >/dev/null 2>&1
echo "tool default: $(tot $work/ref.dict)"
dotnet $dll $work/rb.dict --size $sz --dir $c/seed1/$g/$k --files 20000 --rebuild $work/ref.dict >/dev/null
echo "tool default content, our tables: $(tot $work/rb.dict)"
KDS="${KDS:-32,8 64,8 128,8 256,8 512,8 1024,8 2048,8 128,6 512,6 1024,6}"
for kd in $KDS; do
  kk=${kd%,*}; dd=${kd#*,}
  dotnet $dll $work/o.dict --size $sz --dir $c/seed1/$g/$k --files 20000 --k $kk --d $dd $EXTRA >/dev/null
  "$zstd" --train-fastcover=k=$kk,d=$dd -q --maxdict=$sz -o $work/f.dict $(cat $work/tr.list) >/dev/null 2>&1 || true
  echo "k=$kk d=$dd ours=$(tot $work/o.dict) toolfast=$(tot $work/f.dict)"
done
