#!/usr/bin/env bash
# pipeline/deploy/install-weekly.sh — set up the weekly Q&A refresh's own clone and
# its systemd *user* units. Idempotent; re-running it reinstalls the units.
#
#   1. clone origin to ~/.local/share/dotfit/qa-job
#   2. link what git does not carry to this checkout: .env, the customer mail and
#      the caches, so there is one copy of each and a run here pays only for what
#      is new
#   3. install the units; the timers start only with --enable
#
#   pipeline/deploy/install-weekly.sh            # set up, timers off
#   pipeline/deploy/install-weekly.sh --enable   # and start the timers

set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/../.." && pwd)
JOB="$HOME/.local/share/dotfit/qa-job"
UNIT_DIR="$HOME/.config/systemd/user"
UV=$(command -v uv)

export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"

# --- 1. the clone -------------------------------------------------------------
if [ ! -d "$JOB/.git" ]; then
    mkdir -p "$(dirname "$JOB")"
    git clone --quiet "$(git -C "$REPO_ROOT" remote get-url origin)" "$JOB"
    echo "cloned to $JOB"
fi

# --- 2. what git does not carry -----------------------------------------------
ln -sfn "$REPO_ROOT/.env" "$JOB/.env"
ln -sfn "$REPO_ROOT/original-data/QAs" "$JOB/original-data/QAs"
for runs in qa/runs qa/stage2/runs qa/stage4/runs index/runs; do
    mkdir -p "$REPO_ROOT/pipeline-output/$runs"
    ln -sfn "$REPO_ROOT/pipeline-output/$runs" "$JOB/pipeline-output/$runs"
done
uv sync --quiet --project "$JOB/pipeline"
echo "linked .env, original-data/QAs and the run caches"

# --- 3. the units -------------------------------------------------------------
mkdir -p "$UNIT_DIR"
for unit in dotfit-qa-monday.service dotfit-qa-monday.timer \
            dotfit-qa-thursday.service dotfit-qa-thursday.timer; do
    sed -e "s|/home/kovach/.local/bin/uv|$UV|g" "$SCRIPT_DIR/$unit" > "$UNIT_DIR/$unit"
done
systemctl --user daemon-reload
echo "units installed"

if [ "${1:-}" = "--enable" ]; then
    systemctl --user enable --now dotfit-qa-monday.timer dotfit-qa-thursday.timer
    systemctl --user list-timers 'dotfit-qa-*' --no-pager
else
    echo "timers not started; re-run with --enable to start them"
fi
