---
name: CoS — Hourly sweep
verb: sweep
tier: sweep
routine: proactive.md § Tier 2
cron: "0 9-17 * * 1-5"
mode: autopilot
---

Load the `chief-of-staff` skill, then run `references/proactive.md` as tier: **sweep**.

Unattended mode. This tier is cheap, fast and USUALLY SILENT — that is the point of it.

- Never call `ask_user`, and never end with an offer. Nobody is reading; a run that asks a
  question is a hung run.
- Never call `workiq-ask` in a sweep. Delta (`workiq-call_function`) and `workiq-fetch` only.
- Read the cursor with `scripts/proactive_state.py cursor-get sweep`; if it exits 1, sweep the
  last hour only, never from the epoch.
- Dedupe every candidate with `seen <id>` before considering it.
- Apply the interrupt test. Items that clear it are surfaced; everything else goes to
  `queue-add` and waits for the next anchor. When in doubt, queue.
- Set the cursor even when nothing was found.
- Produce NO output when nothing clears the bar. Silence is a successful run — do not
  manufacture something to report.
