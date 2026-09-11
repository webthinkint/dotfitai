#!/usr/bin/env bash
# runtime/deploy/install-user-service.sh — install dotfit-agent-service as a
# systemd *user* service on a Linux VM (plan §11, the preview deployment).
#
# Idempotent: re-running it is the redeploy procedure — publish the current
# build, rewrite the unit, restart in place, smoke the health check.
#
#   1. dotnet publish the SSE service to ~/.local/share/dotfit/service
#   2. generate DOTFIT_SERVICE_API_KEY into the repo .env if absent
#      (fail-closed boot: the service refuses to start without one)
#   3. install the committed unit into ~/.config/systemd/user/ with the
#      repo/home paths substituted (BIND=... to change the listen address)
#   4. install the verdict-log viewer as ~/.local/bin/dotfit-verdict-log
#   5. enable linger (survives logout), enable --now, wait for /healthz
#
# Deployment shape this encodes — a preview, deliberately not production:
# trusted internal network, port 5199 not exposed externally, one trusted
# server-side caller (the website backend) on the shared secret, no TLS
# proxy. Anything public-facing needs the front layer runtime/README.md
# describes before DOTFIT_SERVICE_AUTH=none is even a question.

set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/../.." && pwd)
UNIT_SRC="$SCRIPT_DIR/dotfit-agent-service.service"
UNIT_DST="$HOME/.config/systemd/user/dotfit-agent-service.service"
PUBLISH_DIR="$HOME/.local/share/dotfit/service"
BIND="${BIND:-0.0.0.0:5199}"
PORT="${BIND##*:}"

# A user unit is only addressable with a runtime dir; ssh logins normally
# have it, cron-adjacent shells sometimes do not.
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"

echo "repo:  $REPO_ROOT"
echo "unit:  $UNIT_DST"

# --- 1. publish -----------------------------------------------------------
echo "publishing DotFit.Agents.Service (Release)…"
dotnet publish "$REPO_ROOT/runtime/src/DotFit.Agents.Service" \
  -c Release -o "$PUBLISH_DIR" > /dev/null

# --- 2. shared secret (open item 22, fail-closed) ------------------------
ENV_FILE="$REPO_ROOT/.env"
if [ ! -f "$ENV_FILE" ]; then
    echo "ERROR: $ENV_FILE missing — copy .env.example and fill it in first." >&2
    exit 1
fi
if ! grep -q '^DOTFIT_SERVICE_API_KEY=' "$ENV_FILE"; then
    # openssl is the usual source; fall back to the kernel's uuids if absent.
    if command -v openssl > /dev/null; then
        KEY=$(openssl rand -hex 24)
    else
        KEY=$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')
    fi
    printf '\n# --- added by runtime/deploy/install-user-service.sh ---\nDOTFIT_SERVICE_API_KEY=%s\n' \
        "$KEY" >> "$ENV_FILE"
    chmod 600 "$ENV_FILE"
    echo "generated DOTFIT_SERVICE_API_KEY into .env (gitignored) — the caller sends it"
    echo "as 'Authorization: Bearer <key>'. Retrieve with:"
    echo "    grep DOTFIT_SERVICE_API_KEY $ENV_FILE"
else
    echo "DOTFIT_SERVICE_API_KEY already present in .env"
fi

# --- 3. install the unit ---------------------------------------------------
mkdir -p "$(dirname "$UNIT_DST")"
sed -e "s|/home/kovach/dotfitai|$REPO_ROOT|g" \
    -e "s|/home/kovach/.local|$HOME/.local|g" \
    -e "s|http://0.0.0.0:5199|http://$BIND|" \
    "$UNIT_SRC" > "$UNIT_DST"
echo "unit written"

# --- 4. the verdict-log command on PATH -------------------------------------
# The traffic view of the journal: only the dotfit.verdict lines (and, with
# --transcripts, the debug transcript log the unit enables). ~/.local/bin is
# on PATH on the distros this preview targets.
if [ -f "$SCRIPT_DIR/verdict-log" ]; then
    mkdir -p "$HOME/.local/bin"
    install -m 0755 "$SCRIPT_DIR/verdict-log" "$HOME/.local/bin/dotfit-verdict-log"
    echo "installed ~/.local/bin/dotfit-verdict-log"
fi

# --- 5. linger + start + health ---------------------------------------------
loginctl show-user "$USER" -p Linger | grep -q '^Linger=yes' \
    || loginctl enable-linger "$USER" 2> /dev/null \
    || sudo loginctl enable-linger "$USER"
echo "linger enabled (the unit survives logout)"

systemctl --user daemon-reload
systemctl --user enable --now dotfit-agent-service.service
systemctl --user restart dotfit-agent-service.service

# The boot validates the whole .env slice it needs (RuntimeNeeds.Full), so
# "running" is itself the smoke that config, key and alias table all resolve.
for _ in $(seq 1 30); do
    if curl -sf "http://127.0.0.1:$PORT/healthz" > /dev/null 2>&1; then
        echo "healthz: $(curl -s "http://127.0.0.1:$PORT/healthz")"
        systemctl --user status dotfit-agent-service.service --no-pager | head -n 4
        exit 0
    fi
    if ! systemctl --user is-active --quiet dotfit-agent-service.service; then
        echo "ERROR: the service exited — a fail-closed boot or a bad .env. Logs:" >&2
        journalctl --user -u dotfit-agent-service.service -n 20 --no-pager >&2
        exit 1
    fi
    sleep 1
done

echo "ERROR: service running but /healthz not answering on port $PORT" >&2
exit 1
