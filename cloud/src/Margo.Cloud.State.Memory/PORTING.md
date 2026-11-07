# Margo.Cloud.State.Memory: porting notes

Ports `skills/chief-of-staff/scripts/memory_store.py` (complete), `memory_governance.py` (complete) and the observable
behaviour of `memory_encoder.py` in Margo Cloud, where local embeddings are never available (no ONNX model, no
`onnxruntime`/`tokenizers`, no private interpreter is ever run, nothing is downloaded). These are libraries: the
`memory_state.py` CLI that calls them is a later port (MemoryCommands). Reference behaviour is CPython 3.13, as for
`Margo.Cloud.State.Core`; `tools/state_parity.py` judges the libraries through the `memory_probe.py` probe pair.

## What maps to what

Values are Python values as `JsonNode` (State.Core's PyJson value model). Parameters that Python receives from
model-supplied JSON (through `memory_state.py ... --input`) are `JsonNode?`, so strings and ints convert implicitly
(`store.Show("mem_...")`, `store.Put(key, data, "active", 1, evidence)`), and Python's dynamic errors on unexpected types
are reproduced (unhashable `TypeError`s, `AttributeError`, `KeyError`, SQLite binding errors, `str()` of the value in
`unknown memory: ...`). Where Python's default is not `None`, the parameter is a State.Core `PyArg` (omitted -> Python's default,
`PyArg.None` -> an explicit `None`), so `put(status=None)` still fails as in Python. Results are `JsonObject`s with
Python's key order (ints stored as `long`).

### memory_store.py

| Python | C# |
| --- | --- |
| `SCHEMA_VERSION`, `MAX_SOURCE_FILE_BYTES`, `MAX_MEMORY_TEXT_CHARS`, `PREFERENCES_RECOVERY` | `MemoryStore.SchemaVersion`, `MaxSourceFileBytes`, `MaxMemoryTextChars`, `PreferencesRecovery` |
| `DOMAINS`, `KINDS`, `AUTHORITIES`, `SENSITIVITIES`, `USES`, `SOURCE_KINDS`, `STATUSES`, `TYPED_RELATIONS`, `RELATIONS`, `OBSERVATIONAL_KINDS`, `TRANSITIONS` | static `IReadOnlySet<string>`s / `IReadOnlyDictionary` of the same names |
| `SCHEMA_V1`, `SCHEMA` | `MemoryStore.SchemaV1`, `MemoryStore.Schema` (byte-identical; `encoder_constants.json` prints both) |
| `search_text`, `memory_identity`, `scoped_entity_key` | `MemoryStore.SearchText`, `MemoryIdentity`, `ScopedEntityKey` (static) |
| `MemoryStore(account, state_root, migrate, read_only)` | `new MemoryStore(account, stateRoot, migrate, readOnly)` (`IDisposable`; `Close()`) |
| `account`, `read_only`, `conn`, `transaction()` | `Account`, `ReadOnly`, `Conn` (`PyConnection`), `Transaction(Func<T>)`/`Transaction(Action)` |
| `_identity`, `record_id` | `Identity` (returns `(Id, KeyHash)`), `RecordId` |
| `_normalise_data`, `_normalise_source_refs`, `_resolve_work_sources(data, _checkpoint_ref=)` | `NormaliseData`, static `NormaliseSourceRefs`, `ResolveWorkSources(data, checkpointRef)` |
| `_content_hash`, `_row`, `_flatten`, `show`, `list` | static `ContentHash`, `Row` (a `PyRow`), `Flatten`, `Show`, `List` |
| `_human` | `Human` (State.Work's `WorkLedger.Human`, as Python imports `work_ledger.human`) |
| `dream_index_current`, `put`, `revise`, `_put(..., _checkpoint_ref=)` | `DreamIndexCurrent`, `Put`, `Revise`, `PutRecord(memoryId, keyHash, data, status, revision, evidence, checkpointRef)` |
| `link`, `links`, `active_links`, `unlink`, `history` | `Link`, `Links`, `ActiveLinks`, `Unlink`, `History` |
| `_derived_descendants`, `forget_preview`, `forget(..., policy_revision=)` | `DerivedDescendants` (a sorted set), `ForgetPreview`, `Forget(memoryId, revision, evidence, policyRevision)` |
| `_environment_subset`, `freshness_pass`, `_file_digest`, `_work_sources_current(data, seen)` | `EnvironmentSubset`, `FreshnessPass(Func<T>)`, `FileDigest(path)`, `WorkSourcesCurrent(data, seen)` |
| `eligible`, `policy`, `capture`, `record_usage`, `usage` | `Eligible`, `Policy`, `Capture`, `RecordUsage`, `Usage` |
| `pending_jobs`, `complete_job`, `block_job`, `blocked_jobs`, `health` | `PendingJobs`, `CompleteJob`, `BlockJob`, `BlockedJobs`, `Health` |
| private `_initialize_schema`, `_migrate_v1`, `_snapshot`, `_queue`, `_invalidate_derived_indexes`, `_cycle`, `_protected`, `_activation_confirmation_required`, `_transition_decision`, `_evidence_json` | private methods of the same meaning |

### memory_governance.py

`MemoryGovernance.Digest`, `DefaultPolicy` (the inline default of `policy`), `Policy`, `NormalisePolicy`,
`PolicyPreview`, `SetPolicy`, `AuthorizeCapture`, `Capture(memory, key, data, revision, checkpointRef)`,
`RetentionAuthorized`, `Maintain(memory, limit, after)`, `Tombstones`, `TombstonesPreview`, `RestoreTombstones`,
`ExportPreview`, `ExportRecipe`; `Doc` is the module docstring.

### memory_encoder.py (status without a model or runtime)

| Python | C# (`MemoryEncoder`) |
| --- | --- |
| `MODEL_ID` ... `DOWNLOAD_TIMEOUT_SECONDS`, `PREPROCESSING`, `ASSETS`, `__doc__` | constants, `Preprocessing`, `Assets` (`Asset` records), `Doc` |
| `EmbeddingError` | `EmbeddingError` (`PythonType` "EmbeddingError") |
| `_canonical_json`, `_identity_payload`, `model_spec`, `_expected_manifest` | `CanonicalJson`, `IdentityPayload`, `ModelSpec`, `ExpectedManifest` |
| `_copilot_home`, `default_model_dir`, `_safe_model_path`, `_private_python` | `CopilotHome`, `DefaultModelDir`, `SafeModelPath`, `PrivatePython` |
| `_private_file`, `_digest_bytes`/`_new_hasher`, `_verify_asset`, `_read_manifest`, `_validate_model` | `PrivateFile`, `DigestBytes`, `VerifyAsset`, `ReadManifest`, `ValidateModel` |
| `_runtime_available`, `_validate_texts` | `RuntimeAvailable` (always false), `ValidateTexts` |
| `status`, `status_local`, `encode_local` | `Status`, `StatusLocal`, `EncodeLocal` |

### Dependencies ported here

`work_ledger.human` is not copied: this project references State.Work and calls `WorkLedger.Human`, as
`memory_store.py` imports it (the copy `HumanEvidence` was removed in the state consolidation).

| Python | C# | Why here |
| --- | --- | --- |
| `memory_dream.source_keys`, `validate_source`, its `text` | `DreamSources.SourceKeys`, `ValidateSource`, `Text` | memory_store/governance call them; memory_dream sits above this layer, so its MemoryCommands port should call these rather than re-port them. |
| `memory_governance.digest` (used by `validate_source`) | `MemoryGovernance.Digest` | |

API other ports need (all public): memory_search (`Show`, `List`, `Eligible`, `PendingJobs`, `CompleteJob`,
`BlockJob`, `BlockedJobs`, `DreamIndexCurrent`, `Account`, `ReadOnly`, `Conn`, `SearchText`, `ScopedEntityKey`,
`MaxMemoryTextChars`, `MemoryEncoder.EncodeLocal`/`EmbeddingError`), memory_context (`Show`, `Links`, `List`, `Eligible`,
`WorkSourcesCurrent`, `PreferencesRecovery`, `ScopedEntityKey`, `Conn`), memory_learning (`Transaction`, `Put`,
`Revise`, `Show`, `List`, `RecordId`, `Human`, `NormaliseData`, `ResolveWorkSources`, `WorkSourcesCurrent`),
memory_consolidation (`Transaction`, `Show`, `WorkSourcesCurrent`, `EnvironmentSubset`), memory_dream (`Identity`,
`RecordId`, `Show`, `Policy`, `Capture`, `NormaliseData`, `ResolveWorkSources`, `WorkSourcesCurrent`,
`MemoryGovernance.AuthorizeCapture`/`Capture(..., checkpointRef)`/`Digest`, `DreamSources`), memory_state
(`MemoryStore(..., migrate:, readOnly:)`, `MemoryIdentity`, `Relations`, every store method, every governance function,
`MemoryEncoder.StatusLocal`).

### Python semantics

The dynamic helpers this port first carried itself moved to State.Core in the state consolidation (see its
PORTING.md): `Py` is `PyNode` (`Py.Clone`/`Copy` are `PyNode.Copy`/`CopyObject`, `Py.DictWith` is `PyNode.DictWith`,
which now reads every pair before failing, as CPython's `dict()` does: `consolidation_dict_pairs.json`), `Py.Equal`
and `Py.Str(object)` on SQLite values are `PyObj.Eq`/`PyObj.Str`, `PyArg` moved unchanged, `PyAttributeError`,
`PySliceKeyError` (`PyKeyError.ForSlice`) and `PyOSErrorTwoPaths` (`PosixNative.Link` now names both paths) are in
State.Core, and `MemoryUnicodeData` with its generator became State.Core's `PyUnicodeData`/`PyText`
(`IsWord`, `IsSpace`, `IsDigit`, `MatchesIgnoreCase`). `MemoryStore.ValidateTimestamp` is gone: State.Core's
`StateStore.ValidateTimestamp` raises the timestamp `StateError` for a list or dict itself (review finding 1).
What stays here:

| Python behaviour | C# | Note |
| --- | --- | --- |
| the regular expressions of memory_store.py and memory_governance.py (`\w`, `\s`, `\b`, `(?i)` over code points) | `PyPatterns` | matched by hand on State.Core's Unicode 15.1 tables; .NET's `Regex` classes differ |
| `os.stat` device/inode/nanosecond times, `os.fstat`, buffered `read` | `MemoryNative`, `PyFileStat` | State.Core's `PyStat` has mode/uid/nlink/size only |
| `os.stat`'s `ValueError: stat: embedded null character in path` (after encoding the path) | `MemoryNative.Stat` | `PyIO.FsEncode` says `embedded null byte` (`open()`'s and `pwd`'s text), which is right for those callers |

## Contract probe

`cloud/contracts/state/probes/memory_probe.py` and its twin `MemoryProbeScript` (in `Margo.Cloud.State.Probes`;
`margo-state memory_probe.py` in Debug builds with `MARGO_STATE_PROBES=1`) run a
JSON program: library calls on a fresh or a named open store (`MemoryStore`, `memory_governance`, `memory_store`
module functions, `memory_dream` source helpers, `memory_encoder`), held transactions (`begin`/`commit`/`rollback` on
an open store), SQL and scripts against the account database (`@SCHEMA_V1`/`@SCHEMA` name the schema text), module
constants, and file fixtures (text, bytes, JSON, repeated text, modes, symlinks). Arguments are templated from the
clock (`${now+60m}`) and earlier results (`${row.id}`). Results print as JSON, errors as `!Type: message` (the random
`.memory-export-XXXXXXXX` staging name is masked). It is not a Margo command: Release builds of `margo-state` do
not contain it.

## Scenarios (`cloud/contracts/state/memory/`, 39 files, about 1700 probe steps)

| Files | Cover |
| --- | --- |
| `encoder_constants`, `encoder_status`, `encoder_private_runtime`, `encoder_model_dir_environment` | every encoder constant, identity/fingerprint, digests, `_validate_texts`, `status`/`status_local`/`encode_local` without a runtime (unsafe default directory, explicit and environment directories, missing/non-private/symlinked/invalid/expected manifests, asset checks, missing and existing private interpreter) |
| `store_normalisation`, `store_identity_text`, `store_surrogates` | every `_normalise_data` message, entity-key regular expressions over Unicode, source refs, validity windows, the representation limit in code points, `search_text`, identities, `UnicodeEncodeError` |
| `store_put_revise`, `store_transitions`, `store_human_evidence`, `store_jobs_health`, `store_accounts` | construction flags, put/revise, replay, confirmation, transitions and protection, evidence rules, conflicts, history/show/list limits, index jobs, quarantine, health, account isolation |
| `store_links`, `store_eligibility`, `store_file_sources`, `store_work_sources`, `store_memory_parents`, `store_dynamic_values` | relationships, cycles, supersession, unlink, active links, eligibility filters, file hashes, work sources (fixture tables), memory parents, Python dynamic errors |
| `store_forget`, `store_reindex_descendants`, `store_schema`, `store_schema_migration_refusals`, `store_read_only`, `store_nested_transactions`, `store_stale_imports` | erasure and tombstones, descendant requeueing, schema contract and v1 migration, read-only stores beside a writer, savepoints, stale imported preferences |
| `governance_policy`, `governance_policy_preview_impact`, `governance_capture`, `governance_capture_work_source`, `governance_retention`, `governance_usage`, `governance_tombstones`, `governance_export` | every governance function and message |
| `dream_sources` | `source_keys`/`validate_source`, checkpoint roots captured through `_checkpoint_ref`, Dream-derived memories, checkpoint groups, alias tombstones |
| `review_timestamp_types`, `review_file_source_nul`, `review_dream_source_keys`, `review_governance_untyped_arguments` | added by the review (see "Review findings"): timestamps of every JSON type, NUL in file source paths, `source_keys` on unvalidated identities, `authorize_capture`/`retention_authorized` on any value |
| `consolidation_dict_pairs` | added by the state consolidation: `dict(original)` in `_resolve_work_sources` over key/value pairs (unhashable keys, wrong-length pairs, non-sequences after a non-string key) |

Converted Python tests: every case of `tests/test_memory_store.py` and `tests/test_memory_governance.py` is a scenario
or an xUnit test except those that exercise other modules (`test_old_import_approval...` uses memory_learning,
`test_doctor_...` uses margo_doctor, `test_derived_memory_sources...` uses MemorySearch for its index; their store-level
parts are in `store_forget`/`store_reindex_descendants`); the work-source tests use fixture tables instead of the Work
ledger. From `tests/test_memory_review_regressions.py`, the store-level cases (passive capture keeps review flags,
parent updates requeue descendants, representation limit, bare entity names, read-only store beside a writer, the
per-pass source hash cache and the four `_file_digest` stat/fstat races) are scenarios or xUnit tests
(`cloud/tests/Margo.Cloud.State.Tests/Memory/`, which patch the file probes through `MemoryNative.Hooks`). From
`tests/test_memory_encoder.py`: checksums, fingerprint, input validation and status are ported; the pooling, chunking,
subprocess and inference tests belong to the paths not ported (below).

## Deliberate deviations (and why)

1. **Set iteration order.** `_invalidate_derived_indexes` iterates a Python set of descendants, so the order in which
   several descendants are requeued (their `memory_jobs.created_at` and the rowid of re-inserted jobs) follows string
   hashing, which is random per desktop process (`PYTHONHASHSEED`). The port uses sorted order. With one descendant
   both agree (the scenarios use one; `Several_derived_descendants_are_requeued_in_a_fixed_order` pins the C# order).
   Likewise `_migrate_v1` reports the first drifted legacy table in sorted order (Python: hash order); a single drifted
   table matches exactly.
2. **The private embedding interpreter is never executed.** When `~/.copilot/margo/embeddings/venv/bin/python` exists,
   Python's `status_local`/`encode_local` run it. Margo Cloud fails exactly as Python does when it cannot be run
   (`could not inspect the private embedding runtime`, `failed to run the private embedding interpreter`), whatever
   the file is. `_runtime_available()` is always false (the hosted image has no embedding runtime; autopilot
   `parity.NOT_HOSTED_SCRIPTS` lists `memory_encoder.py`). The judge compares the Python side with `tokenizers` absent,
   as on this machine; a Python with numpy, onnxruntime and tokenizers installed would differ by design.
3. **memory_dream's source helpers are copies** (see "Dependencies"): memory_dream sits above this layer, so the
   MemoryCommands port should call them. (`work_ledger.human` is State.Work's, referenced since the state
   consolidation.)
4. **Corrupted stored data.** Rows that only a hand-edited database can contain (non-object `data`, BLOB columns, an
   unknown status) raise `TypeError`/`AttributeError`/`KeyError`-typed errors with texts that can differ from
   Python's. Through the API such rows cannot be produced.
5. **`DerivedDescendants(string)` takes a string**; Python accepts any value (it only ever receives record IDs).
6. **`WorkLedger.Human(..., decision)` takes a string**; Python treats any truthy value as a decision to compare.
7. **Error texts of comparisons on corrupted rows.** `active_links`, `_work_sources_current` and `export_preview` compare stored timestamps as strings; a non-string (only a hand-edited row can hold one, every write validates them) raises `TypeError` naming `'>'` even where Python's failing operator is `<=`.

## Not ported (intentionally)

- `memory_encoder.download_model`, `_download_once`, `_download_asset`, `_asset_url`, `_write_manifest`,
  `_ensure_private_directory`: Margo Cloud never downloads a model and state code never reaches the network.
- `memory_encoder.encode_texts`, `_load_runtime`, `_load_model`, `_chunk_rows`, `_run_chunk_batch`,
  `_encode_with_model`, `_mean_pool_and_normalize`, `_aggregate_chunk_vectors`, `_validate_result`, `_sanitize_stderr`,
  `_read_cli_input`: the ONNX inference and subprocess paths; without a runtime `encode_local` fails before reaching them.
- The `memory_encoder.py` command line (`download`, `encode`, `status`): not hosted (autopilot parity lists the script
  as not hosted), and two of its three subcommands are the paths above. `status()` is judged through the probe.

## Open questions

- `memory_state.py` (MemoryCommands) needs CPython's `**kwargs` binding errors for `put(**input)` and
  `record_usage(**input)`: State.Core's `PyCall` has them, and `PyArg` lets it pass "omitted" versus `None` exactly.
- `_resolve_work_sources` given a reference written as pairs with a key that is not a string raises the `StateError`
  "value must contain finite JSON data" once every pair is read (a `JsonObject` cannot hold the key); Python goes on
  and fails later (`KeyError: 'ref'`, or a dependency `StateError`). Python's callers pass normalised references.
- Python's `_resolve_work_sources` and `_work_sources_current` read the Work ledger's `work_sources` and
  `work_source_revisions` tables when they exist; the scenarios create minimal fixture tables. A cross-component
  scenario with the real Work schema belongs to the integration owner.

## Review findings (independent review of 96f64f7)

A differential review drove both implementations through the probe with generated inputs (more than 30 000 calls:
Unicode text helpers, export markers, `_normalise_data` mutations, lifecycle sequences with revisions and evidence,
file sources, session-checkpoint sources, policies, journals, human evidence timestamps, encoder manifests and every
entry point with arguments of every JSON type). Fixed, each with a scenario that failed before the fix:

1. **Timestamps given as a list or dict** (`valid_from`/`valid_to`/`review_after`, a source reference's
   `verified_at`, relationship validity, a tombstone's `forgotten_at`) raised `TypeError: not a scalar` (from
   State.Core's `ValidateTimestamp`) instead of the timestamp `StateError`; memory_state.py prints either text to the
   model. Now routed through `MemoryStore.ValidateTimestamp` (`review_timestamp_types`); since the state
   consolidation State.Core's `StateStore.ValidateTimestamp` does this itself and the wrapper is gone.
2. **A file source path containing NUL** made eligibility, health and export fail with `ValueError: embedded null
   byte`; `os.stat` says `stat: embedded null character in path` (after encoding, so a lone surrogate still raises
   `UnicodeEncodeError` first). Model input can store such a reference (normalisation does not stat it), and
   memory_state.py prints `ValueError` texts (`review_file_source_nul`).
3. **`DreamSources.SourceKeys`** raised `TypeError` for a non-string `ref` (Python: `parse_json`'s `StateError("invalid
   JSON")`) and `'str'/'dict' object is not subscriptable` for string or dict identities (Python: `IndexError`/
   `TypeError` for strings, `KeyError(slice(None, 5, None))` for dicts). Python's callers only pass validated or stored
   references, so this mattered only to direct callers of the public helper (`review_dream_source_keys`).
4. **`AuthorizeCapture` and `RetentionAuthorized` took `JsonObject`**: the probe could not pass other values, a dict
   missing `domain`/`kind`/`scope` reported "capture policy excludes ..." instead of `KeyError`, and non-list
   `source_refs` crashed with `InvalidCastException`. Both now take `JsonNode?`, read the policy first and index the
   value as Python does (`review_governance_untyped_arguments`). The change is source-compatible for callers.

Confirmed, not changed: the documented set-order deviation (1) shows in the lifecycle fuzz as a different
`pending_jobs` order after a parent revision requeues several descendants in the same pass; all other outputs,
including database rows, matched.

## Verification

- `cd cloud && dotnet build Margo.Cloud.slnx`: 0 warnings.
- `dotnet test --project tests/Margo.Cloud.State.Tests/Margo.Cloud.State.Tests.csproj -- --filter-namespace
  Margo.Cloud.State.Tests.Memory`: 68 tests (store, encoder, governance); the full suite includes `ParityTests`, which
  runs the judge.
- `python3 tools/state_parity.py --check --filter memory/`: 39 scenarios passed (34 + 4 from the review + 1 from the state
  consolidation, `consolidation_dict_pairs.json`, which fails on the build before it).
- Model-dependent behaviour is out of scope: these are state-library contracts, not model evaluations.
