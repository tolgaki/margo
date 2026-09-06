# Capture commitments and review the next action

**Preconditions:** complete [setup](setup-and-migration.md) and load the
[shared shell setup](README.md#before-running-a-cli-recipe). For connected reads, verify the
invoking Work IQ binding and account. All CLI writes below are local.

## 1. Start with an ask, not an inferred promise

Try:

> Rafa asked for a review. Find the request and later replies, capture a candidate with its
> evidence, and show me what is known about owner, due date, and current status. Do not send.

Margo should show the source, coverage gaps, and candidate. An old request without a later reply
in a partial search is not automatically open, overdue, or unresolved.

Then review the specific record in the conversation:

> This is mine to do. Confirm this exact candidate with no deadline; none was agreed.

That human decision confirms an obligation, not a reply. Next:

> Prepare a private response for Rafa and show the exact recipients and body for review.

An action can be prepared locally. A later request to create an Outlook draft or send requires
its own exact approval. "Handle it", a standing preference, or accepting the commitment does not
approve a particular outward action.

## 2. Store evidence with the right identity

If the provider returned canonical IDs, `source.json` has this shape. This is a template:
replace uppercase values with actual observations before ingestion.

```json
{
  "family": "mail",
  "scope": "inbox",
  "external_id": "ACTUAL_CANONICAL_MESSAGE_ID",
  "revision": "ACTUAL_PROVIDER_REVISION",
  "evidence": {"quote": "Please review the example specification."},
  "web_link": "https://example.com/ACTUAL_SOURCE_LINK",
  "sensitivity": "unknown"
}
```

`family`, `scope`, `external_id`, `revision`, `evidence`, and `web_link` are required.
Work-ledger `scope` is a string, unlike proactive coverage's object-valued `scope`.
`observed_at` is optional and must have an explicit time-zone offset. Keep excerpts minimal.
Use an observed provider revision or a deterministic content revision, not a made-up timestamp.

```sh
work source --input source.json > source-ref.json
```

**Expected:** `{"source_id":...,"revision":...,"fingerprint":...}`. Preserve this exact object for
`source_refs`. Account + family + scope + external ID determine source identity; revision and
evidence fingerprint are separate. Changed evidence under the same revision is rejected.

### If you have only a link and excerpt

Do not turn a link, title, or search result position into a pretend message ID.
Use a local review-evidence observation instead. For a fictional rehearsal, save the following as
`observation.json`; for real work, replace its content with the actual observation and
qualifications:

```json
{
  "identity_kind": "evidence_only",
  "canonical_id": null,
  "source_link": "https://example.com/review/request",
  "quote": "Please review the example specification.",
  "coverage": {"later_resolution": "partial", "sent_mail": "partial"}
}
```

Create a deterministic local source without claiming provider identity:

```sh
python3 - <<'PY'
import hashlib
import json
from pathlib import Path

observation = json.loads(Path("observation.json").read_text())
content = json.dumps(observation, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
fingerprint = hashlib.sha256(content.encode("utf-8")).hexdigest()
source = {
    "family": "review-evidence",
    "scope": "bootstrap-mail-review",
    "external_id": "evidence:" + fingerprint,
    "revision": "sha256:" + fingerprint,
    "evidence": observation,
    "web_link": observation["source_link"],
    "sensitivity": "unknown",
}
with Path("source.json").open("x", encoding="utf-8") as stream:
    json.dump(source, stream, indent=2)
PY
work source --input source.json > source-ref.json
```

The file creation refuses to overwrite an existing input. Inspect it rather than deleting an
earlier observation. This local hash identifies evidence, not a provider object. Resolve actual
target IDs and fresh source state before proposing any executable external action.

## 3. Ingest and inspect the candidate

Save `candidate.json` with the exact returned reference substituted below:

```json
{
  "claim_key": "review-example-specification",
  "data": {
    "title": "Review the example specification",
    "owner": null,
    "direction": "owe",
    "due": null,
    "source_refs": [
      {
        "source_id": "RETURNED_SOURCE_ID",
        "revision": "RETURNED_REVISION",
        "fingerprint": "RETURNED_FINGERPRINT"
      }
    ],
    "notes": "Current obligation state unknown; later-message and sent-mail coverage partial."
  }
}
```

```sh
work ingest --input candidate.json
work list --view decisions --json
work show ITEM_ID --json
work history ITEM_ID --json
```

**Expected:** a `candidate`, never automatic confirmation. `owner` and `due` are required, with
`null` for unknowns. Direction is `owe`, `waiting_on`, or `own`. Optional fields include confidence,
next step, blocker, outcome ID, and notes. An attributed direct instruction can use
`confirmation_source` when no external source exists; ingestion still creates a candidate.

Replaying the same account, source revision set, and `claim_key` returns the same item, retaining
rejection or deferral. Different content under that identity fails. Different source/claim
identities can produce separate candidates with similar wording; review possible duplicates.
Changed provider IDs and semantic matches are not automatically merged.

## 4. Confirm, work, and resolve deliberately

After the real foreground decision, `confirmation.json` contains `state:"confirmed"` and the
[actual human evidence](README.md#recording-a-real-human-decision) for `ITEM_ID`, current revision,
and `decision:"confirm"`. It may include an explicit `patch` of item fields.

```sh
work item-update ITEM_ID --revision REVISION --input confirmation.json
work show ITEM_ID --json
```

Usual flow is `candidate -> confirmed -> active/waiting -> resolved`. For later transitions, use
another `item-update` input with real evidence matching `activate`, `wait`, `resolve`, `defer`,
or `cancel`; an edit without a transition uses `edit`. Every edit of ever-confirmed work requires
human evidence. New messages can suggest resolution but cannot silently close the item.

Reject or defer an unconfirmed candidate rather than confirming it merely to load a tracker.
An unconfirmed deferred/cancelled item must pass through confirmation before active work.
`relate` links work IDs with `depends_on`, `blocks`, or `supersedes`, or uses `tracked_in` for an
actual canonical tracker reference. Confirmed relationships require `relate` evidence. A link
does not complete either item or replace Planner/the decision log.

**Recovery:** on a revision conflict, run `show`, review what changed, and obtain a new decision
where required. Never just increment the revision in an old approval file.

## 5. Review an exact action proposal

```sh
work list --view approval --json
work list --view waiting --json
work list --view problems --json
work show ACTION_ID --json
```

Prepare `action.json` only after discovering the actual Work IQ write schema and canonical target.
It must contain `kind`, `target`, `payload`, `why`, `source_refs`, and `target_fingerprint`;
optional links include `work_item_id`, dependencies, and an exact artifact ID/revision.
Put every write-relevant field in the snapshot: recipients, body, IDs, dates, attachments, and
other arguments. There is no universal mail-send payload to copy from this guide.

```sh
work propose --input action.json
work edit ACTION_ID --revision REVISION --expected-hash DISPLAYED_HASH \
  --input edited-action.json
```

The edit input is the complete action document, not just the changed body. Editing the reason
also creates a revision and invalidates approval. To defer/dismiss a proposal:

```sh
work defer ACTION_ID --revision REVISION --expected-hash DISPLAYED_HASH \
  --until 2026-09-08T09:00:00-07:00
work dismiss ACTION_ID --revision REVISION --expected-hash DISPLAYED_HASH
```

These are alternatives, not a sequence with a reused revision. Choose an actual future deferral
time. Dismissal hides only that proposal; it does not reject work or learn a preference.

The optional canvas reads the same ledger. It can revise an action's JSON payload, defer or
dismiss the proposal, and request foreground review. Work items, including candidates, and typed
records are read-only. The review button is not approval.

## 6. Approve once, preflight, and reconcile uncertainty

**Preconditions:** the human has seen account, exact target/payload, current revision/hash,
sources, and expiry, and explicitly approved this action. `approval.json` must contain that
`action_hash`, `expires_at`, and real evidence with `decision:"approve"` and the same hash.
Expiry must be future and no more than seven days away.

```sh
work approve ACTION_ID --revision REVISION --input approval.json
```

Immediately before execution, Margo re-reads the target and all supporting sources through the
verified binding and records new source revisions. `fresh.json` contains actual `checked_at`,
`target_fingerprint`, and the complete matching `source_refs`. Preflight must be within five
minutes. A changed source/target requires refresh, revision, and new approval.

```sh
work begin ACTION_ID --revision REVISION --input fresh.json
```

`begin` returns the gated payload and an `attempt_id`, and records `executing`. It does not send.
Only then may foreground Margo perform the exact approved Work IQ call and journal its actual
result using `finish ATTEMPT_ID --input result.json`.

No automatic retry follows a timeout, partial effect, or crash. Inspect history and actual
provider state, then use `reconcile ATTEMPT_ID --input reconciled-result.json` with real evidence.
Even a definite no-effect failure needs a new edited revision and approval to retry.
See [receipt shapes and failure states](../../skills/chief-of-staff/references/action-desk.md).
Never claim exactly-once external delivery or treat a successful `begin` as a send receipt.
