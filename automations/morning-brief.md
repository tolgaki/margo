---
name: CoS — Morning brief
verb: brief
tier: anchor
routine: daily-brief.md (full)
cron: "0 6 * * 1-5"
mode: autopilot
---

Load the `chief-of-staff` skill, then run `references/proactive.md` as tier: **anchor**,
routine: **morning brief** (`references/daily-brief.md`, full depth).

Unattended mode — apply the Unattended Mode Contract exactly: never call `ask_user`, no
trailing offers, nothing is sent or RSVP'd or changed, drafts may be prepared but never
delivered.

Drain the queue first with `scripts/proactive_state.py queue-drain --format json` and fold
those items into the brief sections named by their `section` field. The brief must read as an
accumulation, not a scrape — if it looks identical to what "brief me" produces on demand, the
queue is not being drained.

Mark everything rendered and set delta cursors before exiting.
