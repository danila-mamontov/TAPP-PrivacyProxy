"""
Experiment 1 - does the PrivacyProxy reliably hide detected PII from the LLM?

WHAT WE TEST (the important part)
    For every piece of PII that Presidio detects, the PrivacyProxy must:
      (1) replace it with a placeholder BEFORE the text reaches the LLM   -> leak must be 0%
      (2) restore the original value in the LLM's answer                  -> restore must be 100%

    So we do NOT grade how good Presidio is at finding PII. We only look at the
    entities Presidio actually found, and check what the proxy did with them.
    That makes this a test of the PROXY, not of Presidio.

META INFO (secondary, not the point of this experiment)
    How many of the dataset's known PII values did Presidio catch at all?
    That is Presidio's "recall" and is reported only for context.

DATA FLOW
    plain text --> [proxy anonymizes] --> mock LLM (echoes what it received)
               --> [proxy de-anonymizes] --> final answer

    The mock LLM records the exact text it received (the anonymized prompt) and
    we read it back via GET /last. That recorded text is what we inspect for leaks.
    We check leaks on the recorded prompt, NOT on the proxy's answer - the answer
    is plain text again after de-anonymization.

HOW WE KNOW WHAT PRESIDIO DETECTED
    We ask the SAME Presidio service directly (POST /analyze), using the SAME
    settings the proxy uses (read from presidio_config.json). Because both the
    proxy and this script talk to the same Presidio with the same configuration,
    we see exactly the entities the proxy will try to hide.

REQUIREMENTS
    Presidio, the mock LLM and the PrivacyProxy must be running. "./run.sh" starts
    everything with docker compose. All ports/parameters come from ".env".
"""
import json
import os
from datetime import datetime
from pathlib import Path

import pandas as pd
import requests
from datasets import load_dataset
from dotenv import load_dotenv

load_dotenv()  # read ".env" in the current folder, if present

# ---------------------------------------------------------------------------
# Configuration (ports, sample size, languages) - all from ".env"
# ---------------------------------------------------------------------------
PROXY_PORT    = os.environ.get("PROXY_PORT", "8090")
MOCK_PORT     = os.environ.get("MOCK_LLM_PORT", "5005")
PRESIDIO_PORT = os.environ.get("PRESIDIO_PORT", "5002")

PROXY_URL          = f"http://localhost:{PROXY_PORT}/v1/chat/completions"
MOCK_LAST_URL      = f"http://localhost:{MOCK_PORT}/last"
MOCK_RESET_URL     = f"http://localhost:{MOCK_PORT}/reset"
PRESIDIO_ANALYZE_URL = f"http://localhost:{PRESIDIO_PORT}/analyze"

# Samples PER LANGUAGE. Lower this (e.g. 50) for a quick test run.
SAMPLE_SIZE = int(os.environ.get("SAMPLE_SIZE", 500))

# Which languages to test. ".env": LANGUAGES=de,en
LANGUAGES = [x.strip().lower() for x in os.environ.get("LANGUAGES", "de,en").split(",") if x.strip()]

# The dataset writes the language sometimes as a code, sometimes spelled out.
# Map everything onto the two languages the PrivacyProxy supports.
LANGUAGE_ALIASES = {
    "de": {"de", "german", "deutsch"},
    "en": {"en", "english", "englisch"},
}

# ---------------------------------------------------------------------------
# Presidio settings - read from the SAME file the proxy is configured from,
# so our direct Presidio calls match exactly what the proxy detects/hides.
# (run.sh turns presidio_config.json into the proxy's environment variables.)
# ---------------------------------------------------------------------------
PRESIDIO_CONFIG_PATH = Path("presidio_config.json")
if not PRESIDIO_CONFIG_PATH.exists():
    raise SystemExit(
        "presidio_config.json not found. It defines which entities/thresholds to "
        "use and must match the proxy's configuration. Run ./run.sh from this folder."
    )

presidio_config = json.loads(PRESIDIO_CONFIG_PATH.read_text())
_cfg = presidio_config["Presidio"]

GLOBAL_THRESHOLD  = _cfg["ScoreThreshold"]
ALLOW_LIST        = _cfg["AllowList"]
CONTEXT           = _cfg["Context"]
ENTITY_TYPES      = {"de": _cfg["GermanEntityTypes"],      "en": _cfg["EnglishEntityTypes"]}
ENTITY_THRESHOLDS = {"de": _cfg["GermanEntityThresholds"], "en": _cfg["EnglishEntityThresholds"]}


# ---------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------
def normalize_language(raw):
    """Map a raw dataset language value (e.g. 'German') to 'de'/'en', or None."""
    raw = str(raw).lower()
    for code, aliases in LANGUAGE_ALIASES.items():
        if raw in aliases:
            return code
    return None


def get_ground_truth(sample):
    """Return the dataset's known PII values as a list of (value, label).

    Used ONLY for the secondary meta metric (Presidio recall). The dataset's
    `privacy_mask` may already be a list, or still be a JSON string.
    """
    mask = sample["privacy_mask"]
    if isinstance(mask, str):
        mask = json.loads(mask)
    return [(e["value"], e["label"]) for e in mask]


def analyze_with_presidio(text, language):
    """Ask Presidio directly which PII it finds in `text` for one language.

    We call Presidio the SAME way the proxy does (same entity list, threshold,
    allow-list and context), so we see the same detections the proxy sees.
    Returns Presidio's raw list, e.g.
        [{"entity_type": "PERSON", "start": 3, "end": 8, "score": 0.99}, ...]
    """
    response = requests.post(
        PRESIDIO_ANALYZE_URL,
        json={
            "text": text,
            "language": language,                # "de" or "en"
            "entities": ENTITY_TYPES[language],  # only the configured entity types
            "score_threshold": GLOBAL_THRESHOLD,
            "allow_list": ALLOW_LIST,
            "context": CONTEXT,
            "return_decision_process": False,
        },
        timeout=60,
    )
    response.raise_for_status()
    return response.json()


def resolve_overlaps(entities):
    """Keep the highest-scoring entity when two detections overlap.

    Same rule the proxy uses (DefaultEntityPolicy): sort by score, then greedily
    keep an entity only if it does not overlap one we already kept.
    """
    kept = []
    for entity in sorted(entities, key=lambda e: e["score"], reverse=True):
        overlaps = any(k["start"] < entity["end"] and k["end"] > entity["start"] for k in kept)
        if not overlaps:
            kept.append(entity)
    return kept


def detect_pii(text):
    """Return the PII entities the proxy will try to hide in `text`.

    Mirrors the proxy exactly: analyze in German AND English, drop anything below
    the configured per-entity threshold (or with an unconfigured type), then
    remove overlaps. Each entity carries its exact text value (text[start:end]).
    """
    found = []
    for language in ("de", "en"):
        for entity in analyze_with_presidio(text, language):
            label = entity["entity_type"]
            threshold = ENTITY_THRESHOLDS[language].get(label)
            if threshold is None:            # type not configured -> proxy ignores it
                continue
            if entity["score"] < threshold:  # below the configured threshold -> dropped
                continue
            value = text[entity["start"]:entity["end"]]
            if not value.strip():            # empty/whitespace span -> nothing to hide
                continue
            found.append({
                "label": label,
                "start": entity["start"],
                "end": entity["end"],
                "score": entity["score"],
                "value": value,
            })
    return resolve_overlaps(found)


# ---------------------------------------------------------------------------
# Load and sample the dataset
# ---------------------------------------------------------------------------
print("Loading dataset (first run downloads + caches, then it is fast) ...")
ds = load_dataset("ai4privacy/pii-masking-200k", split="train")

for lang in LANGUAGES:
    if lang not in LANGUAGE_ALIASES:
        raise SystemExit(f"Unknown language '{lang}' in LANGUAGES; supported: {sorted(LANGUAGE_ALIASES)}")

samples_by_lang = {}
for lang in LANGUAGES:
    subset = ds.filter(lambda x, _lang=lang: normalize_language(x.get("language", "")) == _lang)
    print(f"'{lang}': {len(subset)} entries found in the dataset")
    if len(subset) == 0:
        raise SystemExit(f"No entries for '{lang}'. Adjust LANGUAGE_ALIASES.")
    n = min(SAMPLE_SIZE, len(subset))
    samples_by_lang[lang] = subset.select(range(n))
    print(f"  -> using {n} samples")

# One flat list of (language, index, sample) to process.
all_samples = [
    (lang, idx, subset[idx])
    for lang, subset in samples_by_lang.items()
    for idx in range(len(subset))
]
total_samples = len(all_samples)
print(f"Total: {total_samples} samples across {len(samples_by_lang)} language(s)\n")

# ---------------------------------------------------------------------------
# Run the experiment
# ---------------------------------------------------------------------------
requests.post(MOCK_RESET_URL)

detail_rows = []          # one row per Presidio-detected entity (the PRIMARY metric)
meta_gt_total = 0         # known dataset PII values (for the META recall metric)
meta_gt_detected = 0
errors = 0

for i, (lang, idx, sample) in enumerate(all_samples):
    text = sample["source_text"]

    # 1) Ask Presidio (directly) what it detects in this text.
    try:
        detected = detect_pii(text)
    except Exception as ex:
        errors += 1
        print(f"  [WARN] sample {lang}/{idx}: Presidio call failed ({ex})")
        continue

    # 2) Send the same text through the proxy and read back what the LLM received.
    payload = {"model": "mock", "stream": False, "messages": [{"role": "user", "content": text}]}
    try:
        answer_resp = requests.post(PROXY_URL, json=payload, timeout=120)
        answer_resp.raise_for_status()
        final_answer = answer_resp.json()["choices"][0]["message"]["content"] or ""
        anonymized   = requests.get(MOCK_LAST_URL, timeout=10).json()["content"] or ""
    except Exception as ex:
        errors += 1
        print(f"  [WARN] sample {lang}/{idx}: proxy request failed ({ex})")
        continue

    # 3) PRIMARY: for each detected entity, did the proxy hide and restore it?
    for entity in detected:
        value = entity["value"]
        leaked = value in anonymized                       # must be False
        restored = (value in final_answer) if not leaked else None  # only meaningful if hidden
        detail_rows.append({
            "sample_idx": idx,
            "language": lang,
            "label": entity["label"],
            "value": value,
            "score": entity["score"],
            "leaked": leaked,
            "restored": restored,
        })

    # 4) META: how many of the dataset's known PII values did Presidio catch?
    #    A value that disappeared from the outgoing text was detected by Presidio.
    for value, _label in get_ground_truth(sample):
        if not value.strip():
            continue
        meta_gt_total += 1
        if value not in anonymized:
            meta_gt_detected += 1

    if (i + 1) % 50 == 0:
        print(f"  {i + 1}/{total_samples} samples processed ...")

# ---------------------------------------------------------------------------
# Aggregate the results
# ---------------------------------------------------------------------------
df = pd.DataFrame(detail_rows)

detected_total = len(df)
leaked_total = int(df["leaked"].sum()) if detected_total else 0
hidden_total = detected_total - leaked_total
restored_ok = int((df["restored"] == True).sum()) if detected_total else 0  # noqa: E712

# Per language + label breakdown of the primary metric.
if detected_total:
    summary = (
        df.groupby(["language", "label"])
        .agg(
            detected=("leaked", "size"),
            leaked=("leaked", "sum"),
            hidden=("leaked", lambda s: (~s).sum()),
            restored=("restored", lambda s: (s == True).sum()),  # noqa: E712
        )
        .reset_index()
    )
    summary["leak_rate"]    = summary["leaked"]   / summary["detected"]
    summary["restore_rate"] = summary["restored"] / summary["hidden"].replace(0, pd.NA)
else:
    summary = pd.DataFrame(columns=["language", "label", "detected", "leaked", "hidden", "restored", "leak_rate", "restore_rate"])


def pct(part, whole):
    return f"{part / whole:.2%}" if whole else "n/a"


# ---------------------------------------------------------------------------
# Print the report
# ---------------------------------------------------------------------------
print()
print("=" * 70)
print("PRIMARY RESULT - what the proxy did with the PII that Presidio detected")
print("=" * 70)
print(f"  PII entities detected by Presidio: {detected_total}")
print(f"  leaked to the LLM:                 {leaked_total} ({pct(leaked_total, detected_total)})   <- must be 0%")
print(f"  hidden as placeholder:             {hidden_total} ({pct(hidden_total, detected_total)})")
print(f"  restored in the answer:            {restored_ok}/{hidden_total} ({pct(restored_ok, hidden_total)})   <- must be 100%")

if detected_total:
    print()
    print(f"  {'lang':<5}{'label':<20}{'detected':>9}{'leaked':>8}{'leak%':>8}{'restore%':>10}")
    print("  " + "-" * 60)
    for _, r in summary.iterrows():
        restore_str = pct(int(r["restored"]), int(r["hidden"]))
        print(f"  {r['language']:<5}{r['label']:<20}{int(r['detected']):>9}{int(r['leaked']):>8}"
              f"{r['leak_rate']:>8.1%}{restore_str:>10}")

print()
print("=" * 70)
print("META - Presidio detection quality (context only, NOT the point)")
print("=" * 70)
print(f"  known PII values in dataset: {meta_gt_total}")
print(f"  detected by Presidio:        {meta_gt_detected} ({pct(meta_gt_detected, meta_gt_total)})")

if errors:
    print(f"\nFailed requests: {errors}")

# ---------------------------------------------------------------------------
# Write results to results/<timestamp>/
# ---------------------------------------------------------------------------
run_id = datetime.now().strftime("%Y%m%d_%H%M%S")
out_dir = Path("results") / run_id
out_dir.mkdir(parents=True, exist_ok=True)

df.to_csv(out_dir / "detail.csv", index=False)       # one row per detected entity
summary.to_csv(out_dir / "summary.csv", index=False)  # per language + label
(out_dir / "presidio_config.json").write_text(json.dumps(presidio_config, indent=2, ensure_ascii=False))

run_meta = {
    "run_id": run_id,
    "languages": LANGUAGES,
    "sample_size_per_language": SAMPLE_SIZE,
    "samples_used": total_samples,
    "dataset": "ai4privacy/pii-masking-200k",
    "privacyproxy_image_tag": os.environ.get("PRIVACYPROXY_IMAGE_TAG"),
    "errors": errors,
    "primary": {
        "detected": detected_total,
        "leaked": leaked_total,
        "hidden": hidden_total,
        "restored": restored_ok,
        "leak_rate": (leaked_total / detected_total) if detected_total else None,
        "restore_rate": (restored_ok / hidden_total) if hidden_total else None,
    },
    "meta_presidio_recall": {
        "known_values": meta_gt_total,
        "detected": meta_gt_detected,
        "rate": (meta_gt_detected / meta_gt_total) if meta_gt_total else None,
    },
    "presidio_config": presidio_config,
}
(out_dir / "run_meta.json").write_text(json.dumps(run_meta, indent=2, ensure_ascii=False))

print(f"\nResults written to: {out_dir.resolve()}")
