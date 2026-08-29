# Triage — what counts as a partner update

A sweep surfaces far more material than belongs on the board. This is the filter.

## The test

A partner update **changes what someone would do or believe about that partnership.**

If a reader who checked the note last week would act differently after reading it, it's an update.
If not, it's noise wearing the costume of news.

## Update, or not

| Update ✅ | Not an update ❌ |
|---|---|
| Stage moved — preview → GA, pilot → WW | The same stage restated |
| A date was set, hit, or missed | A date repeated unchanged |
| A blocker appeared, moved, or cleared | A known blocker mentioned again |
| Ownership changed, or a gap was named | An existing owner named again |
| A gate was added or removed | Progress within a known gate |
| A number that changes the picture | Routine telemetry → shiproom note |
| A partner-specific decision | A general platform decision → `decision-log` |
| Someone escalated | Someone complained |

⚠️ **The trap is restatement.** A partner gets discussed every week; almost none of that is new.
"{Partner} parity work continues" is not an update — it's the absence of one, described in a way that
reads like progress. Writing it down manufactures activity and hides that the partnership hasn't
moved in a fortnight.

**When in doubt, don't write.** A missed update surfaces again next week. A false one persists.

## Worked examples, from one shiproom

| Heard | Verdict |
|---|---|
| "{Partner} fork baseline 63% vs their ~73%" | ✅ **Update** — first hard parity number; changes the picture |
| "{Partner} integration is red hot" ({name}) | ✅ **Update** — named escalation from at-risk toward blocking |
| "{Partner} can't reuse prior compliance assessments" | ✅ **Update** — new work on the critical path |
| "{Partner} targeting MCP write in {env} 9 Sep" | ⚠️ **Only if the date moved.** Same date restated = no update |
| "{Partner} considers aligned billing a GA blocker" ({name}) | ✅ **Update** — reclassifies a known gap as a blocker |
| "~Half of REST calls degraded and not charged" | ✅ **Update**, but to the platform note, not to a partner |
| "47% WoW MAU growth, 682k credits" | ❌ Platform telemetry → shiproom note |
| "Pre-auth is the hot item" ({name}) | ⚠️ Known since 19 Aug. The **one-pager to leadership** is the update |
| "{Partner} should scale with primitive tools" ({name}) | ❌ Design principle, not a partner status |
| "No {Partner} status this week" | ✅ **Record it** — a partner falling out of the conversation is information |

💡 **Two of these are the ones people get wrong.** The "pre-auth" line *sounds* like news and
isn't — the blocker was already known; what changed is that it's going to leadership. And the
"no status this week" line sounds like nothing and is actually the most useful thing on the list.

## Routing

| Found | Goes to |
|---|---|
| A durable constraint on future work | `decision-log` skill |
| A commitment made to the user | `chief-of-staff` → `commitments.md` |
| Platform-wide telemetry | Shiproom note |
| One blocker hitting many partners | `Partners/Partners.md` § Cross-partner blockers |
| A meeting that happened with no note | Flag only — **never** write `Meetings/` from this skill |
| An action item | The partner's **What's needed now** table, if partner-specific |

### The cross-partner check

**Run this at the end of every sweep, before writing the report.** After triaging per partner, ask:
*did the same blocker appear in three or more partners today?*

If yes, that belongs in `Partners/Partners.md` § Cross-partner blockers, not buried in each note.
A nested billing problem hitting five partner surfaces at once is **one problem with five
symptoms** —
and it is only visible from the board. Each individual note reports it as a local issue, which is
precisely how a systemic blocker stays unowned.

## Confidence

| Level | Test | Action |
|---|---|---|
| **High** | Explicit, attributed, from a named source with a date | Write it |
| **Uncertain** | Ambiguous, second-hand, contradicts the note, or from a paraphrase | Stage in the review queue |

Stage rather than write when:

- The source **contradicts** what the note currently says and you can't tell which is newer
- It's a **paraphrase** — "sounds like {Partner} is going provider" is not a status
- The partner is **misidentified or ambiguous** — check `config.md` aliases first
- A **health change** rests on tone rather than a stated fact
- Two sources **disagree** — stage both, show the conflict, don't pick

### Contradictions are content, not errors to resolve

⚠️ When two sources genuinely disagree about architecture or plan, **record the disagreement.** Do
not silently pick the newer one and present it as settled.

Live example: a partner is described as onboarding **as a provider** (10 Aug) and as federating
**as a peer IQ** (17 Aug ecosystem deck). Those are different architectures with different billing,
governance and discovery consequences. The note says so explicitly, and that flag is more useful
than a confident wrong answer — it's what tells someone the decision hasn't been made.

## Absence is information

Three findings that look like nothing and aren't:

1. **No update for a partner that usually reports weekly** — it has fallen out of the conversation.
2. **A meeting that was scheduled but not held** — record it; don't write a note for it.
3. **A blocker that has been open, unchanged, for three weeks** — the *lack* of movement is the
   story, and it is invisible unless you say it.

The board's most valuable output is often what has stopped moving, not what moved.
