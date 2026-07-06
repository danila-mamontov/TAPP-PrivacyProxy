from flask import Flask, jsonify, request

# create new Flask application at current file system
app = Flask(__name__)

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

    return jsonify({
        "id": "mock-llm",
        "object": "chat.completion",
        "created": 0,
        "model": body.get("model", "mock"),
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