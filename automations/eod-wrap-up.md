---
name: CoS — EOD wrap-up
verb: eod
tier: anchor
routine: daily-brief.md § Catch-up
cron: "45 17 * * 1-5"
mode: autopilot
---

Load the `chief-of-staff` skill, then run `references/proactive.md` as tier: **anchor**,
routine: **EOD wrap-up** (`references/daily-brief.md` § Catch-up / EOD).

Unattended mode — apply the Unattended Mode Contract exactly: never call `ask_user`, no
trailing offers, nothing is sent or RSVP'd or changed, drafts may be prepared but never
delivered.

Drain the queue first with `scripts/proactive_state.py queue-drain --format json`.

Review `commitments.md` for anything created or resolved today and note proposed updates for
approval — do not write them unattended.

Mark everything rendered and set cursors before exiting.
