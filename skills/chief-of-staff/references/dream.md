# Dream: manual reflection from opted-in checkpoints

Use this procedure for “Dream about yesterday”, “save a session checkpoint”, “review Dream”
or “what context from our sessions applies to this task?”. Read `memory.md` first.
Keep persona out of this procedure. Dream is private preparation, never external action.

## Establish capability and consent

1. Resolve the actual account, host and workspace. Run `memory_state.py dream-status --host HOST
   --workspace WORKSPACE`. A missing store is setup-needed, not permission to initialize.
2. Explain the supported adapter: `margo-session-checkpoint-v1` accepts bounded, selected events
   visible in the **current Margo session**, supplied by the host. A session list, recalled title,
   or `list_sessions_and_chats` is not conversation-content access. Do not invent a transcript
   tool, scrape host databases, import logs, or claim all-session coverage.
3. Ask for an exact scoped memory policy through `policy-preview` / `policy-set`. Use the scope
   returned by `dream-status`, user domain, episode/decision kinds, and
   session_checkpoint/memory_record source categories. Leave other policy settings as reviewed.
   Capture starts off. Never use `put` to evade a denial. No Dream schedule is installed.

## Capture a checkpoint

Use `memory_state.py dream-checkpoint --input PRIVATE_JSON` with the
[checkpoint contract](../../../docs/how-to/dream.md#checkpoint-contract).
Preserve stable host/session/event IDs, opaque source revision, zoned event timestamp,
speaker, source locator, exact selected text, sensitivity, allowed uses and canonical work IDs.
Do not use a summary's wording or repetition as event identity. The host must exclude Dream
prompts, outputs and runs, including quotations of a Dream result presented as new evidence.
Do not checkpoint this reflection session. Host assertions are audit bindings, not authentication.

Checkpoint selected observations, not entire transcripts. Assistant/tool statements are attributed
observations, never user confirmation. A changed event needs its current memory `expected_revision`;
keep earlier revisions for chronology. A different event that contradicts it remains a separate
record, not a replacement. Resolve people/projects by exact IDs, not matching display names.

## Prepare one daily page

1. Obtain the user's day, IANA timezone and local cutoff. Use `dream-plan --input PRIVATE_JSON`
   with `after:null` and an explicit 0–7-day late-arrival lookback. The interval runs from that
   day's cutoff to the next day's cutoff; ambiguous/nonexistent DST cutoffs are rejected.
2. Inspect coverage, excluded records, truncation and `next_cursor`. Even an exhausted local
   page is **partial checkpoint-only coverage**, never complete host history or an all-clear.
   The bounds are 500 scanned rows, 100 events and 14000 serialized event characters per page.
   `excluded_oversized` reports checkpoints that exceed the character bound individually, including
   metadata/entities. They are skipped, not truncated; later events remain accessible. Follow an
   empty page's cursor too, and never claim excluded checkpoints were reflected on.
   Continue a returned cursor only as another deliberately bounded page with a new run key.
   New/changed events invalidate a preview; repeat from `after:null` to catch late arrivals.
3. Run `dream-start KEY --input PRIVATE_JSON` with the exact request, snapshot hash and actual
   `conversation:` or `host-interaction:` user request reference. This commits the source/policy
   snapshot and claims one task step, reserving one host model call **before** reasoning.
   Do not call a model after a rejected claim or from a transaction. Keep its returned token private.
4. Reason once over the supplied page. Distinguish chronology, discussion, interpreted decisions,
   rationale, alternatives, stated direction, inferred themes and open questions. Quote the source
   and attribute speaker for each claim; do not fill missing rationale or silently settle conflicts.
   Submit at most 20 candidate claims / 8000 JSON characters using `dream-finish --input PRIVATE_JSON`.
   Exact observed episodes are deterministic copies; **all model interpretations remain candidates**.
   Quotation matching checks locatability, not entailment or model quality.
5. Inspect with `dream-inspect SNAPSHOT_ID`. Collection coverage, task completion, prepared output
   and human review are separate. The existing output API holds only a pointer to erasable memory.
   Record actual availability separately through `proactive_state.py publication-publish`; never claim
   delivery because a task finished. No output receipt confirms memory or shared work.

## Review, retrieval and recovery

Use `inspect`, `history` and `consolidate` to review exact candidates. Correct or reject them with
`revise ID --revision N`; promoting a claim to user-confirmed active memory needs the actual
foreground human decision, not a fabricated approval. Preferences, lessons, supersession and shared
decisions keep their existing separately reviewed procedures. Dream never changes a work item.

Next session, retrieve only task-scoped `context --input PRIVATE_QUERY_JSON --entity EXACT_ID
--work-id CANONICAL_ID --environment PRIVATE_ENVIRONMENT_JSON --mode lexical`. The environment
must include the exact account/host/workspace. Use `explain ID` to inspect selection and source
validity. Re-read canonical work state; do not prepend a whole Dream digest to every task.
Candidates and checkpoint/snapshot plumbing are excluded from ordinary recall.

If a source revision or capture policy changed, discard the pending synthesis and prepare a new
page/key. Never retry an old payload against new evidence. Exact successful finish replay is
idempotent. Overlapping runs reuse only eligible episodes for the exact checkpoint ID/revision,
without recapture or review reactivation. Suppressed/forgotten or otherwise ineligible episodes are
withheld, not recreated; they do not block other eligible reflection. Inspection excludes obsolete
source-revision episodes. A competing/expired/paused/cancelled claim cannot persist. For an interrupted claim,
use task `show`/`recover` to inspect its real state, cancel remaining work and start a new key with
a fresh snapshot and budget; do not reset task history or reuse a lost token.

Use `forget-preview` / `forget` on the source checkpoint to erase its historical text and derived
snapshots, episodes, interpretations and indexes. Source-key tombstones block alternate-key
reimport even while another reflection is interrupted. Corrections/suppression invalidate dependent
retrieval/index text without erasing chronology. Already surfaced host output and private backups
are not recalled; never store reflection prose in task/publication receipts.
