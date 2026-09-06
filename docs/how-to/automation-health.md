# Check automation coverage, output, and recovery

**Preconditions:** use the [shared shell setup](README.md#before-running-a-cli-recipe), the correct
account, and current observations from the invoking host. These helpers record local state only.
They do not perform Work IQ reads, publish to a host, or authenticate a session.

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
