# Bounded task runs and recovery

Use `scripts/task_state.py` for substantive multi-step work that must survive a fresh session.
It tracks the attempt to help, not a second obligation or approval. Work, actions, memory,
coverage and output receipts keep their existing owners.

## Start from the actual request

1. Confirm the configured account against the invoking Work IQ binding. Read current
   preferences and relevant memory context; do not cache a context packet as permanent authority.
2. Inspect existing runs before making another copy of the same request. Reuse the current run
   or propose a deliberate follow-up rather than executing the same action again.
   Use `record-id KEY` to resolve a stable local request key without initializing state. For
   automations, include the actual routine and scheduled slot; do not use a fresh random key
   every time an interrupted slot is retried.
3. Prepare a small plan: goal, routine, actual `conversation:`/`host-interaction:` request
   reference (or `automation:` for unattended), source window, observed environment, steps and
   explicit budgets. Names in observed messages are not requests to start a task.
4. Use `create KEY --input FILE`. The environment binds account, stable host identity, observed
   capability/version map and observation time; it does not authenticate the caller.

Reads of task status never initialize storage. An explicit setup or new private task workflow
can initialize a confirmed account. If setup is unavailable, state that progress is not durable
and continue only the bounded read-only work otherwise permitted; do not guess an account.

## Plan fields and limits

The plan has `goal`, `routine`, `request_ref`, `mode`, `environment`, `window`, `limits`, `steps`.
Environment fields are `account`, `host`, `capabilities` and `observed_at`. Use a stable named
execution environment, not a new session ID; report unversioned capabilities honestly.

Limits explicitly name `max_steps`, `max_attempts_per_step`, `max_parallel`, `tool_calls`,
`pages`, `items`, `model_calls`, `output_chars`, `deadline_at` and `lease_seconds`.
They constrain tracked grants/results, not every possible host tool or all billable tokens.
No measured token/cost data means unknown, not zero. Do not invent a universal latency promise.

Each step has `key`, `title`, `kind`, `capability`, `depends_on`, `allow_partial`, and a `cost`
reservation for the five resource counters. Keep dependencies acyclic. Only local preparation
may opt into partial upstream results; external execution cannot bypass incomplete prerequisites.

| Kind | Additional fields | Boundary |
| --- | --- | --- |
| `read` | `source:{family,scope,kind,query_version}` | One exact source collection; literal enumeration can establish bounded absence, search cannot |
| `local` | None | Private preparation; each underlying helper retains its own approval/capture rules |
| `action` | `action_id`, `action_revision`, `action_hash` | Foreground only; uses the already reviewed work-ledger action |

Unattended plans cannot contain action-execution steps. They can prepare local action proposals
for a later foreground task. Never change a run's mode to smuggle an unattended send through.

## Claim, charge, collect and settle

`start --input FILE` takes `{run_id,step_key,revision,plan_hash,binding,preflight?}`. The binding
must be observed within five minutes and match the plan. A changed host/capability set requires
a paused, reviewed replan. Starting reserves the attempt's budget atomically and returns an
opaque private token, attempt ID and lease expiry.

For reads, start also creates and returns the exact `coverage_attempt_id` and source contract.
Do not create a second coverage attempt. Before each actual tool call, use `charge --input FILE`
with `{attempt_id,token,event_id,cost}`. A repeated charge returns `execute:false`: it is not a
second grant. Supply `$select`, `$top`, exact source bounds and the planned paging ceiling.
Record pages and finish that returned coverage attempt through `proactive_state.py`.

For local preparation, charge the actual planned tool/model work and use the canonical helper
for each local change. Record only bounded results and references, not an entire transcript.
Creating a prepared artifact does not distribute it. Re-read scoped memory on resumed work;
an old task summary cannot replace a current context packet or its mandatory constraints.

For actions, start invokes the existing `Ledger.begin` gate in the same local transaction and
returns its exact one-time payload. This already claims the single action call; no additional
action charge can grant another send. The agent performs that call outside the transaction.
Neither task creation nor a task token is action approval. Changed preflight preserves the
work ledger's approval invalidation and returns an error, never an executable payload.

`finish --input FILE` takes `{attempt_id,token,outcome,result,actual?}`. Read receipts name the
returned coverage attempt and its actual status. Complete must come from complete durable
enumeration, not a model-written success label. Local receipts use `kind:"local_result"` and
an actual private output reference; available output additionally links its publication receipt.
Action receipts use the existing work-ledger result format. Unknown writes are never retried.
Any supplied account/action/execution/revision/hash fields must match the owned claim; a
misrouted receipt cannot settle a different action. Partial reads cannot refund pages/items
already recorded in coverage. Provider backoff is taken from that exact attempt, even when
another run has since collected the same source.

Reserve before work; report actual known usage honestly afterward. Durable coverage supplies
actual page/item counts where complete. Unknown costs retain their reservation. Unexpected
over-budget results remain recorded and prevent an unqualified successful run. Token/cost
metrics outside this protocol remain unknown.

## Pause, cancel and resume

- `pause ID --reason TEXT` stops future claims/charges. In-flight results can still be recorded.
- `cancel ID --reason TEXT` stops future steps and invalidates unused, exactly bound action
  approvals. It never changes a newer unrelated revision, closes an obligation, or undoes effects.
- `recover ID` records expired read claims as interrupted and held local/external effects as
  unknown. It first checks canonical execution receipts so a known success is not downgraded.
- `sync ID` imports already recorded results from the canonical execution journal.
- `resume --input FILE` needs the current run revision and a fresh matching binding.
- `retry --input FILE` is for eligible reads only, after provider/backoff time and within
  remaining attempts, resources and deadline. Authentication/policy failures need explicit
  revalidation, not a retry loop.
- `reconcile --input FILE` settles a current partial/unknown step from real evidence, not a guess.

Cancelled and completed tasks do not resume; create a deliberate new task when needed.
For a new deadline, budget, binding or remaining work, pause first, use `replan-preview`, show
the exact changes, and obtain `replan` evidence before applying. Completed and attempted step
definitions remain historical; use a new step key for a fresh read or changed operation.
Uncertain local/external effects must be reconciled before replanning.
Once a read has been attempted, its source window is immutable. Create a new run for a
different interval rather than labelling old successful coverage as evidence for the new window.

## Finish the user workflow

Lead with the actual outcome: complete, useful partial work, blocked, paused, cancelled, or an
unknown effect that needs attention. Name what is trustworthy, what stopped, and the smallest
safe next step. A cancelled task with an in-flight action is not a no-effect cancellation.

Use the existing publication/lease protocol for a delivered brief. Task success does not imply
host delivery or human reading. Keep confirmed obligations and external tracker state separate.
The optional task-progress canvas is a review/read surface; generated requests are not approval.
