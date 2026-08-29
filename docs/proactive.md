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
- **Silence is success.** A run that finds nothing worth interrupting for writes its cursor and
  exits quietly. *"Nothing needs you"* is a **completed** run, not a failed one.
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
3. **A commitment due today, still open** — from `commitments.md`.
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

## Three tiers

### Tier 1 — Anchors

Scheduled, always produce output, and **the only tier allowed to spend `workiq-ask`**.

| Anchor | When | Routine |
|---|---|---|
| Morning brief | 07:15, weekdays | `daily-brief.md` (full) |
| EOD wrap-up | 17:45, weekdays | `daily-brief.md` § Catch-up |
| Week ahead | Sunday 17:00 | `daily-brief.md` § Week ahead |
| Commitment ageing | Friday 16:00 | `follow-through.md` |

An anchor **drains the queue first**, then runs the underlying routine and folds each queued item
into the section it belongs in — *Needs your response*, *FYI*, *Waiting on*. Not a separate
"here's what I saw overnight" block: those items are context for the brief, not an appendix.

> The morning brief must read as an **accumulation, not a scrape**. If it looks identical to what
> "brief me" produces on demand, the queue isn't being drained and the tiering is decorative.

Then it marks everything it rendered, sets cursors for every delta source consumed, and prunes
weekly.

### Tier 2 — Sweeps

Hourly during working hours. **Cheap, fast, and usually silent.**

**Never call `workiq-ask` in a sweep.** It costs 10–60 seconds per call and this runs ~40 times a
week. Sweeps use `workiq-call_function` (delta) and `workiq-fetch` only.

> A sweep that takes a minute and prints nothing is a bug.

Highest-value delta source is `/me/calendarView/delta` — cancellations and moves are the things
you most want to know about within the hour.

First run has a trap: if there's no cursor yet, **don't sweep from the epoch**. Use the last hour
and set the cursor.

### Tier 3 — Ambient

Low-urgency daily scans that surface *weekly* — relationship drift, calendar hygiene, the
document queue, stale PRs. Findings are promoted into an anchor rather than interrupting.

---

## State lives on disk

Every scheduled run is **a fresh session with no memory**. Continuity comes from
`scripts/proactive_state.py`, which is the ledger for what's already been surfaced, what's queued
for the next anchor, and the delta cursors.

```bash
python3 scripts/proactive_state.py seen "<id>"      # exit 0 = already told them, 1 = new
python3 scripts/proactive_state.py mark "<id>" --tier sweep
python3 scripts/proactive_state.py queue-add --json '{"id":"…","title":"…","action":"…"}'
python3 scripts/proactive_state.py queue-drain      # prints a batch id, holds items in flight
python3 scripts/proactive_state.py queue-ack --batch <id>   # retire AFTER the brief rendered
python3 scripts/proactive_state.py cursor-get mail  # exit 1 = no cursor yet
python3 scripts/proactive_state.py cursor-set mail "2026-08-22T18:00:00Z"
python3 scripts/proactive_state.py prune --days 30
python3 scripts/proactive_state.py status
```

Two rules:

- **Never hand-edit the JSON**, and never track "did I already mention this?" in reasoning.
- **IDs must be stable identifiers** — a message ID, event ID, `owner/repo#123`, `engage:<postId>`.
  **Never a summary string.** The wording changes between runs, dedupe silently fails, and the
  result looks exactly like an assistant nagging you.

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

Everything under `state/` contains **real subjects, senders and links from your mailbox** — plus
relationship notes and 1:1 agendas, which are Markdown rather than JSON. The whole subtree is
gitignored with no exceptions, and both installers archive all of it rather than deleting it. Don't move it somewhere synced without thinking about who else has access.

---

## Setting it up

Verify the ledger first:

```bash
python3 ~/.copilot/skills/chief-of-staff/scripts/proactive_state.py status
```

```
state dir : …/skills/chief-of-staff/state
surfaced  : 0
queued    : 0
cursors   : (none)
```

Then schedule it with the bundled wrapper:

```bash
./tools/margo-scheduled.sh brief        # or: eod · week · commitments · sweep
```

```powershell
.\tools\margo-scheduled.ps1 brief
```

Put that in `cron`, `launchd` or Task Scheduler at 07:15 on weekdays. Use
`--print` (`-Print`) to see the exact command without running it.

**Use the wrapper rather than a hand-written command line.** It runs
`copilot --agent margo -p …` with `--allow-all-tools` *and* four `--deny-tool`
rules covering every Work IQ tool that writes:

```
workiq(do_action)  workiq(create_entity)  workiq(update_entity)  workiq(delete_entity)
```

Denial takes precedence over every allow rule, so those four make sending
**impossible** rather than merely discouraged — the read-only contract above
stops being an instruction and becomes something the CLI enforces. They are not
parameters and cannot be switched off; extra arguments are passed through but
cannot re-enable writes.

That matters because the failure mode here is not malice, it is someone copying
four lines into a crontab and trimming one. Without them you are trusting an
instruction not to send mail at 07:15 while you are asleep.

Start with **one anchor** — the morning brief — and run it for a week before adding sweeps.
The interrupt bar needs tuning against your actual inbox, and it's much easier to loosen a quiet
system than to regain trust in a noisy one.

When you invoke a proactive routine **directly** (*"run my sweep"*, *"what's changed since
lunch"*), the queue-only rule drops: Margo reports what she finds in the moment, then still
records it as surfaced so the next anchor doesn't repeat it.
