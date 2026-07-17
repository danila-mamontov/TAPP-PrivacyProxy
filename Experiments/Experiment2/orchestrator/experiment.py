"""
Experiment 2 - orchestrator.

Question: does the real OpenClaw agent, talking through PrivacyProxy, behave
correctly across three domains (plain conversation, email, calendar) - and can we
measure it fully automatically?

Unlike Experiment 1 (fixed text, proxy only) a real multi-round agent (OpenClaw)
runs here with real tools (mail -> Mailpit, calendar -> Radicale). Presidio is
mocked (value matching), so we have 100% control over what counts as PII. For every
dataset row we measure three levels plus function:

  Level 1   Pseudonymized?     No real PII value ever reached the LLM.
                               Source: llm-recorder /log (all proxy->LLM requests,
                               including tool_calls arguments from the history).
  Level 2/3 De-pseudonymized?  The tool arguments arrived with the REAL values
                               (placeholders were restored).
                               Source: agent-tools /last (tool-call recording).
                               For conversation: the real value is in the reply.
  Function  Did it work?       The mail is in Mailpit / the event is in Radicale
                               (read out-of-band, correlated via the run's UUID).

Per row x iteration: reset everything -> set the PII -> fresh, isolated session ->
one agent turn -> four checks. No manual checking.

Requirements: infra/docker-compose.yml (mailpit, radicale, presidio-mock,
llm-recorder, agent-tools, privacyproxy) is running, the OpenClaw gateway is running
and onboarded, and the exp2tools MCP server is registered. Configuration comes from
infra/.env (run.sh exports it before starting this script).
"""
import argparse
import csv
import json
import os
import re
import shutil
import subprocess
import sys
import time
import uuid
from datetime import datetime
from pathlib import Path

import requests
from dotenv import load_dotenv

HERE = Path(__file__).resolve().parent

# Read the same infra/.env the docker stack uses, so ports/names stay in sync.
# (run.sh also exports these; loading here lets you run experiment.py directly.)
load_dotenv(HERE.parent / "infra" / ".env")


# --------------------------------------------------------------------------- #
#  Configuration (from environment / .env, with sensible defaults)
# --------------------------------------------------------------------------- #
def _port(name: str, default: str) -> str:
    return os.environ.get(name, default)


# Endpoints (host view; every service publishes on localhost).
RECORDER = f"http://localhost:{_port('RECORDER_PORT', '8000')}"   # what the LLM saw
TOOLS    = f"http://localhost:{_port('TOOLS_PORT', '3100')}"      # tool-call recording
PRESIDIO = f"http://localhost:{_port('PRESIDIO_PORT', '5002')}"   # set PII ground truth
MAILPIT  = f"http://localhost:{_port('MAILPIT_UI_PORT', '8025')}"  # Mailpit REST API
CALDAV_URL = f"http://localhost:{_port('CALDAV_PORT', '5232')}/"
CALDAV_USER, CALDAV_PASS = "test", "test"
CALDAV_CAL = os.environ.get("CALDAV_CAL", "exp2")

# OpenClaw: one agent turn through the running gateway (fast, via docker exec).
OPENCLAW_CONTAINER = os.environ.get("OPENCLAW_CONTAINER", "openclaw-openclaw-gateway-1")
# The agent-tools container (it has the caldav library for the Radicale read-back).
TOOLS_CONTAINER = os.environ.get("AGENT_TOOLS_CONTAINER", "exp2-agent-tools")

# Defaults for the command-line options (still overridable on the CLI).
DEFAULT_ITERATIONS = int(os.environ.get("ITERATIONS", "1"))
DEFAULT_TIMEOUT = int(os.environ.get("AGENT_TIMEOUT", "180"))
DEFAULT_GROUP = os.environ.get("GROUP", "exp2-mock")

# The agent stores context in workspace/MEMORY.md (root) AND workspace/memory/*.
# BOTH must be cleared between iterations, otherwise PII values from earlier rows
# leak into the system prompts of later rows.
WORKSPACE = Path.home() / ".openclaw" / "workspace"
WORKSPACE_MEMORY_DIR = WORKSPACE / "memory"
WORKSPACE_MEMORY_FILE = WORKSPACE / "MEMORY.md"

# Only ONE orchestrator may run at a time: all runs share the global state of the
# recorder / tool-log / presidio-mock. Parallel runs would reset each other's state
# and produce wrong LEAK / no-tool-call errors. Enforced with a lock file.
LOCKFILE = Path("/tmp/exp2_orchestrator.lock")


# --------------------------------------------------------------------------- #
#  Controlling the test services
# --------------------------------------------------------------------------- #
def reset_all() -> None:
    """Clean starting state before each iteration."""
    for url in (f"{RECORDER}/reset", f"{TOOLS}/reset", f"{PRESIDIO}/reset"):
        try:
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


def set_pii(pii: list[dict]) -> None:
    """Tell the mock which exact PII values to detect for this row (ground truth)."""
    requests.post(f"{PRESIDIO}/set-pii", json=pii, timeout=5)


# --------------------------------------------------------------------------- #
#  Running one OpenClaw agent turn
# --------------------------------------------------------------------------- #
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
        json_start = stdout.find("{")  # the JSON result is the last {...} object in stdout
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


def reply_text(agent_result: dict) -> str:
    """The agent's final visible answer text."""
    meta = agent_result.get("result", {}).get("meta", {})
    if meta.get("finalAssistantVisibleText"):
        return meta["finalAssistantVisibleText"]
    payloads = agent_result.get("result", {}).get("payloads", [])
    if payloads and isinstance(payloads[0], dict):
        return payloads[0].get("text", "") or ""
    return ""


# --------------------------------------------------------------------------- #
#  Level 1 - did any real PII reach the LLM?
# --------------------------------------------------------------------------- #
# Generic tokens that are NOT a PII leak as a fragment (TLDs, protocols, fake
# domains, generic company suffixes).
FRAGMENT_STOPWORDS = {"com", "org", "net", "www", "http", "https", "mailto", "example", "gmbh"}

# Any [TYPE_hexhex] placeholder produced by the proxy.
PLACEHOLDER_RE = re.compile(r"\[[A-Z_]+_[0-9a-f]+\]")

# A fragment only counts as a PARTIAL leak when it sits DIRECTLY next to a
# placeholder (at most 3 separator characters in between) - the signature of a
# split-up anonymization, e.g. "picsysteme.[PERSON_x]". A stray "info"/"report"
# somewhere in the text is NOT a partial leak.
_PLACEHOLDER_PATTERN = r"\[[A-Z_]+_[0-9a-f]+\]"
_SEPARATOR = r"[\s.@/:_+\-]{0,3}"


def pii_fragments(value: str) -> set[str]:
    """Meaningful sub-parts of a PII value (>=4 chars, no generic tokens).
    "dennis.itzel@picsysteme.de" -> {dennis, itzel, picsysteme}."""
    fragments = set()
    for part in re.split(r"[^0-9A-Za-z]+", value):
        if len(part) >= 4 and part.lower() not in FRAGMENT_STOPWORDS:
            fragments.add(part)
    return fragments


def collect_non_system_text(log: list[dict]) -> str:
    """Join all NON-system messages the LLM received (that is where our PII flows:
    user/assistant/tool, including tool_calls). OpenClaw's static system prompt
    contains many common words (info, support, report, ...) and would otherwise
    cause false alarms."""
    parts = []
    for entry in log:
        for message in entry.get("request", {}).get("messages", []):
            if message.get("role") == "system":
                continue
            for key, value in message.items():
                if key == "role":
                    continue
                parts.append(value if isinstance(value, str)
                             else json.dumps(value, ensure_ascii=False))
    return "\n".join(parts)


def check_level1(pii: list[dict]) -> tuple[bool | None, str, str]:
    """Check pseudonymization on TWO levels (only in non-system messages):
      - FULL leak:    the complete PII value appears un-anonymized in an LLM request.
      - PARTIAL leak: a PII fragment sits right next to a placeholder
                      (e.g. "dennis.itzel@picsysteme.[PERSON_x]" -> "picsysteme").
    Both count as a failure. Returns (ok, kind in {clean|full|partial|none}, detail).
    """
    try:
        log = requests.get(f"{RECORDER}/log", timeout=5).json()
    except requests.RequestException as error:
        return False, "none", f"recorder-unreachable: {error}"
    if not log:
        return False, "none", "no-llm-requests"

    text = collect_non_system_text(log)
    # Strip placeholder contents so their random hex cannot match by accident.
    text_without_placeholders = PLACEHOLDER_RE.sub(" ", text)

    # Full leaks: the entire value is present un-anonymized.
    full_leaks = [item["value"] for item in pii
                  if item["value"] in text_without_placeholders]
    if full_leaks:
        return False, "full", "FULL: " + "; ".join(full_leaks)

    # Partial leaks: a fragment sits directly next to a placeholder.
    partial_leaks = []
    for item in pii:
        for fragment in pii_fragments(item["value"]):
            escaped = re.escape(fragment)
            next_to_placeholder = (
                re.search(rf"\b{escaped}\b{_SEPARATOR}{_PLACEHOLDER_PATTERN}", text)
                or re.search(rf"{_PLACEHOLDER_PATTERN}{_SEPARATOR}\b{escaped}\b", text)
            )
            if next_to_placeholder and fragment not in partial_leaks:
                partial_leaks.append(fragment)
    if partial_leaks:
        return False, "partial", "PARTIAL (next to placeholder): " + ", ".join(partial_leaks)

    # No leak found. If we never saw a placeholder at all, the agent produced no
    # anonymized traffic in this iteration -> not a privacy event.
    if "[" not in text or "_" not in text:
        return False, "none", "no-placeholder-seen"
    return True, "clean", f"clean ({len(log)} requests)"


# --------------------------------------------------------------------------- #
#  Detection quality measured directly on the prompt
# --------------------------------------------------------------------------- #
def analyze(text: str, language: str) -> list[dict]:
    """One analyzer call with reasoning + threshold 0 (like the proxy). In Exp2 this
    is the mock -> it returns exactly the ground-truth spans (perfect baseline)."""
    try:
        response = requests.post(f"{PRESIDIO}/analyze", timeout=30, json={
            "text": text, "language": language,
            "return_decision_process": True, "score_threshold": 0,
        })
        return response.json() or []
    except (requests.RequestException, ValueError):
        return []


def find_all(text: str, value: str) -> list[tuple[int, int]]:
    """Every (start, end) occurrence of `value` in `text`."""
    spans = []
    start = 0
    while True:
        index = text.find(value, start)
        if index == -1:
            break
        spans.append((index, index + len(value)))
        start = index + len(value)
    return spans


def spans_overlap(a_start: int, a_end: int, b_start: int, b_end: int) -> bool:
    return a_start < b_end and b_start < a_end


def measure_detection(text: str, pii: list[dict]) -> tuple[dict, list[dict], list[dict]]:
    """Detection quality ON THE PROMPT (deterministic, independent of the agent).
    Analyzes in both languages like the proxy (de+en) and compares with the ground
    truth (dataset.pii). Returns:
      - counts:   {expected, full, partial, miss, fp}
      - outcomes: one entry per PII value {value, expected, outcome(full|partial|miss),
                  detected_as}
      - fps:      detected spans that do NOT overlap any ground-truth PII (over-
                  anonymization), each with reasoning (recognizer / pattern / score).
    In Exp2 (mock) this is 100% / fp=0 by construction -> the baseline."""
    detections = analyze(text, "de") + analyze(text, "en")

    # All ground-truth occurrences of every PII value in the text.
    ground_truth = []
    for item in pii:
        for start, end in find_all(text, item["value"]):
            ground_truth.append({"value": item["value"], "type": item["type"],
                                 "start": start, "end": end})

    # For each PII value, decide whether it was fully / partially / not detected.
    outcomes = []
    for item in pii:
        occurrences = [g for g in ground_truth if g["value"] == item["value"]]
        any_char_covered = False
        all_chars_covered = True
        detected_types = set()

        for occurrence in occurrences:
            length = occurrence["end"] - occurrence["start"]
            covered = [False] * length
            for detection in detections:
                if spans_overlap(occurrence["start"], occurrence["end"],
                                 detection["start"], detection["end"]):
                    detected_types.add(detection["entity_type"])
                    overlap_start = max(occurrence["start"], detection["start"])
                    overlap_end = min(occurrence["end"], detection["end"])
                    for pos in range(overlap_start, overlap_end):
                        covered[pos - occurrence["start"]] = True
            any_char_covered = any_char_covered or any(covered)
            all_chars_covered = all_chars_covered and all(covered)

        if occurrences and all_chars_covered:
            outcome = "full"
        elif any_char_covered:
            outcome = "partial"
        else:
            outcome = "miss"
        outcomes.append({"value": item["value"], "expected": item["type"],
                         "outcome": outcome, "detected_as": sorted(detected_types)})

    # False positives: detections that overlap no ground-truth PII (deduplicated).
    false_positives = []
    seen = set()
    for detection in detections:
        overlaps_pii = any(
            spans_overlap(g["start"], g["end"], detection["start"], detection["end"])
            for g in ground_truth
        )
        if overlaps_pii:
            continue
        key = (detection["start"], detection["end"], detection["entity_type"])
        if key in seen:
            continue
        seen.add(key)
        explanation = detection.get("analysis_explanation") or {}
        false_positives.append({
            "text": text[detection["start"]:detection["end"]],
            "type": detection["entity_type"],
            "score": round(detection.get("score", 0), 2),
            "recognizer": explanation.get("recognizer"),
            "pattern": explanation.get("pattern_name"),
        })

    counts = {
        "expected": len(pii),
        "full": sum(1 for o in outcomes if o["outcome"] == "full"),
        "partial": sum(1 for o in outcomes if o["outcome"] == "partial"),
        "miss": sum(1 for o in outcomes if o["outcome"] == "miss"),
        "fp": len(false_positives),
    }
    return counts, outcomes, false_positives


# --------------------------------------------------------------------------- #
#  Level 2/3 and function checks
# --------------------------------------------------------------------------- #
def check_tool(expect: dict) -> tuple[bool, str]:
    """The tool call arrived with the REAL values (level 2/3 at the tool level)."""
    try:
        last = requests.get(f"{TOOLS}/last", timeout=5).json()
    except requests.RequestException as error:
        return False, f"tools-unreachable: {error}"
    if not last:
        return False, "no-tool-call"
    if last.get("tool") != expect["tool"]:
        return False, f"wrong-tool: {last.get('tool')}"

    args = last.get("args", {})
    args_blob = json.dumps(args, ensure_ascii=False)
    if re.search(r"\[[A-Z_]+_[0-9a-f]{8,}\]", args_blob):
        return False, "placeholder-in-args (not de-pseudonymized)"
    for key, expected_value in expect.get("args_contains", {}).items():
        if expected_value not in str(args.get(key, "")):
            return False, f"arg {key} != {expected_value!r} (was {args.get(key)!r})"
    return True, "ok"


def check_reply(agent_result: dict, expect: dict) -> tuple[bool, str]:
    """Conversation: the real values appear (de-pseudonymized) in the reply."""
    text = reply_text(agent_result)
    missing = [needle for needle in expect.get("reply_contains", []) if needle not in text]
    if missing:
        return False, "missing in reply: " + "; ".join(missing)
    return True, "ok"


# Correlating the real effect (mail/event) to THIS run: we snapshot the store BEFORE
# the agent acts and count only what is NEW. We deliberately do NOT rely on the agent
# writing a per-run marker into the mail body / event title -- the model drops such
# markers sometimes, which would mark an effect that really happened as "not sent".
def mailpit_message_ids() -> set[str]:
    """Snapshot of all current Mailpit message IDs (taken before the agent acts)."""
    try:
        data = requests.get(f"{MAILPIT}/api/v1/messages", params={"limit": 500}, timeout=5).json()
        return {m["ID"] for m in data.get("messages", [])}
    except (requests.RequestException, ValueError, KeyError):
        return set()


def check_mail_effect(pre_ids: set[str], expect: dict) -> tuple[bool, str]:
    """The mail really landed in Mailpit. Correlated by "new since the pre-run
    snapshot": a new message to the expected recipient means this run sent it. We do
    not rely on any per-run marker in the mail body (the agent may omit it)."""
    try:
        data = requests.get(f"{MAILPIT}/api/v1/messages", params={"limit": 500}, timeout=5).json()
    except (requests.RequestException, ValueError) as error:
        return False, f"mailpit-unreachable: {error}"

    new_messages = [m for m in data.get("messages", []) if m["ID"] not in pre_ids]
    if not new_messages:
        return False, "no new mail sent"

    wanted_recipient = expect.get("args_contains", {}).get("to")
    if wanted_recipient:
        for message in new_messages:
            recipients = [t["Address"] for t in message.get("To", [])]
            if wanted_recipient in recipients:
                return True, f"mail to {wanted_recipient}"
        return False, f"mail sent, but not to {wanted_recipient}"
    return True, f"{len(new_messages)} new mail(s)"


def caldav_all_events() -> list[dict]:
    """Read every calendar event (uid + raw iCal) from all calendars, via the
    agent-tools container (it has the caldav library). Returns [] on error."""
    script = (
        "import caldav,json,warnings\n"
        "warnings.filterwarnings('ignore')\n"
        f"c=caldav.DAVClient(url='http://radicale:5232/',username='{CALDAV_USER}',password='{CALDAV_PASS}')\n"
        "out=[]\n"
        "for cal in c.principal().calendars():\n"
        "    for e in cal.events():\n"
        "        raw=e.data\n"
        "        uid=''\n"
        "        for line in raw.splitlines():\n"
        "            if line.startswith('UID:'):\n"
        "                uid=line[4:].strip(); break\n"
        "        out.append({'uid': uid, 'raw': raw})\n"
        "print(json.dumps(out))\n"
    )
    try:
        completed = subprocess.run(
            ["docker", "exec", "-i", TOOLS_CONTAINER, "python3", "-c", script],
            capture_output=True, text=True, timeout=20,
        )
        line = next((l for l in completed.stdout.splitlines() if l.strip().startswith("[")), "")
        return json.loads(line) if line else []
    except Exception:  # noqa: BLE001
        return []


def caldav_event_uids() -> set[str]:
    """Snapshot of all current calendar event UIDs (taken before the agent acts)."""
    return {event["uid"] for event in caldav_all_events()}


def check_calendar_effect(pre_uids: set[str], expect: dict) -> tuple[bool, str]:
    """The event really landed in Radicale. Correlated by "new since the pre-run
    snapshot" (the tool assigns each event a unique UID), so we do not depend on the
    agent putting any per-run marker in the event title. The new event must contain
    the expected fields (e.g. the attendee)."""
    new_events = [e for e in caldav_all_events() if e["uid"] not in pre_uids]
    if not new_events:
        return False, "no new event created"
    for event in new_events:
        raw = event["raw"]
        if all(value in raw for value in expect.get("args_contains", {}).values()):
            return True, "event ok"
    missing = [f"{k}={v!r}" for k, v in expect.get("args_contains", {}).items()]
    return False, "event created, but missing " + ", ".join(missing)


# --------------------------------------------------------------------------- #
#  One run (one dataset row, one iteration)
# --------------------------------------------------------------------------- #
def run_row(row: dict, iteration: int, model: str | None, timeout: int,
            keep_memory: bool, group: str) -> dict:
    run_id = uuid.uuid4().hex[:8]          # only to give each run a fresh session
    message = row["prompt"]
    session_key = f"agent:main:exp2-{row['id']}-{run_id}"
    is_control = bool(model and "ollama-direct" in model)
    expect = row["expect"]

    # 1. Clean starting state.
    reset_all()
    if not keep_memory:
        clear_workspace_memory()
    set_pii(row["pii"])

    # 2. Detection quality on the prompt (deterministic, independent of the agent).
    #    Pointless for the control group (no proxy/presidio) -> skip.
    if is_control:
        det_counts = {"expected": 0, "full": 0, "partial": 0, "miss": 0, "fp": 0}
        det_outcomes, det_fps = [], []
    else:
        det_counts, det_outcomes, det_fps = measure_detection(message, row["pii"])

    # 3. Snapshot the out-of-band store BEFORE the agent acts, so the function check
    #    can correlate by "what is new" instead of by the run UUID (which the agent
    #    sometimes drops from the mail body / event title).
    effect = expect.get("effect")
    pre_mail_ids = mailpit_message_ids() if effect == "mailpit" else set()
    pre_event_uids = caldav_event_uids() if effect == "radicale" else set()

    # 4. Run one agent turn.
    started = time.time()
    result = run_agent(session_key, message, model, timeout)
    seconds = round(time.time() - started, 1)
    agent_error = result.get("_error")

    # 4a. Level 1 (skip for the control group: no proxy -> nothing to pseudonymize).
    if is_control:
        level1_ok, level1_kind, level1_detail = None, "n/a", "n/a (control group, no proxy)"
    else:
        level1_ok, level1_kind, level1_detail = check_level1(row["pii"])

    # 4b. Level 2/3: tool arguments (or the reply for conversation rows).
    if expect["tool"]:
        level23_ok, level23_detail = check_tool(expect)
    else:
        level23_ok, level23_detail = check_reply(result, expect)

    # 4c. Function: the real side effect (mail in Mailpit / event in Radicale),
    #     correlated against the pre-run snapshot taken in step 3.
    if effect in ("mailpit", "radicale"):
        time.sleep(1.0)  # give Mailpit/Radicale a moment to index the effect
    if effect == "mailpit":
        function_ok, function_detail = check_mail_effect(pre_mail_ids, expect)
    elif effect == "radicale":
        function_ok, function_detail = check_calendar_effect(pre_event_uids, expect)
    else:
        # conversation: function == a correct reply (already checked in level23_ok)
        function_ok, function_detail = level23_ok, "= reply check"

    return {
        "group": group,                  # control | exp2-mock | exp3-real
        "id": row["id"],
        "domain": row["domain"],
        "lang": row.get("lang", ""),
        "iteration": iteration,
        "seconds": seconds,
        "agent_error": agent_error or "",
        "level1_pseudonym": "" if level1_ok is None else level1_ok,
        "level1_kind": level1_kind,      # clean | full | partial | none | n/a
        "level1_detail": level1_detail,
        # Detection on the prompt (PII -> ?): detected / partial / missed + FP
        "pii_expected": det_counts["expected"],
        "pii_full": det_counts["full"],
        "pii_partial": det_counts["partial"],
        "pii_miss": det_counts["miss"],
        "fp_count": det_counts["fp"],
        "pii_outcomes": json.dumps(det_outcomes, ensure_ascii=False),
        "false_positives": json.dumps(det_fps, ensure_ascii=False),
        "level23_or_reply": level23_ok,
        "level23_detail": level23_detail,
        "function_ok": function_ok,
        "function_detail": function_detail,
    }


# --------------------------------------------------------------------------- #
#  Main
# --------------------------------------------------------------------------- #
def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Experiment 2 orchestrator")
    parser.add_argument("--dataset", default=str(HERE / "dataset.json"))
    parser.add_argument("--iterations", type=int, default=DEFAULT_ITERATIONS,
                        help="repetitions per row")
    parser.add_argument("--only", default="",
                        help="filter (comma-separated): row IDs, a domain, or a language (de/en)")
    parser.add_argument("--model", default="",
                        help="model override (e.g. ollama-direct/... for the control group)")
    parser.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT,
                        help="agent-turn timeout (s)")
    parser.add_argument("--keep-memory", action="store_true",
                        help="do NOT clear the workspace memory")
    parser.add_argument("--group", default=DEFAULT_GROUP,
                        help="label for this constellation (CSV column)")
    return parser.parse_args()


def acquire_lock() -> None:
    """Make sure only one orchestrator runs at a time (see LOCKFILE comment)."""
    if LOCKFILE.exists():
        try:
            other_pid = int(LOCKFILE.read_text().strip())
            os.kill(other_pid, 0)  # is that process still alive?
            sys.exit(f"Aborting: an orchestrator is already running (PID {other_pid}). "
                     f"Only ONE run at a time! Remove {LOCKFILE} if it is stale.")
        except (ProcessLookupError, ValueError):
            pass  # stale lock -> overwrite
    LOCKFILE.write_text(str(os.getpid()))


def load_rows(dataset_path: str, only: str) -> list[dict]:
    data = json.loads(Path(dataset_path).read_text())
    rows = data["rows"]
    if only:
        wanted = set(only.split(","))
        rows = [row for row in rows
                if row["id"] in wanted
                or row["domain"] in wanted
                or row.get("lang") in wanted]
    return rows


def print_run_line(result: dict, iteration: int) -> None:
    """One compact status line per run, plus details on failures."""
    level1_labels = {True: "PSEUDO OK", False: "PSEUDO FAIL", "": "PSEUDO -"}
    level1 = level1_labels.get(result["level1_pseudonym"], "PSEUDO -")
    level23 = "TOOL/REPLY OK" if result["level23_or_reply"] else "TOOL/REPLY FAIL"
    function = "FUNC OK" if result["function_ok"] else "FUNC FAIL"
    print(f"[{result['id']:>8} #{iteration}] {level1}  {level23}  {function}  ({result['seconds']}s)")
    if not result["level23_or_reply"]:
        print(f"           tool/reply: {result['level23_detail']}")
    if not result["function_ok"]:
        print(f"           function : {result['function_detail']}")
    if result["level1_pseudonym"] is False:
        print(f"           LEVEL1[{result['level1_kind']}]: {result['level1_detail']}")
    if result["agent_error"]:
        print(f"           agent-err: {result['agent_error']}")


def print_summary(results: list[dict], csv_path: Path) -> None:
    total = len(results)

    def pct(predicate) -> str:
        count = sum(1 for r in results if predicate(r))
        return f"{count}/{total} ({100 * count // total if total else 0}%)"

    full_leaks = sum(1 for r in results if r["level1_kind"] == "full")
    partial_leaks = sum(1 for r in results if r["level1_kind"] == "partial")
    level1_applicable = sum(1 for r in results if r["level1_pseudonym"] != "")
    print("\n== Summary ==")
    if level1_applicable == 0:
        # control group runs without a proxy -> level 1 is not applicable, not "0%".
        print("  Level 1 fully pseudonymized  :  n/a (control group, no proxy)")
    else:
        print("  Level 1 fully pseudonymized  : ", pct(lambda r: r["level1_pseudonym"] is True))
        print(f"    of which full leaks: {full_leaks} | partial leaks: {partial_leaks}")
    print("  Level 2/3 or reply           : ", pct(lambda r: r["level23_or_reply"]))
    print("  Function (effect)            : ", pct(lambda r: r["function_ok"]))

    # Detection on the prompt (Presidio recall + false positives).
    expected = sum(r["pii_expected"] for r in results)
    detected_full = sum(r["pii_full"] for r in results)
    detected_partial = sum(r["pii_partial"] for r in results)
    missed = sum(r["pii_miss"] for r in results)
    false_positives = sum(r["fp_count"] for r in results)
    print("\n-- Detection on the prompt (ground truth: dataset.pii) --")
    print(f"  PII expected: {expected} | full: {detected_full} | "
          f"partial: {detected_partial} | missed: {missed}")
    recall = 100 * detected_full / expected if expected else 0
    print(f"  Recall (full): {recall:.1f}%  |  false positives total: {false_positives}")

    # Per-language breakdown (only when both languages are present).
    languages = sorted({r.get("lang", "") for r in results if r.get("lang")})
    if len(languages) > 1:
        for language in languages:
            subset = [r for r in results if r.get("lang") == language]
            ok = sum(1 for r in subset if r["level1_pseudonym"] is True)
            exp = sum(r["pii_expected"] for r in subset)
            full = sum(r["pii_full"] for r in subset)
            part = sum(r["pii_partial"] for r in subset)
            miss = sum(r["pii_miss"] for r in subset)
            fp = sum(r["fp_count"] for r in subset)
            print(f"    [{language}] Level1 {ok}/{len(subset)} | detection full {full}/{exp}"
                  f" (partial {part}, miss {miss}) | FP {fp}")
    print(f"\nDetails: {csv_path}")


def main() -> None:
    args = parse_args()

    # Show progress immediately (also in the background, without a TTY).
    sys.stdout.reconfigure(line_buffering=True)

    acquire_lock()
    try:
        rows = load_rows(args.dataset, args.only)
        model = args.model or None
        print(f"== Experiment 2 == {len(rows)} rows x {args.iterations} iteration(s)"
              f"{' | model=' + model if model else ''}\n")

        out_dir = HERE / "results" / datetime.now().strftime("%Y%m%d_%H%M%S")
        out_dir.mkdir(parents=True, exist_ok=True)
        csv_path = out_dir / "detail.csv"

        results = []
        writer = None
        with csv_path.open("w", newline="") as handle:
            for row in rows:
                for iteration in range(1, args.iterations + 1):
                    result = run_row(row, iteration, model, args.timeout,
                                     args.keep_memory, args.group)
                    results.append(result)

                    if writer is None:  # header from the first row's keys
                        writer = csv.DictWriter(handle, fieldnames=list(result.keys()))
                        writer.writeheader()
                    writer.writerow(result)   # write incrementally -> survives an abort
                    handle.flush()

                    print_run_line(result, iteration)
    finally:
        LOCKFILE.unlink(missing_ok=True)

    print_summary(results, csv_path)


if __name__ == "__main__":
    main()
