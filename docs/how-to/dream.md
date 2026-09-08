# Dream over opted-in Margo sessions

> Remember selected checkpoints from our Margo sessions in this workspace. Then Dream about
> yesterday: show what we discussed, possible decisions, alternatives and unanswered questions,
> with sources. Do not treat inferred themes as my preferences.

Dream's initial slice is **manual and opt-in**: selected current-session checkpoints → a bounded
daily reflection → sourced episodes and candidate interpretations → task-scoped next-session
context. It uses the existing account memory, work ledger, task journal and output APIs. No second
memory database, model download, background schedule, or outward-action permission is added.

## Try a small reflection before the advanced commands

1. Ask **"Can this host supply authorized checkpoints from our current Margo session in this
   workspace? Show the supported scope; do not import history."** Unsupported hosts stop here.
2. Review the exact capture-policy proposal, including episode/decision kinds and checkpoint
   sources. Opting in is not the same as importing every session.
3. During ordinary non-Dream work, choose a short observation to checkpoint. Ask to see its
   actual text, speaker and source locator; it should not be replaced with an invented summary.
4. After the chosen daily cutoff has passed, ask **"Dream about that completed day in my time
   zone. Use one bounded page; show what was omitted and keep interpretations as candidates."**
5. Review one candidate against its citation. Correct, reject or explicitly confirm it through
   [memory controls](memory-controls-and-learning.md), then retrieve only relevant context for a
   later task. A reflected "decision" is not automatically a team decision-log entry.

Expect **checkpoint-only partial coverage**, even when reflection succeeds. If no checkpoints
were saved, Dream cannot reconstruct the day from a session list. The commands below explain
the host/controller contract; ordinary users do not need to manufacture checkpoint JSON.

## Set up and opt in

Run commands from the installed `chief-of-staff` skill directory. Normal copy installation ships
the Dream procedure and Python helper; upgrade the installed copy before using these commands.
Keep the configured account database and all JSON inputs outside the checkout. Explicit setup,
only if needed, uses the existing `margo_store.py init`, `memory_state.py init`, and
`task_state.py init` workflows. Reads never initialize or migrate private state.

```sh
python3 scripts/memory_state.py dream-status --host HOST --workspace WORKSPACE
python3 scripts/memory_state.py policy
python3 scripts/memory_state.py policy-preview --input PRIVATE_POLICY_JSON
python3 scripts/memory_state.py policy-set --input PRIVATE_POLICY_JSON --evidence PRIVATE_APPROVAL_JSON
```

Use the exact account/host/workspace `scope` from status in the policy's `capture.scopes`.
Enable `domains:["user"]`, `kinds:["episode","decision"]`,
`source_kinds:["session_checkpoint","memory_record"]`. Preserve other previously reviewed
settings; do not overwrite unrelated scopes. The existing policy preview binds your actual
`configure` decision. Sensitive capture remains separately controlled; this first reflection
slice excludes sensitive checkpoints even if capture was approved.
Both `dream-status` and `dream-plan` require episode and decision capture before reporting readiness,
so missing decision support is reported before a reflection reserves model work.

This adds a source category to memory schema v2, not a new schema or migration. Existing v1
accounts still need the documented explicit memory migration. Do not downgrade writers that do
not understand `session_checkpoint`; pause writers and upgrade the installed code together.

## Checkpoint contract

The only shipped adapter is a host-supplied **current-session** checkpoint. Listing sessions
does not expose conversation contents. Historical import, other hosts' databases, automatic
cross-session discovery and full transcript ingestion are unsupported. The adapter must use
stable IDs and attest actual current-session authorization; these fields do not authenticate
a caller or grant permission to read another workspace.

```json
{
  "capability": {
    "account": "dana@example.com",
    "host": "fixture-host",
    "workspace": "fixture-workspace",
    "adapter": "margo-session-checkpoint-v1",
    "content_access": "current-session",
    "session_list": false,
    "authorized": true
  },
  "session": "fixture-session",
  "event": "event-1",
  "revision": "host-rev-1",
  "expected_revision": null,
  "timestamp": "2026-09-05T12:00:00Z",
  "speaker": "user",
  "locator": "conversation:fixture-session/event-1",
  "text": "Consider the Cedar option. Timing is open.",
  "origin": "margo-session",
  "sensitivity": "private",
  "allowed_uses": ["reasoning"],
  "entities": ["project:cedar"],
  "work_refs": []
}
```

```sh
python3 scripts/memory_state.py dream-checkpoint --input PRIVATE_CHECKPOINT_JSON
```

Each checkpoint contains at most 6000 text characters. Keep the exact selected observation,
speaker and locator, not a generated digest. `created_at`/`updated_at` record local ingestion;
`timestamp` records the event. Event identity is account/host/workspace/session/event, independent
of the opaque revision or text. A changed source revision requires the current memory
`expected_revision`. Reusing a source revision for different content fails. Distinct contradictory
events remain distinct; repeated wording does not establish independence. Use canonical work IDs
in `work_refs`, not copied task status. Names alone are not entity identity.

An unchanged checkpoint retry is a no-op after entity/allowed-use normalization, including after a
policy revision. It preserves the observation's memory revision, capture-policy provenance and review
state, but still requires current capture authorization. Do not manufacture a source correction just
to retry an unchanged observation.

Only `dream-checkpoint` creates the canonical source checkpoint. Generic `put`/`capture` references
must resolve an existing checkpoint; a matching source-shaped string or metadata label is not enough.
Legacy dangling references are excluded from recall and cleared from derived indexes by index
maintenance; inspect, correct or forget those records rather than treating them as current evidence.

Exclude Dream's own prompts, outputs and reflection runs, including feedback loops disguised as
fresh session observations. The host must enforce this origin contract: Python cannot identify
mislabelled prose or prove source access, speaker truth or entailment.

## Reflect on one daily page

```json
{
  "host": "fixture-host",
  "workspace": "fixture-workspace",
  "day": "2026-09-05",
  "timezone": "UTC",
  "cutoff": "00:00",
  "lookback_days": 1,
  "after": null
}
```

```sh
python3 scripts/memory_state.py dream-plan --input PRIVATE_WINDOW_JSON
python3 scripts/memory_state.py dream-start DAILY_PAGE_KEY --input PRIVATE_START_JSON
python3 scripts/memory_state.py dream-finish --input PRIVATE_REFLECTION_JSON
python3 scripts/memory_state.py dream-inspect SNAPSHOT_ID
```

`PRIVATE_START_JSON` contains `request` (the exact window object), `snapshot_hash` from the
preview, and `request_ref` pointing to your actual current conversation request. The local
cutoff must have completed. Explicit `UTC` works without a timezone database, including on Windows.
Other IANA zones require available timezone data; missing data and ambiguous/nonexistent DST cutoffs
fail rather than silently choosing an offset. No timezone package is installed implicitly.

The start response supplies events, coverage and a private task claim. The host may reason once
outside the database transaction. Submit that claim as follows:

```json
{
  "snapshot_id": "{snapshot-id}",
  "attempt_id": "{claim-attempt-id}",
  "token": "{private-claim-token}",
  "claims": [
    {
      "key": "open-timing",
      "type": "question",
      "text": "The timing remains unresolved.",
      "citations": [
        {"id": "{checkpoint-memory-id}", "revision": 1, "quote": "Timing is open."}
      ]
    }
  ]
}
```

Claim types are `discussion`, `decision`, `rationale`, `alternative`, `direction`, `question`,
and `hypothesis`. Every model-written interpretation stays **inferred/candidate**, including a
decision interpretation or alleged stated direction. Exact checkpoint copies become sourced
episodes, never user-confirmed decisions. Sources retain revisions, chronology, canonical
references, sensitivity and allowed-use restrictions. No automatic preference, lesson, shared
decision, supersession, obligation or external action is created.

Expect partial checkpoint-only coverage, not an all-day/all-session reconstruction. Bounds are
500 scanned memory rows, 100 events, 14000 serialized event characters, one reserved host model
call, 20 claims / 8000 claim JSON characters and 24000 output characters per run. The host must
honor the model grant; these limits do not sandbox arbitrary host tools. Claims expire after
15 minutes. The receipt records bounded execution, not evidence of model quality.

If `next_cursor` is present, use it as `after` on a separately bounded page with a new key;
chronology is ordered within each page. Page results are not independent corroboration.
`excluded_oversized` counts otherwise eligible checkpoints whose full serialized record (including
metadata and entities, not just text) exceeds 14000 characters. They remain stored but are excluded
from reflection; scanning continues to later records. An empty page can still have a continuation
cursor. If no eligible events remain, no reflection starts; inspect the saved checkpoints rather
than claiming they were reflected on or silently truncating their evidence.
Start future sweeps at `after:null` to catch changed events or late arrivals inside the explicit
0–7-day lookback. Events outside that window, missing checkpoints, unsupported history, pagination
gaps and excluded records remain limitations; “task completed” never means source coverage complete.

## Review and use next session

```sh
python3 scripts/memory_state.py inspect CANDIDATE_ID
python3 scripts/memory_state.py consolidate --limit 10
python3 scripts/memory_state.py revise CANDIDATE_ID --revision 1 --input PRIVATE_REVIEW_JSON --evidence PRIVATE_APPROVAL_JSON
python3 scripts/memory_state.py context --input PRIVATE_QUERY_JSON --entity project:cedar --environment PRIVATE_ENVIRONMENT_JSON --mode lexical
python3 scripts/memory_state.py explain EPISODE_ID --environment PRIVATE_ENVIRONMENT_JSON
```

Review JSON is the existing `{data,status}` full-record replacement. Actual human confirmation
must bind the exact ID/revision/decision before `authority:user_confirmed,status:active`.
Reject or dispute unsupported candidates rather than silently merging contradictions. Preferences,
lessons and shared decisions follow their existing review workflows.

Environment JSON includes the exact `account`, `host` and `workspace`. Use the present task query,
not “load all Dreams”; add `--work-id` for canonical live work. Context explains selection and
retains live work state. Checkpoint/snapshot plumbing and candidate, disputed, stale, suppressed,
forgotten or inferred claims do not enter ordinary recall. Lexical retrieval needs no optional
embedding installation.

`dream-inspect` separates collection, reflection completion, source validity, **prepared** output
and human review. The publication record is only a memory pointer. Use the existing output
availability receipt workflow only after the host actually makes that pointer available; output
availability or review is never consent to promote a memory. No daily schedule is shipped/enabled
until a supported host can reliably checkpoint, provide bounded content and honor claims.

## Correct, forget or recover

A changed policy/source or competing, paused, cancelled or expired task claim blocks persistence.
Re-plan under a new key; never force an old synthesis through. Exact successful finish retries are
idempotent. If a response/token is lost, inspect the task with `task_state.py show`, use `recover`
after lease expiry and cancel remaining work before starting a fresh bounded attempt. Existing
receipts are not erased. A changed payload cannot reuse a completed claim.

Overlapping runs reuse eligible sourced episodes for the exact checkpoint ID and memory revision
without recapturing them, including after a capture-policy change. Existing episode review state and
capture-policy provenance are preserved. Suppressed, forgotten, disputed, stale or review-due episodes
are withheld, never reactivated or recreated under another key; they do not block reflection on other
eligible evidence. `dream-inspect` also withholds ineligible episodes and episodes from obsolete source
revisions. Candidate interpretations remain per-run records for separate review.

Correct checkpoint source revisions, or suppress/dispute with the existing memory review commands.
Dependent indexes are invalidated immediately and old derived prose becomes ineligible, while
historical revisions stay inspectable. Still-current memories reached only through a relationship or
historical dependency are reindexed by the next ordinary index pass; they do not require a full rebuild.
Use `forget-preview CHECKPOINT_ID`, then the exact
reviewed `forget` command to erase the checkpoint's historical text and all dependent reflection
records/indexes, including interrupted snapshots and alternate memory keys. Minimal hashed
event-identity and locator tombstones prevent reimport with another key/revision, including changed
session/event IDs that retain the same host/workspace locator. They cannot detect a host
that lies about every source identity; content similarity is deliberately not deletion identity.
Erasure never deletes canonical work, source conversations, already surfaced host responses or
private backups. Export/restore the existing deletion journal when restoring backups.

The executable synthetic Dream journey tests state, citations, isolation and recovery through
real APIs/CLI. No actual host/model trace is included: reasoning usefulness, source entailment,
live host compatibility and full-session coverage are **not evaluated**.
