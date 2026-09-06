# Margo 1.1 how-to guides

These guides explain how to use durable work records without turning a suggestion into a promise,
an approval into a send, or an empty search into an all-clear.

| I want to... | Start here |
| --- | --- |
| Install or migrate safely | [Setup and migration](setup-and-migration.md) |
| Track an ask or review an action | [Commitments and action desk](commitments-and-action-desk.md) |
| Plan a week and carry meeting work forward | [Outcomes and meetings](outcomes-and-meetings.md) |
| Correct or prepare work | [Feedback and work products](feedback-and-work-products.md) |
| Check coverage and durable output | [Automation health](automation-health.md) |

Use the conversational recipes first. The CLI recipes are for inspecting records, troubleshooting,
or operating with Margo in the foreground. They are not scripts for unattended approval.

## Before running a CLI recipe

Complete [setup](setup-and-migration.md) first. The examples use Python 3.9+ and a POSIX shell.
On Windows, use your configured Copilot home and Python executable; the Python subcommands and
JSON contracts are the same. No additional Python packages are needed.

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
