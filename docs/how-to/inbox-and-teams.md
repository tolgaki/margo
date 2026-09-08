# Triage your inbox and Teams

**Preconditions:** complete [setup](setup-and-migration.md) and confirm the Work IQ connection.

## Work through one queue

1. Bound the review: **"Triage mail and Teams since yesterday afternoon. Show the sources checked
   and leave everything unchanged."** Mention important senders or channels if useful.
2. Correct the ranking: **"Rafa's request can wait until Thursday; Dana's blocks my review."**
   A ranking correction need not become a permanent [rule](feedback-and-work-products.md#rules).
3. Choose one item: **"Draft a reply to Dana in this thread, for private review only."**
   [Drafting](drafting-and-follow-ups.md) resolves the exact thread and destination.
4. For work that outlasts this triage, ask to capture a [commitment candidate](commitments-and-action-desk.md#commitments).
   A "Waiting" bucket is not proof that an ask is unresolved; later replies still need checking.

Mail read state and reply obligation are different: a message you already opened can still need
your response. Ask for recent relevant mail as well as unread mail when that distinction matters.

## Inbox triage

**What this helps you do:** turn a noisy inbox into a short, ranked list — what needs a reply
today, what can wait, what to delegate, and what's safe to archive — with a recommended action
on every line.

**Before you start:** a configured Work IQ connection. Preferences → VIPs and standing rules
(always-flag senders, auto-deprioritize rules) sharpen the ranking but aren't required.

**Try it:**

> Triage my inbox.

> What in my email actually needs a response today?

**What you will see:** items grouped into 🔴 respond today, 🟡 respond this week, 🟢 delegate,
🔵 read/FYI, ⚪ archive candidates, and ⏳ waiting on a reply — each with the sender, subject, the
ask in one line, and a recommended action. Nothing is marked read, archived, replied to, or
deleted while this list is being shown.

**What needs your decision:** every reply, forward, or delete needs approval of that specific
message. Mark-read, categorize, flag, archive, and move-between-folders have a narrow **standing,
bounded authorization** exception in the procedure because they are reversible and private.
If you want it, review the exact account, operations and scope and have the grant confirmed in
writing. It never covers delete, sends, Teams actions or unattended writes, and is not an
approve-all feature in the action ledger. Bulk archiving is still re-confirmed with the exact
batch before it runs.

**Change your mind:** ask for a different bucket, a narrower sender, or to skip the archive
suggestions entirely. Changing a recommendation does not erase earlier session or local records.

**Your data:** the list remains in session history. Tracked or scheduled work may also retain
private evidence, candidates, coverage, task progress and output receipts. Drafts are versioned
[action-desk proposals](commitments-and-action-desk.md); dismissal or approval does not erase
their payloads, revisions or execution history.

**If something goes wrong:** an ambiguous priority is surfaced for your judgment rather than
silently deprioritized. A failed or partial mail fetch is reported as such, not folded into
"inbox clear."

**Availability:** implemented, procedure. Since 1.0.0.

## Teams triage

**What this helps you do:** find what actually needs a reply across Teams chats and channels,
even though there's no reliable "unread" feed to filter on — Margo builds an attention queue
from recency and mentions instead.

**Try it:**

> What needs my attention in Teams right now?

> Anything from Dana or Rafa in Teams since this morning I need to answer?

**What you will see:** active chats and channel threads worth a look, with who needs you and
why, built from recent activity and @mentions rather than a true unread count. Margo says
plainly that this is recency, not an unread feed — it will not claim Work IQ exposes a native
Teams unread/notification surface, because it does not.

**What needs your decision:** every Teams reply, reaction, or post needs approval of that exact
message, with no standing-authorization exception — Teams and Engage posts are visible to other
people and stay per-action always.

**Change your mind:** ask for a narrower window ("just the last hour") or to skip a specific chat
going forward. A one-off scope change is not a standing preference; ask separately to save one.

**Your data:** the same session and private-state boundary as inbox triage above. A local queue
receipt records what was surfaced, not whether you read or answered a Teams message.

**If something goes wrong:** if the chat/message enumeration is incomplete, Margo says which
chats it couldn't check rather than reporting a clean queue.

**Availability:** implemented, procedure. Since 1.0.0.

## Advanced reference

Both routines are described in
[the triage reference](../../skills/chief-of-staff/references/triage.md), including the exact
Work IQ query examples and the classification buckets. In the documented binding,
server-filtering `flag/flagStatus` or `from` can return `400 InefficientFilter`; the procedure
narrows recent mail locally instead. Discover the current host's tools and supported schemas,
rather than assuming every endpoint accepts the same `$select`/`$top`/`$filter` fields.
There is no dedicated CLI for triage; approved replies flow through the same
[work ledger and action desk](commitments-and-action-desk.md) as any other draft.
