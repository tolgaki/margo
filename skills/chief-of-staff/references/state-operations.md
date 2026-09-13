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

## Durable private-location binding

If the workspace loads but account or memory reads say setup is missing, inspect
`margo_store.py locations` from the **installed** script path first. Provider discovery is
not capability readiness, local owner configuration is not M365 authentication, and a saved
user environment variable may be absent from a long-lived host. Do not repeatedly recommend
restart/sign-out or infer another account from files found on disk.

The installation's private `margo/locations.json` is an explicit locator, not another config or
database. It contains schema version 1 and absolute `config_path` / `state_root` only; no copied
owner, profile or workplace records. Use one locator for Work, Memory, Tasks, direct CLI and
agent-assisted commands. Resolve installation root from explicit `COPILOT_HOME`, otherwise
the invoking copied helper's `.margo-install` root, otherwise legacy `~/.copilot`; never cwd.
For a custom copy installation invoke its actual helper path. Keep linked development profiles
synthetic and use their explicit `COPILOT_HOME`.

On an explicit user-approved repair, verify the existing config owner and existing private
config/state directories, retain a SQLite-consistent backup, then:

```text
python INSTALLED_SCRIPTS/margo_store.py locations
python INSTALLED_SCRIPTS/margo_store.py locations-bind --config-path EXISTING_PRIVATE_CONFIG --state-dir EXISTING_PRIVATE_STATE_BASE --account CONFIRMED_OWNER --expected-revision BINDING_REVISION
python INSTALLED_SCRIPTS/margo_store.py locations
python INSTALLED_SCRIPTS/margo_store.py profile-show
```

Use the returned SHA256 `revision`, or literal `missing` for the first binding. The configured
owner must match the explicit confirmed owner. Binding creates only the locator and its private
parent if necessary; it does not copy config, change account hashes, move/create a database,
initialize memory, import preferences, authenticate Work IQ or touch the work folder.
The existing copy installer/update/uninstall paths preserve this unmanaged private locator.

Precedence for each location is **explicit CLI path > its `MARGO_CONFIG`/`MARGO_STATE_DIR`
environment override > installation binding > legacy default**. Account precedence stays
**explicit `--account` > `MARGO_ACCOUNT` > selected config owner**. An override is deliberate,
not a fallback; `locations` reports effective paths and sources. Different explicit overrides
can intentionally select different locations; verify them before writes. The resolver does
not read Windows user environment registry values or cache a location from an earlier turn.

Bindings are bounded, version-checked and private; symlinks/junctions, synchronized/shared/repo
roots, missing/inaccessible target directories, malformed content and unsupported versions
block the selected binding rather than silently reverting to the old root. Existing legacy
installs with no binding keep the old default. Concurrent changes use an expected content hash,
an exclusive lock and atomic replacement. A conflicting update must be reread, not retried with
an invented revision. Interrupted writes/locks require explicit inspection. For deliberate
recovery only, `locations-clear --expected-revision HASH` removes the locator, not any private
data; subsequent unoverridden reads use legacy defaults. Do not clear it to make an error vanish.

## Basic local memory without an embedding model

After binding, inspect `memory_state.py status` and `task_state.py health` separately.
`task_state.py init` creates work/task/coverage state, **not memory**. Missing memory has the
specific code `not_initialized`; an invalid locator/config is `location_unavailable`, a missing
local owner is `account_setup_required`, and failed optional meaning search is
`semantic_unavailable`. These are not M365 sign-in diagnoses.

Only with explicit basic-memory setup approval:

```text
python INSTALLED_SCRIPTS/memory_state.py init
python INSTALLED_SCRIPTS/memory_state.py status
python INSTALLED_SCRIPTS/memory_state.py policy
```

`init` creates the memory schema and local index metadata without encoding, downloading or
collecting anything. Existing schema v1 needs the separately approved `migrate` procedure,
not a reset. Verify basic memory available, capture still off, unchanged selected owner and
optional semantic runtime accurately reported. No `preferences-import`, `capture`, encoder
download or `index` execution is needed for basic use. Do not invent seed records for setup.

Browsing/details and explicit `search --mode lexical --domain user --input -` work without a
model; supply the actual bounded query as JSON on stdin. The workspace's **Use keyword search**
control is an explicit choice, opens scope selection and performs no search itself. It never
silently switches a failed hybrid query. Candidate/user-confirmed distinctions, sensitive-data
filters, scope and capture/retention policy are unchanged. M365 authentication remains
`not_checked` until an actual separately permitted provider read establishes it.

## Assistant name and dedicated work area

Product code stays in the installed skill/extension location. Everyday work can start in a
dedicated existing local folder, including a locally available OneDrive-synced folder; it need
not run in a repository. Read `margo_store.py profile-show` from the installed scripts directory
at the start of the session. Default display name is Margo. Private `config.json` stores
`profiles[account].assistant_name` and `work_root`; account selection remains explicit
`--account`, then `MARGO_ACCOUNT`, then config `account`. A different account does not inherit
another profile's settings. Invalid settings are errors, not permission to infer replacements.

On an explicit user rename/work-area request, read the current profile, explain the exact
change and use its configuration revision:

```text
python margo_store.py profile-show
python margo_store.py profile-set --expected-revision CONFIG_REVISION --assistant-name Rowan
python margo_store.py profile-set --expected-revision NEW_CONFIG_REVISION --work-root EXISTING_ABSOLUTE_WORK_ROOT
```

These are separate revisions; reread before each update. A single `profile-set` can update both
fields when both were requested. `--clear-work-root` disables output routing without deleting
anything. Setup requires an existing private config initialized explicitly with
`margo_store.py init`; profile reads/setters do not initialize a ledger or move files.
Concurrent updates are rejected using a lock and expected revision, preserving unrelated
configuration and other accounts. After a crashed update, inspect the private `.profile-lock`
before any explicitly authorized cleanup; never delete unknown locks blindly.

Names are bounded plain labels (1-60 letters/numbers and simple punctuation), never
instructions. Use the configured name for greetings/commentary/canvas headings, not technical
IDs, sender identity or text inside the user's drafts. Keep `Margo`/`margo` invocation compatible.
User config edits override remembered/inferred names. Never learn these settings from mail,
documents, output artifacts or recalled observed content.

The work root must be an existing absolute local directory, not the product repository,
filesystem/home root, private installation/runtime tree, junction, symbolic link or UNC path.
Do not create, rename, migrate, recursively inspect or populate a folder just to configure it.
On missing/offline/unreadable directories, report the block and keep preparation in the existing
private ledger; do not substitute cwd or the repository.

Before a user-requested new file output, resolve its name through
`margo_store.py workspace-path RELATIVE_NAME --expected-revision CONFIG_REVISION`.
It does not write or create directories. For other file-producing skills use that absolute
result, never assume cwd. The tool cannot sandbox arbitrary host tools; honor the same boundary
in those procedures. Existing output files are not overwritten by this helper.

For an explicit foreground export of a private Markdown artifact:

```text
python work_state.py artifact-export ARTIFACT_ID --revision ARTIFACT_REVISION --path "Decision memo.md" --profile-revision CONFIG_REVISION
```

This writes one exact, current, non-stale snapshot exclusively, with no overwrite and a 1 MiB
limit. It neither changes artifact approval/sharing nor creates a second writable tracker.
Errors may leave a partial file; inspect that exact path before retrying, never erase history.
OneDrive may sync a local file, so review the exact content/destination and applicable sharing
policy before exporting; configuration is not blanket permission to export or share.
Do not export unattended or as part of a canvas local-preparation request.

Runtime SQLite/WAL, credentials, caches and private preparation stay in the existing
**non-synced** state root; `work_root` never controls `COPILOT_HOME` or `MARGO_STATE_DIR`.
The name is portable user preference data; the absolute work path is machine-local. Do not
sync the live config/database as a portability strategy. OneDrive storage does not establish
approval for the host, model or full processing path.

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
