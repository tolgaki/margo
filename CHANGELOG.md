# Changelog

Only 1.0.0 has a release tag; 1.1.0 and 1.2.0 shipped on `main` without one.

## 1.3.0 (unreleased)

### Margo Cloud (new)

- **Margo Cloud, in C# on .NET 10** (`cloud/`), self-hosted for Agent 365 with one container per hired Margo. Its
  state commands stay byte-compatible with the desktop helpers, judged by `tools/state_parity.py`. A preview, not for
  production: only provisioning, the router-to-instance path, delegated mail and calendar reads and a Teams reply
  are verified in a test tenant. Everything else below is built and tested offline; see the Margo Cloud overview's
  maturity table.
- **Approval runs once, on what the card showed.** The record that an approved action was sent is saved to shared
  storage before the call leaves; the email, meeting, chat or file it acts on is read again at the tap; and the card
  lists everything in the call, including link destinations and who a reply to everyone reaches.
- **The platform's application must be named.** Entra-issued deliveries are refused until `MARGO_PLATFORM_APP_IDS`
  is set (deployment guide, step 6); an existing deployment that ran without it stops answering until it is.
- **Suspending an instance removes its container app**, so nothing runs in the background; a retirement whose
  clean-up fails is retried hourly instead of dropped.
- **Work IQ through the official MCP C# SDK** (`ModelContextProtocol.Core`). The model gets Work IQ's own tool
  descriptions and schemas from `tools/list`, limited by the guard. Writes run on their own session and are never
  resent; throttled reads honor a short `Retry-After` once; an expired token is refreshed once for reads; calls are
  traced and measured. When Work IQ rejects a call, the model is told why, and the server's text stays out of logs.
- **The guard** sits between the model and every Work IQ call. It has no fixed list of allowed roots: any path Work
  IQ serves can be read, while `$batch`, colleagues' `/users` data and blocked resources are refused. It follows
  Graph paging and delta links, lets `ask` name a Copilot agent, refuses disguised path segments and limits file links
  to SharePoint hosts.
- **Approval cards.** Margo sends email (from the manager's mailbox, on their behalf), sets up, moves or cancels
  meetings, posts Teams messages, and writes to Pages, Viva Engage and SharePoint only after the manager taps Approve
  on a card showing that exact action, in plain words with no request details. A typed or spoken "yes" is not an
  approval.
- **Works with her manager in group chats and channels.** When the manager @mentions her there, she answers in
  front of everyone, bringing in only what the request needs; cards posted there count only on the manager's tap.
- **Reports to her manager without a card**: a message in their 1:1 chat, an email to them alone, or a file shared
  with them alone; the guard checks the manager is the only recipient.
- **The backchannel and listening.** When someone else messages, emails or copies Margo, and every
  `MARGO_LISTEN_MINUTES` in the chats and channels she belongs to (with her container kept running), she reads through Work
  IQ with her own token and may send the manager a note. She never answers those people. `MARGO_BACKCHANNEL`, a daily
  cap (`MARGO_BACKCHANNEL_DAILY_RUNS`) and a per-conversation cooldown bound it.
- **Other people get no reply.** Messages and mail from anyone but the manager are queued as untrusted requests;
  with `/replies acknowledge` the sender gets one fixed "passed this on" note per conversation.
- **Access modes.** Delegation (the default) reads the manager's mail and calendar through Exchange delegation; on-
  behalf-of mode signs the manager in from their own 1:1 messages through a configured handler.
- **Microsoft's `workiq` skill** (3.0.0, pinned in `cloud/vendor/workiq/source.json`, refreshed with
  `cloud/scripts/update-workiq-skill.sh`) is served next to Margo's skills; where they differ, Margo's rules win.
- **Security, privacy and reliability.** Platform tokens are checked for issuer, calling application and reply host
  (Entra-issued deliveries are refused until `MARGO_PLATFORM_APP_IDS` names the platform's application); model requests ask the service to store nothing; replaced state versions expire after 30 days; voice signs in through its own single-page app;
  duplicate deliveries are dropped, turns in flight finish on shutdown, and the provisioner grants only the roles an
  instance needs. Instructions are cacheable and token use is logged. `cloud/terms.md` describes what this version
  does and keeps.

### Desktop Margo

- **A plugin** for GitHub Copilot CLI and Claude Code (skills and persona) and OpenAI Codex (skills only; not yet
  tested): the repository is its own marketplace (`/plugin marketplace add tolgaki/margo`,
  `/plugin install margo@margo`). Personal files can live in `~/.copilot/margo/`, outside the plugin.
- **Office documents**: ask for a Word document, Excel workbook or PowerPoint presentation and Work IQ's document
  tools build it in your OneDrive. Sharing it with anyone else still needs approval.
- **"What changed since…"** uses Microsoft Graph delta per source, then asks Copilot only about the changed items.
- **New procedures**, each discovering its paths first and routing every write through approval: Viva Engage reads,
  meeting transcripts, people and org lookups, publishing to Copilot Pages or SharePoint, SharePoint approvals,
  access and version restore, and asking a Copilot agent.
- Write procedures check an unfamiliar path's schema first; collections follow paging links within a budget and
  report partial results; briefs and meeting prep show sensitivity labels.
- Margo agent selection is separate from skill routing, so names and greetings no longer trigger the playbook.
- Opt-in, manual Dream reflection over selected session checkpoints.
- Updates fetch the exact remote commit checked; `update --reinstall` refreshes code without replacing personal
  files; remote failures and downgrades stop without changing the installation. The installer no longer fails on a
  group-writable folder.

### Documentation

- **Learn Work IQ** (`docs/work-iq/`): loading it in a harness or a custom agent, identity and consent, a capability
  map, patterns, errors and recovery, and approved writes. `samples/harness/` and `cloud/samples/WorkIQHello` are the
  quickstarts.
- A Margo Cloud overview, an FAQ, a glossary, task guides for every catalogued feature, and guides for customizing
  Margo with Claude Code or GitHub Copilot.

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
