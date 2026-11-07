# State command contracts

Scenarios that `tools/state_parity.py` runs against both implementations of Margo's state
commands: the Python helpers in `skills/chief-of-staff/scripts/` (Margo desktop) and their C#
ports behind `margo-state SCRIPT.py ARGS...` (Margo Cloud). A port is finished when every
subcommand, including its failure paths, has scenarios here and `--check` passes.

```sh
cd cloud && dotnet build Margo.Cloud.slnx
python3 ../tools/state_parity.py --check              # every scenario
python3 ../tools/state_parity.py --filter store -v    # unified diff of the first mismatch
```

## Layout

One folder per area; every `*.json` file in them is a scenario (`tests/test_state_parity.py` checks that this
table names every folder and probe).

| Folder | Scripts judged | Scenarios |
| --- | --- | --- |
| `core/` | `margo_store.py`, `state_core_probe.py`, and the shared helpers through `work_state.py` | `margo_store_cli` (init: help, argparse errors, create/replay, owner isolation, unsafe roots, file modes), `margo_store_unicode` (`casefold()`d names, surrogateescape arguments and `MARGO_CONFIG`), `probe_json` (`json.dumps`/`json.loads`/`parse_json`: separators, indent, key order, `ensure_ascii`, float repr, big ints, error positions, depth and digit limits), `probe_time` (`fromisoformat`, `isoformat`, `timedelta` text, `validate_timestamp`), `probe_args` (argparse abbreviations, choices, `type=int`, nargs, help wrapping under `COLUMNS`), `probe_store` (`connect`, read-only before init, nested transactions, sqlite3 semantics, account resolution), `probe_io` (`read_json` on unreadable files), `probe_unicode` (Unicode 15.1 `int()`/`repr()`, astral digits, a state root of `/`, `~user`), `json_recursion_budget` (CPython's C recursion budget in `json.loads` and `parse_json` near the nesting limit), `int_whitespace` (`int()` keeps U+001C..U+001F) |
| `work/` | `work_state.py`, `work_ledger_probe.py` | the CLI parser and errors, sources and ingest, items, relations, the five record kinds, import/export, actions from proposal to execution, the library API, schema contracts, account isolation (see `cloud/src/Margo.Cloud.State.Work/PORTING.md`) |
| `proactive/` | `proactive_state.py`, `proactive_probe.py` | every subcommand: queue, delivery and receipts, leases, publications, coverage pages/finish/tokens/health/retirement, legacy import, library composition, damaged and tampered state, BLOB values (`json.loads(bytes)`), big ints (see `cloud/src/Margo.Cloud.State.Proactive/PORTING.md`) |
| `memory/` | `memory_probe.py` | `memory_store.py`, `memory_governance.py` and `memory_encoder.py` status as libraries: normalisation, identities, put/revise, transitions, links, eligibility, file and work sources, forgetting, schema and migration, read-only stores, governance policy/capture/retention/usage/tombstones/export, Dream sources (see `cloud/src/Margo.Cloud.State.Memory/PORTING.md`) |
| `helpers/` | `capacity.py`, `teams_feedback.py`, `engage_parse.py`, `helpers_probe.py` | the three stateless helpers' command lines, reports, errors and untrusted JSON types, and the Python semantics layer (sort traces, comparisons, numbers, text, `json.dump`, the JSON recursion budget) on its own (see `cloud/src/Margo.Cloud.State.Helpers/PORTING.md`) |

### Contract probes

A probe is a Python reference in `probes/` and its C# twin in `cloud/src/Margo.Cloud.State.Probes`. Probes
are not Margo commands: they exist so that helpers and libraries are judged byte for byte against CPython, not
only through whichever scripts happen to call them. They run arbitrary SQL and write files, so `margo-state`
contains them only when built with `MargoStateProbes=true` (the Debug default; Release builds never do) and runs
one only when `MARGO_STATE_PROBES=1`, which the judge sets for every step. The hosted tool
(`autopilot/src/margo_autopilot/statecmd.py`) runs only classified scripts and never sets it.

| Probe | C# twin | What it reaches |
| --- | --- | --- |
| `probes/state_core_probe.py` | `StateCoreProbeScript` | `json.dumps`/`json.loads`, `margo_store.parse_json`/`canonical_json`/`validate_timestamp`, ISO parsing, a demo argparse parser, hashing and ids, `margo_store.connect` and nested transactions |
| `probes/work_ledger_probe.py` | `WorkLedgerProbeScript` | `work_state.main(argv)` in process with clock- and result-templated input (approvals, preflights, random action IDs), the ledger library API, SQL and fixture files |
| `probes/proactive_probe.py` | `ProactiveProbeScript` | `proactive_state.main()` under a fixed clock, library calls on one connection inside an outer transaction, SQL fixtures, legacy files and symlinks |
| `probes/memory_probe.py` | `MemoryProbeScript` | `MemoryStore`, `memory_governance`, `memory_dream` source helpers and `memory_encoder` calls on fresh or held stores, held transactions, SQL, constants and file fixtures |
| `probes/helpers_probe.py` | `HelpersProbeScript` | the library code of the three stateless helpers and the Python semantics they rely on; `write`/`write-hex`/`write-nested` create input files (also for other scripts' scenarios) |

Later ports add a folder per script family (`tasks/`, `memory_commands/`, ...) and, where a library needs it,
a probe pair; add both to these tables.

## Scenario schema

One JSON object per file:

```json
{
  "description": "what the scenario proves (required)",
  "account": "parity-owner@example.com",
  "env": {"NAME": "value or null to unset"},
  "compare_db": true,
  "compare_files": true,
  "steps": [
    {"script": "work_state.py", "args": ["list", "--view", "all"], "stdin": "text or any JSON value",
     "expect_exit": 0, "env": {"COLUMNS": "40"}, "state_args": true}
  ]
}
```

- `steps` run in order in one fresh private root per run. The judge runs Python, then C#, then
  Python again.
- `script` is a file name in `skills/chief-of-staff/scripts/` or `cloud/contracts/state/probes/`.
  A scenario may combine both, for example `helpers_probe.py write-nested` to create a deep input
  file for `work_state.py`.
- `args` may use `{root}` (the run's temporary root), `{state_dir}` (`{root}/state`) and
  `{account}`. `stdin` strings may use them too; a JSON value is sent as
  `json.dumps(value, ensure_ascii=False)`, as the hosted tool does. Without `stdin` the process
  reads an empty stdin.
- Stateful scripts (`work_state.py`, `proactive_state.py`, `memory_state.py`, `task_state.py`)
  get `--account ACCOUNT --state-dir {state_dir}` prepended, as the hosted tool passes them.
  Stateless scripts get nothing. `state_args` overrides this per step.
- `expect_exit` asserts Python's exit status, so a scenario that stopped testing what it claims
  (for example an error path that started succeeding) fails loudly.
- `env` (scenario, then step) is applied on top of the base environment: `TZ=UTC`,
  `LC_ALL=C.UTF-8`, `PYTHONHASHSEED=0`, `PYTHONDONTWRITEBYTECODE=1`, `PYTHONIOENCODING=utf-8`,
  `HOME={root}/home`, `MARGO_ALLOW_UNSAFE_STATE_DIR=1` (temporary roots live under a
  world-writable `/tmp`; set it to `"0"` to test the refusal) and `MARGO_STATE_PROBES=1`
  (lets `margo-state` run the contract probes). Nothing else from the caller's
  environment leaks in except `PATH` and `DOTNET_ROOT`.
- `compare_db` and `compare_files` default to `true`.

## What is compared

Per step: the exit status, stdout after normalisation, whether stderr is empty, and the error the
hosted tool reports from stderr (`statecmd._first_error`: the last non-empty line, which the model
sees). For an uncaught exception only the exception class is compared (`sqlite3.IntegrityError`
and `IntegrityError` match), because the C# port prints a traceback-shaped report, not a Python
stack. The full stderr text is shown in `--verbose` diffs.

After the last step:

- every SQLite database under the root: `PRAGMA user_version`, `sqlite_master` rows sorted by
  type and name (exact schema SQL), and the rows of every table (`rowid` order, or primary key
  for `WITHOUT ROWID` tables; blobs as hex). Rows of FTS/R-tree shadow tables are skipped
  because their encoding belongs to the SQLite build; their schema is still compared.
- the file tree: relative paths, types, permission bits, text contents (binary files by
  SHA-256). `home/` is compared only under `home/.copilot`, so runtime caches do not count.

Normalisation: the root path becomes `<ROOT>`. ISO timestamps, UUIDs, decimal runs of 9+
digits with a fraction and hexadecimal runs of 8+ characters are tokens. A token whose value
differs between the two Python runs at the same position is volatile and becomes
`<Vn:shape>` (numbered by first occurrence in that run; the shape masks digits and hex letters,
so a format change still fails). A token that is the same in both Python runs must match C#
literally. If the two Python runs cannot be aligned token by token, every token is normalised
and the result line says so.

Use fictional data and `example.com` only. Never put real workplace content in a scenario.
