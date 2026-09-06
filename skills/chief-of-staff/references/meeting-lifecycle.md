# Meeting lifecycle

This connects `one-on-ones.md`, `meeting-prep.md`, `meeting-debrief.md`, `follow-through.md`,
and the decision log. It does not create another meeting archive.

## Persistent identity

Use the series ID for carry-forward and the occurrence ID for individual preparation and actions.
Record source revision, scheduled time, linked work items, and the current lifecycle stage using
the meeting commands in `work-ledger.md`. A rename must not create a second meeting record.

Calendar presence is not proof of attendance. Mark attendance unknown unless the source establishes
it. Moving or cancelling one occurrence does not update the whole series.

## Before

Accumulate person-specific discussion topics into the rolling agenda as the week progresses.
Link obligations to the canonical work ledger rather than copying their status into an independent
tracker. Topics are not automatically obligations.

Before the meeting, prepare the decision packet: purpose, decisions needed, options, sources,
unresolved asks, and the recommended position. Store it as a versioned work product. A changed
meeting or document source makes the packet stale until reviewed.

## After

Record the occurrence as recap pending and check for the actual recap/transcript through Work IQ.
Use the existing sweep/anchor cadence and the ledger's retry state, not a new unbounded poller.
Suggested retry policy is the next eligible sweep, then the next anchor; after two working days
without a record, surface the gap once and await new evidence. A policy denial is blocked, not
retryable. User-provided notes can be a separate, explicitly attributed source.

Extract decisions, candidate obligations, candidate resolutions, and unresolved questions with
no owner. Check current ledger records and later replies before presenting them. A missing recap
does not mean no decisions or actions.

## Review and carry-forward

Only user confirmation promotes candidates or closes obligations. Decision-log writes remain
separate approved operations; link the canonical decision ID and supersession status instead of
creating a second decision history. Follow-up messages stay in the action desk until approved.

Carry unresolved topics and linked obligations to the next occurrence with owner, age, and why
they still matter. Do not duplicate the same obligation each week. Resolved topics remain in the
history and leave the current agenda.
