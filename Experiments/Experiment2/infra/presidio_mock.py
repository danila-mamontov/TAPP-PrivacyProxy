"""
Presidio Mock for Experiment 2 — VALUE MATCHING.

Difference to Experiment 1: there, exactly one fixed dataset text was analyzed, so
the mock knew the PII *positions* in advance and just returned them (it ignored the
text). Experiment 2 runs a REAL agent that produces many dynamic messages (the user
prompt, tool calls, tool results, the drafted mail, a final confirmation …), all of
which pass through the proxy. Their PII positions are unknown up front.

So this mock works differently: the orchestrator tells it, per dataset row, the exact
PII *values*. On every /analyze it searches the given text for every occurrence of
those values and returns them as Presidio spans. That gives us "100 % control": we
decide which strings are PII, and the mock detects them perfectly in whatever the
agent writes — in every proxy round.

Endpoints:
  POST /set-pii  -> orchestrator sets the PII values for the current row
                    body: [{"value": "dennis.itzel@picsysteme.de", "type": "EMAIL_ADDRESS"}, ...]
  POST /reset    -> clears the PII values (clean state between rows)
  POST /analyze  -> PrivacyProxy calls this; body {"text": "...", ...} (Presidio format)
                    returns [{"entity_type","start","end","score"}] for every match
  GET  /health   -> Docker health check

Run (local):  python presidio_mock.py     (listens on http://0.0.0.0:5002)
"""
import os
import re

from flask import Flask, request, jsonify

app = Flask(__name__)

# The current row's PII: list of {"value": str, "type": str}.
_pii: list[dict] = []


def clean_type(label: str) -> str:
    """The proxy placeholder is [TYPE_<hash>]; TYPE must be [A-Z0-9_]."""
    cleaned = re.sub(r"[^A-Z0-9_]", "", (label or "").upper())
    return cleaned or "PII"


@app.post("/set-pii")
def set_pii():
    """Set the PII values the mock should detect from now on."""
    global _pii
    body = request.get_json(force=True) or []
    _pii = [
        {"value": item["value"], "type": clean_type(item.get("type", ""))}
        for item in body
        if item.get("value")
    ]
    return jsonify({"ok": True, "count": len(_pii), "pii": _pii})


@app.post("/reset")
def reset():
    global _pii
    _pii = []
    return jsonify({"ok": True})


@app.post("/analyze")
def analyze():
    """PrivacyProxy asks us to analyze a text. We return every occurrence of every
    known PII value as a Presidio span. Matching is exact and case-sensitive so the
    controlled ground truth stays unambiguous."""
    body = request.get_json(force=True) or {}
    text = body.get("text", "") or ""

    results: list[dict] = []
    for item in _pii:
        value = item["value"]
        etype = item["type"]
        if not value:
            continue
        start = 0
        while True:
            idx = text.find(value, start)
            if idx == -1:
                break
            results.append(
                {
                    "entity_type": etype,
                    "start": idx,
                    "end": idx + len(value),
                    "score": 1.0,
                }
            )
            start = idx + len(value)

    return jsonify(results)


@app.get("/health")
def health():
    return jsonify({"status": "ok"})


if __name__ == "__main__":
    # 0.0.0.0 so PrivacyProxy can reach the mock from another container.
    port = int(os.environ.get("PORT", 5002))
    app.run(host="0.0.0.0", port=port)
