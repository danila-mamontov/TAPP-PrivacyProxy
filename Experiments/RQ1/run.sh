#!/usr/bin/env bash
# use bash for the script

set -euo pipefail                   # terminate script when fail appeared
cd "$(dirname "${BASH_SOURCE[0]}")" # go to current folder where run.sh is

# --- 1. .env verification ---
if [[ ! -f .env ]]; then
    cp _env.example .env
    echo "No .env found -- created it from _env.example."
    echo "Please set PRIVACYPROXY_IMAGE_TAG in .env and run './run.sh' again."
    exit 1
fi

source .env # load .env

# --- 2. venv installation ---
if [[ ! -d venv ]]; then
    echo "Creating venv and installing dependencies ..."
    python3 -m venv venv
    venv/bin/pip install --quiet -r requirements.txt
fi

# exp1 infra setup
echo "Starting Docker stack (Presidio Mock + LLM Mock + PrivacyProxy) ..."
trap 'echo "Tearing down Docker stack ..."; docker compose down' EXIT
docker compose up --force-recreate --build --wait

# run experiment
echo "Running experiment ..."
venv/bin/python experiment.py
