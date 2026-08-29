# Daily Brief, Catch-up & Week Ahead

Produce a decision-ready snapshot of the user's day. Goal: in 60 seconds of reading they know
what matters, what's coming, and what needs them.

## Procedure

1. **Load preferences** (`../preferences.md`) — working hours, VIPs, projects, brief depth —
   and **`../commitments.md`** — open commitments and waiting-on items to carry into the brief.

   **Then drain the queue**, before fetching anything:

   ```bash
   python3 scripts/proactive_state.py queue-drain --format json   # prints 'batch <id>' on stderr
   # ...render the brief, THEN, with the id it printed:
   python3 scripts/proactive_state.py queue-ack --batch <id>
   ```

   These are items the day's sweeps already noticed and deliberately didn't interrupt for. Fold
   each into the section its `section` field names — they are context for the brief, not a
   separate block. **A brief that ignores the queue is just an on-demand scrape**, and the whole
   proactive tiering becomes decorative. See `../references/proactive.md`.

2. **Enumerate the skeleton (use `workiq-fetch`, in parallel, always with `$select` + `$top`):**
   - **Calendar:** today's `/me/calendarView` between start/end of day
     (`$select=id,subject,start,end,organizer,attendees,isAllDay,onlineMeeting,location,webLink`).
     Note back-to-backs and missing prep time.
   - **Mail needing attention:** unread `/me/messages` with
     `$filter=isRead eq false` + `$orderby=receivedDateTime desc` (index-backed and safe),
     `$select=id,subject,from,receivedDateTime,isRead,flag,toRecipients,bodyPreview,webLink`,
     `$top=50`. Convert "this week" to an explicit ISO date before it reaches the filter.
     **Do not `$filter` on `flag/flagStatus`** — it returns `400 InefficientFilter`; pick the
     flagged items out locally from the `flag` field. Prioritize items where the user is the
     sole/primary recipient (higher signal than CC).
   - **Teams:** there is no reliable `workiq-fetch` path that enumerates *unread* Teams messages —
     don't burn calls looking for one. Cover Teams via `workiq-ask` in step 3 ("what needs my
     attention in Teams — DMs, @mentions, active chats?") or via chat-message delta
     (`workiq-call_function`) for a precise since-yesterday sweep.
   - **Commitments/tasks:** Planner tasks due soon (`/planner/...`), and follow-ups.
   - **GitHub:** review requests, stale PRs, and assigned issues — see `github.md`. These are
     commitments with the same standing as email; a review request sitting three days is exactly
     as overdue as an unanswered ask, and belongs in the same sections of the brief.

3. **Synthesize with `workiq-ask` (1–3 focused questions, not one giant prompt; pass `timeZone`):**
   - "What's changed or is new across my inbox, meetings, and Teams since yesterday evening?"
   - "Across my unread mail and Teams messages, what actually needs a response from me today,
     and what's the ask in each?"
   - "For today's meetings {list titles}, what's the purpose and what do I need to prepare?"
   Use delta endpoints (`workiq-call_function`) for precise "since yesterday" changes.

4. **Prioritize.** Rank by: VIP sender → hard deadline today → blocks others → external/customer
   → quick win. Cap top priorities at 5. Everything else goes to FYI or read-later.

5. **Render** in the Standard Daily Brief format (see SKILL.md). Trim empty sections. Every
   actionable line ends in a recommended action (reply / delegate / decline / schedule / read).
   Render "Waiting on / open commitments" from `../commitments.md` (plus anything new found
   today); flag items past their due date or stale enough to nudge.

6. **Offer next steps:** prep a specific meeting, open Draft Studio for the top replies, or
   deep-dive an item. Propose — never send.

## Depth control
- **Quick brief:** top priorities + calendar + needs-response only.
- **Full brief:** all sections including FYI/changed and open commitments.
Default to the user's `preferences.md`; if unset, give the full brief once and ask which they prefer.

## Recurring brief
The host **does** have a scheduler: workflows stored in the app, which fire a fresh agent session
running a one-line prompt. Setup, cadence, and the unattended output contract are in
**`../references/proactive.md`** — read it before creating or changing any scheduled run.

When the user sets a time in `preferences.md` → Daily brief, propose the workflow (with approval),
and remember the two things that silently break it: the workflow must run in **autopilot** mode
(the default `plan` mode waits forever for an approval nobody gives), and the app must be running
and the machine awake at the scheduled time.

## § Catch-up ("what did I miss")
Same data pull, scoped to a window (since last login / yesterday / while I was out N days). Lead
with: decisions made, things that now need the user, threads that moved, and anything that went
stale waiting on them. Use delta endpoints for accuracy. End with a short "here's what I'd tackle
first" recommendation.

## § End-of-day wrap-up
- What got resolved vs. what's still open.
- Unanswered items that will roll to tomorrow (with recommended handling).
- Commitments the user made today (so nothing is dropped) and what they're waiting on from others.
- Tomorrow's first-look: earliest meeting, anything needing prep tonight.
- **Update `../commitments.md`** (with approval): add commitments made today, mark resolved ones
  done, refresh waiting-on entries. This is the moment the tracker earns its keep.

## § Week ahead ("prepare me for next week")

Forward-looking, not a rolled-up daily brief. The job is to surface what the user must *decide,
prepare, or protect* before the week starts — while there is still time to move things.

1. **Load `../preferences.md`** (working hours, focus blocks, VIPs) and **`../commitments.md`**.

2. **Pull the window** with `workiq-fetch`, in parallel. Default to the next 7 days from the
   coming Monday; honor an explicit window if the user gives one.
   - `/me/calendarView?startDateTime={ISO}&endDateTime={ISO}` with
     `$select=id,subject,start,end,organizer,attendees,isAllDay,onlineMeeting,location,responseStatus,webLink,hasAttachments`,
     `$top=50`.
   - Pending invites in that window (`responseStatus` not responded) — these are the cheapest
     thing to fix early and the most annoying to fix late.
   - Planner tasks and follow-ups due inside the window.

3. **Compute the shape of the week**, and lead with it:
   - **Load:** meeting count and total booked hours per day; which days are over-booked.
   - **Prep debt:** meetings where the user is organizer with no agenda, or an attendee with a
     pre-read they have not opened. Each one is work that must happen *before* that day.
   - **Focus time:** whether any protected block from `preferences.md` survives the week, and
     what is eating it.
   - **Conflicts and travel/OOF:** double-bookings, back-to-backs with no gap, anything
     colliding with time off.
   - **Commitments due in-window** from `../commitments.md`, plus anything the user is waiting
     on that will block them next week if it does not land.

4. **Render:**

   ```
   🗓️ Week Ahead — {Mon DD}–{Fri DD}

   ⚖️ Shape of the week
     • {n} meetings, {h}h booked · heaviest: {day} ({h}h) · lightest: {day}
     • Focus time: {what survives / what's eaten}

   🔥 Decide or prepare before Monday ({n})
     1. {what} — {why it must happen first} → {recommended action}

   📅 Day by day
     {Mon} — {n} mtgs · {the one that matters} {⚠️ if prep needed}
     ...

   ⚠️ Conflicts & risks
     • {double-bookings, no-prep-time, invites still unanswered}

   📌 Due this week (from commitments)
     • {what} → {to whom} → {due}

   ⏳ Blocking you
     • {waiting on X from Y — nudge now if it gates next week}

   Want me to draft agendas, chase the open invites, or protect focus time? I'll propose first.
   ```

5. **Offer the fixes, don't apply them:** draft the missing agendas, propose declines for the
   low-value conflicts, block focus time, nudge the people who are blocking. Each is an action
   and needs explicit approval of that specific action.
