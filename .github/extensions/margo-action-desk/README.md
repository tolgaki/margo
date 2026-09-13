# Margo Workspace

One optional Copilot app canvas over Margo's work ledger, memory and task progress. The app supplies
`@github/copilot-sdk`; no package installation or build step is required.

Open **Margo Workspace** (`margo-action-desk`) and switch between **Work**, **Memory**, **Tasks**, **Automations** and **Config**
inside that same host panel. Two legacy canvas declarations remain explicit compatibility
entrypoints, not separate applications. The CLI/conversation remains sufficient. Start with the
[developer journey](../../../docs/development/README.md) for a synthetic-only setup and
[the user guide](../../../docs/user-guide.md) for the surrounding workflow.

## Development map

| Surface | Declaration / implementation | State owner |
| --- | --- | --- |
| Unified workspace | `extension.mjs`, `server.mjs`, `index.html`, `workspace.js`, `ui.css` / `ui.js` | None; host lifecycle, section navigation and local HTTP adapters only |
| Work | `backend.mjs`, `work.html`, `app.js`, `decision-model.js`, `decision-requests.mjs` | `work_state.py`, `decision_workspace.py` over Ledger/TaskStore |
| Memory | `memory-backend.mjs`, `memory.html`, `memory-app.js` | `memory_state.py` |
| Task Progress | `task-backend.mjs`, `tasks.html`, `task-app.js` | `task_state.py` |
| Config | `config.html`, `config-app.js`, fixed profile-save adapter | Existing private profile configuration with conditional revisions |
| Automations | `automation-backend.mjs`, `automations.html`, `automations-app.js` | `automation_definitions.py`; registered workspace Markdown, not a second scheduler |

Read the [state map](../../../docs/development/architecture.md) before adding data. Extend an
existing CLI contract first, not a renderer-owned database or browser-selected command.
Review requests return to the conversation; they never stand in for a subsequent exact decision.

## One panel, five sections

`index.html` is the single document shell with a shared assistant/profile/account header and
sticky Work/Memory/Tasks/Automations/Config tab navigation. The server composes five trusted HTML fragments,
namespaces their IDs/label targets, and serves them under one loopback origin and token.
There are no nested app iframes or navigation-time HTML fetches. Config and Automations have
separate fixed, authenticated local write routes; neither route grants execution approval.
Each renderer exports one scoped controller. `workspace.js` mounts it once, on first visit,
and keeps its DOM, search, filters, selection, disclosures and unsaved draft alive on switches.
Hidden-section callbacks cannot steal focus from the active section.

Tabs use manual keyboard activation: Left/Right or Home/End moves focus; Enter/Space opens the
section. Switching restores that section's last focused control and scroll position. Browser
Back/Forward restores sections in the same document. URL paths `/`, `/memory`, `/tasks`, `/automations`, `/config` name
the starting section and preserve the same token fragment. A page reload/open rehydration is
different: ephemeral UI/drafts reset; saved ledger records do not. Save edits before reloading,
closing a panel, or applying an extension update.

Work's visible clock/poll resumes on return without replacing a dirty detail. Memory/Tasks
retain their last reads and use their explicit refresh controls. Opening/navigation never
initializes or migrates state. Each section preserves its own errors; unavailable Memory does
not hide Work/Tasks. A detected account change hides all cached sections, disables navigation
and asks for an explicit workspace reload before any new account can be shown.

### Canonical and compatibility entrypoints

| Entry | Open input | Behavior |
| --- | --- | --- |
| `margo-action-desk` / Margo Workspace | `{}` or `{"section":"work"|"memory"|"tasks"|"automations"|"config"}` | Canonical unified workspace; section is the initial view |
| `margo-memory` | `{}` | Same unified workspace, initially Memory; legacy list/search/status actions unchanged |
| `margo-task-progress` | `{}` | Same unified workspace, initially Tasks; legacy list/show/history/health actions unchanged |

The SDK has no documented hidden/alias declaration field. Compatibility entries therefore
remain discoverable and are labelled as such; their descriptions and procedures direct new
use to `margo-action-desk`. Canonical snapshot/list/show/refresh keep their existing Work read
contracts. Switching sections in the browser does not invoke a host open or replace its handle.

Already-open legacy panels are independent host instances. After supported deployment/reload,
each rehydrates to this same unified UI, at its legacy starting section. They are **not**
automatically coalesced or closed. Save any unsaved edits before deployment, keep one workspace
panel, and close redundant panels manually in the host. Do not invent alias/close/hide APIs
or claim that opening the canonical ID closes old instances. Old loopback URLs are ephemeral
and may expire after extension restart; reopen through the stable canvas ID when needed.

## Shared reading-first UX

All sections use one nonce-inlined stylesheet and a small, network-free renderer helper.
`ui.css` owns the neutral surface tokens, host-token overrides, typography, controls, reading
cards and responsive list/detail layout. `ui.js` owns safe text-only labels, facts/disclosures,
host/system theme synchronization and section-local reading navigation. It has no state API,
storage, credentials or mutation capability. Avoid adding page-specific copies of these rules.

The hierarchy is **records, manual controls, supporting detail, optional assistance**:

| Canvas | First useful view | Primary action | Disclosed rather than removed |
| --- | --- | --- | --- |
| Work | Locally searchable asks and the next recorded item | Inspect, reload, edit a saved proposal | Assistance, coverage/settings, impact, source snapshots, identity/hash, exact JSON, execution history |
| Memory | Search/browse with readable context previews | Search, browse, inspect or reload | Assistance, provenance, bounded relationships, forgetting preview and history |
| Tasks | Locally searchable task goals and named step progress | Browse, refresh and inspect recorded progress | Assistance, tracked budgets, exact plan/identities and activity history |
| Automations | Registered Markdown definitions and honest native-controller status | Add/edit/review a descriptor change | Provenance, source sections and unresolved execution gates |
| Config | Current name and output workspace | Save or cancel explicit local settings | Revision-conflict review; no automatic file moves |

See [settings and automation authoring](../../../docs/how-to/workspace-settings-and-automations.md)
for shared UI/natural-language authoring and its unbound native-controller limitation.
[Restricted app preparation](../../../docs/how-to/restricted-app-proactivity.md) uses a separate
`margo-proactive` agent and narrow tools; it cannot author these settings or scenario files.

### Restrained palette and optional assistance

This is an **Operate** surface, not a capability showcase. The user-pinned design brief calls
for neutral host-aware surfaces and less visible AI functionality. The existing `--cp-*` names
remain internal CSS compatibility names; they no longer imply a Scout/Clawpilot brand palette.
Light/dark surfaces, selected rows, tabs, filters, badges, ordinary buttons and progress use
neutrals. A restrained blue is reserved for links/focus; the host's semantic focus token wins
when present. Muted red is limited to labelled errors/destructive controls. Color is never the
only state cue: selected tabs are underlined, filters use weight/borders, and statuses/errors
retain text. There are no gradients, colorful card fills, promotional eyebrows or decorative
shadow layers. System typography and the shared six-pixel control/card radius stay consistent.

By default **zero preparation/review request buttons are visible**, on both lists and newly
opened details. Work's overview has only **Inspect item**. Manual search, refresh, record
inspection, proposal editing/saving, snooze and dismissal stay in their established locations.
One closed **Assistance** disclosure groups all optional requests for the selected record:

| Section | Inside Assistance | Unchanged boundary |
| --- | --- | --- |
| Work | Prepare for me; Recommend a response; Review in conversation; request progress/results | Exact revision/hash, capability/readiness gates and durable dispatch deduplication |
| Memory | Correction, do-not-use, supersession, forgetting, and eligible sanitized lesson export review | Requests only; exact revision and eligibility still checked |
| Tasks | Pause, cancel, resume, replan, recover and reconcile review | Requests only; exact run/revision/plan hash still checked |

The disclosure uses native keyboard-accessible `details`/`summary` and survives section
switches with the rest of the selected DOM. It does not grant permissions or turn off any
backend feature. Optional-capability explanations live with those optional controls; setup,
source-change, execution uncertainty and API errors remain explicit. Empty states describe
the actual returned records without recommending more AI work.

**Design method:** the user requested Impeccable. The native launcher was unavailable; its
official v4.3.1 [manual fallback](https://github.com/pbakaus/impeccable/blob/main/.github/skills/impeccable/SKILL.md)
was used against the existing code, this design reference and incumbent screenshots.
No PRODUCT.md/DESIGN.md existed, and none was invented. The
[quieter](https://github.com/pbakaus/impeccable/blob/main/.github/skills/impeccable/reference/quieter.md),
[distill](https://github.com/pbakaus/impeccable/blob/main/.github/skills/impeccable/reference/distill.md),
[Operate](https://github.com/pbakaus/impeccable/blob/main/.github/skills/impeccable/reference/operate.md)
and [craft floor](https://github.com/pbakaus/impeccable/blob/main/.github/skills/impeccable/reference/craft-floor.md)
references informed neutral dominance, flattened containers, task-first copy, one secondary
assistance entry and a bounded visual inspection plus at most one confirmation. The pinned
brief overrides generic expressive-brand advice. No Impeccable binary, hook or dependency
was installed, and no native slash-command execution is claimed.

At 900px and below, selecting an item opens a dedicated reading view instead of appending a
long detail page beneath the list. **Back to list** or **Escape** returns to the current row
without discarding the detail, search or draft. Escape does not intercept text-entry controls.
Wider panels retain a side-by-side list and reading pane. Keyboard focus moves to the selected
detail and returns to the current list row, including after a time-based reorder.

Common string fields in action payloads have ordinary text editors backed by the same exact
JSON draft; **Advanced: exact payload JSON** remains available for every payload field and
unsupported shape. Text edits preserve unrelated fields and still save one conditional
revision through the existing backend. An invalid or structurally changed raw field disables
its simple editor. Busy/stale/offline states cannot silently overwrite drafts. Returning to a
dirty selected item reopens its existing detail without fetching over it.

Memory **History** filters loaded rejected/stale/superseded/suppressed/forgotten records; every
record still has its own revision history in detail. Search method never silently falls back:
choosing keyword-only makes its required domain/routine scope discoverable. Task **Refresh
tasks** reloads the first page without silently refreshing a selected detail; if that row
changed or is outside the page, review controls stay disabled until an exact detail reread.
Step counts reflect completed attempts, not a progress prediction or proof of delivery.

Failed reads retain prior context when available and show a recoverable error. Initial setup
failures show unavailable, not empty data. Review failures disable further review until reread;
unknown delivery still requires checking the conversation, not blind retry. Normal status
updates are compact; consequential gaps/errors remain visible. Raw content is never rendered as
HTML, even when a provider payload describes HTML.

Visual regressions use `ux-browser.test.mjs` with synthetic fixtures: all three sections at
360px/1280px in light/dark mode, host-theme overrides, labelled fields, sampled 4.5:1 text
contrast, keyboard list/detail return, progressive disclosure, search, stale-revision review
guards and offline/setup recovery. `browser.test.mjs` additionally exercises real DOM edits,
snooze expiry, request progress, repeated clicks and draft preservation. This is renderer
evidence, not a screen-reader audit, live provider test or model-adherence evaluation.
`quiet-browser.test.mjs` adds an opt-in, dependency-free rehearsal through an **existing**
Chromium/Edge executable (`MARGO_BROWSER_EXECUTABLE`) using Node's built-in WebSocket.
It never downloads a browser/tool. It batches all sections at 360/1280px in light/dark,
captures list/detail views when `MARGO_QUIET_SCREENSHOTS` is set, checks neutral default
surfaces and zero visible assistance requests, then exercises disclosed actions, manual
editing, retained drafts, exact revisions and offline/conflict guards. Browser profiles and
all records are synthetic. Without an existing executable it is explicitly skipped.

## Runtime contract

- Canvas: `margo-action-desk`. Open input: `{}` or optional initial `section` from the table above.
- Read-only agent actions: `snapshot`, `list`, `show` (`{"id":"…"}`), and `refresh`.
- Data belongs to the CLI's configured account, not a canvas instance. Work reads/local
  mutations go through `work_state.py`; Memory and Tasks retain their respective fixed
  CLI adapters. This extension has no database.
- Resolve the script from this project's
  `skills/chief-of-staff/scripts/work_state.py` (`../../../` from the extension),
  then an installed destination's adjacent `skills/` directory (`../../`), then
  `$COPILOT_HOME/skills/chief-of-staff/scripts/work_state.py` (default
  `~/.copilot/skills/…`), checking each for an existing regular file. This also
  supports the installer's custom `--dest` layout. Missing core/configuration is
  shown as setup needed.
- The CLI resolves the configured account using its trusted process environment and private
  config. Explicit invocation/environment paths win; otherwise the shared
  `margo/locations.json` installation binding selects the approved existing config/state.
  No binding retains the legacy `~/.copilot/margo` default. Copied helpers identify their
  `.margo-install` root independently of cwd/host environment; `COPILOT_HOME` overrides it.
  Malformed/missing bound targets are errors, not a silent old-root fallback. Browser input cannot select
  an account, script, command, interpreter, configuration file, or database path.
- Local UI operations on action proposals are payload revision, defer, and
  dismiss. Work items and typed records are read-only because their transitions
  may require human evidence. Revisions require the displayed revision/hash; a
  conflict leaves the unsaved editor text intact.
- Preparation/recommendation/review buttons queue SDK `session.send` messages with exact
  identity and fixed local-only instructions. Existing TaskStore claims durably deduplicate
  dispatch by account/item/revision/hash/intent before messaging. Acceptance has a receipt;
  an unknown dispatch is retained, never automatically resent. No claim token is sent to
  the renderer. Only the conversation worker claims the prepare step after an atomic subject
  revision check, then records its actual result. No registered action dispatches requests.
- These buttons require initialized task/work/coverage schemas and SDK messaging; opening
  does not initialize them. Normal task-progress reads/review behavior is unchanged.
- None of these requests stores approval or performs an external write. Only a subsequent
  explicit user confirmation in the conversation can authorize an exact external action.

## Decision workspace

The default Focus view groups **Now**, **Needs your decision**, and **Next** with one suggested
next focus. The All/status views and exact payload editor remain available. Rendered asks use
the work's `next_step`, linked work, artifact `proposed_next_action`, or recorded title; missing
blockers/affected people are labelled unknown rather than invented.

`decision-model.js` is a pure presentation projection, not another tracker. It ranks unresolved
effects first, overdue work next, then meetings/deadlines within an explicit 60-minute window,
other decisions, and next steps. Historical/completed records and unexpired snoozes cannot
win focus. Snooze expiry resurfaces a record without mutating it. Meeting end time never
establishes attendance, completion, or a new future occurrence.

The one-second clock uses the IANA timezone from the existing adjacent private preference
field, or explicitly labelled device time. Working context supports
`America/Los_Angeles; Mon-Fri; 09:00-18:00` and comma-separated day names. Missing, ambiguous
or overnight schedules remain unknown. Date-only deadlines use account calendar-day boundaries
without fabricating an exact deadline; DST is handled by `Intl`.

Local state polls every 15 seconds while visible; this never fetches M365. Coverage comes from
`proactive_state.coverage_status` under the same account, with real collection times, scopes,
failures and cadence. Freshness can expire while the clock runs. Old or unavailable coverage
is not an empty source. The bounded desk includes the newest 50 records per type and explicitly
reports truncation; CLI `list --view all` remains the complete view.

Ready request results mean private preparation only. The UI displays canonical result links,
accepted/working/ready/blocked/failed/partial/unknown states and disables repeat submission of
the same request. Offline cached content stays visible with mutations disabled; conflicts
preserve unsaved edits. Keyboard focus survives list reprioritization, and opening detail moves
focus to its heading region. No external CDN, storage service or new runtime dependency is used.

## Display identity and operating folder

The shared workspace title/header reads `work_state.py profile` through the existing fixed-path
adapter. The authenticated `/api/profile` route supplies validated display-only settings to
`profile.js`; labels use `textContent`, never injected HTML or instructions. The Action Desk
shows work-root availability and setup guidance once. Technical canvas/agent/tool IDs remain stable.

Explicit profile edits use `margo_store.py profile-set` with the exact configuration revision.
There is no browser settings-write endpoint. The account-scoped name defaults to Margo;
changing it never changes account identity, the user's draft identity, or file locations.
The dedicated operating folder is for explicitly requested outputs, not code installation or
SQLite/WAL. Read [personalization](../../../docs/personalization.md#assistant-name-and-everyday-workspace)
for setup from an arbitrary folder, conditional updates and safe export limitations.

### Local account and basic-memory readiness

Provider discovery does not prove that any section can read its private state. The shared
header identifies the **configured local owner**, while Microsoft 365 authentication remains
**not checked**. A durable explicit binding fixes location lookup without requiring the
long-lived host to inherit newly saved environment variables. Use the installed
`margo_store.py locations`, then the revision-safe `locations-bind` command documented in
[setup](../../../docs/how-to/setup-and-migration.md#durable-private-location-binding).
It writes a locator only; no config/profile/database is copied and no section is initialized.

`account_setup_required`, `location_unavailable`, `not_initialized` and
`semantic_unavailable` are distinct errors across Work/Memory/Tasks. Memory initialization is
separate from task initialization. After an approved `memory_state.py init`, status reports
`basic_memory` availability, optional `semantic_search`, raw `embedding_runtime` and capture
policy independently. `missing_model` does not block ordinary list/details or scoped keyword
search. The Memory view exposes **Use keyword search** only when basic memory is available
and the optional encoder is unavailable. It changes the mode only after a user click, opens
the existing scope selector and does not run a search. No fallback, model download, automatic
capture, import or initialization occurs from the renderer.

`locations-integration.test.mjs` runs the actual installed Python adapters and HTTP routes
with both MARGO location variables and COPILOT_HOME absent. It verifies explicit binding,
same owner/profile/state across all sections, pre-init errors, post-init lexical recall,
missing optional embeddings, unchanged capture policy and no old-root database/model files.
`test_margo_locations.py` additionally covers precedence, conflicts, private-path failures and
no-network/no-encoder basic recall. Installer regressions verify that the private locator is
preserved outside the managed file manifest.

## Local HTTP boundary

Each panel owns an ephemeral server bound to `127.0.0.1`, with an independent
random 256-bit token in the URL fragment. The static page contains no stored data.
API requests use a bearer header, exact Host validation, and same-origin checks;
mutations additionally require an exact Origin header and JSON body. No CORS,
external resources, telemetry, executable stored HTML, or arbitrary command route
is provided. The token is a local access capability, **not proof of human
approval**. Another trusted local process with the token could edit local ledger
state, which is why this server has no approval or execution operation.

All stored values, including payload/evidence/provenance/errors, render through
`textContent` or form values. Content security policy disallows inline handlers,
external connections, forms and object embeds.
Source detail offers user-opened evidence links only for absolute HTTP(S) URLs,
labelled with their actual hostname and optional stored title. Links use
`target="_blank"` and `rel="noopener noreferrer"`; credentials, relative URLs and
other schemes are rejected. The canvas never fetches those external URLs.

The shared profile read is `GET /api/profile`; the Action Desk reads are `GET /api/desk`,
`GET /api/items` and `GET /api/items/:id`.
User-interface POST routes are `/api/items/:id/revise`, `/defer`, `/dismiss`,
and legacy `/review`, plus `/api/decision-request` for task-backed bounded assistance.
The new UI uses the durable request path; legacy `/review` is a request-only compatibility
endpoint without task-backed progress. There is deliberately no `/approve` or `/execute`.

### Portable CLI adapter

Commands use fixed Python argv without a shell:

```text
work_state.py list --view all --json
work_state.py desk
work_state.py show ID --json
work_state.py edit ID --revision N --expected-hash HASH --input -
work_state.py defer ID --revision N --expected-hash HASH --until ISO_TIMESTAMP
work_state.py dismiss ID --revision N --expected-hash HASH
work_state.py desk-request --input -
work_state.py desk-dispatched --input -
```

The list contract is `{schema_version:1, account, items, actions, records}`; show
and mutations return a raw record. The HTTP adapter combines all three lists
into display rows and wraps individual rows as `{item:…}`. `action_hash` remains
the authoritative hash; `payload_hash` is a display compatibility alias. Before
each mutation the adapter checks that exact hash and revision, then the core
checks both `--revision` and `--expected-hash` in the same transaction. Edit,
defer and dismiss all return a new immutable revision/hash. The adapter never
passes an approval command.

`desk` extends the list JSON with `read_at`, `time_preferences`, `coverage`,
`request_capability`, `requests`, and explicit `limit_per_type`/`truncated` fields.
The bridge privately passes the dispatch claim over stdin to `desk-dispatched`; the browser
cannot select the SDK host, account, claim token or arbitrary prompt. The worker uses
`desk-start RUN_ID --host SDK_HOST` with its actual current SDK host identity, then existing
`task_state.py charge/finish` APIs. A different host requires foreground plan review, not
relabeling a saved observation as fresh. The binding is not authentication. Preparation is limited
to local evidence, 8 tool calls, 1 model call, 20 sources and 8,000 output characters; tasks
expire after 30 minutes with a maximum 15-minute claim and no retry budget. These are
tracked-path budgets, not a sandbox for arbitrary host tools or a measure of total model usage.

For edit, the adapter preserves the original action's target, rationale, source
references, fingerprints, work-item association and optional dependencies while
replacing only `payload`. The complete document goes through subprocess stdin,
not argv or temporary files. Structured CLI failures on stderr are propagated
as HTTP errors; arbitrary stderr and stack traces are not exposed.

## Margo Memory

Optional read-only inspection/search over `memory_state.py`, adjacent to the resolved work
script. This panel does not initialize, migrate, capture, correct, forget, activate or export
memory and does not install a model.

- Normal entry: `margo-action-desk` with `{"section":"memory"}`, or the Memory tab.
  Compatibility entry: `margo-memory` with `{}`.
- Registered agent actions: `list`, `search` and `status`. The browser also offers detail,
  inspection/history, graph and policy reads; those are **not** additional registered actions.
- Search defaults to hybrid meaning-based retrieval. A missing local runtime is an explicit
  unavailable state, not a cloud or silent keyword fallback. The user may explicitly select
  lexical search with a domain or routine scope. Query text goes through subprocess stdin.
- `memory-backend.mjs` uses fixed `memory_state.py` argv, `-B`, no shell, a 60-second timeout
  and a 4 MiB response cap. IDs, enums and result shapes are validated.
- Read operations use `POST /api/memory/list`, `/show`, `/search`, `/status`, `/inspect`,
  `/graph` and `/policy`; HTTP POST here does not imply a memory mutation. Static routes are
  `GET /memory` and `GET /memory.js`.
- `POST /api/memory/review` requests foreground discussion of an exact ID/revision and
  `correct`, `forget`, `supersede`, `do-not-use` or `export` intent. It rereads the current
  record, rejects changed or forgotten records, and shares the panel's review-request lock.
  Export review is restricted to an active user-confirmed lesson and is discussion of a
  sanitized recipe, not a raw dump, file creation or publication.
- Schema mismatch and missing initialization require explicit foreground recovery. Do not
  catch those errors and render an empty collection. Memory data and selected context remain
  subject to the [memory privacy contract](../../../docs/how-to/memory-controls-and-learning.md).

## Margo Task Progress

Optional read-only canvas over bounded task runs (`skills/chief-of-staff/scripts/task_state.py`,
adjacent to the resolved `work_state.py`). This surface never initializes, plans, claims,
charges, finishes, pauses, cancels, resumes, replans, recovers or reconciles a task run.

- Normal entry: `margo-action-desk` with `{"section":"tasks"}`, or the Tasks tab.
  Compatibility entry: `margo-task-progress` with `{}`.
- Read-only agent actions: `list` (`{"limit"?,"after"?}`), `show` (`{"id":"…"}`),
  `history` (`{"id":"…","limit"?}`), and `health`. There is no registered `review`
  action; a registered agent action never requests foreground review of an
  operation. Only the browser panel can do that, and only as a request.
- `list` defaults to 20 runs and accepts at most 50; `history` defaults to 20
  events and accepts at most 100. Cursors/IDs follow the `task_…` identity shape.
- Every command uses fixed argv (`task_state.py list --limit N [--after CURSOR]`,
  `show ID`, `history ID --limit N`, `health`) with `-B`, no shell, a bounded
  10-second timeout and a 4 MiB output cap. The browser cannot select an account,
  script, command, interpreter or state path; the CLI resolves the configured
  account exactly as `work_state.py` does.
- Reads use the core's actual `mode=ro` connection and never create, migrate or
  repair state: an uninitialized account surfaces `not_initialized` (503), never
  an empty-looking success. An unknown run surfaces `not_found` (404).
- The response contract mirrors `TaskStore.list/show/history/health` exactly,
  including `token_usage:null`, `model_cost:null`, `approval_granted:false`, and
  `limits_enforcement` text clarifying that budgets are tracked-path claims, not
  a sandbox over other host tools or a measure of all agent credits. Neither
  `show` nor `history` ever include a claim/execution token.
- The panel shows the current account, a paginated run list (`next_cursor`,
  "Load next page"), the exact plan/steps/budgets/history for a selected run,
  and distinguishes "no task runs yet" from "not initialized" or "unavailable".
  Cancelled runs with unresolved effects, and expired read claims, are shown as
  prominent warnings that explicitly say cancellation never undoes an in-flight
  effect.
- Optional buttons only **request** foreground review of one exact
  `pause`/`cancel`/`resume`/`replan`/`recover`/`reconcile` intent for the
  currently displayed run ID/revision/plan hash, via `POST /api/task/review`.
  The server resolves the run's current `show` first and rejects a stale
  revision or plan hash (409) before sending anything. The SDK message and the
  HTTP response both state, verbatim, that the request is **not** approval and
  **not** the operation itself, and that readiness still needs a fresh binding,
  preflight and, for any action step, a separate current approval. No pause,
  cancel, resume, replan, recover or reconcile endpoint executes anything.
- The two static routes are `GET /tasks` and `GET /tasks.js`, following the
  same nonce/theme substitution as `/memory`. The five API routes are
  `POST /api/task/list`, `/show`, `/history`, `/health`, and `/review`
  (request-only); they share this server's token, exact Host/Origin checks,
  `Cache-Control: no-store`, strict CSP, and the single `reviewPending` lock
  used by the other review-request routes on this same panel.

## Validation

```sh
node --test .github/extensions/margo-action-desk/*.test.mjs
node --check .github/extensions/margo-action-desk/extension.mjs
node --check .github/extensions/margo-action-desk/backend.mjs
node --check .github/extensions/margo-action-desk/server.mjs
node --check .github/extensions/margo-action-desk/app.js
node --check .github/extensions/margo-action-desk/memory-backend.mjs
node --check .github/extensions/margo-action-desk/memory-app.js
node --check .github/extensions/margo-action-desk/task-backend.mjs
node --check .github/extensions/margo-action-desk/task-app.js
```

Tests cover argv isolation, script resolution, response errors, HTTP access
controls, unsafe content rendering, revision conflicts, shared backend views,
and the review/approval boundary. The real-core integration test requires the
core modules and `MARGO_CANVAS_TEST_PARENT` pointing to an existing private
fixture directory outside any repository. It creates a unique isolated
account/state directory there, resolves its physical path, removes it afterward,
and never populates the configured user ledger. The production safe-root guard
is not bypassed. No OS temporary directory is selected automatically.
`task.test.mjs` covers the task-progress canvas the same way: allowed reads,
invalid fields, missing initialization, stale/conflicting/concurrent review
requests, hostile-content rendering, budget/unknown/cancellation states and
pagination — all against a mocked `task_state.py`-shaped backend.

For a focused change, run the matching `action-desk.test.mjs`, `memory.test.mjs` or
`task.test.mjs` with `node --test` before escalating to the full set.
Without `MARGO_CANVAS_TEST_PARENT`, report the real-core cases as skipped, not passed.
`decision.test.mjs` covers exact time thresholds, date-only days, DST, invalid inputs, freshness
versus clock and dispatch failures. `browser.test.mjs` is an opt-in real browser rehearsal:
set `MARGO_PLAYWRIGHT_MODULE` to an explicitly installed Playwright `index.mjs`, and optionally
`MARGO_BROWSER_EXECUTABLE` to an existing browser. It uses synthetic data only and never
installs a browser or package automatically. It covers actual edit/snooze/request controls,
duplicate clicks, focus preservation, offline recovery, narrow layout and light/dark modes.
Use an existing private fixture parent created deliberately outside repositories and
synchronized folders; its ancestors must satisfy the production ownership/permission checks.
Do not point this variable at a configured account database.

CI's real-core path exercises persisted state through Python and HTTP, not a live Copilot app
or a real embedding model. Host-level install/reload/open and accessibility observations are
separate, explicitly opted-in checks. The installer flag `--action-desk` / `-ActionDesk` copies
the unified workspace and compatibility entrypoints; it does not initialize account state,
enable capture or activate schedules.

## Limitations

- A matching portable CLI and Python on the app extension's PATH are required
  (`python3` on macOS/Linux, `python` on Windows).
- The work-action panel refreshes every 15 seconds while visible. Memory and task-progress
  panes refresh through their explicit controls. Unsaved payload edits are panel-local;
  persisted revisions are shared.
- The payload editor intentionally accepts JSON objects only, at most 64 KiB.
- Source revalidation, authentication recovery, creation of proposals, work-item
  transitions, and actual approved execution belong to the portable
  CLI/conversation worker, not the renderer. Local preparation requests do not permit external
  refresh or delivery. A model must follow the documented request/receipt procedure; deterministic
  synthetic tests do not prove model adherence.
- The task-progress panel never claims, charges, finishes, pauses, cancels,
  resumes, replans, recovers or reconciles a run; those remain CLI/conversation
  operations, requested here only as an explicit, non-binding foreground ask.
- Canvas APIs are experimental. Parent-session reload/open verification is
  required after installation; CLI-only hosts need no extension.
- Local storage, SDK messaging and Work IQ do not establish organizational approval or automatic
  governance inheritance. Each deployment needs its own organizational review.
