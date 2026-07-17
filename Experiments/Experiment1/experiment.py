"""
Experiment 1 - does PrivacyProxy pseudonymize and de-pseudonymize correctly?

We do NOT test Presidio's detection here. A MOCK Presidio is TOLD where the PII is
(from the dataset's privacy_mask), so we know exactly what the proxy must hide.
Then we check two things for each row:

  1. Pseudonymized?    The text the LLM received is the original text with every
                       PII span replaced by a [TYPE_<hash>] placeholder - nothing
                       more, nothing less. (The original PII never reaches the LLM.)
  2. De-pseudonymized? The original text comes back in the proxy's answer, i.e. the
                       placeholders were restored to the real values (round-trip).

Every row is sent through the proxy in BOTH response modes, non-streaming and
streaming. Anonymization is the same in both, so pseudonymization is identical;
streaming uses a different restore path (placeholders arrive split across chunks),
so de-pseudonymization is checked separately for each mode.

FLOW per row (see the diagram):
  privacy_mask --POST /send-solution--> Presidio Mock     (tell it where the PII is)
  source_text  --POST /v1/chat/completions--> PrivacyProxy
                  -> proxy asks Presidio Mock, anonymizes, sends to LLM Mock
                  -> LLM Mock echoes it back, proxy de-anonymizes, returns it
  GET /last on the LLM Mock returns the messages the LLM received; we read the
  anonymized user message from it.

REQUIREMENTS: Presidio Mock, LLM Mock and PrivacyProxy must be running. "./run.sh"
starts everything with docker compose. All ports come from ".env".
"""
import csv
import json
import os
import re
from datetime import datetime
from pathlib import Path

import requests
from datasets import load_dataset
from dotenv import load_dotenv

load_dotenv()

# ---------------------------------------------------------------------------
# Configuration (ports, sample size, languages) - all from ".env"
# ---------------------------------------------------------------------------
PROXY_PORT         = os.environ.get("PROXY_PORT", "8090")
MOCK_LLM_PORT      = os.environ.get("MOCK_LLM_PORT", "5005")
PRESIDIO_MOCK_PORT = os.environ.get("PRESIDIO_MOCK_PORT", "5002")

PROXY_URL         = f"http://localhost:{PROXY_PORT}/v1/chat/completions"
MOCK_LAST_URL     = f"http://localhost:{MOCK_LLM_PORT}/last"
SEND_SOLUTION_URL = f"http://localhost:{PRESIDIO_MOCK_PORT}/send-solution"

SAMPLE_SIZE = int(os.environ.get("SAMPLE_SIZE", 200))  # rows PER LANGUAGE
LANGUAGES = [x.strip().lower() for x in os.environ.get("LANGUAGES", "de,en").split(",") if x.strip()]

# The dataset writes the language as a code or spelled out; normalize to de/en.
LANGUAGE_ALIASES = {"de": {"de", "german", "deutsch"}, "en": {"en", "english", "englisch"}}


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
def normalize_language(raw):
    raw = str(raw).lower()
    for code, aliases in LANGUAGE_ALIASES.items():
        if raw in aliases:
            return code
    return None


def get_privacy_mask(row):
    """The dataset's known PII spans: a list of {value, start, end, label}."""
    mask = row["privacy_mask"]
    if isinstance(mask, str):
        mask = json.loads(mask)
    return mask


# Any [TYPE_hash] placeholder. We do NOT pin the TYPE name: the proxy keeps ONE
# placeholder per distinct value across all requests, so a value first seen under a
# different label keeps that label's type. What matters for pseudonymization is that
# the PII span became a placeholder (hidden from the LLM), not which type name it has.
PLACEHOLDER = r"\[[A-Z0-9_]+_[0-9a-f]{16}\]"


def expected_anonymized_pattern(source_text, mask):
    """Regex for 'source_text with each PII span replaced by a placeholder'.

    We escape the non-PII parts (so they match literally) and put a placeholder
    pattern where each PII span was. If the proxy pseudonymized correctly, the text
    the LLM received matches this exactly. If any PII value were left unreplaced, the
    literal value would not match the placeholder pattern -> the check fails.
    """
    parts = []
    pos = 0
    for item in sorted(mask, key=lambda x: x["start"]):
        parts.append(re.escape(source_text[pos:item["start"]]))
        parts.append(PLACEHOLDER)
        pos = item["end"]
    parts.append(re.escape(source_text[pos:]))
    return "".join(parts)


MODES = ["non-stream", "stream"]  # test both response modes of the proxy


def call_proxy(source_text, stream):
    """Send `source_text` through the proxy and return (anonymized, final_answer).

    - anonymized:   the (last) user message the LLM Mock received (read via /last).
                    Anonymization is identical in both modes.
    - final_answer: the proxy's de-anonymized answer. In streaming mode it arrives
                    as Server-Sent Events; each `data:` line is normally a JSON chunk
                    (we take delta.content), but the proxy may also emit a plain-text
                    tail when it flushes its buffer at the end, so we fall back to the
                    raw line if it is not JSON.
    """
    payload = {"model": "mock", "stream": stream, "messages": [{"role": "user", "content": source_text}]}

    if not stream:
        response = requests.post(PROXY_URL, json=payload, timeout=120)
        response.raise_for_status()
        final_answer = response.json()["choices"][0]["message"]["content"] or ""
    else:
        final_answer = ""
        with requests.post(PROXY_URL, json=payload, timeout=120, stream=True) as response:
            response.raise_for_status()
            for line in response.iter_lines(decode_unicode=True):
                if not line or not line.startswith("data:"):
                    continue
                data = line[len("data:"):].strip()
                if data == "[DONE]":
                    break
                try:
                    final_answer += json.loads(data)["choices"][0]["delta"].get("content") or ""
                except (json.JSONDecodeError, KeyError, IndexError):
                    final_answer += data  # plain-text tail flushed by the proxy

    messages = requests.get(MOCK_LAST_URL, timeout=10).json()["privacyproxy_sent"]["messages"]
    user_texts = [m.get("content", "") or "" for m in messages if m.get("role") == "user"]
    anonymized = user_texts[-1] if user_texts else ""
    return anonymized, final_answer


# ---------------------------------------------------------------------------
# Load and sample the dataset (only de/en; all other languages are skipped)
# ---------------------------------------------------------------------------
print("Loading dataset (first run downloads + caches, then it is fast) ...")
ds = load_dataset("ai4privacy/pii-masking-200k", split="train")

for lang in LANGUAGES:
    if lang not in LANGUAGE_ALIASES:
        raise SystemExit(f"Unknown language '{lang}' in LANGUAGES; supported: {sorted(LANGUAGE_ALIASES)}")

rows = []
for lang in LANGUAGES:
    subset = ds.filter(lambda x, _l=lang: normalize_language(x.get("language", "")) == _l)
    n = min(SAMPLE_SIZE, len(subset))
    print(f"'{lang}': {len(subset)} rows in the dataset -> using {n}")
    for i in range(n):
        rows.append((lang, subset[i]))

print(f"Total: {len(rows)} rows\n")

# ---------------------------------------------------------------------------
# Run the experiment
# ---------------------------------------------------------------------------
results = []
pseudo_ok = errors = 0
deanon_ok = {mode: 0 for mode in MODES}   # de-pseudonymization is checked per mode
failures_shown = 0

for i, (lang, row) in enumerate(rows):
    source_text = row["source_text"]
    try:
        mask = get_privacy_mask(row)
    except Exception as ex:
        errors += 1
        print(f"  [WARN] row {i}: unreadable privacy_mask ({ex})")
        continue

    # 1) Tell the Presidio Mock where the PII is, and wait for its OK.
    try:
        requests.post(SEND_SOLUTION_URL, json=mask, timeout=10).raise_for_status()
    except Exception as ex:
        errors += 1
        print(f"  [WARN] row {i}: send-solution failed ({ex})")
        continue

    # 2) Send the source text through the proxy in BOTH modes and read each answer.
    #    Anonymization is identical in both, so we take `anonymized` from the first.
    try:
        anonymized = None
        final_answers = {}
        for mode in MODES:
            anon, final = call_proxy(source_text, stream=(mode == "stream"))
            if anonymized is None:
                anonymized = anon
            final_answers[mode] = final
    except Exception as ex:
        errors += 1
        print(f"  [WARN] row {i}: proxy request failed ({ex})")
        continue

    # 3) The checks.
    #    Pseudonymized (mode-independent): the text the LLM received is the original
    #    with every PII span turned into a placeholder - and nothing else changed.
    pseudonymized = re.fullmatch(expected_anonymized_pattern(source_text, mask), anonymized) is not None
    #    De-pseudonymized (per mode): the original text comes back in the answer. (The
    #    answer also contains the proxy's system instruction, so we check "contains".)
    depseud = {mode: source_text in final_answers[mode] for mode in MODES}

    pseudo_ok += int(pseudonymized)
    for mode in MODES:
        deanon_ok[mode] += int(depseud[mode])
    results.append({
        "row": i, "language": lang, "pii_count": len(mask),
        "pseudonymized": pseudonymized,
        **{f"depseudonymized_{mode}": depseud[mode] for mode in MODES},
    })

    # Show the first few failures so problems are easy to inspect.
    if (not pseudonymized or not all(depseud.values())) and failures_shown < 5:
        failures_shown += 1
        print(f"  [FAIL] row {i} ({lang}) pseudo={pseudonymized} "
              f"depseudo={ {m: depseud[m] for m in MODES} }")
        print(f"         original : {source_text[:120]!r}")
        print(f"         to LLM   : {anonymized[:120]!r}")
        for mode in MODES:
            print(f"         answer ({mode}): {final_answers[mode][:100]!r}")

    if (i + 1) % 50 == 0:
        print(f"  {i + 1}/{len(rows)} rows processed ...")

# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------
total = len(results)


def pct(part, whole):
    return f"{part / whole:.1%}" if whole else "n/a"


print()
print("=" * 60)
print("RESULT")
print("=" * 60)
print(f"  rows tested:                     {total}")
print(f"  pseudonymized correctly:         {pseudo_ok}/{total} ({pct(pseudo_ok, total)})   <- must be 100%")
for mode in MODES:
    print(f"  de-pseudonymized ({mode:<10}): {deanon_ok[mode]}/{total} ({pct(deanon_ok[mode], total)})   <- must be 100%")
if errors:
    print(f"  failed requests:                 {errors}")

# ---------------------------------------------------------------------------
# Write per-row results to results/<timestamp>/detail.csv
# ---------------------------------------------------------------------------
out_dir = Path("results") / datetime.now().strftime("%Y%m%d_%H%M%S")
out_dir.mkdir(parents=True, exist_ok=True)
with open(out_dir / "detail.csv", "w", newline="") as f:
    fieldnames = ["row", "language", "pii_count", "pseudonymized"] + [f"depseudonymized_{m}" for m in MODES]
    writer = csv.DictWriter(f, fieldnames=fieldnames)
    writer.writeheader()
    writer.writerows(results)

print(f"\nResults written to: {(out_dir / 'detail.csv').resolve()}")
