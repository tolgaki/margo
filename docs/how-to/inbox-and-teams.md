# Triage your inbox and Teams

**Preconditions:** complete [setup](setup-and-migration.md) and confirm the Work IQ connection.

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
message. Mark-read, categorize, flag, and move-between-folders can be granted as a **standing,
bounded authorization** because they're reversible and invisible to anyone else — ask Margo to
set that up if you want it. Archiving in bulk is re-confirmed with the exact count before it runs.

**Change your mind:** ask for a different bucket, a narrower sender, or to skip the archive
suggestions entirely; nothing here is durable until you approve a specific action.

**Your data:** the triage list itself isn't persisted; any draft you ask for is stored as a
private action-desk proposal (see [commitments and the action desk](commitments-and-action-desk.md))
until you approve or discard it.

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
going forward; this triage keeps no memory of what it showed you unless you ask it to.

**Your data:** same boundary as inbox triage above — nothing persists unless you ask for a draft,
which becomes a private action-desk proposal.

**If something goes wrong:** if the chat/message enumeration is incomplete, Margo says which
chats it couldn't check rather than reporting a clean queue.

**Availability:** implemented, procedure. Since 1.0.0.

## Advanced reference

Both routines are described in
[the triage reference](../../skills/chief-of-staff/references/triage.md), including the exact
Work IQ query shapes (`$select`/`$top`/`$filter` constraints — note `flag/flagStatus` and `from`
cannot be server-filtered and return `400 InefficientFilter`) and the classification buckets.
There is no dedicated CLI for triage; approved replies flow through the same
[work ledger and action desk](commitments-and-action-desk.md) as any other draft.
