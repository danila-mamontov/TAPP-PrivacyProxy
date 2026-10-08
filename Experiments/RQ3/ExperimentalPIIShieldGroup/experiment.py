import csv
import json
import os
import re
import shutil
import subprocess
import time
from datetime import datetime
from pathlib import Path

import caldav
import requests
from dotenv import load_dotenv

HERE = Path(__file__).resolve().parent
DATASET = HERE.parent / "ExperimentalPresidioGroup" / "dataset.json"
load_dotenv(HERE / "infrastructure" / ".env")

MCP_SERVER = f"http://localhost:{os.environ.get('TOOLS_PORT', '3101')}"
MAILPIT_UI = f"http://localhost:{os.environ.get('MAILPIT_UI_PORT', '8026')}"
CALDAV = f"http://localhost:{os.environ.get('CALDAV_PORT', '5233')}/"
LLM_RECORDER = f"http://localhost:{os.environ.get('RECORDER_PORT', '8001')}"
PROXY = f"http://127.0.0.1:{os.environ.get('PROXY_PORT', '8081')}"

CALDAV_USER, CALDAV_PASS = "test", "test"
CALDAV_CAL = os.environ.get("CALDAV_CAL", "exp_rq3_experimentalpiishieldgroup")
OPENCLAW_CONTAINER = os.environ.get("OPENCLAW_CONTAINER", "openclaw-openclaw-gateway-1")
ITERATIONS = int(os.environ.get("ITERATIONS", "5"))
TIMEOUT = int(os.environ.get("AGENT_TIMEOUT", "180"))
GROUP = os.environ.get("GROUP", "exp-rq3-experimentalpiishieldgroup")
LOCKFILE = Path("/tmp/openclaw_piishield_experiment_run.lock")
RUN_ID = datetime.now().strftime("%Y%m%d%H%M%S")

PLACEHOLDER = r"\{\{[A-Z0-9_]+_[0-9]+\}\}"


def _model_ids_from_openclaw() -> list[str]:
    raw = os.environ.get("MODELS", "").strip()
    if raw:
        candidates = [x.strip() for x in raw.split(",") if x.strip()]
    else:
        config_path = Path.home() / ".openclaw" / "openclaw.json"
        if not config_path.exists():
            raise SystemExit("No MODELS env and ~/.openclaw/openclaw.json was not found.")
        config = json.loads(config_path.read_text(encoding="utf-8"))
        providers = config.get("models", {}).get("providers", {})
        provider = os.environ.get("PII_PROVIDER", "piishield")
        models = providers.get(provider, {}).get("models", [])
        if not models:
            for alternate in ("piishield", "pii-shield", "privacyproxy"):
                if alternate in providers and providers[alternate].get("models"):
                    provider = alternate
                    models = providers[alternate]["models"]
                    break
        if not models:
            raise SystemExit(
                "Could not find a PII Shield provider model list in openclaw.json. "
                "Set MODELS=model1:cloud,...,model7:cloud explicitly."
            )
        candidates = [m["id"] if isinstance(m, dict) else str(m) for m in models]

    normalized = []
    for model in candidates:
        model = model.split("/", 1)[1] if "/" in model else model
        normalized.append(model)

    unique = list(dict.fromkeys(normalized))
    if len(unique) != 7:
        raise SystemExit(
            f"PII Shield benchmark requires exactly 7 unique models; got {len(unique)}: {unique}"
        )
    non_cloud = [m for m in unique if not m.endswith(":cloud")]
    if non_cloud:
        raise SystemExit(f"All seven models must be Ollama Cloud models ending ':cloud': {non_cloud}")
    return unique


def _pii_provider() -> str:
    return os.environ.get("PII_PROVIDER", "piishield")


def reset_mcp_and_tools():
    try:
        requests.post(f"{MCP_SERVER}/reset", timeout=10)
    except requests.RequestException:
        pass


def clear_workspace_memory():
    workspace = Path.home() / ".openclaw" / "workspace"
    memory_dir = workspace / "memory"
    memory_file = workspace / "MEMORY.md"
    if memory_dir.exists():
        for path in memory_dir.iterdir():
            if path.is_file():
                path.unlink()
            else:
                shutil.rmtree(path, ignore_errors=True)
    memory_file.parent.mkdir(parents=True, exist_ok=True)
    memory_file.write_text("# MEMORY.md\n", encoding="utf-8")


def clear_openclaw_sessions():
    sessions = Path.home() / ".openclaw" / "agents" / "main" / "sessions"
    if sessions.exists():
        for path in sessions.iterdir():
            if path.is_file():
                path.unlink()
            else:
                shutil.rmtree(path, ignore_errors=True)


def wait_until_gateway_healthy():
    for _ in range(60):
        inspect = subprocess.run(
            ["docker", "inspect", "-f", "{{.State.Health.Status}}", OPENCLAW_CONTAINER],
            capture_output=True, text=True,
        )
        if inspect.stdout.strip() == "healthy":
            return
        time.sleep(2)
    raise SystemExit("Gateway did not become healthy.")


def stop_stray_agent():
    print("      timeout: restarting gateway")
    subprocess.run(["docker", "restart", OPENCLAW_CONTAINER], capture_output=True, text=True)
    wait_until_gateway_healthy()


def reset_agent_store_for_run():
    db = Path.home() / ".openclaw" / "agents" / "main" / "agent" / "openclaw-agent.sqlite"
    print("Resetting OpenClaw agent store ...")
    subprocess.run(["docker", "stop", OPENCLAW_CONTAINER], capture_output=True, text=True, check=True)
    for path in db.parent.glob(db.name + "*"):
        path.unlink()
    subprocess.run(["docker", "start", OPENCLAW_CONTAINER], capture_output=True, text=True, check=True)
    wait_until_gateway_healthy()


def clean_all():
    reset_mcp_and_tools()
    clear_workspace_memory()
    clear_openclaw_sessions()
    try:
        requests.post(f"{LLM_RECORDER}/reset", timeout=5)
    except requests.RequestException:
        pass


def reply_text(agent_result: dict) -> str:
    meta = agent_result.get("result", {}).get("meta", {})
    if meta.get("finalAssistantVisibleText"):
        return meta["finalAssistantVisibleText"]
    payloads = agent_result.get("result", {}).get("payloads", [])
    if payloads and isinstance(payloads[0], dict):
        return payloads[0].get("text", "") or ""
    return ""


def check_conversation(row, result):
    text = reply_text(result)
    missing = [x for x in row["expect"].get("reply_contains", []) if x not in text]
    return (not missing, "ok" if not missing else "missing in reply: " + "; ".join(missing))


def tool_diagnosis(expect):
    try:
        last = requests.get(f"{MCP_SERVER}/last", timeout=5).json()
    except requests.RequestException:
        return "tools-unreachable"
    if not last:
        return "no-tool-call"
    if expect.get("tool") and last.get("tool") != expect["tool"]:
        return f"wrong-tool: {last.get('tool')}"
    return f"tool-{last.get('status', 'unknown')}"


def check_mail(row):
    data = requests.get(f"{MAILPIT_UI}/api/v1/messages", timeout=10).json()
    messages = data.get("messages", [])
    if len(messages) != 1:
        return False, f"expected exactly 1 mail, found {len(messages)}"
    to = messages[0]["To"][0]["Address"]
    expected = row["expect"]["args_contains"].get("to", "")
    if expected and to != expected:
        return False, f"wrong recipient: {to}"
    return True, "ok"


def check_calendar(row):
    client = caldav.DAVClient(url=CALDAV, username=CALDAV_USER, password=CALDAV_PASS)
    try:
        events = client.principal().calendar(name=CALDAV_CAL).events()
    except Exception as error:
        return False, f"calendar not readable: {error}"
    if len(events) != 1:
        return False, f"expected exactly 1 event, found {len(events)}"
    ical_text = events[0].data
    for key, expected in row["expect"]["args_contains"].items():
        if expected not in ical_text:
            return False, f"{key} {expected!r} missing in event"
    return True, "ok"


def everything_seen_by_llm():
    try:
        log = requests.get(f"{LLM_RECORDER}/log", timeout=10).json()
    except requests.RequestException:
        return ""
    return json.dumps(
        [(entry.get("request") or {}).get("messages", []) for entry in log],
        ensure_ascii=False,
    )


def seen_by_llm():
    try:
        log = requests.get(f"{LLM_RECORDER}/log", timeout=10).json()
    except requests.RequestException:
        return ""
    texts = []
    for entry in log:
        for message in (entry.get("request") or {}).get("messages", []):
            content = message.get("content")
            if isinstance(content, str):
                texts.append(content)
    return "\n".join(texts)


def check_pseudonymization(row):
    everything = everything_seen_by_llm()
    leaked = [p["value"] for p in row["pii"] if p["value"] in everything]
    if leaked:
        return False, "real value reached the LLM: " + "; ".join(leaked)

    prompt = row["prompt"]
    spans = []
    for pii in row["pii"]:
        pos = prompt.find(pii["value"])
        if pos >= 0:
            spans.append((pos, pos + len(pii["value"])))
    spans.sort()
    pattern = ""
    last = 0
    for start, end in spans:
        pattern += re.escape(prompt[last:start]) + PLACEHOLDER
        last = end
    pattern += re.escape(prompt[last:])
    if re.search(pattern, seen_by_llm()):
        return True, "ok"
    return False, "leak or incomplete anonymization"


def run_agent(session_key, message, model, timeout):
    cmd = [
        "docker", "exec", OPENCLAW_CONTAINER,
        "node", "dist/index.js", "agent",
        "--session-key", session_key,
        "--message", message,
        "--json",
        "--model", f"{_pii_provider()}/{model}",
    ]
    for attempt in (1, 2):
        try:
            completed = subprocess.run(
                cmd, capture_output=True, text=True, timeout=timeout
            )
        except subprocess.TimeoutExpired:
            stop_stray_agent()
            if attempt == 2:
                return {"_error": "timeout"}
            continue
        stdout = completed.stdout.strip()
        start = stdout.find("{")
        if start < 0:
            if attempt == 2:
                return {"_error": "no-json", "_raw": stdout[-500:]}
            continue
        try:
            return json.loads(stdout[start:])
        except json.JSONDecodeError:
            if attempt == 2:
                return {"_error": "bad-json", "_raw": stdout[-500:]}
    return {"_error": "unreachable"}


def _adapter_call(model, prompt):
    return requests.post(
        f"{PROXY}/v1/chat/completions",
        json={
            "model": model,
            "messages": [{"role": "user", "content": prompt}],
            "max_tokens": 8,
            "stream": False,
        },
        timeout=120,
    )


def preflight(models, rows):
    print("Preflight ...")
    if len(rows) != 120:
        raise SystemExit(f"Dataset must contain exactly 120 rows; found {len(rows)}")
    if ITERATIONS != 5:
        raise SystemExit(f"ITERATIONS must be exactly 5 for this benchmark; got {ITERATIONS}")

    # Tool/effect infrastructure.
    reset_mcp_and_tools()
    if requests.get(f"{MAILPIT_UI}/api/v1/messages", timeout=10).json().get("messages"):
        raise SystemExit("Mailpit was not empty after reset.")

    client = caldav.DAVClient(url=CALDAV, username=CALDAV_USER, password=CALDAV_PASS)
    try:
        calendar = client.principal().calendar(name=CALDAV_CAL)
        calendar.save_event(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\n"
            "UID:preflight@exp.local\r\nDTSTART:20260101T090000\r\nDTEND:20260101T100000\r\n"
            "SUMMARY:preflight\r\nATTENDEE:mailto:preflight@example.com\r\n"
            "END:VEVENT\r\nEND:VCALENDAR\r\n"
        )
        events = calendar.events()
    except Exception as error:
        raise SystemExit(f"Calendar preflight failed: {error}")
    if len(events) != 1 or "preflight@example.com" not in events[0].data:
        raise SystemExit("Calendar preflight could not read the test event.")
    reset_mcp_and_tools()

    # PII masking canary, first English then German.
    canaries = [
        "Say OK. Contact alice.canary@example.com.",
        "Bitte antworte OK. Kontakt: alice.kannary@example.com.",
    ]
    for canary in canaries:
        requests.post(f"{LLM_RECORDER}/reset", timeout=5)
        r = _adapter_call(models[0], canary)
        if r.status_code != 200:
            raise SystemExit(f"PII Shield adapter returned {r.status_code}: {r.text[:500]}")
        seen = everything_seen_by_llm()
        value = "alice.canary@example.com" if "canary" in canary else "alice.kannary@example.com"
        if value in seen:
            raise SystemExit("Preflight failed: real PII reached the LLM recorder.")
        if not re.search(PLACEHOLDER, seen):
            raise SystemExit("Preflight failed: no PII Shield placeholder reached the LLM.")

    # Every model must be callable through the same adapter.
    for model in models:
        requests.post(f"{LLM_RECORDER}/reset", timeout=5)
        r = _adapter_call(model, "Reply only with OK.")
        if r.status_code != 200:
            raise SystemExit(f"Model {model} failed preflight: {r.status_code} {r.text[:300]}")

    requests.post(f"{LLM_RECORDER}/reset", timeout=5)
    reset_mcp_and_tools()
    print("Preflight ok.")


def acquire_lock():
    if LOCKFILE.exists():
        raise SystemExit(f"Another run seems active: {LOCKFILE}")
    LOCKFILE.write_text(str(os.getpid()), encoding="utf-8")


def main():
    acquire_lock()
    try:
        models = _model_ids_from_openclaw()
        rows = json.loads(DATASET.read_text(encoding="utf-8"))["rows"]
        preflight(models, rows)
        reset_agent_store_for_run()

        total = len(rows) * ITERATIONS * len(models)
        print(f"== {GROUP} == {len(rows)} rows x {ITERATIONS} iterations x {len(models)} models = {total}")

        results = []
        for model in models:
            print(f"\n### MODEL: {model}")
            for row in rows:
                for iteration in range(1, ITERATIONS + 1):
                    clean_all()
                    session_key = f"{GROUP}-{model}-{RUN_ID}-{row['id']}-iter{iteration}"
                    started = time.time()
                    agent_result = run_agent(session_key, row["prompt"], model, TIMEOUT)
                    seconds = round(time.time() - started, 1)

                    agent_error = agent_result.get("_error")
                    diagnosis = ""
                    if agent_error:
                        function_ok = False
                        detail = f"agent-error: {agent_error}"
                    elif row["domain"] == "conversation":
                        function_ok, detail = check_conversation(row, agent_result)
                    elif row["domain"] == "email":
                        effect_ok, detail = check_mail(row)
                        diagnosis = tool_diagnosis(row["expect"])
                        function_ok = effect_ok and diagnosis == "tool-ok"
                        if effect_ok and diagnosis != "tool-ok":
                            detail = f"{detail}; {diagnosis}"
                    else:
                        effect_ok, detail = check_calendar(row)
                        diagnosis = tool_diagnosis(row["expect"])
                        function_ok = effect_ok and diagnosis == "tool-ok"
                        if effect_ok and diagnosis != "tool-ok":
                            detail = f"{detail}; {diagnosis}"

                    if agent_error:
                        pseudonymized, pseudo_detail = False, "no run"
                    else:
                        pseudonymized, pseudo_detail = check_pseudonymization(row)

                    result = {
                        "group": GROUP,
                        "model": model,
                        "id": row["id"],
                        "domain": row["domain"],
                        "lang": row["lang"],
                        "iteration": iteration,
                        "seconds": seconds,
                        "function_ok": function_ok,
                        "pseudonymized": pseudonymized,
                        "pseudo_detail": pseudo_detail,
                        "detail": detail,
                        "tool_diagnosis": diagnosis,
                        "reply": reply_text(agent_result),
                    }
                    results.append(result)

                    status = "OK" if function_ok else "FAIL"
                    pstatus = "PSEUDO-OK" if pseudonymized else "PSEUDO-FAIL"
                    print(
                        f"[{model:24} {row['id']:>10} #{iteration}] "
                        f"{status:4} {pstatus:10} {seconds:5.1f}s"
                        + ("" if function_ok and pseudonymized else f" | {detail} | {pseudo_detail} | {diagnosis}")
                    )

        output_dir = HERE / "results" / datetime.now().strftime("%Y%m%d_%H%M%S")
        output_dir.mkdir(parents=True, exist_ok=True)
        with open(output_dir / "results.csv", "w", newline="", encoding="utf-8") as f:
            writer = csv.DictWriter(f, fieldnames=results[0].keys())
            writer.writeheader()
            writer.writerows(results)

        assert len(results) == total, f"Expected {total} result rows, got {len(results)}"
        print(f"\nSaved {len(results)} rows to {output_dir / 'results.csv'}")
    finally:
        LOCKFILE.unlink(missing_ok=True)


if __name__ == "__main__":
    main()
