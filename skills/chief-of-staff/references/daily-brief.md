# Daily Brief, Catch-up & Week Ahead

For substantive multi-source work, follow `task-runs.md`: retain bounded progress, exact source
coverage and output references so interruption does not become an all-clear or a duplicate send.

Produce a decision-ready snapshot of the user's day. Goal: in 60 seconds of reading they know
what matters, what's coming, and what needs them.

## Procedure

1. **Load preferences** (`../preferences.md`) — working hours, VIPs, projects, brief depth —
   and the **canonical work ledger** — confirmed obligations, candidates, waiting-on items, and
   unresolved execution outcomes. Read `work-ledger.md` and `action-desk.md`.

   **Use the anchor's existing leased batch; do not drain again.** If this is an on-demand brief
   with no anchor yet, own one batch using `state-operations.md`. Fold items into the sections
   named by their `section` field. Publish the actual output and receipt before acknowledging
   only its included IDs. Source coverage and publication are separate state transitions.

2. **Enumerate the skeleton (use `workiq-fetch`, in parallel, always with `$select` + `$top`).**
   State a page budget for each collection (default 5 pages). Follow `@odata.nextLink` until the
   collection is complete or the budget runs out; a source that still has a `nextLink` is
   **partial** and is reported as such in the brief, never counted as complete.
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
     don't burn calls looking for one. Use a chat delta path if `delta.md` discovery finds one;
     otherwise build the recency queue in `triage.md` step 3 and label it a snapshot. Then ask
     about those chats in step 3.
   - **Commitments/tasks:** Planner tasks due soon (`/planner/...`), and follow-ups.
   - **GitHub:** review requests, stale PRs, and assigned issues — see `github.md`. These are
     commitments with the same standing as email; a review request sitting three days is exactly
     as overdue as an unanswered ask, and belongs in the same sections of the brief.

3. **Read what changed, then synthesize over it.**
   - **Delta first.** Follow `delta.md` for mail and calendar (and chats when a delta path is
     discovered): replay each source's stored delta link with `workiq-call_function`, record
     pages and coverage with `proactive_state.py`, and do a bounded full sync on first use or
     after a 410. This is the "changed since yesterday" set; do not ask `workiq-ask` to
     reconstruct it.
   - **Then `workiq-ask` over the diff** (1–3 focused questions, not one giant prompt; pass
     `timeZone`). Name the changed and unread items in the question:
     - "These items changed since {checkpoint}: {one line each with link}. Which need a response
       from me today, and what's the ask in each?"
     - "For today's meetings {list titles}, what's the purpose and what do I need to prepare?"
   - Report each source's coverage (complete, new baseline, partial, unavailable) in the brief.
     A partial delta is not "nothing else changed".

4. **Prioritize.** Rank by hard deadline, consequence, what is unblocked, agreed outcome impact,
   and effort, respecting the user's explicit VIP overrides. Use `outcomes.md` for the capacity
   trade-off; broad focus areas are not automatically confirmed weekly outcomes. Cap top
   priorities at 5. Everything else goes to FYI or read-later.

5. **Render** in the Standard Daily Brief format (see SKILL.md). Trim empty sections. Every
   actionable line ends in a recommended action (reply / delegate / decline / schedule / read).
   Apply the sensitivity-label rule from SKILL.md: show the label of any labeled item a line
   quotes or summarizes, keep Confidential and Highly Confidential items to title, ask and link,
   and cite the underlying source (via `workiq-retrieve` or `workiq-fetch`) for anything taken
   from `workiq-ask`, which carries no label.
   Render confirmed work separately from new candidates; recheck resolution before recommending
   a nudge. Include material coverage gaps and execution problems, not just the host run status.

6. **Prepare next steps:** persist the useful draft or work product in the action desk, with
   evidence and the exact proposed action. In a foreground session ask for the one decision that
   matters; unattended, finish without a question or offer. Never send without explicit approval.

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
stale waiting on them. Read the window with `delta.md` (replay the stored link, or a bounded full
sync from the window's start when none exists), then `workiq-ask` only over the changed items.
Name any source whose coverage does not reach back to the window's start. End with a short
"here's what I'd tackle first" recommendation.

## § End-of-day wrap-up
- What got resolved vs. what's still open.
- Unanswered items that will roll to tomorrow (with recommended handling).
- Commitments the user made today (so nothing is dropped) and what they're waiting on from others.
- Tomorrow's first-look: earliest meeting, anything needing prep tonight.
- **Stage work-ledger updates:** add candidates and suspected resolutions with evidence; confirm
  them only with approval. Regenerate the compatibility view after approved transitions rather
  than hand-editing it. An unattended run leaves candidates ready for later review.

## § Week ahead ("prepare me for next week")

Forward-looking, not a rolled-up daily brief. The job is to surface what the user must *decide,
prepare, or protect* before the week starts — while there is still time to move things.

Follow `outcomes.md` for agreed outcomes and deterministic capacity calculation. Use the work
ledger instead of writing a second task list. Missing deadlines, estimates, or complete calendar
coverage remain explicit gaps; do not report a feasible allocation from incomplete inputs.

1. **Load `../preferences.md`** (working hours, focus blocks, VIPs) and **`../commitments.md`**.

2. **Pull the window** with `workiq-fetch`, in parallel. Default to the next 7 days from the
   coming Monday; honor an explicit window if the user gives one.
   - `/me/calendarView?startDateTime={ISO}&endDateTime={ISO}` with
     `$select=id,subject,start,end,organizer,attendees,isAllDay,onlineMeeting,location,responseStatus,webLink,hasAttachments`,
     `$top=50`. Follow `@odata.nextLink` until the window is complete (budget 10 pages); a
     truncated week is reported as partial, because it understates the load.
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
