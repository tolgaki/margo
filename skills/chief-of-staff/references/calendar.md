# Calendar Management

Find time, schedule and reschedule meetings, and triage pending invites. **All calendar writes —
create, update, cancel, accept, decline, tentative — happen only after explicit approval of the
specific action.** Note that declining or cancelling *sends a message* to the organizer/attendees;
treat it with the same care as any send.

## Modes

### A. Find time / schedule ("find 30 min with Dana this week", "set up a follow-up")

1. **Load preferences** — working hours, focus-time blocks to protect, time zone.
2. **Resolve attendees** with `workiq-fetch` (people lookup) so you have exact addresses; check
   `preferences.md` → People first.
3. **Check availability:**
   - The user's own calendar: `workiq-fetch` `/me/calendarView` over the candidate window with
     `$select=id,subject,start,end,showAs,isAllDay,organizer`.
   - Others' availability: use the Graph scheduling helpers (`findMeetingTimes` /
     `getSchedule`) via `workiq-do_action` / `workiq-call_function` — see the workiq skill's
     references for exact shapes. If availability for someone isn't visible, say so; propose
     times that at least work for the user and note the assumption.
4. **Propose 2–3 concrete slots**, honoring working hours and protected focus blocks (theirs
   too, when visible). Flag any trade-off (e.g. "only slot is inside your 2–4pm heads-down").
5. **On approval**, create the event via `workiq-create_entity` on `/me/events` (subject,
   attendees, Teams link if requested, agenda in the body — offer to draft the agenda via Draft
   Studio). Confirm what was created, with the event's `webLink`.

### B. Reschedule / cancel ("move my 2pm", "cancel the sync")

1. **Resolve the exact event** by `workiq-fetch` on `/me/calendarView` (or event ID) — never guess
   which instance; for recurring meetings confirm *this occurrence vs. the series*.
2. For a reschedule, find a new slot as in mode A; present old → new, who's affected, and
   whether the user is the organizer (organizer moves the event; an attendee proposes a new time
   or replies instead).
3. **On approval**, `workiq-update_entity` the event (or `workiq-do_action` to cancel). If a note
   to attendees is warranted, draft it for approval like any other message.

### C. Invite triage ("should I accept these?", pending RSVPs)

1. **Gather pending invites**: `workiq-fetch` events awaiting response (responseStatus not
   responded / meeting-request messages in the inbox).
2. **Classify each** against `preferences.md` → Standing rules ("Meetings I'll usually decline":
   optional, no agenda, conflicts with focus time) plus conflicts with existing events:
   - ✅ **Accept** — clear purpose, needed, no conflict.
   - 🤔 **Tentative / propose new time** — worth it but conflicts, or purpose unclear (offer to
     draft a question to the organizer).
   - ❌ **Decline** — matches the user's decline rules; draft a polite decline note if one is due.
3. **Present the list** with a one-line rationale per invite and the recommended response.
4. **On approval only**, respond via `workiq-do_action` (accept/decline/tentativelyAccept) —
   batch is fine, but re-confirm the batch exactly ("accept these 3, decline these 2?").

### D. Hygiene ("how's my calendar looking", "what can I cut", weekly ambient scan)

The other modes act on one meeting at a time. This one judges the calendar *as a whole* and tells
the user what it costs them. Quantify — "nine hours last week where you were optional" lands where
"you have a lot of meetings" does not.

1. **Pull a real window**, not today: last week and the next two, via `/me/calendarView`
   (mandatory `startDateTime`/`endDateTime` — it 400s without them),
   `$select=id,subject,start,end,organizer,attendees,isAllDay,isCancelled,responseStatus,recurrence,seriesMasterId,body,webLink`,
   `$top=250`. Page until the window is genuinely complete; a truncated pull understates the
   problem, and understating it is the one failure mode that makes this section pointless.

2. **Compute, don't impress.** Report hours, not adjectives:
   - **Optional hours** — meetings where the user's `responseStatus`/attendee type is optional, or
     they're one of many attendees and not the organizer.
   - **Meeting load** — total hours, and as a percentage of working hours from `preferences.md`.
   - **Fragmentation** — count of focus blocks of 90+ uninterrupted minutes. Usually the most
     damning single number.
   - **Back-to-backs** — runs of 3 or more, and any meeting with zero prep time before it.
   - **Recurring share** — hours in recurring meetings vs one-offs.

3. **Find the specific offenders**, because aggregate numbers don't get anything cancelled:
   - **Agenda-less meetings** — empty `body`, no agenda, and the user isn't the organizer. Worst
     when they're recurring.
   - **Zombie recurrences** — a recurring series where recent instances are sparsely attended,
     frequently declined, or the user has stopped contributing. **Attendance requires per-instance
     data** — expand the series over the window rather than judging from the master.
   - **Optional-and-large** — recurring, user is optional, 10+ attendees. The cheapest hours to
     reclaim and the least political.
   - **The user's own sprawl** — recurring meetings the user organizes that have outlived their
     purpose. Name these first. It's easier to hear about your own meeting than someone else's,
     and it earns the right to raise theirs.

4. **Recommend, with the cost attached.** Every line gets an action: decline the series, make
   yourself optional, halve the frequency, shorten it, or ask the organizer for an agenda.
   Attach hours reclaimed per month — that's the number that makes the decision.

5. **Propose, never act.** Declining a recurring series is highly visible to the organizer. It
   requires per-action approval, always, and there is no standing authorization that covers it.
   For a recurring series, show what the organizer sees and whether it declines one instance or
   the whole series — getting that wrong is a genuinely embarrassing mistake.

```
📉 Calendar hygiene — {window}

  {n}h in meetings ({n}% of working hours) · {n}h optional · {n} focus blocks ≥90min

  Worth cutting:
  • {title} — {recurrence}, {n} attendees, you're optional — {n}h/month
    → decline series / go optional / ask for an agenda
  • {title} — no agenda, {n} instances, you've declined {n} → drop?

  Your own:
  • {title} — you organize, {n} attendees, last 3 instances thin → still needed?
```

## Invite text: write it for the person receiving it

An invite body is a **message to the attendee**, not a record of how the meeting got scheduled.
Before writing one, sit on their side of the table: this lands uninvited in their calendar and
they will re-read the subject line every single occurrence. Draft it in the user's voice
(`../preferences.md`), same as any other outbound message.

**Never expose scheduling mechanics.** The recipient's calendar client already shows the date,
time, and recurrence. Restating it in prose adds nothing and makes the meeting feel administered:
- ❌ cadence math — "every 4 weeks", "recurring monthly, 25 min"
- ❌ rotation or alternation logic — "alternating with {name}", "rotating with the other leads",
  "on the weeks we don't meet as a group"
- ❌ **other attendees' names or their separate meetings.** Telling someone they're one slot in a
  rotation tells them they're interchangeable. This is the most common and most damaging leak.
- ❌ why this slot won — "only time that worked for everyone", "you were free here", availability
  or free/busy reasoning. It reveals you inspected their calendar and reads as machine-scheduled.
- ❌ template placeholders. **Never ship `{name}`-style tokens.** Even for near-identical invites,
  write each one separately for its recipient — a mail-merge tell undercuts the whole gesture.

**Do lead with intent and agency:**
- Open with *why this time exists* and what they get from it, not with logistics.
- Say what to bring, in plain terms, so the first occurrence isn't awkward.
- Hand them control: the time, length, or cadence can change if it doesn't suit them. A standing
  invite from someone senior can read as a summons; one sentence of warmth prevents that.
- 2–4 short lines, then the required sign-off block. Length signals bureaucracy.

**Subject lines get the same test.** Match the naming convention already visible on the user's
calendar, and keep out anything that describes the recipient's place in a system.

**Before proposing, run the perspective check:**
1. If the recipient forwarded this invite to a peer, would anything in it embarrass either party?
2. Does any line describe *scheduling* rather than *the conversation*?
3. Would they guess an assistant generated it from a template?

If any answer is bad, rewrite before showing it to the user.

## Guardrails

- Never modify or delete an event the user doesn't organize — propose the appropriate attendee
  action instead (respond, propose new time, message the organizer).
- Before proposing any slot, check it against the *current* calendar — availability found
  earlier in a long session may be stale.
- Respect focus-time blocks by default; booking over one requires calling it out explicitly.
- Cancellations and declines are outward-facing sends: show exactly what the recipient
  experiences before asking for approval.
- If the request creates follow-up obligations ("send the agenda before Friday"), offer to log
  them in `../commitments.md`.
