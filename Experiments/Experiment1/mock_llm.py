"""
Mock LLM for Experiment 1.

A minimal, OpenAI-compatible chat endpoint that does NOT call a real model.
Instead it:
  - remembers the message it received (already anonymized by the proxy), and
  - returns exactly that message as its answer ("echo").

This lets us measure what the PrivacyProxy sends to the LLM (= the anonymized
version). On the way back, the proxy replaces the placeholders in the echoed
answer with the original values again.

The mock supports BOTH response modes:
  - non-streaming (default): returns one JSON completion.
  - streaming ("stream": true): returns an SSE stream of chunks. We split the
    echoed text into deliberately small chunks so placeholders like
    [PERSON_....] get cut across chunk boundaries - that stress-tests the proxy's
    streaming de-anonymizer, which must reassemble them before restoring.

Endpoints:
  POST /v1/chat/completions  -> echo + record (JSON or SSE, depending on "stream")
  GET  /v1/models            -> dummy list (for the proxy's connectivity check)
  GET  /last                 -> last received (anonymized) user message
  POST /reset                -> clear the recording
  GET  /health               -> for the Docker health check

Run (local):  python mock_llm.py   (listens on http://0.0.0.0:5005)
Run (Docker): runs as service "mock-llm" in docker-compose.yml
"""
import json
import os

from flask import Flask, Response, request, jsonify

app = Flask(__name__)

# Recording: the last user message the mock received (already anonymized)
_received: list[str] = []

# Small on purpose: guarantees placeholders are split across streaming chunks.
STREAM_CHUNK_SIZE = 12


def _sse_stream(content, model):
    """Echo `content` back as an OpenAI-style SSE stream, in small chunks."""
    for start in range(0, len(content), STREAM_CHUNK_SIZE):
        piece = content[start:start + STREAM_CHUNK_SIZE]
        chunk = {
            "id": "mock-llm", "object": "chat.completion.chunk", "created": 0, "model": model,
            "choices": [{"index": 0, "delta": {"content": piece}, "finish_reason": None}],
        }
        yield f"data: {json.dumps(chunk)}\n\n"

    done = {
        "id": "mock-llm", "object": "chat.completion.chunk", "created": 0, "model": model,
        "choices": [{"index": 0, "delta": {}, "finish_reason": "stop"}],
    }
    yield f"data: {json.dumps(done)}\n\n"
    yield "data: [DONE]\n\n"


@app.post("/v1/chat/completions")
def chat_completions():
    body = request.get_json(force=True)
    messages = body.get("messages", [])
    user_messages = [m for m in messages if m.get("role") == "user"]
    anonymized = user_messages[-1]["content"] if user_messages else ""
    _received.append(anonymized)

    model = body.get("model", "mock")

    # Echo the (anonymized) input back -> the proxy de-anonymizes it on the way out.
    if body.get("stream"):
        return Response(_sse_stream(anonymized, model), mimetype="text/event-stream")

    return jsonify({
        "id": "mock-llm",
        "object": "chat.completion",
        "created": 0,
        "model": model,
        "choices": [{
            "index": 0,
            "message": {"role": "assistant", "content": anonymized},
            "finish_reason": "stop",
        }],
        "usage": {"prompt_tokens": 0, "completion_tokens": 0, "total_tokens": 0},
    })


@app.get("/v1/models")
def models():
    return jsonify({"object": "list", "data": [{"id": "mock", "object": "model"}]})


@app.get("/last")
def last():
    return jsonify({"content": _received[-1] if _received else None})


@app.post("/reset")
def reset():
    _received.clear()
    return jsonify({"ok": True})


@app.get("/health")
def health():
    return jsonify({"status": "ok"})


if __name__ == "__main__":
    # 0.0.0.0 instead of 127.0.0.1 so the mock is reachable from other
    # containers (PrivacyProxy) inside the Docker network.
    port = int(os.environ.get("PORT", 5005))
    app.run(host="0.0.0.0", port=port)