# Check automation coverage, output, and recovery

**Preconditions:** use the [shared shell setup](README.md#before-running-a-cli-recipe), the correct
account, and current observations from the invoking host. These helpers record local state only.
They do not perform Work IQ reads, publish to a host, or authenticate a session.

## Find the missing brief before rerunning it

Start in conversation:

> I cannot find today's morning brief. Check the schedule and latest run, then source coverage,
> then durable output and host delivery. Do not enable another schedule or rerun blindly.

| Question | Evidence to inspect | What it does not prove |
| --- | --- | --- |
| Was the routine enabled and invoked? | Current host workflow listing/latest run, or the actual OS/container scheduler | That mail, calendar and Teams were read |
| What was covered? | Per-source coverage windows and completion states | That a usable output was created |
| Where is the result? | Publication record, content and local/host receipt | That you saw or reviewed it |
| Where did work stop? | Task progress and any interrupted claim | That an unknown write had no effect |

For app schedules, the app must be running and the machine awake. The local `proactive status`
command reports queue/publication/coverage state; it cannot tell you whether a saved app workflow
is enabled. Doctor needs an actual host snapshot for that view. If the host does not expose it,
inspect the scheduler manually and leave the missing evidence unknown.

Choose one action after diagnosis: retrieve an existing output, repair sign-in, review an exact
schedule change, or [resume a bounded task](task-progress-and-recovery.md). Do not create a second
scheduler owner for the same routine. See [schedule setup](../proactive.md) before enabling one.
Most users can stop here; the numbered sections are the advanced collector/receipt protocol.

## 1. Ask the questions separately

> Show each source's last reliable coverage, gaps and retry state. Then show the latest durable
> brief, which queued items it includes, and whether the host delivered it or I reviewed it.

```sh
proactive coverage-status
proactive status
proactive publication-list --limit 10
```

**Expected:** separate source coverage and publication evidence. A workflow exit zero, a drained
queue, and a local output are three different facts. None means the user reviewed anything.
`coverage-status` redacts private continuation/delta tokens.

## 2. Record one bounded source collection

**Preconditions:** discover a supported read capability in the current binding, verify account,
choose an explicit interval and page/call budget. Each source is independent: inbox success does
not establish Teams, sent-mail, or calendar coverage.

Save `coverage-start.json` with actual values. The following is a template, not a claim that this
capability or interval was collected:

```json
{
  "family": "mail",
  "scope": {"folder": "inbox"},
  "window": {
    "start": "2026-09-01T00:00:00Z",
    "end": "2026-09-02T00:00:00Z"
  },
  "run_id": "ACTUAL_RUN_ID",
  "capability": "ACTUAL_DISCOVERED_READ_CAPABILITY",
  "query_version": "1",
  "kind": "enumeration",
  "cadence_seconds": 3600
}
```

```sh
proactive coverage-start --json - < coverage-start.json
```

Save the returned `attempt_id` and `source_key`. Source identity is account + family + scope +
optional `collection_window`. The ordinary moving `window` belongs to this attempt; it does not
create a new source for every hourly poll.

For a provider-bound calendar delta horizon, set `collection_window` to the same fixed start/end
object as `window`. Both must match, and the horizon must not slide on continuation. After its last
attempt finishes, retire that collection:

```sh
proactive coverage-retire --source-key SOURCE_KEY --reason "Horizon ended"
```

A retired source stays in history but is excluded from current monitoring. Reuse requires actual
revalidation and `reactivate:true` on `coverage-start`. Do not retire a failing active source just
to make health look better.

### Persist pages before declaring completion

Save an actual page as `page.json`, with contiguous page numbers starting at 1:

```json
{
  "page": 1,
  "observations": [
    {
      "id": "ACTUAL_SOURCE_OBJECT_ID",
      "revision": "ACTUAL_SOURCE_REVISION",
      "link": "https://example.com/ACTUAL_SOURCE_LINK",
      "observed_at": "2026-09-02T00:01:00Z"
    }
  ],
  "final": false,
  "continuation": "ACTUAL_PROVIDER_CONTINUATION"
}
```

```sh
proactive coverage-page --attempt ATTEMPT_ID --json - < page.json
```

Every page and its observations are committed together. The final page uses `final:true` and
no continuation. A genuinely empty, fully enumerated collection still records a final page with
`observations:[]`. Never fabricate that page after an error or empty semantic search.

Only after all pages of the exact requested enumeration are durably recorded, save
`complete.json`:

```json
{
  "status": "complete",
  "checkpoint_at": "2026-09-02T00:00:00Z",
  "watermark": "2026-09-02T00:00:00Z"
}
```

Use the actual reliable checkpoint/watermark, not these example dates.

```sh
proactive coverage-finish --attempt ATTEMPT_ID --json - < complete.json
proactive coverage-status
```

**Expected:** successful checkpoint advances for this source only. It cannot be future-dated or
regress. `watermark` is optional, cannot regress, and cannot exceed the checkpoint. Optional
`covered_start`/`covered_end` must match the requested interval.

If the budget expires with more pages, use `status:"partial"` instead, optionally with an actual
sanitized error/retry. Do not include a checkpoint, watermark, covered interval, or delta token in
a non-complete finish. `kind:"search"` and `kind:"snapshot"` cannot finish as complete enumeration;
record their actual partial/unknown coverage rather than claiming all changes were observed.

### Resume without corrupting cursor meaning

```sh
proactive coverage-resume --attempt ATTEMPT_ID
```

This returns private token/checkpoint details for the collector, not a new running attempt.
Do not paste tokens into a brief, shared document, or logs.

Start a new attempt using `resume_attempt` with the previous attempt ID. Keep family, scope,
collection horizon, requested interval, capability, query version, and read kind unchanged.
Only compatible partial/failed attempts with usable nonfinal continuations can resume. Continue
from the next page number; copied page history is retained.

Opaque delta tokens are not timestamp cursors. They are bound to capability + `query_version` +
`kind`. Bump the query version when its contract changes; an incompatible stored token is cleared
and resynchronization is required without discarding the last reliable checkpoint. `observed_at`
is collection time, excluded from revision-bearing observation content.

The legacy `cursor-get`/`cursor-set` commands expose timestamp hints only. They cannot prove
successful collection or replace `coverage-finish`. Old ambiguous window identities are retained
as retired during migration, not silently reused as current sources.

## 3. Handle failure according to evidence

Save the actual failure in `failure.json` and run
`proactive coverage-finish --attempt ATTEMPT_ID --json - < failure.json`.

| Observation | Finish fields and next step |
| --- | --- |
| Read tools absent in this host | `failed`, `binding_unavailable`; repair this binding |
| OAuth refresh required | `blocked`, `authentication_required`; refresh sign-in in the host |
| Policy/access denial | `blocked`, `access_denied`; no automatic retry or identity workaround |
| Throttled | `partial` or `failed`, `throttled`; honor actual retry timing |
| Expired delta token | `failed`, `expired_token`; bounded resync with overlap and deduplication |
| Timeout/network failure | `partial` or `failed`, matching error; retain durable pages |

Use keys `status` and `error_class`. Retry timing uses `retry_after_seconds` and/or an
offset-bearing `next_retry_at` only when appropriate; blocked sources cannot schedule retries.
After actual authentication recovery, an explicit `reauthenticated:true` start is required.
The flag does not authenticate anything. Expired or denied continuations cannot be resumed.

**Expected:** partial/failed/blocked attempts retain the previous reliable checkpoint and surface
the gap. A missing tool is not proof of a tenant-wide outage. Do not route around policy denials.
Use a snapshot alternative only when independently supported and allowed, clearly labeled as such.

## 4. Lease one batch and acknowledge only recorded output

**Preconditions:** this is an anchor preparing a durable output. It alone owns the lease; the
underlying brief routine must not drain again. A queue item needs stable `id`, `revision`, and
renderable content; `family` and object-valued `scope` distinguish its source.

To enqueue an actual prepared item from private `queue-item.json`:

```sh
proactive queue-add --json - < queue-item.json
proactive queue-drain --owner RUN_ID --lease-seconds 900 --limit 20 --format json
```

**Expected:** a JSON array of leased rows with `batch`, `owner`, `item_key`, and expiry. Keep the
actual returned batch and exact keys. If empty, use the standalone recipe below.

Prepare the complete output, then save `publication.json`:

```json
{
  "id": "UNIQUE_OUTPUT_ID",
  "content": "ACTUAL_COMPLETED_OUTPUT",
  "item_keys": ["RETURNED_INCLUDED_ITEM_KEY"],
  "status": "available",
  "receipt": {"kind": "local"}
}
```

Use only keys actually included in the output, not every drained key by default. Then:

```sh
proactive publication-record --batch BATCH_ID --json - < publication.json
proactive queue-ack --batch BATCH_ID --receipt OUTPUT_ID
proactive publication-show OUTPUT_ID
```

**Expected:** output is durably discoverable, then only its receipt-covered items are acknowledged.
Local `available` means retrievable here, not delivered by the app or reviewed by a human.
For known host delivery, record `status:"published"` and a real receipt with `kind:"host"`,
`receipt_id`, `location`, and `published_at`. The helper records evidence; it never publishes.
`prepared` output has no receipt and cannot acknowledge anything.

Use a unique output ID for each output. Exact recording replay is safe; changed content/status
under the same ID fails. `publication-publish OUTPUT_ID --json -` can move a prepared output to
available/published using actual receipt JSON. Once available/published, its receipt is immutable;
this is not an in-place local-to-host delivery upgrade.

### Empty queue: still save the brief

Do not invent a queue item just to get a batch. Save `standalone-output.json`:

```json
{
  "id": "UNIQUE_STANDALONE_OUTPUT_ID",
  "content": "ACTUAL_COMPLETED_BRIEF_WITH_ITS_COVERAGE_QUALIFICATIONS",
  "status": "available",
  "receipt": {"kind": "local"}
}
```

```sh
proactive publication-record --json - < standalone-output.json
proactive publication-list --limit 10
```

**Expected:** a standalone output with zero included items and no pending acknowledgements.
Omit `--batch` and membership fields; do not call `queue-ack`. A standalone prepared output can
become available/published later without a queue lease.

### Recover a lease safely

Before expiry, renew only your own batch:

```sh
proactive queue-renew --batch BATCH_ID --owner RUN_ID --lease-seconds 900
```

If unfinished, release it:

```sh
proactive queue-release --batch BATCH_ID --owner RUN_ID
```

Omitted items remain leased until release/expiry, then become pending again. Never steal another
run's lease or use bare ack-all. After a crash, inspect `publication-list`/`publication-show`
before preparing duplicates. An expired receipt/batch cannot acknowledge a newly owned lease;
reconcile membership rather than faking delivery.

Call `publication-review OUTPUT_ID --at ACTUAL_REVIEW_TIMESTAMP` only after actual human review.
A local receipt or successful scheduled run is not review evidence.

## 5. Inspect doctor with a real workflow snapshot

> Use the host's workflow listing to capture current schedules and latest runs. Show missing
> coverage, output gaps, and prompt drift without querying the host's internal database.

Obtain the snapshot through the app's `list_workflows` tool. Save `workflow-snapshot.json`
privately with its actual capture time and returned IDs:

```json
{
  "captured_at": "2026-09-05T18:00:00Z",
  "workflows": [
    {
      "id": "RETURNED_WORKFLOW_ID",
      "enabled": true,
      "status": "unknown",
      "expected_at": null,
      "last_started_at": null,
      "output_available": null
    }
  ]
}
```

Map known `latestRun.status`/`latestRun.startedAt` to `status`/`last_started_at`; map `nextRunAt`
to `expected_at` when returned. Unsupported/missing states stay `unknown`. Capture output
availability independently. Include actual `prompt` and `expected_prompt_sha256` from the
reviewed automation body when checking prompt drift; hashing the current prompt alone is not a
baseline. Never refresh an old snapshot merely by replacing its timestamp.

```sh
python3 "$COPILOT_HOME/skills/chief-of-staff/scripts/margo_doctor.py" \
  --account "$MARGO_ACCOUNT" --state-dir "$MARGO_STATE_DIR" \
  --install-root "$COPILOT_HOME" --snapshot workflow-snapshot.json --strict
```

**Expected:** a report covering configuration, managed-file provenance, source health, delivery
backlog, and supplied host evidence. `--strict` returns 1 when not healthy; without it, a completed
report may exit zero while reporting attention needed. Invalid input/state errors exit 2.
An absent/stale snapshot (default maximum age: 86400 seconds) is unknown, not healthy.

Doctor is read-only operationally: it diagnoses rather than repairing schedules or confirming
work. No host database is inspected. A replacement job with no run is unknown; disabled historical
jobs are not active failures needing another replacement. Inspect source coverage even when the
host says `completed`. Preserve damaged state instead of resetting it.

## 6. Choose the schedule's safety boundary

Copy deployment and schedule synchronization are separate. Review the proposed saved-workflow
diff before approving a sync; preserve unrelated jobs and intentional customizations.

The shell/PowerShell scheduled wrappers deny the four Work IQ mutation tools:
`do_action`, `create_entity`, `update_entity`, and `delete_entity`. App workflows do not inherit
that deny list: read-only there is a prompt instruction under the app's permissions, a weaker
boundary. Wrappers still allow other tools, so their Work IQ denial is not a general sandbox.
Neither path authorizes unattended sends, calendar changes, or external drafts.

See [schedule setup](../proactive.md), [automation definitions](../../automations/README.md), and
[full state contracts](../../skills/chief-of-staff/references/state-operations.md).

## Feature reference

The numbered sections above are the full CLI walkthrough. These are the stable per-feature entry
points the [feature catalog](../feature-catalog.json) links to. The six scheduled routines share
the boundary in [§6](#6-choose-the-schedules-safety-boundary); their tier, cadence and prompt
come from each `automations/*.md` manifest. Anchors may synthesize; hourly/ambient scans cannot
call `workiq-ask`. Nothing here replaces the manifest as the source of the exact schedule.

### Automation morning

Get the full daily brief automatically on weekday mornings, with no chat interaction needed.
Try it: ask Margo to inspect the actual schedule and latest run, then use `proactive
publication-list --limit 10` for stored outputs. What you'll see after a successful run: the
brief and its output receipt, following the [morning brief](../../automations/morning-brief.md)
manifest. The configured start time is not a guarantee of completed delivery at that instant.
Nothing is sent, posted, or changed — the unattended contract in [§1](#1-ask-the-questions-separately)
applies fully. Change your mind: review a pause or edit through [your scheduler](../proactive.md).
Your data: same private output-delivery and source-coverage records described in this file.
If something goes wrong: check the host's actual latest-run evidence and doctor with a fresh
snapshot; local state alone cannot establish that a host run was missed or completed.
Implemented, procedure (schedule invocation is deterministic; brief content is model-authored).
Since 1.0.0.

### Automation eod

Get an end-of-day/catch-up wrap-up automatically on weekday evenings. Try it: inspect the
host's latest run, then `proactive publication-list --limit 10` for the corresponding output.
What you'll see: the anchor owning one leased batch and
persisting the output before acknowledging only what it included — see
[§4 Lease one batch and acknowledge only recorded output](#4-lease-one-batch-and-acknowledge-only-recorded-output).
Nothing sends; drafts may be prepared and held only. Change your mind: review a pause/edit in
[your scheduler](../proactive.md). Your data: same output-delivery boundary as automation morning.
If something goes
wrong: the underlying brief routine must not drain the leased batch again — that's a bug to
report, not a retry to attempt yourself. Implemented, procedure. Since 1.0.0.

### Automation week ahead

Get next week's shape automatically on Sunday afternoon, before Monday. Try it: inspect the
actual schedule/latest run and then `proactive publication-list --limit 10`.
What you'll see: the same week-ahead output described in
[outcomes and meetings](outcomes-and-meetings.md#week-ahead), persisted as a receipt. Nothing
sends or changes your calendar. Change your mind: review a pause/edit in
[your scheduler](../proactive.md). Your data: same
boundary as the other anchors. If something goes wrong: missing estimates or calendar coverage
stay explicit gaps in the scheduled output too, never smoothed into a false-looking plan.
Implemented, procedure. Since 1.0.0.

### Automation commitments

Get a weekly commitments-ageing pass and ambient digest every Friday afternoon. Try it: inspect
the actual schedule/latest run and the latest stored publication.
What you'll see: silent-resolution checked before ageing anything, then
versioned nudge proposals held in the action desk (never sent) plus a five-item digest rendered
from the week's ambient queue. Nothing sends.
Change your mind: review a pause/edit in [your scheduler](../proactive.md).
Your data: same boundary as the other anchors, plus the [follow-through](commitments-and-action-desk.md#follow-through)
ledger. If something goes wrong: coverage is recorded per source, with failed/incomplete
resolution checks left explicitly unknown. Implemented, procedure. Since 1.0.0.

### Automation hourly

Get cheap, usually-silent hourly checks for anything that clears the interrupt test. Try it:
check `proactive coverage-status` for recent sweep attempts. What you'll see: silence on most
runs when source coverage is healthy, with non-urgent findings queued. An item meeting the
documented interrupt test may surface privately through the host's supported output channel;
that is not permission to contact a colleague. Nothing sends externally; `workiq-ask` is never
called in this tier. Change your mind: review a pause/edit in [your scheduler](../proactive.md).
Your data: same
source-coverage boundary as other tiers. If something goes wrong: a recap-pending meeting that
hits an access denial is reported blocked, not retried through another route. Implemented,
procedure. Since 1.0.0.

### Automation ambient

Get a quiet daily scan for slow-moving drift — commitment ageing, relationship cadence, stale
PRs, calendar hygiene, unread documents — queued for the Friday digest and never interrupting. Try
it: check `proactive queue-list` to see what's queued. What you'll see: items promoted only the
run they first cross a threshold, never re-promoted every day after. Nothing sends or changes
anything externally. Change your mind: review a pause/edit in [your scheduler](../proactive.md).
Your data: same source-coverage and
output-delivery boundary as the other tiers. If something goes wrong: a partial or failed read
does not advance that source's successful checkpoint. Implemented, procedure. Since 1.0.0.

### Source coverage

Trust that a brief or sweep actually covered its sources — see
[§2 Record one bounded source collection](#2-record-one-bounded-source-collection) and
[§3 Handle failure according to evidence](#3-handle-failure-according-to-evidence). Try it: *"Show
each source's last reliable coverage, gaps and retry state."* What you'll see: per-source
attempts, completeness, and retry state — one source succeeding never advances another source's
checkpoint. What needs your decision: nothing — this is a local diagnostic record with no
external effect. Change your mind: not applicable; this only reports state. Your data: coverage
history lives in the private account-scoped database. If something goes wrong: a missing read
capability is reported as `binding_unavailable`, never mistaken for a tenant-wide outage.
Implemented, runtime (deterministic coverage/retry state machine with tests). Since 1.1.0.

### Output delivery

Know whether a scheduled output was actually delivered — see
[§4 Lease one batch and acknowledge only recorded output](#4-lease-one-batch-and-acknowledge-only-recorded-output).
Try it: *"Show the latest durable brief, and whether the host delivered it or I reviewed it."*
What you'll see: a drained queue, a workflow exit code, and a delivered/reviewed brief reported as
three separate facts, never conflated. What needs your decision: `publication-review` records only
an actual human review — nothing here manufactures that evidence for you. Change your mind: not
applicable; this only reports and records real delivery state. Your data: publication receipts and
leases live in the private account-scoped database. If something goes wrong: an expired lease is
recovered explicitly, never silently stolen from another run. Implemented, runtime (deterministic
lease/receipt state machine with tests). Since 1.1.0.

### Doctor

Get one honest health report covering configuration, managed-file drift, source coverage,
task-run health, and delivery backlog — see
[§5 Inspect doctor with a real workflow snapshot](#5-inspect-doctor-with-a-real-workflow-snapshot).
Try it: run `margo_doctor.py` with a captured workflow snapshot. What you'll see: a report that
can exit zero while still flagging attention needed (use `--strict` to fail on that); task
tracking that was never set up reports `not-initialized`, not a failure. What needs
your decision: nothing — doctor never repairs anything itself. Change your mind: not applicable.
Your data: doctor reads existing state; it writes nothing new. If something goes wrong: an
absent or stale snapshot (default max age 86400 seconds) is reported unknown, never healthy.
Implemented, runtime (deterministic diagnostic script with tests). Since 1.1.0.
