"""
Agent-tools MCP server for Experiment 2.

Exposes two deterministic tools to OpenClaw over MCP (streamable-http, path /mcp):

  - send_email(to, subject, body)                 -> SMTP to Mailpit
  - create_calendar_event(title, start, end, ...) -> CalDAV to Radicale

The tool call flow is: LLM -> (answer with a tool_call, arguments as placeholders)
-> PrivacyProxy de-anonymizes the arguments -> OpenClaw calls this tool with the
REAL values. So this server is the measurement point for level 2/3 at the tool
level: "did the tool arguments arrive with the real values (instead of
placeholders)?" and at the same time the place that performs the real effect (mail
is sent, event is created) -> later verified out-of-band by the orchestrator via the
Mailpit API and Radicale CalDAV.

Every call is recorded and available via extra routes:
  GET  /calls  -> all recorded tool calls [{ts, tool, args, result}]
  GET  /last   -> the last call
  POST /reset  -> clear the log (before every iteration)
  GET  /health

Network: runs in the exp2-infra compose network and reaches the targets by service
name (mailpit:1025, radicale:5232). OpenClaw reaches this server from its own network
via host.docker.internal:<published port>/mcp.
"""
import os
import time
import smtplib
from email.message import EmailMessage
from datetime import datetime

import caldav
from icalendar import Calendar, Event
from mcp.server.fastmcp import FastMCP
from starlette.requests import Request
from starlette.responses import JSONResponse

# --- Targets (reachable by service name in the exp2-infra network) ---
SMTP_HOST = os.environ.get("SMTP_HOST", "mailpit")
SMTP_PORT = int(os.environ.get("SMTP_PORT", "1025"))
MAIL_FROM = os.environ.get("MAIL_FROM", "openclaw@exp2.local")

CALDAV_URL = os.environ.get("CALDAV_URL", "http://radicale:5232/")
CALDAV_USER = os.environ.get("CALDAV_USER", "test")
CALDAV_PASS = os.environ.get("CALDAV_PASS", "test")
CALDAV_CAL = os.environ.get("CALDAV_CAL", "exp2")

mcp = FastMCP("exp2-agent-tools", host="0.0.0.0", port=int(os.environ.get("PORT", "3100")))

# Recording of all tool calls (the tool-level measurement point).
_calls: list[dict] = []


def _record(tool: str, args: dict, result: str) -> None:
    _calls.append({"ts": time.time(), "tool": tool, "args": args, "result": result})


@mcp.tool()
def send_email(to: str, subject: str, body: str) -> str:
    """Send an email. Use this tool when the user wants to write/send an email.

    Args:
        to: Recipient email address.
        subject: Subject of the email.
        body: Text content of the email.
    """
    msg = EmailMessage()
    msg["From"] = MAIL_FROM
    msg["To"] = to
    msg["Subject"] = subject
    msg.set_content(body)
    with smtplib.SMTP(SMTP_HOST, SMTP_PORT, timeout=10) as s:
        s.send_message(msg)
    result = f"Email sent to {to} (subject: {subject})."
    _record("send_email", {"to": to, "subject": subject, "body": body}, result)
    return result


@mcp.tool()
def create_calendar_event(
    title: str,
    start: str,
    end: str = "",
    attendee: str = "",
    description: str = "",
) -> str:
    """Create a calendar event. Use this tool when the user wants to add an
    appointment/meeting.

    Args:
        title: Title of the event.
        start: Start time as ISO 8601 (e.g. 2026-07-15T14:00:00).
        end: End time as ISO 8601 (optional; default: start + 1 hour).
        attendee: Email address of a participant (optional).
        description: Free-text description (optional).
    """
    client = caldav.DAVClient(url=CALDAV_URL, username=CALDAV_USER, password=CALDAV_PASS)
    principal = client.principal()
    try:
        calendar = principal.calendar(name=CALDAV_CAL)
        calendar.events()  # force access -> NotFound if the calendar does not exist
    except Exception:
        calendar = principal.make_calendar(name=CALDAV_CAL)

    cal = Calendar()
    cal.add("prodid", "-//exp2-agent-tools//EN")
    cal.add("version", "2.0")
    ev = Event()
    uid = f"exp2-{int(time.time()*1000)}@exp2.local"
    ev.add("uid", uid)
    ev.add("summary", title)
    ev.add("dtstart", datetime.fromisoformat(start))
    ev.add("dtend", datetime.fromisoformat(end) if end else datetime.fromisoformat(start))
    if description:
        ev.add("description", description)
    if attendee:
        ev.add("attendee", f"mailto:{attendee}")
    cal.add_component(ev)
    calendar.save_event(cal.to_ical().decode("utf-8"))

    result = f"Event '{title}' created at {start} (uid {uid})."
    _record(
        "create_calendar_event",
        {"title": title, "start": start, "end": end, "attendee": attendee, "description": description},
        result,
    )
    return result


@mcp.custom_route("/calls", methods=["GET"])
async def calls(_request: Request) -> JSONResponse:
    return JSONResponse(_calls)


@mcp.custom_route("/last", methods=["GET"])
async def last(_request: Request) -> JSONResponse:
    return JSONResponse(_calls[-1] if _calls else {})


@mcp.custom_route("/reset", methods=["POST"])
async def reset(_request: Request) -> JSONResponse:
    _calls.clear()
    return JSONResponse({"ok": True})


@mcp.custom_route("/health", methods=["GET"])
async def health(_request: Request) -> JSONResponse:
    return JSONResponse({"status": "ok"})


if __name__ == "__main__":
    mcp.run(transport="streamable-http")
