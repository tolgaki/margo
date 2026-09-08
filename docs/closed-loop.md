# Closed-loop productivity

Margo's portable core now has two linked responsibilities: `proactive_state.py` records what
sources were covered and what output is available; `work_state.py` records obligations,
prepared actions, approvals, and results. Both use the same account-scoped SQLite store.
Neither script makes Microsoft 365 writes. The interactive agent performs explicitly approved
Work IQ actions and records their actual results.

Start with the [user journey](user-guide.md#5-make-the-work-survive-the-next-session) for the
user-facing flow, the [how-to index](how-to/README.md) for step-by-step recipes, or the
[feature reference](features.md) for implemented behaviour and limits. Developers can follow
the same path through the [state-ownership map](development/architecture.md).

## One thread, several distinct decisions

Suppose a meeting recap says you will send Dana a revised launch plan. This fictional example
shows why the records are connected but not interchangeable.

| Stage | What Margo keeps | What you decide |
| --- | --- | --- |
| Capture | The exact recap revision and a candidate obligation | Whether you actually accepted that commitment |
| Confirm | A confirmed work item with owner, date, and source | Whether its details accurately describe what you owe |
| Prepare | A private, versioned plan and follow-up proposal | Whether the content is ready |
| Approve | The exact account, recipient, payload, and action revision | Whether to send that specific version |
| Execute | Fresh preflight, an execution claim, and the actual result | How to handle a stale proposal or uncertain effect |
| Reconcile | Later evidence and any proposed work-item transition | Whether the obligation is resolved, not just whether a message was sent |

You can stop at any stage. A ready draft can remain unsent; a sent message can leave work still
open; a dismissed proposal does not erase the commitment. An interrupted task run points back
to these records rather than creating a second copy of the work.

## Install and upgrade

Python 3.9+ is required. From a reviewed checkout:

```bash
./install.sh --all --dry-run
./install.sh --all
```

For an unversioned installation, use that normal install rather than `update`. Do not use
`--force` to repair missing metadata: that option also overwrites personal files. Back up existing
code and state, pause affected schedules, and review managed-file customisations first.

The installer records the version, source revision when available, and managed-file hashes.
The private runtime directory is not removed on uninstall. Legacy state stays intact until an
explicit migration; do not run the old and new writers concurrently.

The action-desk canvas is opt-in:

```bash
./install.sh --all --action-desk
```

```powershell
.\install.ps1 -All -ActionDesk
```

Reload Copilot extensions after installing it. The reference checkout also declares the project
canvas. It uses the same CLI backend and private records, not a browser database. Customised
renderer files are preserved rather than overwritten silently.

## Setup and migration

Follow [State operations](../skills/chief-of-staff/references/state-operations.md) for account
configuration, source coverage, migration, publication receipts, and doctor commands.
Use an explicitly confirmed account, never one guessed from git or the local OS.

Complete working hours, focus policy, and priorities in the private preferences file. If
priorities are only broad focus areas, keep weekly outcomes unconfirmed until their definition
of done, due date, and effort have been agreed.

Read [Work ledger](../skills/chief-of-staff/references/work-ledger.md) for candidate review and
commitment import/export. Migration preserves original files and does not promote old notification
history into evidence that an obligation was confirmed or completed. `commitments.md` becomes
a compatibility view after cutover; edits are detected rather than silently discarded.

## The action desk

Read [Action desk](../skills/chief-of-staff/references/action-desk.md). Each proposal has its
work item, evidence, why now, exact target/payload, revision, and current state.

Editing recipients or content invalidates approval. The agent re-reads relevant source/target
state before executing. A timeout can leave an outcome unknown; it is not a safe automatic retry.
Partial plans retain their completed and unfinished steps.

The canvas is a review surface. Requesting a foreground review does not approve a send, and
model-invokable canvas actions do not impersonate user approval. Final explicit approval remains
in the conversation. The ledger enforces its own state transitions but does not sandbox other
tools available to the agent.

## Connected routines

| Routine | What is connected |
|---|---|
| [Outcomes](../skills/chief-of-staff/references/outcomes.md) | Agreed outcomes, dependencies, calendar capacity, and explicit trade-offs |
| [Meeting lifecycle](../skills/chief-of-staff/references/meeting-lifecycle.md) | Rolling agenda, preparation, delayed recap, debrief, and carry-forward |
| [Feedback](../skills/chief-of-staff/references/feedback.md) | Specific corrections, proposed scoped rules, activation, and revocation |
| [Work products](../skills/chief-of-staff/references/work-products.md) | Sourced memos, comparisons, status notes, agendas, and delegation briefs |

Private preparation is not publication. Confirming a commitment is not approval to send its
follow-up. Approving a work product is not approval to distribute it. Planner tasks and decision
logs keep their own canonical IDs; the ledger links to them rather than silently creating copies.

## Operational limits

An OAuth failure blocks the live bootstrap until the Work IQ connection is signed in again.
An unsupported or denied source is not an empty one. Source coverage is recorded separately
from scheduler success, and a local scheduler cannot report while its machine is asleep.

Use synthetic fixtures before enabling new behaviour, then review-only operation, then one
specifically approved action. Retain the current database on rollback; restarting a legacy writer
or blindly restoring an old snapshot could lose the intervening history.

## Memory boundary

The separate memory records now support user context and agent learning with hybrid local
retrieval, provenance, lifecycle and forgetting. They link context without replacing work records.
See [semantic memory](how-to/semantic-memory.md). Installing a skill still does not imply proven
competence; the capability observer records installation and version, not execution success.

Opt-in [Dream reflection](how-to/dream.md) adds sourced episodes and candidate interpretations
from selected checkpoints, not confirmed obligations or team decisions. Durable
[task progress](how-to/task-progress-and-recovery.md) records this attempt's plan, claims, and
limits. Neither memory nor task tracking grants external-action authority.
