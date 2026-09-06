# A complete agent-owned change

Start from a user outcome, not a directory-sized refactor. A feature is complete when its
documented workflow, core behavior, failure handling and installed surfaces agree.

## Task contract

| Field | Required content |
| --- | --- |
| User story | Who needs what outcome and why |
| Baseline | Branch/revision, existing changes, current behavior |
| Write scope | Exact owned files/directories and integration owner |
| Dependencies | Existing APIs, other changes, runtime/host requirements |
| Acceptance | Visible normal result, failure result, and approval behavior |
| Data and authority | Synthetic inputs, account boundaries, private/outward effects |
| Documentation | Feature ID, guide/section, availability and recovery notes |
| Evidence | Relevant existing commands and the kind of scenario/model evidence needed |
| Non-goals | What must not change |
| Stop conditions | Ambiguous identity, unsafe authority expansion, conflicting edits, unsupported schema |

Use the repository's agent-task issue form when delegating. Do not create an issue or post
anything merely because this document mentions one.

## Workflow

1. Read `AGENTS.md`, contribution rules and the affected feature's guide/procedure. Inspect
   actual branch and dirty files; do not overwrite another task.
2. Trace the complete path across router, data owner, CLI, optional UI and installed copy.
   Reuse existing helpers before creating a new representation of the same truth.
3. Make the acceptance scenario concrete with fictional data. Include the failure case most
   likely to fool an agent: partial input, stale evidence, ambiguity, interrupted execution,
   unavailable capability or a changed approval revision.
4. Implement the bounded slice and preserve the load-bearing invariants. Ask before a
   significant authority or irreversible data-contract decision.
5. Update catalog and help in the same change. Explain what a user asks, sees, approves and
   does when it stops. Generated navigation comes from the catalog; prose remains authored.
6. Run relevant existing checks from isolated fixtures. Preserve the difference between
   runtime proof, procedure-contract coverage and model observations.
7. Inspect the proposed change, source/data hygiene and migration effects. Hand off evidence
   and limitations. Commit, publish or deploy only when the current request authorizes it.

## Parallel work

One agent owns a vertical slice. A separate reviewer can challenge it. Assign one integrator
for schemas, routers, installers and shared APIs. Independent documentation/fixture work can
proceed in parallel once the interface is agreed; overlapping edits cannot.

Do not spawn a permanent fleet, recursively delegate without bounds, or let an implementation
agent approve its own permission expansion. Instructions are reviewed code too.

## Handoff

Report the outcome, changed surfaces, behavior/recovery evidence, guide links, compatibility
impact and unresolved limitations. Never write “done” because a stub exists, a counter is
nonzero, an optional scenario was skipped, or a model graded its own generated answer.

For optional model runs, record model, host, code/skill version, fixture version, actual tool
trace and measured usage where available. Unknown metrics stay unknown. Synthetic data may
be committed; private account artifacts may not.
