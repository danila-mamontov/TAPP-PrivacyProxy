import json
import os
import time
import uuid

import requests
from fastapi import FastAPI, Request, Response
from fastapi.responses import JSONResponse, StreamingResponse

PII_SHIELD_URL = os.environ.get("PII_SHIELD_URL", "http://pii-shield:8000").rstrip("/")
UPSTREAM = os.environ.get("UPSTREAM", "http://llm-recorder:8000").rstrip("/")
LANGUAGE_MODE = os.environ.get("PII_SHIELD_LANGUAGE_MODE", "force_en").strip().lower()
REQUEST_TIMEOUT = float(os.environ.get("PII_SHIELD_REQUEST_TIMEOUT", "120"))

app = FastAPI(title="PII Shield OpenAI Adapter")


def _headers(request: Request) -> dict[str, str]:
    blocked = {"host", "content-length"}
    return {k: v for k, v in request.headers.items() if k.lower() not in blocked}


def _language(messages: list[dict]) -> str:
    if LANGUAGE_MODE in {"en", "force_en"}:
        return "en"
    if LANGUAGE_MODE == "de":
        return "de"
    # Optional lightweight auto mode. The benchmark uses force_en because the
    # pinned upstream PII Shield engine currently declares only English support.
    text = " ".join(
        str(m.get("content", ""))
        for m in messages
        if isinstance(m, dict) and isinstance(m.get("content"), str)
    ).lower()
    german_markers = (
        " der ", " die ", " das ", " und ", " ein ", " eine ", " bitte ",
        " termin ", " sende ", " erstelle ", " kalender ", " e-mail ",
    )
    return "de" if any(marker in f" {text} " for marker in german_markers) else "en"


def _shield_anonymize(messages: list[dict]) -> tuple[list[dict], str]:
    # Serialize the complete message structure. This protects content, tool-call
    # arguments and tool messages with one PII Shield session, so placeholders can
    # be restored even when the LLM copies a placeholder from one field to another.
    packed = json.dumps(messages, ensure_ascii=False, separators=(",", ":"))
    language = _language(messages)
    r = requests.post(
        f"{PII_SHIELD_URL}/anonymize_unique",
        json={"text": packed, "language": language},
        timeout=REQUEST_TIMEOUT,
    )
    r.raise_for_status()
    payload = r.json()
    anonymized = json.loads(payload["anonymized_text"])
    return anonymized, payload["id"]


def _shield_deanonymize(payload: dict, session_id: str) -> dict:
    packed = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
    r = requests.post(
        f"{PII_SHIELD_URL}/deanonymize",
        json={"id": session_id, "text": packed},
        timeout=REQUEST_TIMEOUT,
    )
    r.raise_for_status()
    restored = r.json()["text"]
    return json.loads(restored)


def _post_upstream(req: dict) -> requests.Response:
    forwarded = dict(req)
    # The adapter currently buffers the upstream completion. When OpenClaw asks
    # for a stream, we synthesize a valid single-chunk SSE response below.
    forwarded["stream"] = False
    return requests.post(
        f"{UPSTREAM}/v1/chat/completions",
        json=forwarded,
        timeout=REQUEST_TIMEOUT,
    )


@app.post("/v1/chat/completions")
async def chat_completions(request: Request):
    req = await request.json()
    messages = req.get("messages")
    if not isinstance(messages, list):
        return JSONResponse({"error": {"message": "messages must be a list"}}, status_code=400)

    original_stream = bool(req.get("stream"))
    try:
        protected_messages, session_id = _shield_anonymize(messages)
        protected_request = dict(req)
        protected_request["messages"] = protected_messages
        upstream = _post_upstream(protected_request)

        content_type = upstream.headers.get("content-type", "application/json")
        if upstream.status_code < 200 or upstream.status_code >= 300:
            return Response(upstream.content, status_code=upstream.status_code, media_type=content_type.split(";")[0])

        upstream_json = upstream.json()
        restored = _shield_deanonymize(upstream_json, session_id)

        if not original_stream:
            return JSONResponse(restored)

        # OpenAI-compatible single-chunk stream. This is deliberately buffered so
        # de-anonymization is completed before any restored PII is sent downstream.
        chunks = []
        for idx, choice in enumerate(restored.get("choices", [])):
            message = choice.get("message") or {}
            delta = {}
            for key in ("role", "content", "tool_calls", "function_call", "refusal"):
                if key in message:
                    delta[key] = message[key]
            chunk = {
                "id": restored.get("id", f"pii-shield-{uuid.uuid4().hex}"),
                "object": "chat.completion.chunk",
                "created": restored.get("created", int(time.time())),
                "model": restored.get("model", req.get("model", "unknown")),
                "choices": [{
                    "index": idx,
                    "delta": delta,
                    "finish_reason": choice.get("finish_reason"),
                }],
            }
            chunks.append("data: " + json.dumps(chunk, ensure_ascii=False) + "\n\n")
        chunks.append("data: [DONE]\n\n")
        return StreamingResponse(iter(chunks), media_type="text/event-stream")

    except requests.HTTPError as exc:
        detail = getattr(exc.response, "text", str(exc))
        return JSONResponse({"error": {"message": detail[:2000], "type": "pii_shield_upstream_error"}}, status_code=502)
    except (requests.RequestException, json.JSONDecodeError, KeyError, TypeError, ValueError) as exc:
        return JSONResponse({"error": {"message": str(exc), "type": "pii_shield_adapter_error"}}, status_code=502)


@app.get("/v1/models")
def models():
    r = requests.get(f"{UPSTREAM}/v1/models", timeout=REQUEST_TIMEOUT)
    return Response(r.content, status_code=r.status_code, media_type=r.headers.get("content-type", "application/json"))


@app.get("/health")
def health():
    return {"status": "ok"}


@app.get("/{path:path}")
def passthrough_get(path: str):
    r = requests.get(f"{UPSTREAM}/{path}", timeout=REQUEST_TIMEOUT)
    return Response(r.content, status_code=r.status_code, media_type=r.headers.get("content-type", "application/json"))
