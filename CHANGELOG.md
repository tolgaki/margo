# Changelog

## 1.3.0 (unreleased)

- **Margo Cloud**: hosted Margo rewritten in C# on .NET 10 (`cloud/`), self-hosted for Agent 365 with one
  container per hired Margo. Its state commands stay byte-compatible with the desktop helpers, judged by
  `tools/state_parity.py`. See the deployment guide's known limits for what is unverified and the hardening still
  open.
- Margo Cloud delegation mode reads the manager's mail and calendar directly through the delegation and searches only
  what is shared with Margo; Copilot search of the manager's whole data reports "unavailable".
- Margo Cloud loads Work IQ with the official MCP C# SDK (`ModelContextProtocol.Core`) instead of a hand-written MCP
  client ported from Python. The model now gets Work IQ's own tool descriptions and argument schemas from
  `tools/list` (limited by the guard), with the built-in definitions as a fallback. When Work IQ rejects a call, the
  model is told why, without the server's text reaching logs. Writes still get a session of their own and are never
  resent. The protocol revision is pinned to 2025-06-18.
- Approval cards are written for the person deciding: plain words about what happens, to whom, with what text and
  when, and which email, meeting, chat or file it affects. They no longer show the Work IQ request, IDs or JSON.
  Outcomes are plain too ("Done: the meeting is updated.", "Nothing went out: your organization's settings don't allow
  it.").
- Office documents: ask for a Word document, Excel workbook or PowerPoint presentation and Margo writes the
  content and has Work IQ's Office document tools build it. Desktop creates it in your OneDrive; Margo Cloud creates
  it in Margo's OneDrive and shares it with you alone. Creating a document you asked for is a standing permission
  (no card); sharing it with anyone else still needs approval. Unclear creations are never retried.
- Margo Cloud approval cards: Margo sends email and replies from the manager's mailbox (on their behalf), sets up,
  moves or cancels meetings, and posts Teams messages as herself, each only after the manager taps Approve on a card
  in Teams showing that exact action. The tap runs the stored call once through the guard; typed or spoken "yes" is not
  an approval. Needs Send on Behalf and calendar Editor delegation for the agent user.
- Margo Cloud security review (November 23, 2026). Platform tokens are accepted only from the platform, never a
  signed-in person (`MARGO_PLATFORM_APP_IDS` pins the application); replies go only to Microsoft hosts; the voice page
  signs in for its own app's scope, not the blueprint's; the guard refuses disguised path segments and limits file
  links to SharePoint hosts; logs carry path templates, not identifiers.
- Margo Cloud privacy: the model service stores nothing (`store: false`; the conversation is carried in memory,
  bounded); replaced state versions and daily copies expire after 30 days; `cloud/terms.md` rewritten to match what
  this version does and keeps.
- Margo Cloud reliability: per-instance router pools; no routing before the route table has loaded; turns in flight
  finish when a container is stopped; duplicate deliveries are dropped; a second message waits for a long turn instead
  of failing; `azd provision` no longer breaks the provisioner job; a failed retirement cannot bring an agent back;
  an instance cap; the provisioner job is queue-triggered.
- Margo Cloud cost: instructions are cacheable (the time moved to the message), reasoning effort and output limits are
  set, per-turn token usage is logged, Work IQ reads reuse connections, unchanged state is neither downloaded nor
  uploaded, the image is compiled ahead of time, health probes are not traced.
- Margo Cloud hardening: the router and instances accept tokens only from Bot Framework and the tenant's own Entra
  issuers (`MARGO_EXTRA_TOKEN_ISSUERS` admits another); the router finishes forwarding an activity after Agent 365
  stops waiting, so a cold instance no longer loses the first message; the provisioner may grant an instance's roles
  only on the resources they apply to and only to service principals other than Margo's own identities.
- Margo Cloud voice signs in through a separate single-page app (`MARGO_VOICE_CLIENT_ID`), because agent blueprints
  refuse redirect URIs.
- Separated Margo agent selection from chief-of-staff skill routing: names and greetings no
  longer trigger the playbook, which preserves the caller's identity. Installer and setup help
  now explicitly select the agent; updated instructions require a fresh session, not migration.
- Connected user and developer documentation journeys, a shared documentation home, and detailed
  task guides for all 68 catalogued features, with corrected setup, approval and recovery guidance.
- Opt-in, manual Dream reflection over selected current-session checkpoints: sourced episodes,
  reviewable candidate interpretations, bounded daily pages and task-scoped recall.
- Revision-aware shell and PowerShell updates fetch the exact remote commit checked, including
  same-version changes, instead of reinstalling a stale local checkout.
- Safe `update --reinstall` / `update -Reinstall` refreshes code without forcing replacement of
  personal files. Remote failures and downgrades stop without changing the installation.
- Installation explicitly distinguishes copying files from private-state migration, app workflow
  synchronization, extension/session reload and capture opt-in.

## 1.2.0

- Agent-ready contribution instructions, state-ownership map, bounded task issue/PR contracts,
  and a user-goal feature catalog with generated navigation and scenario coverage gates.
- Durable account-scoped task runs with explicit budgets, claims, pause/cancel, bounded read
  retries, exact action-ledger integration, reconciliation and reviewed replanning.
- Task progress and recovery help, optional review surface, installed-copy preservation, and
  distinct deterministic journey contracts versus recorded synthetic model evidence.
- Adversarial-review fixes: truly read-only memory inspection; optional-runtime health separated
  from core health; explicit preference re-import recovery; persisted reuse labels and draft-purpose
  filtering; protected dispute/stale states; bounded source hashing and memory representations.
- Indexing isolates known invalid legacy records and drains deletion jobs during rebuilds;
  preview chunk boundaries and observed trend windows are validated before use, and material
  trend corrections can be reviewed again.
- Private user/agent memory records with scoped authority, provenance and lifecycle.
- SQLite keyword and semantic vector indexes with versioned indexing jobs and rebuild support.
- Explicitly installed local MiniLM embedding runtime, pinned ONNX/tokenizer artifacts and
  offline inference; no cloud embedding service.
- Hybrid relevance ranking, exact/relationship retrieval and bounded context packets that retain
  applicable mandatory preferences.
- Reviewed preference seeding, installed-skill observations and evidence-backed trend candidates.
- Explicit forgetting of memory history and derived indexes, with anti-reimport tombstones.
- Optional read-only Margo Memory canvas and semantic-memory how-to documentation.
- Reviewed passive-capture policy, separately opted-in usage records, do-not-use suppression
  and bounded per-kind retention/review maintenance.
- Dated relationship evidence, immutable derivation history, atomic derived-index erasure
  and reviewed deletion-journal reconciliation for restored backups.
- Exact reviewed export of generic lessons to new private files, without source metadata,
  automatic publication or skill installation.
- Explicit memory schema 1-to-2 migration preserving existing records; capture defaults off.
- Host/version-scoped capability evidence, reproducible input-contract exercises, existing
  execution-backed lesson proposals and exact reviewed activation.
- Configurable evidence-backed trend candidates and bounded consolidation review pages with
  once-per-evidence surfacing, without an always-on model or automatic rule activation.
- Bounded connected context over canonical work records, exact identities and temporal
  relationships, with current-decision resolution, provenance and explicit conflicts/gaps.

## 1.1.0

### Work and actions

- Account-scoped SQLite work ledger with immutable source revisions, candidate review,
  confirmed obligations, relationship links and history.
- Revision/hash-bound action proposals, explicit approval evidence, preflight checks,
  execution receipts and uncertain-outcome reconciliation.
- Confirmed-only Markdown export with manual-edit detection and reviewed legacy import.
- Optional Copilot app action desk over the portable CLI: review, safe evidence navigation,
  local payload revisions, defer/dismiss and foreground review requests.

### Proactive reliability

- Leased delivery batches with receipt-gated acknowledgments and interrupted-run recovery.
- Standalone publications for anchors with no queued inputs.
- Independent source attempts and coverage, stable polling identity, fixed provider windows,
  query-bound delta tokens, continuation checks, failure episodes and explicit retirement.
- Doctor reports for required settings, managed-file drift, source health, delivery backlog
  and timestamped host snapshots.

### Productivity

- Agreed weekly outcomes with required completion criteria and capacity inputs.
- Deterministic interval-union calculations for work capacity, focus gaps and overflow.
- Persistent meeting lifecycle, bounded recap retries and same-series carry-forward.
- Explicit corrections, scoped rule proposals, revocation and do-not-learn records.
- Private versioned decision memos, document comparisons, status updates, agendas and
  delegation briefs, with separate delivery receipts.

### Deployment and documentation

- Copy installers record managed-file provenance and support the optional renderer.
- Runtime account data stays outside the checkout and survives uninstall.
- Native packages include the new helpers and optional renderer source.
- Synthetic Python and Node regression coverage, including real canvas-to-CLI integration.
- [Feature reference](docs/features.md) and [how-to guides](docs/how-to/README.md).

Nothing in this release grants unattended permission to send or changes the distinction
between local preparation, human approval and external execution.

## 1.0.0

Initial reference implementation: Margo persona, chief-of-staff and decision-log skills,
scheduled routine manifests, CLI wrappers, copy/link installers, native packaging,
Work IQ usage guidance, and repository privacy checks.
