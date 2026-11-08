# Margo Cloud (C#)

Hosted Margo, in C# on .NET 10: self-hosted for Agent 365 in Azure Container Apps, one container per hired Margo.
Plan and phases: [docs/margo-cloud-plan.md](../docs/margo-cloud-plan.md). Design and decisions:
[Margo as an autopilot](../docs/autopilot.md). Deployment: [Deploy Margo as an Agent 365 agent](../docs/autopilot-deployment.md).

**Status: built and tested offline, never run in a live tenant.** The first live run is the Phase 0 checklist in
[Margo.Cloud.Host/VERIFY.md](src/Margo.Cloud.Host/VERIFY.md), which an operator runs in a test tenant. Margo Cloud is not
installed by the local installers.

Margo has two implementations that share the persona (`agents/`) and the skills (`skills/`):

- **Margo desktop**: GitHub Copilot with Margo's persona, skills and the Python state helpers in
  `skills/chief-of-staff/scripts/`. Maintenance mode.
- **Margo Cloud**: this folder. It replaced the Python hosted runtime in the Phase 6 cutover. Its state commands
  (`margo-state`) must stay byte-compatible with the desktop helpers; `tools/state_parity.py` is the judge.

## Build, test, publish

Needs the .NET 10 SDK and, for the tests and the parity judge, `python3` (the desktop helpers; the judge pins
CPython 3.13 behaviour).

```sh
cd cloud
dotnet build Margo.Cloud.slnx                 # warnings are errors
dotnet test --solution Margo.Cloud.slnx
python3 ../tools/state_parity.py --check      # margo-state vs the desktop Python helpers (Debug build, with probes)
node --test src/Margo.Cloud.Voice/wwwroot/*.test.mjs   # the voice page's helpers

dotnet publish src/Margo.Cloud.Host/Margo.Cloud.Host.csproj -c Release -o out/host       # margo-state comes with it
dotnet publish src/Margo.Cloud.Router/Margo.Cloud.Router.csproj -c Release -o out/router
dotnet publish src/Margo.Cloud.Provisioner/Margo.Cloud.Provisioner.csproj -c Release -o out/provisioner
```

A Release build never contains the contract probes (`Margo.Cloud.State.Probes`); the host's build fails if one would
reach its output, and the image build refuses any.

## Image and deployment

One image, [`Dockerfile`](Dockerfile) (build context: the repository root), serves all three roles; the container
command picks one:

| Role | Command | Where it is set |
| --- | --- | --- |
| Instance (one per hired Margo; default) | `dotnet /app/host/Margo.Cloud.Host.dll` | `infra/instance.bicep` / `instance.json` |
| Router (public messaging endpoint) | `dotnet /app/router/Margo.Cloud.Router.dll` | `infra/main.bicep` |
| Provisioner job | `dotnet /app/provisioner/Margo.Cloud.Provisioner.dll run` | `infra/main.bicep` |

The runtime image is ASP.NET Core 10 on Alpine plus `tzdata` (the IANA zones the persona's clock, the Dream window
and quiet hours need), running as the non-root user the .NET images provide; `agents/` and `skills/` sit under `/app`
(`MARGO_REPO_ROOT`), without any `state/`. Deploy with `azd` from this folder; the steps are in
[docs/autopilot-deployment.md](../docs/autopilot-deployment.md).

| Path | Purpose |
| --- | --- |
| `Dockerfile` | The image (multi-stage: .NET 10 SDK build, ASP.NET Core runtime) |
| `azure.yaml`, `infra/main.bicep`, `infra/main.parameters.json` | `azd` project: model, registry, storage, router, provisioner |
| `infra/instance.bicep`, `infra/instance.json` | One instance's identity, scoped storage role and Container App (rebuild the JSON with `bicep build cloud/infra/instance.bicep --outfile cloud/infra/instance.json`) |
| `scripts/` | `azd` hooks and helpers: `preprovision.sh`, `postprovision.sh` (roles, image, blueprint connection), `build-image.sh`, `connect-blueprint.sh`, `blueprint.sh` |
| `config/mcp.json`, `terms.md`, `assets/` | MCP config, terms and privacy, icons |

## Layout

| Project | Ported from | Notes |
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
| `Margo.Cloud.Voice` | `voice/session.py`, `voice/server.py` | The browser page is in `wwwroot/` |
| `Margo.Cloud.Host` | `host/app.py`, `host/azure_store.py` | ASP.NET Core + Microsoft 365 Agents SDK |
| `Margo.Cloud.Router` | `router/` | Public messaging endpoint |
| `Margo.Cloud.Provisioner` | `provision/` | Container Apps job |

The state helpers named above live in `skills/chief-of-staff/scripts/` and remain the specification. The hosted
runtime modules (`engine.py`, `guard.py`, ...) were removed in the cutover; each project's `PORTING.md` records the
mapping, the deliberate deviations and, under **Cutover**, how its tests keep the Python behaviour they were checked
against (recorded answers and frozen fixtures).

## The hosted action convention

Approved writes executed by Margo Cloud carry the exact Work IQ call in the action snapshot, so the human approves the
call itself rather than a description of it:

```json
{
  "kind": "mail.send",
  "target": {"message_id": "draft-1", "on_data_of": "principal"},
  "payload": {"workiq": {"tool": "do_action", "arguments": {"actionUrl": "/me/messages/draft-1/send"}}},
  "why": "...", "source_refs": ["..."], "target_fingerprint": "...", "work_item_id": "..."
}
```

The guard refuses a write unless the run is attended, the claim is live for the current revision, and the call matches
`payload.workiq` exactly. It records a `workiq_dispatched` event before sending, so an attempt is never sent twice,
even after a crash; such an attempt needs reconciliation. Actions in the older descriptive shape cannot execute
through the guard. The guard's argument checks follow the live Work IQ tool schemas where they differ from the
published reference: `search_paths` takes `query`, and `get_schema` accepts `action` and `function` operation types.

## Conventions

These hold for every project.

1. **Behaviour is specified, not invented.** The state commands are specified by the desktop helpers (and judged by
   `tools/state_parity.py`); the hosted modules were specified by the removed Python runtime, whose recorded answers
   and fixtures the tests keep. Port or change behaviour deliberately, including error cases, limits, exit codes and
   messages, and note every deviation in the project's `PORTING.md`.
2. **State commands must be indistinguishable from the Python helpers.** Same subcommands and options, same exit
   codes, same stdout bytes (JSON key order, spacing, `ensure_ascii` escaping, number formatting, text views), same
   SQLite schema text, pragmas and rows. Use the shared helpers in `Margo.Cloud.State.Core` (`PyJson`, `PyTime`,
   `PyArgs`, `StateStore`, and `PyNode`/`PyObj` for Python's dynamic semantics) rather than re-implementing them; if
   one is missing, add it there instead of a private copy. Copy Python values with `PyNode.Copy`, never
   `JsonNode.DeepClone` (a test enforces it). Every subcommand needs scenarios in `cloud/contracts/state/`, including
   failure paths; contract probes go in `Margo.Cloud.State.Probes`. A change to a desktop helper needs the matching
   C# change in the same commit.
3. **Nothing reaches the network from state code**, as in Python.
4. **No secrets, tokens, message bodies or real workplace data** in code, tests, fixtures or logs. Fictional data and
   `example.com` only.
5. **C# style:** file-scoped namespaces, `sealed` by default, records for data, `System.Text.Json` (`JsonNode` for
   open-ended JSON), no `dynamic`, no reflection-based magic beyond the state script registry, async all the way in
   services, `CancellationToken` on I/O.
6. **Each project owns its files.** Do not edit another component's sources; add what you need behind an interface
   in your own project, or note the request in your `PORTING.md`.
7. **Tests:** xUnit v3 under `cloud/tests/`, using fakes for Work IQ, the model, Blob storage and Voice Live.
