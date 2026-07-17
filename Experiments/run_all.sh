#!/usr/bin/env bash
# Master runner for Experiment 2 & 3.
# Runs the three constellations ONE AFTER ANOTHER (only ONE orchestrator at a time):
#   1. control  (OpenClaw -> Ollama directly, no proxy)    calendar: control
#   2. exp2     (proxy -> MOCK Presidio)                    calendar: exp2
#   3. exp3     (proxy -> REAL Presidio)                    calendar: exp3
# Each: 120 rows (60 de + 60 en) x ITER iterations.
#
# Prerequisites: the OpenClaw gateway is running, the ollama-direct provider and the
# exp2tools MCP server are configured, and infra/.env in each experiment pins the
# PrivacyProxy image SHA (the compose files pull it from GHCR - no local build).
set -u
ROOT=/Users/dennis/RiderProjects/OpenClaw-PrivacyProxy
E2=$ROOT/Experiments/Experiment2
E3=$ROOT/Experiments/Experiment3
PY2=$E2/orchestrator/venv/bin/python
PY3=$E3/orchestrator/venv/bin/python
MEM="$HOME/.openclaw/workspace/MEMORY.md"
ITER=${ITER:-5}
TO=${TO:-300}

# --- Central LLM config: the ONE place to set the model + provider. ---
# Exported here so the compose stacks (shell env beats infra/.env) and the
# control-group model below all use the same values.
CONFIG="$ROOT/Experiments/model.env"
[ -f "$CONFIG" ] || { echo "Missing central config: $CONFIG"; exit 1; }
set -a; source "$CONFIG"; set +a

log(){ echo "[$(date '+%F %T')] $*"; }
fresh(){ printf '# MEMORY.md\n' > "$MEM"; rm -rf "$HOME/.openclaw/workspace/memory/"* 2>/dev/null;
         rm -f /tmp/exp2_orchestrator.lock /tmp/exp3_orchestrator.lock; }
latest_csv(){ ls -t "$1"/orchestrator/results/*/detail.csv 2>/dev/null | head -1; }
wait_tools(){ until curl -s -m3 localhost:3100/health >/dev/null 2>&1; do sleep 2; done; }
# Make sure each orchestrator venv exists and has its dependencies (otherwise the
# run would crash immediately, e.g. on a missing python-dotenv).
ensure_venv(){ local d="$1/orchestrator";
  [ -x "$d/venv/bin/python" ] || python3 -m venv "$d/venv";
  "$d/venv/bin/python" -c "import dotenv, requests" 2>/dev/null || \
    "$d/venv/bin/pip" install --quiet -r "$d/requirements.txt"; }

log "START full run (ITER=$ITER, TO=$TO)"
ensure_venv "$E2"; ensure_venv "$E3"

# Clean starting state: both infras down (avoids port conflicts).
docker compose -f $E3/infra/docker-compose.yml down >/dev/null 2>&1
docker compose -f $E2/infra/docker-compose.yml down >/dev/null 2>&1

# ---------- Phase A: exp2-infra (control group + Exp2 mock) ----------
log "Phase A: bring exp2-infra up"
docker compose -f $E2/infra/docker-compose.yml up -d --force-recreate >/dev/null 2>&1
until curl -s -m3 localhost:8000/health >/dev/null 2>&1; do sleep 2; done

log "== 1/3 CONTROL GROUP (ollama-direct, calendar=control) =="
CALDAV_CAL=control docker compose -f $E2/infra/docker-compose.yml up -d --force-recreate agent-tools >/dev/null 2>&1
wait_tools; fresh
$PY2 $E2/orchestrator/experiment.py --iterations $ITER --timeout $TO \
     --model ollama-direct/$LLM_MODEL --group control > /tmp/run_control.log 2>&1
cp "$(latest_csv $E2)" /tmp/result_control.csv 2>/dev/null
log "   control group done -> /tmp/result_control.csv"

log "== 2/3 EXP2 (mock Presidio, calendar=exp2) =="
CALDAV_CAL=exp2 docker compose -f $E2/infra/docker-compose.yml up -d --force-recreate agent-tools >/dev/null 2>&1
wait_tools; fresh
$PY2 $E2/orchestrator/experiment.py --iterations $ITER --timeout $TO --group exp2-mock > /tmp/run_exp2.log 2>&1
cp "$(latest_csv $E2)" /tmp/result_exp2.csv 2>/dev/null
log "   Exp2 done -> /tmp/result_exp2.csv"

# ---------- Phase B: exp3-infra (real Presidio) ----------
log "Phase B: exp2-infra down, exp3-infra up (calendar=exp3)"
docker compose -f $E2/infra/docker-compose.yml down >/dev/null 2>&1
CALDAV_CAL=exp3 docker compose -f $E3/infra/docker-compose.yml up -d --force-recreate >/dev/null 2>&1
log "   waiting for the real Presidio (model load) ..."
until curl -s -m5 -XPOST localhost:5002/analyze -H 'Content-Type: application/json' \
      -d '{"text":"x","language":"de"}' >/dev/null 2>&1; do sleep 3; done
wait_tools

log "== 3/3 EXP3 (real Presidio, calendar=exp3) =="
fresh
$PY3 $E3/orchestrator/experiment.py --iterations $ITER --timeout $TO --group exp3-real > /tmp/run_exp3.log 2>&1
cp "$(latest_csv $E3)" /tmp/result_exp3.csv 2>/dev/null
log "   Exp3 done -> /tmp/result_exp3.csv"

# Preserve the CSVs in the repo so the next run's /tmp copies don't overwrite them.
mkdir -p "$ROOT/Experiments/results"
cp /tmp/result_control.csv /tmp/result_exp2.csv /tmp/result_exp3.csv "$ROOT/Experiments/results/" 2>/dev/null

log "ALL THREE DONE. CSVs in /tmp/ and preserved in Experiments/results/"
