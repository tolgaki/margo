# Inbox & Message Triage

Turn a noisy inbox (and Teams) into a short, ranked, decision-ready list. **Propose actions;
never archive, mark-read, reply, or delete without explicit approval.**

## Procedure

1. **Load preferences** — VIPs, always-flag rules, auto-deprioritize senders, delegates.

2. **Gather** with `workiq-fetch` (always with `$select` + `$top`; run these in parallel):
   - Unread `/me/messages` — `$filter=isRead eq false` **is** index-backed and safe when paired
     with `$orderby=receivedDateTime desc`. Convert "this week" to an explicit ISO date first.
     `$select=id,subject,from,receivedDateTime,isRead,flag,toRecipients,bodyPreview,webLink`,
     `$top=50`.
   - **Flagged mail: do not filter on it server-side.** `$filter` on `flag/flagStatus` returns
     `400 InefficientFilter`. Fetch recent mail ordered by `receivedDateTime desc` and pick out
     the flagged items locally from the `flag` field you already selected.
   - Prefer items where the user is a direct (To) recipient over CC.
   - Pull full message bodies only for the items you'll actually draft against.
   - **Page within a budget.** Follow `@odata.nextLink` until the unread set is complete or the
     budget (default 5 pages) runs out. If a `nextLink` remains, say the triage covers the newest
     {n} items only and how far back it reaches; never report the bucket counts as complete.
   - For "since yesterday" or "since my last triage", replay the inbox delta link instead of
     re-reading the folder (`delta.md`), and classify only the changed items.

3. **Sweep Teams** — this is half the triage, not an optional extra. There is no unread feed, so
   build an attention queue from recency instead, and say that is what you did:
   - `workiq-fetch` `/me/chats?$expand=members&$top=25` to list active chats with participants.
   - For chats that look live (or involve a VIP from `preferences.md`), fetch
     `/chats/{id}/messages?$top=20` — batch several URLs into one `workiq-fetch` call.
   - `workiq-retrieve` for @mentions and anything addressed to the user by name.
   - For a precise "since yesterday" sweep, use a chat delta path only if `delta.md` discovery
     finds one on this binding; otherwise this recency queue is a snapshot, and you say so.

   Load the `workiq` skill (call the `skill` tool with `workiq`) and follow its Teams triage
   reference for the exact shapes — it carries the full attention-queue procedure. **Never claim
   Work IQ exposes a native Teams unread/notification feed**; report recency as recency.

4. **Classify each item** into one bucket:
   | Bucket | Meaning | Default recommended action |
   |---|---|---|
   | 🔴 **Respond today** | Needs the user's reply/decision, time-sensitive or VIP | Draft a reply (Draft Studio) |
   | 🟡 **Respond this week** | Needs a reply but not urgent | Queue; offer draft |
   | 🟢 **Delegate** | Someone else should own it | Draft a hand-off to {delegate} |
   | 🔵 **Read / FYI** | Awareness only, no action | Mark read (on approval) |
   | ⚪ **Archive / ignore** | Newsletter, automated, noise | Batch-archive (on approval) |
   | ⏳ **Waiting** | User already acted; awaiting reply | Track in `../commitments.md`; nudge if stale |

5. **Rank** within 🔴 by VIP → deadline → external/customer → effort-to-clear.

6. **Present the triage list:**
   ```
   📥 Inbox Triage — {n} items ({r} need you today)

   🔴 Respond today
     • {sender} — "{subject}" — {the ask in ≤12 words} → I can draft: {reply/decline/delegate}
   🟡 This week
     • ...
   🟢 Delegate → {who}
     • ...
   🔵 Read / FYI ({n})   ⚪ Archive candidates ({n})   ⏳ Waiting on ({n})
     (collapsed counts; expand on request)

   Shall I draft the 🔴 replies, and/or clear the ⚪ archive batch? Nothing changes without your OK.
   ```

7. **On approval only**, execute batched actions via Work IQ (`workiq-update_entity` to mark read,
   `workiq-do_action` / `workiq-create_entity` to reply/forward). Marking read
   (`/me/messages/{id}` with `{"isRead": true}`) and replying are well known; run
   `workiq-get_schema` only for a path or field you aren't sure of. Confirm what was done and what remains.

## Guardrails
- Re-confirm before any **bulk** action (e.g. "archive these 14?") — list or count them first.
- Never auto-delete. "Archive/ignore" is a proposal, and delete is only ever explicit + specific.
- If a sender/thread is ambiguous in priority, surface it rather than silently deprioritizing.
