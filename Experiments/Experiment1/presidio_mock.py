"""
Presidio Mock for Experiment 1.

A stand-in for the real Presidio Analyzer. It does NOT analyze any text. Instead
the experiment tells it - just before each request - WHERE the PII is (the
dataset's privacy_mask). When PrivacyProxy then asks it to "analyze" the text, it
simply hands back those positions in Presidio's response format.

Why: this makes the test deterministic. We know exactly what "Presidio" finds, so
we can check whether PrivacyProxy correctly hides that PII from the LLM and
restores it again - without depending on Presidio's real detection quality.

Endpoints:
  POST /send-solution  -> experiment stores the PII spans for the NEXT analyze call
  POST /analyze        -> PrivacyProxy asks for the PII; returns the stored spans
  GET  /health         -> for the Docker health check

Run (local):  python presidio_mock.py   (listens on http://0.0.0.0:5002)
Run (Docker): runs as service "presidio-mock" in docker-compose.yml
"""
import os
import re

from flask import Flask, request, jsonify

app = Flask(__name__)

# The PII spans for the NEXT /analyze call, already in Presidio's response format.
_entities: list[dict] = []


def entity_type_from_label(label):
    """Turn a dataset label into a placeholder entity type.

    The proxy's placeholder is [TYPE_<hash>], and TYPE must be uppercase letters,
    digits or underscore. So we uppercase the dataset label (e.g. "FirstName" ->
    "FIRSTNAME") and keep only those characters; if nothing is left we use "PII".
    The proxy then produces e.g. [FIRSTNAME_1a2b3c4d5e6f7a8b].
    """
    cleaned = re.sub(r"[^A-Z0-9_]", "", (label or "").upper())
    return cleaned or "PII"


@app.post("/send-solution")
def send_solution():
    """Store where the PII is for the next request coming from PrivacyProxy.

    Body: the dataset's privacy_mask, e.g.
        [{"value": "John", "start": 6, "end": 10, "label": "FIRSTNAME"}, ...]
    We only need each span's start/end and turn it into a Presidio entity.
    """
    global _entities
    privacy_mask = request.get_json(force=True)
    _entities = [
        {
            "entity_type": entity_type_from_label(item.get("label", "")),
            "start": item["start"],
            "end": item["end"],
            "score": 1.0,
        }
        for item in privacy_mask
    ]
    return jsonify({"ok": True, "count": len(_entities)})


@app.post("/analyze")
def analyze():
    """PrivacyProxy calls this to "analyze" the text. We ignore the request body
    and return the spans the experiment gave us via /send-solution."""
    return jsonify(_entities)


@app.get("/health")
def health():
    return jsonify({"status": "ok"})


if __name__ == "__main__":
    # 0.0.0.0 so PrivacyProxy can reach the mock from another container.
    port = int(os.environ.get("PORT", 5002))
    app.run(host="0.0.0.0", port=port)
