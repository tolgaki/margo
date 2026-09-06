# Watch your community and feedback channels

**Preconditions:** complete [setup](setup-and-migration.md). Both routines are optional and need
a one-time configuration step in `preferences.md` before they do anything.

## Engage

**What this helps you do:** see what your product's Viva Engage community actually thinks —
unanswered questions first, since a customer question sitting open for weeks in public is a
commitment nobody logged, then recurring themes and threads that ran hot.

**Before you start:** the community name and GroupId recorded in `preferences.md` → *Community —
Viva Engage*. If you don't own a community, this does nothing — delete
`references/engage.md` and its `SKILL.md` row.

**Try it:**

> What's the community saying? Anything unanswered?

> Top topics in my Engage community this week.

**What you will see:** unanswered questions leading the read-out, then themes, hot threads, and
what the community's collective mental model gets right or wrong about the product — each with
the poster's name, the thread title, and a link.

**What needs your decision:** posting a reply is public and company-wide, so it always needs
explicit approval, written in your voice — Engage's visibility raises the bar, it doesn't lower
it.

**Change your mind:** ask for a narrower time window or a specific theme any time.

**Your data:** a short private running note records the last sweep's shape so a later sweep can
compare it. Retrieval/parser input files and session history may retain source content; tracked
flows can also retain coverage, progress, proposals and published outputs/receipts. Check the
source's sensitivity before quoting it into a forwardable summary. Clearing the running note
does not erase those other records or the source posts.

**If something goes wrong:** in the verified Work IQ binding, structured Engage paths were
unavailable and grounded retrieval was the available route. Re-check advertised capabilities
for the current host without bypassing a denial. A retrieval query returning nothing means
"no threads found for that phrasing," not "the community is quiet." The verified results did
not expose usable reply counts or upvotes; report "stayed active N days" as a proxy, never as
a reply count or proof of reply-level threading.

**Availability:** optional (needs a configured community), procedure. Runs on request, and
recommended as a periodic manual sweep — not one of the six scheduled routines. Since 1.0.0.

## Teams feedback

**What this helps you do:** see what's actually broken, from the people hitting it — a sharper
signal than Engage, which is more "can this do X" than "this did not work."

**Before you start:** the Team ID and Channel ID for the feedback channel, recorded in
`preferences.md` → *Community — feedback channel (Teams)*. If you don't have one, delete
`references/teams-feedback.md` and its `SKILL.md` row.

**Try it:**

> What's in the feedback channel? Any bugs raised recently?

> What are people reporting as broken this week?

**What you will see:** recurring failure themes, posts with no response (judged on the actual
last message in the thread, not a raw reply count — a thread where the asker spoke last is open
even with a dozen replies), who's carrying the channel's response load, and partner/customer-
facing issues ranked above internal curiosity.

**What needs your decision:** posting a reply always needs explicit approval, in your voice, with
no sign-off on Teams messages per your preferences.

**Change your mind:** ask for a narrower window or a specific theme any time.

**Your data:** the same private running-note pattern as Engage supports comparisons across
sweeps. Message/reply input files, session history and any tracked evidence, proposals or
outputs may also persist. A summary is not a promise that source content was never retained.

**If something goes wrong:** a partial page sweep is reported as partial (the bundled parser
prints an explicit `WARNING ... PARTIAL` line) — Margo will never report an unanswered count from
an incomplete sweep. "No replies" is not treated as "ignored" without checking whether the poster
simply solved their own problem afterward.

**Availability:** optional (needs a configured channel), procedure. Runs on request. Since 1.0.0.

## Advanced reference

[Viva Engage community](../../skills/chief-of-staff/references/engage.md) and
[Product feedback — Teams channel](../../skills/chief-of-staff/references/teams-feedback.md) have
the exact retrieval/delta query shapes and parser invocations:

```sh
python3 skills/chief-of-staff/scripts/engage_parse.py RETRIEVAL_OUTPUT.txt --group "Product name" --format table
python3 skills/chief-of-staff/scripts/teams_feedback.py MESSAGE_PAGE.txt --replies REPLIES_PAGE.txt --format table
```

Both parser scripts are plain utilities with no automated test in this repository today — treat
their output as a formatting aid, not as independently verified logic. Proposed replies flow
through the same [action desk](commitments-and-action-desk.md#action-desk) as any other draft.
