import os
import smtplib
import time
import requests

from icalendar import Calendar, Event
from email.mime.multipart import MIMEMultipart
from email.mime.text import MIMEText
from datetime import datetime
from pathlib import Path
import caldav
from dotenv import load_dotenv
from mcp.server.fastmcp import FastMCP
from starlette.requests import Request
from starlette.responses import JSONResponse

# When started directly on the host, read the .env next to this file. Inside a
# container this is a no-op: load_dotenv never overrides variables that are
# already set, so the compose "environment:" section always wins.
load_dotenv(Path(__file__).resolve().parent / ".env")

# Targets are reachable in 'exp-rq3-controlgroup-network' docker network via names: e.g. mailpit and radicale
# no localhost or 127.0.0.1 required.
SMTP_HOST = os.environ.get("SMTP_HOST", "mailpit")
SMTP_PORT = int(os.environ.get("SMTP_PORT", "1025"))
MAIL_FROM = os.environ.get("MAIL_FROM", "controlgroup@exp.rq3.local")
MAILPIT_API = os.environ.get("MAILPIT_API", "http://mailpit:8025")
CALDAV_URL = os.environ.get("CALDAV_URL", "http://radicale:5232/")
CALDAV_USER = os.environ.get("CALDAV_USER", "test")
CALDAV_PASS = os.environ.get("CALDAV_PASS", "test")
CALDAV_CAL = os.environ.get("CALDAV_CAL", "exp_rq3_controlgroup")
# Mailpit has TWO doors: SMTP (send mail in) and the REST API (inspect/delete).
MAILPIT_API = os.environ.get("MAILPIT_API", "http://mailpit:8025")

mcp = FastMCP("exp-rq3-mcp-server", host="0.0.0.0", port=int(os.environ.get("PORT", "3100")))

# Recording of ALL tool calls, including failed ones. Every call is recorded as
# "attempted" the moment it arrives; the status is updated to "ok" or "error"
# afterwards. This lets the experiment distinguish "tool was never called" from
# "tool was called but failed" (e.g. an unparseable date) - two very different
# failure modes.
_calls: list[dict] = []

def _record(tool: str, args: dict) -> dict:
    call = {"ts": time.time(), "tool": tool, "args": args, "status": "attempted", "result": ""}
    _calls.append(call)
    return call

@mcp.tool()
def send_mail(
        to: str,
        subject: str,
        body: str) -> str:
    """Send an email. Use this tool when the user wants to write/send an email.

    Args:
        to: Recipient email address.
        subject: Subject of the email.
        body: Text content of the email.
    Returns:
        str: Result of function.
    """
    call = _record("send_mail", {"to": to, "subject": subject, "body": body})
    try:
        msg = MIMEMultipart()
        msg['From'] = MAIL_FROM
        msg['To'] = to
        msg['Subject'] = subject
        msg.attach(MIMEText(body))

        with smtplib.SMTP(SMTP_HOST, SMTP_PORT, timeout=10) as mailserver:
            mailserver.send_message(msg)

        call["status"] = "ok"
        call["result"] = f"Email sent to {to} (subject: {subject})."
        return call["result"]
    except Exception as error:
        call["status"] = "error"
        call["result"] = str(error)
        raise

@mcp.tool()
def create_calendar_event(
        title: str,
        start: str,
        end: str = "",
        attendee: str = "",
        description: str = "") -> str:
    """Create a calendar event. Use this tool when the user wants to add an
    appointment/meeting.

    Args:
        title: Title of the event.
        start: Start time as ISO 8601 (e.g. 2026-07-15T14:00:00).
        end: End time as ISO 8601 (optional; default: start + 1 hour).
        attendee: Email address of a participant (optional).
        description: Free-text description (optional).
    Returns:
        str: Result of function.
    """
    call = _record("create_calendar_event",
                   {"title": title, "start": start, "end": end,
                    "attendee": attendee, "description": description})
    try:
        client = caldav.DAVClient(url=CALDAV_URL, username=CALDAV_USER, password=CALDAV_PASS)
        principal = client.principal() # get client's account in radicale

        try:
            calendar = principal.calendar(name=CALDAV_CAL)
            calendar.events()  # force access -> NotFound if the calendar does not exist
        except Exception:
            calendar = principal.make_calendar(name=CALDAV_CAL)

        cal = Calendar() # you have to group an event into a Calendar()-Object to place it into a CalDAV Calender

        # RFC 5545
        cal.add("prodid", "-//exp-rq3-mcp-server//-") # product identifier
        cal.add("version", "2.0") # since 1998 default 2.0

        ev = Event()
        uid = f"controlgroup-{int(time.time()*1000)}@exp.rq3.local"
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

        call["status"] = "ok"
        call["result"] = f"Event '{title}' created at {start} (uid {uid})."
        return call["result"]
    except Exception as error:
        call["status"] = "error"
        call["result"] = str(error)
        raise

@mcp.custom_route("/calls", methods=["GET"])
async def calls(_request: Request) -> JSONResponse:
    return JSONResponse(_calls)

@mcp.custom_route("/last", methods=["GET"])
async def last(_request: Request) -> JSONResponse:
    return JSONResponse(_calls[-1] if _calls else {})

@mcp.custom_route("/reset", methods=["POST"])
async def reset(_request: Request) -> JSONResponse:
    # 1) forget all recorded tool calls
    _calls.clear()

    # 2) delete all mails in Mailpit (REST API, not the SMTP port!)
    requests.delete(f"{MAILPIT_API}/api/v1/messages", timeout=10)
    requests.delete(f"{MAILPIT_API}/api/v1/messages", timeout=10)

    # 3) delete all events in the Radicale calendar
    client = caldav.DAVClient(url=CALDAV_URL, username=CALDAV_USER, password=CALDAV_PASS)
    try:
        calendar = client.principal().calendar(name=CALDAV_CAL)
        for event in calendar.events():
            event.delete()
    except Exception:
        pass  # calendar does not exist yet -> nothing to clean

    return JSONResponse({"ok": True})

@mcp.custom_route("/health", methods=["GET"])
async def health(_request: Request) -> JSONResponse:
    return JSONResponse({"status": "ok"})

if __name__ == "__main__":
    mcp.run(transport="streamable-http")
