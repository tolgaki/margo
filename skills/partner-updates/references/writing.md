# Writing — edit discipline for partner notes

⚠️ **The vault is not a git repo.** No branch, no diff, no PR, no undo. Every edit lands on disk and
syncs to OneDrive immediately. Recovery means a human digging through version history. Everything
below follows from that.

## The rule

**Targeted section edits. Never regenerate a file.**

A full-file rewrite silently drops the footnote, the gotcha, the link someone added by hand — and
with no diff review, nobody notices until the information is needed and gone.

✅ *Append one line to Recent updates; change the health cell in At a glance.*
❌ *Rewrite the note with the new status incorporated.*

## Per-section discipline

| Section | Editable? | How |
|---|---|---|
| Frontmatter | Yes | Field-level. `updated` **only** on content change; `last-swept` every sweep |
| `## At a glance` | Yes | Cell-level. Stage, health, target date, last verified |
| `## Goal` | Rarely | Only on an explicit, sourced change of purpose. Not a status field |
| `## Where it stands` | Yes | Edit the affected paragraph. Don't rewrite the section |
| `## What's needed now` | Yes | Add rows, change Status cells. **Strike, don't delete, resolved items** |
| `## Contacts` | Yes | Add or correct people |
| `## Recent updates` | **Append only** | Newest first. Never edit or remove a prior entry |
| `## See also` | Yes | Add links |
| Footer | Yes | **Must match `updated`** |

### Resolved items in "What's needed now"

Mark them, don't remove them:

```markdown
| ~~Unblock LLM runs with the shared API key~~ | {Name} | ✅ Resolved 2026-08-28 |
```

Then drop it on the **next** edit after that. One run of visible resolution tells the reader the
blocker cleared; deleting it immediately makes the blocker look like it never existed, and the
reader who saw it yesterday now can't tell what happened.

## Write order

Do it in this order so a partial failure leaves the note coherent rather than half-updated:

1. `## Recent updates` — append the dated, sourced entry **first**. If everything else fails, the
   evidence survives.
2. `## Where it stands` / `## What's needed now` — reflect the change in the narrative.
3. `## At a glance` — update stage/health/target/last verified cells.
4. Frontmatter — `stage`, `health`, `updated`, `last-swept`.
5. Footer `*Last updated:*` — must equal `updated`.
6. `Partners/Partners.md` — update the stage/health cell for that partner.

⚠️ **Steps 5 and 6 are the ones that get skipped.** A note whose footer disagrees with its
frontmatter, or a board index that disagrees with the note it links to, is how a reader learns not
to trust either.

## Recent updates format

```markdown
## Recent updates

- **2026-08-28** — Shiproom. Health ⚠️ → 🔴: {name} escalated the {Partner} integration to
  blocking for the 30 Sep public preview. Prior at-risk call was 26 Aug.
- **2026-08-26** — Shiproom. {Partner} fork baseline 63% vs their ~73%; gap attributed to parity,
  not quality.
```

Rules:

- Newest first.
- **Date = the source's date, not the sweep's date.**
- **Name the trigger when health changes**, and say what it changed from. "Health ⚠️ → 🔴 because
  X" is auditable; a bare new health value is not.
- One line per distinct change. Don't bundle three developments into a paragraph.
- Attribute: who said it, where, when.

### No-update entries

Only write these when a partner has been silent long enough to matter (14 days per `config.md`).
**Do not write one on every quiet day** — that's noise that buries the real entries:

```markdown
- **2026-09-10** — No substantive update since 26 Aug. Not raised in the last two shiprooms.
```

## Idempotency

Two runs in one day must not double-append.

**Before appending, check whether an entry with the same (date, source) already exists.** If it
does and the content is the same, skip. If it does and you have genuinely new detail, extend that
entry rather than adding a second one for the same source.

## Frontmatter

```yaml
---
tags: [{platform}, partners, consumer, {partner-slug}]
partner: {Partner}
partner-type: consumer
stage: Integration and evaluation
health: blocked
pm: {Name} (interim)
updated: 2026-08-28      # content changed
last-swept: 2026-08-28   # skill looked
owner: {vault-owner}
---
```

- `tags` — reuse the vocabulary in the vault `README`. A new tag must be added there too, or it
  fragments search and quietly breaks Bases views.
- `health` — one of `on-track` · `at-risk` · `blocked` · `dormant` · `unknown` (stubs only).
- `pm` — carry the qualifier. `{Name} (interim)` is materially different from `{Name}`, and
  dropping "(interim)" turns an escalation path into an owner.

## Stubs for new partners

Only sourced facts. Everything else `TBD`:

```markdown
---
tags: [{platform}, partners, {consumer|provider}]
partner: {name}
partner-type: {consumer|provider}
stage: TBD
health: unknown
pm: TBD
updated: {source date}
last-swept: {sweep date}
owner: {vault-owner}
---

# {Partner}

> ⚠️ {Your confidentiality banner — internal only.}
> 🆕 **Stub** — created from a single source on {date}. Unverified.

## At a glance
| | |
|---|---|
| **Type** | {consumer or provider} |
| **Stage** | TBD |
| **Health** | ❔ Unknown |
| **Work IQ PM** | TBD |
| **Last verified** | {date} |

## Goal
TBD — not stated in the source that surfaced this partner.

## Where it stands
{Only what the source actually said.}

## What's needed now
| Item | Owner | Status |
|---|---|---|
| Confirm this is a distinct partner and not an alias of an existing note | — | ⚠️ Unverified |
| Establish goal, stage and PM | TBD | ⚠️ Open |

## Recent updates
- **{source date}** — {what surfaced it, and where}.

## See also
- [[Partners]]

---

*Last updated: {sweep date}*
```

⚠️ **Do not fill the template to make it look complete.** A stub that admits what it doesn't know
is honest. A plausible-looking invented one is indistinguishable from a real note and will be
quoted as though it were.

## Review queue

`Partners/Review queue.md` — append-only, newest first:

```markdown
## 2026-08-28

- **{Partner}** — proposed: health 🔴 → ⚠️
  Evidence: Rafa reported the API key was provisioned (Teams, 28 Aug 9:14 AM PT).
  ⚠️ Unsure: the second blocker (billing setup) wasn't mentioned, so this may only clear one of two.
  → Confirm with {name}.
```

Each item carries: partner, proposed change, evidence with source, why you're unsure, who can
resolve it. **An item with no named resolver will sit there forever** — if you can't name one, say
so explicitly so a human assigns it.

Items unattended for 7 days get raised in the sweep report.

## Before you finish

- [ ] `updated` and the `*Last updated:*` footer match — **both bumped, both equal**
- [ ] `last-swept` set on **every** partner looked at, including silent ones
- [ ] `updated` **not** bumped on partners with no content change
- [ ] Recent updates appended, not rewritten; no duplicate (date, source)
- [ ] Dates are source dates, not sweep dates
- [ ] Health changes name their trigger
- [ ] `Partners/Partners.md` cells agree with the notes
- [ ] Tags exist in the vault `README` vocabulary
- [ ] No absolute paths, no secrets, no invented facts, no unattributed claims
- [ ] Nothing written to `Meetings/`
