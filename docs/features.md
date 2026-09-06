# Feature reference

This page describes the implemented 1.2 feature set. The
[how-to guides](how-to/README.md) explain how to use it; the skill references define exact data
contracts. All examples are fictional. No private account, work history, or installation state
is distributed with this repository.

## Portable foundation

Margo runs in Copilot CLI. Python 3.9+ provides deterministic state and calculations using the
standard library; the optional Copilot app canvas requires its host's extension support. Work IQ
supplies Microsoft 365 reads and explicitly approved writes. The local Python tools do not call
outbound Microsoft 365 APIs.

| Feature | Implemented behaviour | Boundary |
|---|---|---|
| Account-scoped storage | Explicit owner configuration, private SQLite database per account, transactions and schema checks | Configuration is not authentication; verify Work IQ identity before ingestion |
| Evidence and revisions | Stable source identity, immutable evidence revisions, links, sensitivity and freshness | Search links are not provider IDs; semantic merging is not automatic |
| Commitment candidates | Exact-deduplicated ingestion and separate candidate/confirmed states | An inferred promise never silently becomes a confirmed obligation |
| Work history | Revision-checked transitions, relationship links and confirmed-only Markdown export | Export edits require review; Planner and decision logs keep their own authority |
| Local action desk | Exact target/payload proposals, revisions, defer/dismiss, list/show/history | Dismissing a proposal does not cancel a meeting or close an obligation |
| Approval journal | Human-decision evidence bound to action hash, revision, account and expiry | Records consent; does not authenticate the caller or sandbox other tools |
| Execution journal | Fresh preflight, single start per action revision, success/failure/partial/unknown receipts | Foreground agent performs the approved Work IQ call; uncertain writes are not retried blindly |
| Optional canvas | Shared-account list/detail, safe evidence links, action-payload edits, defer/dismiss and conversation-review requests | No approve/send endpoint; candidate work records are read-only in the canvas |

Use [Work ledger](../skills/chief-of-staff/references/work-ledger.md) and
[Action desk](../skills/chief-of-staff/references/action-desk.md) for schemas and commands.

## Reliable proactive operation

| Feature | Implemented behaviour | Boundary |
|---|---|---|
| Source coverage | Attempts, complete/partial/blocked results, continuation and successful checkpoints | A successful mail read does not advance calendar or Teams |
| Stable polling scope | Ordinary polling bounds remain per-attempt; fixed provider windows bind calendar delta scope | Changed capabilities/query versions invalidate incompatible tokens |
| Coverage history | Failure episodes, retry information, recovery and explicit collection retirement | A retired collection remains historical evidence, not a current health obligation |
| Leased delivery | One owner per queue batch; release/expiry enables redelivery | Another anchor cannot steal a live lease |
| Output receipts | Prepared, available, published and explicitly reviewed outputs | Local availability is not proof of host delivery or human reading |
| Standalone output | Persist a brief even when its input queue is empty | No fake queue items and no acknowledgement required |
| Doctor | Required preference fields, managed-file drift, coverage, delivery backlog and supplied host snapshots | It does not read the host database, authenticate, or silently repair configuration |
| Six scheduled routines | Morning brief, EOD, week ahead, commitment digest, hourly sweep and ambient scan | Same prompt files; app workflows and CLI wrappers have different permission enforcement |

The wrappers deny four Work IQ write tools. App workflows do not inherit those flags. Both remain
subject to the unattended contract, and neither path is a general sandbox. A local scheduler
cannot run while its machine is asleep. See [automation health](how-to/automation-health.md).

## Connected productivity routines

| Routine | Stored state and output | What still requires judgement |
|---|---|---|
| Weekly outcomes | Up to three agreed outcomes with completion criteria, effort, next step, allocation or blocker | The user chooses priorities and agrees commitments |
| Capacity | Working/busy/protected interval unions, focus gaps and minimum overflow | Normalise provider data; incomplete coverage cannot establish feasibility |
| Meeting lifecycle | Series/occurrence identity, preparation, recap-pending retries, debrief review and carry-forward | A calendar event does not prove attendance; no recap does not mean no actions |
| Explicit corrections | Exact subject revision, correction and supplied reason | A one-off edit is not automatically a permanent preference |
| Scoped rules | Proposed/active/revoked rules with supporting feedback and conflict checks | Activation needs approval; learning cannot grant permissions |
| Do-not-learn | Minimal operational opt-out marker without correction/reason text | It is not a general erasure facility for all prior records |
| Work products | Versioned private Markdown memos, comparisons, updates, agendas and delegation briefs | The agent authors the content; local storage does not create a shared document |
| Artefact delivery | Separate link to a successful execution receipt for the exact version | Content approval is not approval to publish or send |

How to use these: [outcomes and meetings](how-to/outcomes-and-meetings.md) and
[feedback and work products](how-to/feedback-and-work-products.md).

## Deployment and maintenance

The installers copy managed files by default. Link mode is for contributors and can place
personalisation inside a checkout; it does not move the account-scoped database into the repo.
Version metadata, source revision when available, and managed-file hashes make drift visible.
Customised prompts and renderer files are preserved rather than silently replaced.

Legacy notification state imports without inventing proof of delivery or successful coverage.
Reviewed commitments import separately. Original files remain available for recovery; never run
old and new state writers concurrently. Uninstall retains the account's runtime directory.

The native packages include the core and optional renderer source. Enable the canvas with
`--action-desk` or `-ActionDesk`; copying it does not reload extensions or synchronise app
workflows. See [setup and migration](how-to/setup-and-migration.md).

## Local semantic memory

| Feature | Behaviour | Boundary |
|---|---|---|
| User and agent domains | Explicit sourced facts/preferences, episodes, capabilities, lessons and trend candidates | Separate from authoritative obligations and action permissions |
| Memory lifecycle | Conditional revisions, confirmation, dispute/staleness, source/time/routine/environment filtering | Candidate/inferred content is not recalled as established active knowledge |
| Capture policy | Revision-bound review of exact domains, kinds, scopes, source categories and optional usage logging | Passive capture defaults off; source observations cannot manufacture user confirmation |
| Temporal relationships | Dated, sourced relationships with exact endpoint revisions and reviewed changes | Names are not unique IDs; memory links do not change external directories or task trackers |
| Hybrid search | Exact context, SQLite keyword search, local embeddings, rank fusion and bounded relationship expansion | Similarity is relevance, not proof of truth |
| Context packets | Applicable mandatory preferences plus relevant recalled evidence within a character budget | Mandatory overflow blocks instead of silently dropping a constraint |
| Reuse permissions | Context and graph entries retain permitted uses, sensitivity and recipient-copy eligibility; UI/CLI can filter drafting-permitted content | Reasoning context and a draft-purpose filter never grant permission to send |
| Connected context | Exact people/project identities, bounded graph traversal, canonical work joins, current-decision resolution and selection explanations | Recent-row discovery is bounded, not exhaustive; unresolved external decisions require revalidation |
| Rebuildable indexes | Revision/content/model fingerprints and transactional index jobs | Old vectors never override changed or forgotten authoritative records |
| Local encoder | Pinned MiniLM ONNX artifacts, explicit download, CPU inference with a private optional runtime | No implicit download, remote code or cloud embedding fallback |
| Capability evidence and lessons | Installed files, host-exported tool schemas, pinned local input exercises, existing execution receipts and reviewed environment-scoped recipes | Input-contract validation is not remote execution; neither level grants permissions or proves general competence |
| Trend candidates | Reviewed thresholds, bounded windows, independent event groups, population and explicit coverage | Rates apply only to the observed population; no automatic behavioural activation or colleague performance scoring |
| Consolidation proposals | Bounded exact-duplicate/candidate/failure review pages with once-per-evidence delivery markers | Page-local dedupe is not a global semantic merge; availability is not human review |
| Forgetting | Remove retrievable memory/history and derived local indexes; retain minimal tombstones | Does not delete source mail, work records, prior outputs or backup copies |
| Suppression and retention | Do-not-use preserves history; reviewed per-kind retention erases due records in bounded batches | Suppression cannot be undone by passive recapture; no retention grant means no automatic erasure |
| Deletion recovery | Reviewed tombstone reconciliation prevents a restored older database from reviving known deletions | The latest private deletion journal must be available; a backup cannot infer later deletions |
| Controlled recipe export | Exact approval of a newly authored generic lesson, saved to a new private file | Identifier checks are heuristic; no automatic publication, skill installation or execution |
| Memory canvas | Inspect, search and request a foreground correction/forgetting decision | Read-only backend; no approval or erasure endpoint |

Use [semantic memory](how-to/semantic-memory.md) for setup, seeding and actual CLI commands.
Use [memory controls and learning](how-to/memory-controls-and-learning.md) for user-facing
capture, correction, retention, recovery and export recipes. Memory schema version 1 requires
an explicit migration to version 2; normal reads never upgrade it silently.
Read commands open existing SQLite state read-only and do not create storage or indexes.
An absent optional encoder is reported separately from core health. Known invalid legacy index
jobs are reported and isolated, not allowed to block valid work; transient runtime failures stay
retryable. Local file freshness checks are capped at 1 MiB and refreshed after inference.

## What this is not yet

There is no model-weight training, automatic public lesson publication, cross-account knowledge
sharing or multi-device memory synchronisation. Permission-aware recall is conservative; sensitive
records are excluded rather than assuming cached access remains valid. The user still agrees
capture scope, preferences and lessons. Margo does not infer a complete biography from a mailbox.

The agent's skills orchestrate collection, reasoning and external execution. There is no standalone
background service that replaces Copilot, no new agent identity, and no unattended permission to
send messages. These limits are deliberate and apply equally to the CLI and optional canvas.
