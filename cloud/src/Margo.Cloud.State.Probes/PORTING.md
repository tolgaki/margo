# Margo.Cloud.State.Probes: porting notes

The C# twins of the contract probes in `cloud/contracts/state/probes/`. A probe is not a Margo command: it lets
`tools/state_parity.py` judge shared helpers and libraries byte for byte against CPython 3.13, not only through
whichever scripts call them. The probes run arbitrary SQL against the account database and write files under
any directory they are given, so they must never ship.

| Python reference | C# twin | Reaches |
| --- | --- | --- |
| `state_core_probe.py` | `StateCoreProbeScript` | `json`, `margo_store.parse_json`/`canonical_json`/`validate_timestamp`, `fromisoformat`, a demo argparse parser, ids, `connect` and nested transactions (State.Core) |
| `work_ledger_probe.py` | `WorkLedgerProbeScript` | `work_state.main(argv)` in process with templated input, the ledger library, SQL and fixture files (State.Work) |
| `proactive_probe.py` | `ProactiveProbeScript` | `proactive_state.main()` under a fixed clock, library calls on one connection, SQL, legacy files (State.Proactive) |
| `memory_probe.py` | `MemoryProbeScript` | `MemoryStore`, `memory_governance`, Dream source helpers, `memory_encoder` status, held transactions, SQL, files (State.Memory) |
| `helpers_probe.py` | `HelpersProbeScript` | the stateless helpers' libraries and the Python semantics layer; input-file writers (State.Helpers) |
| `memory_search_probe.py` | `MemorySearchProbeScript` | `MemorySearch`, `ConnectedContext` and the `memory_search`/`memory_context` module functions with a deterministic fake encoder, held stores, store calls and SQL (State.MemorySearch) |

Each project's own PORTING.md documents what its probe covers and which scenarios use it.

## How probes are kept out of production

1. **Separate assembly.** Every `*ProbeScript` class lives here (namespace `Margo.Cloud.State.Probes`) and
   implements `IStateProbeScript`, State.Core's marker interface. No production project references this one;
   State.Work and State.Memory grant it `InternalsVisibleTo` for the internal entry points the Python probes reach
   (`WorkStateScript.Main` for `work_state.main(argv)`, `Ledger.SourceCore` for `source(..., sensitivity=None)`,
   `MemoryStore.Item`).
2. **Build switch.** `Margo.Cloud.StateCli` references this project only when the MSBuild property
   `MargoStateProbes` is `true`. The Debug configuration sets it by default; Release does not (pass
   `-p:MargoStateProbes=true` to override, never for a deployed image). `tools/state_parity.py` uses the Debug
   build. `ProbeIsolationTests.Release_build_of_margo_state_contains_no_probes` builds StateCli in Release and checks
   the output by metadata (no probe assembly, no type implementing `IStateProbeScript` or named `*ProbeScript`) and
   by running it.
3. **Runtime switch.** `margo-state` runs a probe only when the environment sets `MARGO_STATE_PROBES=1`; otherwise
   it prints `margo-state: error: 'NAME_probe.py' is a contract probe, not a Margo command (set
   MARGO_STATE_PROBES=1 ...)` after the usage line and exits 2, like an unknown script, and neither `--help` nor the
   unknown-script list names a probe. The judge sets the variable for every step (Python ignores it). The hosted
   tool never sets it and, independently, runs only scripts classified in `statecmd.COMMANDS`.

The test project references this project directly, so xUnit tests can drive the probes in any configuration.

## Not ported / deviations

Nothing beyond what each probe's owning port documents. The probes were moved here unchanged except for their
namespace and marker interface (state consolidation).

## Verification

- `python3 tools/state_parity.py --check` (every probe scenario) and the `ProbeIsolationTests` in
  `cloud/tests/Margo.Cloud.State.Tests`.
