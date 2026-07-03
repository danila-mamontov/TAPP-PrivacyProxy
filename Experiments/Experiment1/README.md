# Experiment 1 — does the proxy reliably hide detected PII?

This experiment tests the **PrivacyProxy**, not Presidio.

**The question it answers:** for every piece of PII that Presidio detects, does the
proxy…

1. **hide it before it reaches the LLM?** → leak must be **0%**
2. **restore it in the answer?** → restore must be **100%**

The real LLM is replaced by a **mock** that just echoes back whatever it receives,
so we only need **Presidio** and the **PrivacyProxy** (no OpenClaw, no real model).

Every sample is tested in **both response modes**: non-streaming and streaming.
Anonymization is the same in both, so leaks are identical; streaming uses a
different restore path (placeholders arrive split across chunks), so both modes
are checked separately.

> **Why this is a proxy test, not a Presidio benchmark:** we only look at the
> entities Presidio *actually found*. Whether Presidio misses some PII is a
> separate question — reported as a secondary **meta** number (Presidio's recall),
> but it is not what this experiment grades.

## Data flow
```
plain text → [proxy anonymizes] → mock LLM (echoes what it received)
           → [proxy de-anonymizes] → final answer
```
The mock records the exact text it received (the anonymized prompt). We read that
back via `GET /last` and check it for leaks — **not** the proxy's answer, which is
plain text again after de-anonymization.

## How we know what Presidio detected
The script asks the **same** Presidio service directly (`POST /analyze`), using the
**same settings the proxy uses** — both read from `presidio_config.json`. Because
the proxy and the script share that config, the script sees exactly the entities
the proxy will try to hide. Then, for each of those, it checks the recorded prompt
for a leak and the answer for a correct restore.

## Files
- `experiment.py` — runs the test and writes the results.
- `mock_llm.py` / `Dockerfile.mock-llm` — the echo mock LLM (runs as a container).
- `docker-compose.yml` — starts Presidio + mock LLM + PrivacyProxy in one
  self-contained stack (no manual `docker network create` needed).
- `presidio_config.json` — the entity types and thresholds. **This is the one file
  you tune.** `run.sh` turns it into env vars for the proxy, and `experiment.py`
  reads it directly, so both always agree.
- `generate_presidio_env.py` — turns `presidio_config.json` into `.env.presidio`.
- `_env.example` — template for `.env` (ports, image tag, sample size).
- `run.sh` — the single entry point (see below).
- `results/` — one timestamped subfolder per run.

## One-time setup
```bash
cd Experiments/Experiment1
cp _env.example .env
# open .env and set PRIVACYPROXY_IMAGE_TAG to a fixed commit SHA (not "dev")
docker login ghcr.io   # while the image is private (PAT with read:packages)
```

## Run
```bash
./run.sh
```
`run.sh` does everything:
1. creates `.env` from `_env.example` if missing (then stops so you can set the tag),
2. creates a `venv` and installs `requirements.txt` if needed,
3. generates `.env.presidio` from `presidio_config.json`,
4. starts Presidio + mock LLM + PrivacyProxy and waits until all are healthy,
5. runs `experiment.py`,
6. tears the stack down again — even if step 5 fails.

The very first Presidio start takes a bit longer (it builds the spaCy models),
after that it is fast.

## Results
Each run writes to `results/<timestamp>/`:
- `detail.csv` — one row per Presidio-detected entity **per mode**: `mode`,
  `leaked` (did it reach the LLM?) and `restored` (was it put back in the answer?).
- `summary.csv` — the same, grouped per mode, language and label.
- `run_meta.json` — the headline numbers per mode (leak rate, restore rate) plus
  Presidio recall, and which image tag and config produced them.

The console prints a per-mode headline first — **leak rate (must be 0%)** and
**restore rate (must be 100%)** for non-streaming and streaming — then which
labels leaked, then the secondary Presidio recall.

## Tuning Presidio
Edit `presidio_config.json` (entity types and per-entity thresholds) and run
`./run.sh` again. Because both the proxy and the experiment read that same file,
the two stay in sync automatically. Runs are timestamped, so you can compare
`results/<older>` with `results/<newer>` yourself.

*(Advanced: to change Presidio's underlying recognizers/models, edit the YAML
files in `../../Presidio/` and `docker compose restart presidio-analyzer`.)*

## Dataset
`experiment.py` downloads `ai4privacy/pii-masking-200k` automatically via the
`datasets` library (cached in `~/.cache/huggingface` after the first run). Nothing
to download manually. Optionally set `HF_TOKEN` in `.env` to avoid download rate
limits.
