#!/usr/bin/env bash
# Runs the columns on another machine over ssh and fetches the manifests back.
#
#   tools/hashmatrix/remote.sh --target user@host --prefix NAME --out DIR [--columns LIST]
#                              [--dotnet DIR] [--node DIR] [--remote-dir DIR] [--include-opt-in]
#
# The working tree (tracked and new, non-ignored files) is copied to --remote-dir
# (default ~/onearchive/hashmatrix on the target), columns.sh runs there, and DIR/NAME-*.tsv comes back.
# Extra ssh options (a key file, a port) go in HASHMATRIX_SSH_OPTS. Paths on the target that start
# with ~ are expanded by the target's shell. --dotnet defaults to ~/onearchive/dotnet (see install-dotnet.sh).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
target="" prefix="" out="" columns="" dotnet_dir='~/onearchive/dotnet' node_dir='' remote_dir='~/onearchive/hashmatrix' extra=""
while [ $# -gt 0 ]; do
  case "$1" in
    --target) target="$2"; shift 2 ;;
    --prefix) prefix="$2"; shift 2 ;;
    --out) out="$2"; shift 2 ;;
    --columns) columns="$2"; shift 2 ;;
    --dotnet) dotnet_dir="$2"; shift 2 ;;
    --node) node_dir="$2"; shift 2 ;;
    --remote-dir) remote_dir="$2"; shift 2 ;;
    --include-opt-in) extra="--include-opt-in"; shift ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
[ -n "$target" ] && [ -n "$prefix" ] && [ -n "$out" ] || { echo "--target, --prefix and --out are required" >&2; exit 2; }

# shellcheck disable=SC2206
ssh_opts=(-o BatchMode=yes ${HASHMATRIX_SSH_OPTS:-})
mkdir -p "$out"

cd "$root"
# Fresh copy of the tree each run, so a deleted file cannot linger on the target.
(git ls-files -z --cached --others --exclude-standard) | tar --null -T - -cf - |
  ssh "${ssh_opts[@]}" "$target" "rm -rf $remote_dir/src && mkdir -p $remote_dir/src && tar -xf - -C $remote_dir/src"

cmd="cd $remote_dir/src && nice tools/hashmatrix/columns.sh --out $remote_dir/out --prefix $prefix --dotnet $dotnet_dir ${columns:+--columns $columns} ${node_dir:+--node $node_dir} $extra"
ssh "${ssh_opts[@]}" "$target" "rm -rf $remote_dir/out && ONEARCHIVE_ARTIFACTS=$remote_dir/artifacts bash -lc '$cmd'"
ssh "${ssh_opts[@]}" "$target" "cd $remote_dir/out && tar -cf - ." | tar -xf - -C "$out"
echo "manifests in $out: $(ls "$out" | tr '\n' ' ')"
