# Outcomes and capacity

Extend the week-ahead routine rather than creating another weekly report. Read the work ledger,
confirmed preferences, open obligations, and action desk before making a plan.

## Agree what success means

Ask for up to three outcomes for the week. Each needs an owner, definition of done, due date, and
effort estimate or range. Broad focus areas can be stored as context, but they are not implicitly
weekly promises. Missing dates and estimates remain unknown. Do not truncate a longer priority
list to three or silently choose which three the user has agreed to deliver.

Use the outcome records described in `work-ledger.md`. Link supporting work and dependencies;
show mandatory obligations that do not map to an agreed outcome rather than silently dropping them.
Approval of an outcome never approves an external action.

## Compute capacity

Fetch a bounded, fully paged calendar window through Work IQ, respecting source coverage rules in
`state-operations.md`. Expand recurring occurrences. Do not assume an event implies attendance or
that an optional meeting can be deleted from the calculation before the user decides to skip it.

Normalise working intervals, meetings, known leave, scheduling buffers, and explicit reserve
blocks to ISO timestamps with UTC offsets. Resolve each day's offset using the user's confirmed
time zone; do not reuse today's offset across a DST change. Exclude cancelled/declined/free events
only when those fields are actually known. Treat unknown attendance as a qualification.

Calculate interval unions with `scripts/capacity.py --input <private-json-file>`. Input shape:

```json
{
  "coverage": "complete",
  "working": [{"start": "2026-09-07T09:00:00-07:00", "end": "2026-09-07T17:00:00-07:00"}],
  "busy": [{"start": "2026-09-07T10:00:00-07:00", "end": "2026-09-07T11:00:00-07:00"}],
  "protected": [],
  "minimum_focus_minutes": 90,
  "estimates": [{"id": "outcome-example", "minutes": null}]
}
```

Meeting/leave/buffer overlaps count once. Protected focus time is usable work capacity, not
another meeting to subtract. `fits_total_capacity` is unknown for incomplete coverage or missing
estimates. Even a true result does not prove that work is allocated: respect dependencies,
deadlines, working days, and the sizes of the available blocks before proposing an allocation.

## Recommend the trade-offs

Rank by deadline, consequence, what is unblocked, outcome impact, and effort. Apply explicit VIP
exceptions without making seniority the universal priority score.

Show outcome -> next step -> allocated time or named blocker. If the work will not fit, quantify
the shortfall and recommend what to stop, delegate, shorten, or handle asynchronously. Never make
it fit by reducing estimates without evidence. Put actual calendar changes in the action desk.

Report proposed hours reclaimed separately from calendar changes that have really happened.
Do not claim time saved from the number of summaries or drafts generated.
