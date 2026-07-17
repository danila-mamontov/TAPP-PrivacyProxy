import json

from flask import Flask, Response, jsonify, request

# create new Flask application at current file system
app = Flask(__name__)

# Small on purpose: splits placeholders like [FIRSTNAME_...] across streamed
# chunks, which stress-tests the proxy's streaming de-anonymizer (it must
# reassemble a placeholder that arrives in several pieces).
STREAM_CHUNK_SIZE = 8


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

    # messages array looks like this:
    """
    {
      "messages": [
        {"role": "system",      "content": "Du bist ein Assistent..."       },
        {"role": "user",        "content": "Mein Name ist [PERSON_abc...]"  },
        {"role": "assistant",   "content": "Wie kann ich helfen?"           },
        {"role": "user",        "content": "Ruf [PHONE_def...] an"          }
      ]
    }
    """
    messages = body.get("messages", [])

    global _last_request_messages
    _last_request_messages = messages

    # aggregate all message contents in the echo so that the proxy
    # deanonymizes all placeholders and the experiment can verify them all.
    all_content = "\n".join(
        m.get("content", "") or ""
        for m in messages
    )

    model = body.get("model", "mock")

    # Streaming: return the same echo as an SSE stream (small chunks).
    if body.get("stream"):
        return Response(_sse_stream(all_content, model), mimetype="text/event-stream")

    return jsonify({
        "id": "mock-llm",
        "object": "chat.completion",
        "created": 0,
        "model": model,
        "choices": [{
            "index": 0,
            "message": {"role": "assistant", "content": all_content},
            "finish_reason": "stop",
        }],
        "usage": {"prompt_tokens": 0, "completion_tokens": 0, "total_tokens": 0},
    })

@app.get("/health")
def health():
    return jsonify({"status": "ok"})

@app.get("/last")
def last():
    return jsonify({
        "privacyproxy_sent": {
            "messages": _last_request_messages
        },
    })
