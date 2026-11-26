# Margo Cloud (C#)

Margo Cloud in C# on .NET 10: self-hosted for Agent 365 in Azure Container Apps, one container per hired Margo.
This page is for developers building and testing it. Start with the [Margo Cloud overview](../docs/margo-cloud.md);
deploy with [Deploy Margo Cloud](../docs/autopilot-deployment.md); the reasoning is in the
[design and decision record](../docs/autopilot.md).

Margo Cloud is not installed by the local installers. Read the deployment guide's
[known limits](../docs/autopilot-deployment.md#known-limits) before relying on it. Optional: the
[live verification checklist](src/Margo.Cloud.Host/VERIFY.md) for a test tenant.

Margo has two implementations that share the persona (`agents/`) and the skills (`skills/`):

- **Margo desktop**: GitHub Copilot with Margo's persona, skills and the Python state helpers in
  `skills/chief-of-staff/scripts/`. Stable and supported; new features go into Margo Cloud.
- **Margo Cloud**: this folder. It loads Work IQ as an MCP server with the official MCP C# SDK
  (`Margo.Cloud.WorkIQ`), and gives the model Work IQ's own tool descriptions from `tools/list`, behind the guard.
  Its state commands (`margo-state`) must stay byte-compatible with the desktop helpers; `tools/state_parity.py` is
  the judge.

New to Work IQ in a custom agent? Read [Learn Work IQ](../docs/work-iq/README.md) and the minimal sample in
[`samples/WorkIQHello`](samples/WorkIQHello/).

## Build, test, publish

Needs the .NET 10 SDK and, for the tests and the parity judge, `python3` (the desktop helpers; the judge pins
CPython 3.13 behavior).

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
and quiet hours need), running as the non-root user the .NET images provide; `agents/` and `skills/`, plus Microsoft's
`workiq` skill pinned in `vendor/workiq/` (refresh with `scripts/update-workiq-skill.sh`), sit under `/app`
(`MARGO_REPO_ROOT`), without any `state/`. Deploy with `azd` from this folder; the steps are in
[docs/autopilot-deployment.md](../docs/autopilot-deployment.md).

| Path | Purpose |
| --- | --- |
| `Dockerfile` | The image (multi-stage: .NET 10 SDK build, ASP.NET Core runtime) |
| `azure.yaml`, `infra/main.bicep`, `infra/main.parameters.json` | `azd` project: model, registry, storage, router, provisioner |
| `infra/instance.bicep`, `infra/instance.json` | One instance's identity, scoped storage role and Container App (rebuild the JSON with `bicep build cloud/infra/instance.bicep --outfile cloud/infra/instance.json`) |
| `scripts/` | `azd` hooks and helpers: `preprovision.sh`, `postprovision.sh` (roles, image, blueprint connection), `build-image.sh`, `connect-blueprint.sh`, `blueprint.sh`, `provisioner.sh` (run the provisioner job once: `redeploy-all`, `enqueue …`), and `lib.sh` (their shared helpers) |
| `config/mcp.json`, `terms.md`, `assets/` | MCP config, terms and privacy, icons |

## Layout

| Project | Specified by | Notes |
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
| `Margo.Cloud.Core` | Its recorded answers and fixtures | Names, route table, lifecycle, activity helpers, instance binding |
| `Margo.Cloud.WorkIQ` | The MCP C# SDK and Work IQ's own tool schemas | Work IQ loaded as an MCP server: one session per identity, tool list, writes never resent |
| `Margo.Cloud.Guard` | Its recorded answers and fixtures | The only path from the model to Work IQ |
| `Margo.Cloud.Engine` | Its recorded answers and fixtures | Model turn loop and the `margo-state` tool |
| `Margo.Cloud.Orchestrator` | Its recorded answers and fixtures | |
| `Margo.Cloud.Voice` | Its recorded answers and fixtures | The browser page is in `wwwroot/` |
| `Margo.Cloud.Host` | Its recorded answers and fixtures | ASP.NET Core + Microsoft 365 Agents SDK |
| `Margo.Cloud.Router` | Its recorded answers and fixtures | Public messaging endpoint |
| `Margo.Cloud.Provisioner` | Its recorded answers and fixtures | Container Apps job |

The state helpers named above live in `skills/chief-of-staff/scripts/` and are the specification for the state
projects. The other projects are specified by the recorded answers and frozen fixtures their tests keep. Each project's
`PORTING.md` is a maintainer note: the mapping to its specification and every deliberate deviation. Operators don't
need them.

## The hosted action convention

**Approval cards.** The model proposes an action in this shape and asks for approval (`request_approval`); the host
posts an Adaptive Card drawn from the ledger; the manager's tap arrives as a message whose value names the action,
revision and hash; the orchestrator records the approval, starts the attempt, and runs the stored call through a guard
holding the ledger's claim journal (`Margo.Cloud.Orchestrator/Approvals.cs`, `Margo.Cloud.Host/ApprovalCards.cs`). The
model is never offered a write tool and cannot approve.

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

1. **Behavior is specified, not invented.** The state commands are specified by the desktop helpers (and judged by
   `tools/state_parity.py`); the other projects by the recorded answers and fixtures their tests keep. The Work IQ client is the exception: it is built on the MCP C# SDK and tested on its
   own terms. Port or change behavior deliberately, including error cases, limits, exit codes and
   messages, and note every deviation in the project's `PORTING.md`.
2. **State commands must be indistinguishable from the Python helpers.** Same subcommands and options, same exit
   codes, same stdout bytes (JSON key order, spacing, `ensure_ascii` escaping, number formatting, text views), same
   SQLite schema text, pragmas and rows. Use the shared helpers in `Margo.Cloud.State.Core` (`PyJson`, `PyTime`,
   `PyArgs`, `StateStore`, and `PyNode`/`PyObj` for Python's dynamic semantics) rather than re-implementing them; if
   one is missing, add it there instead of a private copy. Copy Python values with `PyNode.Copy`, never
   `JsonNode.DeepClone` (a test enforces it). Every subcommand needs scenarios in `cloud/contracts/state/`, including
   failure paths; contract probes go in `Margo.Cloud.State.Probes`. A change to a desktop helper needs the matching
   C# change in the same commit.
3. **Nothing reaches the network from state code**, as in the desktop helpers.
4. **No secrets, tokens, message bodies or real workplace data** in code, tests, fixtures or logs. Fictional data and
   `example.com` only.
5. **C# style:** file-scoped namespaces, `sealed` by default, records for data, `System.Text.Json` (`JsonNode` for
   open-ended JSON), no `dynamic`, no reflection-based magic beyond the state script registry, async all the way in
   services, `CancellationToken` on I/O.
6. **Each project owns its files.** Do not edit another component's sources; add what you need behind an interface
   in your own project, or note the request in your `PORTING.md`.
7. **Tests:** xUnit v3 under `cloud/tests/`, using fakes for Work IQ, the model, Blob storage and Voice Live.
