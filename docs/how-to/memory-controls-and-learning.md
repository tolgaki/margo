# Control what Margo remembers and learns

Use this guide when you want Margo to remember useful context without collecting everything,
explain a recommendation, stop using an outdated fact, or turn a successful approach into a
reviewed lesson. [Semantic memory setup](semantic-memory.md) covers the optional local model.

The examples are fictional. All memory operations described here are local; an export is not
publication, and remembering a request is not accepting an obligation.

## Choose what may be captured

> Remember task-relevant observations about review preparation, but do not collect my whole
> mailbox. Show the source categories, retention periods and scope first.

Margo should show an exact policy proposal. Passive capture starts **off**. You choose the
domains, kinds, scopes and source categories; sensitive capture and usage logging have separate
switches. A change is not active until you approve that particular proposal.

To pause future capture:

> Turn off passive memory capture. Keep existing memories and my current preferences.

This changes the capture policy after review. It does not erase existing records or disable the
ordinary work ledger. "Stop remembering" is ambiguous: Margo should distinguish stopping capture,
not using one memory, and actually forgetting it.

Advanced commands, run from the installed skill directory:

```sh
python3 scripts/memory_state.py policy
python3 scripts/memory_state.py policy-preview --input PRIVATE_POLICY_JSON
python3 scripts/memory_state.py policy-set --input PRIVATE_POLICY_JSON --evidence PRIVATE_APPROVAL_JSON
```

The policy data has this shape:

```json
{
  "capture": {
    "enabled": true,
    "domains": ["user"],
    "kinds": ["episode", "project"],
    "scopes": ["review-preparation"],
    "source_kinds": ["work_source"]
  },
  "allow_sensitive": false,
  "usage_enabled": false,
  "usage_retention_days": 30,
  "retention_days": {"episode": 90},
  "review_days": {"project": 14}
}
```

This is an example, not a recommended grant for every user. The approval must use the preview's
actual subject and revision with `decision:"configure"`. Settings or target revision changes
invalidate that approval. Never create a synthetic "yes" to make the command proceed.

Passive collection uses `capture KEY --input FILE`; denied categories remain denied. The
foreground `put` command is not a workaround for a capture-policy refusal. Policy checks govern
this capture path; they do not sandbox a generally capable agent's other file tools.
Passive recapture preserves explicit `disputed` and `stale` review states. A newer observation
does not resolve a conflict by itself; returning those records to active needs a reviewed
`confirm` decision. Time-based refresh of otherwise active observations is a separate policy.

## Inspect why Margo knows something

> Why do you think Dana owns this project? Show the source, when it applied and any conflicts.

Expect evidence and scope, not a confidence score standing in for proof. Names are labels, not
unique identities. Different people with the same name must remain separate.

```sh
python3 scripts/memory_state.py inspect MEMORY_ID
python3 scripts/memory_state.py history MEMORY_ID
```

Inspection includes the current record, recent revisions, relationships, a forgetting preview,
and recent usage events if logging was enabled. Old revisions are historical, not current
instructions. Ordinary recall excludes candidates, disputed or stale claims, suppressed records,
sensitive content and incompatible environments.
Every context entry preserves its permitted uses. **Reasoning-only is not copyable recipient
text.** The panel and `search --usage drafting` can filter for drafting-permitted material;
that filtering never supplies approval to send or overrides the user's applicable constraints.

A typed relationship has dated evidence and exact endpoints. For example, a person can own a
project during a particular interval. A proposed relationship needs review; a name match or a
shared meeting does not prove it. Removing a relationship changes memory only, not the source
directory, task tracker or shared decision log.

## Use people, projects and current work together

> Prepare my next review using the project's current owner, the latest decision and the work
> still open. Tell me if any of that context conflicts.

Margo should retrieve a bounded context packet with selection reasons. It joins canonical work
records rather than saving another copy of their status. A work item that was closed in another
session must not remain open just because an older memory mentioned it.

```sh
python3 scripts/memory_state.py context --input - --routine meeting-prep \
  --work-id CURRENT_WORK_ID --depth 2 --nodes 30
python3 scripts/memory_state.py graph MEMORY_ID --depth 2 --limit 20 --routine meeting-prep
python3 scripts/memory_state.py explain MEMORY_ID --routine meeting-prep
```

For `--input -`, supply a private JSON object containing `query` on standard input.
Use `--mode lexical` explicitly when semantic retrieval is not configured. The packet separates
constraints, people/projects, current decisions, open work, applicable lessons and gaps; its
sections reference the selected entries rather than duplicating their text.

Exact work IDs are preferable to discovery. Discovery examines at most 200 recent rows per
supported operational table; it is not an exhaustive search of the whole account. Unresolved
external decisions remain revalidation gaps. A superseded decision is historical, not current.
The context includes no automatic action authority and records no usage event unless that is
separately requested under the enabled policy.

To add a reviewed relationship:

```sh
python3 scripts/memory_state.py link SOURCE_MEMORY_ID TARGET_MEMORY_ID --relation owns \
  --revision SOURCE_REVISION --target-revision TARGET_REVISION \
  --input PRIVATE_RELATIONSHIP_JSON --evidence PRIVATE_APPROVAL_JSON
```

The relationship data supplies `valid_from`, optional `valid_to`, and `source_refs`. Show both
exact endpoints and the evidence before obtaining the `relate` decision. Normal relationship
removal uses `unlink` with an `unrelate` decision. Derivation and supersession history cannot be
unlinked to conceal provenance; propose a sourced replacement instead.

## Correct, do not use, or forget

| Your request | What happens | What does not happen |
| --- | --- | --- |
| "Correct this fact." | Review a revision-bound replacement with its source and scope | Margo does not overwrite a conflicting concurrent edit |
| "Do not use this." | Suppress the record from ordinary recall while retaining its private history | The source and remembered content are not erased |
| "Do not learn from this correction." | Apply the one-off correction without saving it as a learning example | It does not erase all prior memories |
| "Forget this." | Review the affected root and derived records, then erase their content and indexes | Source messages, operational work records, earlier outputs and backups are not recalled |

For a correction or suppression:

```sh
python3 scripts/memory_state.py revise MEMORY_ID --revision CURRENT_REVISION \
  --input PRIVATE_CHANGE_JSON --evidence PRIVATE_APPROVAL_JSON
```

`PRIVATE_CHANGE_JSON` contains the full `{data,status}` replacement. Suppression uses
`status:"suppressed"` and the exact `suppress` decision. Restoring a confirmed memory requires
a new `confirm` decision; a later import cannot silently override your correction.

For forgetting:

```sh
python3 scripts/memory_state.py forget-preview MEMORY_ID
python3 scripts/memory_state.py forget MEMORY_ID --revision CURRENT_REVISION \
  --evidence PRIVATE_APPROVAL_JSON
```

The preview identifies derived memories too. Forgetting removes retrievable history and the
derived keyword/vector copies atomically. Minimal tombstones stop the same identity being
reimported. This is not a promise to recognize the same fact rewritten under a different
identity; capture policy and review still matter.

## Keep memory fresh without an always-on model

Stable preferences need not expire arbitrarily. Fast-changing project facts can have a review
period; short-lived episodes can have a retention period. The policy shows these per kind.

> Ask me to review project facts after two weeks. Retain review episodes for ninety days.
> Show what would be erased, including derived records, before I approve that policy.

A review date withholds overdue material from ordinary recall; it does not establish that the
fact is false. Approved retention authorizes local erasure when matching records become due.
Without a configured retention rule, maintenance does not automatically delete memories.

```sh
python3 scripts/memory_state.py maintain --limit 100
```

The result shows processed records, erasures, overdue reviews and a `next_cursor`. Continue with
`--after` on the next eligible run rather than running an unlimited loop. Existing schedules can
perform this bounded local maintenance; they cannot enable new categories or activate lessons.

Usage logging is also optional. When enabled, it records IDs, revisions, routine and an outcome
such as `used` or `corrected`, not another copy of the prompt or conversation. It has its own
retention period. A logged use does not prove that a recommendation helped.

## Learn an approach without granting new authority

> Keep a lesson about the query mistake we just corrected. Show what was actually exercised,
> which tool version and host it applies to, and what still needs my review.

Margo should distinguish an installed skill, a host-exposed tool, a local input-format exercise
and an actual successful execution. These are different evidence levels. A synthetic input
check can support "use this input shape"; it cannot establish that a remote operation works.
Margo never sends a message merely to demonstrate a capability.

Advanced workflow:

```sh
python3 scripts/memory_state.py capabilities --skills-dir INSTALLED_SKILLS \
  --environment PRIVATE_ENVIRONMENT_JSON
python3 scripts/memory_state.py capabilities --tools PRIVATE_HOST_MANIFEST_JSON
python3 scripts/memory_state.py validate-capability CAPABILITY_ID --revision CURRENT_REVISION \
  --evidence PRIVATE_OBSERVATION_JSON
python3 scripts/memory_state.py propose-lesson LESSON_KEY --input PRIVATE_LESSON_JSON
```

The environment names the exact `host` and `account`, plus observed versions. A host tool
manifest contains `environment`, `observed_at`, `complete`, and `tools`; each tool has `name`,
`version`, `input_schema` and `exposed`, with optional permissions and dependency versions.
Use an actual host export, not invented evidence of availability. A partial inventory cannot
establish that a missing tool was retired.

Validation accepts one of two evidence shapes:

| Evidence | Required fields | What it establishes |
| --- | --- | --- |
| Local synthetic exercise | `kind:"synthetic_observation"`, `fixture:{path,sha256}` | A pinned fixture's input satisfies a supported subset of the exposed tool schema; not remote execution |
| Existing result | `kind:"execution_receipt"`, `attempt_id` | A settled local execution receipt for the same account, capability hash and exact environment |

The synthetic fixture names `synthetic:true`, the exact `capability_id`, full environment
requirements returned by inspection, and `input`. Unsupported schema constructs fail explicitly.
An existing receipt must carry the matching capability/environment metadata when the real
result was recorded; do not fabricate or rewrite an old receipt to make it qualify.

A lesson proposal supplies `title`, `scope`, `trigger`, `goal`, `preconditions`, `procedure`,
`risk`, pinned `capability_refs`, supporting `evidence`, and `counterexamples`. Preconditions
and procedure are bounded lists. Evidence entries name the capability and observation; each
counterexample also supplies its condition. Optional feedback references must be actual reviewed
feedback that was not marked do-not-learn.

The resulting lesson stays a **candidate**. It shows success evidence, failure categories,
counterexamples, environment and an explicit limit on what the exercise proved. A failed-only
recovery proposal cannot be activated as a proven method.

```sh
python3 scripts/memory_state.py activate-lesson MEMORY_ID --revision CURRENT_REVISION \
  --evidence PRIVATE_APPROVAL_JSON
```

Only a new exact `confirm` decision activates it. Changed tool hashes or supporting evidence
make it inapplicable until reviewed again. A lesson is advisory context, not executable code,
an instruction overriding the user, or permission to install skills or invoke write tools.

## Review patterns without manufacturing trends

> Show whether preparation gaps are recurring, with the observation window and denominator.
> Do not count repeated reports of one meeting as independent events.

Trend candidates carry dated events, source evidence, independent-event groups, an observed
population and coverage. By default they require complete coverage and at least three independent
matches. The supported types concern preparation, focus fragmentation, workstream dependencies,
draft edits, tool failures, retrieval latency and notification value, not colleague performance.

```sh
python3 scripts/memory_state.py trend-definition meeting_preparation --input PRIVATE_DEFINITION_JSON
python3 scripts/memory_state.py trend PREPARATION_PATTERN_KEY --input PRIVATE_EVENTS_JSON
python3 scripts/memory_state.py consolidate --limit 10 --scan-limit 100 \
  --environment PRIVATE_ENVIRONMENT_JSON
```

A definition contains `min_independent_events`, `window_days`, `min_population`, `min_coverage`
and `require_complete`. The first command previews it; add the exact `configure` evidence only
after approval. If partial coverage is explicitly allowed, rates describe the observed population
only and remain qualified candidates. Missing events are never treated as zero.

Each resolved event has `id`, `independence_key`, `occurred_at`, `matched` and `source_refs`.
Supply the overall window, population and coverage too. The caller must resolve cross-source
duplicates; Margo does not claim that an arbitrary event label proves independence.
Observed windows must already have ended. For "this week so far", use the current time as the
end and label the interval accordingly; future planning belongs in calendar/capacity routines,
not a supposedly complete observed trend.

Consolidation proposes review of candidates, exact duplicates, stale lessons and capability
failures. It neither merges facts nor activates lessons. The result includes continuation and
deduplication limits: exact duplicate detection is local to the bounded page, not a global
semantic merge. It does not launch a background model loop.

After the exact proposal page has actually been made available through the routine's output
delivery protocol, `record-consolidation --input PRIVATE_PLAN_JSON` records that delivery.
Preparation alone is not delivery, and delivery is not proof of human reading. Replaying the
same unchanged page is harmless; changed evidence requires a fresh proposal. Unchanged evidence
is not surfaced repeatedly just because another schedule ran.
Material corrections to a rate, population or coverage are surfaced again even if the positive
event IDs are unchanged. A wording-only edit is not new evidence.

## Recover safely from a backup

Keep the minimal deletion journal with your protected backups:

```sh
python3 scripts/memory_state.py tombstones
```

Save that output privately. After restoring an older database, do not resume recall until the
newest journal has been reconciled:

```sh
python3 scripts/memory_state.py tombstones-preview --input PRIVATE_JOURNAL_JSON
python3 scripts/memory_state.py tombstones-restore --input PRIVATE_JOURNAL_JSON \
  --evidence PRIVATE_APPROVAL_JSON
```

The exact approval decision is `restore-erasures`. The preview names resurrected records before
the restore erases them again. An old backup cannot discover deletions made later: if the latest
journal is unavailable, say that recovery is incomplete rather than promising forgotten facts
cannot return. Use a SQLite-aware backup and pause writers during schema migration or restore;
copying a live database file alone may omit its journaled transactions.

## Export a generic lesson, not a personal history

> Turn this accepted lesson into a generic recipe. Remove personal and workplace details,
> show every word, and save it privately only after I approve.

Only an active, confirmed, non-sensitive lesson is eligible. Margo authors a separate recipe
with `title`, `goal`, `preconditions`, `steps` and `limitations`. Source identifiers, account
metadata and raw history are not part of the exported format.

```sh
python3 scripts/memory_state.py export-preview MEMORY_ID --input PRIVATE_RECIPE_JSON
python3 scripts/memory_state.py export MEMORY_ID --input PRIVATE_RECIPE_JSON \
  --evidence PRIVATE_APPROVAL_JSON --out PRIVATE_NEW_FILE
```

The preview binds the exact recipe and current memory revision; the decision is `export`.
Obvious identifiers, URLs, paths and credential markers are refused. Automated checks cannot
recognize every private detail, so your review is still required. The command creates a new
private file and refuses to overwrite an existing one.

The recipe has not been published, installed as a skill or executed. Any of those is a separate
action. Previously exported copies are not erased by forgetting the original memory.

## If something goes wrong

| What you see | Meaning and safe response |
| --- | --- |
| Migration required | Pause writers, back up, run the explicit migration and inspect status; never reset the database |
| Not initialized | Confirm the account and explicitly run `memory_state.py init`; inspection does not create storage |
| Capture denied | The category or source is outside the approved policy; review scope rather than bypassing it |
| Semantic runtime unavailable | Install the optional local runtime explicitly, or request lexical retrieval and label that limitation |
| Imported preferences changed | Keep the constraint block; restore/correct the private file, then preview and approve a fresh preferences import |
| Blocked index job | Inspect the reported ID and deterministic size limit, split/revise or explicitly forget the record, then retry indexing |
| Local source exceeds 1 MiB | Use a bounded revisioned excerpt or provider evidence; do not trust a truncated file hash |
| Conflicting or overdue context | Inspect the source and resolve/revalidate it; do not silently select the newest text |
| Revision conflict | Reload the current record and review the actual change; old approval cannot authorize a new revision |
| Mandatory context exceeds the budget | Increase the budget deliberately or narrow the task; do not omit a prohibition |
| Export refused | Remove identifiers or stale/sensitive material, then obtain approval of a fresh preview |

Private does not mean encrypted or processed only on this computer. SQLite and vectors depend
on OS/disk/backup protections. Selected context reaches the configured Copilot/model service;
Work IQ and other connected services retain their own data-handling boundaries.
