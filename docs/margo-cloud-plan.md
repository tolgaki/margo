# Plan: Margo Cloud in C#

**Status: phases 1 to 6 done offline (November 2026); phase 0 prepared, not yet run.** Margo Cloud, the hosted
Margo, is written in **C# on .NET 10 (LTS)** in [`cloud/`](../cloud/README.md). It replaced the Python hosted
runtime, which was deleted in the phase 6 cutover, and keeps its design: Agent 365 self-hosted, one container per
hired Margo, the same persona and skills, the Work IQ guard ([design](autopilot.md),
[instances](autopilot-instance-containers.md), [voice](autopilot-voice.md), [deployment](autopilot-deployment.md)).

**Nothing has run in a live tenant.** Everything below was built and tested offline against fakes, recorded Python
behaviour and Microsoft's published contracts. The first live run is phase 0, which an operator runs in a test
tenant with [Margo.Cloud.Host/VERIFY.md](../cloud/src/Margo.Cloud.Host/VERIFY.md).

Items marked **verify** are platform or package facts not yet confirmed against the source.

## Why C#

- **Microsoft's agent stack is .NET-first.** The Microsoft 365 Agents SDK, the Agent 365 SDK
  (`Microsoft.Agents.A365.*`, MIT) and the Agent 365 CLI are all .NET. New features, such as agentic
  sign-in and notifications, have tended to reach .NET first (**verify** current parity).
- **Identity is maintained by Microsoft.** MSAL.NET covers the token chain Margo needs (managed identity →
  federated credential → blueprint → agent identity → agent user, plus on-behalf-of), instead of code we would have
  to keep in step with a preview platform.
- **The Azure SDKs are complete**, including resource management, which the provisioner uses.
- **Live meetings become possible.** Real-time media bots exist only for .NET (and run only on
  Windows; see phase 7).
- **Rust was ruled out:** no Agents SDK, no official MSAL, and an early Azure SDK.

## What stayed, what moved, what went

| Part | Before | Now |
| --- | --- | --- |
| Persona and skills (`agents/`, `skills/*/SKILL.md`, references) | Markdown, shared with desktop Margo | **Unchanged and shared.** Still the source of truth for what Margo does |
| Voice page | Browser JavaScript in the Python package | **Unchanged**, moved to `cloud/src/Margo.Cloud.Voice/wwwroot/` and served by the C# host |
| Infrastructure | Bicep under the Python package | **Moved** to `cloud/infra/`; only the container commands changed (the C# router, provisioner and host) |
| Hosted runtime: host, router, provisioner, guard, engine, orchestrator, voice server | Python | **Ported to C#; the Python runtime is deleted** |
| State helpers behind `margo-state` (ledger, approvals, task runs, proactive state, memory) | Python, also used by desktop Margo | **Ported natively to C#** (the `margo-state` CLI), byte-compatible with the Python helpers, which stay for desktop Margo |
| Desktop Margo (GitHub Copilot + persona + skills + Python helpers) | Active | **Maintenance mode**: kept working; new features go to Margo Cloud (decision D3) |

## Shape, as built

```text
cloud/
  Margo.Cloud.slnx, Directory.Build.props (warnings are errors), Directory.Packages.props (central versions)
  Dockerfile                  # one image, three roles (host, router, provisioner)
  azure.yaml, infra/, scripts/, config/, assets/, terms.md   # the azd deployment
  src/
    Margo.Cloud.State.*       # the desktop state helpers ported to C#: Core, Work, Proactive, Tasks, Memory,
                              # MemorySearch, MemoryLearning, MemoryCommands, Helpers; Probes (judge only)
    Margo.Cloud.StateCli/     # margo-state: the script entry points
    Margo.Cloud.Core/         # instance names, route table, lifecycle, activity helpers, instance binding
    Margo.Cloud.WorkIQ/       # MCP client for https://workiq.svc.cloud.microsoft/mcp
    Margo.Cloud.Guard/        # Work IQ guard: subject tokens, path policy, run modes, claims, receipts
    Margo.Cloud.Engine/       # model turn loop (Responses API), skills library, margo-state tool
    Margo.Cloud.Orchestrator/ # who is speaking, manager lookup, office queue, reply gate, leased state sync
    Margo.Cloud.Voice/        # Voice Live session bridge, voice endpoint, browser page (wwwroot/)
    Margo.Cloud.Host/         # ASP.NET Core + Agents SDK: /api/messages, notifications, voice
    Margo.Cloud.Router/       # public messaging endpoint, token check, forwarding, lifecycle → queue
    Margo.Cloud.Provisioner/  # Container Apps job: per-instance deploy, suspend, retire, route table
  tests/                      # xUnit v3, one project per component; fakes for Work IQ, model, Blob, Voice Live
  contracts/state/            # parity scenarios: margo-state against the desktop Python helpers
tools/state_parity.py         # the differential judge (CPython 3.13 behaviour)
```

Packages (central versions in `cloud/Directory.Packages.props`): `Microsoft.Agents.Hosting.AspNetCore`,
`Microsoft.Agents.Authentication.Msal`, `Microsoft.Agents.Builder`, `Microsoft.Agents.A365.Notifications` and
`.Runtime`; `Azure.Identity`, `Azure.Storage.Blobs`, `Azure.Storage.Queues`, `Azure.ResourceManager.*`,
`Azure.Monitor.OpenTelemetry.AspNetCore`; `Microsoft.IdentityModel.*` for token checks; `Microsoft.Data.Sqlite`;
xUnit v3 and `Microsoft.AspNetCore.Mvc.Testing` for tests. The Work IQ MCP client, the Responses API client and the
Voice Live client are small hand-written HTTP/WebSocket clients that port the Python ones' exact behaviour, not the
`ModelContextProtocol`, `OpenAI` or Voice Live SDKs the plan first listed.

## Principles

1. **Behaviour first, not line-by-line translation.** Each port was accepted when it passed the same scenarios as
   the Python version: the Python tests' scenarios ported to xUnit, plus differential comparisons with the Python
   code itself.
2. **Same data.** The SQLite schema, the state bundle (`state.zip` + manifest) and the Blob layout are identical, so
   the cutover needs no data migration. A bundle packed by the Python service installs in C# (a test keeps one).
3. **The state helpers stay byte-compatible.** Desktop Margo keeps the Python helpers; `margo-state` must stay
   indistinguishable from them (stdout bytes, exit codes, SQLite rows). `tools/state_parity.py` judges it on every
   change, under CPython 3.13 (the version the reference behaviour was recorded with).
4. **The repository's safety rules carry over unchanged**: approval of exact actions, unattended routines never
   send, observed content is data, the guard is the only path to Work IQ.

## Phases

| Phase | Outcome | Status |
| --- | --- | --- |
| **0. Prove the platform** | The deployment runs end to end in a test tenant: blueprint from the Agent 365 CLI, approval, hire, chat, mail queueing, the Work IQ token, agentic sign-in, voice | **Prepared, not run.** It is now run against the C# image (the Python deployment was never run live). The checklist is [VERIFY.md](../cloud/src/Margo.Cloud.Host/VERIFY.md); the open platform questions are the [verification list](autopilot.md#10-verification-list-phase-0). An operator runs it in a test tenant with fictional users |
| **1. Foundation** | `cloud/` solution (.NET 10, warnings as errors, analyzers, central package versions), xUnit v3, porting conventions | **Done.** CI job (build, test, parity judge, Release publish check) added at the cutover. No `dotnet format` gate; the image is Alpine-based, not chiseled (chiseled images lack `tzdata`) |
| **2. Security core** | Guard, Work IQ MCP client, engine, skills library, `margo-state` tool | **Done**, with every scenario of the Python guard and runtime tests ported and the guard replayed against 1,709 recorded Python decisions |
| **3. Services** | Host (Agents SDK), orchestrator, office queue, reply gate, router, provisioner, state sync | **Done offline.** Not deployed: the "phase-0 scenarios pass on C#" exit evidence waits for phase 0 |
| **4. Native state** | `margo-state` commands in C#, same arguments, schema and outputs | **Done, differently from the plan:** the Python bridge was skipped. The state helpers were ported natively from the start and judged by a differential judge (`tools/state_parity.py`, scenarios in `cloud/contracts/state/` with contract probes for library functions) |
| **5. Voice** | Voice endpoint and Voice Live bridge in C#; the page unchanged | **Done offline.** The live latency measurement (p50/p95, barge-in, tool calls) waits for phase 0 |
| **6. Cutover and clean-up** | Python runtime deleted; deployment, docs and CI updated | **Done.** `cloud/Dockerfile` (one image, three roles); infrastructure, scripts, terms and icons moved to `cloud/`; the Python runtime and its tests deleted after their scenarios were confirmed in C#; the differential tests that ran the Python runtime now replay its recorded answers and frozen fixtures; the voice page moved to `wwwroot/`; CI builds, tests and judges Margo Cloud |
| **7. Margo Cloud features** | See below | Not started |

### Phase 7: what Margo Cloud adds

In priority order. Each one keeps the unattended-mode contract (prepare privately, never send
unattended) unless the manager approves the exact action.

1. **Schedules.** The automations in `automations/*.md` (morning brief, hourly sweep, end of day)
   run per instance from a scheduler job through the router.
2. **Approvals and sends.** Adaptive Cards in the manager's chat show the exact Work IQ call; an
   approval executes it through the guard's claim check. This is the "`approve`" reply mode and the
   first outward action.
3. **Change notifications.** Microsoft Graph subscriptions (mail, chats, events) through Event Grid
   to a queue, used as a doorbell for sweeps instead of polling.
4. **Document comments.** Word, Excel and PowerPoint @mentions queued as requests for the manager.
5. **Meeting follow-up** from transcripts and recaps after the meeting.
6. **Live meeting join** (optional, large). Real-time media bots run only on Windows, so this needs
   Windows hosting (AKS Windows nodes or VMs), not Container Apps. Decide after 1 to 5.

## Testing

- **Unit and scenario tests** in xUnit with fakes: Work IQ, model, Blob lease store, Voice Live, a local MCP server.
- **State parity:** `tools/state_parity.py --check` runs every scenario in `cloud/contracts/state/` through the
  desktop Python helpers and through `margo-state` and requires identical output, files and SQLite rows.
- **Recorded Python behaviour:** the hosted modules' differential tests replay answers recorded from the Python
  runtime before it was deleted (each project's `PORTING.md`, **Cutover**). They are regression baselines now; a
  deliberate behaviour change edits them by hand, with review.
- **Parity test:** every skill the persona lists loads, and every `margo-state` command a skill shows exists and is
  implemented with the desktop script's parser (`Margo.Cloud.Engine.Tests/ParityTests.cs`, against the live desktop
  scripts).
- **Model evaluations** stay separate from tests: a fixture contract is not a model result.
- **Live checks** in the test tenant (phase 0), with fictional users only.

## Risks

| Risk | Mitigation |
| --- | --- |
| Agents SDK or Agent 365 SDK behaviour differs from what was ported, or changes during the preview | Phase 0 run; pinned versions; read the source (MIT) when behaviour is unclear |
| The desktop helpers and `margo-state` drift | `tools/state_parity.py` in CI; a desktop helper change needs the matching C# change in the same commit |
| The recorded Python answers age | They pin behaviour that was deliberately specified; changing it is a reviewed edit, never a re-recording |
| Native AOT not supported by the Agents SDK, so images are larger | Framework-dependent publish on the Alpine ASP.NET image; measure cold start; keep busy instances warm |
| Desktop Margo falls behind | Decision D3; skills stay shared, so procedure improvements still reach it |

## Decisions

| # | Decision | Outcome |
| --- | --- | --- |
| D1 | Same repository or a new one | **Same repository**, in `cloud/` |
| D2 | State store | **SQLite per instance**, as before; revisit only if schedules or notifications need concurrent writers |
| D3 | Desktop Margo's future | **Maintenance mode**: kept working; no new features. Revisit retiring it later |
| D4 | Which tenant Margo lives in | **The managers' tenant.** On-behalf-of and the manager link only work within one tenant today |
| D5 | Repository positioning | The README still calls Margo a reference implementation; reword once phase 0 and phase 7 make Margo Cloud a product |
| D6 | .NET version | **.NET 10 LTS** |

## Next steps

1. Run phase 0 in a test tenant with [VERIFY.md](../cloud/src/Margo.Cloud.Host/VERIFY.md) and record each result.
2. Fix what phase 0 finds; then start phase 7 with schedules.
