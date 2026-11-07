# Margo.Cloud.State.MemoryCommands: porting notes

Ports `skills/chief-of-staff/scripts/memory_dream.py` (complete) and `memory_state.py` (the CLI, all 47 subcommands)
on top of the Memory, MemorySearch, MemoryLearning, Proactive and Tasks ports, using only their public APIs and
State.Core's shared helpers. Reference behaviour is CPython 3.13 (3.13.16 here) with its C `zoneinfo` and the system
time zone database; `tools/state_parity.py` judges everything through `memory_state.py` and the
`memory_commands_probe.py` probe pair (`cloud/contracts/state/memory-commands/`, 37 scenarios). Nothing here reaches the
network, calls a model, discovers host history or invokes a tool.

## What maps to what

### memory_state.py (`MemoryStateScript`, `margo-state memory_state.py ...`)

| Python | C# |
| --- | --- |
| module docstring, `parser()` | `MemoryStateScript.Doc`, `Parser()` (same subcommands, options, defaults, choices, help strings and order; `--relation` choices are `sorted(RELATIONS)`) |
| `load(path, maximum=None)` | `Load(path, stdin, maximum)`: stdin `read()` / `read(n)` (`PyIO.ReadText`), files `Path(path).open(encoding="utf-8").read()` (`PyIO.ReadTextFile` of the pathlib-normalised path) or `.read(n)` (`PyTextFile.Read`, below); the 100000-character Dream budget; `parse_json`; objects only |
| `main(argv)` | `Main(argv, stdin, stdout, stderr)`: `record-id` without storage, the read-only command set (and `trend-definition` without a truthy `--evidence`), `MemoryStore(..., migrate=, read_only=)`, `MemorySearch(..., rebuild_schema=)` for `init/migrate/status/search/context/graph/explain/index`, the dispatch with Python's argument evaluation order (for example `--evidence` is loaded before `--input` for `link`, after it everywhere else), `canonical_json` output, and the same `except (StateError, OSError, sqlite3.Error, ValueError, TypeError, KeyError)` mapping to `{"error", "command"[, "code": "not_initialized"]}` with exit 2; anything else (for example `memory_encoder.EmbeddingError`, a `RuntimeError`) escapes with exit 1 |
| `memory.put(args.key, revision=args.revision, **load(...))` | `PyCall.MergeKeywords("memory_store.MemoryStore.put()", ...)` then `PyCall.Bind("MemoryStore.put", ...)`: CPython 3.13's "got multiple values for keyword argument", "got multiple values for argument", "unexpected keyword argument ... Did you mean" and "missing N required positional argument(s)" texts; an omitted `status` stays Python's default (`PyArg.Omitted`) while `"status": null` is an explicit `None` |
| `memory.record_usage(**load(...))` | `PyCall.Bind("MemoryStore.record_usage", ...)`, then `MemoryStore.RecordUsage` with `outcome` omitted or explicit |
| the `ImportError` branch | not reachable: every component is compiled in |

### memory_dream.py (`MemoryDream`, static)

| Python | C# |
| --- | --- |
| `__doc__`, `MAX_INPUT`, `MAX_EVENTS`, `MAX_CLAIMS`, `MAX_PACKET_CHARS`, `INTERPRETATIONS` | `Doc`, `MaxInput`, `MaxEvents`, `MaxClaims`, `MaxPacketChars`, `Interpretations` |
| `MAX_SCAN` (patched by Python's tests) | `MaxScan` with `OverrideMaxScan(int?)` for the current async flow |
| `bounded`, `text`, `scope_for`, `capability`, `_data` | `Bounded`, `Text` (State.Memory's `DreamSources.Text`), `ScopeFor`, `Capability`, `Data` |
| `source_keys`, `validate_source` | not copied: State.Memory's `DreamSources.SourceKeys`/`ValidateSource`, which the store itself calls |
| `checkpoint`, `_window`, `_current`, `_episode_current`, `plan`, `start`, `finish`, `inspect`, `status` | `Checkpoint`, `Window` (returns the UTC start and end), `Current`, `EpisodeCurrent`, `Plan`, `Start`, `Finish`, `Inspect`, `Status` |

Model-supplied values stay `JsonNode`s, so Python's dynamic errors are reproduced where the model can reach them:
`unhashable type` for list/dict claim types and citation IDs, `x is True`/`type(x) is int`/`type(x) is bool` checks,
`dict.get` on non-dicts, Python equality between JSON values, `str()` of revisions, and Python's `and` chain in
`status()["opted_in"]` (it returns `capture["enabled"]` itself when that is falsy).

### zoneinfo (`PyZoneInfo`, `ZoneInfoNotFoundError`) — belongs in State.Core

`_window` builds the daily window with `ZoneInfo(value["timezone"])` (`timezone.utc` for `"UTC"`), rejects cutoffs
whose `utcoffset()` differs between fold 0 and fold 1 or that do not survive `astimezone(utc).astimezone(zone)`, and
compares the end with `datetime.now(timezone.utc)`. `PyZoneInfo` implements CPython's C `zoneinfo` for that:

- **Key lookup** (`_tzpath.find_tzfile`): absolute keys, keys `posixpath.normpath` changes and keys escaping the
  search path raise its `ValueError`s; `TZPATH` comes from `PYTHONTZPATH` (relative entries dropped) or the build default
  `/usr/share/zoneinfo:/usr/lib/zoneinfo:/usr/share/lib/zoneinfo:/etc/zoneinfo`; `os.path.isfile` swallows unencodable
  names and NULs; a missing file is `ZoneInfoNotFoundError('No time zone found with key K')` (a `KeyError`, so
  `str()` is the repr), as CPython without the `tzdata` package says; non-string keys raise `TypeError`
  (`unhashable type: 'list'` before `expected str, bytes or os.PathLike object, not int`).
- **TZif** (`_common.load_data`): version 1 and 2+ files, "magic not found" for other files, transition index checks.
- **Offsets** (`find_ttinfo`, `zoneinfo_fromutc`, `ts_to_local`, the first non-DST `ttinfo_before`): PEP 495 folds
  over the wall-time transition lists, and after the last transition the POSIX TZ string (`Mm.w.d`, `Jn`, `n`, times
  from -167 to 167 hours, `dst_diff` folds and gaps), offsets in whole seconds (local mean time keeps its seconds).

**Deliberate deviation from the brief:** the task asked for zoneinfo semantics on .NET `TimeZoneInfo`. Measured on
this image, `TimeZoneInfo.FindSystemTimeZoneById(key).GetUtcOffset(instant)` differs from CPython in 176914 of 5545837
sampled instants in 403 of 499 zones: it truncates local-mean-time offsets to whole minutes (Abidjan `-00:16:08` becomes
`-00:16`; most of the differences), and among whole-minute offsets it still differs at 542 instants in 10 zones: TZ
rules with transition times of 24, more than 24 or negative hours (Cairo `M10.5.4/24`, Nuuk and Scoresbysund
`M3.5.0/-1`, Santiago `M9.1.6/24`, Jerusalem, Tel Aviv, Gaza and Hebron `M3.4.4/26` and `/50`) and historical
transitions of Guam and Saipan, so a window in those zones would start at another instant. `PyZoneInfo` therefore reads the same TZif files CPython reads (same search path,
same data) and ports CPython's algorithm instead; `TimeZoneInfo` is not used. Measured against CPython's C `zoneinfo`:
0 differences in 5545837 UTC instants (every transition +-1 s and quarterly samples 1800-2399, every 3 days 2030-2099)
and 1196569 fold-0/fold-1 wall times and `fromutc` instants around every transition and 40 years of every TZ rule
(not committed; the committed equivalents are `zoneinfo_sweep`, `zoneinfo_keys_and_offsets`, `dream_window` and the
xUnit tests).

### Other helpers that belong in State.Core (implemented here, requested there)

1. **`open(path, encoding="utf-8").read(n)`**: `PyTextFile` is a copy of the Tasks port's internal
   `TaskStateScript.ReadFileText`/`ReadChunks` (CPython's `TextIOWrapper.read(n)`: chunks of
   `max(8192, b2cratio * remaining)` bytes, positions of decoding errors relative to their chunk, nothing read after the
   last chunk). The Tasks PORTING.md already asks for it in `PyIO`; with two users it should move there.
2. **`PyZoneInfo`** (above): State.Core's `PyDateTime` models fixed offsets only.

## Contract probe

`cloud/contracts/state/probes/memory_commands_probe.py` and its twin `MemoryCommandsProbeScript` (in
`Margo.Cloud.State.Probes`, Debug builds only, `MARGO_STATE_PROBES=1`) run a JSON program: `memory_state.main(argv)` in
process with arguments, stdin (`stdin_json` is `json.dumps(VALUE)`, ASCII, as Python's tests send it) and option files
templated from the clock (`${now+2h}`, `${day-1}`) and saved results (`${started.claim.token}`); `memory_dream`,
`MemoryStore`, `memory_governance`, `TaskStore` and `proactive_state` publication calls on a fresh store, optionally under
another account or state root (`${state_dir}/backup`); `task_runs.clock` and `memory_dream.MAX_SCAN` replacements; a
writer holding a transaction on another connection; SQL; files (with a saved SHA-256); `_window`; `ZoneInfo` offsets and
`astimezone`; and a per-zone digest sweep (`zone_sweep`). This project grants `InternalsVisibleTo` to the probes and the
tests (`MemoryStateScript.Main`, `PyTextFile`).

## Scenarios (`cloud/contracts/state/memory-commands/`, 37 files)

| Files | Cover |
| --- | --- |
| `cli_help`, `cli_argument_errors` | help of the root parser and every subcommand (also narrow and wide `COLUMNS`), argparse errors for every option kind, abbreviations |
| `cli_storage_and_accounts`, `cli_migrate` | read commands never initialise (`not_initialized`), `record-id` without storage and account resolution, init/migrate replays, `MARGO_STATE_DIR`, a second account; a v1 schema refused until `migrate` |
| `cli_input_loading` | `load()` for files (universal newlines, pathlib normalisation, BOM, undecodable bytes, duplicate keys, NaN, missing, directories, NUL) and stdin, `--input -` with `--evidence -`, the Dream `read(100001)` budget with chunk-relative decoding errors |
| `cli_memory_lifecycle`, `cli_policy_usage_capture`, `cli_search_context_index`, `cli_review_regressions`, `cli_lexical_without_index` | every lifecycle, policy, usage, capture, search/context/graph/explain/index, link/unlink, forget, tombstone and maintain subcommand with its options and failures, the `**data` binding errors, a held writer transaction, missing and dropped semantic tables |
| `cli_learning_commands`, `cli_trends_and_preferences` | capabilities, validate-capability, propose/activate-lesson, export-preview/export, consolidate, record-consolidation, trend-definition, trend, preferences-preview/-import and the changed-preferences guard |
| `dream_*` (21 files) | every test of `tests/test_memory_dream.py` that does not need a fake encoder or a patched module (see below), checkpoint/start/finish validation in Python's order, plan bounds and paging, read commands without state, the Dream daily window |
| `zoneinfo_sweep`, `zoneinfo_keys_and_offsets` | all 499 system zones around every transition and 30 years of TZ rules; key errors; hand-picked folds and gaps |

Converted Python tests:

- `tests/test_memory_dream.py`: every test is a scenario except the ones that patch internals:
  `test_utc_reflection_does_not_require_a_timezone_database` patches `ZoneInfo` and becomes `PYTHONTZPATH=""` (no zone
  database) in `dream_without_timezone_database`; `test_checkpoint_capture_override_is_bound_to_the_canonical_root` and
  `test_forget_journal_erases_preexisting_alternate_source_identity` exercise `memory_governance.capture(...,
  _checkpoint_ref=)` and a patched `_resolve_work_sources` (store-level, judged by `memory/dream_sources.json`);
  `test_source_correction_during_index_inference_cannot_reinsert_old_episode` and the encoder halves of the corrections
  and missing-source tests need a fake encoder (MemorySearch's probe judges indexing; the CLI has no local runtime);
  `test_concurrent_corrections_have_one_revision_owner` is an xUnit test.
- `tests/test_memory_cli.py`: both CLI tests are scenarios (`cli_memory_lifecycle`, `cli_learning_commands`);
  `test_installed_copy_contains_memory_modules_and_cli` runs desktop Margo's `install.sh` and is not ported.
- `tests/test_memory_review_regressions.py`: the CLI cases are scenarios (`cli_storage_and_accounts`,
  `cli_review_regressions`, `cli_lexical_without_index`, `cli_trends_and_preferences`); the store-level cases belong to
  the Memory and MemorySearch ports; `test_optional_runtime_absence_does_not_fail_core_doctor_health` is margo_doctor's.
- `tests/test_user_journeys.py` has no memory steps on this branch. `tests/test_semantic_e2e.py` needs the real model
  (`MARGO_RUN_EMBEDDING_INTEGRATION=1`); with embeddings unavailable its CLI-visible parts (index without a runtime,
  restart, forget, account isolation) are in `cli_search_context_index`, `cli_memory_lifecycle` and
  `cli_storage_and_accounts`.

xUnit (`cloud/tests/Margo.Cloud.State.Tests/MemoryCommands/`, 32 tests): CPython-measured folds and `fromutc`, key
errors, `normpath`, an empty `TZPATH`, the window under a fixed clock (folds, gaps, unfinished days, `OverflowError`),
the `MAX_SCAN` override scope, concurrent corrections with one revision owner, the chunked reader and an in-process CLI
round trip.

## Deliberate deviations (and why)

1. **`TimeZoneInfo` is not used** (see above).
2. **`PYTHONTZPATH` with relative entries**: CPython drops them and prints an `InvalidTZPathWarning` on stderr when the
   module is imported; the port drops them silently. Hosted Margo never sets the variable.
3. **The `tzdata` package**: CPython falls back to it when no file is found; the reference interpreter has none, and
   the port has none either, so a missing zone is `ZoneInfoNotFoundError` in both. A desktop Python with `tzdata`
   installed (or another tzdata release) would accept other keys or offsets.
4. **Damaged TZif files**: files in the system database are valid; for truncated or corrupt files with the TZif magic
   the port raises `ValueError`s whose texts (`unexpected end of file`, `... is not a valid TZ string`) can differ from
   CPython's `struct.error`/`AssertionError` texts. Only a hand-written file under `TZPATH` can do that.
5. **The ZoneInfo cache** is not modelled (it is not observable: zone files do not change while a command runs).
6. **Uncaught exceptions** (`OverflowError` at the limits of the datetime range, `EmbeddingError`) exit 1 with a
   traceback-shaped report; the judge compares the class (State.Core deviation 6).

## Edits outside this project

- `cloud/src/Margo.Cloud.State.Probes`: the probe twin `MemoryCommandsProbeScript` and a project reference to this
  project (probes live there by convention); `cloud/contracts/state/probes/memory_commands_probe.py`.
- `cloud/tests/Margo.Cloud.State.Tests/ParityTests.cs`: the judge's time limit went from 15 to 40 minutes. With the
  37 memory-commands scenarios (about 2 minutes) the whole judge (219 scenarios) took about 17 minutes standalone on
  this loaded 4-core runner, so the 15-minute limit cancelled a passing run. The judge itself is unchanged.
- Documentation rows in `cloud/contracts/state/README.md`, `cloud/src/Margo.Cloud.State.Probes/PORTING.md` and
  `cloud/README.md`.

## Open questions

- The hosted image needs the system time zone database (`tzdata`) for IANA zones; without it every zone but `UTC` is
  `ZoneInfoNotFoundError`, exactly as CPython without tzdata. The Engine/Host owners should make sure the container
  installs it.
- `finish()` re-reads the cited events when it runs; if an event became `sensitive` after the plan, Python still labels
  a claim citing only it `public` (`"private" if any(... == "private") else "public"`). Kept as the specification.
- `inspect` reads at most 500 records (`MAX_SCAN`) to find outputs and reports `inspection_truncated`; outputs beyond
  that are not listed. Kept.

## Verification

- `cd cloud && dotnet build Margo.Cloud.slnx`: 0 warnings.
- `dotnet test --project tests/Margo.Cloud.State.Tests/Margo.Cloud.State.Tests.csproj -- --filter-namespace
  Margo.Cloud.State.Tests.MemoryCommands`: 32 tests.
- `python3 tools/state_parity.py --check --filter memory-commands/`: 37 scenarios; `--check` (all): 219 passed.
- `dotnet test --solution Margo.Cloud.slnx`: 5180 tests, 5179 passed, 1 skipped (the Engine's container-image check);
  the Engine's `Initialise_prepares_memory_and_task_state`, skipped until `memory_state.py` was ported, now runs.
- `python3 -m unittest discover -s tests -p 'test_*.py'`: 520 tests OK (4 skipped); `./tools/check-clean.sh`: clean.
- Model-dependent behaviour is out of scope: these are state-command contracts, not model evaluations.
