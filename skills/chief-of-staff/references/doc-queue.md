# Document Queue

Surface shared documents the user is expected to read. Treat unread pre-reads as quiet
obligations: source them, judge relevance, and recommend read, defer, or declare bankruptcy.

## Procedure

1. **Confirm document paths before fetching** with `workiq-search_paths`. Search for the exact
   WorkIQ paths for shared documents, message attachments, and chat messages before using them:

   ```text
   workiq-search_paths filter: "sharedWithMe|drive|attachments|chats|messages"
   ```

   Then enumerate shared-with-me documents with `workiq-fetch`:

   ```
   /me/drive/sharedWithMe?$select=id,name,webUrl,lastModifiedDateTime,createdBy,shared,remoteItem,file,parentReference&$top=50
   ```

   **A guessed path 400s and looks identical to "no documents" if you only read the empty result.**
   Say the path failed and treat the document state as unknown.

2. **Find documents sent through mail and Teams.** Use `workiq-fetch` for literal reads and keep
   collection calls bounded.

   Mail candidates:

   ```
   /me/messages?$filter=receivedDateTime gt {ISO}&$orderby=receivedDateTime desc&$select=id,subject,from,receivedDateTime,hasAttachments,bodyPreview,webLink&$top=50
   ```

   Attachments on a candidate message:

   ```
   /me/messages/{id}/attachments?$select=id,name,contentType,size&$top=25
   ```

   Teams candidates, after `workiq-search_paths` confirms the chat endpoints:

   ```
   /me/chats?$select=id,topic,chatType,lastUpdatedDateTime,webUrl&$top=50
   /me/chats/{id}/messages?$select=id,from,createdDateTime,body,attachments,webUrl&$top=50
   ```

   Use `workiq-retrieve` when links are buried in mail or Teams text and when sensitivity labels
   matter:

   ```text
   query: ["documents decks specs pre-reads shared with me this week that need my review"]
   strategy: "grounding"
   ```

3. **Determine relevance before listing anything.** For each candidate, identify who shared it,
   whether they are a VIP from `../preferences.md`, the source thread or meeting, and whether the
   sender stated a deadline. Cross-reference upcoming meetings with `workiq-fetch` on
   `/me/calendarView`; the endpoint **requires** `startDateTime` and `endDateTime` and 400s without
   them.

   ```
   /me/calendarView?startDateTime={nowISO}&endDateTime={plus48hISO}
     &$select=id,subject,start,end,organizer,attendees,body,webLink,hasAttachments
     &$top=50
   ```

   A document is relevant when it is from a VIP, attached to or linked from an upcoming meeting,
   explicitly asks for review, names a deadline, or blocks a commitment in `../commitments.md`.

4. **Apply the pre-read filter.** A pre-read for a meeting in the next 48 hours is urgent and clears
   the interrupt test in `references/proactive.md`. Everything else is ambient unless it has an
   explicit deadline today or a VIP direct ask.

   - Next 48 hours pre-read → `section: "needs-your-response"`, action: read before meeting.
   - No imminent meeting, no deadline → `section: "ambient"` or `"fyi"`, action: read later or drop.
   - Waiting on the user because someone explicitly asked for feedback → `section: "needs-your-response"`.

   **Do not turn every shared file into homework.** Most shared docs are FYI until tied to a person,
   meeting, deadline, or commitment.

5. **Age the queue and declare bankruptcy.** If there is no record of the user opening a document
   after about three weeks, it probably will not be read. Recommend a real decision: tell the sender
   it will not get reviewed, move it to read-later, or drop it.

   **Do not carry stale documents forever.** A permanent unread-doc queue becomes guilt wallpaper;
   the useful action is either read now or declare bankruptcy.

6. **Respect sensitivity labels.** `workiq-retrieve` returns sensitivity metadata. If a document is
   labelled confidential, restricted, or otherwise sensitive, do not quote its contents into a card
   or draft the user might forward. Summarize as "needs your read" with the source link and reason.

   **A labelled document can be cited without being excerpted.** Include title, sender, source, and
   URL; omit internal content unless the user is clearly keeping it private.

7. **Dedupe unattended runs** with `scripts/proactive_state.py`. Use stable IDs like
   `doc:{driveItemId}` or `doc:{messageId}:{attachmentId}`. Never use title text as the key.
   `kind` must match the source that made it relevant: `mail`, `chat`, or `event`. **Do not
   invent `kind: doc`; the anchor groups only the kinds in `references/proactive.md`.**

   Queue a normal doc item:

   ```bash
   key='doc:{driveItemId}'
   if ! python3 scripts/proactive_state.py seen "$key"; then
     python3 scripts/proactive_state.py queue-add --json '{"id":"doc:{driveItemId}","kind":"mail","title":"{doc title}","source":"Shared doc · {sender} · {date}","url":"{webLink}","action":"read later or drop","why":"shared with you and relevant to {project/person}","section":"fyi"}'
   fi
   ```

   Queue an imminent pre-read:

   ```bash
   key='doc:{driveItemId}:pre-read:{eventId}'
   if ! python3 scripts/proactive_state.py seen "$key"; then
     python3 scripts/proactive_state.py queue-add --json '{"id":"doc:{driveItemId}:pre-read:{eventId}","kind":"event","title":"Pre-read: {doc title}","source":"Meeting · {title} · {time}","url":"{webLink}","action":"read before the meeting","why":"pre-read for a meeting in the next 48 hours","section":"needs-your-response"}'
   fi
   ```

   When invoked directly by the user and rendered immediately, mark what was shown:

   ```bash
   python3 scripts/proactive_state.py mark "doc:{driveItemId}" --tier anchor
   ```

8. **Render the document card:**

   ```
   📄 Unread & shared with you — {n}

   🔥 Pre-reads before meetings
     • {doc title} — for {meeting title} at {time}, shared by {person} → read before meeting
       {url}

   👀 Needs a decision
     • {doc title} — shared by {person}, {age}; {deadline/reason} → read / delegate / declare bankruptcy
       {url}

   🌫️ Ambient read-later
     • {doc title} — {project/source}, shared {age} → read later or drop
       {url}

   🧾 Proposed tracker changes
     • {add commitment / no tracker change / ask sender for deadline}
   ```

## Notes
- The user may have read a document outside the tracked surface. Say "no record of you opening
  this," never "you have not read this."
- Documents the user shared themselves are not obligations. They can be FYI only if another person
  replied with an ask.
- If `workiq-fetch` or `workiq-retrieve` returns empty or errors, the result is unknown, not zero.
  Surface the failed source and recommend retrying or narrowing by sender/project.
- Shared folders are not individual read obligations. Only surface a specific file when it has a
  person, meeting, deadline, or commitment attached.
- For unattended ambient scans, promote only threshold crossings: newly imminent pre-reads, a newly
  stale three-week document, or a new VIP-shared doc. Do not re-queue the same stale file daily.
