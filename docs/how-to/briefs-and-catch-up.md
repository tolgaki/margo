# Get briefed, catch up, and close the day

**Preconditions:** complete [setup](setup-and-migration.md) and confirm the Work IQ connection.
These three routines share one data pull; they differ in window and emphasis, not mechanism.

## A first morning, step by step

1. Ask: **"Brief me for today in my time zone. Keep it quick and name any missing sources."**
   If your working hours or time zone are unfilled, confirm them rather than relying on a default
   for scheduling or capacity.
2. Read the priorities, calendar and needs-response sections. Open the source behind any
   consequential recommendation; a partial brief is still useful, but not an all-clear.
3. Pick one next step: **"Prepare a private reply to the first item. Do not send or create an
   Outlook draft."** Review it in [Drafting and follow-ups](drafting-and-follow-ups.md).
4. If it represents work you owe, ask to capture a [candidate](commitments-and-action-desk.md).
   Confirm the owner and deadline separately; urgency in the brief does not establish a deadline.

For a return from leave, start with **"Catch me up from Monday morning through now; decisions and
anything needing me first."** Give dates and a time zone if "Monday" could be ambiguous.

## Daily brief

**What this helps you do:** start the day knowing what actually needs you — priorities,
calendar, mail and Teams that need a response, and what you're waiting on — instead of
reconstructing it yourself across four apps.

**Before you start:** a configured Work IQ connection and, ideally, a filled-in
[`preferences.md`](../personalization.md) so priorities reflect your VIPs and working hours.
Nothing else is required; an unfilled preferences file just falls back to sensible defaults.

**Try it:**

> Brief me.

> Prep me for my day. Keep it quick — just priorities, calendar and what needs a reply.

**What you will see:** the standard Daily Brief — top priorities (capped at five), today's
calendar with conflicts and no-prep-time flags, mail/Teams needing a response, FYI items, and
open commitments. Every line cites its source (sender, meeting, or chat) with a link. A source
that failed to load is named as a gap, never silently dropped from the count.

**What needs your decision:** the brief makes no external changes. Private drafting or meeting
preparation can follow within the request's scope; an actual send or calendar change needs its
own exact proposal and approval. A brief is never itself consent to execute those actions.

**Change your mind:** ask for a different depth ("just the quick version") or a narrower scope
("only what's new since yesterday") any time. Suggestions become obligations only when you
explicitly confirm them. Private candidates, drafts, coverage and output history
can still be retained before that confirmation.

**Your data:** the brief's output receipt and any useful drafts it prepares are stored locally in
your private account state (see [automation health](automation-health.md)). Minimal source
excerpts, references, candidates and task progress may also persist there. This is not an archive
of the whole mailbox, nor a promise of no local retention. The conversation and selected source
content remain subject to your host and configured model service's handling.

**If something goes wrong:** an unavailable source (mail, calendar, or Teams) is reported by
name, and the rest of the brief still renders — a partial brief is never presented as complete.
If everything fails, Margo says so rather than guessing.

**Availability:** implemented, procedure-driven (the model reads sources and writes the brief;
no deterministic script grades its usefulness). Runs on request, and automatically on weekday
mornings if you enable the [morning brief schedule](automation-health.md#automation-morning).
Since 1.0.0.

## Catch-up

**What this helps you do:** answer "what did I miss" after being away — a meeting, a day off, or
just a busy afternoon — without re-reading everything that happened.

**Try it:**

> What did I miss since yesterday afternoon?

> I was out for two days. Catch me up — what needs me first?

**What you will see:** decisions made, threads that moved, anything now needing you, and one
short recommendation for what to tackle first. Margo uses delta/change endpoints where available
so "since X" is accurate rather than a re-read of everything.

**What needs your decision:** nothing by itself — catch-up is a read. Any suggested reply or
follow-up is a separate draft awaiting your approval.

**Change your mind:** ask for a narrower or wider window at any time. This changes the requested
coverage; it does not erase a prior output or confirm its suggested obligations.

**Your data:** same storage boundary as the daily brief above.

**If something goes wrong:** a source that can't be checked for the requested window is named as
a gap. Missing coverage for Teams, for example, is reported as "Teams coverage unavailable," never
folded silently into "nothing changed."

**Availability:** implemented, procedure. Runs on request for your chosen window. The related
[EOD wrap-up schedule](automation-health.md#automation-eod) runs a distinct end-of-day variant,
not an automatic reconstruction of any absence you have in mind. Since 1.0.0.

## End-of-day

**What this helps you do:** close out the day deliberately — what got resolved, what's still
open, what you promised today, and what tomorrow's first meeting needs — instead of the day just
ending on whatever was in front of you last.

**Try it:**

> End-of-day wrap-up, please.

> What's still open from today, and what do I need to look at first thing tomorrow?

**What you will see:** resolved vs. still-open items, anything rolling to tomorrow with a
recommended handling, commitments you made today (so none get dropped), and a first look at
tomorrow's earliest meeting. An unattended end-of-day run stages candidate commitments (from
today's sent mail and meetings) for your later review — it never confirms them itself.

**What needs your decision:** confirming a staged candidate as a real obligation, and approving
any drafted reply — the wrap-up itself proposes, it does not act.

**Change your mind:** review or discard a staged candidate any time before you confirm it; nothing
becomes a tracked obligation without that explicit step.

**Your data:** staged candidates live in your private work ledger (see
[commitments and the action desk](commitments-and-action-desk.md#commitments)) until you confirm
or reject them.

**If something goes wrong:** the same source-gap handling as the daily brief. An unattended run
that hits a hard failure leaves candidates exactly where they were — it never marks unresolved
work complete to make the run look clean.

**Availability:** implemented, procedure. Runs on request, and automatically on weekday evenings
via the [EOD wrap-up schedule](automation-health.md#automation-eod). Since 1.0.0.

## Advanced reference

These three routines have no dedicated CLI of their own — they read Work IQ directly and write
into the same private state used by [commitments](commitments-and-action-desk.md) and
[automation health](automation-health.md). Their exact procedures, including the standard brief
format and the depth/window rules, are in
[the daily-brief reference](../../skills/chief-of-staff/references/daily-brief.md).

For the scheduled variants (cadence, autopilot mode, and what "silence is a successful run"
means), see [automation health](automation-health.md).
