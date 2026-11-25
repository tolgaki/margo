# Meeting Prep

Make the user walk in ready. For a given meeting (or every meeting today that needs prep),
assemble purpose, people, context, and talking points.

Read `meeting-lifecycle.md` before preparing a recurring occurrence. Include accumulated agenda
topics and linked unresolved work, and store the resulting packet with its evidence revisions
using `work-products.md`. Do not rebuild the agenda from a fresh scrape alone.

Retrieve `meeting-prep` context under `memory.md` for relevant people, prior decisions, user
preferences and lessons. Revalidate changed roles and current decisions rather than treating
old similar episodes as present facts.

## Procedure

1. **Resolve the meeting** with `workiq-fetch` on `/me/calendarView` (or by event ID). The
   `calendarView` collection **requires** a time window — `startDateTime` and `endDateTime`
   are mandatory query parameters and the call 400s without them:

   ```
   /me/calendarView?startDateTime=2026-08-22T00:00:00&endDateTime=2026-08-23T00:00:00
     &$select=id,subject,start,end,organizer,attendees,onlineMeeting,location,body,webLink,hasAttachments
     &$top=25
   ```

   Capture: title, time, organizer, attendees, location/online link, agenda/body.

   **Attachments are a navigation property — `$select` will not return them.** If
   `hasAttachments` is true, fetch them explicitly with `workiq-fetch` on
   `/me/events/{id}/attachments?$select=id,name,contentType,size`, and pull the content of the
   ones worth reading. If you cannot retrieve an attachment, say so in the prep card rather
   than omitting it silently — a missing pre-read is the thing the user will get caught by.

2. **Build context with `workiq-ask`** (and `workiq-retrieve` when you need citable sources):
   - "What is this meeting about and what's the latest on {topic/project}?"
   - "What was decided or left open in previous {recurring meeting} sessions?" (for recurring)
   - "What have {attendees} said recently about {topic} in mail/Teams?"
   - Pull related **documents** shared for or relevant to the meeting (specs, decks, notes) and
     summarize the parts the user needs.

3. **Know the room.** For key attendees, note role/relationship (VIP? external? your report?) and
   anything they're likely to raise. Use the org reads in `people.md`: title and department,
   each person's manager, and whether they are the user's manager, skip-level, direct report,
   peer or external. Flag the most senior person present and anyone whose manager is also in the
   room. A lookup that fails or a guest with no directory entry is shown as unknown.

4. **Apply the sensitivity-label rule** from SKILL.md. Show the label on any context line or doc
   that carries one; keep Confidential and Highly Confidential material to title, the point the
   user needs, and the link. Facts taken from `workiq-ask` have no label: cite the underlying
   source found with `workiq-retrieve` or `workiq-fetch`, or present them as unsourced.

5. **Render the prep card:**
   ```
   📋 Meeting Prep — {title}
   🕐 {time} ({duration}) · {location/online} · Organizer: {who}
   👥 Attendees: {names + roles; flag VIP/external}

   🎯 Purpose: {1–2 lines}
   📌 Context / where things stand: {bullets, grounded + cited}
   📎 Docs: {title — 1-line relevance} [{label, if any}]
   🗣️ Talking points / your goals: {what to raise, decisions to push}
   ❓ Likely questions & suggested answers:
   ✅ Prep to-dos before the meeting: {if any}
   ```

6. **Offer follow-ons:** draft a pre-read/agenda, a message to an attendee, or a
   post-meeting follow-up (Draft Studio) — proposed, not sent.

## Notes
- If several meetings need prep, do the next/most-important first and list the rest.
- For recurring meetings, emphasize the delta since last time and open action items.
- Flag prep risks: no agenda, double-booked, no prep time before it, or you're the organizer and
  haven't sent an agenda.
