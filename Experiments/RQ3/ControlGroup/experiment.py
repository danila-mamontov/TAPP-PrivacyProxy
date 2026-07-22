import csv
import json
import os
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
CALDAV_USER, CALDAV_PASS = "test", "test"
CALDAV_CAL = os.environ.get("CALDAV_CAL", "exp_rq3_controlgroup")

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

# -------------------------
# Experiment vars
# -------------------------

DEFAULT_ITERATIONS = int(os.environ.get("ITERATIONS", "1"))
DEFAULT_TIMEOUT = int(os.environ.get("AGENT_TIMEOUT", "180"))
DEFAULT_GROUP = os.environ.get("GROUP", "exp-rq3-controlgroup")
MODEL = os.environ.get("MODEL", "llm-direct/kimi-k2.6:cloud")

# Only ONE orchestrator may run at a time: all runs share the global state of the
# recorder / tool-log / presidio-mock. Parallel runs would reset each other's state
# and produce wrong LEAK / no-tool-call errors. Enforced with a lock file.
LOCKFILE = Path("/tmp/exp_rq3_controlgroup_run.lock")

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

def clean_all():
    reset_mcp_and_tools()
    clear_workspace_memory()

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

def main() -> None:
    acquire_lock()
    try:
        rows = json.loads((HERE / "dataset.json").read_text(encoding="utf-8"))["rows"]
        print(f"== {DEFAULT_GROUP} == {len(rows)} rows x {DEFAULT_ITERATIONS} iteration(s) | model={MODEL}")

        results = []
        for row in rows:
            for iteration in range(1, DEFAULT_ITERATIONS + 1):

                # Every data point starts from a clean world: no mails, no events,
                # no recorded tool calls, no agent memory - and a fresh session.
                clean_all()
                session_key = f"{DEFAULT_GROUP}-{row['id']}-iter{iteration}"

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

                results.append({
                    "group": DEFAULT_GROUP,
                    "id": row["id"],
                    "domain": row["domain"],
                    "lang": row["lang"],
                    "iteration": iteration,
                    "seconds": seconds,
                    "function_ok": ok,
                    "detail": detail,
                    "tool_diagnosis": diagnosis,
                    "reply": reply_text(agent_result),
                })
                status = "OK  " if ok else "FAIL"
                print(f"[{row['id']:>10} #{iteration}] {status} ({seconds}s)"
                      + ("" if ok else f"  {detail}  [{diagnosis}]"))

        # ---- summary ----
        print("\n== Summary ==")
        for domain in ("conversation", "email", "calendar"):
            subset = [r for r in results if r["domain"] == domain]
            good = sum(1 for r in subset if r["function_ok"])
            print(f"  {domain:12}: {good}/{len(subset)}")

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