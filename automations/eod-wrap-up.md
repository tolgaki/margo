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

Read `references/state-operations.md` first. The anchor owns one leased queue batch; the
underlying routine must not drain again. Persist the output and publication receipt before
acknowledging only the items actually included.

Review confirmed work, candidates, and execution receipts in the work ledger. Stage proposed
additions and resolutions for approval, never confirm them unattended or hand-edit the
compatibility export. Carry unresolved meeting items forward and retain recap-pending status.

Record successful coverage independently per source after complete paging and durable ingestion.
Failed or partial reads do not advance successful checkpoints. Include health changes without
repeating unchanged failure notices on every run.
