# Keep a durable decision log

**Preconditions:** install the optional `decision-log` skill (`./install.sh --all` or
`.\install.ps1 -All`) and fill in `skills/decision-log/config.md` with the log's location,
sources, team members and workstream areas. Writes to the log are pull requests to a shared git
repository, not in-place edits — this is a team-shared surface, not private account state.

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

**Change your mind:** reject a proposed record before it's written; nothing durable happens
until you approve it. Once written, a record is never edited away — see supersession, below.

**Your data:** approved records go into the shared decision-log repository via a pull request,
reviewable like any other change; nothing about the extraction process is stored privately.

**If something goes wrong:** genuinely uncertain items are marked `needs-confirmation` rather
than logged at full confidence — under-extracting is the deliberate failure mode here, because a
log with 20 real decisions beats one with 200 maybes.

**Availability:** optional (requires the `decision-log` skill), procedure. Since 1.0.0.

## Decision answer

**What this helps you do:** get the current, authoritative answer to "why did we decide X" or "is
that still the plan" — checked against the log first, so a stale transcript opinion never
outranks a settled decision.

**Try it:**

> Why did we decide to use Postgres for ingestion?

> Is the schema freeze still three weeks before release?

**What you will see:** the current record, and if it was superseded, the full chain to what's
active now. If the log has no answer, Margo says so explicitly and falls back to transcripts,
clearly marked unconfirmed — never presented with the log's authority.

**What needs your decision:** nothing — this is read-only.

**Change your mind:** not applicable; answering a question never writes anything.

**Your data:** nothing is stored; this reads the existing shared log and, if needed, other Work
IQ sources.

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

**What needs your decision:** nothing — the digest is read-only output.

**Change your mind:** not applicable.

**Your data:** nothing is stored beyond the conversation; the digest is generated fresh from the
current log each time.

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

**Your data:** nothing is stored beyond the conversation.

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
