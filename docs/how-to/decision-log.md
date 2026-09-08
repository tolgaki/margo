# Keep a durable decision log

**Preconditions:** install the optional `decision-log` skill (`./install.sh --all` or
`.\install.ps1 -All`) and fill in the installed `skills/decision-log/config.md` with the private
registry repository, clone location, team slug and ID prefix. That file is a **pointer**:
the shared registry's `teams/{team-slug}/config.md` holds sources, people, areas and conventions;
`teams/{team-slug}/DECISIONS.md` is the log. Writes are pull requests to that shared git
repository, not in-place edits — this is a team-shared surface, not private account state.

## Your first source-to-record journey

1. Verify the configured registry and read its current default-branch log. If you cannot refresh
   it, say the log may be stale; do not infer that a decision is still current.
2. Ask: **"Extract the decisions from this meeting, using its actual date. Show duplicates,
   uncertain items, open questions and tasks separately. Do not publish yet."**
3. Review the proposed choice, rationale, owner, affected area, reversibility and decisive source.
   An AI recap without decisive quoted language may warrant `needs-confirmation`, not `high`.
4. Approve the exact records and destination when ready. The PR contains one meeting or thread,
   newly allocated IDs, any supersession updates and a regenerated index.
5. Treat an unmerged PR as a proposal, not the authoritative default-branch log. Later, ask
   **"What is the current call on this, including amendments and supersession?"**

Keep the registry private when it contains workplace decisions. This reference-implementation
repository is not a destination for your team's log. Logging a team's conditional decision also
does not give Margo standing permission to execute its future external actions.

## Decision extraction

**What this helps you do:** turn a meeting or thread into a small, high-signal, append-only
record of what the team actually decided — separated from discussion, tasks, and open questions,
so it's still worth searching six months later.

**Try it:**

> Log the decisions from yesterday's architecture sync.

> Extract decisions from this thread — what did we actually settle?

**What you will see:** proposed records grouped high-confidence first, then
needs-confirmation, each with the decision, the rationale, whether it's easy or expensive to
reverse, and its source link — plus a dedupe check against the existing log, open questions
raised but not decided, and action items misclassified as decisions (routed elsewhere instead).

**What needs your decision:** nothing is written to the log without your explicit approval of the
proposed set — a summary is not consent. You can log all, a subset, or edit any record first.

**Change your mind:** reject a proposed record before it is written to the shared log.
Once active, a record is never edited away — see supersession, below. The procedure has a
specific exception for a human-rejected `needs-confirmation` record, which may be removed.

**Your data:** approved records go into the shared decision-log repository via a pull request,
reviewable like any other change. Source content and proposals may remain in session history,
private local inputs or the registry clone; approval of publication is not a no-retention promise.

**If something goes wrong:** genuinely uncertain items are marked `needs-confirmation` rather
than logged at full confidence — under-extracting is the deliberate failure mode here, because a
log with 20 real decisions beats one with 200 maybes. With your approval, uncertain records can
be stored as `Status: needs-confirmation`; they do not outrank active decisions. Later explicit
confirmation updates Status/Confidence, while rejection removes that unconfirmed record.

**Availability:** optional (requires the `decision-log` skill), procedure. Since 1.0.0.

## Decision answer

**What this helps you do:** get the current, authoritative answer to "why did we decide X" or "is
that still the plan" — checked against the log first, so a stale transcript opinion never
outranks a settled decision.

**Try it:**

> Why did we decide to use Postgres for ingestion?

> Is the schema freeze still three weeks before release?

**What you will see:** the current record, and if it was superseded, the full chain to what's
active now. Applicable amendments are included too; a passed `Revisit` date is flagged, not
silently treated as expiry. If the log has no answer, Margo checks repository artifacts before
meeting/chat evidence, keeping implementation evidence and unconfirmed discussion distinct.

**What needs your decision:** nothing — this is read-only.

**Change your mind:** not applicable; answering a question never writes anything.

**Your data:** this does not change the log. It reads the registry and any needed supporting
sources; retrieved content and the answer can remain in the conversation and private inputs.

**If something goes wrong:** a conflict between a transcript and a logged decision is flagged
explicitly, with the decision winning by default.

**Availability:** optional (requires the `decision-log` skill), procedure. Since 1.0.0.

## Decision supersession

**What this helps you do:** record that the team changed its mind — without erasing the original
decision, since the record of *changing your mind* is often the most valuable thing in the log.

**Try it:**

> We changed our mind on ENG-0031 — log the new decision as a supersession.

**What you will see:** a proposed new record marked as superseding the old one, with what changed
and why; the original record's `Status` updates, but its text is never edited away.
An amendment that only adds a constraint can leave both records active. A reversal withdraws
the constraint; it is not necessarily a replacement decision.

**What needs your decision:** the same explicit approval as any other log write — supersession is
still a write.

**Change your mind:** reject the proposed supersession before approving it; once written, revert
by logging a further supersession, never by editing history.

**Your data:** same shared-repository PR flow as extraction, above.

**If something goes wrong:** a concurrent PR that also touched the index table is resolved by
renumbering your unmerged records, not by silently overwriting the merged one.

**Availability:** optional (requires the `decision-log` skill), procedure. Since 1.0.0.

## Decision digest

**What this helps you do:** get a short, Teams-message-length weekly read of what was decided,
what changed, and what's still open — without re-reading the whole log.

**Try it:**

> Give me this week's decision digest.

**What you will see:** decided items, changed-our-mind items (with what changed), still-open
questions (unowned ones flagged), and needs-confirmation records due for review.

**What needs your decision:** nothing to produce the digest privately. Posting it to Teams,
emailing it or publishing a mirror is a separate, exact approved action.

**Change your mind:** not applicable.

**Your data:** the digest is generated from the current log without changing it. Session history
and any private output copy remain under their own retention rules.

**If something goes wrong:** an unowned open question is named plainly rather than dropped
silently — in a fast team, an un-owned question is the actual failure mode, not a bad decision.

**Availability:** optional (requires the `decision-log` skill), procedure. Runs on request; not
one of the six scheduled routines. Since 1.0.0.

## Decision audit

**What this helps you do:** find what's unresolved in the log — stale needs-confirmation records,
open questions with no owner, one-way decisions with no recorded rationale, and dead source
links — so nothing quietly rots.

**Try it:**

> What's unresolved in the decision log? What needs confirmation?

**What you will see:** a list of exactly those categories, each naming the specific record.
Margo proposes fixes (confirm, assign an owner, re-link a source); it doesn't apply them itself.

**What needs your decision:** applying any proposed fix is a normal log write and needs the same
approval as extraction, above.

**Change your mind:** not applicable to the audit itself; declining a proposed fix leaves the
record as-is.

**Your data:** the audit leaves the shared log unchanged. The report and any private input/output
files remain subject to their own retention rules.

**If something goes wrong:** a record whose source link is dead is flagged, not silently trusted
or silently dropped from the log.

**Availability:** optional (requires the `decision-log` skill), procedure. Since 1.0.0.

## Advanced reference

The full extraction heuristics, record schema, retrieval rule, and audit/digest formats are in
[`skills/decision-log/SKILL.md`](../../skills/decision-log/SKILL.md) and its
[`references/extraction.md`](../../skills/decision-log/references/extraction.md),
[`references/retrieval.md`](../../skills/decision-log/references/retrieval.md), and
[`references/schema.md`](../../skills/decision-log/references/schema.md). There is no CLI for the
decision log — it is Markdown in a git repository, written through ordinary git/PR operations
that a coding session can prepare directly.
