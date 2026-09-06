# Correct Margo and prepare useful work

**Preconditions:** use the [shared shell setup](README.md#before-running-a-cli-recipe).
Corrections need a stored subject and exact revision. Work products need current source references.
Everything in these recipes stays private unless a separately approved action actually publishes it.

## 1. Record a correction without making it a general rule

> For this agenda, move Priya's decision first. This is a one-off correction, not a rule.

Margo should identify the agenda/action revision being corrected, record the correction, and
apply the appropriate local edit. It must not infer a reason you did not give or silently edit
`preferences.md`. A dismissed proposal, delay, or silence is not feedback.

Inspect the subject and resolve a stable feedback ID before collecting the real decision:

```sh
work show SUBJECT_ID --json
work record-id feedback agenda-order-correction
```

The `data` portion of `feedback.json` is:

```json
{
  "subject_id": "ACTUAL_STORED_SUBJECT_ID",
  "subject_revision": 1,
  "correction": "Put the decision needed before the background section.",
  "reason": null,
  "do_not_learn": false
}
```

Substitute the actual subject revision, not necessarily 1. This is **data only**, not a complete
command input. Wrap it in `{"state":"recorded","data":...,"evidence":...}` using
[real human evidence](README.md#recording-a-real-human-decision) for the derived **feedback record
ID**, revision 1 at creation, and `decision:"confirm"`. The subject revision inside `data` is a
different field from the feedback record revision in evidence.

```sh
work record feedback agenda-order-correction --input feedback.json
work show FEEDBACK_ID --json
```

**Expected:** an immutable `recorded` feedback event. Recording feedback does not itself revise
the subject or activate a rule. Record another correction as a new feedback identity.

### Respect "do not learn"

> Correct this instance, but do not learn from it or retain it as a preference example.

Use the same real-decision process with state `do_not_learn` or `data.do_not_learn:true`.
The minimal `data` needs only `subject_id`, `subject_revision`, and `do_not_learn:true`.
Resolve the opt-out's own ID with `record-id feedback` before confirmation.

**Expected:** a minimal operational opt-out marker, not stored correction/reason/statement text
as learning data. It cannot support a rule. This does not erase source messages, already stored
work products, or earlier feedback; it is not a general deletion/retention command.

## 2. Propose, activate, and revoke a scoped rule

If you want repeated behavior, be explicit:

> Propose a rule for my meeting agendas: put decisions needed first. Show its scope, affected
> routines, supporting corrections, and conflicts. Do not activate it yet.

Save `rule.json` using actual recorded feedback IDs and an actual conflict review:

```json
{
  "state": "proposed",
  "data": {
    "wording": "Put decisions needed before background in meeting agendas.",
    "scope": "meeting agendas prepared for Dana",
    "routines": ["meeting-prep"],
    "feedback_ids": ["ACTUAL_RECORDED_FEEDBACK_ID"],
    "conflict_check": "ACTUAL_RESULT_OF_REVIEWING_CURRENT_RULES_AND_PREFERENCES",
    "authority": "advisory_only",
    "source_refs": []
  }
}
```

```sh
work record-id rule agenda-decisions-first
work record rule agenda-decisions-first --input rule.json
work show RULE_ID --json
```

**Expected:** a proposed rule, not an active preference. A do-not-learn marker cannot satisfy the
feedback requirement. Inspect existing active rules and authoritative personal preferences before
claiming there is no conflict.

After the human approves the exact wording/scope, create `active-rule.json` with the complete same
data, state `active`, and actual `confirm` evidence for the current rule revision:

```sh
work record rule agenda-decisions-first --revision REVISION --input active-rule.json
```

To stop applying it, first get the human's explicit revocation. `revoked-rule.json` keeps the
complete accepted data unchanged, sets `state:"revoked"`, and supplies real `revoke` evidence:

```sh
work record rule agenda-decisions-first --revision REVISION --input revoked-rule.json
work history RULE_ID --json
```

**Expected:** the history survives; revocation does not rewrite previous messages. Accepted rule
content is immutable, including after revocation. To change wording, propose a new identity with
`data.supersedes` pointing to the prior rule. Explicitly revoke/supersede an overlapping active
rule before activating its replacement. Restoration also needs a real confirmation.

**Recovery:** an overlapping active rule or stale revision is rejected. Inspect history and resolve
the conflict with the human; do not rename the scope merely to bypass it. Rules are advisory only:
they cannot authorize sends, broaden permissions, lower approval, or confirm inferred obligations.
Show a separate settings diff before any approved material change to the personal preferences file.
See [feedback policy](../../skills/chief-of-staff/references/feedback.md).

## 3. Prepare a substantive private work product

> Prepare a decision memo for Marco from the current sources. Include options, trade-offs, a
> recommendation, and unresolved questions. Keep it private; do not create an Outlook draft.

Supported `artifact_kind` values are `decision_memo`, `document_comparison`, `status_update`,
`meeting_agenda`, and `delegation_brief`. The CLI kind is spelled `artifact`.

**Preconditions:** inspect the work item, current source revisions, intended audience, sensitivity,
and applicable voice rules. Do not copy protected content into a more shareable document.
Store each supporting source with `work source` and use its exact returned reference.

Save this shape as `memo.json`, replacing the placeholder reference with the actual source:

```json
{
  "state": "prepared",
  "data": {
    "artifact_kind": "decision_memo",
    "title": "Choose the example review approach",
    "markdown": "# Options\nJoint or independent review.\n# Call\nIndependent; version unknown.",
    "audience": "Marco",
    "purpose": "Support a decision about how to review the specification.",
    "sensitivity": "unknown",
    "open_questions": ["Which specification version is current?"],
    "proposed_next_action": "Review the memo privately; resolve the source-version question.",
    "source_refs": [
      {
        "source_id": "RETURNED_SOURCE_ID",
        "revision": "RETURNED_REVISION",
        "fingerprint": "RETURNED_FINGERPRINT"
      }
    ]
  }
}
```

Expand the compact `markdown` sample with actual context, options, trade-offs, and a supported
recommendation. A joint read gives immediate clarification; independent reviews may reveal
different gaps. Use that distinction only if it fits the actual decision. Add `work_item_id`
when there is an actual canonical work item. Keep unknown dates/owners as open questions rather
than filling them from context. Separate sourced facts, recommendations, and inferences.

```sh
work record artifact review-approach-memo --input memo.json
work show ARTIFACT_ID --json
work history ARTIFACT_ID --json
```

**Expected:** a private `prepared` record with versioned Markdown inside SQLite. It is not a
tracked repository document, shared file, assigned delegation, Outlook draft, or message.
The CLI validates structure and source references, not whether the prose is accurate or useful.

For a local revision, prepare the complete updated envelope with state `prepared`:

```sh
work record artifact review-approach-memo --revision REVISION --input revised-memo.json
```

For content approval, first review the actual Markdown and fresh sources. Then save the complete
envelope as `approved-memo.json`, with state `approved` and actual `confirm` evidence:

```sh
work record artifact review-approach-memo --revision REVISION --input approved-memo.json
```

**Recovery:** changed sources make the record stale. Re-read and save a reviewed version using
current source refs. Editing approved content must return it to `prepared`; linked delivery
proposals are invalidated by artifact updates. Approval of stale content is not a shortcut.

## 4. Keep publishing separate

> Show me exactly how this memo would be shared, with its destination and content. Wait for
> approval of that action.

The action proposal must link `artifact_id` and its exact `artifact_revision`, plus the actual
target/payload required by the discovered Work IQ API. Follow the
[action review and execution recipe][execution].
Content approval alone is not delivery approval, and creating an Outlook draft is itself an
outward write requiring specific approval.

Only after a real succeeded execution, link its returned attempt:

```sh
work artifact-shared ARTIFACT_ID --revision REVISION --attempt-id ATTEMPT_ID
```

**Expected:** a `shared` history event with the actual receipt for that exact artifact revision.
Its content state remains `prepared` or `approved`. The command does not publish anything and
rejects an attempt that did not deliver this revision. Do not invent an editable `sharing` field.

Unattended work-product preparation is local only. Agree an explicit item/cost budget before bulk
generation; without one, prepare just the highest-priority actionable item and defer the rest.
See [work-product policy](../../skills/chief-of-staff/references/work-products.md).

[execution]: commitments-and-action-desk.md#6-approve-once-preflight-and-reconcile-uncertainty

## Feature reference

The numbered sections above are the full CLI walkthrough. These are the stable per-feature entry
points the [feature catalog](../feature-catalog.json) links to.

### Feedback

Record a specific correction tied to the exact item it applies to, without it silently becoming a
permanent rule — see [§1](#1-record-a-correction-without-making-it-a-general-rule). Try it: *"That
recommendation was wrong — here's why. Don't turn this into a standing rule."* What you'll see: a
correction recorded against the exact work/action revision, with the reason kept only if you gave
one. What needs your decision: recording preserves the correction; changing future behavior
requires a separately confirmed scoped rule and never grants action permission. Change your
mind: the correction is a historical record, not an active control to revoke.
Your data: corrections live in the private work ledger next to the item they correct. If
something goes wrong: silence, delay, or a dismissed proposal is never treated as an implied
preference. Implemented, runtime (deterministic correction record with tests). Since 1.1.0.

### Rules

Turn a repeated correction into a standing rule, proposed with evidence and activated only on
your explicit confirmation — see [§2 Propose, activate, and revoke a scoped rule](#2-propose-activate-and-revoke-a-scoped-rule).
Try it: *"I keep correcting this — propose it as a rule with the scope you'd apply it to."* What
you'll see: a proposed rule with supporting examples, scope, and a conflict check against existing
preferences — never a silent update to `preferences.md`. What needs your decision: activation is
always explicit; a rule can never grant send permission, widen a standing grant, or lower an
approval requirement. Change your mind: revoke an active rule any time — revocation keeps its
supersession history and never rewrites messages already sent. Your data: rules live in the
private work ledger with their supporting evidence. If something goes wrong: a proposed rule that
conflicts with an existing preference is flagged before you're asked to activate it. Implemented,
runtime (deterministic rule proposal/activation/revocation with tests). Since 1.1.0.

### Do not learn

Say "don't learn from this" and have it actually mean nothing is stored as a lesson or rule
proposal — see [§1 → *Respect "do not learn"*](#respect-do-not-learn). Try it: *"Handle it this
way, but don't learn from this — it's a one-off."* What you'll see: an ordinary operational
result with no accompanying learning receipt. What needs your decision: nothing — this is an
opt-out, not a write requiring its own approval. Change your mind: this marker only affects
future learning; it does not retroactively remove other stored corrections or rules. Your data: a
minimal marker is recorded so future runs know not to propose a rule from this interaction; no
correction/reason text is stored alongside it. If something goes wrong: this is not a general
erasure facility — for removing existing memory, see
[memory control](memory-controls-and-learning.md#memory-control). Implemented, runtime (minimal
deterministic opt-out marker). Since 1.1.0.

### Work products

Get a useful first draft of the real deliverable — a decision memo, comparison, status update,
agenda, or delegation brief — tied to its sources and versioned as it changes — see
[§3 Prepare a substantive private work product](#3-prepare-a-substantive-private-work-product).
Try it: *"Prepare a decision memo on the migration options, with trade-offs and a recommendation."*
What you'll see: a private, versioned Markdown draft citing its sources, with unknown dates/owners
left unknown rather than invented. What needs your decision: content approval and delivery
approval are different — an approved memo is not shared until you separately approve sending or
publishing it. Change your mind: ask for a revision any time before approving the content; each
edit is a new version. Your data: work products are private, versioned records in your account's
ledger, re-checked as stale when their sources change. If something goes wrong: unattended
preparation is bounded to the single highest-priority item and never bulk-generates without an
agreed budget. Implemented, runtime (deterministic versioned artifact storage with tests). Since
1.1.0.

### Artifact delivery

Know a work product actually reached someone, with the delivery receipt linked to the exact
version that was shared — see [§4 Keep publishing separate](#4-keep-publishing-separate). Try it:
after a real approved send/share of a memo, ask Margo to link the receipt. What you'll see: a
`shared` history event tied to the exact artifact revision that was actually delivered; an
attempt that did not deliver this revision is rejected outright. What needs your decision: the
send/share itself is a separate, ordinary approval — recording the receipt afterward doesn't
require a new one. Change your mind: not applicable — this only records that a real delivery
already happened. Your data: the receipt link lives with the artifact's version history in the
private ledger. If something goes wrong: content approval for a memo is never treated as
approval to publish or send it — those stay two separate facts. Implemented, runtime
(deterministic receipt-linkage with tests). Since 1.1.0.
