# Record schema & lifecycle

## Fields

| Field | Required | Values / rules |
|---|---|---|
| `ID` | ✓ | `<TEAM>-<NNNN>` — the team prefix from `config.md` plus a zero-padded 4-digit sequence (`ENG-0042`). Never reused, never renumbered after merge. |
| Title | ✓ | Short, imperative, specific. "Use Postgres for the ingestion store", not "Database discussion". |
| `Date` | ✓ | Date the decision was **made**, not logged. Backdate when logging late. |
| `Status` | ✓ | `active` · `superseded by <TEAM>-xxxx` · `reversed (<TEAM>-xxxx)` · `needs-confirmation` · `expired` |
| `Type` | ✓ | `one-way` (expensive/impossible to reverse) · `two-way` (cheap to revisit) |
| `Decision` | ✓ | One sentence, present tense, active voice. States the constraint. |
| `Why` | ✓ | The rationale. Required for `one-way`; strongly encouraged otherwise. |
| `Alternatives rejected` | — | Each with the reason it lost. Omit if genuinely none were considered. |
| `Owner` | ✓ | The person accountable for it, not whoever spoke. |
| `Affects` | ✓ | One or more workstream areas from `config.md`. Drives retrieval. |
| `Source` | ✓ | Markdown link to meeting/thread/PR. Include a short verbatim quote for non-English sources. |
| `Confidence` | ✓ | `high` · `needs-confirmation` |
| `Condition` | — | For pre-approved decisions: the unresolved condition, and who resolves it. See § Conditional decisions. |
| `Supersedes` / `Amends` | — | Prior record ID. See § Supersession. |
| `Revisit` | — | A date or trigger condition, for decisions taken under time pressure. |

### Writing a good `Why`

The rationale is the field that ages best. In a year, nobody needs to be told what the decision
was — the code says that. They need to know **what was true at the time** that made it right, so
they can tell whether it still is.

Prefer: "Cosmos RU costs were growing superlinearly with ingest volume, and our query patterns are
relational."
Avoid: "Postgres is better."

The first tells a future reader exactly which assumption to re-test.

## Lifecycle

```
proposed ──approve──▶ active ──┬──▶ superseded by <TEAM>-xxxx
   │                            ├──▶ reversed (<TEAM>-xxxx)
   │                            └──▶ expired
   └──▶ needs-confirmation ──confirm──▶ active
                            └──reject──▶ discarded (record deleted, only pre-approval)
```

A record may be deleted **only** before it is first approved, or when a human explicitly rejects a
`needs-confirmation` record. Once `active`, it is permanent — `Status` changes only, and every
other field stays exactly as written.

## Conditional decisions

Authority granted now, condition resolved later. The decision is `active` immediately — what's
outstanding is a fact, not an approval.

```markdown
### ENG-0044 — Add an Asia runner pool if monthly cost is under $1k
- **Status:** active
- **Type:** two-way
- **Decision:** A CI runner pool is added in Asia (likely Singapore) provided the cost is under $1,000/month.
- **Condition:** Pricing confirmed under $1,000/month — @wei to resolve.
- **Why:** CI takes ~40 min from China because runners are in West US and artifacts cross the Pacific.
```

When the condition resolves, don't rewrite the record. Either it proceeds as written (add a short
`Resolved:` note to `Condition`), or the condition failed and a **new** record supersedes it.
Conditional records with long-unresolved conditions surface in the audit routine — an
indefinitely-pending pre-approval is functionally an open question that nobody is tracking.

## Supersession

The most important mechanic in the log, and the reason it beats searching transcripts.

When a new decision contradicts an existing `active` one:

1. Create the new record normally.
2. Add to the new record: `- **Supersedes:** ENG-0031`
3. Edit **only the `Status` line** of the old record to: `superseded by ENG-0042 (2026-08-07)`
4. Leave every other field of the old record untouched. Its `Why` is the historical evidence of
   what you believed then.

**Supersede vs. reverse:**
- *Supersede* — the constraint changed. "Postgres" replaces "Cosmos".
- *Reverse* — the constraint is withdrawn and you're back to no decision, or to the status quo
  ante. Use `reversed (<TEAM>-xxxx)`.

**Amendment** — if the new decision only narrows or extends the old one without contradicting it,
don't supersede. Add a new record with `- **Amends:** ENG-0031` and leave the old one `active`. Both
apply. Over-superseding destroys context as badly as never superseding.

**`expired`** — for decisions scoped to a period that has passed ("no refactors until the March
release"). Not superseded, just no longer binding. An audit run proposes these.

## Storage layout

Single `DECISIONS.md` at the path in `config.md`, newest records appended at the bottom, with an
agent-maintained index at the top:

```markdown
# Decision Log

| ID | Date | Title | Area | Type | Status |
|---|---|---|---|---|---|
| ENG-0042 | 2026-08-07 | Use Postgres for the ingestion store | ingestion | two-way | active |
| ENG-0031 | 2026-06-12 | Keep ingestion state in Cosmos | ingestion | two-way | superseded by ENG-0042 |

---
## Records
### ENG-0001 — …
```

The index must be regenerated on every write. It is what makes the file scannable by a human and
cheap to load for an agent — a reader should be able to answer "what's the current call on
ingestion?" from the table alone.

Split into `DECISIONS-{YYYY}-Q{n}.md` past ~300 records, keeping one root index that spans files.
Don't split earlier; premature sharding makes retrieval worse, not better.

Markdown in a git repo is canonical because version control gives history, blame, and diffs for
free, and it's greppable by every human and agent. If `config.md` specifies a SharePoint or Teams
mirror, that mirror is a **copy** — never edit it directly, or the two silently diverge and you
have two logs and no source of truth.

## Concurrent writes

In a shared registry, two people can claim the same ID in parallel PRs. The index table makes this
a hard git conflict rather than a silent duplicate — that's deliberate.

**Resolution:** renumber *your unmerged* record to the next free ID. A record that hasn't merged
has no inbound references, so renumbering costs nothing. Never take an ID that already exists on
the default branch, and never renumber a merged record to make room — inbound references to it are
exactly what the log is for.

Before proposing IDs, always read the current default-branch log rather than relying on what you
saw earlier in the session. Someone else may have merged since.

## Cross-team decisions

Each team owns its own log and only its own log.

- **Never supersede another team's record.** If their decision needs to change, they change it.
- When another team's decision constrains yours, cite it in `Why` and link it. Your record stands
  on its own; theirs stays theirs.
- If a decision genuinely binds two teams, each logs a record and each links the other. Duplication
  is the right answer here — a single record in one team's file will not be found by the other
  team, and ownership of it will be ambiguous exactly when it matters.
