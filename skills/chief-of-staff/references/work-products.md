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

Unattended preparation is local only. Set an explicit item/cost budget with the user before
generating in bulk; without one, prepare the single highest-priority actionable item and defer
the rest. Do not manufacture documents for informational mail.

## Office documents (Word, Excel, PowerPoint)

When the user asks for the deliverable as a real Word document, Excel workbook or PowerPoint
presentation, Work IQ's Office document tools build it from an outline you write. You write the
complete content: the document type, purpose and audience, then every heading, paragraph, bullet,
table and value in Markdown, then formatting preferences. No placeholders and no instructions to
invent content; facts you don't have stay out or are marked as unknown.

- **Desktop:** call `workiq-do_action` with `actionUrl` `/office-agent-mcp/generate_artifact` and a
  `jsonBody` string `{"appName": "Word" | "Excel" | "PowerPoint", "taskDescription": <the outline>,
  "userRequest": <their request>}`. It is created in the user's own OneDrive; give them the link it
  returns (`resource_link`). Check the result: an error result means it was not created.
- **Margo Cloud:** call `create_document` with `app`, `outline` and `request`. It is created in
  Margo's own OneDrive and shared with the manager alone, who can edit it.

Creating a document the user asked for, in their own OneDrive (desktop) or shared with them alone
(Margo Cloud), is a standing permission the user granted: no per-action approval. Everything else
about it stays per-action: sharing it with anyone else, editing a document other people use
(`/office-agent-mcp/edit_artifact`), or sending it. Create it once: if the answer is unclear or times
out, say you don't know whether it was created and do not call it again. Only in a conversation,
never in an unattended run.

Re-read changed sources before approval. Material source changes make the existing version stale.
Content approval and delivery approval are different: a local approved memo is not shared, an
Outlook draft is not sent, and a proposed delegation is not an assignment accepted by someone else.
Publishing or sending uses the specific approved Work IQ action and a real result receipt.
