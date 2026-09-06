# Architecture and state ownership

Margo is an agent persona and procedural skills running in a capable host. Python helpers
preserve state; they do not replace the model, authenticate the human, or sandbox host tools.
Microsoft 365 operations use Work IQ after the applicable approval.

```text
User request / scheduled manifest
  -> skill router and selected procedure
  -> bounded task plan, scoped memory and current source evidence
  -> private work / proposed action
  -> exact foreground approval when required
  -> fresh preflight and one execution claim
  -> actual result receipt, reconciliation and user-facing outcome
```

## Owners

| Surface | Authority | Code / procedure |
| --- | --- | --- |
| Persona | Who speaks; never the user's draft voice | `agents/margo.agent.md` |
| Routine selection | Trigger-to-procedure routing | `skills/*/SKILL.md` |
| Preferences | Human-readable private settings; imported memory is attributed to the file revision | `preferences.md`, `references/memory.md` |
| Storage identity | Explicit configured principal and private per-account SQLite path | `margo_store.py` |
| Evidence and obligations | Source revisions, candidate/confirmed work and canonical tracker links | `work_ledger.py`, `work_state.py` |
| External-action consent/results | Exact proposal revisions, approval invalidation and execution receipts | `work_ledger.py` |
| Productivity | Outcomes, meeting lifecycle, feedback/rules and private artifacts | `work_productivity.py` |
| Collection and delivery | Per-source attempts/checkpoints, leased queues and output receipts | `proactive_state.py` |
| Memory | Scoped claims, provenance, lifecycle, capture/retention policy and derived indexes | `memory_store.py`, `memory_governance.py`, `memory_search.py`, `memory_context.py` |
| Agent learning | Evidence-scoped capability/lesson/trend candidates, not executable self-modification | `memory_learning.py`, `memory_consolidation.py` |
| Dream reflection | Opted-in current-session checkpoints, sourced episodes and candidate interpretations; existing memory provenance and task claims | `memory_dream.py`, `memory_state.py`, `references/dream.md` |
| Task progress | Bounded runs, step claims, budgets, pause/cancel/recovery and links to the owners above | `task_runs.py`, `task_state.py` |
| Rolling agendas / relationships | Human-editable private discussion surfaces, not duplicate obligation stores | `references/one-on-ones.md`, `references/relationships.md` |
| Team decisions | Approved shared-log records and supersession chain | `skills/decision-log/` |
| Schedules | Names, cadence and unattended prompts | `automations/*.md` |
| Optional UI | Thin review/read surface, no independent credentials or database | `.github/extensions/margo-action-desk/` |
| User feature inventory | User outcomes, availability, guide and scenario mapping | `docs/feature-catalog.json` |

Python filenames above live in `skills/chief-of-staff/scripts/`. Reference filenames are under
that skill unless another location is given. The account's operational database lives outside
the checkout and survives uninstall.

## Task runs do not replace work

A task run tracks *this attempt to help*: its goal, bounded plan, evidence collection and
progress. A confirmed work item remains an obligation across many runs. A run may finish
preparing a draft while its linked action is still awaiting a decision.

Task steps link exact action IDs/revisions/hashes and claim execution through the existing
approval journal. They never copy or mint approval authority. Unknown effects remain unknown
until a real reconciliation receipt exists. Cancellation stops future work; it cannot unsend
or undo earlier effects.

Read-only views open existing state without initialization. Initialization and schema changes
are explicit local operations. Each namespace validates its schema and fails closed on missing
history; adding a task namespace is not permission to reset work or memory.

## Transaction and provider boundaries

Short local transactions protect claims, revision checks, budget reservations and receipts.
Nested calls preserve the outer transaction through savepoints. Remote reads, model reasoning
and external execution happen outside those transactions.

The agent resolves actual provider identity and availability before consequential work. A
configured account string or an environment snapshot is an audit binding, not authentication.
Collection coverage records exact windows and limitations; missing/denied sources are not
empty ones.

## Evidence classes

| Evidence | What it can establish | What it cannot establish |
| --- | --- | --- |
| Core scenario using synthetic providers | State, ordering, budget and recovery behavior through the real APIs | A model's judgment or live tenant compatibility |
| Procedure contract | Router coverage, documented boundaries and a well-formed scenario | That the model followed the procedure |
| Recorded synthetic model trace | Observed behavior for that host/model/skill/fixture revision | Universal reliability or live account consent |
| Live pilot | Scoped real-world behavior when separately authorized | Permission to publish workplace fixtures or transcripts |

See [the agent workflow](agent-workflow.md), [feature reference](../features.md) and
[trust and safety](../safety.md).
