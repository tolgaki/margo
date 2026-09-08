# Plan outcomes and carry meeting work forward

**Preconditions:** use the [shared shell setup](README.md#before-running-a-cli-recipe), an
explicitly selected account, and current sources. The capacity calculator never fetches or changes
a calendar.

## Run the journey in conversation

| When | Ask | Check before moving on |
| --- | --- | --- |
| Planning the week | "Propose up to three outcomes, and check whether the work fits." | Missing effort, calendar coverage and mandatory work are visible |
| Before a meeting | "Prep my next review with Ines, including the carried agenda and decision needed." | The correct occurrence, current documents and source gaps are identified |
| After the meeting | "Debrief that occurrence; separate decisions, my actions and unresolved questions." | A missing recap is not reported as no actions |
| Reviewing follow-through | "Show the candidate obligations and carry only unresolved topics into the next occurrence." | Confirmation, carry-forward and any shared decision-log write stay separate |

The conversation is sufficient; the numbered sections below show the underlying CLI records.
For a prep-only request, start at [section 4](#4-keep-one-record-per-actual-meeting-occurrence);
you do not need weekly outcomes before preparing a meeting. For a delayed recap, go to
[section 5](#5-check-recaps-with-a-finite-budget).

## 1. Ask for a plan that exposes the trade-offs

> Help me agree up to three outcomes for next week. Show owners, definitions of done, due dates,
> effort, and allocated time or blockers. Include mandatory work outside those outcomes.

Margo should ask for missing inputs, not turn broad priority labels into promises. A longer list
must be discussed rather than silently truncated. To check whether the proposal is realistic:

> Use my confirmed working hours and a fully paged calendar window. Count overlaps once. Keep
> protected focus time usable, show conflicts, and label missing calendar or effort information.

An outcome approval is not permission to move meetings, delegate, or send messages.

## 2. Calculate capacity from explicit intervals

**Preconditions:** normalize working time, meeting occurrences, known leave, buffers, and reserves
into offset-bearing timestamps. Expand recurrence first. Resolve each day's confirmed time-zone
offset, including DST changes. Calendar presence is not proof of attendance. Exclude cancelled,
declined, or free events only when their source fields establish that status.

Save this fictional one-day example as `capacity.json`:

```json
{
  "coverage": "complete",
  "working": [
    {"start": "2026-09-07T09:00:00-07:00", "end": "2026-09-07T17:00:00-07:00"}
  ],
  "busy": [
    {"start": "2026-09-07T10:00:00-07:00", "end": "2026-09-07T11:00:00-07:00"},
    {"start": "2026-09-07T10:30:00-07:00", "end": "2026-09-07T12:00:00-07:00"},
    {"start": "2026-09-07T16:00:00-07:00", "end": "2026-09-07T18:00:00-07:00"}
  ],
  "protected": [
    {"start": "2026-09-07T11:00:00-07:00", "end": "2026-09-07T13:00:00-07:00"}
  ],
  "minimum_focus_minutes": 90,
  "estimates": [{"id": "specification-review", "minutes": 180}]
}
```

```sh
python3 "$COPILOT_HOME/skills/chief-of-staff/scripts/capacity.py" \
  --input capacity.json
```

**Expected:** 480 working minutes, 180 busy, and 300 available. The overlapping morning meetings
count once; the last busy interval is clipped at the end of the working day. Protected time has
60 available minutes and 60 conflicting minutes. It is part of available capacity, not extra
capacity and not another subtraction. Only the four-hour 12:00-16:00 gap meets the 90-minute
focus threshold; output timestamps are normalized to UTC.

`fits_total_capacity:true` means 180 minutes fits the total 300. It does not allocate work:
`allocation_status` remains `not_allocated`. Check deadlines, dependencies, and block sizes.
Do not remove an optional meeting merely to make the numbers fit.

**Try incomplete input:** set `coverage` to `partial` or an estimate's `minutes` to `null`.
`fits_total_capacity` becomes `null`, with warnings. Computed gaps are not established
availability. Known estimates still contribute to `minimum_overflow_minutes`, which is only a
lower-bound shortfall.

The calculator accepts scalar `minutes`, not outcome effort-range objects. For a range, run
separate low/high scenarios or use an explicitly chosen planning estimate and label it. Do not
quietly average or reduce effort. Propose what to stop, defer, or delegate when work will not fit.
Actual calendar changes must go through the [action desk](commitments-and-action-desk.md).

## 3. Save a proposed outcome, then agree it

Save `outcome.json`:

```json
{
  "state": "proposed",
  "data": {
    "week": "2026-09-07",
    "title": "Complete the example specification review",
    "owner": null,
    "definition_of_done": "Review comments are consolidated with unresolved questions listed.",
    "due": "2026-09-11",
    "effort": {"min_minutes": 120, "max_minutes": 180},
    "next_step": "Compare the two versions",
    "allocation": null,
    "blocker": null,
    "work_item_ids": [],
    "source_refs": []
  }
}
```

```sh
work record-id outcome week-2026-09-07-specification
work record outcome week-2026-09-07-specification --input outcome.json
work show OUTCOME_ID --json
```

**Expected:** one `proposed` record. Reuse the same identity for updates; changing its title is not
a reason to create another ID. `week` must be an ISO Monday. Required fields remain present even
when unknown.

After discussing owner, definition of done, due date, effort, next step, and allocation or a
named blocker, obtain explicit agreement. Prepare `agreed-outcome.json` with:

- The **complete** updated `data`, including real supporting `work_item_ids` and source refs.
- `state:"agreed"`.
- Actual human `evidence` for this record and current revision, using `decision:"confirm"`.

```sh
work record outcome week-2026-09-07-specification \
  --revision REVISION --input agreed-outcome.json
```

Agreement requires a nonempty owner, definition of done, due, next step, positive effort in
minutes or a valid range, and an allocation or named blocker. At most three outcomes may be
currently `agreed`, `active`, or `achieved` for the week; achieved outcomes still count.

Use the same conditional `record` command and complete input for `active` or `achieved`, with
real `confirm` evidence. Deferral/cancellation uses `defer`/`cancel` evidence once agreement has
been established. Show what actually finished before recording achievement. To plan a later
week, create a new proposed outcome and link the same supporting work IDs where appropriate;
do not rewrite an achieved outcome as next week's promise.

**Recovery:** refresh on a revision conflict. `record` replaces all data rather than patching it.
An agreed outcome without time allocated or a named blocker is rejected, not silently repaired.
See [outcome policy](../../skills/chief-of-staff/references/outcomes.md).

## 4. Keep one record per actual meeting occurrence

> Prepare my next meeting with Ines from its current occurrence and sources. Carry open topics,
> link existing obligations, and leave attendance unknown unless there is evidence.

**Preconditions:** resolve actual series and occurrence IDs and store the current occurrence
source with `work source`. A meeting title or evidence-only search link is not its canonical ID.
Use the returned source reference in `meeting.json`:

```json
{
  "state": "scheduled",
  "data": {
    "series_id": "ACTUAL_SERIES_ID",
    "occurrence_id": "ACTUAL_OCCURRENCE_ID",
    "scheduled_start": "2026-09-08T10:00:00-07:00",
    "scheduled_end": "2026-09-08T10:30:00-07:00",
    "attendance": "unknown",
    "source_refs": [
      {
        "source_id": "RETURNED_SOURCE_ID",
        "revision": "RETURNED_REVISION",
        "fingerprint": "RETURNED_FINGERPRINT"
      }
    ],
    "work_item_ids": [],
    "decision_refs": [],
    "topics": [
      {
        "id": "specification-question",
        "title": "Which version should be reviewed?",
        "state": "open",
        "owner": null,
        "created_at": "2026-09-07T09:00:00-07:00",
        "why": "The source version needs clarification."
      }
    ]
  }
}
```

Replace all provider fields and times with observations for the real meeting.

```sh
work record meeting ACTUAL_OCCURRENCE_ID --input meeting.json
work show MEETING_ID --json
```

The CLI identity must equal `data.occurrence_id`. Both occurrence and series identity are immutable.
Use the returned meeting record ID, not the provider occurrence ID, for `show` and `recap-check`.

For agenda accumulation or preparation, use
`record meeting ACTUAL_OCCURRENCE_ID --revision REVISION`
with `--input` naming a complete updated envelope and state `agenda_accumulating` or `prepped`.
Store a substantive agenda as a [private work product](feedback-and-work-products.md).
Known `present`/`absent` attendance requires `attendance_evidence`.

## 5. Check recaps with a finite budget

After the occurrence, save a complete meeting envelope as `recap-pending.json` with
`state:"recap_pending"` and a `data.recap_retry` object:

```json
{
  "attempts": 0,
  "max_attempts": 3,
  "status": "pending",
  "next_check_at": "2026-09-08T11:00:00-07:00"
}
```

Choose the actual next eligible sweep/anchor time, not the example's date.

```sh
work record meeting ACTUAL_OCCURRENCE_ID \
  --revision REVISION --input recap-pending.json
```

At the eligible time, check Work IQ for the recap. Save the **actual check result** as
`recap-result.json`: `{"result":"pending","evidence_ref":"ACTUAL_CHECK_REFERENCE",
"next_check_at":"ACTUAL_FUTURE_OFFSET_TIMESTAMP"}`. Allowed results are `pending`, `available`,
`blocked`, and `exhausted`.

```sh
work recap-check MEETING_ID --revision REVISION --input recap-result.json
```

**Expected:** one recorded check and an incremented attempt count, not a recap fetch. A pending
result needs a future retry time unless the budget has just exhausted. Stopped retries
(`available`, `blocked`, `exhausted`) cannot be checked again without explicit record review of
new evidence. Policy denial is blocked, not something to poll around.

Use existing schedules, not a new polling daemon. Missing recaps mean missing evidence, not no
decisions. User-supplied notes may be a separate attributed source. After obtaining evidence,
record `debrief_proposed`; show candidate obligations/resolutions for human review, then record
`reviewed` with real `confirm` evidence. Updating the meeting does not confirm or close its work
items. Decision references use actual `{canonical_id,web_link,status}` records, with status
`current`, `superseded`, or `unknown`; the decision log remains authoritative.

## 6. Carry only unresolved work into the next occurrence

**Preconditions:** the source meeting is `reviewed`. The target is a later occurrence in the same
series, already recorded as `scheduled`, `agenda_accumulating`, or `prepped`.

Show both records. After the human approves the carry-forward, prepare `carry.json` containing
`target_id` (the target **meeting record ID**), `target_revision` (current integer), and actual
human `evidence` for the source meeting/revision with `decision:"carry_forward"`.

```sh
work carry-forward MEETING_ID --revision REVISION --input carry.json
```

**Expected:** one atomic update returns `source` and `target`. The source becomes
`carried_forward`; the target becomes `agenda_accumulating`. Open topics retain their ID, owner,
creation time, and reason. Existing work IDs are reused; resolved/rejected/cancelled work is not
newly carried, and no commitment is re-ingested.

**Recovery:** mismatched series, stale revisions, or conflicting same-ID topics fail. Review the
conflict rather than inventing a merged topic. Moving/cancelling one occurrence never applies a
series-wide calendar change.
See [meeting lifecycle](../../skills/chief-of-staff/references/meeting-lifecycle.md).

For recurring 1:1 discussion files, continue to
[Relationships and one-on-ones](relationships-and-one-on-ones.md). For a durable team decision,
use the separately approved [decision-log](decision-log.md) workflow rather than treating the
meeting's decision reference as another authoritative log.

## Feature reference

The numbered sections above are the full CLI walkthrough. These are the stable per-feature entry
points the [feature catalog](../feature-catalog.json) links to.

### Week ahead

See next week's shape — load, prep debt, focus time, conflicts, what's due — while there's still
time to act on it; see [§1](#1-ask-for-a-plan-that-exposes-the-trade-offs) for the underlying
agreement flow. Try it: *"Prepare me for next week."* What you'll see: meeting load and heaviest
day, prep debt, what survives of your protected focus time, conflicts, and what's due — with
missing deadlines or calendar coverage stated as explicit gaps, never smoothed over. What needs
your decision: any proposed fix (draft an agenda, decline a conflict, protect a block) is its own
approval, in [calendar management](calendar-management.md) or
[drafting](drafting-and-follow-ups.md). Change your mind: ask for a narrower or wider window any
time. Your data: private preparation can retain coverage, task progress, candidate records and
published outputs/receipts without confirming an outcome or approving an outward action. Session
history and any supplied input files also remain under their own retention rules.
If something goes wrong: an incomplete calendar pull is reported as
incomplete, not folded into a false feasible-looking week. Implemented, procedure. Runs on
request, and automatically via the
[week-ahead schedule](automation-health.md#automation-week-ahead). Since 1.0.0.

### Meeting prep

Walk into a meeting ready — purpose, people, context, and talking points, including accumulated
agenda topics — see [§4](#4-keep-one-record-per-actual-meeting-occurrence). Try it: *"Prep me for
my 2pm with Ines."* What you'll see: a prep card citing sources, with a missing attachment named
explicitly rather than silently dropped. What needs your decision: any offered follow-on
(pre-read, agenda, message) is a separate draft. Change your mind: ask for a different meeting or
a different depth any time. Your data: a substantive agenda is stored as a private, versioned work
product (see [feedback and work products](feedback-and-work-products.md#work-products)). If
something goes wrong: an empty calendar search means unknown, not "no meeting" — Margo tries a
known event ID or a wider window before giving up. Implemented, procedure. Since 1.0.0.

### Meeting debrief

Turn a recap or transcript into your actions, what you're waiting on, and unresolved gaps nobody
owns — see [§5 Check recaps with a finite budget](#5-check-recaps-with-a-finite-budget). Try it:
*"Debrief that meeting — what did I commit to?"* What you'll see: decisions, your actions, what
you're waiting on, and an explicit "unresolved and nobody owns it" section — a debrief with zero
actions for you is a legitimate, plainly stated result. What needs your decision: proposed
work-ledger additions/resolutions and any drafted follow-up are separate approvals. Change your
mind: ask for a different lookback window or to re-check for a delayed recap. Your data: proposed
changes go into the same private work ledger as [commitments](commitments-and-action-desk.md#commitments).
If something goes wrong: no recap available is a valid, explicitly stated result — never an
invented set of decisions. Implemented, procedure. Since 1.0.0.

### Meeting lifecycle

Keep one durable record per meeting occurrence across preparation, delayed-recap retries, debrief
and carry-forward — see [§4](#4-keep-one-record-per-actual-meeting-occurrence),
[§5](#5-check-recaps-with-a-finite-budget) and
[§6 Carry only unresolved work into the next occurrence](#6-carry-only-unresolved-work-into-the-next-occurrence).
Try it: *"Carry this into the next occurrence and watch for the recap."* What you'll see: the
occurrence tracked through `scheduled → recap_pending → debrief_proposed → reviewed →
carried_forward`, retrying on the existing sweep/anchor cadence rather than a new poller. What
needs your decision: promoting a candidate or closing an obligation always needs your
confirmation. Change your mind: nothing here is undone by asking again — a carried-forward topic
can be resolved or dropped in review the same way any other topic can. Your data: series/occurrence
identity, recap-retry state, and carried topics live in the private work ledger. If something
goes wrong: a policy denial on a recap check is blocked, not silently retried through another
route. Implemented, runtime (deterministic occurrence/recap/carry-forward state machine with
tests). Since 1.1.0.

### Outcomes

Agree up to three weekly outcomes with an owner, definition of done, due date, and effort — see
[§3 Save a proposed outcome, then agree it](#3-save-a-proposed-outcome-then-agree-it). Try it:
*"Help me agree three outcomes for this week."* What you'll see: a proposed outcome record with
required fields present even when unknown, moved to `agreed` only after you supply what's missing
and confirm. What needs your decision: agreeing an outcome never approves a calendar change or a
message — those stay separate. Change your mind: defer or cancel an agreed outcome with the same
review process used to agree it. Your data: outcome records live in the private work ledger,
capped at three concurrently agreed/active/achieved. If something goes wrong: a revision conflict
means something changed underneath you — re-read before deciding again. Implemented, runtime
(deterministic outcome record state machine with tests). Since 1.1.0.

### Capacity

Check whether a plan actually fits the week, from real working hours, meetings, leave, and
protected blocks — see [§2 Calculate capacity from explicit intervals](#2-calculate-capacity-from-explicit-intervals).
Try it: ask Margo to check whether a plan fits, given your actual calendar. What you'll see: total
working/busy/available minutes, focus-block gaps meeting your minimum, and an honest `null`
(unknown) fit when coverage is partial or an estimate is missing — never a false "fits." What
needs your decision: nothing — capacity is a pure calculation; any resulting calendar change is a
separate approval in [calendar management](calendar-management.md). Change your mind: recompute
with different inputs any time; the calculation itself does not save a decision. Your data: the
calculator is stateless, but private input files remain until you remove them. Results can enter
session history, task results or published outputs/receipts when used in a tracked routine.
If something goes wrong: an incomplete input reports unknown fit and a lower-bound
shortfall, never a fabricated allocation. Implemented, runtime (deterministic interval-union
calculator with tests). Since 1.1.0.
