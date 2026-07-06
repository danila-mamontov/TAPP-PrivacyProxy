#!/usr/bin/env bash
# Single entry point for Experiment 1:
#   1. check/create .env
#   2. check/create venv + install dependencies
#   3. start the Docker stack (Presidio Mock + LLM Mock + PrivacyProxy) and wait
#      until all three services are "healthy"
#   4. run experiment.py
#   5. tear the Docker stack down again (even if step 4 fails)
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

# --- 3. start the Docker stack ---
echo "Starting Docker stack (Presidio Mock + LLM Mock + PrivacyProxy) ..."
trap 'echo "Tearing down Docker stack ..."; docker compose down' EXIT
docker compose up --build --wait

# --- 4. run the experiment ---
echo "Running experiment ..."
venv/bin/python experiment.py

# Step 5 (docker compose down) happens automatically via the trap above,
# even if experiment.py exits with an error.
