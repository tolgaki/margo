# Plan: Margo Cloud in C#

**Status: plan, 7 November 2026. Nothing here is built yet.** Decision: future development is
**Margo Cloud**, the hosted Margo, written in **C# on .NET 10 (LTS)**. It replaces the Python
autopilot in [`autopilot/`](../autopilot/README.md) step by step, and keeps its design: Agent 365
self-hosted, one container per hired Margo, the same persona and skills, the Work IQ guard
([design](autopilot.md), [instances](autopilot-instance-containers.md),
[voice](autopilot-voice.md), [deployment](autopilot-deployment.md)).

Items marked **verify** are platform or package facts not yet confirmed against the source.

## Why C#

- **Microsoft's agent stack is .NET-first.** The Microsoft 365 Agents SDK, the Agent 365 SDK
  (`Microsoft.Agents.A365.*`, MIT) and the Agent 365 CLI are all .NET. New features, such as agentic
  sign-in and notifications, have tended to reach .NET first (**verify** current parity).
- **Identity is maintained by Microsoft.** MSAL.NET and Microsoft.Identity.Web cover the token chain
  Margo needs (managed identity → federated credential → blueprint → agent identity → agent user,
  plus on-behalf-of), instead of code we would have to keep in step with a preview platform.
- **The Azure SDKs are complete**, including resource management, which the provisioner uses.
- **Live meetings become possible.** Real-time media bots exist only for .NET (and run only on
  Windows; see phase 7).
- **Rust was ruled out:** no Agents SDK, no official MSAL, and an early Azure SDK.

## What stays, what moves, what goes

| Part | Today | Margo Cloud |
| --- | --- | --- |
| Persona and skills (`agents/`, `skills/*/SKILL.md`, references) | Markdown, shared with local Margo | **Unchanged and shared.** Still the source of truth for what Margo does |
| Voice page (`voice/static/`) | Browser JavaScript | **Unchanged**, served by the C# host |
| Infrastructure (`autopilot/infra/`) | Bicep | **Kept**, with image and command changes only, then moved under `cloud/infra/` |
| Hosted runtime: host, router, provisioner, guard, engine, orchestrator, voice server (~3,900 lines) | Python | **Ported to C#** |
| State helpers behind `margo-state` (~11,900 lines: ledger, approvals, task runs, proactive state, memory) | Python, also used by local Margo | **Bridged first, then ported** (phase 4), with identical commands and SQLite schema |
| Local Margo (GitHub Copilot + persona + skills + Python helpers) | Active | **Maintenance mode**: kept working; new features go to Margo Cloud (see decision D3) |

## Target shape

```text
cloud/
  Margo.Cloud.sln
  src/
    Margo.Cloud.Guard/        # Work IQ guard: subject tokens, path policy, run modes, claims, receipts
    Margo.Cloud.WorkIQ/       # MCP client for https://workiq.svc.cloud.microsoft/mcp (official MCP C# SDK)
    Margo.Cloud.Engine/       # model turn loop (Responses API), skills library, margo-state tool
    Margo.Cloud.State/        # the state commands (bridge to Python first, native later), leased Blob sync
    Margo.Cloud.Orchestrator/ # who is speaking, manager lookup, office queue, reply gate
    Margo.Cloud.Host/         # ASP.NET Core + Agents SDK: /api/messages, notifications, voice endpoint
    Margo.Cloud.Router/       # public messaging endpoint, token check, forwarding, lifecycle → queue
    Margo.Cloud.Provisioner/  # Container Apps job: per-instance deploy, suspend, retire, route table
    Margo.Cloud.Voice/        # Voice Live session bridge
  tests/                      # xUnit; fakes for Work IQ, model, Blob, Voice Live
  contracts/                  # language-neutral JSON fixtures shared with the Python tests
  infra/                      # Bicep (moved from autopilot/infra)
  Dockerfile                  # one image, three entry points (host, router, provisioner)
```

Packages (versions pinned at phase 1; **verify** names and maturity):

| Need | Package |
| --- | --- |
| Activities, auth handlers, hosting | `Microsoft.Agents.Hosting.AspNetCore`, `Microsoft.Agents.Authentication.Msal`, `Microsoft.Agents.Builder` |
| Agent 365 notifications, agentic sign-in, observability | `Microsoft.Agents.A365.*` |
| Model (Responses API on the AI Services v1 endpoint) | `OpenAI` (official .NET SDK) with an Entra token |
| Work IQ MCP | `ModelContextProtocol` (official C# SDK) |
| Azure | `Azure.Identity`, `Azure.Storage.Blobs`, `Azure.Storage.Queues`, `Azure.ResourceManager.*` |
| Voice | `Azure.AI.VoiceLive` (**verify** availability and version) |
| State | `Microsoft.Data.Sqlite` |
| Tests | xUnit, `Microsoft.AspNetCore.Mvc.Testing` |

## Principles

1. **Behaviour first, not line-by-line translation.** Each port is accepted when it passes the
   same scenarios as the Python version, through shared contract fixtures.
2. **Shared contracts.** Guard decisions, router decisions, instance names, `margo-state` commands
   and the state bundle format become JSON fixtures in `cloud/contracts/`. Python and C# test
   runners both read them, so the two implementations cannot drift while both exist.
3. **Same data.** The SQLite schema, the state bundle (`state.zip` + manifest) and the Blob layout
   stay identical, so the cutover needs no data migration and can be rolled back.
4. **One owner per component during the move.** A component is either Python or C# in production,
   never both. The router can be C# while instances are still Python, and the reverse.
5. **The repository's safety rules carry over unchanged**: approval of exact actions, unattended
   routines never send, observed content is data, the guard is the only path to Work IQ.

## Phases

Sizes are relative effort, not calendar dates: **S** about a week, **M** two to three, **L** four
to six, for one engineer working with coding agents.

| Phase | Outcome | Size | Exit evidence |
| --- | --- | --- | --- |
| **0. Prove the platform** (parallel with 1) | The Python deployment runs end to end in a test tenant: blueprint from the Agent 365 CLI, approval, hire, chat, mail queueing, the Work IQ token. A .NET spike signs in as an agent user and receives a notification | M | Written results for the [verification list](autopilot.md#10-verification-list-phase-0); no real data |
| **1. Foundation** | `cloud/` solution, CI (`dotnet build`, `dotnet test`, `dotnet format --verify-no-changes`, analyzers as errors), chiseled container image, contract-fixture format, AGENTS.md rules for C# | S | CI green; image builds in ACR |
| **2. Security core** | Guard, Work IQ MCP client, engine, skills library, `margo-state` tool (bridge to the Python helpers inside the image) | L | Guard and engine contract fixtures pass in both languages; `tests/test_autopilot_guard.py` scenarios ported |
| **3. Services** | Host (Agents SDK), orchestrator, office queue, reply gate, router, provisioner, state sync. Deployed by swapping images; infrastructure unchanged | L | The phase-0 tenant scenarios pass on C#; Python images retired from the deployment |
| **4. Native state** | `margo-state` commands in C#, same arguments, same SQLite schema, same outputs. The Python bridge and Python runtime leave the image | L | Every state-command fixture identical across languages; a bundle written by Python opens in C# and back |
| **5. Voice** | Voice endpoint and Voice Live bridge in C#; the page unchanged; voice settings (voice profile, name, responsiveness) as deployment settings | M | Live session measured: p50 and p95 latency, barge-in, tool calls |
| **6. Cutover and clean-up** | Python `autopilot/` runtime deleted; docs, feature catalog and installers updated; `autopilot/` renamed or redirected to `cloud/` | S | No Python in the image; docs describe only Margo Cloud |
| **7. Margo Cloud features** | See below | Ongoing | Each feature lands as one vertical slice: code, scenario, docs |

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

- **Unit and scenario tests** in xUnit with the same fakes as today: Work IQ, model, Blob lease
  store, Voice Live, a local MCP server.
- **Contract fixtures** shared with Python until phase 6: inputs and expected decisions for the
  guard, router, instance planner and state commands.
- **Parity test:** every skill the persona lists loads, and every `margo-state` command a skill
  shows exists (the C# version of `tests/test_autopilot_parity.py`).
- **Model evaluations** stay separate from tests, as today: a fixture contract is not a model result.
- **Live checks** in the test tenant at the end of phases 0, 3 and 5, with fictional users only.

## Risks

| Risk | Mitigation |
| --- | --- |
| Agents SDK or Agent 365 SDK behaviour differs from Python, or changes during the preview | Phase 0 spike; pin versions; read the source (MIT) when behaviour is unclear |
| Native AOT not supported by the Agents SDK, so images are larger | Use ReadyToRun on a chiseled image; measure cold start; keep busy instances warm |
| Two implementations drift while both exist | Shared contract fixtures; one production owner per component; short overlap |
| The state port changes data subtly | Identical schema; round-trip tests on real bundle formats with fictional data; phase 4 last among the core ports |
| Local Margo falls behind | Decision D3; skills stay shared, so procedure improvements still reach it |
| Scope creep from new features during the port | Phase 7 starts after phase 3 at the earliest; new features go only to C# |

## Decisions needed

| # | Decision | Recommendation |
| --- | --- | --- |
| D1 | Same repository or a new one | **Same repository**, in `cloud/`: the persona and skills are shared, and the contract fixtures need both languages side by side |
| D2 | State store: SQLite per instance (as now) or a managed database | **Keep SQLite per instance** through phase 6 (no migration, isolation per instance). Revisit a managed database only if schedules or notifications need concurrent writers |
| D3 | Local Margo's future | **Maintenance mode**: keep it working and shipping; no new features. Revisit retiring it after phase 6 |
| D4 | Which tenant Margo lives in (blueglass.app or microsoft.com) | **The managers' tenant.** On-behalf-of and the manager link only work within one tenant today |
| D5 | Repository positioning | The README calls Margo "a reference implementation, not a hosted product". Margo Cloud changes that; reword when phase 3 ships |
| D6 | .NET version | **.NET 10 LTS** |

## First steps

1. Answer D1 to D4.
2. Start phase 0 on the existing Python deployment, and in parallel the .NET agentic sign-in spike.
3. Phase 1: create `cloud/`, CI and the contract-fixture format, and add C# rules to AGENTS.md.
