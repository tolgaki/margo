# Manage your calendar

**Preconditions:** complete [setup](setup-and-migration.md) and confirm the Work IQ connection.
All calendar writes below — create, update, cancel, accept, decline, tentative — happen only
after your explicit approval of that specific action.

## Give a schedulable request

Include the people, purpose, duration, date window and time zone. Mention whether an existing
meeting is one occurrence or a whole series; titles alone do not uniquely identify events.

> Find 30 minutes with Dana next week for a specification review, during my working hours.
> Keep my protected focus blocks. Show options only; do not book anything.

Review the selected slot's attendees, agenda and time zone together. **"The morning option looks
better"** is a preference while choosing, not necessarily approval of a fully specified event.
Before a real create/update, Margo shows the exact account, action and current proposal revision.
Afterward, expect the event link and observed result, not merely "approved".

## Calendar scheduling

**What this helps you do:** find a time that actually works and get a meeting on the calendar,
without a back-and-forth email chain.

**Try it:**

> Find 30 minutes with Dana this week for a follow-up.

> Set up a review with Rafa and Ines next Tuesday afternoon, 45 minutes.

**What you will see:** two or three concrete candidate slots, honoring your working hours and
protected focus blocks (and theirs, where visible), with any trade-off called out explicitly —
for example, a slot that's the only option but sits inside your heads-down block.

**What needs your decision:** the event is created only after you approve one exact proposed
slot with its attendees and content. If someone's availability isn't visible, Margo says so and
proposes times that at least work for you, naming the assumption.

**Change your mind:** ask for different times, a different duration, or to drop an attendee
before approving; nothing is booked until you say which slot.

**Your data:** proposals, payloads, revisions, approval evidence and execution receipts remain
in the private ledger after approval, dismissal or execution. The calendar service remains
authoritative for the created event; the local receipt records the observed result.

**If something goes wrong:** availability found earlier in a long conversation may be stale —
Margo re-checks the current calendar before proposing, and again immediately before creating the
event.

**Availability:** implemented, procedure. Since 1.0.0.

## Calendar reschedule

**What this helps you do:** move or cancel a specific meeting occurrence cleanly, seeing exactly
who's affected before anything changes.

**Try it:**

> Move my 2pm with Dana to tomorrow morning if she's free.

> Cancel the Thursday sync — send a short note to the attendees.

**What you will see:** the exact occurrence resolved (never guessed, and for recurring meetings,
explicitly this occurrence vs. the whole series), a proposed new time if rescheduling, and who is
notified.

**What needs your decision:** cancelling or rescheduling sends a message to attendees — treat it
with the same care as any other send, always per-action. If you don't organize the meeting, Margo
proposes the attendee-side action instead (respond, propose a new time, message the organizer) —
it will not modify or delete an event you don't own.

**Change your mind:** ask to keep the original time, change the proposed attendees, or revise a
note before approving. Whether notifications can be controlled depends on the actual provider
operation; do not assume an organizer update can be made silently. A partially completed plan
reports which steps finished and which did not rather than silently rolling back.

**Your data:** the proposed old→new time, affected people, source revisions, approvals and
execution receipts remain in the private ledger after approval, dismissal or execution.

**If something goes wrong:** if a step in a multi-meeting cascade partially fails, Margo records
what completed and what didn't; a rollback that would notify people again needs its own fresh
approval, not an automatic undo.

**Availability:** implemented, procedure. Since 1.0.0.

## Calendar RSVP

**What this helps you do:** clear a backlog of pending invitations quickly, with a reasoned
recommendation for each instead of reflexively accepting everything.

**Try it:**

> Which of my pending invites should I accept?

> Triage my meeting invitations for this week.

**What you will see:** each pending invite classified — ✅ accept, 🤔 tentative/propose a new
time, or ❌ decline — against your standing rules (optional/no-agenda/conflicts-with-focus-time)
and current conflicts, with a one-line reason for each.

**What needs your decision:** every accept/decline/tentative response needs approval, batched or
not — "accept these three, decline these two" is fine, but Margo re-confirms the exact set before
responding, never a vague "handle my invites."

**Change your mind:** move an item between buckets, or ask for a draft note to the organizer
instead of a flat decline, before you approve the batch.

**Your data:** the proposed response set, its revisions, approvals and execution receipts remain
in the private ledger after approval, dismissal or execution.

**If something goes wrong:** a partial batch reports known completed responses separately from
failed or unknown ones. Unknown outcomes require a fresh provider read, not another RSVP.
Remaining writes need their own valid proposal and approval; they are not blindly retried.

**Availability:** implemented, procedure. Since 1.0.0.

## Calendar hygiene

**What this helps you do:** see what your calendar actually costs you — hours in optional
meetings, fragmentation, agenda-less recurring series — with real numbers instead of a vague
sense that things are busy, and specific meetings worth cutting.

**Try it:**

> How's my calendar looking? What can I cut?

> How much time am I losing to meetings I don't need to be in?

**What you will see:** a quantified read of a real window (last week plus the next two, fully
paged, not just today) — optional hours, meeting load as a percentage of working hours,
90-minute-plus focus blocks, back-to-back runs, and named offenders (agenda-less meetings,
thinly-attended recurring series, your own sprawl), each with hours reclaimed per month and a
recommended action.

**What needs your decision:** declining a recurring series is highly visible to its organizer and
always needs per-action approval — there is no standing authorization that covers it, and Margo
shows exactly what the organizer would see (one instance vs. the whole series) before you decide.

**Change your mind:** ask to exclude a category (say, skip-levels) or widen/narrow the window;
nothing is declined or changed until you approve a specific meeting or series.

**Your data:** the report enters the session history. Tracked or scheduled runs also retain
private coverage, task progress and published output/receipt records. Resulting calendar-change
proposals and their history remain in the private action ledger; dismissing one is not erasure.

**If something goes wrong:** a truncated calendar pull understates the problem, so Margo pages
the full window before reporting rather than judging from a partial read; if it can't, it says the
window was incomplete rather than presenting partial numbers as final.

**Availability:** implemented, procedure. Runs on request; the daily
[ambient scan](automation-health.md#automation-ambient) can queue findings for the weekly digest.
Since 1.0.0.

## Advanced reference

All four modes share one reference file —
[Calendar Management](../../skills/chief-of-staff/references/calendar.md) — including the exact
Work IQ query examples (`calendarView` requires `startDateTime`/`endDateTime`), the
invite-text guidance (never expose scheduling mechanics to the recipient), and the hygiene
computation. There is no dedicated calendar CLI; every proposed change flows through the same
[action desk](commitments-and-action-desk.md#action-desk) as any other action.
Provider capabilities and schemas must still be discovered in the current host. For a
week-level plan rather than an individual meeting, continue to
[Outcomes and capacity](outcomes-and-meetings.md#capacity).
