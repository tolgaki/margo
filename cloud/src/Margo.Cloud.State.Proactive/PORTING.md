# Margo.Cloud.State.Proactive: porting notes

Ports `skills/chief-of-staff/scripts/proactive_state.py` completely: the `proactive_state.py` CLI (all 24
subcommands) and the library functions that `task_runs.py` and `memory_dream.py` import. Reference
behaviour is CPython 3.13 with the shared helpers of `Margo.Cloud.State.Core` (see its PORTING.md).
Nothing here reaches the network; no real workplace data is used anywhere (fixtures use `example.com`).

## What maps to what

### Library (`ProactiveState`, public static)

| Python | C# | Used by |
| --- | --- | --- |
| `SCHEMA` | `Schema` (verbatim statement text; `sqlite_master` must match) | |
| `ERROR_CLASSES` | `ErrorClasses` | |
| `validate_schema(conn, allow_empty)` | `ValidateSchema(conn, allowEmpty)` | |
| `initialize(conn)` | `Initialize(conn)` | task_runs.py (inside its own transaction) |
| `text`, `integer`, `object_json`, `digest` | `Text`, `Integer`, `ObjectJson`, `Digest` | |
| `fields`, `_family`, `_scope`, `_item`, `_insert_item`, `_window`, `_receipt`, `_select_items`, `_standalone`, `_live_batch` | private helpers of the same names | |
| `_release_expired(conn, now)` | `ReleaseExpired` (internal; the CLI's `prune` uses it) | |
| `_render_item(row, batch)` | `RenderItem` (internal) | |
| `queue_add(conn, data, tier)` | `QueueAdd(conn, data, tier)` | |
| `queue_drain(conn, owner, lease_seconds, limit)` | `QueueDrain(conn, owner, leaseSeconds, limit)` | |
| `queue_release` / `queue_renew` | `QueueRelease` / `QueueRenew` | |
| `publication_record(conn, batch_id, data)` | `PublicationRecord(conn, batchId, data)` (`null` batch = standalone) | memory_dream.py |
| `publication_publish(conn, publication_id, data)` | `PublicationPublish` | tests of memory_dream |
| `queue_ack(conn, receipt_id, batch_id, ids, item_keys)` | `QueueAck` | |
| `publication_review(conn, publication_id, reviewed_at)` | `PublicationReview` | tests of memory_dream |
| `coverage_start(conn, data)` | `CoverageStart` | task_runs.py |
| `coverage_retire(conn, source_key, reason)` | `CoverageRetire` | |
| `_attempt(conn, attempt_id)` | `Attempt` (public: tests and later ports read attempt rows) | |
| `coverage_page(conn, attempt_id, data)` | `CoveragePage` | task_runs/user_journeys tests |
| `coverage_finish(conn, attempt_id, data)` | `CoverageFinish` | task_runs.py (`recover`) |
| `coverage_status(conn)` | `CoverageStatus` | |
| `publication_show(conn, publication_id)` | `PublicationShow` | memory_dream.py (`inspect`) |
| `publication_list(conn, limit)` | `PublicationList` | |
| `status_report(conn)` | `StatusReport` | |
| `import_legacy(conn, directory)` | `ImportLegacy` | |

Python dicts and lists are `JsonObject`/`JsonArray` (the Core value model); data arguments are `JsonNode?` so
non-dict input reaches the same `JSON input must be an object` error. Results are the dicts Python returns;
SQLite values become JSON nodes as `dict(row)` would hold them. Every function composes with an outer
`StateStore.Transaction` (nested calls become savepoints, as `margo_store.transaction` does), which is how
task_runs.py and memory_dream.py call them.

### CLI (`ProactiveStateScript`, `margo-state proactive_state.py ...`)

`parser()` is `ProactiveStateScript.Parser()` with the same subcommands, options, defaults, help strings and
order (the module docstring is `ProactiveStateScript.Doc`, shown with the raw description formatter);
`main()` is `Main` with the same command dispatch, exit statuses (0, 1 for `seen`/`cursor-get` misses, 2 for
argparse/validation/storage errors), stderr lines (`ERROR: ...`, the queue-drain lease line, the cursor hint),
`setup-needed` status, and the same exception families mapped to `storage unavailable; no reset performed`.
`_input`, `_emit` and `_queue_output` are `Input`, `Emit` and `QueueOutput`; the text queue view formats
values with Python `str()` (`PyNode.Str`: dict/list `repr`, `True`/`None`, float repr).

### Probe (`ProactiveProbeScript` / `cloud/contracts/state/probes/proactive_probe.py`)

Not a Margo command: the twin lives in `Margo.Cloud.State.Probes`, which Release builds of `margo-state` do not
contain and which runs only with `MARGO_STATE_PROBES=1` (and the hosted tool runs only scripts classified in
`statecmd.COMMANDS`). It lets the judge reach what separate `proactive_state.py` processes cannot:

- `cli ARGS...` runs `proactive_state.main()` (C#: `ProactiveStateScript.Run`) under an optional fixed clock
  (`--now`; Python patches `utc_now`/`datetime.now`, C# uses `PyRuntime.Override(clock:)`), so freshness,
  missed cadences, escalation, lease expiry and future-dating are deterministic and compared literally;
- `call` runs library functions on one connection, optionally inside an outer transaction that commits or is
  rolled back (task_runs.py / memory_dream.py composition), with NaN/Infinity and non-dict data allowed;
- `sql` applies fixtures (backdated rows, corrupt payloads, dropped tables, wrongly typed values);
- `write`/`link` create legacy import files and symlinks;
- `@{attempt}`, `@{batch}`, `@{source}`, `@{item}`, `@{publication}` (with `:N` / `:-N`) chain random IDs
  across steps.

### Scenarios (`cloud/contracts/state/proactive/`, 19 files)

Every subcommand and option, success and failure paths, replay/idempotency, text and JSON views: argparse
surface and help (`cli_args`), accounts and setup (`setup_and_accounts`), queue add/list
(`queue_add_list`), drain/record/ack/seen/mark (`delivery_end_to_end`), receipts and acknowledgement errors
(`delivery_receipts`), leases, expiry, renew/release, prune (`delivery_leases`), standalone outputs and
receipt/publish/review errors (`publications`), pages (`coverage_pages`), finish (`coverage_finish`), delta
tokens and resume (`coverage_tokens`), failure episodes and freshness (`coverage_health`), source identity,
authentication and start errors (`coverage_sources`), retirement and legacy identity (`coverage_retirement`),
legacy import/cursors/seen (`legacy_import`), library composition (`library_calls`), damaged or tampered
state (`damaged_state`, `damaged_database_file`, `tampered_numbers`), Python ints beyond 64 bits in items,
observations and legacy files (`big_integers`). Each description names the `tests/test_proactive_state.py`
cases it converts; library-level cases are also ported to xUnit in
`cloud/tests/Margo.Cloud.State.Tests/Proactive/`.

## Deliberate deviations (and why)

1. **C# defaults instead of explicit `None`.** `QueueDrain(leaseSeconds: null, limit: null)` and
   `PublicationList(limit: null)` mean the Python defaults (900, 100, 20). Python raises
   `lease_seconds is out of range` when a caller passes `None` explicitly; no importer does. The probe's
   `call` mode therefore only accepts ints for those arguments.
2. **Typed identifiers.** Library functions take `string` ids/owners/reasons where Python would bind any
   SQLite value (an int publication id, say). The CLI only ever passes strings; no importer passes others.
3. **Counts are `long`.** Results always hold Python ints as `long` JSON values (`released`, `acked`,
   `included_items`, ...), so C# callers can read them uniformly. The JSON text is identical.
4. **BLOB values in tampered rows.** `dict(row)` with a BLOB column fails in C# when the row is converted
   (`TypeError`), in Python only when `json.dumps` reaches it. The CLI result is the same (exit 2, `storage
   unavailable`), except the text queue view, which Python prints without serialising `item_key`. Only reachable
   by editing the database by hand. (A BLOB `payload`, `scope`, `window` or `receipt` used to be decoded as strict
   UTF-8 for `json.loads`; since the review of the state consolidation `PyNode.Loads` decodes bytes as CPython does,
   with `json.detect_encoding` and `surrogatepass`: `blob_values.json`.)
5. *(Withdrawn in review.)* Arithmetic on hand-edited values in INTEGER columns now follows Python:
   `PyNumbers.Add` (int + 1 stays an int and fails to bind beyond 64 bits with `OverflowError`; REAL stays a
   float; TEXT/BLOB/None raise `TypeError`), `PyTimeDelta.FromPySeconds` (`timedelta(seconds=x)` for a REAL cadence,
   with CPython's half-even microsecond rounding and its `OverflowError`s) and `PyNumbers.Trunc` (`int()` of a float
   is unbounded). See `tampered_numbers.json`.
6. **Uncaught Python exceptions** (`AttributeError` for a non-dict payload, `ZeroDivisionError`) are
   State.Core's `PyAttributeError`/`PyZeroDivisionError` and escape as exit 1, as in Python. The judge
   compares their class (`damaged_state.json`).

## Python semantics (State.Core)

This port's own helpers (`PyValues.cs`) moved to State.Core in the state consolidation (see its PORTING.md):
`Py.Get`/`Index`/`Pop`/`IsStr`/`Repr`/`Format`, `Py.Node`/`Dict`/`Loads` (SQLite values, `dict(row)`) and `Py.Copy`
are `PyNode`; `Py.Eq`/`Compare`/`Truthy`/`Str` on SQLite values are `PyObj` (which follows `float_richcompare`
for NaN, where `Py` treated NaN as equal to itself; SQLite cannot store NaN); `Py.Add`/`Trunc`/`FloorDivide` are
`PyNumbers`; `Py.TimedeltaSeconds` is `PyTimeDelta.FromPySeconds`; the exception classes are in `PyErrors.cs`.
`Py.Timestamp` is gone: `StateStore.ValidateTimestamp` itself now raises the timestamp `StateError` for a dict or
list, as Python's `validate_timestamp` does.

## Review fixes (second pass)

An independent review diffed the port against CPython 3.13 with generated inputs (random coverage, publication
and queue calls through the probe, argparse corners and help under several `COLUMNS`, undecodable argument
bytes, legacy paths, fixed clocks at the datetime limits) and fixed these differences, each with a failing
scenario and xUnit test first:

- **Python ints beyond 64 bits were corrupted.** `_item`, `_window`, `coverage_page` and `coverage_finish` copied
  input with `JsonNode.DeepClone()`, which turns a `BigInteger` value into `{"IsEven":...,"Sign":1}`: the stored
  payload, the computed revision and item key, observation payloads and page fingerprints differed from Python
  (for example `queue-add --json '{"id":"j","x":1000000000000000000000000000000}'`). Copies now use `Py.Copy` (since the state consolidation `PyNode.Copy`)
  (`big_integers.json`, `Python_ints_beyond_64_bits_keep_their_value_in_payloads_digests_and_pages`).
- **Arithmetic on hand-edited stored values** raised `TypeError` (exit 2) where Python computes a float (a REAL
  `cadence_seconds`, `consecutive_failures`, `episode` or `pages`), and silently wrapped around 64 bits where
  Python fails to bind (`OverflowError`, exit 1) or prints the exact int (`coverage-resume` `next_page`,
  `missed_cadences`) (`tampered_numbers.json`, `Float_timedelta_seconds_round_like_cpython`,
  `Stored_number_arithmetic_raises_python_errors`).
- The probe's `write --hex` refused whitespace between byte pairs, which `bytes.fromhex` accepts, and encoded
  text with replacement characters instead of strict UTF-8.

## Not ported

Nothing from proactive_state.py. Related but owned elsewhere: `margo_doctor.py` (only checks that
proactive_state.py exists); the hosted refusal of `import-legacy` (`statecmd.NOT_HOSTED_COMMANDS`) belongs to
the Engine's `statecmd` port; the C# command itself implements `import-legacy` for parity.

## Open questions

- `status_report` orders `health_events` by `created_at DESC` only, and `publication_show`/`queue_ack` read
  joined publication items without `ORDER BY`. Ties and join order are SQLite query-plan details: the bundled
  `e_sqlite3` and the system SQLite matched in every scenario here (including equal timestamps under a fixed
  clock and a three-item publication), but a future SQLite could differ. Python behaviour kept; an explicit
  tiebreaker in both implementations would remove the risk.
- `main()` initializes the proactive schema for every command, including read commands such as `status`
  (AGENTS.md asks read commands not to initialize). Kept as the specification.
- `publication-review --at` accepts a future timestamp, and `reviewed_at` keeps the first review time on
  replay (`COALESCE`). Kept.
- `coverage_finish` reads the source row before its complete-status checks and raises `TypeError` if the row is
  missing, where Python raises those `StateError`s first. Unreachable: `connect()` refuses broken references and
  foreign keys are enforced.
- `status_report` keys the item and publication counts by `str`; Python would also accept non-text statuses,
  which only a schema rebuilt without its `CHECK` constraints could hold.

## Verification

- `cd cloud && dotnet build Margo.Cloud.slnx` (0 warnings) and `dotnet test --solution Margo.Cloud.slnx`.
- `cloud/tests/Margo.Cloud.State.Tests/Proactive/`: 67 tests, the library-level cases of
  `tests/test_proactive_state.py` (concurrent anchors, lock contention, legacy import, receipts, coverage
  episodes, retirement, standalone outputs, authentication, binding failures), task_runs.py/memory_dream.py
  composition (savepoints inside the caller's transaction), a fixed-clock freshness/escalation case, and the
  helper semantics with CPython-measured values.
- `python3 tools/state_parity.py --check`: all `cloud/contracts/state/proactive/*.json` scenarios pass, with
  the rest of the suite (also after the state consolidation, which changed no proactive output; the State.Core
  `timedelta` lower bound this port reported is fixed there, `StateCoreFixTests`).
