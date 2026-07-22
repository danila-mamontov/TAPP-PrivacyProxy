import os
import time

import requests
from flask import Flask, request, jsonify, Response

app = Flask(__name__)

UPSTREAM = os.environ.get("UPSTREAM", "http://host.docker.internal:11434").rstrip("/")

# Recorded exchanges: [{ts, request, response}]
_log: list[dict] = []

def _fwd_headers():
    return {k: v for k, v in request.headers if k.lower() not in ("host", "content-length")}

@app.post("/v1/chat/completions")
def chat_completions():
    raw = request.get_data()
    req_json = request.get_json(force=True, silent=True)
    is_stream = bool(req_json and req_json.get("stream"))

    entry = {"ts": time.time(), "request": req_json, "response": None, "stream": is_stream}
    _log.append(entry)

    r = requests.post(
        f"{UPSTREAM}/v1/chat/completions",
        data=raw,
        headers=_fwd_headers(),
        stream=is_stream,
    )

    if is_stream:
        def generate():
            collected = b""
            for chunk in r.iter_content(chunk_size=None):
                collected += chunk
                yield chunk
            entry["response"] = collected.decode("utf-8", "replace")
        return Response(
            generate(),
            status=r.status_code,
            content_type=r.headers.get("Content-Type", "text/event-stream"),
        )

    entry["response"] = r.text
    return Response(
        r.content,
        status=r.status_code,
        content_type=r.headers.get("Content-Type", "application/json"),
    )

@app.get("/last")
def last():
    return jsonify(_log[-1] if _log else {})

@app.get("/log")
def log():
    return jsonify(_log)

@app.post("/reset")
def reset():
    _log.clear()
    return jsonify({"ok": True})

@app.get("/health")
def health():
    return jsonify({"status": "ok"})

# Everything else (e.g. GET /v1/models that some clients probe) -> pass through.
@app.route("/<path:path>", methods=["GET", "POST"])
def passthrough(path):
    url = f"{UPSTREAM}/{path}"
    if request.method == "GET":
        r = requests.get(url, headers=_fwd_headers(), params=request.args)
    else:
        r = requests.post(url, data=request.get_data(), headers=_fwd_headers())
    return Response(
        r.content,
        status=r.status_code,
        content_type=r.headers.get("Content-Type", "application/json"),
    )

if __name__ == "__main__":
    app.run(host="0.0.0.0", port=int(os.environ.get("PORT", 8000)))
