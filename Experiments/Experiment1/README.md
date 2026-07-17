# Experiment 1 — does the proxy pseudonymize and de-pseudonymize correctly?

This experiment tests the **PrivacyProxy**, not Presidio. The real Presidio is
replaced by a **mock** that is simply *told* where the PII is (from the dataset),
so the test is deterministic: we know exactly what the proxy must hide.

**The two things we check per row:**
1. **Pseudonymized?** The text the LLM received is the original text with every PII
   span turned into a `[<TYPE>_<hash>]` placeholder (TYPE = the dataset label, e.g.
   `[FIRSTNAME_...]`) — the real PII never reaches the LLM.
2. **De-pseudonymized?** The original text comes back in the proxy's answer (the
   placeholders were restored). Both must be **100%**.

Every row is sent in **both response modes**: non-streaming and streaming.
Anonymization is the same in both, so pseudonymization is identical; streaming uses
a different restore path — the mock streams the answer back in small chunks, which
splits placeholders across chunk boundaries and stress-tests the proxy's streaming
de-anonymizer. De-pseudonymization is therefore reported separately per mode.

## Data flow
```
privacy_mask ── POST /send-solution ──▶ Presidio Mock      (tell it where the PII is)
source_text  ── POST /v1/chat/completions ──▶ PrivacyProxy
                  → asks Presidio Mock, anonymizes, sends to LLM Mock
                  → LLM Mock echoes it back, proxy de-anonymizes, returns it
GET /last on the LLM Mock = the messages the LLM received (anonymized).
```

## Files
- `presidio_mock.py` / `Dockerfile.presidio-mock` — fake Presidio: `/send-solution`
  stores the PII spans, `/analyze` returns them to the proxy.
- `llm_mock.py` / `Dockerfile.llm-mock` — echo LLM: returns what it received and
  exposes it via `/last`.
- `experiment.py` — downloads the dataset, drives one row at a time, checks the
  two properties, writes `results/<timestamp>/detail.csv`.
- `docker-compose.yml` — starts the two mocks + PrivacyProxy, fully self-contained.
  The proxy is configured entirely via environment variables; the mock reports each
  PII with the dataset's own label, which the proxy anonymizes via its global
  ScoreThreshold fallback.
- `_env.example` — template for `.env` (image tag, ports, sample size, languages).
- `run.sh` — the single entry point.

## Setup (once)
```bash
cd Experiments/Experiment1
cp _env.example .env
# open .env and set PRIVACYPROXY_IMAGE_TAG to a fixed commit SHA
docker login ghcr.io   # while the image is private (PAT with read:packages)
```

## Run
```bash
./run.sh
```
It creates `.env`/`venv` if needed, starts the stack (`docker compose up --wait`),
runs `experiment.py`, then tears the stack down again.

## Results
The console prints the headline:
```
  pseudonymized correctly:         N/N (100.0%)   <- must be 100%
  de-pseudonymized (non-stream): N/N (100.0%)   <- must be 100%
  de-pseudonymized (stream    ): N/N (100.0%)   <- must be 100%
```
`results/<timestamp>/detail.csv` has one row per tested sample, with a
`depseudonymized_<mode>` column per response mode. The first few failing rows are
printed with the original / anonymized / restored text so problems are easy to inspect.

## Dataset
`experiment.py` downloads `ai4privacy/pii-masking-200k` automatically (cached after
the first run). Only `en` and `de` rows are used; `SAMPLE_SIZE` rows per language.
