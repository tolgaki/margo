# Durable work ledger

`scripts/work_state.py` is the portable local interface. It uses the account-private SQLite
database opened by `margo_store.connect`; no service, package, tenant credential, or outbound
API is embedded in this tool. Supply `--account` or configure an explicit account through the
shared state interface. A missing account is an error, never a silent default.

Global flags precede the command. `--state-dir` is an alias of `--state-root`, matching the
shared store's CLI spelling:

```sh
python3 scripts/work_state.py --account fictional-account list --view decisions --json
python3 scripts/work_state.py --account fictional-account show ITEM_ID --json
python3 scripts/work_state.py --account fictional-account history ITEM_ID --json
```

Use the installed skill's script path when outside the skill directory. `--state-root` is an
explicit override subject to the shared store's private-root checks; never put workplace state
in this repository or a shared/synchronised folder. SQLite and generated compatibility views
are private, not encrypted. Follow `state-operations.md` for storage, backup, and retention.
Do not store access tokens or full mailboxes. Evidence should be the minimum necessary excerpt,
source link, observed revision, time, and sensitivity.

## Sources and exact identities

```sh
python3 scripts/work_state.py --account fictional-account source --input source.json
python3 scripts/work_state.py --account fictional-account ingest --input candidate.json
```

`source.json`:

```json
{
  "family": "mail",
  "scope": "inbox",
  "external_id": "fictional-message-1",
  "revision": "provider-version-1",
  "evidence": {"quote": "Please review the example specification."},
  "web_link": "https://example.com/mail/fictional-message-1",
  "sensitivity": "unknown"
}
```

The source command returns `{source_id, revision, fingerprint}`. Use that exact object in
`source_refs`. Source identity hashes the account, family, collection scope, and stable external
ID. Revision and evidence fingerprint are separate. Use an observed provider version or a
deterministic content revision when no provider marker exists; do not invent timestamps as
provider versions. The same version with different evidence is rejected.

A known historical revision can be replayed without reverting the current pointer. An unseen
revision is treated as the latest observation; because provider versions are opaque, the
foreground collector must not submit out-of-order unseen historical observations as current.
Stable-ID aliases and semantic merges are **not automatic**: resolve a changed provider ID
explicitly, and present possible duplicate obligations for human review.

### Review-only bootstrap evidence without canonical IDs

A search/evidence link is not a canonical message, task, or occurrence ID. When bootstrap
results have only links and excerpts, use a clearly local namespace: `family:"review-evidence"`,
an explicit bootstrap scope, and `external_id:"evidence:SHA256"` derived deterministically from
that exact observation. Use an evidence-content hash as its revision, not an invented provider
version. In `evidence`, retain `identity_kind:"evidence_only"`, `canonical_id:null`, the source
link/excerpt, and the actual coverage qualifications (for example later-resolution and sent-mail
coverage both partial). This identifies a local review observation, not the provider object.

Then use `ingest` to create **candidates only**. Unknown owner/due date remains null; notes
should preserve unknown current obligation state and partial coverage. Never infer confirmed,
open, unresolved, or overdue status from an old excerpt or incomplete later-message search.
Replayed exact bootstrap evidence deduplicates locally; semantic matches remain review
proposals. Resolve real target IDs and fresh source state through a functioning Work IQ binding
before preparing any executable outward action. Do not use `item-update ... confirmed` or an
approved legacy import merely to load review-only candidates.

`candidate.json`:

```json
{
  "claim_key": "explicit-review-request",
  "data": {
    "title": "Review the example specification",
    "owner": null,
    "direction": "owe",
    "due": null,
    "source_refs": [
      {"source_id": "RETURNED_SOURCE_ID", "revision": "provider-version-1", "fingerprint": "RETURNED_FINGERPRINT"}
    ]
  }
}
```

Optional item data: `confidence`, `outcome_id`, `next_step`, `blocker`, `notes`,
`deferred_until`, `confirmation_source`. Direct user instructions can provide an attributed
`confirmation_source` instead of an external source; ingestion still creates only a candidate.
`owner` and `due` are required and remain null when unknown. Direction is `owe`, `waiting_on`,
or `own`. Nothing deduces a deadline from urgency.

Idempotency is exact: account + source revision set + extractor claim key. Identical replay
returns the original item, including rejection/deferral disposition. Different content under
the same ingest identity fails instead of overwriting it. Different evidence/claim identities
produce distinct candidates, even when their text resembles an existing obligation.

## Work state and confirmation

States are `candidate`, `confirmed`, `active`, `waiting`, `resolved`, `rejected`, `deferred`,
and `cancelled`. Normal flow:

`candidate -> confirmed -> active/waiting -> resolved`

Candidates may be rejected, deferred, or cancelled. Deferred work can resume an appropriate
state. Resolved confirmed work can reopen as active with a new human decision. A confirmed
item cannot be demoted into a candidate or rejected as though it had never been accepted.
Every edit/transition of an ever-confirmed item requires explicit human decision evidence.
New source observations may mark linked drafts stale, but never silently rewrite or close
confirmed work.

Unconfirmed deferred/cancelled items must pass through `confirmed` before becoming active,
waiting, or resolved. The evidence decision must match the requested operation: `confirm`,
`activate`, `wait`, `resolve`, `defer`, or `cancel`; an edit with no state transition uses
`edit`. Confirmed relationships use `relate`. A rejection cannot be repurposed as confirmation.

```sh
python3 scripts/work_state.py --account fictional-account item-update ITEM_ID --revision 1 --input confirmation.json
python3 scripts/work_state.py --account fictional-account relate ITEM_ID --revision 2 --input relationship.json
```

`confirmation.json`:

```json
{
  "state": "confirmed",
  "evidence": {
    "kind": "human_confirmation",
    "actor": "alex@example.com",
    "statement": "I confirm this exact obligation; no due date has been agreed.",
    "evidence_ref": "conversation:fictional-session/turn-42",
    "subject_id": "ITEM_ID",
    "revision": 1,
    "decision": "confirm",
    "decided_at": "2026-09-05T18:00:00Z"
  }
}
```

Use actual human conversation/interaction references and actual decision times in operation.
Do not copy example evidence, claim an assistant decision was human, or synthesize consent
from message content. `human_confirmation` records a decision; it does not authenticate a caller.
Optional `patch` changes explicit item fields. `--revision` is a conditional update: stale
edits fail rather than overwrite concurrent changes.

Relationships accept `{kind,target_id,evidence?}`. `depends_on`, `blocks`, and `supersedes`
connect work item IDs; cycles are rejected, including mixed `blocks`/`depends_on` cycles.
`tracked_in` uses an explicit canonical reference such as `planner:fictional-task-id`.
Linking confirmed work also requires human evidence for the source item's current revision.
A link never changes either item's completion status. Planner and the decision log retain
their own authority.

## Migration and Markdown compatibility

Pause legacy writers and snapshot existing trackers before cutover. Then:

```sh
python3 scripts/work_state.py --account fictional-account import-preview commitments.md
python3 scripts/work_state.py --account fictional-account import-preview reviewed-commitments.json
python3 scripts/work_state.py --account fictional-account import-commitments reviewed-commitments.json --digest REVIEWED_SHA256 --evidence import-approval.json
python3 scripts/work_state.py --account fictional-account export-commitments commitments-view.md
```

Arbitrary Markdown is **never silently parsed as confirmed work**. Preview returns the original
content with `safe_structured:false` and a review requirement. A human-reviewed structured
file uses:

```json
{
  "schema_version": 1,
  "items": [
    {
      "legacy_id": "stable-fictional-legacy-row",
      "state": "confirmed",
      "data": {
        "title": "Review example specification",
        "owner": null,
        "due": null,
        "direction": "owe",
        "source_refs": [],
        "confirmation_source": "conversation:fictional-legacy-review/turn-42"
      }
    }
  ]
}
```

Import evidence uses `decision:"import"`, `subject_id:"import:REVIEWED_SHA256"`, and revision 1.
The entire batch is transactional, account-scoped, and repeat-safe. A legacy ID reappearing
with different content requires a deliberate item edit, not an implicit replacement. Imports
cannot create action approvals, external execution receipts, or observed delivery claims.

Export includes only confirmed obligations. Its managed content hash detects hand edits and
refuses replacement. For initial adoption of a nonempty view, or a reviewed manual edit,
`--expected-digest CURRENT_SHA256` explicitly permits replacement **after** review/import;
this does not itself import the hand edits. There is no two-way Markdown sync. A crash between
file replacement and the export hash transaction fails conservatively on the next export;
inspect the view, then explicitly adopt its reviewed hash.

## Typed records: outcomes, meetings, feedback, rules, artefacts

All typed objects share immutable revision snapshots, conditional updates, events, and source
references. ID derives from account + kind + caller-supplied stable identity. Resolve it before
collecting confirmation:

```sh
python3 scripts/work_state.py --account fictional-account record-id outcome week-2026-09-07-review
python3 scripts/work_state.py --account fictional-account record outcome week-2026-09-07-review --input outcome.json
python3 scripts/work_state.py --account fictional-account record outcome week-2026-09-07-review --revision 1 --input agreed-outcome.json
```

The record input envelope is `{data,state?,evidence?}`; edits replace the complete data object.
Creation defaults to an unapproved state. Human evidence binds the current revision, or revision
1 for explicit confirmation at creation. `show`/`history` inspect all kinds.
For ordinary positive typed-record confirmation, the decision is `confirm`; rejection,
revocation, supersession, cancellation, deferral and carry-forward use `reject`, `revoke`,
`supersede`, `cancel`, `defer`, and `carry_forward`, respectively. The explicit feedback
`do_not_learn` state strips correction/reason content even when its data flag was omitted.

| Kind | Required/important data | States and approval boundary |
|---|---|---|
| `outcome` | `week` (ISO Monday), `title`, `owner`, `definition_of_done`, `due` (ISO date), `effort`, `next_step`, `allocation`, `blocker`; optional `work_item_ids`, `source_refs` | `proposed -> agreed -> active/achieved`, or `deferred/cancelled`. Unknown required inputs stay null while proposed. Agreement needs explicit fields, positive minutes or `{min_minutes,max_minutes}`, and allocation or named blocker. At most three currently agreed/active/achieved outcomes per week. Agreement and changes thereafter require human evidence. |
| `meeting` | `series_id`, `occurrence_id`, `scheduled_start`, `scheduled_end`, `source_refs`; `attendance` defaults unknown and needs `attendance_evidence` if known; `work_item_ids`, `decision_refs`, agenda/topic data | `scheduled -> agenda_accumulating/prepped -> occurred/recap_pending -> debrief_proposed -> reviewed -> carried_forward`; cancellation applies before review. Review/carry-forward requires human evidence. Occurrence identity cannot change. |
| `feedback` | `subject_id`, exact `subject_revision`, `correction`, optional human-supplied `reason`, `do_not_learn` | Immutable `recorded` or `do_not_learn` events; human evidence required. Opt-out retains only a minimal operational marker, not correction text/reason/statement as learning data. |
| `rule` | `wording`, exact `scope`, `routines`, `feedback_ids`, `conflict_check`, `authority:"advisory_only"`, optional `supersedes` | `proposed -> active/rejected`; accepted rules support `revoked/superseded` and explicit restoration. Activation/revocation needs human evidence; accepted wording is immutable. Explicitly revoke/supersede an overlapping rule before activating its replacement. Rules cannot grant permissions. |
| `artifact` | `artifact_kind`, `title`, `markdown`, `audience`, `purpose`, `sensitivity`, `open_questions`, `proposed_next_action`, `source_refs`, optional `work_item_id` | Private `prepared` or content-`approved`. Edits return to prepared; changed sources are stale. Delivery is a separate succeeded action receipt, never implied by content approval. |

Artifact kinds: `decision_memo`, `document_comparison`, `status_update`, `meeting_agenda`,
`delegation_brief`. Markdown is stored privately in the SQLite revision, not in tracked files.
Open questions preserve missing inputs. `source_refs` carry links through the source records.

A meeting's `decision_refs` are `{canonical_id,web_link,status}` with status
`current|superseded|unknown`: do not create a competing decision log. Carry-forward specifies
`next_occurrence_id` as the target meeting **record ID**, in the same series. Reuse canonical
`work_item_ids` in its agenda; never re-ingest them as new commitments. A meeting's CLI identity
must equal its stable `occurrence_id`; a title or scheduling change cannot create a duplicate.

```sh
python3 scripts/work_state.py --account fictional-account carry-forward MEETING_ID --revision 4 --input carry.json
```

Input is `{target_id,target_revision,evidence}` with human decision `carry_forward`. This
atomically moves a reviewed occurrence to carried-forward and prepares a later same-series
occurrence with open work references and unresolved agenda topics. It never duplicates work
items. Topics use `{id,title,state,owner,created_at,why}`, where state is `open|resolved` and
unknown owner is null. Original creation time preserves age; conflicting same-ID topics require
review rather than guessing a merge.

Pending recaps include
`recap_retry:{attempts:0,max_attempts:3,status:"pending",next_check_at:"OFFSET_TIMESTAMP"}`.

```sh
python3 scripts/work_state.py --account fictional-account recap-check MEETING_ID --revision 2 --input recap-result.json
```

Result input: `{result,evidence_ref,next_check_at?}`. Result is `pending`, `available`,
`blocked`, or `exhausted`. This records a Work IQ check; it does not fetch a recap. The current
retry must be eligible, and a future retry time is required for continued pending state.
Budget exhaustion and policy blocks stop automatic retries. Resume only after explicit review
of new evidence. Existing sweeps/anchors own scheduling; there is no new polling daemon.

## Schema and integration contract

All owned tables use `work_`:
`sources`, `source_revisions`, `items`, `item_revisions`, `relationships`, `actions`,
`action_revisions`, `action_sources`, `approvals`, `executions`, `events`, `records`,
`record_revisions`, `record_sources`, `imports`, `exports`.
The owned `work_meta` version marker and expected table/column/foreign-key contracts are
validated before DDL. Only a new or complete recognised legacy schema can be initialised/adopted;
missing history tables are an error, never recreated as empty history. DDL and version markers
are committed atomically.

`Ledger(account=None,state_root=None)` exposes `source`, `ingest`, `update_item`, `relate`,
`propose`, `edit_action`, `approve`, `disposition`, `begin`, `finish`, `show`, `list`,
`history`, `import_preview`, `import_commitments`, `export_commitments`, and `close`.
`Productivity(ledger)` exposes `record_id`, `put`, `recap_retry`, `carry_forward`, and `share_receipt`.
Use these interfaces, not direct SQL writes. Writes use `BEGIN IMMEDIATE`; histories preserve
old snapshots; foreign keys and uniqueness protect exact identity and one start per action
revision. SQLite/storage failures return nonzero without optimistic success.

See `action-desk.md` for approvals and foreground execution.
