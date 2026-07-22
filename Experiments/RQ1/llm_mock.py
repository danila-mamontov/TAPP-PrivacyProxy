import json
import os

from flask import Flask, Response, jsonify, request

app = Flask(__name__)

_last_messages: list[dict] = []

# Streaming: the answer is cut into fixed pieces of this many characters.
# 6 is deliberately SMALLER than a placeholder like [PERSON_a1b2...] (~26 chars),
# so every placeholder is guaranteed to be split across chunks -> the proxy has
# to buffer partial placeholders, which is exactly what we want to test.
CHUNK_SIZE = 6


def in_chunks(text):
    """Cut a text into pieces of CHUNK_SIZE characters."""
    return [text[i:i + CHUNK_SIZE] for i in range(0, len(text), CHUNK_SIZE)]


def sse(choice_index, delta, finish_reason=None):
    """One Server-Sent-Events line: a chat.completion.chunk with a single delta."""
    payload = {
        "id": "123",
        "object": "chat.completion.chunk",
        "created": 0,
        "model": "mocked-llm",
        "choices": [
            {"index": choice_index, "delta": delta, "finish_reason": finish_reason}
        ],
    }
    return "data: " + json.dumps(payload) + "\n\n"


def content_delta(piece):
    """Delta for choice 0: one piece of the normal message text."""
    return {"content": piece}


def arguments_delta(piece):
    """Delta for choice 1: one piece of the tool_call arguments text."""
    return {"tool_calls": [{"index": 0, "function": {"arguments": piece}}]}


def stream_response(all_content_breaked, all_content_fusion):
    """The streaming variant of the SAME answer: choice 0 = content in 6-char
    pieces, choice 1 = tool_call arguments in 6-char pieces, taking turns."""
    arguments = json.dumps({"pii_text": all_content_fusion})

    def generate():
        # First chunk of each choice announces what is coming: the role, and for
        # the tool_call its id + function name (the arguments text follows below).
        yield sse(0, {"role": "assistant"})
        yield sse(1, {"role": "assistant", "tool_calls": [
            {"index": 0, "id": "call_123", "type": "function",
             "function": {"name": "get_mocked_function", "arguments": ""}}]})

        # Cut both texts into pieces. While pieces are left, send the next one of
        # each - so content (choice 0) and arguments (choice 1) take turns.
        content_pieces = in_chunks(all_content_breaked)
        args_pieces    = in_chunks(arguments)
        while content_pieces or args_pieces:
            if content_pieces:
                yield sse(0, content_delta(content_pieces.pop(0)))
            if args_pieces:
                yield sse(1, arguments_delta(args_pieces.pop(0)))

        # Finally: finish both choices and end the stream.
        yield sse(0, {}, finish_reason="stop")
        yield sse(1, {}, finish_reason="tool_calls")
        yield "data: [DONE]\n\n"

    return Response(generate(), mimetype="text/event-stream")

@app.post("/v1/chat/completions")
def v1_chat_completions():

    body = request.get_json()

    messages = body["messages"]
    # messages looks like this:
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

    global _last_messages
    _last_messages = messages

    all_content_breaked = "\n".join(
        m.get("content", "")
        for m in messages
    )
    all_content_fusion = "".join(
        m.get("content", "")
        for m in messages
    )

    # Streaming requested? Same answer, but as many small SSE chunks.
    if body.get("stream"):
        return stream_response(all_content_breaked, all_content_fusion)

    return jsonify({
        "id": "123",
        "object": "chat.completion",
        "created": 0,
        "model": "mocked-llm",
        "choices": [
            {
                "index": 0,
                "message": { "role": "assistant", "content": all_content_breaked},
                "finish_reason": "stop"
            },
            {
                "index": 1,
                "message": {
                    "role": "assistant",
                    "content": "",
                    "tool_calls": [
                        {
                            "id": "call_123",
                            "type": "function",
                            "function": {
                                "name": "get_mocked_function",
                                "arguments": json.dumps({"pii_text": all_content_fusion})
                            }
                        }
                    ]
                },
                "finish_reason": "tool_calls"
            }]
    })

@app.route("/health")
def health():
    return jsonify({"status": "ok"})

@app.route("/last")
def last():
    return jsonify({
        "last_received_messages": _last_messages
    })

if __name__ == "__main__":
    # 0.0.0.0 so the PrivacyProxy (inside Docker) can reach this mock on the host.
    port = int(os.environ.get("PORT", 5005))
    app.run(host="0.0.0.0", port=port)