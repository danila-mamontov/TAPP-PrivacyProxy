#!/usr/bin/env bash
# Single entry point for Experiment 2.
#   1. check/create infra/.env (image SHA + ports + model)
#   2. check/create the orchestrator venv + install dependencies
#   3. start the Docker stack (infra/docker-compose.yml) and wait until it is up
#   4. check the external prerequisites (OpenClaw gateway + Ollama) and warn
#   5. run the orchestrator; any arguments are passed straight through, e.g.
#        ./run.sh --iterations 5
#        ./run.sh --only mail-01,cal-01
#        ./run.sh --model ollama-direct/kimi-k2.6:cloud   # control group
#
# The stack is left running afterwards so you can inspect Mailpit / the calendar
# viewer. Tear it down with:  docker compose -f infra/docker-compose.yml down
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

COMPOSE="infra/docker-compose.yml"

# --- 1. infra/.env ---
if [[ ! -f infra/.env ]]; then
    cp infra/_env.example infra/.env
    echo "No infra/.env found -- created it from infra/_env.example."
    echo "Check PRIVACYPROXY_IMAGE_TAG in infra/.env, then run './run.sh' again."
    exit 1
fi
# Export the config so the orchestrator (ports/container names) matches the stack.
set -a
# shellcheck disable=SC1091
source infra/.env
# Central LLM config (model + provider) -- the ONE place; overrides infra/.env.
[ -f ../model.env ] && source ../model.env
set +a

# --- 2. orchestrator venv ---
if [[ ! -d orchestrator/venv ]]; then
    echo "Creating venv and installing dependencies ..."
    python3 -m venv orchestrator/venv
    orchestrator/venv/bin/pip install --quiet -r orchestrator/requirements.txt
fi

# --- 3. start the Docker stack ---
echo "Starting Docker stack (mailpit, radicale, presidio-mock, llm-recorder, agent-tools, privacyproxy) ..."
docker compose -f "$COMPOSE" up -d --wait

# --- 4. check external prerequisites (not part of the compose stack) ---
if ! docker ps --format '{{.Names}}' | grep -q "^${OPENCLAW_CONTAINER:-openclaw-openclaw-gateway-1}$"; then
    echo "WARNING: OpenClaw gateway container '${OPENCLAW_CONTAINER:-openclaw-openclaw-gateway-1}' is not running."
    echo "         Start OpenClaw and register the exp2tools MCP server first."
fi
# Note: OLLAMA_UPSTREAM (host.docker.internal) is the CONTAINER view; from the host
# Ollama is reachable on 127.0.0.1. Use the IPv4 literal, not "localhost": on macOS
# "localhost" resolves to ::1 first, but Ollama binds IPv4 127.0.0.1 only -> a
# "localhost" check would falsely fail even when Ollama is up.
if ! curl -s -m 3 "http://127.0.0.1:11434/api/tags" >/dev/null 2>&1; then
    echo "WARNING: Ollama does not seem reachable on http://127.0.0.1:11434."
fi

# --- 5. run the orchestrator (pass all arguments through) ---
echo "Running orchestrator ..."
orchestrator/venv/bin/python orchestrator/experiment.py "$@"

echo
echo "Done. The stack is still running (Mailpit UI: http://localhost:${MAILPIT_UI_PORT:-8025},"
echo "calendar viewer: http://localhost:${CALENDAR_VIEWER_PORT:-8090})."
echo "Tear it down with:  docker compose -f $COMPOSE down"
