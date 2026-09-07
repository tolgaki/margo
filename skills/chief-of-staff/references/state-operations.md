# State operations and health

Use the installed scripts, normally under `~/.copilot/skills/chief-of-staff/scripts/`.
Never assume the current directory is the skill directory. On Windows use the configured
Copilot home and Python executable rather than a Unix path.

The state helpers are portable Python 3.9+ with standard-library SQLite. No Microsoft 365
credentials are stored in the ledger, and the helpers do not call Microsoft 365 themselves.

## Account and storage

The ledger owner must be explicitly configured. An email address found in git settings or
source text is not approval to select that account. At the start of a connected run fetch
`/me?$select=id,userPrincipalName` through Work IQ and compare it to the configured owner.
If identities disagree, stop; do not ingest one account's data into another account's ledger.

From the installed skill directory, configure the explicitly confirmed owner:

```bash
python3 scripts/margo_store.py init --account OWNER_PRINCIPAL
python3 scripts/proactive_state.py import-legacy --directory state
python3 scripts/proactive_state.py status
python3 scripts/margo_doctor.py
```

`COPILOT_HOME` selects a nondefault installation; explicit `--account` and `--state-dir`
flags precede proactive subcommands. The state directory is a base: the account hash is always
appended. The default account is never inferred. Initialization configures storage, not OAuth.

Read `scripts/proactive_state.py --help` and `scripts/margo_doctor.py --help` for full contracts.
State lives under the private Copilot user directory, not the repository or a host session DB.
An explicit state-directory override is for controlled use, never an excuse to place workplace
content in a tracked or shared-sync folder.

Pause affected schedules before migration, back up both legacy state and personal files, and
use the import command. Retain the original files. Import is repeat-safe and must not infer
successful coverage or human review from the old cursor/surfaced records.

## Updating the installed app copy

Inspect the actual install root, `.margo-install`, local customizations and remote version/revision
before changing files. Do not infer the installed version from a PR title or a checkout. Use
`install.sh update --check`, then `update` (`-Check` on PowerShell). Copy updates download the exact
remote revision checked; linked installations require a separately authorized checkout update.
`update --reinstall` (`-Reinstall`) refreshes matching code while preserving personal files.
Never recommend `--force` merely because the version number has not changed: it also replaces
personal files. Remote failure is an unavailable update, not proof that local code is current.

Separate file deployment from state readiness. Before an explicitly approved memory migration,
record which affected schedules are enabled, pause them and other writers, and retain a private
SQLite-aware backup plus personal files outside the checkout. Use the installed
`memory_state.py migrate` for a schema v1 account; inspect status afterwards without resetting
state or enabling capture. Restore only previously enabled schedules when state is ready.
Saved app workflow prompt changes require their own reviewed diff; do not silently sync them.
Reload extensions through the host and start a fresh session for updated agent/skill instructions.
Report installed revision, migration outcome and any remaining reload/setup requirement separately.

## Source collection protocol

Use the coverage commands from the installed `proactive_state.py --help` for each read operation.
Source identity includes family, container/scope, optional fixed `collection_window`, and
configured account. The changing requested `window` belongs to the attempt, so ordinary hourly
mail polls share their checkpoint and health history. For provider-bound calendar delta horizons,
set `collection_window` explicitly to the same fixed start/end as `window`; do not move it with
the clock. Continuations can resume only their original requested interval. Retire completed
horizons with `coverage-retire --source-key KEY --reason "Horizon ended"` so historical
collections do not poison current freshness. Reuse of a retired source requires explicit
`reactivate:true` after revalidation. Old ambiguous window identities are retained as retired
during migration rather than silently treated as current collections.

Opaque delta tokens are separate from timestamp watermarks and bound to their
capability, query version, and read kind. Changing that contract clears incompatible tokens
and requires resynchronisation without discarding the last reliable checkpoint.
Observation collection time is separate from revision-bearing content; a later read of an
unchanged revision must not fail merely because `observed_at` changed.

1. Record the attempt, requested interval, capability and query version.
2. Persist every received observation with source identity and revision; preserve continuation
   while paging. Use a bounded call budget and report remaining pages rather than hiding them.
3. Finish as complete only after all pages are durably ingested. Successful empty enumeration
   means no matches in that exact scope. Empty semantic search is not proof of absence.
4. On partial/failure/blocked results record diagnostics and retry state, but do not advance the
   successful checkpoint. One source completing says nothing about another.
5. An expired token calls for a bounded re-sync from reliable coverage, with overlap and dedupe.
   Access denial is not transient; do not reroute or switch identities. OAuth failure requires
   the host to refresh sign-in before retrying.

Prefer a source-specific snapshot over an invented delta path only when independently supported
and not a workaround for a policy denial. Label its coverage as a snapshot rather than proof of
all changes since a prior timestamp.

Example for a fully enumerated fictional source; substitute actual discovered capabilities,
observations and run IDs, never these illustrative values:

```bash
python3 scripts/proactive_state.py coverage-start --json '{"family":"mail","scope":{"folder":"inbox"},"window":{"start":"2026-09-01T00:00:00Z","end":"2026-09-02T00:00:00Z"},"run_id":"example-run","capability":"messages.list","query_version":"1","kind":"enumeration"}'
python3 scripts/proactive_state.py coverage-page --attempt ATTEMPT_ID --json '{"page":1,"observations":[],"final":true}'
python3 scripts/proactive_state.py coverage-finish --attempt ATTEMPT_ID --json '{"status":"complete","checkpoint_at":"2026-09-02T00:00:00Z"}'
python3 scripts/proactive_state.py coverage-status
```

Capture `attempt_id` from start. Nonfinal pages carry opaque `continuation`; partial runs
resume with `resume_attempt` in a matching start request. A failure finishes with its actual
`status` and sanitised `error_class`, not a made-up empty final page. `kind:"search"` cannot
establish complete enumeration. `coverage-resume --attempt ATTEMPT_ID` returns private tokens;
never put them in the brief. Missing tools in one host binding use `binding_unavailable`, not
a tenant-wide authentication claim.

## Publication protocol

One anchor leases one batch. The underlying brief does not lease again. Store the completed
output in private durable storage and record precisely which queued item IDs it includes.
Publish/record its receipt before acking those items. If the host cannot prove delivery, record
availability of the local artefact without claiming that the user reviewed it.

Never use bare ack-all, retire another run's batch, or call an item delivered merely because it
was drained. On failure release the batch; after a crash its lease expires. Omitted items remain
pending. Legacy seen/mark commands are compatibility bookkeeping, not substitutes for receipts.

```bash
python3 scripts/proactive_state.py queue-add --json '{"id":"example-item","revision":"v1","title":"Example proposal","family":"mail","scope":{"folder":"inbox"}}'
python3 scripts/proactive_state.py queue-drain --owner RUN_ID --lease-seconds 900 --format json
python3 scripts/proactive_state.py publication-record --batch BATCH_ID --json '{"id":"OUTPUT_ID","content":"Actual completed output","item_keys":["RETURNED_ITEM_KEY"],"status":"available","receipt":{"kind":"local"}}'
python3 scripts/proactive_state.py queue-ack --batch BATCH_ID --receipt OUTPUT_ID
```

Capture the actual batch and `item_key` values from drain. Renew a lease before it expires
using `queue-renew --batch BATCH_ID --owner RUN_ID`; release unfinished work with
`queue-release`. `publication-show OUTPUT_ID` retrieves the stored output. Use
`publication-list` to discover it later; call `publication-review` only after actual human review.
An empty queue needs no invented dummy item; the anchor can still persist its standalone output.

For that case omit `--batch` and all item membership, and do not call `queue-ack`:

```bash
python3 scripts/proactive_state.py publication-record --json '{"id":"UNIQUE_OUTPUT_ID","content":"Actual completed brief","status":"available","receipt":{"kind":"local"}}'
```

Standalone prepared outputs may later become available/published independently of a queue lease.
They appear in `publication-list` with zero included items and zero pending acknowledgements.

## Doctor and workflow snapshots

Doctor reads configuration completeness, installed-file provenance, source health, delivery
backlog, and supplied workflow snapshots. Obtain snapshots through `list_workflows`; never query
or edit the host's internal database.

Store the actual capture time with the supplied values, not a fresh timestamp on an old export:

```json
{
  "captured_at": "2026-09-05T18:00:00Z",
  "workflows": [{
    "id": "returned-workflow-id",
    "enabled": true,
    "status": "unknown",
    "expected_at": null,
    "last_started_at": null,
    "output_available": null
  }]
}
```

Map the host's `latestRun.status` and `latestRun.startedAt` to `status` and `last_started_at`;
use returned `nextRunAt` for the next expected run where available. Missing values remain unknown.
Map only known error evidence to sanitised categories. A replacement with no run is unknown,
not successful. Add actual `prompt` and `expected_prompt_sha256` from the reviewed automation
body to detect drift. Call `scripts/margo_doctor.py --snapshot <private-snapshot.json>`.

Keep snapshots timestamped and private. A stale or absent snapshot is unknown, not a current
healthy schedule. Inspect latest start, completion, failure, next expected run, configured
workspace, and output availability independently. Disabled replaced jobs are historical, not
current failures demanding a second replacement.

Deduplicate failures by source/scope/error episode. Surface a first failure at the next anchor
and a worsening state after repeated missed source cadences; surface recovery once. A source
with no successful checkpoint remains uncovered even if the most recent run returned exit zero.

## Approval and recovery

Doctor is read-only. Re-enabling schedules, changing personal rules, confirming commitments,
creating Outlook drafts, or sending anything requires the appropriate explicit approval.
Unattended automation may update its own coverage and proposal state only.

On damaged state, stop writes and surface the error. Preserve the files for recovery rather
than replacing them with an empty success-shaped ledger. On rollback, retain current database
history; do not run old JSON writers concurrently with migrated SQLite state.
