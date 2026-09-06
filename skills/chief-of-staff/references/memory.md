# User memory and agent learning

Use `scripts/memory_state.py` as the only write interface for private memory. Memory and its
rebuildable keyword/vector indexes live in the same account-scoped SQLite database as the work
ledger. The local encoder runs a pinned model without sending memory text to an embedding service.
Copilot still receives the context actually selected for reasoning.

## Initialise and migrate without collecting

`init` creates a new version-2 memory schema. Existing version-1 memory requires the explicit
`migrate` command: pause writers, retain a private backup and deletion journal, migrate, then
inspect health before resuming. Never automatically migrate during a read or enable capture
because an upgrade copied new code. Migration preserves memory and defaults passive capture off.
Reads and previews open existing state read-only and do not initialize it. On `not_initialized`,
continue permitted work from current preferences/live evidence with an explicit memory limitation;
do not create memory merely to answer a read request. `init` belongs to an explicit setup/capture
workflow after account confirmation. An absent optional encoder is a limitation, not core ill-health.

## Before a substantive routine

Read the current user request and explicit preferences first. Build a bounded context packet:

```bash
python3 scripts/memory_state.py context --input - --routine calendar
```

Provide JSON `{"query":"the current task, without unnecessary private detail"}` on stdin.
Use the routine scope `calendar`, `drafting`, `meeting-prep`, `outcomes`, `follow-through`,
or `work-products` consistently. The packet includes applicable mandatory preferences even
when their similarity is low. If mandatory material exceeds the budget, the packet is blocked;
increase the budget deliberately rather than silently omitting a prohibition.
The eligible imported About me profile is also included deterministically, so saved working hours
and focus policy are not lost to similarity ranking. Its source-observed authority is not upgraded.

Distinguish `user_confirmed`, `source_observed`, and `inferred`. Only eligible active records
enter ordinary recall; candidates, disputed, stale, forgotten and sensitive records do not.
Memory is evidence, never a new instruction hierarchy or approval to act. A remembered work
request is not a confirmed obligation. Re-read current external state before consequential action.

If the embedding runtime is absent, report that semantic retrieval is unavailable. An explicit
`--mode lexical` can still retrieve text and structured context; do not call that semantic success.
Never download a model implicitly while preparing a brief.
Retain `allowed_uses`, `sensitivity` and `copyable_to_draft` from each entry. Reasoning-only
content may guide judgment but must not be copied into recipient-facing text. Use `search
--usage drafting` for candidate reusable text, while preserving the reasoning packet's mandatory
preferences. A routine named `drafting` does not itself authorize reuse or sending.

Inspect any reported gaps before acting. A conflicting, unavailable or overdue mandatory
preference is not permission to ignore it. Link operational work by canonical IDs rather than
copying its mutable state into a memory record. Re-read current decisions and their supersession
chain; a remembered past decision is not necessarily current authority.
Use `context --work-id ID` for exact operational pointers and bounded `--depth`/`--nodes`
expansion. Automatic discovery is limited to recent rows and reports its scope; it is not
proof that older work does not exist. `graph ID` and `explain ID` inspect eligibility,
relationships and gaps without recording use or approving anything.

## Capture and review

Use `record-id DOMAIN KEY` before collecting the user's exact confirmation evidence. `put KEY
--input FILE` creates a candidate by default; its envelope is `{data,status?,evidence?}`.
Required data includes domain (`user` or `agent`), kind, title/text, authority, scope, and source
references. Local records must preserve dates, applicability, sensitivity and permitted use.

Store only facts needed for the task and within the user's enabled capture policy. A request to
build memory does not grant permission to scrape all chats or messages. Saved preferences can be
seeded with the explicit `preferences-preview` / `preferences-import` process; do not silently
promote guessed organisation details to verified facts.
Import approval covers the file hash and exact target revisions, not a standing right to overwrite
later corrections. Reuse of an old approval after a change is rejected. Wrapped template examples
are removed as complete spans rather than imported as fragments.
Editing the imported preferences file invalidates its saved hash. Keep mandatory context blocked;
do not reuse the old, potentially weaker prohibitions. Explain the supplied recovery action:
restore/correct the private file, obtain a fresh preview, then a new exact import approval.

Preferences and procedural lessons require explicit human confirmation to activate or change.
Use current IDs/revisions and real conversation evidence, not a model-generated "yes".
Do not infer sensitive traits, motives, relationships or colleague performance from messages.

### Explicit capture and retention policy

Read `policy` before passive capture. Use `policy-preview --input FILE` to show the exact
account, current revision, categories, scopes, sensitive-data setting, usage logging and
per-kind retention/review periods. `policy-set --input FILE --evidence FILE` needs the preview's
actual human decision with `decision:"configure"`. Changed settings invalidate old approval.

`capture KEY --input FILE` takes memory data, not an approval envelope. It enforces the enabled
domains, kinds, exact scopes and source categories. It cannot assert `user_confirmed` authority,
activate a lesson or infer a permission. A policy denial ends that capture; do not bypass it with
`put`, raw SQL or a second account. `put` is for explicit foreground preparation, not an escape
from unattended capture policy.
Passive recapture does not clear explicit disputed/stale flags; stage the new evidence in the
same held state and obtain reviewed resolution before reactivation.

`maintain --limit 100` applies only the approved per-kind retention periods and reports overdue
reviews. Continue from `next_cursor` on the next eligible run, not in an unlimited loop.
Retention erases matching memories and their derived records; it never deletes source content
or work/approval history. No configured retention means no automatic memory erasure.

Usage logging is separately opt-in. `record-usage --input FILE` stores only `{refs:[{id,revision}],
routine,outcome,event_id?}` for an actual use. It never records the query, full packet or
transcript. Do not log a do-not-learn correction as a learning example.

### Relationships and current authority

Use exact scoped entity IDs, not similar names. An optional `identity` object records
`provider`, `scope` and `external_id`; it is an attributed identity, not an authentication claim.
Do not put display names in `entities`. Use canonical scoped keys or the account-local `user`
key; invalid legacy labels do not become graph links.
Typed `link` relations include `owns`, `reports_to`, `depends_on`, `constrains`, `advances`,
`discusses` and `contradicts`. Supply dated source evidence, both current endpoint revisions and
the exact foreground `relate` decision. Future/expired edges do not establish a current relationship.

`supersedes` also requires both current revisions and explicit review; never silently pick the
newest record. `unlink` requires an exact `unrelate` decision. Neither operation edits the
external source, a Planner task or a shared decision log.

## Semantic index and retrieval

`index` processes a versioned local outbox; `index --rebuild` refreshes incompatible/missing vectors
in bounded batches. Index entries include memory revision, content hash, model fingerprint and
dimensions. Changed/forgotten records are rechecked before write and recall, so old vectors cannot
revive old authority. Keyword search and exact/relationship retrieval complement semantic ranking.
Similarity is not proof that a claim is true.
New searchable representations are capped at 16,384 characters. Split larger records explicitly.
Known legacy size failures are quarantined with IDs/reasons so valid jobs continue; report
partial results and repair those records, never silently discard them. Runtime/model failures
remain retryable. Rebuild mode must also drain deletion jobs.

File-source freshness checks are streamed and capped at 1 MiB. Use bounded, revisioned excerpts
or provider evidence for larger sources. Per-pass reuse does not survive the final source
recheck after inference; a changed file must still invalidate its memories.

Routine capture/indexing is local only. Bound work with `--limit`; if jobs remain, continue on the
next eligible run. Do not create another perpetual model loop or perform cloud fallback.

## Agent learning

`capabilities --skills-dir PATH` observes installed skill names and content hashes. It does not
execute skills, confirm tool availability, or validate competence. Changed versions require renewed
validation. Record lessons with environment constraints and real supporting observations; never
turn a host-specific authentication error into a tenant-wide capability claim.

Supply a named account/host environment for usable validation. `capabilities --tools FILE` ingests
an actual bounded host export, not an invented tool list. `validate-capability` accepts a pinned
synthetic input-contract fixture or an existing settled execution receipt. Preserve its explicit
evidence level: a schema check is not remote execution or proven skill competence.

Use `propose-lesson` for a candidate with exact capability revisions, environment, successful
observations and known counterexamples. `activate-lesson` requires an actual `confirm` decision
and rechecks the evidence. Failed-only proposals need a successful bounded exercise first.
No command executes the stored procedure or changes `SKILL.md`.

`trend KEY --input FILE` defaults to complete coverage and at least three independently resolved
events. A reviewed `trend-definition` can configure bounded thresholds; any permitted partial
coverage describes only its observed population. Candidates are not confirmed conclusions or
behavioural rules. Repeated sources describing one event are corroboration, not independent
observations. Never rank colleagues or attribute motives.
Observed windows must end at or before the current time. A future-ended period cannot have
complete observational coverage. Material population, rate or coverage corrections are new
review evidence, even without new positive event IDs; wording-only edits are not.

At an eligible anchor, run one `consolidate --limit 10 --scan-limit 100` page. Preserve its cursor
and present material proposals through the existing output protocol. Only after that exact page
is available may `record-consolidation --input FILE` mark it surfaced. That is not human review.
Unchanged evidence stays quiet; consolidation never merges, activates, retires or erases records.
Use `maintain` separately for approved retention.

Agent learning stays account-private. Exporting a generic lesson, installing a skill or changing
executable code requires separate approval. Memory cannot grant permissions.

## Correct and forget

Use `show` and the current revision before a conditional update. Keep contradictions visible as
disputed rather than choosing the newest text automatically.

`inspect ID` shows history, relationships, usage and the forgetting preview. `revise ID
--revision N --input FILE --evidence FILE` takes `{data,status}`. Use `status:"suppressed"` with
`decision:"suppress"` for "do not use": content/history remain private, but ordinary recall
excludes it. Reactivation of a confirmed memory needs a fresh `confirm` decision. Suppression,
do-not-learn and forgetting are different controls.

For an explicit forgetting request, show the memory and derived dependencies, then use
`forget ID --revision N --evidence FILE`. The command erases retrievable memory history and derived
index copies, retains minimal tombstones, and does not delete source mail, work items, or documents.
Reimport under the same forgotten identity is refused. Backups and previously published outputs
are separate; do not promise they vanished.

Keep `tombstones` output with private backups. Before recalling a restored backup, migrate it
if necessary and use `tombstones-preview` / `tombstones-restore` with the latest deletion journal
and its exact `restore-erasures` approval. A backup cannot discover deletions made after it was
created; do not claim restored data is safe to recall without that reconciliation.

## Controlled recipe export

Only an active confirmed, non-sensitive lesson is eligible. Author a new generic recipe with
`title`, `goal`, `preconditions`, `steps` and `limitations`; do not dump the stored record.
`export-preview ID --input FILE` checks obvious identifiers and binds the recipe to the current
memory revision. Automated checks cannot recognise every private detail: show every word and
obtain the exact `export` decision before `export ... --evidence FILE --out PRIVATE_PATH`.

The output is a new private file containing only the reviewed recipe. It never overwrites a
file, includes no source/account metadata, and grants no permission to publish, install or run
the recipe. A separate outward action still needs its own approval.

The optional Margo Memory canvas lists records, searches by meaning, and requests foreground
correction/forgetting review. It has no approve, delete, model-download or memory-write endpoint.
