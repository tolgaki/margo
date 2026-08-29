# Retrieval

Answering "why did we do X", "what's the current call on Y", "is that still the plan".

## Order of authority

Search in this order and stop when you have a confident answer:

1. **The decision log** — authoritative, current
2. **Repo artifacts** (ADRs, PR descriptions, code comments) — authoritative for implementation
3. **Meeting recaps / transcripts / chat** — *suggestive only, never authoritative*

Never blend tiers 1 and 3 into a single confident answer. If tier 3 is all you have, say so
plainly: *"This isn't in the decision log. Based on the 2026-06-12 sync, it looks like… — want me
to confirm and log it?"* That sentence is the whole point of the skill: it converts an unreliable
answer into a durable one.

## Required checks before answering

1. **Status.** If the matching record is `superseded` or `reversed`, follow the chain to the
   current record. Never quote a superseded record as current — not even with a caveat buried at
   the end. Lead with the current answer.
2. **Confidence.** If the record is `needs-confirmation`, say so and offer to get it confirmed.
3. **Amendments.** Check for records that `Amend` the one you found; the full constraint is the
   base plus its amendments.
4. **Age vs. `Revisit`.** If a `Revisit` date has passed, flag it: the decision stands but was
   meant to be re-examined.

## Answer shapes

**Simple, current:**
```
ENG-0042 (2026-08-07): Ingestion state uses Postgres; Cosmos retained only for the user index.
Why: RU costs growing superlinearly; queries are relational.
Type: two-way — cheap to revisit. Owner: @ana.
Source: Ingestion sync, 2026-08-07 →
```

**Changed over time — lead with current, then the chain:**
```
Current: ENG-0042 (2026-08-07) — Postgres for ingestion state.

How it got here:
  ENG-0031 (2026-06-12) Cosmos for ingestion state
    └─ superseded by ENG-0042 — RU costs grew superlinearly once real ingest volume landed;
       relational query patterns made the Cosmos model awkward.
```

The chain is usually the *most* useful part of the answer. "We already tried that and here's what
broke" is exactly the context a fast team loses when people rotate across time zones.

**Not in the log:**
```
⚠️ Not in the decision log.

Closest signal: in "Platform sync" (2026-05-03), Wei said the team would keep the shared
queue "for now" — but no decision was logged and there's no owner on record.

Want me to (a) confirm with @wei and log it, or (b) raise it as an open question?
```

Always end an unfound answer with a route to fixing the gap. Every miss is a chance to make the
log better; a miss that ends in a shrug is a wasted one.

**Conflict between log and reality:**
```
🚨 Conflict. ENG-0018 says all services expose /healthz, but ingestion-svc uses /health.
Either the code drifted or the decision was amended without being logged.
Want me to flag it to @ana?
```

Surface these rather than silently trusting either side. Drift between the log and the code is a
signal the log is losing touch — catch it early or people stop believing it.

## Searching well

- Search `Affects` areas and titles before full text — the index table is small and high-signal.
- Query the log for *topics*, not phrasing. Someone asking "why aren't we on Cosmos" needs ENG-0042,
  which never says "Cosmos" in its title.
- Check both the current record and what it superseded. Questions phrased in the past tense
  ("why did we pick Cosmos") are usually asking about a superseded record — answer it, then say
  what replaced it.
- When several records touch a question, present the constraint as the *set*, not just the newest.

## What not to do

- Don't summarize the log at length. Answer the question, cite the ID, link the source.
- Don't hedge a logged decision. If it's `active` and `high` confidence, state it flatly — the
  hedging is what people came here to escape.
- Don't invent an ID. If you can't find a record, say you can't find it.
- Don't treat your own prior answer in this conversation as a source. Re-read the log.
