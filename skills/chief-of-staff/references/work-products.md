# Prepare the work

When an actionable item merits preparation, produce the useful first version rather than only
recommending that the user write it. Use the artefact commands in `work-ledger.md` so each version
is attached to the work item and the source revisions that support it.

| Product | Minimum useful content |
|---|---|
| Decision memo | Decision needed, context, options, trade-offs, recommendation, unresolved evidence |
| Document comparison | Documents and versions, material differences, consequences, open questions |
| Status update | What changed, evidence, blockers, owners and dates when stated, decisions needed |
| Meeting agenda | Purpose, decisions, ordered topics, preparation sources, carried-forward asks |
| Delegation brief | Outcome, scope, proposed owner, constraints, acceptance criteria, unresolved dates |

## Procedure

Retrieve `work-products` context under `memory.md`, scoped to the actual audience and linked
people/projects. Explain material preferences or decisions used. Do not copy private reasoning
context into the deliverable merely because it was recalled.

Read the work item, exact current sources, intended audience, and relevant user voice rules.
Use `drafting.md` or `exec-followup.md` where appropriate. Respect sensitivity restrictions;
never copy protected substance into a more shareable document. Separate facts, inferences, and
recommendations. Unknown dates and owners stay unknown.

Prepare Markdown first unless the requested deliverable calls for a specialised document skill.
Store the private content, evidence links and revisions, purpose, audience, and missing inputs.
Use the action desk to review the actual artefact, not just a title announcing it exists.

For an explicitly requested user-facing file, read `margo_store.py profile-show` and resolve
the new output filename with `workspace-path` as described in `state-operations.md`.
Use that absolute path for document-producing tools, never cwd or the repository as a fallback.
An existing private Markdown artifact can be exported with `work_state.py artifact-export`,
binding both artifact revision and profile revision. Export is an explicit foreground action,
not a side effect of preparation. A synced folder may expose the file according to its own
sharing configuration; review destination/content policy first. Never put runtime SQLite,
credentials, caches or a second tracker in the work folder.

Unattended preparation is local only. Set an explicit item/cost budget with the user before
generating in bulk; without one, prepare the single highest-priority actionable item and defer
the rest. Do not manufacture documents for informational mail.

Re-read changed sources before approval. Material source changes make the existing version stale.
Content approval and delivery approval are different: a local approved memo is not shared, an
Outlook draft is not sent, and a proposed delegation is not an assignment accepted by someone else.
Publishing or sending uses the specific approved Work IQ action and a real result receipt.
