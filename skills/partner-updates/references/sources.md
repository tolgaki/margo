# Retrieval — where partner news actually comes from

## Order of operations

**Read the vault before you call Work IQ.** A shiproom note already written by the meeting-note
discipline is better evidence than re-deriving the same meeting from a transcript: it has been
structured, and it may have been human-corrected. Work IQ is for what the vault doesn't have yet.

1. `Meetings/{Shiproom}/` and `Meetings/{Advisory Board}/` — newest notes first
2. Work IQ, for anything after the newest vault note
3. Existing partner notes — to know what you already knew

## Batch by source, not by partner

⚠️ **The expensive mistake is looping the roster and searching Work IQ once per partner.** That is
21 retrieval passes for a week where one shiproom transcript covers a dozen of them, and it will
throttle before it finishes.

Do it the other way round:

```
for each source since the earliest last-swept:
    read it once
    fan out its content to every partner it mentions
```

One shiproom read updates twelve notes. Then chase only the partners that source didn't cover.

## Tool choice

| Need | Tool | Notes |
|---|---|---|
| Known meeting/message by ID | `workiq-fetch` | Always `$select`; `$top` on collections |
| Citable hits across chats, mail, docs | `workiq-retrieve` | Returns `webLink` — needed for sourcing |
| "What was decided about X in meeting Y" | `workiq-ask` | Synthesis only; verify specifics before writing |
| Confirming a meeting happened at all | `workiq-call_function` on `calendarView` | Caps at 100, throttles on concurrency |

⚠️ **`workiq-ask` returning `NO MEETING FOUND` is not proof the meeting didn't happen.** Observed
27 Aug 2026: `ask` returned `NO MEETING FOUND` for the 26 Aug shiproom while the calendar showed
the event and `retrieve` found the recording. **Always cross-check `calendarView` before recording
a meeting as not-held.** One negative from one tool is a retrieval miss until corroborated.

## Known Work IQ limits

- `$skip`, `contains()` filters and `/instances` are unsupported.
- `calendarView` caps at 100 results and throttles on concurrent requests.
- The service returns 500s for a few minutes at a time.
- Meeting titles in transcripts are frequently **paraphrased** — verify names and dates against the
  calendar, not against the summary.

**Narrow the window, batch small, retry.** Never convert a failed call into a "no update" finding.

## Search terms per partner

Alias table is in `config.md`. Beyond the name itself, these phrasings carry status:

| Looking for | Phrases that carry it |
|---|---|
| Stage change | "public preview", "GA", "private preview", "shipped", "rolled out", "worldwide", "MSIT" |
| Date movement | "slipping", "pushed to", "targeting", "by end of", "before the train", "blocker for" |
| Blockers | "blocked on", "waiting on", "needs a decision", "escalate", "one-pager", "gate" |
| Ownership | "taking point", "owns", "DRI", "no owner", "TBD", "interim" |
| Health downgrade | "red hot", "at risk", "not going to make", "concern", "surprise" |

💡 **Ownership language is worth catching precisely.** In one meeting a partner's inventory changed
owner twice in three minutes — {A} asked {B}, {C} redirected to {D}'s workstream, {E} then said
"I'll take point on this." Only the last one is the owner of record. Read the
whole exchange before recording an owner; the first name mentioned is usually wrong.

## Sourcing what you write

Every Recent updates entry needs a source good enough to re-check:

```markdown
- **2026-08-26** — Shiproom. {what changed}. {who said it}.
- **2026-08-25** — Teams, {sender} → {recipient}, 3:27 PM PT. {what changed}.
- **2026-08-22** — Provider one-pager. {what changed}.
```

Prefer a durable `webLink` from Work IQ plus the decisive quote. When the source has no stable URL,
cite title + date + the decisive line, and say the link is unstable so someone can attach a real
one later.

⚠️ **The date in the entry is the date of the *source*, never the date you ran the sweep.** A
Thursday sweep processing Wednesday's shiproom writes `2026-08-26`, not `2026-08-27`. Getting this
wrong makes the timeline lie in a way that's very hard to spot later.

## Distinguishing "nothing" from "not yet"

The most important judgement in a sweep.

| Situation | Signal | Action |
|---|---|---|
| Searched, source exists, partner not mentioned | Real absence | "No update found" · advance `last-swept` |
| Source not indexed yet (Wed shiproom at 5pm) | Timing | ⏳ Pending · **leave `last-swept`** |
| Call failed / 500 / throttled | Infrastructure | ⏳ Pending · **leave `last-swept`** · retry |
| Meeting was scheduled but not held | Real absence, and it's information | Note it; a not-held meeting is a finding |

Leaving `last-swept` alone on a pending result is what makes the next run re-search that window.
Advancing it prematurely is how a partner silently drops out of coverage.
