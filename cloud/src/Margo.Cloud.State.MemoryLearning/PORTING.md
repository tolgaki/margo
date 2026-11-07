# Margo.Cloud.State.MemoryLearning: porting notes

Ports `skills/chief-of-staff/scripts/memory_learning.py` and `memory_consolidation.py` completely. Both are libraries
without a command line: `memory_state.py` (the MemoryCommands port) calls them for `capabilities`,
`validate-capability`, `propose-lesson`, `activate-lesson`, `trend-definition`, `trend`, `preferences-preview`,
`preferences-import`, `consolidate` and `record-consolidation`, and `memory_consolidation.py` imports `_data`, `_digest`
and `_file` from `memory_learning.py`. (`memory_dream.py` imports neither.) Reference behaviour is CPython 3.13, as for
`Margo.Cloud.State.Core`; `tools/state_parity.py` judges both libraries through the `memory_learning_probe.py` probe
pair. Nothing here reaches the network, executes a stored recipe or invokes a tool.

## What maps to what

Values are Python values as `JsonNode` (State.Core's PyJson value model). Arguments that `memory_state.py` takes from
model-supplied JSON (`--input`, `--evidence`, `--environment`) are `JsonNode?`, so Python's checks on any JSON type are
reproduced; argparse values (`--revision`, `--limit`, paths) arrive as JSON ints or strings. Results are `JsonObject`s
with Python's key order. Both classes are static; every function takes the open `MemoryStore` first, as in Python.

### memory_learning.py (`MemoryLearning`)

| Python | C# |
| --- | --- |
| `__doc__`, `MAX_OBSERVATIONS`, `MAX_FILE_BYTES`, `TREND_TYPES`, `DEFAULT_TREND` | `Doc`, `MaxObservations`, `MaxFileBytes`, `TrendTypes`, `DefaultTrend` (a fresh dict per read) |
| `_digest`, `_text`, `_strings`, `_time` | `Digest`, `Text`, `Strings`, `Time` (returns a `PyDateTime`) |
| `_environment(memory, value)` | `Environment(memory, value)` |
| `_file(path, expected=None)`, `_json_file(path, expected=None)` | `File(JsonNode? path, expected)` / `File(string path, expected)` and `JsonFile(...)`, returning `(byte[] Payload, JsonObject Source)` / `(JsonObject Data, JsonObject Source)` |
| `_data`, `_refs`, `_prior`, `_conditional`, `_normalise_proposal`, `_validation_scope` | `Data`, `Refs`, `Prior`, `Conditional`, `NormaliseProposal`, `ValidationScope` |
| `capabilities(memory, skills_directory=None, environment=None, tools=None)` | `Capabilities(memory, string? skillsDirectory, JsonNode? environment, string? tools)` |
| `classify_error(code, *, confirmed_defect=False)` | `ClassifyError(code, bool confirmedDefect)` |
| `_input_contract(value, schema, depth=0)` | `InputContract(value, schema, depth)` |
| `_observation`, `validation`, `_feedback` | `Observation`, `Validation`, `Feedback` |
| `lesson_plan`, `activate_lesson` | `LessonPlan`, `ActivateLesson` |
| `_definition`, `configure_trend`, `trend` | `Definition`, `ConfigureTrend`, `Trend` |
| `_preference_sections(content)`, `_preference_file(path)`, `preference_sections(path)` | `ParsePreferenceSections(content)`, `PreferenceFile(path)`, `PreferenceSections(path)` (lists of `(Title, Text)`) |
| `preferences_plan`, `import_preferences` | `PreferencesPlan`, `ImportPreferences` |
| `Path(...).glob("*/SKILL.md")` sorted | `GlobSkills(root)` (internal) |

### memory_consolidation.py (`MemoryConsolidation`)

| Python | C# |
| --- | --- |
| `__doc__` | `Doc` |
| `_stored(memory, key)` | `Stored(memory, key)` |
| `_sources(memory, record, seen=None, budget=None, depth=0)` | `Sources(memory, record, IEnumerable<string>? seen, long[]? budget, int depth)` (the budget is a one-element array shared down the walk, like Python's list) |
| `_current_sources`, `_trend_evidence`, `_proposal` | `CurrentSources`, `TrendEvidence`, `Proposal` (null for Python's `None`) |
| `consolidation_plan(memory, limit=10, environment=None, after=None, scan_limit=100)` | `ConsolidationPlan(memory, PyArg limit, JsonNode? environment, JsonNode? after, PyArg scanLimit)` (`PyArg` keeps "omitted" apart from an explicit `None`, which Python refuses) |
| `surface_consolidation(memory, plan)` | `SurfaceConsolidation(memory, plan)` |

API for the MemoryCommands port (`memory_state.py`): `Capabilities`, `Validation`, `LessonPlan`, `ActivateLesson`,
`ConfigureTrend`, `Trend`, `PreferencesPlan`, `ImportPreferences`, `MemoryConsolidation.ConsolidationPlan` and
`SurfaceConsolidation`. Python opens the store read-only for `consolidate`, `preferences-preview` and a
`trend-definition` without `--evidence`; the functions only read in those cases (`MemoryStore.Transaction` issues a
plain `BEGIN` on a read-only store).

### Dependencies

`work_ledger.Ledger.validate_receipt` and `work_ledger.human` are State.Work's `Ledger.ValidateReceipt` and
`WorkLedger.Human` (this project references State.Work, as `memory_learning.py` imports `work_ledger`). The bounded
file read (`open(...).read(MAX_FILE_BYTES + 1)`) uses State.Memory's public `MemoryNative.OpenRead`/`Read`.
`StateStore.ParseJson`, `CanonicalJson`, `ValidateTimestamp`, `UtcNow`, `PyNode`, `PyObj`, `PySort`, `PyText`, `PyRepr`,
`PyNumbers`, `PyPath`, `PyDateTime`/`PyTimeDelta` and `PyIds` come from State.Core; nothing is copied.

## Things that belong in State.Core (implemented here, requested there)

1. **`PyPath.Resolve` and a NUL in the path.** `Path.resolve()` (`posixpath.realpath`) fails in `os.lstat`, whose text is
   `ValueError: lstat: embedded null character in path`; State.Core's resolver reports the encoder's `embedded null
   byte` (`open()`'s wording). `MemoryLearning.Resolve` maps it (scenarios `files`, `preference_files`). State.Core's
   `PyPath.Resolve` should raise realpath's text itself.
2. **Python's `\s` as a .NET regex class.** `MemoryLearning.PythonSpace` spells out `str.isspace()` (U+001C..U+001F are
   missing from .NET's `\s`); `Preference_whitespace_class_matches_python_isspace` checks it against `PyText.IsSpace`
   for every code point. A shared regex fragment in `PyText` would serve other ports.
3. **`pathlib` glob.** `GlobSkills` implements CPython 3.13's `Path.glob("*/SKILL.md")` (hidden entries included,
   directories followed through symlinks, `lexists` for the last part, so dangling links and directories named
   `SKILL.md` count) and `sorted()` of paths by components. A `PyPath.Glob` would be the general form.
4. **`parse_json(bytes)`.** `_json_file` parses `StateStore.ParseJson(PyIO.DecodeJsonBytes(payload))`. That equals
   `parse_json(payload)` for every outcome `_json_file` can observe: each failure (detect-encoding errors, a second BOM,
   duplicate keys, non-finite numbers, the digit limit) is a `ValueError` that becomes "observation must contain valid
   JSON", and `RecursionError` near the nesting limit is the same (`files.json` covers both). A `StateStore.ParseJson`
   overload for bytes would make the equivalence explicit.

## Deliberate deviations (and why)

1. **A real store only.** Python's `capabilities` and `trend` accept duck-typed stores without `transaction`, `conn` or
   `_normalise_data` (the unit tests' `CaptureMemory`); the C# functions take a `MemoryStore`. `memory_state.py` always
   passes a `MemoryStore`, so no command can observe the difference. The `CaptureMemory` tests were ported against a
   real store (`Capability_is_installed_not_validated_and_hash_change_creates_revision`, `Trends_require_coverage_...`).
2. **Set order in `_input_contract`.** Python checks `additionalProperties` against `set(value) - set(properties)` in
   string-hash order (random per desktop process unless `PYTHONHASHSEED` is fixed); C# uses the value's key order. Only
   an object with two or more extra keys where one nested check raises a `StateError` and another fails earlier can
   differ (which wins depends on the seed). Scenarios use at most one failing extra key; 4800 fuzzed calls agreed.
3. **Path parameters are strings.** `skills_directory`, `tools` and the preference paths are `string` (memory_state.py
   passes argparse strings); Python would also take a `Path` or raise pathlib's `TypeError` for other types. `_file`
   keeps a `JsonNode` overload because fixture paths come from model JSON, with Python's "observation path must name a
   local file" check.
4. **Directory names.** The skill glob reads names through .NET's directory enumeration: a name that is not valid
   UTF-8 is decoded with U+FFFD rather than Python's surrogate escapes, and an unreadable directory yields no entries
   where `os.scandir` would raise. The hosted skill inventory is written by the image, never by the model.
5. **Corrupted stored data.** Records that only a hand-edited database (or a model `put` with crafted metadata) can
   hold, such as non-string failure categories in a capability's `validation_history` or a non-list history, raise
   `TypeError`s whose texts can differ from Python's. The common damaged cases are judged: damaged inventory clocks,
   trend definitions, delivery markers, receipts and feedback evidence (`capabilities_tool_clock`, `trend_definitions`,
   `consolidation_helpers`, `validation_receipts`, `lesson_feedback`). Where crafted metadata changes an outcome
   rather than an error text, it is ported exactly: `trend` keys a prior candidate's `resolved_events` by Python's
   dict-key equality (State.Core's `PyDict`), so events with non-string ids (`1`, `True` and `1.0` are one key) still
   contribute their independent outcomes (`trend_prior_event_keys`).
6. **Unorderable trend outcomes.** `_trend_evidence` sorts a *set* of `(independence_key, matched)` tuples. When a
   hand-made trend mixes unorderable types (a `None` and an int key, say) both raise `TypeError`, but the operand order
   in the message follows CPython's set iteration order, which C# does not reproduce (it uses first-seen order). Only
   the message of this crash on crafted metadata can differ; the parity judge compares the class of uncaught errors.

## Not ported

Nothing from either module.

## Contract probe

`cloud/contracts/state/probes/memory_learning_probe.py` and its twin `MemoryLearningProbeScript` (in
`Margo.Cloud.State.Probes`; `margo-state memory_learning_probe.py` in Debug builds with `MARGO_STATE_PROBES=1`) run a
JSON program: `memory_learning`/`memory_consolidation` calls by their Python names (the store is passed first where the
function takes one; a fresh, held or read-only store), `MemoryStore` calls, Work `Ledger` and `Productivity` calls to
create real execution receipts and feedback records, held transactions, SQL, constants and file fixtures (text, hex,
JSON, symlinks, removals; a write can save its SHA-256 for fixture evidence). Arguments are templated from the clock
(`${now+30m}`) and saved results. Results print as JSON (tuples as lists, bytes as hex, datetimes as ISO text), errors
as `!Type: message`. Execution IDs are random in both implementations, so the scenarios rename each fixture execution
to a fixed ID (`exec_att1`) right after it settles; otherwise source references sorted by canonical JSON would order
differently from run to run.

## Scenarios (`cloud/contracts/state/memory-learning/`, 41 files, about 2000 probe steps)

| Files | Cover |
| --- | --- |
| `helpers`, `input_contract`, `files`, `environment` | constants and every private helper: `_digest`, `_text`/`_strings` bounds in code points and Python whitespace, `_time`, `_data`, `_refs`, `_conditional`, `_validation_scope`, `classify_error`, every `_definition` threshold; the JSON-schema subset with each fail-closed error, bool exclusion, exact int/float bounds and float overflow; `_file`/`_json_file` path types, symlinks and loops, the 1 MiB boundary, pinned hashes, `~` and unknown users, NUL, encodings, BOMs, duplicates, non-finite numbers, digit and nesting limits; `_environment` |
| `capabilities_skills`, `capabilities_skill_budget`, `capabilities_tool_errors`, `capabilities_tool_lifecycle`, `capabilities_tool_clock` | the skill glob and its exclusions, unchanged refreshes, revisions, retirement and reinstall, unbound versus host-scoped records, forgotten records, the 200-skill budget; every manifest refusal; discovery, exposure, retirement, scopes and sources; the inventory clock, damaged clocks and legacy file origins |
| `validation_synthetic`, `validation_shapes`, `validation_receipts`, `validation_without_ledger` | pinned synthetic fixtures (forged hashes, environments, capabilities), degrade/validate/replay, an old success that cannot erase a newer failure, changed fixtures and inventories; capability shapes; real Work-ledger receipts (other hosts, hashes, accounts, failure classification, confirmed defects, older evidence, refreshed history, unsettled/empty/invalid receipts); missing ledger tables |
| `lesson_proposals`, `lesson_activation`, `lesson_activation_guards`, `lesson_environments`, `lesson_feedback` | every `lesson_plan` refusal, counterexamples and lesson text, replay and conditional revisions; activation evidence, pinned-environment recall, immutability; deadlines, moved or unvalidated capabilities, changed fixtures; incompatible environments, skill receipts and edited skill files; `_feedback` and feedback references |
| `trend_errors`, `trend_lifecycle`, `trend_definitions`, `trend_transactions`, `trend_prior_event_keys` | every `trend` refusal; legacy and resolved trends, corroboration, conditional updates, changed events and outcomes, rejected trends, a window ending now; definitions preview/review/revision, thresholds applied, damaged definitions; nested transactions and read-only stores; prior candidates whose events carry non-string or colliding ids and keys |
| `preference_sections`, `preference_files`, `preferences_plan`, `preferences_import` | template parsing (placeholders, example fields, fences, separators), file errors, kinds/routines, chunk boundaries, refusals that never echo private text; import evidence, replay against the receipt, changed sources and targets, forgotten targets |
| `consolidation_*` (13 files), `lifecycle` | Python's consolidation tests as scenarios (trend material changes and wording-only edits, stale pages, pagination, exact duplicates and duplicate pages, lesson revalidation with environments, degraded capabilities, drift and forgotten targets, hash-only receipts, the dependency depth limit), every `surface_consolidation` refusal, `_sources`/`_current_sources`/`_trend_evidence`/`_proposal` directly, read-only stores; one end-to-end run |

Converted Python tests: every case of `tests/test_memory_learning.py` and `tests/test_memory_consolidation.py` is a
scenario, an xUnit test (`cloud/tests/Margo.Cloud.State.Tests/MemoryLearning/`), or both. Cases that patch the clock
(`utc_now` in the trend tests, review deadlines, future observations) are xUnit tests with `PyRuntime.Override(clock:)`;
`test_preference_preview_uses_the_bounded_source_payload` patches `Path.read_bytes` (C# never calls an unbounded read;
the oversized-file half is `Preference_preview_of_an_oversized_file_changes_nothing` and `preference_files`);
`test_deep_dependencies_are_bounded_and_proposed_for_review` patches `_work_sources_current` to prove it is not called
(the scenario `consolidation_dependencies` checks the result; C# has no hook there).

## Open questions

- The Python desktop runs with a random `PYTHONHASHSEED`, so deviation 2 is not even stable between two desktop runs;
  should `_input_contract` iterate extra keys in a defined order upstream?
- `memory_state.py` passes `args.skills_dir`/`args.tools` straight through; an empty `--tools ""` is a path to the
  working directory in both implementations ("observation file is unavailable"), not a missing option. Kept.
- Security note for the hosted command (behaviour kept, Python is the specification): a synthetic fixture's `path`
  comes from model JSON and may name any local file. The refusals tell apart a missing or unreadable file ("observation
  file is unavailable"), a readable file with another hash ("observation file revision changed") and a matching file
  that is not a fixture, so a model can test whether a path exists and whether it has a given SHA-256. The MemoryCommands
  port (or the hosted tool's path policy) should decide whether fixture paths must stay below an allowed root.

## Verification

- `cd cloud && dotnet build Margo.Cloud.slnx`: 0 warnings.
- `dotnet test --project tests/Margo.Cloud.State.Tests/Margo.Cloud.State.Tests.csproj -- --filter-namespace
  Margo.Cloud.State.Tests.MemoryLearning`: 26 tests.
- `python3 tools/state_parity.py --check --filter memory-learning/`: 41 scenarios.
- Independent review (not committed harness): differential runs of the probe pair on generated programs, each about
  60-80 calls on one store: pure helpers with Unicode whitespace, astral and lone-surrogate strings, big ints and
  float/int bounds (1600 calls); mutated trends with duplicate and contradictory reports, big populations, partial
  coverage, configured definitions, then consolidation pages and delivery (20 programs); tool inventories, synthetic
  validations, lessons, activation, inventory refreshes and consolidation (66 programs); preference files near the
  1500 code-point chunk budget with imports and replays (39 programs). The only behavioural difference found was the
  prior-event key handling in `trend` (fixed, deviation 5); the `_trend_evidence` message order is deviation 6.
- Differential fuzzing during the port (not committed): 4800 random pure-helper calls (`_input_contract`,
  `classify_error`, `_preference_sections`, `_definition`, `_refs`, `_conditional`), and mutated trend, manifest,
  definition, environment, lesson and consolidation programs on real stores (about 1100 stateful calls); all outputs and
  final states matched.
- Model-dependent behaviour is out of scope: these are state-library contracts, not model evaluations.
