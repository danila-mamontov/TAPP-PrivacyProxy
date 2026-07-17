"""
Calendar viewer for Experiment 2 (read-only).

Reads the Radicale calendar over CalDAV and renders the events as a simple,
self-refreshing HTML page. Purely for looking at the events the agent created - no
editing (use Radicale's own /.web/ interface for that).

Runs in the exp2-infra network, reaches Radicale by service name (radicale:5232),
and listens on port 5000 (published on the host).
"""
import os
import datetime as dt

import caldav
from flask import Flask, Response

app = Flask(__name__)

CALDAV_URL = os.environ.get("CALDAV_URL", "http://radicale:5232/")
CALDAV_USER = os.environ.get("CALDAV_USER", "test")
CALDAV_PASS = os.environ.get("CALDAV_PASS", "test")


def _fmt(value) -> str:
    if isinstance(value, (dt.datetime, dt.date)):
        return value.strftime("%a %d.%m.%Y %H:%M" if isinstance(value, dt.datetime) else "%a %d.%m.%Y")
    return str(value)


def collect_events() -> list[dict]:
    client = caldav.DAVClient(url=CALDAV_URL, username=CALDAV_USER, password=CALDAV_PASS)
    events = []
    for cal in client.principal().calendars():
        for ev in cal.events():
            c = ev.icalendar_component
            start = c.get("dtstart")
            end = c.get("dtend")
            att = c.get("attendee")
            events.append({
                "calendar": str(cal.name or cal.id or "?"),
                "summary": str(c.get("summary", "")),
                "start": start.dt if start else None,
                "end": end.dt if end else None,
                "attendee": (str(att).replace("mailto:", "") if att else ""),
                "description": str(c.get("description", "")),
            })
    events.sort(key=lambda e: (e["start"] is None, str(e["start"])))
    return events


PAGE = """<!doctype html><html lang="en"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<meta http-equiv="refresh" content="10">
<title>Exp2 Calendar ({n})</title>
<style>
 body{{font-family:system-ui,sans-serif;margin:0;background:#0f172a;color:#e2e8f0}}
 header{{padding:16px 24px;background:#1e293b;position:sticky;top:0}}
 header h1{{margin:0;font-size:18px}} header small{{color:#94a3b8}}
 table{{border-collapse:collapse;width:100%}}
 th,td{{text-align:left;padding:10px 24px;border-bottom:1px solid #1e293b;font-size:14px;vertical-align:top}}
 th{{color:#94a3b8;font-weight:600;position:sticky;top:56px;background:#0f172a}}
 tr:hover td{{background:#111c33}}
 .sum{{font-weight:600}} .att{{color:#38bdf8}} .desc{{color:#cbd5e1}}
 .empty{{padding:40px;color:#94a3b8;text-align:center}}
</style></head><body>
<header><h1>📅 Experiment 2 — Calendar</h1>
<small>{n} event(s) · Radicale · updated {ts} · auto-refresh 10s</small></header>
{body}</body></html>"""


@app.get("/")
def index() -> Response:
    try:
        events = collect_events()
    except Exception as e:  # noqa: BLE001
        return Response(f"<p style='color:#f87171'>Error reading Radicale: {e}</p>", mimetype="text/html")

    if not events:
        body = "<div class='empty'>No events in the calendar yet.</div>"
    else:
        rows = "".join(
            f"<tr><td>{_fmt(e['start'])}</td><td>{_fmt(e['end']) if e['end'] else ''}</td>"
            f"<td class='sum'>{e['summary']}</td>"
            f"<td class='att'>{e['attendee']}</td>"
            f"<td class='desc'>{e['description']}</td>"
            f"<td>{e['calendar']}</td></tr>"
            for e in events
        )
        body = ("<table><thead><tr><th>Start</th><th>End</th><th>Title</th>"
                "<th>Attendee</th><th>Description</th><th>Calendar</th></tr></thead>"
                f"<tbody>{rows}</tbody></table>")

    html = PAGE.format(n=len(events), ts=dt.datetime.now().strftime("%H:%M:%S"), body=body)
    return Response(html, mimetype="text/html")


if __name__ == "__main__":
    app.run(host="0.0.0.0", port=int(os.environ.get("PORT", "5000")))
