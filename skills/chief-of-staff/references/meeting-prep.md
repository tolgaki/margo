# Meeting Prep

Make the user walk in ready. For a given meeting (or every meeting today that needs prep),
assemble purpose, people, context, and talking points.

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
   anything they're likely to raise. Use people/org lookups via `workiq-fetch` when useful.

4. **Render the prep card:**
   ```
   📋 Meeting Prep — {title}
   🕐 {time} ({duration}) · {location/online} · Organizer: {who}
   👥 Attendees: {names + roles; flag VIP/external}

   🎯 Purpose: {1–2 lines}
   📌 Context / where things stand: {bullets, grounded + cited}
   📎 Docs: {title — 1-line relevance}
   🗣️ Talking points / your goals: {what to raise, decisions to push}
   ❓ Likely questions & suggested answers:
   ✅ Prep to-dos before the meeting: {if any}
   ```

5. **Offer follow-ons:** draft a pre-read/agenda, a message to an attendee, or a
   post-meeting follow-up (Draft Studio) — proposed, not sent.

## Notes
- If several meetings need prep, do the next/most-important first and list the rest.
- For recurring meetings, emphasize the delta since last time and open action items.
- Flag prep risks: no agenda, double-booked, no prep time before it, or you're the organizer and
  haven't sent an agenda.
