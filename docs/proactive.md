# Proactive & scheduled

Everything else in this repo is *pull* — a routine runs when you ask. This is the *push* half:
scheduled runs that produce the morning brief, the end-of-day wrap-up, hourly sweeps and weekly
ambient scans.

> This is the difference between a chief of staff and a search box: something is watching, and
> **mostly choosing not to speak.**

---

## The unattended contract

When a run is triggered by a schedule rather than by you, **nobody is reading**. Every other
routine assumes a human on the other end and ends by offering to draft something. Unattended,
that offer goes nowhere.

So the output contract changes, and getting this wrong is the main way the feature fails:

- **Never ask a question.** There is no one to answer. A run that ends in a question is a hung
  run.
- **No trailing offers.** Don't close with "want me to draft that?" — queue the item instead.
- **Healthy silence is success.** A completely covered source with no interrupt-worthy findings
  needs no message. Missing coverage is recorded as a health problem, not an empty successful sweep.
- **Read-only, always.** No sends, replies, posts, reactions, RSVPs, deletes or work-item changes
  — regardless of any standing authorization. Drafts may be prepared and held; never delivered.
- **Surface script failures.** A `WARNING` or non-zero exit from the state script goes into the
  next brief. A ledger that silently reset is why you'd start seeing repeats.

The third one is the hard one. A model handed an inbox will always find *something* it could
report. The discipline is not reporting it.

---

## The interrupt test

An item may break silence **only** if it clears one of these. Everything else queues.

1. **A VIP with a direct ask** — someone from `preferences.md`, addressed to you (not CC),
   asking for something.
2. **It touches a meeting starting within 2 hours** — a cancellation, a room change, a pre-read
   that just landed, an attendee dropping out.
3. **A confirmed commitment due today, still open** — from the work ledger, after a resolution check.
4. **A meeting cancelled or moved** — it either breaks the day or frees an hour. Both are worth
   knowing immediately.
5. **An explicit deadline today** — stated in the message, not inferred.

Everything else queues: FYI CCs, newsletters, threads that moved without needing you, comments on
an unblocked PR.

**When in doubt, queue.** The cost of a queued item is a line in tomorrow's brief. The cost of a
false interrupt is you muting the whole thing by Wednesday.

Your `preferences.md` standing rules layer on top: *always flag* becomes an additional interrupt
criterion, and *auto-deprioritize* is an absolute bar to interrupting.

---

## The schedule

Defined in [`automations/`](../automations/README.md) — one Markdown file each, front matter for
the schedule, body for the prompt. **This table is generated from those files**; edit the front
matter and run `./tools/gen-automations-docs.sh --write`.

<!-- BEGIN GENERATED: automations -->

| Automation | Tier | When | Routine | Wrapper |
|---|---|---|---|---|
| Commitments and ambient digest | anchor | Friday 16:00 | `follow-through.md + ambient digest` | `commitments` |
| EOD wrap-up | anchor | Weekdays 17:45 | `daily-brief.md § Catch-up` | `eod` |
| Morning brief | anchor | Weekdays 06:00 | `daily-brief.md (full)` | `brief` |
| Week ahead | anchor | Sunday 17:00 | `daily-brief.md § Week ahead` | `week` |
| Hourly sweep | sweep | Weekdays hourly 09:00–17:00 | `proactive.md § Tier 2` | `sweep` |
| Ambient scan | ambient | Weekdays 05:15 | `proactive.md § Tier 3` | `ambient` |

<!-- END GENERATED: automations -->

The same files drive both ways of running a schedule, so a prompt cannot mean one thing in cron
and another in the app.

---

## Three tiers

### Tier 1 — Anchors

Scheduled, always produce output, and **the only tier allowed to spend `workiq-ask`**.

An anchor **drains the queue first**, then runs the underlying routine and folds each queued item
into the section it belongs in — *Needs your response*, *FYI*, *Waiting on*. Not a separate
"here's what I saw overnight" block: those items are context for the brief, not an appendix.

> The morning brief must read as an **accumulation, not a scrape**. If it looks identical to what
> "brief me" produces on demand, the queue isn't being drained and the tiering is decorative.

The anchor alone leases its batch; the underlying routine never drains again. It persists the
output and a publication receipt before acknowledging only the included items. Source checkpoints
advance independently after complete ingestion, not as a blanket step at the end of the brief.

### Tier 2 — Sweeps

Hourly during working hours. **Cheap, fast, and usually silent.**

**Never call `workiq-ask` in a sweep.** It costs 10–60 seconds per call and this runs ~40 times a
week. Sweeps use `workiq-call_function` (delta) and `workiq-fetch` only.

> A sweep that takes a minute and prints nothing is a bug.

Calendar cancellations and moves are high-value signals. Discover supported delta capabilities
and record unavailable or denied sources honestly; a known path is not proof it works in a tenant.

First run has a trap: if there's no successful source checkpoint, **don't sweep from the epoch**.
Use a bounded window and record its boundary. Never advance the source's successful checkpoint
because another source succeeded.

### Tier 3 — Ambient

Low-urgency daily scans that surface *weekly* — relationship drift, calendar hygiene, the
document queue, stale PRs. Findings are promoted into an anchor rather than interrupting.

---

## State lives on disk

Every scheduled run is **a fresh session with no memory**. Continuity comes from
`scripts/proactive_state.py`, which manages leased delivery batches and per-source coverage in
the private account-scoped SQLite store shared with `work_state.py`.

```bash
python3 scripts/proactive_state.py --help
python3 scripts/proactive_state.py status
python3 scripts/margo_doctor.py --help
python3 scripts/work_state.py --help
```

Two rules:

- **Never hand-edit the database or legacy JSON**, and never track "did I already mention this?"
  in reasoning. Use [State operations](../skills/chief-of-staff/references/state-operations.md)
  for configuration, migration, coverage, and publication.
- **IDs must be stable identifiers** — a message ID, event ID, `owner/repo#123`, `engage:<postId>`.
  Pair identity with revision: a moved event can keep its ID and still need attention.
  **Never a summary string.** Rewording must not create a new obligation.

Suggested prefixes: `mail:` `evt:` `chat:` `gh:` `ado:` `engage:` `commit:` `person:`.

Queued items are stored **renderable**, so the anchor doesn't have to re-fetch:

```json
{
  "id": "mail:AAMkAD...",
  "kind": "mail | event | chat | github | ado | commitment | person",
  "title": "Dana — 'Q3 API review deck'",
  "source": "Email · Dana · 14:02",
  "url": "<webLink>",
  "action": "reply / delegate / read later / decide",
  "why": "one line: why it's here",
  "section": "needs-your-response | fyi | waiting-on | ambient"
}
```

`section` uses exactly those four values so the drain can group without guessing.

### Privacy note

The user-scoped `margo/state/` directory contains private account data and survives uninstall.
Legacy skill `state/` files and rolling agendas remain private and are preserved during migration.
Do not move either into a repository or shared-sync folder. File permissions are not application
encryption; protect the local account and backups accordingly.

---

## Setting it up

Verify the ledger first:

```bash
python3 ~/.copilot/skills/chief-of-staff/scripts/proactive_state.py status
```

An unconfigured account reports setup required. Follow the
[migration procedure](../skills/chief-of-staff/references/state-operations.md) before enabling
schedules; an empty new store is not proof that the old queue or commitments were imported.

Then pick how you want the schedule to run. **Both paths read the same
`automations/` files** — the difference is what's enforced.

### Via cron, launchd or Task Scheduler

```bash
./tools/margo-scheduled.sh list       # what is defined
./tools/margo-scheduled.sh brief      # run one now
./tools/margo-scheduled.sh crontab    # ready-to-install crontab lines
```

A broken or missing manifest is an **error**, not a fallback. An unknown verb stops the run
rather than being sent as the prompt — the failure that matters here is a 06:00 job whose
entire prompt turned out to be the word `brief`.

```powershell
.\tools\margo-scheduled.ps1 list
.\tools\margo-scheduled.ps1 brief
.\tools\margo-scheduled.ps1 schtasks  # register-task commands
```

`crontab` and `schtasks` emit one entry per automation at the times in the front matter, calling
the wrapper by absolute path with `PATH` set — the two things that otherwise make a
correct-looking schedule silently do nothing. Review the output before installing it. Use
`--print` (`-Print`) to see the exact command, or `--show-prompt` (`-ShowPrompt`) for just the
prompt.

**Use the wrapper rather than a hand-written command line.** It runs
`copilot --agent margo -p …` with `--allow-all-tools` *and* four `--deny-tool`
rules covering every Work IQ tool that writes:

```
workiq(do_action)  workiq(create_entity)  workiq(update_entity)  workiq(delete_entity)
```

Denial takes precedence over every allow rule, so those four tools are not
callable — sending through Work IQ is **impossible** rather than merely
discouraged, and that much of the read-only contract stops being an instruction
and becomes something the CLI enforces. They are not parameters and cannot be
switched off; extra arguments are passed through but cannot re-enable writes.

The wrapper also passes `--allow-all-tools`, so shell, `gh` and `curl` stay
available: the guarantee is precise about Work IQ writes and remains an
instruction everywhere else. That is the intended trade — see
[**what is enforced, and what is asked**](safety.md#what-is-enforced-and-what-is-asked)
— a container can help reduce exposure, but only with appropriately restricted credentials,
network and tools; [the container design](container.md) is not an enforced sandbox by itself.

That matters because the failure mode here is not malice, it is someone copying
four lines into a crontab and trimming one. Without them you are trusting an
instruction not to send mail at 06:00 while you are asleep.

### Via the Copilot app's scheduled workflows

Ask Margo to **"sync my automations"** and she registers each file as a workflow, matching on
`name` so a re-sync updates rather than duplicates.
The updated workflows explicitly select the Margo agent. Copy deployment alone does not
change saved app prompts. Review custom prompt differences and leave unrelated workflows alone.

> ⚠️ **The deny list does not apply here.** App workflows run under the app's own permissions,
> so on this path read-only is an instruction the model follows, not a wall it cannot cross. The
> unattended contract in each prompt is doing the work. If that distinction matters to you, use
> the wrapper — or [a container](container.md).

### Either way

Start with **one anchor** — the morning brief — and run it for a week before adding sweeps.
The interrupt bar needs tuning against your actual inbox, and it's much easier to loosen a quiet
system than to regain trust in a noisy one.

When you invoke a proactive routine **directly** (*"run my sweep"*, *"what's changed since
lunch"*), the queue-only rule drops: Margo reports what she finds in the moment, then still
records it as surfaced so the next anchor doesn't repeat it.

For the exact coverage, token, lease, standalone-output, and doctor workflows, use
[the automation-health guide](how-to/automation-health.md). No successful run status should
be interpreted as proof of source coverage, publication or human review.
