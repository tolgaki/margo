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
- Read `references/state-operations.md` and use each source's successful checkpoint, never
  `cursor-get sweep` as a shared data watermark. On first use, bound the requested window to
  the last hour and record the initial coverage limit.
- Dedupe source identity AND revision, so a changed event is not suppressed by its old ID.
- Apply the interrupt test. Items that clear it are surfaced; everything else goes to
  `queue-add` and waits for the next anchor. When in doubt, queue.
- Record attempts separately from successful coverage. Advance only after complete paging and
  durable ingestion for that source; preserve the checkpoint on failure or partial results.
- Recheck due recap-pending meetings through supported read paths. Access denial is blocked,
  not a reason to retry through a different tool.
- Publish a durable receipt for anything surfaced. Failed publication retains the item.
- Produce NO output when nothing clears the bar. Silence is a successful run — do not
  manufacture something to report.
