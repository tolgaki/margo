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

## Validation

```sh
node --test .github/extensions/margo-action-desk/*.test.mjs
node --check .github/extensions/margo-action-desk/extension.mjs
node --check .github/extensions/margo-action-desk/backend.mjs
node --check .github/extensions/margo-action-desk/server.mjs
node --check .github/extensions/margo-action-desk/app.js
```

Tests cover argv isolation, script resolution, response errors, HTTP access
controls, unsafe content rendering, revision conflicts, shared backend views,
and the review/approval boundary. The real-core integration test requires the
core modules and `MARGO_CANVAS_TEST_PARENT` pointing to an existing private
fixture directory outside any repository. It creates a unique isolated
account/state directory there, resolves its physical path, removes it afterward,
and never populates the configured user ledger. The production safe-root guard
is not bypassed. No OS temporary directory is selected automatically.

## Limitations

- A matching portable CLI and Python on the app extension's PATH are required
  (`python3` on macOS/Linux, `python` on Windows).
- Panels refresh every 15 seconds while visible, or immediately with Refresh.
  Unsaved payload edits are panel-local; persisted revisions are shared.
- The payload editor intentionally accepts JSON objects only, at most 64 KiB.
- Source revalidation, authentication recovery, creation of proposals, work-item
  transitions, and actual approved execution belong to the portable
  CLI/conversation, not this canvas.
- Canvas APIs are experimental. Parent-session reload/open verification is
  required after installation; CLI-only hosts need no extension.
