# Draft Studio

Produce ready-to-send drafts (email replies, new emails, Teams messages, follow-ups) grounded in
real context and written in the user's voice. **Always present for approval; only send/reply via
WorkIQ after an explicit go-ahead.**

## Procedure

1. **Load voice** from `../preferences.md` — tone, sign-off, length, do/don't. If unset, use
   friendly-professional, concise, and end with a clear next step.
   **The draft is in the user's voice, never the assistant's.** Whatever persona you're speaking
   in applies to your commentary around the draft — never to the text inside the block, which the
   user is signing their name to.

2. **Gather context (WorkIQ):**
   - For a **reply**: `workiq-fetch` the exact thread/message (resolve its ID) so you have the
     real content, participants, and the specific ask you're answering. Use `workiq-ask` to
     summarize a long thread and `workiq-retrieve` to pull citable related context from other
     emails/meetings/Teams/docs.
   - For a **new message**: `workiq-ask` for the relevant background (project status, prior
     commitments, what the recipient last said) so the draft is informed, not generic.
   - Note any facts/dates/links you're asserting — they must come from real data.

3. **Draft.** Match voice and length. Structure for the medium:
   - **Email:** clear subject, brief opener, the point/ask up front, specifics, explicit next
     step + owner + date, sign-off.
   - **Teams/chat:** shorter, conversational, still with a clear ask/next step.
   - Make the recipient's decision easy: propose specifics (a time, an answer, an option) rather
     than open-ended questions where possible.

4. **Present the draft in a labeled block:**
   ```
   ✉️ Draft — {Email reply / New email / Teams message}
   To: {recipient(s)}   Cc: {if any}   [Channel/Chat: {name} if Teams]
   Subject: {subject}
   ─────────────────────────────
   {draft body}
   ─────────────────────────────
   Send as-is, edit, or discard?  (I won't send until you say so.)
   ```
   If helpful, offer **2 variants** (e.g. concise vs. warmer, or accept vs. propose-alternative).

5. **Iterate** on feedback until approved.

6. **On explicit approval**, send/reply via WorkIQ:
   - Reply/forward/send → `workiq-do_action` (or `workiq-create_entity` for a draft +
     `workiq-do_action` to send) on the resolved message/thread.
   - Teams message/reply/react → entity tools on `/chats/...` or `/teams/...`. Load the
     `workiq` skill (call the `skill` tool with `workiq`) and follow its Teams reference for
     the exact shapes.
   Then confirm it was sent and, if the message created a commitment (you promised something) or
   a waiting-on (you asked for something), offer to log it in `../commitments.md` with owner, due
   date, and source link.

## Quality bar
- Never fabricate a commitment, date, name, number, or link — pull it or ask the user.
- Keep it tight; respect the user's length preference.
- Always include a concrete next step and owner.
- Preserve thread etiquette (reply-all vs. reply, keep/trim quoted history appropriately).
- If the message is sensitive (escalation, bad news, exec audience), flag it and suggest a
  careful tone; offer to soften or add nuance before sending.
- **Write from the recipient's side.** Never leak the mechanics behind a message — how the time
  was found, who else is in a rotation, what the template looked like. For calendar invites
  specifically, follow `calendar.md` → *Invite text: write it for the person receiving it*.
