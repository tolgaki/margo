---
name: CoS — Ambient scan
verb: ambient
tier: ambient
routine: proactive.md § Tier 3
cron: "15 5 * * 1-5"
mode: autopilot
---

Load the `chief-of-staff` skill, then run `references/proactive.md` as tier: **ambient**.

Unattended mode — never call `ask_user`, nothing is sent or changed. Ambient findings NEVER
interrupt; they queue for the Friday anchor.

Read `references/state-operations.md` first. Record each source's coverage independently;
partial or failed reads do not advance its successful checkpoint. Never call `workiq-ask`
in this tier; queue synthesis work for an anchor instead.

Run each scan: `references/follow-through.md` (commitment ageing), `references/relationships.md`
(cadence drift), `references/github.md` (stale PRs, aged review requests),
`references/calendar.md` § D. Hygiene, `references/doc-queue.md`.

Promote an item ONLY on the run where it crosses a threshold — not every day afterwards.
Steady-state decay is not news, and re-promoting it is how this tier becomes noise the user
mutes. Queue promoted items with `"tier":"ambient"` and a per-rung dedupe id.

Read confirmed work from the canonical ledger; proposals and suspected resolutions remain
unconfirmed. Run `scripts/proactive_state.py prune --days 30` at the end; never prune pending
work as though it had been delivered. Produce no chat output.
