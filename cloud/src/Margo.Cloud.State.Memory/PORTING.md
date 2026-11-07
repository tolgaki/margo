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
`unknown memory: ...`). Where Python's default is not `None`, the parameter is a `PyArg` (omitted -> Python's default,
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
| `_human` | `Human` (`work_ledger.human`, see below) |
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

| Python | C# | Why here |
| --- | --- | --- |
| `work_ledger.human`, `text`, `timestamp`, `now` | `HumanEvidence.Human`, `Text`, `Timestamp` | memory_store calls `human` for every reviewed change; the Work port (`WorkLedger.Human`) is not in this branch. Replace with the Work port's once both are integrated. |
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

### Helpers added here (they belong in State.Core)

| Python behaviour | C# | Note |
| --- | --- | --- |
| `str()`/`repr()` of JSON values, `x in {...}` with unhashable values, `dict(x, **kw)` with pair sequences, `str.isdigit()`, BigInteger-safe copies | `Py` (internal), `PyArg` | The Work port has an equivalent `PyDynamic`/`PyCall`; merge them into State.Core. |
| `AttributeError` | `PyAttributeError` | missing from `PyErrors.cs` (the Work port added one too) |
| `os.link` failure text with both paths | `PyOSErrorTwoPaths` | Core's `PosixNative.Link` names only the source (Core deviation 9) |
| `re`'s `\w`, `\s`, `(?i)` and `str.isdigit()` tables of CPython 3.13 (Unicode 15.1) | `MemoryUnicodeData` (generated by `generate_memory_unicode_data.py`), `PyPatterns` | .NET's `\w` includes combining marks and excludes letter numerals; `(?i)` has CPython's extra equivalents (KELVIN SIGN, LONG S, dotted/dotless I) |
| `os.stat` device/inode/nanosecond times, `os.fstat`, buffered `read` | `MemoryNative`, `PyFileStat` | State.Core's `PyStat` has mode/uid/nlink/size only |

## Contract probe

`cloud/contracts/state/probes/memory_probe.py` and its twin `MemoryProbeScript` (`margo-state memory_probe.py`) run a
JSON program: library calls on a fresh or a named open store (`MemoryStore`, `memory_governance`, `memory_store`
module functions, `memory_dream` source helpers, `memory_encoder`), held transactions (`begin`/`commit`/`rollback` on
an open store), SQL and scripts against the account database (`@SCHEMA_V1`/`@SCHEMA` name the schema text), module
constants, and file fixtures (text, bytes, JSON, repeated text, modes, symlinks). Arguments are templated from the
clock (`${now+60m}`) and earlier results (`${row.id}`). Results print as JSON, errors as `!Type: message` (the random
`.memory-export-XXXXXXXX` staging name is masked). It is not a Margo command and the hosted tool cannot run it.

## Scenarios (`cloud/contracts/state/memory/`, 34 files, about 1500 probe steps)

| Files | Cover |
| --- | --- |
| `encoder_constants`, `encoder_status`, `encoder_private_runtime`, `encoder_model_dir_environment` | every encoder constant, identity/fingerprint, digests, `_validate_texts`, `status`/`status_local`/`encode_local` without a runtime (unsafe default directory, explicit and environment directories, missing/non-private/symlinked/invalid/expected manifests, asset checks, missing and existing private interpreter) |
| `store_normalisation`, `store_identity_text`, `store_surrogates` | every `_normalise_data` message, entity-key regular expressions over Unicode, source refs, validity windows, the representation limit in code points, `search_text`, identities, `UnicodeEncodeError` |
| `store_put_revise`, `store_transitions`, `store_human_evidence`, `store_jobs_health`, `store_accounts` | construction flags, put/revise, replay, confirmation, transitions and protection, evidence rules, conflicts, history/show/list limits, index jobs, quarantine, health, account isolation |
| `store_links`, `store_eligibility`, `store_file_sources`, `store_work_sources`, `store_memory_parents`, `store_dynamic_values` | relationships, cycles, supersession, unlink, active links, eligibility filters, file hashes, work sources (fixture tables), memory parents, Python dynamic errors |
| `store_forget`, `store_reindex_descendants`, `store_schema`, `store_schema_migration_refusals`, `store_read_only`, `store_nested_transactions`, `store_stale_imports` | erasure and tombstones, descendant requeueing, schema contract and v1 migration, read-only stores beside a writer, savepoints, stale imported preferences |
| `governance_policy`, `governance_policy_preview_impact`, `governance_capture`, `governance_capture_work_source`, `governance_retention`, `governance_usage`, `governance_tombstones`, `governance_export` | every governance function and message |
| `dream_sources` | `source_keys`/`validate_source`, checkpoint roots captured through `_checkpoint_ref`, Dream-derived memories, checkpoint groups, alias tombstones |

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
3. **`work_ledger.human` and memory_dream's source helpers are copies** (see "Dependencies"), not references to the
   Work/MemoryCommands assemblies, which are separate components not in this branch.
4. **Corrupted stored data.** Rows that only a hand-edited database can contain (non-object `data`, BLOB columns, an
   unknown status) raise `TypeError`/`AttributeError`/`KeyError`-typed errors with texts that can differ from
   Python's. Through the API such rows cannot be produced.
5. **`DerivedDescendants(string)` takes a string**; Python accepts any value (it only ever receives record IDs).
6. **`HumanEvidence.Human(..., decision)` takes a string**; Python treats any truthy value as a decision to compare.

## Not ported (intentionally)

- `memory_encoder.download_model`, `_download_once`, `_download_asset`, `_asset_url`, `_write_manifest`,
  `_ensure_private_directory`: Margo Cloud never downloads a model and state code never reaches the network.
- `memory_encoder.encode_texts`, `_load_runtime`, `_load_model`, `_chunk_rows`, `_run_chunk_batch`,
  `_encode_with_model`, `_mean_pool_and_normalize`, `_aggregate_chunk_vectors`, `_validate_result`, `_sanitize_stderr`,
  `_read_cli_input`: the ONNX inference and subprocess paths; without a runtime `encode_local` fails before reaching them.
- The `memory_encoder.py` command line (`download`, `encode`, `status`): not hosted (autopilot parity lists the script
  as not hosted), and two of its three subcommands are the paths above. `status()` is judged through the probe.

## Open questions

- `cloud/contracts/state/README.md`'s layout table should list `memory/` and `probes/memory_probe.py`; it is a shared
  file outside this component, so the integration owner should add the rows.
- Merge `Py`/`PyArg` with the Work port's `PyDynamic`/`PyCall`, and `HumanEvidence` with `WorkLedger.Human`, when the
  ports are integrated (State.Core is the natural home for the dynamic helpers and the Unicode tables).
- `memory_state.py` (MemoryCommands) needs CPython's `**kwargs` binding errors for `put(**input)` and
  `record_usage(**input)`; `PyArg` lets it pass "omitted" versus `None` exactly.
- Python's `_resolve_work_sources` and `_work_sources_current` read the Work ledger's `work_sources` and
  `work_source_revisions` tables when they exist; the scenarios create minimal fixture tables. A cross-component
  scenario with the real Work schema belongs to the integration owner.

## Verification

- `cd cloud && dotnet build Margo.Cloud.slnx`: 0 warnings.
- `dotnet test --project tests/Margo.Cloud.State.Tests/Margo.Cloud.State.Tests.csproj -- --filter-namespace
  Margo.Cloud.State.Tests.Memory`: 68 tests (store, encoder, governance); the full suite includes `ParityTests`, which
  runs the judge.
- `python3 tools/state_parity.py --check --filter memory/`: 34 scenarios passed.
- Model-dependent behaviour is out of scope: these are state-library contracts, not model evaluations.
