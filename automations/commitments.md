---
name: CoS — Commitments and ambient digest
verb: commitments
tier: anchor
routine: follow-through.md + ambient digest
cron: "0 16 * * 5"
mode: autopilot
---

Load the `chief-of-staff` skill, then run `references/proactive.md` as tier: **anchor**,
routine: **commitment ageing** (`references/follow-through.md`).

Unattended mode — never call `ask_user`, nothing is sent, no row in `commitments.md` is
written without approval. Prepare nudge drafts and hold them.

Check for silent resolution BEFORE ageing anything — being told to chase someone who already
replied is the fastest way to lose trust in this.

Then render the week's ambient digest from the queue: one section, capped at five items,
worst first.
