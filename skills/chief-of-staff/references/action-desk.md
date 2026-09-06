# Portable action desk

The desk is a local review and execution journal, not a mail client or a new feed. Margo prepares
the exact proposal, obtains a real human decision in the foreground, performs Work IQ preflight
and execution, then records the actual result. **The CLI never makes an outbound API call.**
An unavailable or unauthenticated invoking host binding blocks external mutation from that
session; it does not establish a tenant-wide Work IQ outage. Keep proposals local and report
the scope actually observed. Another authenticated session may operate through its own verified
binding under the same per-action approval rules. A policy denial must never be bypassed.

## Review surface

```sh
python3 scripts/work_state.py --account fictional-account list --view decisions --json
python3 scripts/work_state.py --account fictional-account list --view approval --json
python3 scripts/work_state.py --account fictional-account list --view waiting --json
python3 scripts/work_state.py --account fictional-account list --view problems --json
python3 scripts/work_state.py --account fictional-account show ACTION_ID --json
python3 scripts/work_state.py --account fictional-account history ACTION_ID --json
```

Views: **Needs a decision**, **Ready for approval**, **Waiting**, **Execution problems**.
`all` includes historical dispositions. JSON is always emitted; `--json` explicitly declares
the portable client contract. Successful commands exit 0; validation/storage errors exit 2
with a JSON error on stderr.

List returns `{schema_version:1,account,items:[],actions:[],records:[]}`.

* Items: `{id,type:"item",revision,state,confirmed,data,confirmation,relationships,stale,...}`.
  `data` contains title, ownership, due date, sources, and any outcome/next-step link.
* Actions: `{id,type:"action",revision,state,kind,target,payload,why,source_refs,
  target_fingerprint,work_item_id,action_hash,approvals,executions,stale,...}`.
* Typed records: `{id,type:"record",kind,identity_key,revision,state,data,stale,...}`.

Rows also include resolved `sources` with the evidence excerpt, source link, observed time,
sensitivity and current/approved revision, so a reviewer can navigate directly to the evidence.
`source_refs` remains the immutable fingerprint-bearing approval snapshot.

The optional canvas is only a thin client of this JSON. Show the actual target/recipients,
payload, sources, revision and hash—not merely “send follow-up.” Without a trustworthy human
interaction bridge, its buttons are review/edit requests and final approval stays in the
foreground conversation. Do not manufacture a human click by calling an approval tool.

## Prepare and edit

```sh
python3 scripts/work_state.py --account fictional-account propose --input action.json
python3 scripts/work_state.py --account fictional-account edit ACTION_ID --revision 1 --expected-hash DISPLAYED_HASH --input edited-action.json
python3 scripts/work_state.py --account fictional-account defer ACTION_ID --revision 2 --expected-hash DISPLAYED_HASH --until 2026-09-08T09:00:00-07:00
python3 scripts/work_state.py --account fictional-account dismiss ACTION_ID --revision 3 --expected-hash DISPLAYED_HASH
```

Action input:

```json
{
  "kind": "mail.reply",
  "target": {"message_id": "fictional-message-1", "recipients": ["blair@example.com"]},
  "payload": {"body": "Here is the reviewed example."},
  "why": "The sender explicitly requested this follow-up.",
  "source_refs": [
    {"source_id": "SOURCE_ID", "revision": "v1", "fingerprint": "SOURCE_FINGERPRINT"}
  ],
  "target_fingerprint": "OBSERVED_TARGET_FINGERPRINT",
  "work_item_id": "ITEM_ID"
}
```

Use the **discovered** Work IQ API's exact fields, not this illustrative payload as a guessed
send schema. Store any recipients, IDs, body, dates, attachment references, diff and other
write-relevant fields in target/payload. Optional `affected_people`, `dependencies` (work IDs),
`artifact_id` and exact `artifact_revision` are included in the approved snapshot. A linked
artifact must be current and not stale. Resolve dependencies before execution.

The source command computes fingerprints from recorded minimal evidence/link/sensitivity.
For target fingerprints, deterministically hash all externally observed fields relevant to the
write (for example recipients, thread revision, event times and ownership); use the same
projection and normalization on preflight. Fingerprints are supplied by the foreground agent:
the CLI validates consistency but cannot prove that a genuine fresh read occurred.

Edits create immutable revisions and invalidate previous approvals even when only the reason
changes. Deferral/dismissal also create a new revision/hash, preserving the exact content, so
one panel cannot overwrite another panel's newer disposition. All three commands accept an
additional `--expected-hash`; it and `--revision` are checked atomically in the write transaction.
Use the displayed `action_hash`, not a separately calculated payload-only hash.
Editing a dismissed/deferred proposal explicitly returns it to ready. Dismissal only
hides that proposal; it never closes an obligation, deletes a source, cancels a meeting, or
learns a general preference.

## Approve exactly one revision

Show account, revision/hash, exact action/target/payload, source freshness and expiry. Then obtain
the human's explicit approval of that proposal. Record it:

```sh
python3 scripts/work_state.py --account fictional-account approve ACTION_ID --revision 2 --input approval.json
```

`approval.json`:

```json
{
  "action_hash": "DISPLAYED_ACTION_HASH",
  "expires_at": "2026-09-05T20:00:00Z",
  "evidence": {
    "kind": "human_confirmation",
    "actor": "alex@example.com",
    "statement": "Send this exact reply to the displayed recipient.",
    "evidence_ref": "conversation:fictional-session/turn-42",
    "subject_id": "ACTION_ID",
    "revision": 2,
    "decision": "approve",
    "action_hash": "DISPLAYED_ACTION_HASH",
    "decided_at": "2026-09-05T19:00:00Z"
  }
}
```

Dates and identities above are illustrative. In operation record the actual interaction;
never generate evidence as a substitute for asking. Expiry must be future and within seven
days. No `--yes`, no approve-all, no standing grant for outward actions. Approval of a work
item/outcome/artifact is not delivery approval.

This is a **record of human confirmation, not an authentication wall**. A generally capable
agent can invoke tools directly; host permissions and the agent's approval rules remain
necessary. Deterministic enforcement here covers stored account/revision/hash/expiry,
conditional transitions, source consistency and transactional execution starts.

## Preflight, execute, receipt

Immediately before the actual Work IQ write, re-read target and supporting sources. Persist
newly observed revisions with `source`; a changed source invalidates linked ready/approved
proposals. A changed target requires a new proposal revision and human approval.

```sh
python3 scripts/work_state.py --account fictional-account begin ACTION_ID --revision 2 --input fresh.json
```

`fresh.json`:

```json
{
  "checked_at": "2026-09-05T19:01:00Z",
  "target_fingerprint": "OBSERVED_TARGET_FINGERPRINT",
  "source_refs": [
    {"source_id": "SOURCE_ID", "revision": "v1", "fingerprint": "SOURCE_FINGERPRINT"}
  ]
}
```

Preflight must be no older than five minutes, and its complete source/target snapshot must
match the approved revision. `begin` transactionally changes `approved -> executing`, creates
one attempt, and returns `{account,action_id,revision,attempt_id,action_hash,kind,target,payload}`.
Only then does foreground Margo call the discovered Work IQ API with the exact gated payload.
Use provider idempotency/conditional-write fields only where that API supports them.

`begin` is not a send receipt. Losing its output or crashing before the Work IQ write leaves
an interrupted attempt: inspect/reconcile it, never blindly start again. Even with successful
preflight, a provider can change between read and write; do not claim exactly-once delivery.

```sh
python3 scripts/work_state.py --account fictional-account finish ATTEMPT_ID --input result.json
python3 scripts/work_state.py --account fictional-account reconcile ATTEMPT_ID --input reconciled-result.json
```

`result.json`:

```json
{
  "state": "succeeded",
  "receipt": {
    "kind": "tool_result",
    "reference": "tool:fictional-result-1",
    "outcome": "succeeded",
    "recorded_at": "2026-09-05T19:02:00Z"
  }
}
```

Keep real result references and minimal proof, not credentials or opaque access tokens.
Receipt kinds: `tool_result`, `provider_receipt`, `reconciliation_read`.

| State | Meaning / next step |
|---|---|
| `executing` | One attempt is in flight or interrupted. Double-start is blocked. |
| `succeeded` | Actual result receipt establishes success. Old revision cannot execute again. |
| `failed` | Definite failure with `definitive_no_effect:true` in the receipt. A retry still requires a new edited revision and new human approval. |
| `partial` | Some effects occurred; receipt identifies completed and unfinished steps. No automatic retry, edit, dismissal, or generic rollback. Reconcile the original attempt and propose any remaining steps as new actions. |
| `outcome_unknown` | Timeout/transport uncertainty. May be recorded without a receipt; never retry. |

Reconciliation of `executing`, `partial`, or `outcome_unknown` requires an actual receipt
establishing the chosen outcome. A stale approval or lost result is not authorization to repeat
a send. Compensating changes also require their own exact outward-action approval.

After a successful artifact delivery, link its exact succeeded execution:

```sh
python3 scripts/work_state.py --account fictional-account artifact-shared ARTIFACT_ID --revision 2 --attempt-id ATTEMPT_ID
```

That records a `shared` history event and receipt without changing the private artifact's
`prepared/approved` content state. The action must reference that exact artifact revision.
Preparing an Outlook draft is a separate, specifically authorized Work IQ write; it is neither
local Markdown preparation nor permission to send.
