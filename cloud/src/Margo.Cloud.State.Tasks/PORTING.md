# Margo.Cloud.State.Tasks: porting notes

Ports `skills/chief-of-staff/scripts/task_runs.py` (durable bounded task progress over the work ledger, the coverage
journal and receipts) and `task_state.py` (its CLI, every subcommand). Reference behaviour is CPython 3.13, as for
`Margo.Cloud.State.Core`; `tools/state_parity.py` judges the CLI and the library byte for byte
(`cloud/contracts/state/tasks/`). The project builds on `Margo.Cloud.State.Work` (`Ledger`, `WorkLedger.Digest`,
`WorkLedger.Human`) and `Margo.Cloud.State.Proactive` (`ProactiveState.Initialize`, `CoverageStart`, `CoverageFinish`)
and uses only State.Core's shared helpers.

## What maps to what

### task_runs.py (module level: `TaskRuns`)

| Python | C# |
| --- | --- |
| `METRICS`, `KINDS`, `TERMINAL`, `RETRYABLE`, `SCHEMA` | `TaskRuns.Metrics`, `Kinds`, `Terminal`, `Retryable`, `Schema` (byte-identical text; a test compares it with the Python source) |
| `text(value, field, maximum=4000)` | `TaskRuns.Text(JsonNode?, field, maximum)` (code points, `str.strip()`; an `int` or `BigInteger` maximum) |
| `integer(value, field, low=0, high=1000000)` | `TaskRuns.Integer(JsonNode?, field, low, high)` (`type(x) is int`: bool refused; returns a `BigInteger`; `long` or `BigInteger` bounds, as a Python int has no width) |
| `when(value)` | `TaskRuns.When(object?)` |
| `clock()` | `TaskRuns.Clock()`; `TaskRuns.OverrideClock(Func<PyDateTime>)` replaces it for the current async flow, as tests `patch("task_runs.clock")` |
| `resource_cost`, `zero`, `environment(value, account, fresh=False)`, `binding_hash`, `task_identity(account, key)` | `ResourceCost`, `Zero`, `Environment`, `BindingHash`, `TaskIdentity` |

### task_runs.TaskStore (`TaskStore`, `IDisposable`)

| Python | C# |
| --- | --- |
| `TaskStore(account=None, state_root=None, read_only=False)` | `new TaskStore(account, stateRoot, readOnly)` |
| `TaskStore.from_connection(conn, account)` | `TaskStore.FromConnection(PyConnection, account)` (never creates or repairs; validates as read-only, then writable) |
| `account`, `conn`, `ledger`, `transaction()`, `close()` | `Account`, `Conn`, `Ledger`, `Transaction(Func<T>)`/`Transaction(Action)`, `Close()` |
| `create`, `start`, `charge`, `finish`, `pause`, `cancel`, `resume`, `retry`, `recover`, `sync`, `reconcile`, `replan_preview`, `replan`, `show`, `list`, `health`, `history` | methods of the same names (`ReplanPreview` for `replan_preview`) |
| `list()` / `list(limit, after=None)`, `history(run_id)` / `history(run_id, limit)` | `List()` / `List(limit, after)`, `History(runId)` / `History(runId, limit)`: the overloads without a limit are Python's omitted default (50, 100); a null limit is Python's explicit `None` and is refused by `integer()` as in Python |
| private `_initialize`, `_run`, `_step`, `_attempt`, `_event`, `_plan`, `_binding`, `_prerequisites`, `_budget_reasons`, `_token`, `_start`, `_result`, `_settle`, `_sync` | private `Initialize`, `RunRow`, `StepRow`, `AttemptRow`, `Event`, `PlanData`, `Binding`, `Prerequisites`, `BudgetReasons`, `CheckToken`, `StartCore`, `ResultData`, `Settle`, `SyncCore` |

Parameters that Python takes from model-supplied JSON are `JsonNode?` (strings and ints convert implicitly), and they
travel unchanged into SQL parameters, journal events and results, as in Python: a step key given as the int `1` matches
the TEXT column `'1'` by SQLite affinity and is journaled as `1` (`tasks/untrusted_types.json`). Results are
`JsonObject`s.

API other ports need (all public): `memory_dream.py` imports `TaskStore` and `zero` and uses
`TaskStore.from_connection`, `show`, `create`, `start`, `charge`, `finish` and `ledger.show` (`TaskStore.Ledger`);
`margo_doctor.py` uses `TaskStore(principal, state_root, read_only=True)`, `health` and `close`. All of them are
covered by `tasks/library_probe.json` and `TaskStoreTests.Borrowed_store_composes_a_claim_inside_the_callers_transaction`.

### task_state.py (`TaskStateScript`)

`margo-state task_state.py ...`: the same parser (description, help strings, option order, `--account`/`--state-dir`),
all 19 subcommands, `load()` (stdin or a file, `read(262145)`, more than 262144 characters refused, `parse_json`, objects
only), read-only stores for `list`/`show`/`history`/`health`/`replan-preview`, `record-id` without opening storage, the
`**load(args.input)` dispatch with CPython 3.13's binding `TypeError`s (`PyCall`), and the same error handling:
`StateError`, `OSError`, `sqlite3.Error`, `TypeError`, `KeyError` and `ValueError` print
`canonical_json({"error": str(exc), "command": ...})` (plus `"code": "not_initialized"`) on stderr with exit 2; anything
else escapes with exit 1. Output is `canonical_json(result)`.

## Contract probe

`cloud/contracts/state/probes/tasks_probe.py` and its twin `TasksProbeScript` (in `Margo.Cloud.State.Probes`, Debug
builds only, `MARGO_STATE_PROBES=1`) run a JSON program: `task_state.main(argv)` in process with arguments and `--input`
JSON templated from the real clock (`${now+10m}`) and from earlier results (`${claim.token}`), a `{"clock": ...}` step
that fixes `task_runs.clock` (Python) / `TaskRuns.OverrideClock` (C#), `TaskStore` methods (also borrowed with
`from_connection`, read-only, or inside an outer transaction that is rolled back), the module functions, the ledger and
`proactive_state` coverage calls on the store's connection, SQL and input files (prefix + body * N + suffix). Bindings
must be observed within five minutes and deadlines lie within seven days of the real clock, so fixed timestamps cannot
drive these flows; the judge normalises the volatile timestamps, ids and tokens.

## Scenarios (`cloud/contracts/state/tasks/`)

| File | Covers |
| --- | --- |
| `cli_parser_and_errors.json` | help of the root parser and the 19 subcommands (also `COLUMNS=60`), argparse errors, `record-id` and account resolution, read commands before `init` (`not_initialized`), `init` replay, list/history limits and cursors, unknown runs/steps/attempts, reasons, `--input` errors, the `**data` `TypeError`s, cost and outcome validation |
| `input_files.json` (probe) | `--input` files: universal newlines (also split across decoder chunks), BOM, undecodable/truncated UTF-8, the 256 KiB bound in characters, positions relative to the second chunk, pathlib normalisation, stdin |
| `local_workflow.json` (probe) | `test_task_cli.py::test_read_setup_and_full_local_workflow_across_processes` and the claim/settlement refusals around it |
| `plan_validation.json` (probe) | every rule of `_plan` (including unhashable values, cycles, unattended reauthentication and actions, the storage budget) |
| `read_steps.json`, `read_recovery_and_retry.json` (probe) | reads: coverage binding, result validation, actual costs, prerequisites, recovery, exponential and provider backoff, continuation, reconciliation of a completed source |
| `charges_and_tokens.json` (probe) | charges, replays, reservations, tokens, pause/resume bindings, one winner per step, budgets, expired claims |
| `action_steps.json` (probe) | preflight invalidation, gated execution claims, misrouted receipts, recovery to `outcome_unknown`, reconciliation, sync, cancel, approvals that expire or go stale |
| `lifecycle_and_views.json` (probe) | list pages and isolation, pause/cancel/resume, deadline and binding changes, interrupted/blocked/failed/partial/waiting_approval statuses, health with the wall clock |
| `replan.json` (probe) | replan preview and replan with evidence, immutable steps and windows, removed actions, changed bindings |
| `library_probe.json` (probe) | module functions, `from_connection`, read-only stores, library limits, rollback with the caller, account isolation |
| `schema_contracts.json` (probe) | markers, column and foreign-key drift, missing and extra tables, re-creation, a broken proactive schema |
| `untrusted_types.json` (probe) | lists, dicts, None, floats, bools and big ints in every `**data` envelope field |

Converted Python tests: every test in `tests/test_task_runs.py` is a scenario (named in the scenario descriptions);
the concurrent claim, the read-only connection refusing `DELETE`, the outer-transaction rollback, the clock override and
the backoff flow are also xUnit tests (`cloud/tests/Margo.Cloud.State.Tests/Tasks/TaskStoreTests.cs`), as is
`tests/test_task_cli.py::test_read_setup_and_full_local_workflow_across_processes` (in process). Not ported:
`test_task_cli.py::test_installed_task_core_preserves_existing_memory_and_customizations` (it runs desktop Margo's
`install.sh`, which Margo Cloud does not use). `tests/test_memory_dream.py` and `tests/test_user_journeys.py` use
`TaskStore` as infrastructure for their own components; their cases belong to the MemoryCommands port and desktop
journeys (the task calls they make are covered here).

## Deliberate deviations (and why)

1. **Corrupted stored data.** Values this store writes itself (plans, definitions, usage, details, receipts) are read
   with casts; a hand-edited database with the wrong JSON shapes can raise `InvalidCastException` or
   `NullReferenceException` (exit 1) where Python raises `TypeError`/`KeyError`/`AttributeError` (exit 2). The schema
   contract check and every path reachable through the commands match (`schema_contracts.json`). `hmac.compare_digest`
   against a stored token hash that is not text raises a `TypeError` whose text is approximated; the column is
   `TEXT NOT NULL` and only this code writes it.
2. **Dict key order.** Results are built in Python's key order where it is cheap, but nothing guarantees it: every
   command prints `canonical_json` (sorted keys) and the importers read values, so the order is not observable.
3. **The clock override** is an `AsyncLocal` (per async flow) rather than a module global, so parallel tests do not
   share a patched clock. Like the Python patch it changes only `TaskRuns.Clock()`: `utc_now()` stamps, the ledger,
   the coverage journal and `health`'s lease comparison keep the real time.

## Belongs in State.Core (requests)

- **`open(path, encoding="utf-8").read(n)`.** `PyIO.ReadTextFile(path, maxCodePoints)` and `PyIO.ReadBytes` read the
  whole file before truncating, so an undecodable byte after the first `n` characters raises where CPython never reads
  it, a decoding error reports an absolute position where CPython reports one relative to the decoder chunk, and an
  endless or huge file (`/dev/zero`, a multi-gigabyte log) is loaded into memory where CPython reads one bounded chunk.
  `task_state.py` reads `--input` files with `read(262145)`, so `TaskStateScript.ReadFileText` models
  `TextIOWrapper.read(n)` (`textiowrapper_read_chunk`): each chunk is one unbuffered `read(2)` of
  `max(8192, int(max(b2cratio, 1.0) * remaining))` bytes, where `b2cratio` is the bytes per decoded character of the
  previous chunk, through the incremental UTF-8 decoder and the universal-newline decoder holding a trailing CR; it
  opens with `PosixNative.Open` and reads through a non-owning `FileStream` (State.Core has no single-`read(2)` helper;
  one, `PosixNative.Read(fd, buffer)`, would let this drop the `FileStream`). It should move to `PyIO` (memory_state.py
  reads its inputs the same way). Evidence: `tasks/input_files.json`, `TaskStoreTests.Input_files_are_read_like_a_text_wrapper`,
  `Input_chunks_follow_the_bytes_per_character_of_the_previous_chunk`, `Input_files_are_read_no_further_than_python_reads_them`.

## Not ported

Nothing from `task_runs.py` or `task_state.py`.

## Open questions

- `TaskStore.health()` compares lease expiry with the wall clock while every other check uses `clock()`; under a
  patched clock the two disagree (kept, and shown in `lifecycle_and_views.json`).
- A read recovered as interrupted keeps the durable coverage error class (`timeout`, written by recovery) rather than
  the `interrupted` it reported, because `_result` replaces the reported class with the coverage one. Retry still
  works (timeout is retryable); the Python behaviour is kept.

## Review findings fixed

An independent review (differential fuzzing of plans, envelopes, read/local/action flows and argv against the Python
original, all passing) found and fixed, each with a failing scenario or test first:

- `--input` files were read whole before decoding: `--input /dev/zero` grew to 2 GiB and failed with exit 1 where
  Python refuses it after one chunk ("task input exceeds 256 KiB of text", exit 2); any large file was loaded into
  memory (`input_files.json`, `/dev/zero` step).
- Later chunks were sized `max(8192, remaining)` instead of by the previous chunk's bytes per character, so an
  undecodable byte after the bound was decoded where CPython never reads it, and one CPython does decode was missed
  (`input_files.json`, `ratio-wide.json` and `ratio-tail.json`).
- `List(null)`/`History(id, null)` used the default where Python's `list(None)` refuses; `Integer`/`Text` bounds were
  `long`/`int`, so bounds Python accepts overflowed (`library_probe.json`).

## Verification

- `cd cloud && dotnet build Margo.Cloud.slnx` (0 warnings) and `dotnet test --solution Margo.Cloud.slnx`.
- `python3 tools/state_parity.py --check` (all scenarios, including `tasks/`).
