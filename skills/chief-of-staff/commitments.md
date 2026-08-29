# Commitments & Waiting-on — persistent tracker

> This file is the durable memory for what the user owes others and what they're waiting on.
> The Chief of Staff **reads it** during every Daily Brief, Catch-up, and EOD wrap-up, and
> **updates it only with the user's approval** — typically right after an approved send creates
> or resolves an item, and during the EOD wrap-up.
>
> Rules for maintaining it:
> - Dates are always **absolute** (2026-08-15, never "next Friday").
> - Every row carries a **source** (subject/thread/meeting name, plus `webLink` when available)
>   so the item can be verified and reopened in one click.
> - When an item completes, move it to the Log rather than deleting it; prune the Log below ~20
>   entries, oldest first.
> - Never invent or infer a commitment — a row is added only when the user made or received it
>   in a real message/meeting, or tells you directly.

## 🔴 I owe (open commitments)

| What I committed to | To whom | Due | Source | Notes |
|---|---|---|---|---|
| | | | | |

## ⏳ Waiting on others

| What I'm waiting for | From whom | Asked / expected | Source | Last nudge |
|---|---|---|---|---|
| | | | | |

## ✅ Log (recently closed)

| Item | Direction | Closed | Source |
|---|---|---|---|
| | | | |
