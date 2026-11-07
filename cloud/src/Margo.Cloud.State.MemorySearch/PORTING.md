# Margo.Cloud.State.MemorySearch: porting notes

Ports `skills/chief-of-staff/scripts/memory_search.py` and `memory_context.py` completely. Both are libraries: the
`memory_state.py` commands that call them (`init`, `migrate`, `status`, `search`, `context`, `graph`, `explain`,
`index`) belong to the MemoryCommands port, which uses the public API below. Reference behaviour is CPython 3.13 with
its system SQLite (3.45.1 here); `tools/state_parity.py` judges the libraries through the `memory_search_probe.py`
probe pair. Margo Cloud never has a local embedding runtime, so the default encoder is State.Memory's
`MemoryEncoder.EncodeLocal`, which fails exactly as Python's does without a runtime; every vector path is still
ported and judged with the probe's deterministic fake encoder.

## What maps to what

Values are Python values as `JsonNode` (State.Core's PyJson value model). Parameters with a non-`None` Python default
are State.Core `PyArg`s (omitted is Python's default, `PyArg.Of(null)` an explicit `None`), so `search(limit=True)` or
`context(budget_chars=12000.0)` fail as in Python. Results are `JsonObject`s with Python's key order (context and graph
packets are `json.loads(canonical_json(...))`, so their keys are sorted, as in Python).

### memory_search.py (`MemorySearch`, namespace `Margo.Cloud.State.MemorySearch`)

| Python | C# |
| --- | --- |
| `representation`, `text_hash`, `vector_bytes`, `decoded_vector` | static `MemorySearch.Representation`, `TextHash`, `VectorBytes(vector, dimensions)` (bytes), `DecodedVector(blob, dimensions)` (doubles); `dimensions` is a `long` for the callers, or any Python value (`JsonNode`) with Python's equality, repetition and `struct` behaviour (`2.0`/`True` match, `None`/dict are a `TypeError`, a non-integral float can raise `struct.error`, `MemorySearch.StructError`) |
| `MemorySearch(memory, encoder=None, rebuild_schema=False)` | `new MemorySearch(MemoryStore, encoder, rebuildSchema)` or `new MemorySearch(IMemorySource, ...)`; `encoder` is `Func<JsonArray, JsonNode?>` (texts to the result dict) |
| `memory`, `conn`, `encoder` (assignable), `read_only`, `initialized`, `fts` | `Memory`, `Conn`, `Encoder` (settable), `ReadOnly`, `Initialized`, `Fts` |
| `_prepare_rebuild`, `_semantic_tables`, `_inspect_schema(allow_missing_fts)`, `_initialize_schema` | private methods of the same meaning |
| `encode(texts)` | `Encode(JsonArray)` |
| `_purge`, `purge_unavailable()` | private `Purge`; `PurgeUnavailable()` (long) |
| `_delete_jobs`, `_quarantine_oversized` | private `DeleteJobs`, `QuarantineOversized` |
| `index(limit=100, rebuild=False)` | `Index(PyArg limit, JsonNode? rebuild)` (`rebuild` by truthiness) |
| `health()` | `Health()` |
| `search(query, limit=8, domain, routine, usage="reasoning", entities, environment, mode="hybrid")` | `Search(query, limit, domain, routine, usage, entities, environment, mode)` |
| `context(query, routine, usage, entities, environment, domain, budget_chars=12000, mode, max_depth=2, max_nodes=30, work_ids)` | `Context(...)` in the same order |
| `graph(memory_id, max_depth, max_nodes, routine, usage, environment, domain, budget_chars)`, `explain(memory_id, **kwargs)` | `Graph(...)`, `Explain(...)` (same parameters as `Graph`) |

### memory_context.py (`MemoryContext` static class, `ConnectedContext`)

| Python | C# |
| --- | --- |
| `SECTIONS`, `RELATIONS` | `MemoryContext.Sections`, `Relations` |
| `identity_key`, `entity_keys`, `applicable`, `active_edges`, `bounds` | `IdentityKey`, `EntityKeys` (a set), `Applicable`, `ActiveEdges`, `Bounds` (returns the validated pair) |
| `read_snapshot(conn)` (context manager) | `ReadSnapshot(conn, Func<T>)` |
| `memory_entry`, `mandatory_constraint`, `section`, `sized`, `budget_packet` | `MemoryEntry` (`mandatory` is a `PyArg` stored as given), `MandatoryConstraint` (bool; `MandatoryConstraintValue` is the value of Python's `and`/`or` chain), `Section`, `Sized`, `BudgetPacket` |
| `ConnectedContext(memory, **filters)` | `new ConnectedContext(memory, domain, routine, usage, environment)` |
| `current`, `gaps`, `edges`, `conflicted_ids`, `truncated`, `tables` | `Current` (ordered), `Gaps` (the list object the build result shares), `Edges`, `ConflictedIds`, `Truncated`; tables are private |
| `gap`, `constraint_guard`, `_diagnose`, `_successors`, `_work`, `build` | `Gap`, `ConstraintGuard`, `Diagnose`, `Successors`, `Work`, `Build`; `_constraint_scope`, `_sources`, `_work_entry`, `_work_ids`, `_related_work`, `_decision` are private |

### The memory seam

Python duck-types `memory` (`tests/test_memory_search.py` passes a fake store; `active_edges` uses
`getattr(memory, "active_links", memory.links)`). `IMemorySource` is that seam: `Account`, `Conn`, `ReadOnly`, `List`,
`Eligible` (usage passed explicitly), `Show`, `Links`, `ActiveLinks` (a source without temporal links returns `Links`),
`DreamIndexCurrent`, `WorkSourcesCurrent`, `PendingJobs`, `CompleteJob`, `BlockJob`, `BlockedJobs`.
`MemoryStoreSource` adapts State.Memory's `MemoryStore`.

API the MemoryCommands port needs (`memory_state.py`): `new MemorySearch(store, rebuildSchema: ...)`, `Health`,
`Search`, `Context`, `Graph`, `Explain`, `Index`, and `MemoryContext.ReadSnapshot` for `inspect`/`list`.

### Belongs in State.Core

- `MemorySearch.PySum` (internal): CPython 3.13's `sum()` over mixed ints and floats (`builtin_sum_impl`: a C-long
  fast path for ints, then the float path with Neumaier compensation where ints that fit a C long are added to the
  running sum without compensation, then plain addition). State.Core's `PyNumbers.Sum` covers floats only and
  `vector_bytes` sums `value * value` over JSON ints and floats. The algorithm was confirmed against CPython 3.13's
  `sum()` with 300 000 random mixed lists (through a Python model of it that the C# code mirrors); the C# version is
  pinned by `MemorySearchTests.Python_sum_matches_cpython_for_mixed_ints_and_floats` and judged through
  `vector_bytes` in `module_functions` and the differential fuzz.
  Request: move it to `PyNumbers` (as `Sum(IEnumerable<object>)`) in a State.Core change.
- `MemorySearch.StructError`: Python's `struct.error` (`PythonType` `error`), raised only by `DecodedVector` with a
  non-integral float dimension. Request: add it to State.Core's exception taxonomy (`PyErrors.cs`).

## Contract probe

`cloud/contracts/state/probes/memory_search_probe.py` and its twin `MemorySearchProbeScript` (in
`Margo.Cloud.State.Probes`, which now references this project) run a JSON program: `MemorySearch` methods on a fresh
or a held store (open/close/begin/rollback; rollback reports `in_transaction`), `ConnectedContext` methods with the
context's current IDs, gaps, edges, conflicted IDs and truncation printed after each call, `memory_search`/
`memory_context` module functions, a few `MemoryStore` calls (put, revise, link, unlink, forget, show, list,
eligible, jobs, health) and SQL against the account database. The fake encoder is deterministic (per text, group
word counts times weights, or a default vector) and can return fixed values, raise `StateError`, log its batches and
run store-changing steps on its first call (forgetting during an embedding request). Templates add `id:DOMAIN:KEY`
(the record ID) and `json:EXPR` (parse a saved string). Binding errors use CPython 3.13's texts (`PyCall.Bind`).

## Scenarios (`cloud/contracts/state/memory-search/`, 20 files)

| Files | Cover |
| --- | --- |
| `module_functions` | every module function on literal values: 40 vectors (ints, floats, big ints, mixed sums, overflow, zero, bools, strings, dicts, non-sized values), decoding, identity and entity keys, validity windows, bounds, entries, mandatory values, sections, `sized`, budgets dropping entries/edges/gaps and blocking |
| `schema_states` | construction over missing, incomplete, incompatible, unsupported, fts5-mismatched, FTS-less (bounded lexical), wrong-column, unknown-object, view and LIKE-wildcard schemas; FTS repair from documents; read-only refusals; a rebuild inside a caller's transaction |
| `index_lifecycle`, `index_quarantine` | first and repeated indexing (vectors, documents, FTS rows), limits, the absent runtime, transient and every malformed encoder result, forgetting while encoding, model fingerprints, stale revisions, replayed/duplicate keyword rows, corrupt vectors, orphan and NULL-ID purges, drifted job revisions; the representation limit boundary, quarantine not consuming the page, blocks persisting through rebuilds and recovering on a new revision, sensitive deletes first in rebuilds, delete-only rebuilds without an encoder |
| `fts_queries` | FTS5 query results (see below) |
| `search_validation` | argument validation order and binding errors, eligibility filters, hybrid/semantic searches failing without the runtime or skipping it when nothing is eligible |
| `semantic_ranking` | weighted float vectors, cosine similarity, positive similarities only, fusion scores and component scores of all four channels, exact identities, canonicalised entities, relationship neighbours through applicable links only, model/dimension mismatches, malformed query embeddings, forgetting during the request (also of a relationship seed) |
| `context_work_live`, `context_work_fixtures` | live work joins through the real ledger (work_refs, work_ids, outcomes, meetings, resolution, other accounts, changed and restricted sources) and every work-entry branch over hand-built ledger rows (states, unknown fields, recap, unsupported kinds, relationship directions, superseded items, edge/node limits, the 200-row discovery limit, malformed meeting data) |
| `context_decisions`, `context_diagnostic_limit` | supersession chains, decision references, retired decisions, unconfirmed/contradicting/duplicate/disputed decisions, the 50-gap cap |
| `context_identities_relationships`, `context_memory_edges` | identities, relationship validity and evidence, environment filters, coverage evidence, shared-source depth; supersession limits and evidence, diagnostics for every ineligible kind, entity seeds, edge limits, nested environment requirements |
| `review_regressions` | the independent review's failing-first cases: `section` of a mandatory entry without (or with an unhashable) kind, `memory_entry` keeping a non-bool `mandatory`, `vector_bytes`/`decoded_vector` with float, bool, string, list, `None` and dict dimensions (including `struct.error`), and a meeting's unhashable `recap_retry` status raising `TypeError` |
| `context_bounds_budget`, `context_mandatory_constraints`, `context_mandatory_sources`, `context_mandatory_replacements`, `context_imported_preferences`, `context_read_guarantees` | bounds and budgets; the constraint guard in every state (stale, disputed, overdue, changed or restricted source, conflict links and flags, sensitive, expiry, routines, suppression, replacements, scoped successors, small budgets, the imported About-me profile and its file-change recovery action); caller transactions, no usage writes, forgetting, re-reading authority after inference |

### FTS: e_sqlite3 versus the system SQLite

The judge skips FTS shadow-table rows (their encoding belongs to the SQLite build), so FTS is proven through query
results: `fts_queries` indexes a 20-record corpus (ASCII case, diacritics and combining accents, sharp s, dotted and
dotless i, CJK, emoji, underscores and hyphens, apostrophes, digits and versions, Greek final sigma, fullwidth
letters, FTS operator words and punctuation, zero-width characters) plus two records only the lexical fallback sees,
and runs about 100 queries (casefolding and `\w` extraction, stopwords, deduplication, the 24-word cap, quoted phrase
escaping so `NEAR`, `OR`, `*`, `^`, `col:` are never operators, bm25 values, ties broken by ID, limits). Every result,
including each bm25 float, matches between SQLitePCLRaw's bundled e_sqlite3 and CPython's SQLite 3.45.1.

### Differential fuzz (not committed)

340 random programs (random Unicode corpora, entities, preferences, links, forgetting, weighted fake encoders with
negative and fractional weights, lexical/hybrid/semantic searches, contexts and graphs with random bounds and
budgets, `vector_bytes` on random ints, floats and big ints) were run through both probes: all outputs identical.

Converted Python tests: every case of `tests/test_memory_search.py` is an xUnit test over the same fake store
(`cloud/tests/Margo.Cloud.State.Tests/MemorySearch/`, 28 cases plus 6 C# seam tests); every case of
`tests/test_memory_context.py` is a parity scenario over the real store and ledger (the "fresh process" case is
implicit: every probe step is a fresh process). `tests/test_semantic_e2e.py` needs the real model and runtime: its
non-model parts (index, restart, forget, purge, account isolation) are covered by the index and read-guarantee
scenarios with the fake encoder; the model ranking itself is not portable.

## Deliberate deviations (and why)

1. **Identities are strings.** Python treats any hashable as a node identity; the C# build keys nodes, seeds and
   work references by string. Every identity the callers produce is a string (memory IDs, validated `work_ids`,
   ledger-validated `work_item_ids`/`outcome_id`, `work_refs` filtered to strings); a hand-edited ledger row with a
   non-string `work_item_ids` entry raises `TypeError` in C# where Python would report it as unavailable.
2. **`dict(row)` of a work row with a BLOB column** raises `TypeError` (State.Core `PyNode.FromRow`); Python keeps the
   bytes. Only a hand-edited ledger row can hold one.
3. **`ConnectedContext` filters** are the four the callers pass (`domain`, `routine`, `usage`, `environment`);
   Python's `**filters` also forwards `include_stale` to `eligible`, which no caller uses.
4. **`explain(memory_id, **kwargs)`** has `Graph`'s parameters (C# has no `**kwargs`); the probe reproduces Python's
   binding errors for both.
5. **`IMemorySource.ActiveLinks` is required**; a source without temporal links implements it as `Links` (Python's
   `getattr` fallback).
6. **A second `ConnectedContext.Build` on the same instance** returns the live gaps list in the new result and leaves
   the earlier result (or wherever the caller moved its `gaps`) a copy of the gaps as they stood: a JSON node has one
   parent, while Python's results all share one list. `MemorySearch` builds once per context, so only direct callers
   can see it (`MemorySearchTests.Connected_context_builds_more_than_once_like_python`).
7. **`Encode(texts)` takes a list** (`JsonArray`, as `index`/`search` pass); Python's `encode` accepts any sized
   iterable (a string's characters, a dict's keys). The probe twin therefore cannot run `encode` on a non-list (it
   stops with an unhandled cast); no caller passes one. Likewise the probe's `memory_entry` needs a list of reasons and
   `build` needs string IDs (deviation 1).

## Not ported (and why)

- The `no such module: fts5` fallbacks in `_initialize_schema` are ported but cannot be reached: both SQLite builds
  include FTS5, so no scenario exercises them (the bounded-lexical backend itself is covered through `fts5=0`
  metadata).
- Model-dependent behaviour (real embeddings and their ranking quality) is out of scope: Margo Cloud has no runtime.

## Open questions

- `PySum` belongs in State.Core (see above).
- The FTS proof is per SQLite build: if SQLitePCLRaw's bundled SQLite or the system SQLite changes its unicode61
  tables or bm25, `fts_queries` will show it; re-run it after upgrading either.

## Independent review (second agent)

Re-read both Python modules against the port line by line, then ran differential programs through both probes:
560 random store programs (records of every kind with entities, routines, uses, canonical decisions, conflicts,
expiry, sensitivity and work refs; random links; weighted encoders; index/rebuild with page limits around the 16-text
batch; hybrid/lexical/semantic searches, contexts with small budgets, graphs and explains), 300 FTS programs (random
code points from 25 scripts and blocks in corpora and queries, prefixes, case changes, FTS operator tokens), 300
`vector_bytes`/`decoded_vector` programs (C-long boundaries, big ints, subnormals, overflow), plus targeted programs
for hand-built ledger rows with malformed data, Dream-sourced records (`dream_index_current` false: snapshot
metadata and `memory_record` sources whose parent changed), argument binding and validation. All matched except the
defects fixed below (scenario `review_regressions` and the xUnit test failed before the fixes):

- `section(entry)` read `entry["kind"]` before `entry.get("mandatory")`: a mandatory entry without a kind raised
  `KeyError` where Python returns `constraints` (`MemoryContext.Section`).
- A meeting's `recap_retry.status` that is a list or dict was ignored; Python's set membership raises
  `TypeError: unhashable type` (`ConnectedContext.WorkEntry`).
- `memory_entry(..., mandatory=X)` stored `bool(X)`; Python stores `X`.
- `vector_bytes`/`decoded_vector` only accepted an `int` dimension (the probe raised `TypeError: not an int`); Python
  compares with `!=`/`*` and formats with `%d`.
- A second `ConnectedContext.Build` threw `InvalidOperationException` (the gaps list already had a parent); see
  deviation 6.

## Verification

- `cd cloud && dotnet build Margo.Cloud.slnx`: 0 warnings.
- `dotnet test --project tests/Margo.Cloud.State.Tests/Margo.Cloud.State.Tests.csproj -- --filter-namespace
  Margo.Cloud.State.Tests.MemorySearch`: 35 tests.
- `python3 tools/state_parity.py --check --filter memory-search/`: 20 scenarios.
