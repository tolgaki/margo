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

| Path | What it covers |
| --- | --- |
| `core/margo_store_cli.json` | `margo_store.py init`: help, argparse errors, create/replay, owner isolation, unsafe roots, file modes |
| `core/probe_json.json` | `json.dumps`/`json.loads`/`margo_store.parse_json`: separators, indent, code-point key order, `ensure_ascii`, float repr, big ints, error positions, depth and digit limits |
| `core/probe_time.json` | `datetime`/`date`/`time.fromisoformat`, `isoformat` timespecs, `timedelta` text, `validate_timestamp`, hashing |
| `core/probe_args.json` | argparse: abbreviations, ambiguity, choices, `type=int`, append, nargs, `--`, help and usage wrapping under `COLUMNS` |
| `core/probe_store.json` | `margo_store.connect` (pragmas, identity rows, private modes), read-only before init, nested transactions, sqlite3 semantics, account resolution |
| `probes/state_core_probe.py` | The Python reference of the contract probe. Its C# twin is `Margo.Cloud.State.Core.StateCoreProbeScript` |

Later ports add a folder per script (`work_state/`, `proactive_state/`, ...).

The probe is not a Margo command. It exists so that the shared helpers every port uses
(`PyJson`, `PyTime`, `PyArgs`, `PyIds`, `StateStore`, `PyConnection`) are judged byte for byte
against CPython, not only through whichever scripts happen to call them. The hosted tool
(`autopilot/src/margo_autopilot/statecmd.py`) runs only classified scripts, so the model cannot
reach it.

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
  `HOME={root}/home` and `MARGO_ALLOW_UNSAFE_STATE_DIR=1` (temporary roots live under a
  world-writable `/tmp`; set it to `"0"` to test the refusal). Nothing else from the caller's
  environment leaks in except `PATH` and `DOTNET_ROOT`.
- `compare_db` and `compare_files` default to `true`.

## What is compared

Per step: the exit status, stdout after normalisation, and whether stderr is empty (stderr text
is shown in `--verbose` diffs but not required to match: it carries Python tracebacks).

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
