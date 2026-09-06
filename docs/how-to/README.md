# Margo how-to guides

These guides explain how to use durable work records without turning a suggestion into a promise,
an approval into a send, or an empty search into an all-clear.

| I want to... | Start here |
| --- | --- |
| Install or migrate safely | [Setup and migration](setup-and-migration.md) |
| Get briefed, catch up, or close the day | [Briefs and catch-up](briefs-and-catch-up.md) |
| Clear my inbox or Teams | [Inbox and Teams](inbox-and-teams.md) |
| Draft a reply or an executive follow-up | [Drafting and follow-ups](drafting-and-follow-ups.md) |
| Schedule, reschedule, RSVP, or clean up my calendar | [Calendar management](calendar-management.md) |
| Track an ask or review an action | [Commitments and action desk](commitments-and-action-desk.md) |
| Plan a week and carry meeting work forward | [Outcomes and meetings](outcomes-and-meetings.md) |
| Correct or prepare work | [Feedback and work products](feedback-and-work-products.md) |
| Keep up with people and 1:1s | [Relationships and one-on-ones](relationships-and-one-on-ones.md) |
| Read, download, copy, upload or share a file | [Documents and files](documents-and-files.md) |
| Review GitHub PRs or an Azure DevOps backlog | [GitHub and work items](github-and-work-items.md) |
| Watch a community or feedback channel | [Community and feedback](community-and-feedback.md) |
| Keep a durable decision log | [Decision log](decision-log.md) |
| Check coverage and durable output | [Automation health](automation-health.md) |
| Recall context by meaning and manage private memory | [Semantic memory](semantic-memory.md) |
| Control capture, retention, correction, forgetting and recipe export | [Memory controls and learning](memory-controls-and-learning.md) |
| Know where a task stopped and continue safely | [Task progress and recovery](task-progress-and-recovery.md) |

Use the conversational recipes first. The CLI recipes are for inspecting records, troubleshooting,
or operating with Margo in the foreground. They are not scripts for unattended approval.

## Every feature, by guide

Generated from [`docs/feature-catalog.json`](../feature-catalog.json) with
`tools/feature_catalog.py --write` — see [the full feature index](../features.md#full-feature-index)
for availability and implementation kind. Edit the catalog, not this table.

<!-- BEGIN GENERATED: feature-index -->
| Guide | Features covered |
| --- | --- |
| [../container.md](../container.md) | [Run Margo unattended somewhere other than a laptop](../container.md#running-it) |
| [automation-health.md](automation-health.md) | [Get a quiet daily scan for slow-moving drift](automation-health.md#automation-ambient), [Get a weekly commitments and ambient digest](automation-health.md#automation-commitments), [Get an end-of-day wrap-up automatically](automation-health.md#automation-eod), [Get cheap, silent hourly checks](automation-health.md#automation-hourly), [Get the morning brief without asking for it](automation-health.md#automation-morning), [Get next week's shape before Monday](automation-health.md#automation-week-ahead), [Get one honest health report](automation-health.md#doctor), [Know if a scheduled output was actually delivered](automation-health.md#output-delivery), [Trust that a brief actually covered its sources](automation-health.md#source-coverage) |
| [briefs-and-catch-up.md](briefs-and-catch-up.md) | [See what changed since you were away](briefs-and-catch-up.md#catch-up), [Know what needs attention today](briefs-and-catch-up.md#daily-brief), [Close the day without dropping anything](briefs-and-catch-up.md#end-of-day) |
| [calendar-management.md](calendar-management.md) | [See what your calendar actually costs you](calendar-management.md#calendar-hygiene), [Move a meeting without making a mess](calendar-management.md#calendar-reschedule), [Triage invitations quickly](calendar-management.md#calendar-rsvp), [Find time without the back-and-forth](calendar-management.md#calendar-scheduling) |
| [commitments-and-action-desk.md](commitments-and-action-desk.md) | [See what's ready for your decision](commitments-and-action-desk.md#action-desk), [Review proposals without a second CLI window](commitments-and-action-desk.md#action-desk-canvas), [Approve once, safely](commitments-and-action-desk.md#approval-execution), [Track an ask without inventing a promise](commitments-and-action-desk.md#commitments), [Know what you're waiting on, before it's overdue](commitments-and-action-desk.md#follow-through) |
| [community-and-feedback.md](community-and-feedback.md) | [See what your Viva Engage community actually thinks](community-and-feedback.md#engage), [See what's breaking, from the people hitting it](community-and-feedback.md#teams-feedback) |
| [decision-log.md](decision-log.md) | [Get the current answer, not a stale one](decision-log.md#decision-answer), [Find what's unresolved in the log](decision-log.md#decision-audit), [Get a short weekly read of what changed](decision-log.md#decision-digest), [Log what a team actually decided](decision-log.md#decision-extraction), [Change your mind on the record](decision-log.md#decision-supersession) |
| [documents-and-files.md](documents-and-files.md) | [Know what to read before it's too late](documents-and-files.md#document-queue), [Copy a file server-side within Microsoft 365](documents-and-files.md#file-copy), [Pull a large file from Microsoft 365 to disk](documents-and-files.md#file-download), [Share a file without emailing the bytes](documents-and-files.md#file-sharing), [Upload a local file into Microsoft 365](documents-and-files.md#file-upload) |
| [drafting-and-follow-ups.md](drafting-and-follow-ups.md) | [Get a ready-to-send draft in your voice](drafting-and-follow-ups.md#drafting), [Land a high-stakes message with the right tone](drafting-and-follow-ups.md#executive-followup) |
| [feedback-and-work-products.md](feedback-and-work-products.md) | [Know a work product actually reached someone](feedback-and-work-products.md#artifact-delivery), [Say 'not a lesson' and mean it](feedback-and-work-products.md#do-not-learn), [Correct Margo without a permanent rule you didn't ask for](feedback-and-work-products.md#feedback), [Turn a repeated correction into a standing rule](feedback-and-work-products.md#rules), [Get a useful first draft of the real deliverable](feedback-and-work-products.md#work-products) |
| [github-and-work-items.md](github-and-work-items.md) | [Review your Azure DevOps backlog without hunting for the query](github-and-work-items.md#ado-work-items), [Never miss a stale review request](github-and-work-items.md#github-reviews) |
| [inbox-and-teams.md](inbox-and-teams.md) | [Clear your inbox with confidence](inbox-and-teams.md#inbox-triage), [Find what needs a reply in Teams](inbox-and-teams.md#teams-triage) |
| [memory-controls-and-learning.md](memory-controls-and-learning.md) | [Inspect memory without a CLI window](memory-controls-and-learning.md#memory-canvas), [Let Margo remember useful context, on your terms](memory-controls-and-learning.md#memory-capture), [Correct, suppress or forget what Margo remembers](memory-controls-and-learning.md#memory-control), [Share a lesson without sharing your history](memory-controls-and-learning.md#memory-export), [Teach an approach without granting new permissions](memory-controls-and-learning.md#memory-learning), [See a pattern without it becoming a rule](memory-controls-and-learning.md#memory-trends) |
| [outcomes-and-meetings.md](outcomes-and-meetings.md) | [Check whether a plan actually fits the week](outcomes-and-meetings.md#capacity), [Close the loop after a meeting](outcomes-and-meetings.md#meeting-debrief), [Never rebuild a recurring meeting from scratch](outcomes-and-meetings.md#meeting-lifecycle), [Walk into a meeting ready](outcomes-and-meetings.md#meeting-prep), [Agree what success looks like this week](outcomes-and-meetings.md#outcomes), [Prepare for what next week actually needs](outcomes-and-meetings.md#week-ahead) |
| [relationships-and-one-on-ones.md](relationships-and-one-on-ones.md) | [Never reconstruct a 1:1 agenda five minutes before it](relationships-and-one-on-ones.md#one-on-ones), [Notice who you've quietly stopped talking to](relationships-and-one-on-ones.md#relationships) |
| [semantic-memory.md](semantic-memory.md) | [Recall relevant context by meaning, not just keywords](semantic-memory.md#memory-retrieval) |
| [setup-and-migration.md](setup-and-migration.md) | [Know which account and where data lives](setup-and-migration.md#account-storage), [Get a first useful brief](setup-and-migration.md#setup), [Remove Margo without losing personal files](setup-and-migration.md#uninstall), [Update without losing your settings or work](setup-and-migration.md#upgrade-migration) |
| [task-progress-and-recovery.md](task-progress-and-recovery.md) | [Know where a task stopped](task-progress-and-recovery.md#task-progress), [Pick up a task safely after an interruption](task-progress-and-recovery.md#task-recovery) |
| [../personalization.md](../personalization.md) | [Make Margo work the way you do](../personalization.md#personalization) |
<!-- END GENERATED: feature-index -->

## Before running a CLI recipe

Complete [setup](setup-and-migration.md) first. The examples use Python 3.9+ and a POSIX shell.
On Windows, use your configured Copilot home and Python executable; the Python subcommands and
JSON contracts are the same. The core needs no additional Python packages; semantic embeddings
have a separately installed optional runtime described in the memory setup guide.

Dana is a fictional account. Replace the account value with the owner you explicitly selected.
Do not infer it from a source message, repository settings, or whichever account last signed in.
For a nondefault installation, change `COPILOT_HOME` to that same private installation root.

Run this in each new shell before the operational recipes:

```sh
export COPILOT_HOME="$HOME/.copilot"
export MARGO_ACCOUNT="dana@example.com"
export MARGO_STATE_DIR="$COPILOT_HOME/margo/state"
umask 077
mkdir -p "$COPILOT_HOME/margo/manual/$MARGO_ACCOUNT"
cd "$COPILOT_HOME/margo/manual/$MARGO_ACCOUNT"

work() {
  python3 "$COPILOT_HOME/skills/chief-of-staff/scripts/work_state.py" \
    --account "$MARGO_ACCOUNT" --state-root "$MARGO_STATE_DIR" "$@"
}
proactive() {
  python3 "$COPILOT_HOME/skills/chief-of-staff/scripts/proactive_state.py" \
    --account "$MARGO_ACCOUNT" --state-dir "$MARGO_STATE_DIR" "$@"
}
```

The functions are just explicit, account-scoped CLI invocations. All relative input/output paths
below refer to this private manual directory, not a checkout. `umask` protects new files; it does
not repair an existing shared directory. Check existing permissions before writing workplace data.

`work --help` and `proactive --help` list the installed contracts. Work commands return JSON;
validation and storage failures exit 2 rather than resetting the database.

## Reading the examples safely

- Uppercase values such as `ITEM_ID`, `REVISION`, and `ATTEMPT_ID` are placeholders. Copy returned
  IDs and current integer revisions from `show`, not from another account or an earlier example.
- JSON examples describe shapes or fictional local preparation. Replace source observations with
  actual evidence before using them for real work. Never invent provider IDs or delivery receipts.
- A filename such as `agreed-outcome.json` or `approval.json` is a required input, not a file the
  tool generates. Instructions explain what it must contain.
- No guide supplies synthetic human consent. Approval-dependent commands are conditional recipes:
  run them only after the stated human interaction has really occurred.

### Recording a real human decision

For approval-dependent records, Margo must show the exact subject and revision and obtain a
specific foreground decision. The recorded `evidence` object contains:

| Field | Required source |
| --- | --- |
| `kind` | Literal `human_confirmation` |
| `actor`, `statement` | Actual decision maker and their actual decision |
| `evidence_ref` | Actual `conversation:`, `host-interaction:`, or `legacy-review:` reference |
| `subject_id`, `revision` | Exact subject being decided; current revision, or 1 at creation |
| `decision` | Operation-specific value, such as `confirm`, `revoke`, or `approve` |
| `decided_at` | Actual decision time, as an ISO timestamp with an offset |

Action approval also binds the displayed `action_hash`. Typed records use `record-id` to resolve
their ID before confirmation. Their usual positive decision is `confirm`, even for an outcome
moving to `active`; work-item activation instead uses `activate`.

This is an audit record, not proof that the caller is human. A guessed reference, a model-written
"I approve", a source email, silence, or a canvas review request cannot supply consent.

## Where to find the full contracts

- [Work ledger](../../skills/chief-of-staff/references/work-ledger.md)
- [Action desk](../../skills/chief-of-staff/references/action-desk.md)
- [State operations](../../skills/chief-of-staff/references/state-operations.md)
- [Privacy and safety](../safety.md)

Private SQLite state is not encrypted. The helpers store local evidence and decisions; they do not
call Microsoft 365. Publishing, sending, changing a calendar, and even creating an Outlook draft
are separate outward actions requiring exact approval and a verified invoking Work IQ binding.
