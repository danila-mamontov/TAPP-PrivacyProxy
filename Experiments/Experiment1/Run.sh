#!/usr/bin/env bash
# Single entry point for Experiment 1:
#   1. check/create .env
#   2. check/create venv + install dependencies
#   3. translate presidio_config.json -> .env.presidio (Presidio tuning values)
#   4. start the Docker stack (Presidio + Mock-LLM + PrivacyProxy) and wait
#      until all three services are "healthy"
#   5. run experiment.py (it copies presidio_config.json into the
#      results/<run_id>/ folder automatically)
#   6. tear the Docker stack down again (even if step 5 fails)
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

# --- 1. .env ---
if [[ ! -f .env ]]; then
    cp _env.example .env
    echo "No .env found -- created it from _env.example."
    echo "Please set PRIVACYPROXY_IMAGE_TAG in .env and run './run.sh' again."
    exit 1
fi

# shellcheck disable=SC1091
source .env

# --- 2. venv ---
if [[ ! -d venv ]]; then
    echo "Creating venv and installing dependencies ..."
    python3 -m venv venv
    venv/bin/pip install --quiet -r requirements.txt
fi

# --- 3. presidio_config.json -> .env.presidio ---
echo "Generating .env.presidio from presidio_config.json ..."
venv/bin/python generate_presidio_env.py

# --- 4. start the Docker stack ---
echo "Starting Docker stack (Presidio + Mock-LLM + PrivacyProxy) ..."
trap 'echo "Tearing down Docker stack ..."; docker compose down' EXIT
docker compose up --build --wait

# --- 5. run the experiment ---
echo "Running experiment ..."
venv/bin/python experiment.py

# Step 6 (docker compose down) happens automatically via the trap above,
# even if experiment.py exits with an error.