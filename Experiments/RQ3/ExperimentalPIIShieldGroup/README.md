# RQ3 — Experimental PII Shield Group

This group replaces TAPP/PrivacyProxy with **Microsoft PII Shield** while keeping the
same OpenClaw + MCP + Mailpit + Radicale + LLM-recorder benchmark apparatus.

PII Shield is consumed from the `KasamShaikh/pii-shield` repository at pinned commit
`3730e54943a9844780652b8ce50cd588ca9e0cba` and used through its
`PiiShieldEngine` anonymization/de-anonymization core.

## Experimental chain

```text
OpenClaw
   |
   v
PII Shield OpenAI adapter
   |
   +--> Microsoft PII Shield (detect + replace)
   |
   v
LLM recorder
   |
   v
Ollama Cloud
   |
   v
PII Shield de-anonymization
   |
   v
OpenClaw / MCP tools
```

The adapter exposes `/v1/chat/completions` so OpenClaw keeps the same OpenAI-compatible
interface as the TAPP experiment. For every request, all OpenAI `messages` are
serialized into one PII Shield anonymization call. This includes assistant
`tool_calls` and tool messages from previous rounds, so PII is masked again before
the next LLM request. The LLM response is de-anonymized before it is returned to
OpenClaw; therefore real PII is available to the downstream Mailpit/Calendar tools
but not to the LLM.

## Benchmark invariants

The runner enforces all of the following before starting:

- exactly **120** dataset rows
- exactly **5** iterations
- exactly **7 unique Ollama Cloud model IDs**
- 4200 total agent runs = 120 × 5 × 7
- email/calendar `function_ok` requires both the intended external effect and
  `tool_diagnosis == tool-ok`
- `pseudonymized` is a separate privacy metric: no ground-truth PII value may
  appear anywhere in the messages recorded by the LLM recorder.

The dataset itself is reused from the existing RQ3 benchmark
(`Experiments/RQ3/ExperimentalPresidioGroup/dataset.json`).

## German benchmark support

The current upstream PII Shield engine is configured for English. To keep the same
120-row bilingual benchmark, this experiment adds a small isolated language patch
that:

1. builds one PII Shield instance with `en_core_web_lg`,
2. builds a second PII Shield instance with `de_core_news_lg`, while exposing
   the German instance to Presidio under the existing `en` language code, and
3. routes German benchmark turns to the German-model instance.

The PII detection/anonymization and de-anonymization operators remain PII Shield's
implementation. The German instance is an experimental compatibility adaptation,
not a claim that upstream PII Shield natively supports German. This is documented
explicitly because the pinned upstream engine declares only English support.

## OpenClaw model configuration

Add a provider named `piishield` to `~/.openclaw/openclaw.json` with the same seven
Ollama Cloud model IDs used in the Control/TAPP runs:

```json
"piishield": {
  "baseUrl": "http://host.docker.internal:8080/v1",
  "api": "openai-completions",
  "models": [
    {"id": "<model-1>:cloud", "name": "<model-1> (via PII Shield)"},
    {"id": "<model-2>:cloud", "name": "<model-2> (via PII Shield)"},
    {"id": "<model-3>:cloud", "name": "<model-3> (via PII Shield)"},
    {"id": "<model-4>:cloud", "name": "<model-4> (via PII Shield)"},
    {"id": "<model-5>:cloud", "name": "<model-5> (via PII Shield)"},
    {"id": "<model-6>:cloud", "name": "<model-6> (via PII Shield)"},
    {"id": "<model-7>:cloud", "name": "<model-7> (via PII Shield)"}
  ]
}
```

The experiment runner invokes them as `piishield/<model-id>`.

## Running

```bash
cd Experiments/RQ3/ExperimentalPIIShieldGroup/infrastructure
cp _example.env .env
# set MODELS=... to the exact seven model IDs used for Control/TAPP

docker compose up -d --build

cd ..
python3 -m pip install -r ../ExperimentalPresidioGroup/infrastructure/requirements.txt
python3 experiment.py
```

The generic MCP / calendar / recorder Dockerfiles are reused from the existing
RQ3 infrastructure; only the privacy middleware is new.

Results are written as one CSV row per model × dataset row × iteration.

## Language routing

OpenClaw's system messages are normally English even when the task is German.
Therefore the adapter detects language from the most recent user message only.
German rows are routed to `de`; all other rows are routed to `en`.
