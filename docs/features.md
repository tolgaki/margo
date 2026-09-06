# Feature reference

This page describes the implemented 1.2 feature set. The
[how-to guides](how-to/README.md) explain how to use it; the skill references define exact data
contracts. All examples are fictional. No private account, work history, or installation state
is distributed with this repository.

## Full feature index

Every capability in the baseline has one stable entry in
[`docs/feature-catalog.json`](feature-catalog.json), keyed by a stable ID, with its user goal,
availability, implementation kind (`runtime` deterministic code, `procedure` a model-followed
routine, or `design` a documented but not yet built approach), and guide anchor. The table below
is generated from that catalog with `tools/feature_catalog.py --write`; edit the catalog, not this
table, and run `tools/feature_catalog.py --check` before committing either. Availability is one of
`implemented`, `optional` (needs extra configuration or an installed extra), `limited` (ships but
is currently blocked on an external condition) or `planned` (not yet built).

<!-- BEGIN GENERATED: feature-catalog -->
| Feature | User goal | Availability | Kind | Guide |
| --- | --- | --- | --- | --- |
| `containers` | Run scheduled routines in a container instead of a local machine, with the same unattended contract | Optional | procedure | [Run Margo unattended somewhere other than a laptop](container.md#running-it) |
| `automation-ambient` | Run a daily ambient scan for commitment ageing, relationship drift and stale PRs, queued for the next anchor, never interrupting | Implemented | procedure | [Get a quiet daily scan for slow-moving drift](how-to/automation-health.md#automation-ambient) |
| `automation-commitments` | Run commitment ageing and render the week's ambient digest every Friday | Implemented | procedure | [Get a weekly commitments and ambient digest](how-to/automation-health.md#automation-commitments) |
| `automation-eod` | Run the end-of-day/catch-up wrap-up on weekday evenings | Implemented | procedure | [Get an end-of-day wrap-up automatically](how-to/automation-health.md#automation-eod) |
| `automation-hourly` | Run a lightweight, usually silent hourly sweep for anything that clears the interrupt test | Implemented | procedure | [Get cheap, silent hourly checks](how-to/automation-health.md#automation-hourly) |
| `automation-morning` | Run the full daily brief automatically on weekday mornings | Implemented | procedure | [Get the morning brief without asking for it](how-to/automation-health.md#automation-morning) |
| `automation-week-ahead` | Run the week-ahead routine automatically on Sunday afternoon | Implemented | procedure | [Get next week's shape before Monday](how-to/automation-health.md#automation-week-ahead) |
| `doctor` | See configuration, managed-file drift, source health, task-run health and delivery backlog in one report, without it repairing anything on its own | Implemented | runtime | [Get one honest health report](how-to/automation-health.md#doctor) |
| `output-delivery` | Track leased delivery batches and publication receipts, separate from a workflow exiting zero | Implemented | runtime | [Know if a scheduled output was actually delivered](how-to/automation-health.md#output-delivery) |
| `source-coverage` | Track per-source attempts, completeness, retries and checkpoints so a partial read is never mistaken for done | Implemented | runtime | [Trust that a brief actually covered its sources](how-to/automation-health.md#source-coverage) |
| `catch-up` | Catch up on mail, meetings and Teams changes since a chosen point without re-reading everything | Implemented | procedure | [See what changed since you were away](how-to/briefs-and-catch-up.md#catch-up) |
| `daily-brief` | Get a bounded, cited brief of today's calendar, mail and Teams, with priorities and suggested next steps | Implemented | procedure | [Know what needs attention today](how-to/briefs-and-catch-up.md#daily-brief) |
| `end-of-day` | Wrap up the day: what got resolved, what rolls to tomorrow, and what you committed to today | Implemented | procedure | [Close the day without dropping anything](how-to/briefs-and-catch-up.md#end-of-day) |
| `calendar-hygiene` | Quantify optional hours, fragmentation and agenda-less meetings, and name the specific ones worth cutting | Implemented | procedure | [See what your calendar actually costs you](how-to/calendar-management.md#calendar-hygiene) |
| `calendar-reschedule` | Reschedule or cancel a specific occurrence, showing who is affected before anything changes | Implemented | procedure | [Move a meeting without making a mess](how-to/calendar-management.md#calendar-reschedule) |
| `calendar-rsvp` | Classify pending invitations against your standing rules and calendar conflicts, with a recommended response for each | Implemented | procedure | [Triage invitations quickly](how-to/calendar-management.md#calendar-rsvp) |
| `calendar-scheduling` | Find and propose meeting times that respect working hours and protected focus blocks | Implemented | procedure | [Find time without the back-and-forth](how-to/calendar-management.md#calendar-scheduling) |
| `action-desk` | Review, edit, defer or dismiss a prepared action proposal before anything is sent | Implemented | runtime | [See what's ready for your decision](how-to/commitments-and-action-desk.md#action-desk) |
| `action-desk-canvas` | Inspect and edit durable Margo proposals in an optional app panel instead of the CLI | Optional | runtime | [Review proposals without a second CLI window](how-to/commitments-and-action-desk.md#action-desk-canvas) |
| `approval-execution` | Approve an exact action, get a fresh preflight check immediately before it runs, and get an honest result even when the outcome is unclear | Implemented | runtime | [Approve once, safely](how-to/commitments-and-action-desk.md#approval-execution) |
| `commitments` | Capture a request as sourced evidence, then confirm it as an obligation only after you say so | Implemented | runtime | [Track an ask without inventing a promise](how-to/commitments-and-action-desk.md#commitments) |
| `follow-through` | Age every open row, check for silent resolution first, and prepare the right nudge at the right rung | Implemented | procedure | [Know what you're waiting on, before it's overdue](how-to/commitments-and-action-desk.md#follow-through) |
| `engage` | Surface unanswered questions and recurring themes from a product's Viva Engage community | Optional | procedure | [See what your Viva Engage community actually thinks](how-to/community-and-feedback.md#engage) |
| `teams-feedback` | Surface unanswered reports and recurring failure themes from a product's Teams feedback channel | Optional | procedure | [See what's breaking, from the people hitting it](how-to/community-and-feedback.md#teams-feedback) |
| `decision-answer` | Ask why something was decided and get the current record, with the supersession chain if it changed | Optional | procedure | [Get the current answer, not a stale one](how-to/decision-log.md#decision-answer) |
| `decision-audit` | List stale needs-confirmation records, unowned open questions, and dead source links for review | Optional | procedure | [Find what's unresolved in the log](how-to/decision-log.md#decision-audit) |
| `decision-digest` | Produce a short digest of what was decided, what changed, and what's still open this week | Optional | procedure | [Get a short weekly read of what changed](how-to/decision-log.md#decision-digest) |
| `decision-extraction` | Extract genuine decisions from a meeting or thread, separating them from discussion, tasks and open questions | Optional | procedure | [Log what a team actually decided](how-to/decision-log.md#decision-extraction) |
| `decision-supersession` | Mark a prior decision superseded or reversed, keeping both records and the chain between them | Optional | procedure | [Change your mind on the record](how-to/decision-log.md#decision-supersession) |
| `document-queue` | Surface shared documents worth reading, tied to a person, meeting, deadline or commitment, and age out the rest | Implemented | procedure | [Know what to read before it's too late](how-to/documents-and-files.md#document-queue) |
| `file-copy` | Copy a large file between Microsoft 365 locations without pulling bytes to this machine | Limited | runtime | [Copy a file server-side within Microsoft 365](how-to/documents-and-files.md#file-copy) |
| `file-download` | Download a file over the 4 MB MCP transport limit straight to local disk without flooding the conversation | Implemented | runtime | [Pull a large file from Microsoft 365 to disk](how-to/documents-and-files.md#file-download) |
| `file-sharing` | Grant a specific person read access to a file, without sending an email notification unless you ask for one | Implemented | procedure | [Share a file without emailing the bytes](how-to/documents-and-files.md#file-sharing) |
| `file-upload` | Upload a local file to a Microsoft 365 location once write scope is granted | Limited | runtime | [Upload a local file into Microsoft 365](how-to/documents-and-files.md#file-upload) |
| `drafting` | Produce a grounded reply, new message or Teams draft written in your voice, never the assistant's | Implemented | procedure | [Get a ready-to-send draft in your voice](how-to/drafting-and-follow-ups.md#drafting) |
| `executive-followup` | Turn a meeting or thread into an executive-ready follow-up: listen-first, specific, credited by name | Implemented | procedure | [Land a high-stakes message with the right tone](how-to/drafting-and-follow-ups.md#executive-followup) |
| `artifact-delivery` | Link a prepared artifact to its real delivery receipt once it has actually been shared | Implemented | runtime | [Know a work product actually reached someone](how-to/feedback-and-work-products.md#artifact-delivery) |
| `do-not-learn` | Mark an interaction as explicitly not to be learned from, without an operational receipt claiming otherwise | Implemented | runtime | [Say 'not a lesson' and mean it](how-to/feedback-and-work-products.md#do-not-learn) |
| `feedback` | Record a specific correction tied to the exact item it applies to, without turning it into a standing rule | Implemented | runtime | [Correct Margo without a permanent rule you didn't ask for](how-to/feedback-and-work-products.md#feedback) |
| `rules` | Propose a scoped rule from supporting examples, then activate it only with explicit confirmation | Implemented | runtime | [Turn a repeated correction into a standing rule](how-to/feedback-and-work-products.md#rules) |
| `work-products` | Produce a decision memo, comparison, status update, agenda or delegation brief tied to its sources | Implemented | runtime | [Get a useful first draft of the real deliverable](how-to/feedback-and-work-products.md#work-products) |
| `ado-work-items` | Run the saved bug and backlog queries by ID and summarize priority, staleness and ownership | Optional | procedure | [Review your Azure DevOps backlog without hunting for the query](how-to/github-and-work-items.md#ado-work-items) |
| `github-reviews` | See review requests, your own stale PRs and assigned issues across both GitHub accounts, aged and cited | Optional | procedure | [Never miss a stale review request](how-to/github-and-work-items.md#github-reviews) |
| `inbox-triage` | Turn a noisy inbox into a short, ranked, decision-ready list with a recommended action per item | Implemented | procedure | [Clear your inbox with confidence](how-to/inbox-and-teams.md#inbox-triage) |
| `teams-triage` | Build an attention queue across chats and channels since there is no native unread feed to rely on | Implemented | procedure | [Find what needs a reply in Teams](how-to/inbox-and-teams.md#teams-triage) |
| `memory-canvas` | Inspect, search and request a foreground correction or forgetting decision from an optional app panel | Optional | runtime | [Inspect memory without a CLI window](how-to/memory-controls-and-learning.md#memory-canvas) |
| `memory-capture` | Opt in to capturing sourced facts, preferences and episodes under a reviewed capture policy | Optional | runtime | [Let Margo remember useful context, on your terms](how-to/memory-controls-and-learning.md#memory-capture) |
| `memory-control` | Inspect why a memory was used, dispute or suppress it, or forget it and its derived indexes entirely | Optional | runtime | [Correct, suppress or forget what Margo remembers](how-to/memory-controls-and-learning.md#memory-control) |
| `memory-export` | Export a newly authored, reviewed generic lesson to a new private file, with no personal history attached | Optional | runtime | [Share a lesson without sharing your history](how-to/memory-controls-and-learning.md#memory-export) |
| `memory-learning` | Record installed capability evidence and propose a reviewed, scoped lesson from a real execution receipt | Optional | runtime | [Teach an approach without granting new permissions](how-to/memory-controls-and-learning.md#memory-learning) |
| `memory-trends` | Review evidence-backed trend candidates over a bounded population and window, without automatic behavioral activation | Optional | runtime | [See a pattern without it becoming a rule](how-to/memory-controls-and-learning.md#memory-trends) |
| `capacity` | Calculate real available time from working hours, meetings, leave and protected blocks, with honest gaps for missing data | Implemented | runtime | [Check whether a plan actually fits the week](how-to/outcomes-and-meetings.md#capacity) |
| `meeting-debrief` | Turn a recap or transcript into your actions, what you're waiting on, and unresolved gaps nobody owns | Implemented | procedure | [Close the loop after a meeting](how-to/outcomes-and-meetings.md#meeting-debrief) |
| `meeting-lifecycle` | Keep one durable record per meeting occurrence: preparation, delayed-recap retries, debrief and carry-forward | Implemented | runtime | [Never rebuild a recurring meeting from scratch](how-to/outcomes-and-meetings.md#meeting-lifecycle) |
| `meeting-prep` | Assemble purpose, people, context and talking points for an upcoming meeting, including linked agenda topics and unresolved work | Implemented | procedure | [Walk into a meeting ready](how-to/outcomes-and-meetings.md#meeting-prep) |
| `outcomes` | Agree up to three weekly outcomes with an owner, definition of done, due date and effort estimate | Implemented | runtime | [Agree what success looks like this week](how-to/outcomes-and-meetings.md#outcomes) |
| `week-ahead` | See the shape of next week: load, prep debt, focus time, conflicts and what's due, while there's still time to act | Implemented | procedure | [Prepare for what next week actually needs](how-to/outcomes-and-meetings.md#week-ahead) |
| `one-on-ones` | Keep a rolling, editable agenda per person, accumulated through the week and rendered before the meeting | Implemented | procedure | [Never reconstruct a 1:1 agenda five minutes before it](how-to/relationships-and-one-on-ones.md#one-on-ones) |
| `relationships` | Surface drifting relationships against an intended cadence, ambient-only and capped at a handful a week | Implemented | procedure | [Notice who you've quietly stopped talking to](how-to/relationships-and-one-on-ones.md#relationships) |
| `memory-retrieval` | Search and build a bounded context packet for a task from hybrid keyword and local semantic search | Optional | runtime | [Recall relevant context by meaning, not just keywords](how-to/semantic-memory.md#memory-retrieval) |
| `account-storage` | Confirm the private, account-scoped storage location before any data is captured | Implemented | runtime | [Know which account and where data lives](how-to/setup-and-migration.md#account-storage) |
| `setup` | Install Margo and confirm the Work IQ connection works | Implemented | procedure | [Get a first useful brief](how-to/setup-and-migration.md#setup) |
| `uninstall` | Uninstall managed files while keeping personal preferences and the private runtime database recoverable | Implemented | runtime | [Remove Margo without losing personal files](how-to/setup-and-migration.md#uninstall) |
| `upgrade-migration` | Upgrade an existing installation while preserving preferences, commitments and durable state | Implemented | runtime | [Update without losing your settings or work](how-to/setup-and-migration.md#upgrade-migration) |
| `task-progress` | See a bounded task's plan, completed steps, remaining work and source gaps across sessions | Implemented | runtime | [Know where a task stopped](how-to/task-progress-and-recovery.md#task-progress) |
| `task-recovery` | Pause, resume, cancel or reconcile a bounded task without repeating a completed external effect | Implemented | runtime | [Pick up a task safely after an interruption](how-to/task-progress-and-recovery.md#task-recovery) |
| `personalization` | Teach preferences, VIPs, working hours and drafting voice so recommendations and drafts match how you actually work | Implemented | procedure | [Make Margo work the way you do](personalization.md#personalization) |
<!-- END GENERATED: feature-catalog -->

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
