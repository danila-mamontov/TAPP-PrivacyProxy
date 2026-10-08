import json
import os
import time
import uuid

import requests
from fastapi import FastAPI, Request, Response
from fastapi.responses import JSONResponse, StreamingResponse

PII_SHIELD_EN_URL = os.environ.get("PII_SHIELD_EN_URL", "http://pii-shield-en:8000").rstrip("/")
PII_SHIELD_DE_URL = os.environ.get("PII_SHIELD_DE_URL", "http://pii-shield-de:8000").rstrip("/")
UPSTREAM = os.environ.get("UPSTREAM", "http://llm-recorder:8000").rstrip("/")
REQUEST_TIMEOUT = float(os.environ.get("PII_SHIELD_REQUEST_TIMEOUT", "120"))

app = FastAPI(title="PII Shield OpenAI Adapter")


def _german(messages: list[dict]) -> bool:
    # Detect language from the most recent user message only. The OpenClaw
    # system prompt is English even for German benchmark rows.
    for message in reversed(messages):
        if message.get("role") != "user":
            continue
        content = message.get("content")
        if not isinstance(content, str):
            continue
        text = f" {content.lower()} "
        if any(ch in text for ch in "äöüß"):
            return True
        markers = (
            " der ", " die ", " das ", " den ", " dem ", " des ",
            " und ", " oder ", " ein ", " eine ", " bitte ",
            " erstellen ", " erstelle ", " sende ", " sende ",
            " termin ", " kalender ", " nachricht ", " email ",
            " e-mail ", " teilnehmer ", " beschreibung ",
        )
        return any(marker in text for marker in markers)
    return False


def _shield_url(messages: list[dict]) -> str:
    return PII_SHIELD_DE_URL if _german(messages) else PII_SHIELD_EN_URL


def _headers(request: Request) -> dict[str, str]:
    blocked = {"host", "content-length"}
    return {k: v for k, v in request.headers.items() if k.lower() not in blocked}


def _shield_anonymize(messages: list[dict]) -> tuple[list[dict], str]:
    # Protect the complete message JSON in one Shield session. This covers
    # message content, tool_call arguments and tool messages.
    packed = json.dumps(messages, ensure_ascii=False, separators=(",", ":"))
    r = requests.post(
        f"{_shield_url(messages)}/anonymize_unique",
        json={"text": packed, "language": "en"},
        timeout=REQUEST_TIMEOUT,
    )
    r.raise_for_status()
    payload = r.json()
    return json.loads(payload["anonymized_text"]), payload["id"]


def _shield_deanonymize(response_payload: dict, session_id: str, shield_url: str) -> dict:
    packed = json.dumps(response_payload, ensure_ascii=False, separators=(",", ":"))
    r = requests.post(
        f"{shield_url}/deanonymize",
        json={"id": session_id, "text": packed},
        timeout=REQUEST_TIMEOUT,
    )
    r.raise_for_status()
    return json.loads(r.json()["text"])


def _post_upstream(req: dict) -> requests.Response:
    forwarded = dict(req)
    forwarded["stream"] = False
    return requests.post(
        f"{UPSTREAM}/v1/chat/completions",
        json=forwarded,
        timeout=REQUEST_TIMEOUT,
    )


def _stream_response(restored: dict, requested_model: str):
    chunks = []
    choices = restored.get("choices", [])
    for idx, choice in enumerate(choices):
        message = choice.get("message") or {}
        delta = {
            key: message[key]
            for key in ("role", "content", "tool_calls", "function_call", "refusal")
            if key in message
        }
        chunk = {
            "id": restored.get("id", f"pii-shield-{uuid.uuid4().hex}"),
            "object": "chat.completion.chunk",
            "created": restored.get("created", int(time.time())),
            "model": restored.get("model", requested_model),
            "choices": [{
                "index": idx,
                "delta": delta,
                "finish_reason": choice.get("finish_reason"),
            }],
        }
        chunks.append("data: " + json.dumps(chunk, ensure_ascii=False) + "\n\n")
    chunks.append("data: [DONE]\n\n")
    return StreamingResponse(iter(chunks), media_type="text/event-stream")


@app.post("/v1/chat/completions")
async def chat_completions(request: Request):
    req = await request.json()
    messages = req.get("messages")
    if not isinstance(messages, list):
        return JSONResponse({"error": {"message": "messages must be a list"}}, status_code=400)

    stream = bool(req.get("stream"))
    shield_url = _shield_url(messages)

    try:
        protected_messages, session_id = _shield_anonymize(messages)
        protected_request = dict(req)
        protected_request["messages"] = protected_messages

        upstream = _post_upstream(protected_request)
        if upstream.status_code < 200 or upstream.status_code >= 300:
            return Response(
                upstream.content,
                status_code=upstream.status_code,
                media_type=upstream.headers.get("content-type", "application/json").split(";")[0],
            )

        restored = _shield_deanonymize(upstream.json(), session_id, shield_url)
        if not stream:
            return JSONResponse(restored)
        return _stream_response(restored, req.get("model", "unknown"))

    except requests.HTTPError as exc:
        detail = getattr(exc.response, "text", str(exc))
        return JSONResponse(
            {"error": {"message": detail[:2000], "type": "pii_shield_upstream_error"}},
            status_code=502,
        )
    except (requests.RequestException, json.JSONDecodeError, KeyError, TypeError, ValueError) as exc:
        return JSONResponse(
            {"error": {"message": str(exc), "type": "pii_shield_adapter_error"}},
            status_code=502,
        )


@app.get("/v1/models")
def models():
    r = requests.get(f"{UPSTREAM}/v1/models", timeout=REQUEST_TIMEOUT)
    return Response(
        r.content,
        status_code=r.status_code,
        media_type=r.headers.get("content-type", "application/json"),
    )


@app.get("/health")
def health():
    return {"status": "ok"}
