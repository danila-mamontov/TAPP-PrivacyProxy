#!/usr/bin/env python3
"""Smoke test for Radicale (CalDAV): create event -> read back -> delete.

Proves that we can write calendar events AND read them back machine-readably, just
like mails with Mailpit.

    pip install caldav
    python3 smoke_caldav.py
"""
import sys
import caldav

URL = "http://localhost:5232/"
USER, PW = "test", "test"
UUID = "a1b2-c3d4"

ICAL = f"""BEGIN:VCALENDAR
VERSION:2.0
PRODID:-//exp3//smoke//EN
BEGIN:VEVENT
UID:smoke-{UUID}
DTSTART:20260715T100000
DTEND:20260715T110000
SUMMARY:Smoke Test Event {UUID}
END:VEVENT
END:VCALENDAR
"""

client = caldav.DAVClient(url=URL, username=USER, password=PW)
principal = client.principal()

# Get or create a calendar.
cals = principal.calendars()
cal = cals[0] if cals else principal.make_calendar(name="exp3")
print(f"Calendar: {cal}")

# Create the event.
cal.save_event(ICAL)
print("Event saved.")

# Read it back.
events = cal.events()
print(f"Events in the calendar: {len(events)}")
found = False
for e in events:
    if UUID in e.data:
        found = True
        print("Found (excerpt):")
        for line in e.data.splitlines():
            if line.startswith(("SUMMARY", "DTSTART", "UID")):
                print("   " + line.strip())

# Clean up (this is also the reset mechanism between runs).
for e in list(cal.events()):
    if UUID in e.data:
        e.delete()
print("Event(s) with the UUID deleted (reset test).")

print("\nOK" if found else "\nERROR: event not found")
sys.exit(0 if found else 1)
