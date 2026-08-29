# Follow-through — ageing and chasing commitments

`commitments.md` records what the user owes and what they're owed. On its own that's a filing
cabinet. This file is what makes it chase: age every row, decide which ones have gone quiet long
enough to act on, and put a drafted nudge in front of the user.

Chasing is the highest-value thing a chief of staff does and the thing people are worst at. The
user will not remember that they asked someone for a cost model eleven days ago. The ledger will.

## Procedure

1. **Read `../commitments.md`** and `../preferences.md` (VIPs, working hours, drafting voice,
   any threshold overrides).

2. **Check for silent resolution before ageing anything.** Nothing corrodes trust faster than
   being told to chase someone who already replied. For each open row, look for movement since
   the ask:
   - `workiq-retrieve` on the thread subject or the person's name, scoped to after the ask date.
   - Or `workiq-fetch` `/me/messages` with
     `$filter=receivedDateTime gt {ask_date}` + `$orderby=receivedDateTime desc`
     + `$select=id,subject,from,receivedDateTime,bodyPreview,webLink` + `$top=50`, then match
     locally on sender.

   **`from` is not index-backed — filtering on it returns `400 InefficientFilter`.** Filter on
   the date, order by date, narrow by sender in your own head.

   If it resolved: propose moving the row to the Log. Never move it silently.

3. **Age what's left.** Count **working days**, not calendar days — a Friday ask chased on Monday
   is not a three-day-old ask, and treating it as one makes the assistant look frantic.

   | Row | Nudge at | Escalate at |
   |---|---|---|
   | Waiting on — my direct report | 3 working days | 5 |
   | Waiting on — peer (internal) | 5 working days | 8 |
   | Waiting on — VIP / exec | 5 working days | 10 (and rarely — see Notes) |
   | Waiting on — external / partner | 7 working days | 12 |
   | I owe — with a due date | day before due | day after due |
   | I owe — no due date | 7 working days | 14 |

   Thresholds are defaults. If `preferences.md` sets its own, those win.

4. **Respect the last nudge.** The `Last nudge` column exists to stop the assistant becoming a
   dunning letter. **Never propose a second nudge within 3 working days of the last one**,
   whatever the age. If a row has been nudged twice already, do not propose a third — go to the
   escalation ladder instead.

5. **Apply the escalation ladder.** Each rung is a different draft, not the same message again:

   - **Nudge 1 — light.** Assume it was lost, not ignored. Restate the ask in one line, make
     replying cheap. No deadline pressure.
   - **Nudge 2 — direct.** Name the impact and propose a specific date. "I need this by Thursday
     to keep the review on track."
   - **Rung 3 — decide, don't nudge.** Two ignored asks is information. Offer the user a real
     choice: escalate to the person's manager, take it off the critical path, do it themselves,
     or drop it. **Recommend one.** Never propose nudge 3.

6. **Detect new commitments** the user made without recording them. Scan sent mail and recent
   meetings for first-person commitment language — "I'll send", "I'll get you", "let me pull
   together", "by end of week". Use `workiq-ask`: *"In my sent mail and meetings since {date},
   what did I commit to doing, for whom, and by when?"*

   Propose each as a new row for approval. **Extraction is inference, not fact** — show the
   quote and the source so the user can reject it. A wrongly-added commitment is worse than a
   missed one, because it invents an obligation.

7. **Render, then draft.** Show the card below. For each item recommended for a nudge, prepare
   the draft in the **user's voice** per `preferences.md` (Teams gets no sign-off; email gets
   the sign-off recorded there) — presented for approval, never sent. Per-item approval: "send them all" is not
   consent to four different messages to four different people. Confirm each.

8. **Update `commitments.md` on approval only.** After an approved send, set `Last nudge` to
   today's date. After a confirmed resolution, move the row to the Log.

## Render card

```
⏳ Follow-through — {n} ageing

🔴 You owe ({n})
  • {what} → {to whom} — due {date}, {n} days {overdue/left}
    {source} → {draft ready / do it now / renegotiate the date}

⏳ You're waiting on ({n})
  • {what} ← {from whom} — asked {date}, {n} working days, {nudged Nx / never nudged}
    {source} → {nudge 1 / nudge 2 / decide: escalate or drop}

✅ Looks resolved since last check ({n})
  • {what} — {who} replied {date} → move to Log?

🆕 Commitments I think you made ({n})
  • "{quote}" — {source} → add to tracker?
```

Trim empty sections. If everything is current, say so in one line — "nothing ageing" is a real
and welcome answer, not an empty run to pad.

## Unattended use

Runs as the Friday anchor and as an `ambient` scan (see `proactive.md`).

- Ambient scans **queue**, never interrupt. The one exception: an *I owe* row due **today** and
  still open clears the interrupt test.
- **Promote on threshold crossing only.** Every scan will find the same ageing rows every day;
  that's the nature of ageing. Promote a row the day it crosses its nudge threshold, not every
  day after. Dedupe with a per-rung id so a later rung can still surface:
  `commit:{row-hash}:nudge1`, `:nudge2`, `:escalate`.
- Drafts may be **prepared and stored** unattended. They are never sent and never shown until a
  human is present.

## Notes

- **Chasing upward is different.** Nudging a CVP is a judgment call, not a threshold. Prefer
  raising it in an existing 1:1 over sending a chase email — propose that instead, and say why.
  If the item genuinely blocks others, say *that* in the draft rather than the elapsed time.
- **Don't chase what the user has stopped caring about.** If a row has aged past escalation with
  no action twice, ask whether to drop it. A tracker full of dead rows gets ignored wholesale.
- **Ambiguous asks age badly.** If a waiting-on row can't be answered because the original ask
  was vague, the fix is a clearer ask, not a nudge. Say so and draft the clearer ask.
- **Working days need the user's calendar.** Their PTO and public holidays count as non-working
  for *both* sides where known. Someone on leave has not ignored the user.
- **An empty retrieve is not proof of no reply.** If the resolution check fails or returns
  nothing, say the check was inconclusive rather than reporting the item as still outstanding.
