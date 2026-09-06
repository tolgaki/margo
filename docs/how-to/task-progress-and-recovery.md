# Know where a task stopped and continue safely

## What this helps you do

Margo can keep a bounded multi-step task across sessions: what you asked for, what has already
been done, what evidence is available, what remains, and what needs a decision. This is task
progress, not another commitments list. A finished draft is not a sent message.

## Before you start

Complete [account setup](setup-and-migration.md) and confirm the active Work IQ identity.
The task core uses the existing private account database, Python 3.9+ and no new service.
Memory remains optional and separately controlled. The task-progress canvas requires the
optional action-desk extension; the CLI and conversation remain sufficient.

Task reads do not initialize anything. Explicit setup is:

```sh
python3 scripts/task_state.py init
```

Run commands from the installed chief-of-staff skill directory, using the same confirmed
account and private state root as other Margo commands. This command configures local state;
it does not authenticate, collect source data, send anything or enable a schedule.

## Try it

### Task progress

> Prepare my day. Keep progress so we can pick it up later. Show any missing sources and stop
> before sending or changing my calendar.

Margo prepares a bounded plan and records the source reads and private output. You do not need
to manipulate a database or know the internal step IDs.

> Where did you stop? Show what is done, what is still unknown and what can safely continue.

Expect the current task status, step outcomes, remaining budget and source gaps. An expired
claim is a recovery item, not proof that a call never happened.

### Task recovery

> Pause this task; I need to change the scope.

> Resume the paused task after checking the account, relevant memory, current sources and
> availability. Do not repeat any completed send.

Margo keeps completed work and rechecks the conditions for remaining steps. If its deadline,
budget or tool binding changed, it proposes an exact revised plan rather than quietly widening it.

> Cancel the remaining steps. I will handle this myself.

Cancellation stops future work and unused bound approvals. It does not undo a message already
sent, close your obligation, or turn an uncertain in-flight call into a known failure.

## What you will see

| State | Meaning |
| --- | --- |
| Planned / ready | Private work is described; a fresh binding and any required approval are still needed before execution |
| Running | A bounded attempt has an active claim; its actual outcome may not yet be known |
| Waiting for approval | The exact linked action is not executable under a current approval |
| Partial | Useful work exists, but some sources/steps or resource results are incomplete |
| Blocked | A prerequisite, changed source, binding, budget or deadline prevents the next step |
| Paused | Future claims stop; in-flight outcomes still need recording |
| Interrupted / unknown outcome | Recovery or a real reconciliation read is required |
| Succeeded | The current planned steps completed; delivery and obligation closure remain separate facts |
| Cancelled | No further steps should start; existing effects and uncertainty remain visible |

These are task-journal states, not a guarantee that the host has delivered a notification or
that you have read it. The optional panel shows the same records, including unknown effects
on a cancelled run.

## What needs your decision

Task creation and private preparation are not outward-action consent. Sending, posting, RSVP,
deletion, sharing and tracker changes still need their exact existing approval.

An action start checks the bound ID/revision/hash and fresh preflight through the existing
action ledger. A task token cannot approve it. A changed target or source invalidates old
approval; a timeout is not an invitation to send again.

Replanning is also explicit: review the changed remaining work, limits and binding before
applying it. It cannot rewrite completed history or hide an unresolved action.

## Change your mind

Pause when you may continue later. Cancel when no remaining step should run. A cancelled task
is not automatically resumed: create a deliberate follow-up plan if necessary.

An already claimed action may still complete after you ask to stop. Margo records that actual
result and does not imply that cancellation undid it. A rollback that sends notifications is
another external action with its own approval.

## Your data

Plans, bounded summaries, source/result references, budgets and progress history stay in the
account's private SQLite database outside the repo. The data is not application-encrypted.
Uninstall retains the account database. Cancelling does not erase history or source documents.

Claim tokens are returned once to the controller and stored only as hashes; do not paste them
into public issues or command arguments. Selected context still reaches the configured model
service, and live source requests retain their Work IQ data-handling boundary.

## If something goes wrong

| Problem | Safe response |
| --- | --- |
| Not initialized | Confirm the account and explicitly initialize; a status request never creates state |
| Source missing, denied or partial | Keep the gap visible; only local preparation explicitly allowing partial inputs may continue |
| Source or action changed | Fetch current evidence and review a new action/remaining plan, not the stale payload |
| A read failed | Retry only if its error permits it, after provider/backoff time and within remaining limits |
| Authentication or policy denial | Revalidate in the foreground; do not loop or use a sibling client to bypass the denial |
| Claim expired | Recover first; read retries are different from uncertain local/external effects |
| Unknown write result | Reconcile the actual result. Do not issue another write merely because a timeout occurred |
| Budget/deadline exhausted | Stop new work and review a revised plan if the goal still merits it |
| Host/tool version changed | Refresh the binding and review a new plan; installed or remembered is not validated availability |

## Availability and limits

Task tracking is local and opt-in through a new task/setup workflow. There is no standalone
background daemon, new agent identity, or permission to act unattended. Unattended plans may
collect and prepare; they cannot contain external execution steps.

Budgets constrain tracked claims and reported results. They are not a sandbox around arbitrary
host tools, a hard limit on all model tokens, or a latency promise. Unknown token/cost metrics
remain unknown. The controller must charge each actual tool/model operation before using it.
The source window and page/item ceilings must also be honored in the actual provider queries.

The following is an illustrative small-task budget, not a universal default or service promise:
at most 10 steps, 3 attempts per step, 3 parallel steps, 12 tool calls, 6 pages, 300 returned
items, 2 explicit synthesis calls, and 12,000 output characters. The actual deadline and lease
are explicit timestamps/durations in the displayed plan. Choose limits for the real task rather
than silently inheriting these numbers.

## Advanced reference

```sh
python3 scripts/task_state.py create TASK_KEY --input PRIVATE_PLAN_JSON
python3 scripts/task_state.py record-id TASK_KEY
python3 scripts/task_state.py list --limit 20
python3 scripts/task_state.py show TASK_ID
python3 scripts/task_state.py history TASK_ID
python3 scripts/task_state.py health
python3 scripts/task_state.py pause TASK_ID --reason "Review remaining scope"
python3 scripts/task_state.py cancel TASK_ID --reason "I will handle this"
python3 scripts/task_state.py recover TASK_ID
python3 scripts/task_state.py sync TASK_ID
```

The controller uses `start`, `charge`, `finish`, `resume`, `retry` and `reconcile` with private
JSON on `--input -` or in a private file. Exact IDs/revisions come from current `show` output;
tokens come from the actual `start` result, never a template.

Read starts return an already-created coverage attempt. Record its actual pages and completion
through the existing source APIs, then finish the task step against that same attempt.
Action starts return the one-time gated execution payload; they never perform the remote call.
Results must name real source, local-artifact or execution references, not model-invented receipts.

```sh
python3 scripts/task_state.py replan-preview TASK_ID --input PRIVATE_REPLACEMENT_PLAN_JSON
python3 scripts/task_state.py replan TASK_ID --input PRIVATE_REPLACEMENT_PLAN_JSON \
  --evidence PRIVATE_APPROVAL_JSON
```

The replacement is a complete plan; its preview binds the current run revision and all changes.
Supply the actual `replan` decision for that preview. Keep completed steps unchanged, use new
keys for changed attempted operations, and reconcile uncertain effects first.
An attempted source window cannot be changed by replanning: use a new run for a different
interval. Previously collected pages remain charged even when a read was only partially complete.
If collection finished but its task bookmark was interrupted, recovery preserves that partial
progress so an actual recovered result can settle it without another remote read.

Exact field contracts and controller procedure:
[Task runs](../../skills/chief-of-staff/references/task-runs.md).
