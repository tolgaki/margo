# Extraction

How to turn a transcript, recap, or thread into decision records without polluting the log.

## The test

For each candidate, ask: **"If a new engineer did the opposite next month, would someone say
'no, we decided against that'?"**

Yes → decision. No → discussion, task, or status. This single test resolves most ambiguity.

Second test, for the borderline cases: **"Does this constrain future work, or does it just
describe present work?"** Constraint → log. Description → skip.

## Linguistic signals

These are evidence, not proof. A phrase never decides on its own — check that the group actually
converged.

**Convergence (likely a decision)**
- Commitment: "let's go with", "we'll ship", "final answer", "locking that in", "settled"
- Closure after divergence: debate → "ok" → topic changes. The topic change is the strongest
  structural signal in a transcript.
- Rejection: "we're not doing", "drop that", "kill it", "not now, and not this half"
- Ratification by silence *with an authority present*: a lead proposes, asks "objections?", none
  raised, discussion moves on
- Ownership assignment attached to a choice: "…so Ana owns the Postgres migration"

**Divergence (not a decision)**
- Hedges: "we could", "maybe", "what if", "one option", "I'd lean toward"
- Deferral: "let's take it offline", "next week", "TBD", "park it", "let me think"
- Conditionals with unresolved antecedents: "if the benchmark holds, we'd use X" — the benchmark
  hasn't happened; this is an open question, not a decision
- Single-voice assertion with no response — one person's opinion into the void

**Amendment (supersession — check the existing log)**
- "actually", "scratch that", "we changed our mind", "revisiting", "that didn't work out"
- Any decision on a topic that already has an `active` record

## Traps

**The loudest voice.** Transcripts over-represent whoever talked most. A strong assertion from one
person is not team convergence. Look for a second voice agreeing, or a lead closing the topic.

**The conditional that never resolved.** "If X, then we'll do Y" logged as "we'll do Y" is a
common way a decision log goes wrong — but so is discarding it entirely. Ask **whether anyone
still has to approve once the condition resolves**:
- *No further approval needed* ("get the number; if it's under $1k, do it") → **conditional
  decision**. Authority was granted. Log it with `Condition`.
- *Someone still has to decide* ("if the benchmark holds, we'd probably use X") → **open
  question**, with the condition recorded so the eventual decision is one step away.

**The rehash.** Teams re-explain settled decisions to whoever joined late. This produces
convincing decision-shaped language for something already logged months ago. Always check the log
before creating a record. If it's a rehash, link to the existing ID and move on.

**The action item in decision clothing.** "We'll fix the flaky test" is a task. "Flaky tests block
merge" is a decision. The difference is durability.

**Ambient agreement.** "yeah", "sure", "mm-hm" in a transcript often means "I'm listening," not "I
agree." Don't count it as ratification.

**Meeting-recap laundering.** If your source is an AI-generated recap rather than the transcript,
remember it has already lossily summarized once. Recap-sourced decisions get
`Confidence: needs-confirmation` unless the recap quotes decisive language verbatim.

## Confidence assignment

Assign `high` only when **all** hold:
1. The choice is explicit and unambiguous
2. Someone with authority over that area was present and did not object
3. You can cite the specific moment (quote or timestamp)
4. It isn't contradicted elsewhere in the same source

Otherwise `needs-confirmation`, and always state *why* in the review block — "Dana proposed, nobody
responded, topic changed" is far more useful to the confirmer than a bare flag.

Default to `needs-confirmation` for: code-switched passages, recap-only sources, decisions
affecting an area whose owner was absent, and anything reversing a `one-way` decision.

## Worked examples

> **Dana:** So do we keep Cosmos or move to Postgres?
> **Ana:** Cosmos RUs are climbing superlinearly, and the queries are relational anyway.
> **Dana:** Yeah. Costs are the thing. Let's move ingestion to Postgres, keep the user index where it is.
> **Ana:** I'll take the migration.
> **Dana:** Good. Next — the China build times…

✅ **Decision, high confidence.** Explicit choice, rationale stated, alternative rejected with a
reason, owner assigned, topic closed. Also emits an action item (Ana → migration) routed
separately.

---

> **Wei:** We might want to split the repo before the release.
> **Dana:** Hmm. Maybe. What's the blast radius?
> **Wei:** Not sure yet, I'd have to look.
> **Dana:** Let's come back to it.

❌ **Not a decision.** Hedged, unresolved, explicitly deferred. → Open question: "Split the repo
before release?" owner @{alias} (investigating blast radius).

---

> **Dana:** If the p99 stays under 200ms we'll turn on the new path for everyone.

❌ **Not a decision** — unresolved condition, and turning it on would still need a call. → Open
question with the condition recorded, so that when the benchmark lands, the decision is one step
away rather than a re-debate.

Contrast: *"Get me the pricing, and if it's under $1k a month, just do it."* → **conditional
decision** (`Condition` field). Dana already approved; only a fact is outstanding.

---

> **Ana:** Remember we're not supporting IE.
> **Wei:** Right, that was decided ages ago.

❌ **Rehash.** Find the existing record, link it. Creating ENG-0087 for something logged as ENG-0009
is how a log loses its authority.

---

> **Wei:** 这个我们上次说了走方案B吧。
> **Dana:** Sorry, say again in English?
> **Wei:** We said option B last time.
> **Dana:** Ok, going with B.

⚠️ **Decision, `needs-confirmation`.** Confirms an apparently prior decision, but the original may
not be logged and the code-switch adds transcription risk. Confirm with @wei, and check whether
the earlier "option B" call exists in the log — if not, it likely needs a backdated record.

## Multi-source runs

When processing several meetings at once:

1. Extract per source, keeping provenance separate.
2. Then reconcile *across* sources before proposing. Two meetings in one week frequently touch the
   same topic, and the later one usually wins.
3. If two sources conflict and neither is clearly later, do **not** pick a winner — propose one
   record marked `needs-confirmation` that states both positions and asks which holds.
