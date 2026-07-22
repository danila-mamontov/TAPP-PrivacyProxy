"""
RQ1 experiment.

Question: does the PrivacyProxy replace PII with placeholders BEFORE the text
reaches the LLM (pseudonymization), and does it put the real values back into
BOTH parts of the LLM answer (de-pseudonymization)?
  - choice 0: normal assistant message content (PII separated by line breaks)
  - choice 1: tool_call arguments (PII joined together)

What happens for every dataset row (see Setup_Sequence.puml):
  1. POST /mock  -> tell the PresidioMock exactly which PII is in the text,
                    so detection is "perfect" and never the reason for a failure
  2. POST /v1/chat/completions -> send the ORIGINAL text through the PrivacyProxy;
                    the proxy pseudonymizes it, sends it to the LlmMock, and
                    de-pseudonymizes the answer
  3. GET /last   -> ask the LlmMock what it actually received
  4. compare:
       pseudonymized       = the LlmMock never saw a real PII value
       content_restored    = choice 0 (message content) contains the original text again
       tool_calls_restored = choice 1 (tool_call args) contains the original text again

How to run:
  1. docker compose up -d      (starts PresidioMock + PrivacyProxy)
  2. python llm_mock.py        (in a second terminal)
  3. python experiment.py
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

# -------------------------
# Configuration (from .env)
# -------------------------
PROXY_PORT         = os.environ.get("PROXY_PORT", "8080")
LLM_MOCK_PORT      = os.environ.get("LLM_MOCK_PORT", "5005")
PRESIDIO_MOCK_PORT = os.environ.get("PRESIDIO_MOCK_PORT", "5002")

PRIVACY_PROXY    = f"http://localhost:{PROXY_PORT}"
LLM_MOCK = f"http://localhost:{LLM_MOCK_PORT}"
PRESIDIO_MOCK     = f"http://localhost:{PRESIDIO_MOCK_PORT}"

SAMPLE_SIZE = int(os.environ.get("SAMPLE_SIZE", "100"))  # rows PER language
LANGUAGES   = [lang.strip().lower() for lang in os.environ.get("LANGUAGES", "de,en").split(",")]
# -------------------------

def make_sure_services_are_running():
    """Fail early with a clear message instead of 100 confusing errors later."""
    services = [
        ("PresidioMock", f"http://localhost:{PRESIDIO_MOCK_PORT}/health"),
        ("LlmMock",      f"http://localhost:{LLM_MOCK_PORT}/health"),
        ("PrivacyProxy", f"http://localhost:{PROXY_PORT}/"),
    ]
    for name, url in services:
        try:
            requests.get(url, timeout=3)
        except requests.ConnectionError:
            raise SystemExit(f"{name} is not reachable at {url} -- "
                             f"did you start it? (see 'How to run' at the top of this file)")

# What a proxy placeholder looks like, e.g. [PII_a1b2c3d4e5f6a7b8]
PLACEHOLDER_PATTERN = r"\[[A-Z0-9_]+_[0-9a-f]{16}\]"


def looks_pseudonymized(row, seen_text):
    """True if the LLM saw the text in the correctly pseudonymized form.

    We rebuild what the LLM SHOULD have seen: the original text, but with every
    annotated PII span (privacy_mask start/end) replaced by "some placeholder".
    Then we simply check that this expected shape appears in seen_text.

    Why not just check "no PII value appears in seen_text"? Because some rows
    contain the same word twice - once as PII and once as a normal word (e.g.
    "Als Administrator, Administrator im Bereich..." where only the second one
    is annotated). Only the ANNOTATED occurrence must be replaced; the other one
    is allowed to stay. Comparing by position instead of by value handles that.
    """
    source   = row["source_text"]
    pattern  = ""
    last_end = 0
    for pii in sorted(row["privacy_mask"], key=lambda p: p["start"]):
        pattern += re.escape(source[last_end:pii["start"]])  # normal text: must be unchanged
        pattern += PLACEHOLDER_PATTERN                       # PII span: must be a placeholder
        last_end = pii["end"]
    pattern += re.escape(source[last_end:])                  # rest after the last PII

    return re.search(pattern, seen_text) is not None


def process_one_row(row):
    """Send one dataset row through the whole chain and return the three results."""
    pii_values = [pii["value"] for pii in row["privacy_mask"]]

    # 1) give the PresidioMock the solution: exactly these spans are PII
    requests.post(PRESIDIO_MOCK + "/mock", json=row["privacy_mask"], timeout=10).raise_for_status()

    # 2) original text -> proxy (pseudonymize) -> LlmMock -> proxy (de-pseudonymize) -> answer
    response = requests.post(PRIVACY_PROXY + "/v1/chat/completions", timeout=60, json={
        "model": "mocked-llm",
        "messages": [{"role": "user", "content": row["source_text"]}],
        "stream": False
    })
    response.raise_for_status()
    answer = response.json()

    # 3) what did the LlmMock actually see? (= what a real cloud LLM would have seen)
    seen_messages = requests.get(LLM_MOCK + "/last", timeout=10).json()["last_received_messages"]
    seen_text = "\n".join(message.get("content", "") for message in seen_messages)

    # 4a) pseudonymized = the LLM saw placeholders exactly where the annotation
    #     says PII is (and the rest of the text unchanged)
    pseudonymized = looks_pseudonymized(row, seen_text)

    if not pseudonymized:
        print("seen: {{{ " + seen_text + "}}}")
        print(pii_values)

    # 4b) de-pseudonymization in the normal message content (choice 0):
    #     the answer must contain the ORIGINAL text again, word for word.
    #     (any placeholder the proxy failed to replace breaks this)
    content = answer["choices"][0]["message"]["content"]
    content_restored = row["source_text"] in content

    # 4c) de-pseudonymization in the tool_call arguments (choice 1):
    #     parse the arguments JSON, the pii_text inside must contain the original
    #     text again. If the proxy broke the JSON while replacing -> failure.
    tool_calls_restored = False
    if len(answer["choices"]) > 1:
        tool_calls = answer["choices"][1]["message"].get("tool_calls") or []
        if tool_calls:
            try:
                arguments = json.loads(tool_calls[0]["function"]["arguments"])
                tool_calls_restored = row["source_text"] in arguments["pii_text"]
            except (json.JSONDecodeError, KeyError):
                tool_calls_restored = False

    return {
        "id": row["id"],
        "language": row["language"],
        "pii_count": len(pii_values),
        "pseudonymized": pseudonymized,
        "content_restored": content_restored,
        "tool_calls_restored": tool_calls_restored,
        "mode": "non-stream",
        "error": "",
    }


def process_one_row_streaming(row):
    """Same as process_one_row, but with "stream": True.

    The answer now arrives as many small Server-Sent-Events lines ("data: {...}").
    We glue the pieces back together (content pieces from choice 0, tool_call
    argument pieces from choice 1) and then run exactly the same three checks.
    """
    pii_values = [pii["value"] for pii in row["privacy_mask"]]

    # 1) give the PresidioMock the solution: exactly these spans are PII
    requests.post(PRESIDIO_MOCK + "/mock", json=row["privacy_mask"], timeout=10).raise_for_status()

    # 2) original text -> proxy -> LlmMock -> proxy -> answer, this time as a stream
    response = requests.post(PRIVACY_PROXY + "/v1/chat/completions", timeout=60, stream=True, json={
        "model": "mocked-llm",
        "messages": [{"role": "user", "content": row["source_text"]}],
        "stream": True
    })
    response.raise_for_status()

    # glue the stream back together
    content   = ""
    tool_args = ""
    for line in response.iter_lines(decode_unicode=True):
        if not line or not line.startswith("data: "):
            continue
        data = line[len("data: "):] # line[6:], take all the content except the leading "data: "
        if data == "[DONE]":
            break
        chunk = json.loads(data)
        for choice in chunk.get("choices", []):
            delta = choice.get("delta", {})
            if choice.get("index") == 0:
                content += delta.get("content") or ""
            elif choice.get("index") == 1:
                for tool_call in delta.get("tool_calls") or []:
                    tool_args += tool_call.get("function", {}).get("arguments") or ""

    # 3) what did the LlmMock actually see?
    seen_messages = requests.get(LLM_MOCK + "/last", timeout=10).json()["last_received_messages"]
    seen_text = "\n".join(message.get("content", "") for message in seen_messages)

    # 4) exactly the same three checks as in the non-stream case
    pseudonymized    = looks_pseudonymized(row, seen_text)
    content_restored = row["source_text"] in content

    tool_calls_restored = False
    if tool_args:
        try:
            arguments = json.loads(tool_args)
            tool_calls_restored = row["source_text"] in arguments["pii_text"]
        except (json.JSONDecodeError, KeyError):
            tool_calls_restored = False

    return {
        "id": row["id"],
        "language": row["language"],
        "pii_count": len(pii_values),
        "pseudonymized": pseudonymized,
        "content_restored": content_restored,
        "tool_calls_restored": tool_calls_restored,
        "mode": "stream",
        "error": "",
    }


make_sure_services_are_running()

print("Loading dataset ai4privacy/pii-masking-200k ...")
dataset = load_dataset("ai4privacy/pii-masking-200k", split="train")

results = []

# ---------------------------------------------------------------
# First pass: Non-Streaming rows
# ---------------------------------------------------------------
for language in LANGUAGES:
    # all rows of this language that contain at least one PII value
    subset = dataset.filter(lambda row: row["language"] == language)
    subset = subset.select(range(min(SAMPLE_SIZE, len(subset))))
    print(f"\n== language '{language}': {len(subset)} rows ==")

    for number, row in enumerate(subset, start=1):

        try:
            result = process_one_row(row)
        except Exception as error:
            result = {"id": row["id"], "language": row["language"],
                      "pii_count": len(row["privacy_mask"]),
                      "pseudonymized": False, "content_restored": False,
                      "tool_calls_restored": False, "mode": "non-stream",
                      "error": str(error)[:120]}

        results.append(result)

        if result["error"]:
            print(f"[{language} {number:3d}/{len(subset)}] ERROR: {result['error']}")
            print(row)
        else:
            print(f"[non-stream, {language} {number:3d}/{len(subset)}] "
                  f"pseudonymized={'OK' if result['pseudonymized'] else 'FAIL'}  "
                  f"depseudonymized content={'OK' if result['content_restored'] else 'FAIL'}  "
                  f"depseudonymized tool_calls={'OK' if result['tool_calls_restored'] else 'FAIL'}  "
                  f"({result['pii_count']} PII)")

# ---------------------------------------------------------------
# Second pass: the SAME rows again, but with streaming responses.
# ---------------------------------------------------------------
for language in LANGUAGES:
    subset = dataset.filter(lambda row: row["language"] == language)
    subset = subset.select(range(min(SAMPLE_SIZE, len(subset))))
    print(f"\n== STREAMING pass, language '{language}': {len(subset)} rows ==")

    for number, row in enumerate(subset, start=1):

        try:
            result = process_one_row_streaming(row)
        except Exception as error:
            result = {"id": row["id"], "language": row["language"],
                      "pii_count": len(row["privacy_mask"]),
                      "pseudonymized": False, "content_restored": False,
                      "tool_calls_restored": False, "mode": "stream",
                      "error": str(error)[:120]}
        results.append(result)

        if result["error"]:
            print(f"[{language} {number:3d}/{len(subset)}] ERROR: {result['error']}")
        else:
            print(f"[stream, {language} {number:3d}/{len(subset)}] "
                  f"pseudonymized={'OK' if result['pseudonymized'] else 'FAIL'}  "
                  f"depseudonymized content={'OK' if result['content_restored'] else 'FAIL'}  "
                  f"depseudonymized tool_calls={'OK' if result['tool_calls_restored'] else 'FAIL'}  "
                  f"({result['pii_count']} PII)")

print("\n== Summary ==")
for mode in ("non-stream", "stream"):
    mode_results = [result for result in results if result["mode"] == mode]
    if not mode_results:
        continue
    print(f"-- {mode} --")
    for check in ("pseudonymized", "content_restored", "tool_calls_restored"):
        good = sum(1 for result in mode_results if result[check])
        print(f"  {check:20}: {good}/{len(mode_results)}")

if results:
    timestamp_str = datetime.now().strftime("%Y%m%d_%H%M%S")
    output_dir = Path(f"results_{timestamp_str}")
    output_dir.mkdir(parents=True, exist_ok=True)
    file_path = output_dir / "results.csv"
    with open(file_path, "w", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=results[0].keys())
        writer.writeheader()
        writer.writerows(results)

    print(f"\nSaved per-row results to {file_path}")
