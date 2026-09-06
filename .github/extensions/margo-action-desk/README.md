# Margo Action Desk

Optional Copilot app canvas over Margo's portable work ledger. The app supplies
`@github/copilot-sdk`; no package installation or build step is required.

## Runtime contract

- Canvas: `margo-action-desk`. Open input: `{}`.
- Read-only agent actions: `list`, `show` (`{"id":"…"}`), and `refresh`.
- Data belongs to the CLI's configured account, not a canvas instance. Every read
  and local mutation goes through `work_state.py`; this extension has no database.
- Resolve the script from this project's
  `skills/chief-of-staff/scripts/work_state.py` (`../../../` from the extension),
  then an installed destination's adjacent `skills/` directory (`../../`), then
  `$COPILOT_HOME/skills/chief-of-staff/scripts/work_state.py` (default
  `~/.copilot/skills/…`), checking each for an existing regular file. This also
  supports the installer's custom `--dest` layout. Missing core/configuration is
  shown as setup needed.
- The CLI resolves `~/.copilot/margo/config.json`. Browser input cannot select
  an account, script, command, interpreter, configuration file, or database path.
- Local UI operations on action proposals are payload revision, defer, and
  dismiss. Work items and typed records are read-only because their transitions
  may require human evidence. Revisions require the displayed revision/hash; a
  conflict leaves the unsaved editor text intact.
- The review button sends an SDK `session.send` message with item ID,
  revision and action hash, requesting interactive foreground review. It does
  not store approval or perform an outbound action. Only a subsequent explicit
  user confirmation in the conversation can authorize the exact action.

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

The two HTTP read routes are `GET /api/items` and `GET /api/items/:id`.
User-interface POST routes are `/api/items/:id/revise`, `/defer`, `/dismiss`,
and `/review`; there is deliberately no `/approve` or `/execute`.

### Portable CLI adapter

Commands use fixed Python argv without a shell:

```text
work_state.py list --view all --json
work_state.py show ID --json
work_state.py edit ID --revision N --expected-hash HASH --input -
work_state.py defer ID --revision N --expected-hash HASH --until ISO_TIMESTAMP
work_state.py dismiss ID --revision N --expected-hash HASH
```

The list contract is `{schema_version:1, account, items, actions, records}`; show
and mutations return a raw record. The HTTP adapter combines all three lists
into display rows and wraps individual rows as `{item:…}`. `action_hash` remains
the authoritative hash; `payload_hash` is a display compatibility alias. Before
each mutation the adapter checks that exact hash and revision, then the core
checks both `--revision` and `--expected-hash` in the same transaction. Edit,
defer and dismiss all return a new immutable revision/hash. The adapter never
passes an approval command.

For edit, the adapter preserves the original action's target, rationale, source
references, fingerprints, work-item association and optional dependencies while
replacing only `payload`. The complete document goes through subprocess stdin,
not argv or temporary files. Structured CLI failures on stderr are propagated
as HTTP errors; arbitrary stderr and stack traces are not exposed.

## Margo Task Progress

Optional read-only canvas over bounded task runs (`skills/chief-of-staff/scripts/task_state.py`,
adjacent to the resolved `work_state.py`). This surface never initializes, plans, claims,
charges, finishes, pauses, cancels, resumes, replans, recovers or reconciles a task run.

- Canvas: `margo-task-progress`. Open input: `{}`.
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

## Limitations

- A matching portable CLI and Python on the app extension's PATH are required
  (`python3` on macOS/Linux, `python` on Windows).
- The work-action panel refreshes every 15 seconds while visible. Memory and task-progress
  panes refresh through their explicit controls. Unsaved payload edits are panel-local;
  persisted revisions are shared.
- The payload editor intentionally accepts JSON objects only, at most 64 KiB.
- Source revalidation, authentication recovery, creation of proposals, work-item
  transitions, and actual approved execution belong to the portable
  CLI/conversation, not this canvas.
- The task-progress panel never claims, charges, finishes, pauses, cancels,
  resumes, replans, recovers or reconciles a run; those remain CLI/conversation
  operations, requested here only as an explicit, non-binding foreground ask.
- Canvas APIs are experimental. Parent-session reload/open verification is
  required after installation; CLI-only hosts need no extension.
