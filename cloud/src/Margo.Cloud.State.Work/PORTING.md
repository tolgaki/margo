# Margo.Cloud.State.Work: porting notes

Ports `skills/chief-of-staff/scripts/work_ledger.py`, `work_productivity.py` and `work_state.py` (the work ledger,
the typed productivity records and the CLI action desk). Reference behaviour is CPython 3.13 (as for
`Margo.Cloud.State.Core`); `tools/state_parity.py` judges the CLI and the library byte for byte.

## What maps to what

### work_ledger.py

| Python | C# |
| --- | --- |
| `ITEM_STATES`, `ITEM_TRANSITIONS`, `SCHEMA` | `WorkLedger.ItemStates`, `ItemTransitions`, `Schema` (byte-identical text; a test compares it with the Python source) |
| `digest`, `text`, `timestamp`, `now`, `human`, `check_revision` | `WorkLedger.Digest`, `Text`, `Timestamp`, `Now`, `Human`, `CheckRevision` |
| `StateError`, `canonical_json`, `utc_now` (re-exports) | `Margo.Cloud.State.Core.StateError`, `StateStore.CanonicalJson`, `StateStore.UtcNow` |
| `Ledger(account, state_root)` | `new Ledger(account, stateRoot)` (`IDisposable`; `Close()`) |
| `Ledger.from_connection(conn, account)` | `Ledger.FromConnection(PyConnection, account)` (`PyConnection.RowFactoryIsRow` stands for `row_factory is sqlite3.Row`) |
| `ledger.account`, `ledger.conn`, `ledger.transaction()` | `Account`, `Conn`, `Transaction(Func<T>)` / `Transaction(Action)` |
| `invalidate_approval`, `event`, `row`, `source`, `find_source`, `source_refs`, `refs_stale`, `source_evidence` | `InvalidateApproval`, `Event`, `Row`, `Source`, `FindSource`, `SourceRefs`, `RefsStale`, `SourceEvidence` |
| `ingest`, `update_item`, `relate` | `Ingest`, `UpdateItem`, `Relate` |
| `propose`, `edit_action`, `disposition`, `approve` | `Propose`, `EditAction`, `Disposition`, `Approve` |
| `begin`, `finish`, `Ledger.validate_receipt` (static) | `Begin`, `Finish`, `Ledger.ValidateReceipt` (static) |
| `show`, `history`, `list` | `Show`, `History`, `List` (plus `Show(string)`/`History(string)` overloads) |
| `import_preview`, `import_commitments`, `export_commitments` | `ImportPreview`, `ImportCommitments`, `ExportCommitments` |
| private `_item_data`, `_item_snapshot`, `_action_data`, `_put_action_revision`, `_check_action_hash`, `_fresh_action`, `_initialize_schema` | private methods of the same names; `_invalidate` is `internal Invalidate` (Productivity calls it, as Python does) |

Values are Python values as `JsonNode` (State.Core's `PyJson` value model). Parameters that Python takes from
model-supplied JSON are `JsonNode?`, so strings and ints convert implicitly (`ledger.Show("item_...")`,
`ledger.Approve(id, 1, hash, evidence, expiresAt)`). Results are `JsonObject`s with Python's key order.

API other ports need (all public): the Guard (`claims.py`) uses `Show`, `History`, `Event` inside `Transaction`,
`Finish` and `Account`; the Orchestrator (`office.py`) uses `Source(..., observedAt:)`, `Ingest`, `FindSource` and
`List("decisions")`; `task_runs.py` uses `FromConnection`, `Show`, `RefsStale`, `Begin`, `Finish`,
`ValidateReceipt`, `SourceRefs(refs, requireCurrent: false)`, `InvalidateApproval`, `WorkLedger.Digest` and
`WorkLedger.Human`; `memory_*.py` use `WorkLedger.Human` and `Ledger.ValidateReceipt`.

### work_productivity.py

| Python | C# |
| --- | --- |
| `STATES`, `TRANSITIONS`, `ARTIFACT_KINDS` | `Productivity.States`, `Transitions`, `ArtifactKinds` |
| `Productivity(ledger)` | `new Productivity(ledger)` |
| `record_id`, `put`, `recap_retry`, `carry_forward`, `share_receipt` | `RecordId`, `Put`, `RecapRetry`, `CarryForward`, `ShareReceipt` |
| `_validate`, `_snapshot` | private `Validate` (split per kind) and `Snapshot` |

### work_state.py

`WorkStateScript` (`margo-state work_state.py ...`): the same parser (description, help strings, option order,
`--state-root`/`--state-dir`), every subcommand including those the hosted tool does not offer (`approve`, `begin`,
`finish`, `reconcile`, `artifact-shared`, `import-preview`, `import-commitments`, `export-commitments`), `load()`
(`-` for stdin, `parse_json`, objects only), the same dispatch and output (`json.dumps(result, ensure_ascii=False,
indent=2, allow_nan=False)`), and the same error handling: `StateError`, `sqlite3.Error`, `OSError`, `ValueError`,
`KeyError` and `TypeError` print `{"error": str(exc), "command": ...}` on stderr and exit 2; anything else
(`OverflowError`, `AttributeError`, `RecursionError`) escapes and exits 1.

Dispatch with `**data` reproduces CPython's call-binding `TypeError`s (`PyCall`): unexpected keywords with the
3.13 "Did you mean" suggestion (`Python/suggestions.c`, ported), duplicate values, missing positional arguments,
and the call-site merge error (`work_ledger.Ledger.relate() got multiple values for keyword argument 'revision'`).

### Python semantics (State.Core)

The dynamic helpers this port first carried itself now live in State.Core (state consolidation, see its PORTING.md):
`PyDynamic` is `PyNode` (with `PyObj` for comparisons), `PyDynamic.Clone` is `PyNode.Copy`, `PyCall` and
`PySortedKeys` moved unchanged (the sort inside `PySortedKeys` is now State.Core's complete `listsort` port,
`PySort`), `PyAttributeError` is in `PyErrors.cs`, and `PosixFiles.Replace`/`PyOSErrorWithTwoPaths` are
`PosixNative.Replace` and `PyOSError.Filename2`. `WorkLedger.Human` is also what State.Memory calls for
`memory_store`'s `from work_ledger import human`.

## Contract probe

`cloud/contracts/state/probes/work_ledger_probe.py` and its twin `WorkLedgerProbeScript` (in
`Margo.Cloud.State.Probes`; `margo-state work_ledger_probe.py` in Debug builds with `MARGO_STATE_PROBES=1`) run a
JSON program: `work_state.main(argv)` in process with arguments and
`--input` JSON templated from the clock (`${now+60m}`) and from earlier results (`${act.action_hash}`), direct
library calls (`Ledger`, `Productivity`, module functions, `Ledger.from_connection` on a borrowed connection),
SQL against the account database and fixture files (JSON, text, raw bytes, symlinks). This is how approvals
(which must expire within seven days), preflights (fresh within five minutes) and actions (random IDs) are judged.
Like `state_core_probe.py`, it is not a Margo command: Release builds of `margo-state` do not contain it.

## Scenarios (`cloud/contracts/state/work/`)

| File | Covers |
| --- | --- |
| `cli_parser_and_errors.json` | help of the root parser and all 23 subcommands (also `COLUMNS=60`), argparse errors, account resolution, `--state-root`, input loading failures |
| `cli_uncaught_errors.json` | `OverflowError`/`AttributeError` escaping with exit 1 |
| `sources_and_ingest.json` | `source`/`ingest`/`show`/`history`/`list` (all views, `--json`), `source(**data)` binding with suggestions, every `_item_data` error |
| `evidence_only_bootstrap.json`, `account_isolation.json` | bootstrap candidates, account isolation, `MARGO_ACCOUNT` |
| `item_lifecycle.json` | `item-update`, `defer` (items), `dismiss` refusal, `relate`: human evidence, transitions, `dict.update` patches, cycles, tracked_in |
| `item_patch_pair_keys.json` | `item-update` patches given as pairs with `null`/bool/int/float keys: CPython's set order and sort comparisons decide the `TypeError` text (review fix) |
| `records_outcomes.json`, `records_meetings.json`, `records_carry_forward.json`, `records_feedback_rules.json`, `records_artifacts.json` | `record-id`, `record`, `recap-check`, `carry-forward` and every validation rule of the five record kinds |
| `import_export.json` | `import-preview`, `import-commitments`, `export-commitments` |
| `actions_propose_approve.json`, `actions_execution.json`, `actions_artifacts.json` (probe) | `propose`, `approve`, `edit`, `defer`/`dismiss` (actions), `begin`, `finish`, `reconcile`, `artifact-shared` |
| `library_probe.json` (probe) | the library API listed above, `from_connection`, `validate_receipt`, `invalidate_approval` |
| `schema_contracts.json` (probe) | atomic schema creation, missing history, legacy adoption, version/marker/column/constraint drift |

Converted Python tests: every test in `tests/test_work_state.py` is a scenario or an xUnit test
(`cloud/tests/Margo.Cloud.State.Tests/Work/`), except the Windows newline test (Linux only). The concurrent double
`begin` (threads), clock-dependent expiry/freshness, nested savepoints, borrowed connections, export modes, the
office-queue flow and the in-process CLI round trip are xUnit tests; `PyDynamicTests` pins `PyCall`/`PyNode`
(formerly `PyDynamic`) to texts produced by CPython 3.13, and `SortedKeyTests` pins `PySortedKeys` to 602 CPython texts
(`SortedKeyFixtures.cs`, regenerated by `Work/generate_sorted_key_fixtures.py`). Of the other Python tests that import these modules,
`test_user_journeys.py::test_meeting_occurrence_delayed_recap_and_carry_forward` is ported as an xUnit test (its
patched `utc_now` becomes a `PyRuntime` clock override); `test_autopilot_guard.py`, `test_autopilot_runtime.py`,
`test_task_runs.py` and `test_memory_*.py` use the ledger as infrastructure for their own components, so their
cases belong to those ports (the ledger calls they make are covered by `library_probe.json` and
`LedgerTests.Uncertain_writes_are_reconciled_never_retried_and_guard_style_claims_settle_once`).

## Deliberate deviations (and why)

1. **Hash-order-dependent messages.** Where Python's message depends on the slots of *strings* in a `set`
   (string hashes are randomised per process), C# picks a fixed order: several drifted tables report the first
   in sorted order ("work schema contract mismatch: ..."). For a `patch` given as a list of pairs with `None`,
   bool, int or float keys, `_item_data`'s `", ".join(sorted(set(data) - allowed))` text is reproduced from
   CPython's own algorithms (`PySortedKeys`: those hashes do not depend on `PYTHONHASHSEED`); the remaining
   approximations are that string keys are assumed not to collide with them in the set's table and unknown string
   keys are placed after them. Python's own text varies between processes in exactly those cases. (Since the state
   consolidation the sort is CPython's complete `listsort`, so 64+ keys are no longer approximated.)
2. **`Source(..., sensitivity: null)`** on the public API means Python's default (`"unknown"`); passing Python's
   `None` explicitly needs the internal `SourceCore` (the CLI and probe use it). C# optional parameters cannot
   tell "omitted" from `null`.
3. **Corrupted stored data.** BLOB values in ledger tables (only possible by editing the database) raise
   `TypeError` when the row is read rather than when it is printed, and a stored JSON value of the wrong shape
   (for example a list where an object was written) may surface as an `InvalidCastException` instead of Python's
   `AttributeError`/`TypeError`. Both exit 1 from the CLI; neither state can be produced through the ledger.
4. **Monkeypatching.** `test_work_schema_creation_is_atomic` patches `SCHEMA`; `Schema` is a constant here, so the
   scenario makes a CREATE fail with a conflicting index instead (same atomicity property, both implementations).
   The Windows newline test (`os.fdopen` patched with `newline="\r\n"`) does not apply: exports are written as
   bytes on Linux in both implementations.

## Review fixes (independent review)

An independent review ran differential tests against CPython 3.13 beyond the shipped scenarios: 80 generated
probe programs (40 mutation fuzzers of 150 steps over every subcommand and the library calls, with wrong types,
big ints, floats, Unicode whitespace, timestamps in every `fromisoformat` shape; 40 random walks of 200 valid-looking
transitions over items, relations, actions and their execution journal, the five record kinds, recaps and
carry-forward), plus targeted cases (actions whose `kind` is `rule`/`meeting`/`artifact`/`feedback`, NUL characters in
raw TEXT columns, lone surrogates, deep and oversized JSON imports, `//`-prefixed and dotted export/import paths).
All matched except one defect, fixed here with a failing-first scenario and tests:

- **Unknown non-string patch keys.** `PyDynamic.JoinSortedError` guessed the `TypeError` text for a patch given as
  pairs with `None`/number keys (it named the first non-`None` key in insertion order). CPython's text follows the
  set's slot order (`hash(None)` is a constant, numbers hash deterministically) and `list.sort`'s comparison order,
  so it is reproducible, and the guess was wrong for 264 of 602 CPython cases (for example `[[2.5, "x"], [1, "y"],
  [null, "z"]]`: Python `'int' and 'NoneType'`, C# `'float' and 'NoneType'`). `PySortedKeys` now ports the
  hashes, `setobject.c` insertion/resizing, `set(dict)` pre-sizing, `set_difference` and 3.13's `count_run` (with
  its ascending-suffix extension) and `binarysort`. Evidence: `item_patch_pair_keys.json` (fails on the original
  build), `SortedKeyTests`.

Observations: the contract probe (`work_ledger_probe.py`) can run arbitrary SQL against the account database and
write files under any `--dir`. Since the state consolidation it lives in `Margo.Cloud.State.Probes`, which Release
builds of `margo-state` do not contain and which runs only with `MARGO_STATE_PROBES=1`; the Engine port must still
run classified scripts only. `Ledger.ValidateReceipt(receipt, string? outcome)`, `Disposition(..., string state)`,
`List(string view)` and the import/export paths take .NET strings where Python accepts any value; every Python
caller passes strings.

## Not ported

Nothing from the three modules. The CLI keeps `--json` as an accepted no-op flag (Python prints JSON either way).

## Open questions

- `Ledger.relate` (Python) scans `work_relationships` of all accounts and `Productivity.put` checks active rules
  and artifact-linked actions without an account filter; ported as is. A shared database holds one account in
  practice (the path is per account), so this is not observable today.
- Python's `list()` ordering of rows with equal `created_at` falls back to `id` (random for actions); both
  implementations agree, but such ties never happen in practice (microsecond timestamps).

## Verification

State consolidation (shared helpers moved to State.Core, probe moved to `Margo.Cloud.State.Probes`): every work
scenario passes unchanged; `core/json_recursion_budget.json` and `core/int_whitespace.json` (State.Core fixes) run
through `work_state.py`.

Before the consolidation:

- `cd cloud && dotnet build Margo.Cloud.slnx`: 0 warnings. `dotnet test --solution Margo.Cloud.slnx`: 2558 passed
  after the review (1955 before; the 603 new ones are `SortedKeyTests`; `ParityTests` runs the judge as well).
- `python3 tools/state_parity.py --check`: 26 scenarios (8 core, 18 work) passed.
- `python3 -m unittest discover -s tests -p 'test_*.py'`: 518 tests OK (4 skipped); `./tools/check-clean.sh`: clean.
- Model-dependent behaviour is out of scope: these are state-command contracts, not model evaluations.
