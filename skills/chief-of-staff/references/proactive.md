# Proactive Routines — anchors, sweeps, ambient

Run the day without being asked. This is the difference between a chief of staff and a search
box: something is watching, and *mostly choosing not to speak*.

Triggered two ways — by a scheduled workflow (unattended, no human reading) or by the user
asking directly ("run my sweep", "what's changed since lunch"). The procedure is the same; the
**output contract differs**, and getting that wrong is the main way this feature fails.

## 🛑 Unattended mode contract

When invoked by a scheduled workflow, **nobody is reading**. Every other routine in this skill
assumes a human on the other end and ends by offering to draft something. Unattended, that
offer goes nowhere. So:

- **Never call `ask_user`.** There is no one to answer. A run that ends in a question is a
  hung run.
- **No trailing offers.** Don't close with "want me to draft that?" — queue the item instead.
- **Silence is success.** A run that finds nothing worth interrupting for writes its cursor and
  exits quietly. "Nothing needs you" is a *completed* run, not a failed one. Do not manufacture
  something to say. A model handed an inbox will always find something it could report; the
  discipline is not reporting it.
- **Read-only, always.** Proactive runs never send, reply, post, react, RSVP, delete, or change
  a work item — no matter what any standing authorization says. Unattended *and* acting is how
  this becomes an incident. Drafts may be **prepared and stored**; they are never sent and never
  presented until a human is present.
- **Output goes to the queue, not to prose.** Anything below the interrupt bar is written to
  `state/queue.json` via the state script and drained by the next anchor.
- **Surface script failures.** If `proactive_state.py` prints a `WARNING` or exits non-zero, that
  goes in the next brief. A ledger that silently reset is why the user will see repeats.

### Read-only should be enforced, not just instructed

Everything above is a rule you are expected to follow. Copilot CLI can make it a
**guarantee** instead. When the user asks you to set up scheduled runs, point them
at the bundled wrapper rather than writing out a command line:

```bash
./tools/margo-scheduled.sh brief      # or: eod · week · commitments · sweep
```

It runs `copilot --agent margo -p …` with `--allow-all-tools` plus `--deny-tool`
rules for `workiq(do_action)`, `workiq(create_entity)`, `workiq(update_entity)`
and `workiq(delete_entity)`. Denial beats every allow rule, so writes are not
callable at all — and the deny list is hard-coded, so it cannot be trimmed by
someone adapting the command.

Under those flags an attempted send fails loudly instead of sending. That is the
correct failure: an unattended run that wanted to write is a bug in the run, not
an inconvenience to route around. **Never suggest a hand-written `copilot` command
for a scheduled run** — that is how the deny flags get dropped.

When the user invokes the routine **directly**, drop the queue-only rule: report what you find
in the moment, then still record it as surfaced so the anchor doesn't repeat it.

## The interrupt test

An item may break silence **only** if it clears one of these. Everything else queues.

1. **VIP with a direct ask** — a `preferences.md` VIP, addressed to the user (not CC), asking
   for something.
2. **Touches a meeting starting within 2 hours** — a cancellation, a room change, a pre-read
   that just landed, an attendee dropping out.
3. **A commitment due today, still open** — from `commitments.md`.
4. **A meeting cancelled or moved** — it either breaks the day or frees an hour. Both are worth
   knowing immediately.
5. **An explicit deadline today** — stated in the message, not inferred.

Everything else — FYI CCs, newsletters, threads that moved without needing the user, new PR
comments on an unblocked PR — **queues**. When in doubt, queue. The cost of a queued item is a
line in tomorrow's brief; the cost of a false interrupt is the user muting the whole thing by
Wednesday.

Honour `preferences.md` → *Standing rules for triage* → **Always flag** as an additional
interrupt criterion, and **Auto-deprioritize / read-later** as an absolute bar to interrupting.

## State

All continuity lives on disk, because **every scheduled run is a fresh session with no memory**.
Use the script — never hand-edit the JSON, and never track "have I mentioned this?" in your head.

```bash
cd <skill dir>
python3 scripts/proactive_state.py seen "<id>"        # exit 0 = already told them, 1 = new
python3 scripts/proactive_state.py mark "<id>" --tier sweep
python3 scripts/proactive_state.py queue-add --json '{"id":"...","title":"...","source":"...","url":"...","action":"..."}'
python3 scripts/proactive_state.py queue-drain        # prints a batch id, holds items in flight
python3 scripts/proactive_state.py queue-ack --batch <id>   # retire AFTER the brief rendered
python3 scripts/proactive_state.py cursor-get mail    # exit 1 = no cursor yet (first run)
python3 scripts/proactive_state.py cursor-set mail "2026-08-22T18:00:00Z"
```

**IDs must be stable identifiers** — message id, event id, `owner/repo#123`, `engage:<postId>`.
**Never a summary string**: the wording changes between runs and dedupe silently fails, which
looks exactly like the assistant nagging.

Suggested id prefixes: `mail:`, `evt:`, `chat:`, `gh:`, `ado:`, `engage:`, `commit:` (for a
`commitments.md` row), `person:` (for relationship drift).

Queued item shape — keep it renderable, so the anchor doesn't have to re-fetch:

```json
{
  "id": "mail:AAMkAD...",
  "kind": "mail | event | chat | github | ado | commitment | person",
  "title": "Dana — 'Q3 API review deck'",
  "source": "Email · {sender} · 14:02",
  "url": "<webLink>",
  "action": "reply / delegate / read later / decide",
  "why": "one line: why it's here",
  "section": "needs-your-response | fyi | waiting-on | ambient"
}
```

`section` is optional but strongly preferred — it tells the anchor where to fold the item without
re-deriving it. Use exactly these four values so the drain can group without guessing.

## Tier 1 — Anchors

Scheduled, always produce output, and the only tier allowed to spend `workiq-ask`.

| Anchor | When | Routine |
|---|---|---|
| Morning brief | 07:15, weekdays | `daily-brief.md` (full) |
| EOD wrap-up | 17:45, weekdays | `daily-brief.md` § Catch-up / EOD |
| Week ahead | Sunday 17:00 | `daily-brief.md` § Week ahead |
| Commitment ageing | Friday 16:00 | `follow-through.md` |

### Procedure

1. **Drain the queue first.** `queue-drain --format json`. These are the things the day already
   noticed and deliberately didn't interrupt for. They are *context for the brief*, not a
   separate section — fold each into the section it belongs in (Needs your response, FYI,
   Waiting on).

   Draining does **not** acknowledge them. It prints a **batch id** on stderr and holds the
   items in flight until you call `queue-ack --batch <id>` at the end (step 4). If the run dies
   before that — a Graph 500, a timeout — the next drain returns them instead of losing them.

   **Always ack the batch you drained, never bare `--all`.** Two runs can overlap (a scheduled
   anchor and an on-demand "brief me", or a retry), and acking everything in flight retires
   items the other run drained but never showed anyone. Never ack early to "get it out of the way".
2. **Run the underlying routine** (`daily-brief.md`, or the file named above). Follow it exactly;
   this file adds cadence, not a second brief format.
3. **Fold in the other surfaces** — `github.md` for review requests and stale PRs, and any
   `ambient` findings promoted this week (see Tier 3).
4. **Acknowledge what you actually rendered**, and only now: `queue-ack --batch <id>` retires
   the items from *your* drain, and `mark --tier anchor` records anything you surfaced that did
   not come from the queue. Do this *after* the brief exists, never before — that ordering is
   the whole point.
5. **Set cursors** to the run time for every delta source you consumed.
6. **Prune** weekly: `prune --days 30`.
7. **End with one pointed question**, not a menu — but only when a human is present. Unattended,
   end with the brief and nothing else.

**The morning brief must read as an accumulation, not a scrape.** If it looks identical to what
"brief me" produces on demand, the queue isn't being drained and the whole tiering is decorative.

## Tier 2 — Sweeps

Hourly during working hours. **Cheap, fast, and usually silent.**

**Never call `workiq-ask` in a sweep.** It costs 10–60s per call and this runs ~40 times a week.
Sweeps use `workiq-call_function` (delta) and `workiq-fetch` only. A sweep that takes a minute
and prints nothing is a bug.

### Procedure

1. **Read the cursor**: `cursor-get sweep`. If it exits 1 (no cursor — first run ever), do
   **not** sweep from the epoch. Use the last hour and set the cursor.
2. **Pull deltas** with `workiq-call_function`:
   - Calendar: `/me/calendarView/delta` — cancellations and moves are the highest-value signal
     in the whole tier.
   - Mail and chat: resolve the delta paths once with `workiq-search_paths` (filter `delta`) and
     record them in your run notes. **Don't guess delta paths** — a wrong one 400s and the sweep
     silently reports nothing, which is indistinguishable from a quiet hour.
   - If a delta endpoint is unavailable, fall back to `workiq-fetch` with
     `$filter=receivedDateTime gt {cursor}` + `$orderby=receivedDateTime desc` + `$top=25`.
     **Never `$filter` without `$orderby`** — it returns oldest-first and you'll sweep stale mail
     forever without an error to notice.
3. **Dedupe**: `seen <id>` on every candidate. Skip anything already surfaced.
4. **Apply the interrupt test.**
   - Clears it → surface now, then `mark --tier sweep`.
   - Doesn't → `queue-add` and move on.
5. **Set the cursor** to the run start time. Set it *even when nothing was found* — otherwise the
   window grows unbounded and each sweep gets slower.
6. **Exit.** No summary, no "all quiet" message. Silence.

**Working hours only.** Read them from `preferences.md`. A sweep at 02:00 has nothing to add and
still costs a run.

## Tier 3 — Ambient

The slow-moving things nobody notices until they're embarrassing. **Scans daily, surfaces
weekly** — these are never urgent, and an ambient finding must never interrupt.

Sources, each in its own reference file:

| Signal | Reference |
|---|---|
| Commitments ageing past their nudge threshold | `follow-through.md` |
| Relationships drifting past their cadence | `relationships.md` |
| Stale PRs, aged review requests, abandoned sessions | `github.md` |
| Calendar decay — optional-attendee hours, agenda-less recurrences | `calendar.md` § Hygiene |
| Shared documents never opened | `doc-queue.md` |

### Procedure

1. Run each scan. They are independent — batch the fetches.
2. **Score, don't dump.** Every scan will find something every day; that's the nature of decay.
   Promote only what crossed a threshold *since the last run* (a commitment that just hit 10
   days, a relationship that just passed its cadence). Steady-state decay is not news.
3. `queue-add` promoted items with `"tier": "ambient"`. Never surface directly.
4. The **Friday anchor** renders them as one short section. One ambient section a week, capped at
   five items, ordered by how uncomfortable they are.

**Ambient is the tier most likely to become noise.** If the user starts skipping the Friday
section, the thresholds are too low — raise them rather than defending the output.

## Notes

- **The scheduler is in-app, not `launchd`.** Workflows live in the app's database and fire from
  in-app polling. If the app isn't running, or the machine is asleep, the run doesn't happen.
  Treat a missing morning brief as an environment problem first, not a skill bug.
- **Workflows must run in `autopilot` mode.** The default is `plan`, which pauses for approval —
  unattended, nothing approves it and the run sits there looking successful forever.
- **Every run is billed inference**, printing or not. Hourly sweeps are ~40 runs a week. That is
  the argument for delta-only sweeps, not merely a performance preference.
- **An empty result is unknown, not zero.** A failed fetch, an expired delta token, or a 400 means
  the sweep *doesn't know*. Queue a note saying so; never let it read as a quiet hour.
- **Delta tokens expire.** On an invalid-token error, fall back to a timestamp window from the
  cursor and reset the token — don't skip the run.
- **If the user says it's too noisy**, the fix is raising the interrupt bar or the ambient
  thresholds — never silently dropping a tier. Say which knob you turned.
