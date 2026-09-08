# Draft replies and executive follow-ups

**Preconditions:** complete [setup](setup-and-migration.md) and confirm the Work IQ connection.
Fill in `preferences.md` → *Communication & drafting voice* so drafts sound like you, not Margo.

## From request to reviewed draft

1. Give the source and intent: **"Reply to Dana's specification-review email. Acknowledge the
   questions and propose a short discussion; don't promise a completion date."**
2. Check the draft's To/Cc, reply versus reply-all, channel, facts and any proposed commitment.
   If two threads or people match, choose the actual one before approving.
3. Revise: **"Make it shorter, keep the source link, and remove the new deadline."** Margo saves
   a new proposal revision; earlier approval no longer applies.
4. Choose the next state explicitly: keep it private, request an Outlook draft, send this exact
   version, or dismiss it. Creating an Outlook draft and sending it are different external writes,
   each requiring its own approval. A private draft needs neither mailbox write.
5. After any approved send, expect a result reference or an explicit unknown/partial outcome.
   A prepared draft or approved proposal is not evidence that the recipient received anything.

For a memo, comparison or full agenda rather than a message, use
[Prepare a work product](feedback-and-work-products.md#work-products).

## Drafting

**What this helps you do:** get a ready-to-send reply, new message, or Teams draft — grounded in
the real thread and written in your voice — instead of writing it yourself from a blank box.

**Before you start:** the thread or context you want drafted against must be reachable through
Work IQ (an email thread, a chat, a meeting). Your voice preferences (tone, sign-off, length) are
optional but make the first draft usable rather than generic.

**Try it:**

> Draft a reply to Dana's email about the spec review.

> Write a Teams message to Rafa asking for the cost model by Thursday.

**What you will see:** a labeled draft block — recipient(s), channel, subject, and the body —
followed by "Send as-is, edit, or discard?" The draft is always in **your** voice per
`preferences.md`, never the assistant's; commentary around the block can carry Margo's voice, the
draft itself never does.

**What needs your decision:** nothing sends until you approve the exact account, action,
destination, payload and revision. Editing recipients, attachments, body or other write-relevant
fields invalidates prior approval; changing only the proposal's reason also creates a new revision.

**Change your mind:** ask for a rewrite, a different tone, or a second variant any time before you
approve it; dismissing a draft hides it from the active action desk and invalidates unused
approval without contacting anyone. It does not erase its history.

**Your data:** the draft is stored as a private, versioned action-desk proposal (see
[commitments and the action desk](commitments-and-action-desk.md#action-desk)). Its payload,
revisions, approvals and execution receipts remain after approval, dismissal or execution.
Sending re-checks the target and sources immediately beforehand; a timeout or unclear result
is reconciled from real evidence, never silently retried.

**If something goes wrong:** if the send result is uncertain (timeout, partial failure), Margo
reports it as unknown and asks you to check before trying again — it will not assume success or
resend automatically.

**Availability:** implemented, procedure. Since 1.0.0.

## Executive followup

**What this helps you do:** turn a meeting, thread, or recap into a message ready for a senior,
peer-exec, or partner audience — listening-first, specific, and credited by name — rather than a
generic status update dressed up.

**Before you start:** the source material (meeting recap, chat thread, related email) needs to be
reachable through Work IQ. This flow does more research than an ordinary draft, so give it the
meeting or thread to start from.

**Try it:**

> Turn today's advisory board session into a follow-up message.

> Make this exec-ready — pull in the meeting chat and the transcript too.

**What you will see:** a message structured "what we heard from you" before "what we're doing,"
each point credited to the person who raised it, with supported owners, dates, and cadence.
Missing details stay open questions or clearly marked proposals, not invented commitments.
Before presenting it, Margo resolves the exact destination chat; if more than one candidate
matches, it stops and asks which one rather than guessing.

**What needs your decision:** you approve the destination and the text together — approving a
topic string is not approval to post to a specific chat if the chat gets resolved afterward.
Posting uses the same per-action approval as any other send.

**Change your mind:** ask for a different balance of listening vs. proposing, a shorter version,
or to hold it entirely; nothing posts until you approve the specific destination and text.

**Your data:** the same private action-desk storage as ordinary drafts, including retained
proposal revisions, payloads, approvals and execution receipts after dismissal or posting.

**If something goes wrong:** if the meeting record, transcript, or related threads can't be
found, Margo says which source is missing rather than drafting from a thinner picture without
saying so. Ambiguous destination chats always stop for your explicit choice.

**Availability:** implemented, procedure. Since 1.0.0.

## Advanced reference

Full procedures, including the voice rubric, self-check, and Work IQ query shapes, are in
[Draft Studio](../../skills/chief-of-staff/references/drafting.md) and
[Executive Follow-up](../../skills/chief-of-staff/references/exec-followup.md). Both write
through the same [action-desk propose/edit/approve/begin/finish](commitments-and-action-desk.md#action-desk)
lifecycle as any other action — there is no separate drafting CLI.
