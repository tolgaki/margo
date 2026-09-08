# Developer journey: from checkout to contribution

Margo is a **reference implementation of a Work IQ assistant**, not a hosted service to start
with `npm run dev`. A capable agent host supplies the model, tools and authenticated connections.
This repository supplies the persona, procedures, portable local state APIs and optional UI.
You can develop and test those pieces without a mailbox, Copilot subscription or live model.

[Documentation hub](../README.md) · [User journey](../user-guide.md) ·
[Feature inventory](../features.md) · [Contribution rules](../../CONTRIBUTING.md)

## 1. Orient yourself before changing anything

From a new clone, or your existing checkout:

```bash
git clone https://github.com/tolgaki/margo.git
cd margo
git status --short
git branch --show-current
```

Skip clone/cd if you already have a workspace. Read [AGENTS.md](../../AGENTS.md),
[CONTRIBUTING.md](../../CONTRIBUTING.md), the [state map](architecture.md) and
[change workflow](agent-workflow.md). Preserve existing edits; do not switch branches, stash,
reset, install into a personal profile, or enable schedules as an incidental setup step.

Read the [user journey](../user-guide.md) next. Choose one concrete outcome from the
[feature inventory](../features.md), follow its guide and inspect the linked procedure. This
establishes what the person should see before you decide which module to change.

### The mental model

```text
Request -> persona loads skill -> router selects procedure
        -> agent collects bounded evidence using configured tools
        -> Python core records private state / proposed action / progress
        -> CLI or optional canvas exposes that same state
        -> exact foreground decision + fresh preflight, if execution is needed
        -> actual result, durable receipt and an honest user-facing outcome
```

The model decides what is useful; deterministic code protects identities, transitions,
revisions, claims and budgets. Local state does not authenticate a human, execute a provider
request by itself, or sandbox other host tools. A prepared draft is not a sent message.

| Where | What belongs here | Start here when changing… |
| --- | --- | --- |
| `agents/` | Persona and the user's-draft voice boundary | How Margo speaks, not how she queries mail |
| `skills/*/SKILL.md` | Trigger descriptions, shared rules and routine routing | Which procedure a request loads |
| `skills/*/references/` | Focused procedures, input/output contracts and recovery | How the assistant performs a routine |
| `skills/chief-of-staff/scripts/` | Python 3.9+ standard-library core and CLI adapters | Durable state, validation, calculations or recovery |
| `.github/extensions/margo-action-desk/` | Optional thin action, memory and task-progress views | Presentation over existing CLI contracts |
| `automations/`, `tools/margo-scheduled.*` | Schedule manifests and wrapper execution | Cadence, prompts and unattended restrictions |
| `install.*`, `packaging/` | Copy/link installs, preservation, provenance and native wrappers | How changed files reach another machine |
| `docs/`, `tests/fixtures/journeys/`, `evals/` | User help, catalog mappings, synthetic inputs and trace contracts | Discoverability and evidence for the outcome |

Use the [state-ownership map](architecture.md#owners) before adding persistence. Task runs
track an attempt to help; work items track obligations; the approval journal owns exact action
revisions; coverage owns source completeness. Do not invent another tracker or write SQLite
from a renderer.

## 2. Start with credential-free checks

The default Python suite uses the standard library. There is no core `pip install` step.
Node 22 is the CI baseline for optional canvas tests; the app supplies its extension SDK at
runtime. PowerShell adds installer coverage when available.

```bash
# Fast documentation and mapping checks; no model or workplace account.
python3 tools/feature_catalog.py --check
python3 tools/journey_contracts.py --check
./tools/gen-automations-docs.sh --check
./tools/check-clean.sh
```

The journey checker validates references and reports evidence classes; it does **not** run
the referenced scenarios. Pick the relevant tests in [step 5](#5-prove-the-right-thing).
Read-only CLI inspection, including `--help`, does not require initialization:

```bash
python3 skills/chief-of-staff/scripts/task_state.py --help
python3 skills/chief-of-staff/scripts/work_state.py --help
```

### Optional: rehearse a copied installation without your real profile

Use this only when the installed path matters. This POSIX example creates a **new, fictional,
private fixture home**, not `~/.copilot`. It calls local installers and state helpers only;
it neither launches Copilot nor connects to Work IQ. The parent must be outside repositories
and synchronized/shared directories, owned by you, with no group/world-writable ancestors.

```bash
(
  set -eu
  umask 077
  fixture_home="$HOME/.margo-contribution-fixture"
  mkdir "$fixture_home"  # Deliberately fails if it already exists.
  export COPILOT_HOME="$fixture_home/copilot"
  unset MARGO_CONFIG MARGO_ACCOUNT MARGO_STATE_DIR MARGO_ALLOW_UNSAFE_STATE_DIR

  ./install.sh --all --dest "$COPILOT_HOME" --dry-run
  ./install.sh --all --dest "$COPILOT_HOME"
  scripts="$COPILOT_HOME/skills/chief-of-staff/scripts"
  python3 "$scripts/margo_store.py" init --account dana@example.com
  # Task initialization includes the shared work/coverage schemas.
  python3 "$scripts/task_state.py" init
  python3 "$scripts/task_state.py" list
  ./install.sh status --dest "$COPILOT_HOME"
)
```

Expected result: copied code and fictional account configuration under the fixture home,
initialized work/task state and an empty task list. Account configuration is not OAuth.
`COPILOT_HOME` controls these bundled helpers; check your host's own configuration-root
support separately before launching a host against a test profile.

Do not export `MARGO_ALLOW_UNSAFE_STATE_DIR=1` into a real session to make setup pass.
Some Python tests explicitly use that switch for isolated, synthetic repository fixtures;
the production guard and the real-core canvas test must remain intact.

For Windows, use the existing `test_install_cli` / `test_task_cli` fixtures or
`install.ps1 -Dest` against an equally isolated test destination; CI exercises Windows
installation separately. Do not assume a macOS-only rehearsal proves Windows behavior.

After inspecting the fixture, use `install.sh uninstall --dest` with its **exact fixture
destination** to test preservation. Uninstall intentionally retains account data and personal
files. Remove the remaining fictional fixture only after verifying its location and contents.
The subshell keeps the environment overrides out of your next real session.

Prefer copy mode for development rehearsals. Reserve `--link` for **synthetic-only development
profiles**, never real workplace use. It makes edits visible immediately but also exposes
tracked templates to personalization. Neither link mode, `.gitignore` nor `skip-worktree` is
a privacy control. Keep real accounts, configuration and workplace data in a separate private
copy outside the checkout.

## 3. Trace one existing feature end to end

Take **“Where did you stop? Cancel the remaining steps.”** The task-progress feature is a
useful example because it crosses every layer without granting the UI an execution capability.

| Layer | Existing implementation to read | What to verify |
| --- | --- | --- |
| Request and router | `agents/margo.agent.md`; **Task progress** row in `skills/chief-of-staff/SKILL.md` | Margo loads the skill; recovery phrases reach `references/task-runs.md` |
| Procedure | `skills/chief-of-staff/references/task-runs.md` | Confirm account, inspect the existing run, preserve history and explain cancellation limits |
| State owner | `skills/chief-of-staff/scripts/task_runs.py`, `TaskStore` | Conditional transitions, claims and budgets delegate action truth to the work ledger |
| Public CLI | `skills/chief-of-staff/scripts/task_state.py` | `list`, `show`, `history`, `health` are reads; `cancel` stops future work, not earlier effects |
| Optional canvas | `extension.mjs`, `task-backend.mjs`, `server.mjs`, `task-app.js` in the extension | `margo-task-progress` reads state; a browser review request does not execute cancellation |
| Installed copy | `install.sh`, `install.ps1`; `tests/test_task_cli.py` | Task modules and references ship; existing memory and customized preferences survive |
| User help and catalog | [Task guide](../how-to/task-progress-and-recovery.md); `docs/feature-catalog.json` | Discovery, prerequisites, CLI/canvas references and evidence class remain accurate |
| Scenario | `tests/test_user_journeys.py`; `tests/fixtures/journeys/scenarios.json` | A cancelled task with an uncertain effect never becomes a no-effect success |

Read commands must report missing initialization instead of creating an empty database. The
task panel must show “not initialized” differently from “no runs yet.” The same principle
applies to absent memory, unavailable provider coverage and missing delivery receipts.

### A concrete contribution example

Suppose your task is **make a cancelled run's unknown effect clearer**:

1. Capture the expected outcome: the user sees that future work stopped, the earlier effect
   remains unknown, and reconciliation is needed before another attempt. Non-goal: change
   approval, retry or cancellation semantics.
2. Read the trace above. If the core already returns the right state, change only the
   procedure/help or rendering; do not add a second status field to the database.
3. Extend the existing cancellation/unknown-effect assertion in `tests/test_user_journeys.py`
   when behavior changes. Use `task.test.mjs` for a rendering change. Fictional provider
   receipts are fixtures, not proof that a real message was or was not sent.
4. Keep `references/task-runs.md`, the router if triggers changed, the task guide, catalog and
   mapped scenario aligned. Reuse the current feature ID for an improvement to the same outcome.
5. Run the focused checks below and the installed-copy selector when shipping paths change.
   Record any untested host UI or platform step explicitly in the handoff.

This is one vertical slice: a person can discover it, use it and recover from failure. A new
helper with no caller, guide or scenario is not a complete contribution.

## 4. Choose the smallest extension point that owns the behavior

| Change | Extend | Avoid |
| --- | --- | --- |
| New phrasing or better reasoning instructions | Existing trigger/procedure, with a routing contract or recorded synthetic trace | Adding Python state solely to hold prose |
| New durable transition or calculation | Current owner's public Python API, then CLI and tests | Direct SQL in UI code or silent schema initialization on reads |
| Another routine using existing data | Focused reference file and router entry | A new framework or duplicated “universal” instructions |
| Better visual inspection | CLI response adapter, safe rendering and UI tests | Browser-selected commands, credentials, account paths or approval endpoints |
| Different persona | Agent file; [build-your-own guide](../build-your-own.md) | Tone requirements in shared skills or the user's outgoing drafts |
| Optional model capability | Explicitly installed isolated runtime and clear unavailable state | Implicit model downloads, cloud fallback or core dependency expansion |
| Scheduling change | Manifest, wrapper contract and generated schedule docs | Editing a saved personal workflow or enabling a schedule during development |

Preserve account isolation, nested transaction ownership, conditional revisions and execution
leases. No network/model calls inside a database write transaction. Retrieved content, even a
helpful-looking instruction in a fixture, is data rather than permission.

## 5. Prove the right thing

Run commands from the repository root. `PYTHONPATH=tests` below lets unittest select existing
modules without adding a runner:

| Scope | Existing targeted command |
| --- | --- |
| Example above: task state, CLI and synthetic journeys | `PYTHONPATH=tests python3 -m unittest test_task_runs test_task_cli test_user_journeys` |
| Task-progress rendering | `node --test .github/extensions/margo-action-desk/task.test.mjs` |
| Catalog, routes, trace importer and fixture contracts | `PYTHONPATH=tests python3 -m unittest test_feature_catalog test_journey_contracts` |
| Install preservation and provenance | `PYTHONPATH=tests python3 -m unittest test_install_cli test_install_manifest` |
| Installed task/memory preservation specifically | `PYTHONPATH=tests python3 -m unittest test_task_cli.TaskCLITests.test_installed_task_core_preserves_existing_memory_and_customizations` |

For broader integration, use the [contribution check list](../../CONTRIBUTING.md#testing-your-changes)
and the relevant jobs in [CI](../../.github/workflows/ci.yml). Ordinary prose-only edits need
documentation checks, not a model download or a native package build.

| Evidence class | What a passing result means |
| --- | --- |
| Python/Node unit and runtime journey tests | Observed deterministic behavior, using production helpers and fictional inputs |
| Catalog / procedure contract checks | Valid mappings, referenced commands and declared invariants; no model execution |
| Imported synthetic model trace | Observed routing, actions and output for that exact model/host/fixture revision, with human review |
| Optional real-core canvas integration | Real CLI/core round trip under the production storage guard; not actual host rendering |
| Separately authorized live pilot | Bounded tenant-specific observation; never permission to commit private data |

The full Node run skips its real-core case unless `MARGO_CANVAS_TEST_PARENT` points to an
existing private, non-repository fixture directory. See the
[extension validation contract](../../.github/extensions/margo-action-desk/README.md#validation).
Report skipped tests; “all selected tests passed” is not “all integration paths verified.”

Semantic embedding tests are separately opted in via `MARGO_RUN_EMBEDDING_INTEGRATION=1`,
an isolated Python environment, `requirements-embeddings.txt` and explicit local model setup.
See [memory setup](../how-to/semantic-memory.md). These test local embedding
behavior, not the assistant's reasoning. [Model-trace evaluation](../../evals/README.md)
requires actual imported traces; missing traces, human judgments or counters cannot be invented.

## 6. Keep migrations, packaging and documentation maintainable

Before changing persistent shapes, establish which namespace and schema version owns them.
Preserve existing work, memory and receipts. Test old state, repeated migration, unsupported
schema and interruption through the public APIs. Do not reset private state to make a test pass.
Users must explicitly migrate; status views and canvas reads never repair state silently.

Document backup and recovery in [setup and migration](../how-to/setup-and-migration.md).
Back up SQLite consistently with writers stopped or the backup API, not a live-file copy.
Reinstalling older code is not a database downgrade. Unknown external effects need real
reconciliation, not deleted history or automatic replay.

Update these together when they are affected:

- **User guide:** request, visible result, decisions, limits, data and recovery.
- **Procedure/router:** instructions the host actually loads; avoid duplicating them in every doc.
- **Catalog and journey mapping:** stable IDs, correct availability, sources and evidence class.
  Generate navigation with `python3 tools/feature_catalog.py --write`, then `--check`.
  Do not hand-edit its marked sections in `docs/features.md` or `docs/how-to/README.md`.
- **Schedule table:** edit manifests, run `./tools/gen-automations-docs.sh`, then `--check`.
- **Distribution:** verify both installers and the copied runtime path. Native builds and
  release publication are separate, explicitly authorized steps; see
  [packaging](../../packaging/README.md). A local dirty build is not a release.

## 7. Hand off a complete contribution

Inspect `git diff --check`, the scoped diff and the privacy check. Use the PR template to
explain the outcome, changed surfaces, tests and evidence limits, guide links, migration impact
and known unsupported paths. Include sanitized summaries, never real transcripts or databases.

A reviewer should be able to repeat the synthetic journey without asking for your account.
Commit, push, publish, install into a personal profile and activate schedules only when the
current request authorizes those actions.
