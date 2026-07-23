import csv
import json
import os
import re
import shutil
import subprocess
import time

import caldav
import requests

from datetime import datetime
from pathlib import Path
from dotenv import load_dotenv

HERE = Path(__file__).resolve().parent

# Read the same infra/.env the docker stack uses, so ports/names stay in sync.
load_dotenv(HERE / "infrastructure" / ".env")

# -------------------------
#  Endpoints
# -------------------------

def _port(name: str, default: str) -> str:
    return os.environ.get(name, default)
MCP_SERVER    = f"http://localhost:{_port('TOOLS_PORT', '3100')}"      # tool-call recording
MAILPIT_UI  = f"http://localhost:{_port('MAILPIT_UI_PORT', '8025')}"  # Mailpit REST API
CALDAV = f"http://localhost:{_port('CALDAV_PORT', '5232')}/"
LLM_RECORDER  = f"http://localhost:{_port('RECORDER_PORT', '8000')}"  # what the LLM actually saw
PROXY = f"http://127.0.0.1:{_port('PROXY_PORT', '8080')}"             # the PrivacyProxy itself
CALDAV_USER, CALDAV_PASS = "test", "test"
CALDAV_CAL = os.environ.get("CALDAV_CAL", "exp_rq2_experimentalgroup")

# -------------------------
# OpenClaw
# -------------------------

# docker container
OPENCLAW_CONTAINER = os.environ.get("OPENCLAW_CONTAINER", "openclaw-openclaw-gateway-1")
# The agent-tools container (it has the caldav library for the Radicale read-back).

# The agent stores context in workspace/MEMORY.md (root) AND workspace/memory/*.
# BOTH must be cleared between iterations, otherwise PII values from earlier rows
# leak into the system prompts of later rows.
WORKSPACE = Path.home() / ".openclaw" / "workspace"
WORKSPACE_MEMORY_DIR = WORKSPACE / "memory"
WORKSPACE_MEMORY_FILE = WORKSPACE / "MEMORY.md"
# Past conversations live in TWO places: as files in sessions/ (incl. soft-deleted
# .zst archives) and as rows in the agent's sqlite store - including a full-text
# search index (session_transcript_fts) that the agent's memory/search tools use.
# Both are wiped per data point, otherwise the agent can "remember" earlier runs.
SESSIONS_DIR = Path.home() / ".openclaw" / "agents" / "main" / "sessions"
AGENT_DB = Path.home() / ".openclaw" / "agents" / "main" / "agent" / "openclaw-agent.sqlite"

# -------------------------
# Experiment vars
# -------------------------

DEFAULT_ITERATIONS = int(os.environ.get("ITERATIONS", "1"))
DEFAULT_TIMEOUT = int(os.environ.get("AGENT_TIMEOUT", "180"))
DEFAULT_GROUP = os.environ.get("GROUP", "exp-rq2-experimentalgroup")
MODEL = os.environ.get("MODEL", "llm-direct/kimi-k2.6:cloud")

# Only ONE orchestrator may run at a time: all runs share the global state of the
# recorder / tool-log / presidio-mock. Parallel runs would reset each other's state
# and produce wrong LEAK / no-tool-call errors. Enforced with a lock file.
LOCKFILE = Path("/tmp/openclaw_experiment_run.lock")

# -------------------------
# Cleaning
# -------------------------

def reset_mcp_and_tools() -> None:
    """Clean starting state before each iteration."""
    # clean mcp tool log
    try:
        url = f"{MCP_SERVER}/reset"
        requests.post(url, timeout=5)
    except requests.RequestException:
        pass

def clear_workspace_memory() -> None:
    """Remove the agent's memory between iterations (isolation): empty the memory/
    subfolder AND reset MEMORY.md to a stub."""
    if WORKSPACE_MEMORY_DIR.exists():
        for path in WORKSPACE_MEMORY_DIR.iterdir():
            if path.is_file():
                path.unlink()
            elif path.is_dir():
                shutil.rmtree(path, ignore_errors=True)
    if WORKSPACE_MEMORY_FILE.exists():
        WORKSPACE_MEMORY_FILE.write_text("# MEMORY.md\n")

def clear_openclaw_sessions() -> None:
    """Delete the session FILES on disk (transcripts, incl. archived .zst).

    The sqlite store is deliberately NOT touched here: sqlite does not survive two
    writers across the Docker file-sharing boundary (host python vs. the gateway in
    the container) - that corrupted the database once. The sqlite store is instead
    wiped ONCE per run, with the gateway stopped (see reset_agent_store_for_run).
    """
    if SESSIONS_DIR.exists():
        for path in SESSIONS_DIR.iterdir():
            if path.is_file():
                path.unlink()
            elif path.is_dir():
                shutil.rmtree(path, ignore_errors=True)

def wait_until_gateway_healthy() -> None:
    """Wait until the gateway container reports 'healthy' again."""
    for _ in range(60):
        inspect = subprocess.run(
            ["docker", "inspect", "-f", "{{.State.Health.Status}}", OPENCLAW_CONTAINER],
            capture_output=True, text=True)
        if inspect.stdout.strip() == "healthy":
            return
        time.sleep(2)
    raise SystemExit("Gateway did not become healthy.")

def stop_stray_agent() -> None:
    """Stop an agent that is still running after a timeout.

    A timeout only kills the local "docker exec" - the agent INSIDE the gateway
    keeps working and keeps sending requests to the proxy. Those late requests
    land in the NEXT row's recorder log and are measured as if they belonged to
    it, which corrupts the pseudonymization result of the following rows.
    Restarting the gateway is the simple and reliable way to make sure nothing is
    left running.
    """
    print("      (timeout: restarting gateway to stop the stray agent)")
    subprocess.run(["docker", "restart", OPENCLAW_CONTAINER], capture_output=True, text=True)
    wait_until_gateway_healthy()

def reset_agent_store_for_run() -> None:
    """Once at run start: stop the gateway, delete the agent's sqlite store
    (sessions + transcripts + full-text search index), start the gateway again.
    Stopping first is essential - deleting while the gateway writes corrupts the db.
    The gateway recreates an empty store on startup (auth/config live in
    openclaw.json, so nothing is lost)."""
    print("Resetting OpenClaw agent store (gateway restart) ...")
    subprocess.run(["docker", "stop", OPENCLAW_CONTAINER], capture_output=True, text=True, check=True)
    for db_file in AGENT_DB.parent.glob(AGENT_DB.name + "*"):   # .sqlite, -wal, -shm, quarantined
        db_file.unlink()
    subprocess.run(["docker", "start", OPENCLAW_CONTAINER], capture_output=True, text=True, check=True)
    wait_until_gateway_healthy()
    print("Gateway healthy, store is fresh.")

def reset_recorder() -> None:
    """Empty the LLM recorder, so /log only holds THIS data point's exchanges."""
    try:
        requests.post(f"{LLM_RECORDER}/reset", timeout=5)
    except requests.RequestException:
        pass

def clean_all():
    reset_mcp_and_tools()
    clear_workspace_memory()
    clear_openclaw_sessions()
    reset_recorder()

def reply_text(agent_result: dict) -> str:
    """The agent's final visible answer text."""
    meta = agent_result.get("result", {}).get("meta", {})

    if meta.get("finalAssistantVisibleText"):
        return meta["finalAssistantVisibleText"]

    payloads = agent_result.get("result", {}).get("payloads", [])

    if payloads and isinstance(payloads[0], dict):
        return payloads[0].get("text", "") or ""

    return ""

# -------------------------
#  Checking
# -------------------------

def check_reply_message(agent_result: dict, expect: dict) -> tuple[bool, str]:
    """Conversation: the expected values appear in the reply."""
    text = reply_text(agent_result)
    missing = [needle for needle in expect.get("reply_contains", []) if needle not in text]

    if missing:
        return False, "missing in reply: " + "; ".join(missing)

    return True, "ok"

def check_conversation(row, reply) -> tuple[bool, str]:
    return check_reply_message(reply, row["expect"])


# -------------------------
#  Pseudonymization (did the LLM see placeholders instead of the real PII?)
# -------------------------

# What a proxy placeholder looks like, e.g. [EMAIL_ADDRESS_a1b2c3d4e5f6a7b8]
PLACEHOLDER = r"\[[A-Z0-9_]+_[0-9a-f]{16}\]"

def seen_by_llm() -> str:
    """All message text the LLM received during this data point (across all rounds)."""
    try:
        log = requests.get(f"{LLM_RECORDER}/log", timeout=5).json()
    except requests.RequestException:
        return ""
    texts = []
    for entry in log:
        for message in (entry.get("request") or {}).get("messages", []):
            content = message.get("content")
            if isinstance(content, str):
                texts.append(content)
    return "\n".join(texts)

def everything_seen_by_llm() -> str:
    """All recorded MESSAGES as raw text - including tool_call arguments, which do
    not sit in "content". Used for the leak check.

    Only the messages, deliberately NOT the whole request: the "tools" section
    holds the tool SCHEMA, and its examples contain things like
    "2026-07-15T14:00:00". A row whose PII is "14:00" would otherwise be reported
    as a leak although that text never came from the user.
    """
    try:
        log = requests.get(f"{LLM_RECORDER}/log", timeout=5).json()
    except requests.RequestException:
        return ""
    return json.dumps([(entry.get("request") or {}).get("messages", []) for entry in log],
                      ensure_ascii=False)

def check_pseudonymization(row) -> tuple[bool, str]:
    """Did the LLM see a placeholder INSTEAD of the real PII - fully, not partially?

    Two things have to hold, and we check BOTH:

    1. No real PII value may appear ANYWHERE in what the LLM received - not in a
       later round either, and not inside tool_call arguments.
    2. The prompt must have the expected SHAPE: the prompt text with a placeholder
       exactly where a PII value was. This catches partial masking such as
       "[PERSON_..] Itzel" or "dennis.[PERSON_..]@...".

    Checking only 2. would not be enough: it asks whether the masked form is
    PRESENT, not whether the real value is ABSENT - so a leak in a later round
    would go unnoticed.
    """
    # 1) the real values must not show up anywhere
    everything = everything_seen_by_llm()
    leaked = [pii["value"] for pii in row["pii"] if pii["value"] in everything]
    if leaked:
        return False, "real value reached the LLM: " + "; ".join(leaked)

    # 2) the prompt must have the expected shape
    prompt = row["prompt"]

    # find where each PII value sits in the prompt (left to right)
    spans = []
    for pii in row["pii"]:
        pos = prompt.find(pii["value"])
        if pos != -1:
            spans.append((pos, pos + len(pii["value"])))
    spans.sort()

    # expected shape = normal text (literal) + a placeholder where PII was
    pattern = ""
    last = 0
    for start, end in spans:
        pattern += re.escape(prompt[last:start]) + PLACEHOLDER
        last = end
    pattern += re.escape(prompt[last:])

    if re.search(pattern, seen_by_llm()):
        return True, "ok"
    return False, "leak or partial leak"

def check_mail(row) -> tuple[bool, str]:
    data = requests.get(f"{MAILPIT_UI}/api/v1/messages", timeout=5).json()
    messages = data.get("messages", [])

    if len(messages) != 1:
        return False, f"expected exactly 1 mail, found {len(messages)}"

    to = messages[0]["To"][0]["Address"]
    expected = row["expect"]["args_contains"].get("to", "")

    if expected and to != expected:
        return False, f"wrong recipient: {to}"

    return True, "ok"

def check_calendar(row) -> tuple[bool, str]:
    client = caldav.DAVClient(url=CALDAV, username=CALDAV_USER, password=CALDAV_PASS)

    try:
        events = client.principal().calendar(name=CALDAV_CAL).events()
    except Exception as error:
        return False, f"calendar not readable: {error}"

    if len(events) != 1:
        return False, f"expected exactly 1 event, found {len(events)}"

    ical_text = events[0].data   # the raw iCalendar text of the event
    for key, expected in row["expect"]["args_contains"].items():
        if expected not in ical_text:
            return False, f"{key} {expected!r} missing in event"

    return True, "ok"


def tool_diagnosis(expect: dict) -> str:
    """What happened at the tool level? Pure diagnosis column for the CSV -
    it does NOT decide success (the effect checks above do), but it tells us
    WHY something failed: never called, wrong tool, called-but-errored, or ok.
    """
    try:
        last = requests.get(f"{MCP_SERVER}/last", timeout=5).json()
    except requests.RequestException:
        return "tools-unreachable"

    if not last:
        return "no-tool-call"
    if expect["tool"] and last.get("tool") != expect["tool"]:
        return f"wrong-tool: {last.get('tool')}"

    # status comes from the MCP server's recording: ok / error / attempted
    return f"tool-{last.get('status', 'unknown')}"


# -------------------------
# Experiment
# -------------------------

def run_agent(session_key: str, message: str, model: str | None, timeout: int) -> dict:
    """Run one agent turn. Returns the parsed --json result (or {} on error)."""
    cmd = [
        "docker", "exec", OPENCLAW_CONTAINER,
        "node", "dist/index.js", "agent",
        "--session-key", session_key,
        "--message", message,
        "--json",
    ]
    if model:
        cmd += ["--model", model]

    # One retry: cloud models occasionally stall under load.
    for attempt in (1, 2):
        try:
            completed = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
        except subprocess.TimeoutExpired:
            stop_stray_agent()   # otherwise it keeps sending requests to the proxy
            if attempt == 2:
                return {"_error": "timeout"}
            continue

        stdout = completed.stdout.strip()
        json_start = stdout.find("{")  # the JSON result is the first {...} object in stdout
        if json_start == -1:
            if attempt == 2:
                return {"_error": "no-json", "_raw": stdout[-500:]}
            continue

        try:
            return json.loads(stdout[json_start:])
        except json.JSONDecodeError:
            if attempt == 2:
                return {"_error": "bad-json", "_raw": stdout[-500:]}

    return {"_error": "unreachable"}

def acquire_lock() -> None:
    if LOCKFILE.exists():
        raise SystemExit(f"Another run seems to be active ({LOCKFILE} exists). "
                         f"If that is wrong, delete the file and start again.")
    LOCKFILE.write_text(str(os.getpid()))

# Unique per START of the experiment: without this, a re-run would reuse the
# session keys of a previous (e.g. aborted) run and OpenClaw would CONTINUE
# those old conversations - the agent then "remembers" earlier events.
RUN_ID = datetime.now().strftime("%Y%m%d%H%M%S")

def preflight() -> None:
    """Check the measuring apparatus BEFORE the run starts.

    Each check stands for a failure that already happened here and stayed
    invisible for hours, because it never raised an error - it only showed up in
    the results. Twenty seconds here save a run of several hours.
    """
    print("Preflight ...")

    # 1) The experimental group MUST go through the proxy. With "llm-direct/..."
    #    the run silently measures the control group instead.
    if not MODEL.startswith("privacyproxy/"):
        raise SystemExit(f"Preflight: MODEL is '{MODEL}' - the experimental group "
                         f"needs a 'privacyproxy/...' model.")

    # MODEL and LLM_MODEL are two separate lines in .env and must always agree.
    # Switching only one of them silently runs a different model behind the proxy.
    behind_proxy = os.environ.get("LLM_MODEL", "")
    if MODEL.split("/", 1)[1] != behind_proxy:
        raise SystemExit(f"Preflight: MODEL '{MODEL}' and LLM_MODEL '{behind_proxy}' "
                         f"do not match - the proxy would order a different model.")

    # 2) Tools, Mailpit: reachable, and /reset really empties the mailbox.
    requests.post(f"{MCP_SERVER}/reset", timeout=10)
    mails = requests.get(f"{MAILPIT_UI}/api/v1/messages", timeout=10).json().get("messages", [])
    if mails:
        raise SystemExit(f"Preflight: Mailpit still holds {len(mails)} mail(s) after reset.")

    # 3) Calendar: write an event WITH an attendee and read it back. This catches
    #    both a broken Radicale mount and a tool that silently drops fields.
    client = caldav.DAVClient(url=CALDAV, username=CALDAV_USER, password=CALDAV_PASS)
    try:
        calendar = client.principal().calendar(name=CALDAV_CAL)
        calendar.save_event(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\n"
            "UID:preflight@exp.local\r\nDTSTART:20260101T090000\r\nDTEND:20260101T100000\r\n"
            "SUMMARY:preflight\r\nATTENDEE:mailto:preflight@example.com\r\n"
            "END:VEVENT\r\nEND:VCALENDAR\r\n")
        events = calendar.events()
    except Exception as error:
        raise SystemExit(f"Preflight: calendar not usable: {error}")
    if len(events) != 1 or "preflight@example.com" not in events[0].data:
        raise SystemExit("Preflight: the calendar did not return the event with its attendee.")
    requests.post(f"{MCP_SERVER}/reset", timeout=10)   # remove the test event again

    # 4) The full chain: Presidio detects -> proxy masks -> recorder sees a
    #    placeholder and NOT the real value. An e-mail address is used on purpose:
    #    that is the one entity the real Presidio recognises reliably, so a failure
    #    here means the chain is broken, not that detection is imperfect.
    canary = "preflight.canary@example.com"
    reset_recorder()
    try:
        answer = requests.post(f"{PROXY}/v1/chat/completions", timeout=120, json={
            "model": "preflight", "max_tokens": 10,
            "messages": [{"role": "user", "content": f"Say OK. Contact: {canary}"}]})
    except requests.RequestException as error:
        raise SystemExit(f"Preflight: proxy not reachable: {error}")
    if answer.status_code != 200:
        raise SystemExit(f"Preflight: proxy answered {answer.status_code}: {answer.text[:300]}")

    seen = everything_seen_by_llm()
    if not seen:
        raise SystemExit("Preflight: the recorder logged nothing - is Llm__BaseUrl pointing at it?")
    if canary in seen:
        raise SystemExit("Preflight: the real value reached the LLM - masking is NOT working.")
    if not re.search(PLACEHOLDER, seen):
        raise SystemExit("Preflight: no placeholder in what the LLM saw - masking is NOT working.")

    reset_recorder()
    print("Preflight ok: tools, mail, calendar, presidio, proxy, recorder and model all work.")

def main() -> None:
    acquire_lock()
    try:
        preflight()
        reset_agent_store_for_run()
        rows = json.loads((HERE / "dataset.json").read_text(encoding="utf-8"))["rows"]
        print(f"== {DEFAULT_GROUP} == {len(rows)} rows x {DEFAULT_ITERATIONS} iteration(s) | model={MODEL}")

        results = []
        for row in rows:
            for iteration in range(1, DEFAULT_ITERATIONS + 1):

                # Every data point starts from a clean world: no mails, no events,
                # no recorded tool calls, no agent memory - and a fresh session.
                clean_all()
                # No set_pii here: the REAL Presidio detects PII on its own (that is
                # what RQ3 measures). check_pseudonymization still uses the dataset's
                # ground truth to judge whether detection + masking was complete.
                session_key = f"{DEFAULT_GROUP}-{RUN_ID}-{row['id']}-iter{iteration}"

                started = time.time()
                agent_result = run_agent(session_key, row["prompt"], MODEL, DEFAULT_TIMEOUT)
                seconds = round(time.time() - started, 1)

                agent_error = agent_result.get("_error", "")
                if agent_error:
                    ok, detail = False, f"agent-error: {agent_error}"
                    diagnosis = ""
                elif row["domain"] == "conversation":
                    ok, detail = check_conversation(row, agent_result)
                    diagnosis = ""                     # conversation uses no tool
                elif row["domain"] == "email":
                    ok, detail = check_mail(row)
                    diagnosis = tool_diagnosis(row["expect"])
                else:  # calendar
                    ok, detail = check_calendar(row)
                    diagnosis = tool_diagnosis(row["expect"])

                # Pseudonymization: did the LLM see placeholders instead of the PII?
                # (function_ok above already proves DE-pseudonymization: the real
                # value came back into the tool / reply.) On an agent error we cannot
                # judge it, so we leave it False with a clear note.
                if agent_error:
                    pseudonymized, pseudo_detail = False, "no run"
                else:
                    pseudonymized, pseudo_detail = check_pseudonymization(row)

                results.append({
                    "group": DEFAULT_GROUP,
                    "model": MODEL,
                    "id": row["id"],
                    "domain": row["domain"],
                    "lang": row["lang"],
                    "iteration": iteration,
                    "seconds": seconds,
                    "function_ok": ok,
                    "pseudonymized": pseudonymized,
                    "pseudo_detail": pseudo_detail,
                    "detail": detail,
                    "tool_diagnosis": diagnosis,
                    "reply": reply_text(agent_result),
                })
                status = "OK  " if ok else "FAIL"
                pstat = "PSEUDO-OK  " if pseudonymized else "PSEUDO-FAIL"
                print(f"[{row['id']:>10} #{iteration}] {status} {pstat} ({seconds}s)"
                      + ("" if ok and pseudonymized else f"  {detail} / {pseudo_detail}  [{diagnosis}]"))

        # ---- summary ----
        print("\n== Summary ==")
        print("  -- function (task worked / de-pseudonymized) --")
        for domain in ("conversation", "email", "calendar"):
            subset = [r for r in results if r["domain"] == domain]
            good = sum(1 for r in subset if r["function_ok"])
            print(f"  {domain:12}: {good}/{len(subset)}")
        pseudo_ok = sum(1 for r in results if r["pseudonymized"])
        print(f"  -- pseudonymization (no leak): {pseudo_ok}/{len(results)} --")

        # ---- CSV ----
        output_dir = HERE / "results" / datetime.now().strftime("%Y%m%d_%H%M%S")
        output_dir.mkdir(parents=True)
        with open(output_dir / "results.csv", "w", newline="", encoding="utf-8") as f:
            writer = csv.DictWriter(f, fieldnames=results[0].keys())
            writer.writeheader()
            writer.writerows(results)
        print(f"\nSaved per-row results to {output_dir / 'results.csv'}")

    finally:
        # Always release the lock, even if the run crashed halfway.
        LOCKFILE.unlink(missing_ok=True)

if __name__ == "__main__":
    main()