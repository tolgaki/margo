# Margo Cloud (C#)

Hosted Margo, in C# on .NET 10. Plan and phases: [docs/margo-cloud-plan.md](../docs/margo-cloud-plan.md).
Margo has two implementations that share the persona (`agents/`) and skills (`skills/`):

- **Margo desktop**: GitHub Copilot with Margo's persona, skills and the Python state helpers in
  `skills/chief-of-staff/scripts/`. Maintenance mode.
- **Margo Cloud**: this folder. Self-hosted for Agent 365, one container per hired Margo.

## Build and test

```sh
cd cloud
dotnet build Margo.Cloud.slnx
dotnet test --solution Margo.Cloud.slnx
python3 ../tools/state_parity.py --check      # C# state commands vs the Python helpers (Debug build)
```

## Layout

| Project | Ports | Notes |
| --- | --- | --- |
| `Margo.Cloud.State.Core` | `margo_store.py` | Paths, private files, SQLite connections, transactions, Python-compatible JSON, time and argument parsing, and the Python value semantics every state port shares (`PyNode`, `PyObj`, `PyNumbers`, `PySort`, `PyText`, `PyCall`) |
| `Margo.Cloud.State.Work` | `work_ledger.py`, `work_productivity.py`, `work_state.py` | Ledger, action desk, approvals |
| `Margo.Cloud.State.Proactive` | `proactive_state.py` | |
| `Margo.Cloud.State.Tasks` | `task_runs.py`, `task_state.py` | |
| `Margo.Cloud.State.Memory` | `memory_store.py`, `memory_encoder.py` (status only), `memory_governance.py` | |
| `Margo.Cloud.State.MemorySearch` | `memory_search.py`, `memory_context.py` | |
| `Margo.Cloud.State.MemoryLearning` | `memory_learning.py`, `memory_consolidation.py` | |
| `Margo.Cloud.State.MemoryCommands` | `memory_dream.py`, `memory_state.py` | Python `zoneinfo` semantics over the system TZif files (`PyZoneInfo`) for the Dream window |
| `Margo.Cloud.State.Helpers` | `capacity.py`, `teams_feedback.py`, `engage_parse.py` | Stateless |
| `Margo.Cloud.State.Probes` | `cloud/contracts/state/probes/*_probe.py` | Contract probes for the parity judge only: in `margo-state` only when built with `MargoStateProbes=true` (Debug default, never Release) and run only with `MARGO_STATE_PROBES=1` |
| `Margo.Cloud.StateCli` (`margo-state`) | the script entry points | `margo-state SCRIPT.py --account A --state-dir D ARGS...` |
| `Margo.Cloud.Core` | `instances.py`, `activity.py`, `instance.py` | Names, route table, lifecycle, activity helpers, instance binding |
| `Margo.Cloud.WorkIQ` | `transport.py` | Work IQ MCP client |
| `Margo.Cloud.Guard` | `guard.py`, `claims.py` | The only path from the model to Work IQ |
| `Margo.Cloud.Engine` | `engine.py`, `skills.py`, `parity.py`, `statecmd.py` | Model turn loop and the `margo-state` tool |
| `Margo.Cloud.Orchestrator` | `orchestrator.py`, `office.py`, `replies.py`, `statesync.py` | |
| `Margo.Cloud.Voice` | `voice/session.py`, `voice/server.py`, `voice/static/` | |
| `Margo.Cloud.Host` | `host/app.py`, `host/azure_store.py` | ASP.NET Core + Microsoft 365 Agents SDK |
| `Margo.Cloud.Router` | `router/` | Public messaging endpoint |
| `Margo.Cloud.Provisioner` | `provision/` | Container Apps job |

Python sources named above live in `autopilot/src/margo_autopilot/` (hosted runtime) and
`skills/chief-of-staff/scripts/` (state helpers).

## Porting conventions

These hold for every project. They exist so independent ports fit together.

1. **The Python code is the specification.** Port behaviour, including error cases, limits, exit
   codes and messages. Do not add features or "fix" behaviour silently; if the Python looks wrong,
   keep its behaviour and note it in the project's `PORTING.md`.
2. **State commands must be indistinguishable from the Python helpers.** Same subcommands and
   options, same exit codes, same stdout bytes (JSON key order, spacing, `ensure_ascii` escaping,
   number formatting, text views), same SQLite schema text, pragmas and rows. Use the shared
   helpers in `Margo.Cloud.State.Core` (`PyJson`, `PyTime`, `PyArgs`, `StateStore`, and `PyNode`/`PyObj`
   for Python's dynamic semantics) rather than re-implementing them; if one is missing, add it there
   instead of a private copy. Copy Python values with `PyNode.Copy`, never `JsonNode.DeepClone` (a test
   enforces it). `tools/state_parity.py` is the judge: every subcommand needs scenarios in
   `cloud/contracts/state/`, including failure paths; contract probes go in `Margo.Cloud.State.Probes`.
3. **Nothing reaches the network from state code**, as in Python.
4. **No secrets, tokens, message bodies or real workplace data** in code, tests, fixtures or logs.
   Fictional data and `example.com` only.
5. **C# style:** file-scoped namespaces, `sealed` by default, records for data, `System.Text.Json`
   (`JsonNode` for open-ended JSON), no `dynamic`, no reflection-based magic beyond the state
   script registry, async all the way in services, `CancellationToken` on I/O.
6. **Each project owns its files.** Do not edit another component's sources; add what you need
   behind an interface in your own project, or note the request in your `PORTING.md`.
7. **Tests:** xUnit v3 under `cloud/tests/`, using fakes for Work IQ, the model, Blob storage and
   Voice Live. Port the scenarios of the matching Python tests (`tests/test_autopilot_*.py`,
   `tests/test_*state*.py`, ...), not just the happy path.
