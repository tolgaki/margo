# Set up and use local semantic memory

Semantic memory is a local search layer over authoritative memory records. It combines exact
context, keyword search and embeddings; a similarity score is never a truth or approval score.
The work ledger remains the source of truth for obligations and actions.

## Choose how much setup you need

| Goal | Required | Not required |
| --- | --- | --- |
| Briefs using current preferences and live sources | Ordinary Margo/Work IQ setup | Memory capture or an embedding model |
| Inspect saved memory and search keywords | Explicit account memory setup | Optional embedding runtime |
| Search saved memory by meaning | Memory setup plus the local runtime/model below | A cloud embedding service |
| Capture observations or learn a standing preference | Reviewed capture/review workflow | Permission to act on the outside world |

Installing a model does not populate memory or enable passive capture. Initializing memory does
not install a model. Keep these choices separate; start with lexical inspection if you want to
understand the records before adding the optional download.

## Start with what you want to do

> What do you remember about my review preferences? Show the sources and anything that is
> out of date. Do not change anything.

Margo should return the relevant current context, not your entire profile. Each memory shows
its origin, authority and scope. A candidate or conflicting observation is not an established
fact, and a missing model is reported rather than replaced with a cloud service.

> Remember this preference for meeting preparation: put the decision needed before background.
> Show me the exact scope before activating it.

Expect a proposed record and a separate confirmation. Remembering a preference never grants
permission to send messages or move meetings.

For pausing capture, correcting or suppressing a memory, reviewing relationships, learning from
outcomes, retention, forgetting and exporting a generic lesson, use
[Memory controls and learning](memory-controls-and-learning.md). The remaining sections here
cover installing the local search runtime and its advanced commands.

## 1. Install the optional local runtime

The ordinary core remains Python 3.9+ and standard-library only. Local embeddings use a private
Python 3.10+ environment with the pinned optional requirements. Model files are not in Git.

After a normal Margo copy install, set the same `COPILOT_HOME` used by the app and schedules.
Choose your installed Python 3.10+ executable instead of the example if necessary:

```bash
export COPILOT_HOME="$HOME/.copilot"
umask 077
python3.13 -m venv "$COPILOT_HOME/margo/embeddings/venv"
"$COPILOT_HOME/margo/embeddings/venv/bin/python" -m pip install \
  -r "$COPILOT_HOME/skills/chief-of-staff/requirements-embeddings.txt"
python3 "$COPILOT_HOME/skills/chief-of-staff/scripts/memory_encoder.py" download
```

On Windows use `Scripts/python.exe` inside the venv. Creating the runtime and downloading public
model weights are explicit setup steps, not work an unattended routine performs.
The pinned MiniLM model runs locally with ONNX Runtime and tokenizers. Inference does not fetch
files, execute downloaded Python code, call a cloud embedding endpoint or log memory text.
If setup is absent or invalid, semantic search fails explicitly; `--mode lexical` is a distinct
fallback you may request.
You can omit this optional setup and use `--mode lexical`. An absent, never-configured semantic
runtime does not make core doctor health fail. A configured runtime that is broken is still
reported as needing attention; nothing is downloaded or repaired automatically.

## 2. Initialise account memory and review a seed

Configure the account using the existing [setup guide](setup-and-migration.md). From the installed
skill directory:

```bash
cd "$COPILOT_HOME/skills/chief-of-staff"
python3 scripts/memory_state.py init
python3 scripts/memory_state.py preferences-preview preferences.md
```

Reads, previews and the panel do not create storage or indexes. If they report
`not_initialized`, confirm the account and run `init` explicitly. `record-id` can calculate a
future record's identity without creating a database. Lexical reads work without a built index.

An existing memory schema version 1 requires an explicit upgrade. Pause memory writers, take a
SQLite-aware private backup, retain the latest deletion journal, and run
`python3 scripts/memory_state.py migrate` before resuming. Ordinary reads never silently migrate.
The upgrade retains existing records and starts passive capture disabled; it does not seed
preferences, install a model, or enable schedules.

The preview exposes saved configuration and target memory revisions for review and returns an
import identity/hash covering both. Only after the user approves that exact plan, supply their real
`human_confirmation` evidence with `decision:"import"`, the returned subject and revision 1:

```bash
python3 scripts/memory_state.py preferences-import preferences.md --evidence PRIVATE_APPROVAL_JSON
python3 scripts/memory_state.py capabilities --skills-dir "$COPILOT_HOME/skills"
python3 scripts/memory_state.py index --limit 100
python3 scripts/memory_state.py status
```

`index` in this example needs the optional runtime from section 1. If you chose keyword-only
use, skip it and select `--mode lexical` in section 3; lexical reads do not need a vector index.

Profile sections are stored as observations of the saved file, not independently verified facts.
Preference sections preserve their user-confirmed scope. Unfilled placeholders are not user
facts. Capability observation means installed, not validated. Do not seed a real account with
fictional examples just to make the screen look populated.
An unchanged completed import can be replayed without writing again. If the file or an imported
record was corrected afterward, old import approval cannot overwrite it; obtain a fresh preview.

**After editing your private `preferences.md`:** imported records are tied to the file's content
hash. Changed or missing mandatory preferences deliberately block context; continuing with the
old copy could ignore a new prohibition. Status and the panel explain this condition. Restore
or correct the file, run `preferences-preview` again, and approve the new `preferences-import`
plan. Unrelated eligible search results remain available; do not bypass the constraint block.

## 3. Search and build context

```bash
python3 scripts/memory_state.py search "How can I avoid a rushed review?" --routine meeting-prep
python3 scripts/memory_state.py context "Prepare my next decision review" --routine meeting-prep
python3 scripts/memory_state.py search "Review preparation" --domain agent
python3 scripts/memory_state.py search "Review preparation" --mode lexical
```

For private queries use `--input -` and JSON `{"query":"..."}` on stdin rather than putting the text
in process arguments. Results explain which channels matched and include source references,
authority and current memory revisions. Scope, validity, routine and environment constraints apply
before recall. Candidate/inferred lessons are not presented as confirmed active rules.
Choose the relevant routine to include its scoped preferences. A general query does not implicitly
apply rules that were accepted only for drafting or calendar work; the panel has the same selector.

The context packet includes applicable mandatory preferences through structured retrieval, not
merely when their vectors score highly. It stops instead of silently omitting mandatory context
that exceeds the requested character budget.

Entries retain `allowed_uses`, `sensitivity` and `copyable_to_draft`. Reasoning-only context may
inform a recommendation but must not be quoted into recipient text. To find material permitted
for that use, use `search ... --usage drafting` (or the panel's **Use purpose** selector).
Keep the reasoning packet's preferences and constraints in force; a drafting filter is not a
reason to drop them or permission to send.

## 4. Capture a new sourced memory

`record-id user KEY` gives the stable local identity. `put KEY --input FILE` accepts a
`{data,status?,evidence?}` envelope and defaults to candidate state. A data shape is:

```json
{
  "domain": "user",
  "kind": "preference",
  "title": "Prepare before decision reviews",
  "text": "Reserve preparation time before meetings where a decision is needed.",
  "authority": "user_confirmed",
  "scope": "personal",
  "sensitivity": "private",
  "allowed_uses": ["reasoning"],
  "entities": ["user"],
  "routines": ["calendar", "meeting-prep"],
  "source_refs": [{"kind": "user_statement", "ref": "ACTUAL_CONVERSATION_REFERENCE"}]
}
```

This is a shape, not real consent. Activating a preference or procedural lesson requires actual
human evidence for its ID and current revision. Changing an existing record is conditional on
`--revision`; source text, a search result or a canvas button cannot provide approval by itself.
Use `domain:"agent"` for scoped procedural lessons or capability observations. Keep those private
too: a lesson can contain tenant-specific context even if it looks generic.
Creating an active confirmed record explicitly uses `--revision 1`; candidate creation does not
need approval. File sources require the actual SHA256 content revision. Their memories become
ineligible if that file changes or disappears.
Exact `entities` values must be scoped identifiers, such as `project:PROJECT_ID`, or the
account-local `user` key. Names belong in title/text; use the structured `identity` for people.
Legacy bare-name aliases are not used to join otherwise distinct identities.

The full searchable representation (title, text, scope, entities and routines) is limited to
16,384 characters. Split longer memories into separately sourced records rather than silently
truncating them. Local file freshness checks are streamed and limited to 1 MiB per cited file;
larger files are unverifiable through this path. Use a bounded, separately revisioned excerpt
or canonical provider evidence instead of treating a partial-file hash as a complete revision.

## 5. Correct, forget and rebuild

Inspect `show ID`, then apply a reviewed conditional `put`. Conflicting evidence should become
disputed; recency alone does not choose the truth.

```bash
python3 scripts/memory_state.py forget MEMORY_ID --revision CURRENT_REVISION \
  --evidence PRIVATE_FORGET_APPROVAL_JSON
python3 scripts/memory_state.py index --rebuild --limit 100
```

Forgetting removes retrievable memory/revision text and derived index copies and retains a
minimal tombstone so the same import identity cannot silently recreate it. It does not delete
the original source, work ledger, past generated messages, or backup copies. A new model
fingerprint requires reindexing; incompatible vectors are never compared as though they share
an embedding space. Repeat bounded rebuilds while `remaining_rebuild` is nonzero.
Rebuilds also drain pending deletion jobs. Legacy over-limit records are isolated as blocked
jobs rather than stopping valid records behind them. Inspect their IDs/reasons in `status`,
revise or explicitly forget the affected records, then index again. Partial progress is reported
as partial; missing/broken runtimes remain retryable errors, not discarded jobs.

## 6. Use the optional panel

Install the action-desk extension as before, reload it, and ask to open **Margo Memory**.
The memory canvas uses the same private CLI, supports semantic search and record inspection,
and can request a foreground correction or forgetting discussion. It cannot alter memory, approve
an action, install models, or send anything.

Browse facts, people, projects, lessons, conflicts and history; select a record to inspect its
sources, recent use and forgetting scope. The bounded graph uses the current routine/domain
filters. Meaning search never silently becomes keyword-only: choose **Keyword only** and a
domain or routine explicitly if the local model is unavailable.

Environment-scoped lessons require an exact current host/tool binding. The panel can inspect
their stored records but may withhold them from search/graphs without that binding; use the
foreground CLI with `--environment` after resolving it. Missing context is not proof the memory
does not exist. All review buttons request a conversation, not an operation or approval.

Keep real profiles, model caches, databases and snapshots outside the repository. SQLite and
embeddings are not encrypted; vector storage is sensitive data too.

## Feature reference

### Memory retrieval

Recall relevant context by meaning, not just keywords, and build a bounded context packet for a
task — see [§3 Search and build context](#3-search-and-build-context). Try it: *"What have you
learned about running smoother reviews?"* or let Margo call this during a routine automatically.
What you'll see: matches with their source, authority (source-observed vs. user-confirmed) and
current revision — a candidate or inferred lesson is never presented with a confirmed rule's
authority. What needs your decision: nothing to recall context, but drafting-permitted content is
still separate from send permission — recalled context never authorizes a send. Change your mind:
nothing to undo; a search doesn't alter memory. Your data: search runs against your private
account-scoped SQLite database, using local embeddings for semantic/hybrid mode or explicit
keyword-only mode — never a cloud embedding service. If something goes wrong: semantic/hybrid
search fails explicitly when the optional encoder is unavailable. Select **Keyword only** in the
panel, or rerun with `--mode lexical` and the required scope; there is no automatic fallback.
Optional (core memory setup is required; the
local embedding runtime is a further optional install for semantic search), runtime (deterministic
hybrid search and context-budget code with tests). Since 1.2.0.
