# Plan: one container per hired Margo

**Status: built, 18 October 2026; since 6 November 2026 the only way Margo is hosted; since the Phase 6 cutover
(November 2026) every part is C# in [Margo Cloud](../cloud/README.md). Unproven in a live tenant.** Deploy it with the
[deployment guide](autopilot-deployment.md).

## Self-hosted for Agent 365

Margo runs in your own Azure subscription; Agent 365 provides the agent around her.

| | Who does it |
| --- | --- |
| Blueprint, messaging endpoint registration | **The Agent 365 CLI** (`a365 setup blueprint --messaging-endpoint …`) |
| Publishing, admin approval, hiring, agent identity and agent user | **Agent 365** and the Microsoft 365 admin center |
| Teams chat, mail and notifications | Delivered by Agent 365 to **the router** (`/api/messages`), which forwards each to **the instance's own container** |
| Lifecycle events (hired, suspended, deleted) | **The router** turns them into provisioning requests; they never reach an instance |
| Each instance's state | **That instance's own Blob container**, written only by that instance |
| Creating and retiring instance containers | **The provisioner** job |
| The model and Voice Live | **Azure AI Services**, called by each instance with its own identity |

An earlier version ran Margo as a Microsoft Foundry hosted agent, with these containers beside it
for voice. That blueprint belonged to Foundry and could not point at the router, so chat could
never reach the containers; Foundry hosting was removed. This design extends
[Margo as an autopilot](autopilot.md) and keeps decision 10: hosted Margo is desktop Margo, with
the same persona and skills, and no general shell.

## What is built

| Part | Where | Tested |
| --- | --- | --- |
| Names, route table, lifecycle mapping, plans | [`Margo.Cloud.Core`](../cloud/src/Margo.Cloud.Core/Instances.cs) | Offline (`Margo.Cloud.Core.Tests`) |
| Router: platform-token check, forwarding, lifecycle to the queue, voice proxy | [`Margo.Cloud.Router`](../cloud/src/Margo.Cloud.Router/PORTING.md) | Decisions offline; the ASP.NET Core app against a fake instance (`Margo.Cloud.Router.Tests`) |
| Provisioner job: deploy, suspend, retire, route table with ETags | [`Margo.Cloud.Provisioner`](../cloud/src/Margo.Cloud.Provisioner/PORTING.md) | Steps offline with a fake cloud; SDK calls checked against the libraries |
| One instance: identity, scoped storage role, Container App | [`infra/instance.bicep`](../cloud/infra/instance.bicep) → `instance.json` | Compiled with Bicep before the Agent 365 change; `instance.json` since edited by hand to match (at the cutover: the host's container command), with parameters checked against the provisioner. Rebuild with `bicep build` before deploying |
| Shared: model, environment, router, provisioner job, queue, route table | [`infra/main.bicep`](../cloud/infra/main.bicep) | Compiled with Bicep before the Agent 365 change; since edited by hand and checked by tests, not recompiled |
| One image for all three roles | [`cloud/Dockerfile`](../cloud/Dockerfile) | Inputs and each Release publish checked; not yet built and run in Azure |
| Roles by name, image, router and job update | [`scripts/postprovision.sh`](../cloud/scripts/postprovision.sh) | Syntax; its required values checked against the Bicep outputs |
| Blueprint: federated credential, Work IQ scope and consent | [`scripts/blueprint.sh`](../cloud/scripts/blueprint.sh), [`connect-blueprint.sh`](../cloud/scripts/connect-blueprint.sh) | Syntax only |
| Instance mode in the host and orchestrator | `Margo.Cloud.Host`, `Margo.Cloud.Orchestrator` (`StateSync`) | Offline; the host started in instance mode |

Nothing was deployed: no Azure subscription or tenant was used.

## Why

With one shared process serving every hired instance (as the earlier Foundry hosted agent did),
one managed identity holds **Storage Blob Data Contributor on the whole storage account**, so
anything that compromises the shared process can read every manager's state.

One container per hired instance changes the blast radius from "every manager" to "one manager":

- **Isolation.** Each instance has its own identity, storage and network policy.
- **Simpler state.** One container is the only writer; the lease guards restarts and deploys
  rather than competing sandboxes.
- **A home for "live".** Schedules, her own inbox and the manager's chat all reach one
  long-lived Margo, like the desktop app that is always awake.

What it does not change: the manager's on-behalf-of token is still inside that container during
a turn. That is why there is still no general shell (§4.1.1 of the design); the skills run
through `margo-state` and narrow tools.

## Target shape

```text
Teams / Agent 365 ──► Router (shared, stateless) ──► Margo container for instance A ──► Work IQ
                         │  validates the inbound token            │ guard, engine, skills, SQLite
                         │  maps recipient → instance              ├─► Blob container A (snapshots)
                         └─► Margo container for instance B …      └─► Azure AI Services (model, voice)
Provisioner (separate, privileged) ◄── Agent 365 lifecycle notifications
```

| Component | Role | Holds |
| --- | --- | --- |
| **Router** | Receives activities at the blueprint endpoint, validates the platform token (the host's existing JWT middleware), reads `recipient.agenticAppId` / `agenticUserId`, forwards to that instance's internal address | No Microsoft 365 tokens, no state |
| **Instance container** | Today's image (orchestrator, guard, engine, skills, core). Serves exactly one instance and refuses activities for any other | Its own user-assigned managed identity; the instance's SQLite on local disk |
| **Instance storage** | One Blob container per instance for snapshots and daily copies (`StateSync` unchanged) | Role assignment scoped to that container only |
| **Provisioner** | Creates and retires an instance's identity, storage, container and route; reconciles drift | Contributor on one resource group; never the model, never a token for anyone's data |
| **Scheduler** | Wakes an instance for its automations (`automations/*.md` crons) | Nothing; it sends a scheduled event through the router |

### Storage

Keep SQLite on the container's local disk and keep snapshotting it to the instance's Blob
container, as `StateSync` does now. Do **not** put the SQLite file on an Azure Files share: SQLite
warns against network file systems because their locking is unreliable. If a persistent disk is
wanted, that points to AKS (below).

### Identity

Each container gets a user-assigned managed identity with:

- **Storage Blob Data Contributor** on its own Blob container only;
- **Cognitive Services OpenAI User** on the AI Services account, for the model, and
  **Cognitive Services User** for Voice Live;
- whatever the agentic token exchange for **its own** instance needs, and nothing for any other
  instance (verification item 2).

This replaces today's account-wide storage role.

### Network

Outbound traffic is limited to the Work IQ endpoint, Entra sign-in, the AI Services account, the
instance's Blob container (private endpoint) and Application Insights. Inbound traffic comes
only from the router (internal ingress).

## Hosting choice

| Option | Fit | Notes |
| --- | --- | --- |
| **Azure Container Apps** (recommended) | One app per instance, at most one replica, scale to zero, its own managed identity, internal ingress, jobs or scale rules for cron | Verify apps-per-environment limits and cold-start time |
| AKS | One StatefulSet pod per instance with an Azure Disk | Most control, most operations work |
| Foundry hosted agents | Not possible, and removed | One agent serves every instance; sessions follow conversations; its blueprint cannot point elsewhere |
| Container Apps dynamic sessions | Wrong shape | Built for short untrusted-code sessions, not a long-lived agent |

## Code changes

- **Orchestrator:** the instance is fixed by configuration (`MARGO_INSTANCE_AGENT_ID`, `MARGO_INSTANCE_USER_ID`); any activity
  for another instance is refused, as defence in depth behind the router.
- **Host:** accepts only router-forwarded requests (internal token check), and gets its Blob
  container name and identity from configuration.
- **New `router` service:** a small ASP.NET Core app (`Margo.Cloud.Router`; first built in Python with aiohttp);
  stateless; route table from the provisioner.
- **New `provisioner`:** C# with the Azure management SDKs (`Margo.Cloud.Provisioner`), plus a per-instance Bicep module
  (identity, container, role assignment, Container App from a pinned image digest).
- **Unchanged:** guard, engine, skills, state tool, parity test, `StateSync`.

## Lifecycle

| Event | What happens |
| --- | --- |
| Instance hired (lifecycle notification or admin action) | The provisioner creates identity, storage, app and route; the first message is held with a fixed "setting up" note until ready |
| Manager changes | Same container; the account key changes (already true today), so the new manager starts with fresh state. The previous manager's data follows the retention rule |
| Instance retired | The route is removed and the app and identity are deleted; storage is kept for the retention period, then deleted |
| Image update | Staged by blueprint version: a new revision per instance, canary first; the lease prevents two writers during a swap |

## Phases

| Phase | Outcome | Exit evidence |
| --- | --- | --- |
| **0. Verify** | Answers to the verification list below | Written results from a test tenant; no real data |
| **1. Router + one instance** | One hand-provisioned instance behind the router, behaving exactly like today | Same test suite; a recorded test-tenant conversation |
| **2. Provisioning** | Hire, manager change and retirement are automatic and idempotent | Reconciliation tests; no orphaned resources after failure injection |
| **3. Least privilege** | The account-wide storage role is gone; egress policy enforced | Access tests: instance A cannot read B's container |
| **4. Schedules** | Automations run per instance under the Unattended Mode Contract | Needs the principal chat ID recorded at hire and the hosted scheduler |

## Verification list

1. **Routing.** Does Agent 365 deliver every instance's activities, mail and lifecycle events to
   the blueprint's one messaging endpoint (the router), with `recipient.agenticAppId` and
   `agenticUserId` set?
2. **Token exchange.** Can a container with its own identity perform the agentic token exchange
   for its instance, and only that instance?
3. **Timing.** Cold start versus Teams activity timeouts. Do long turns need an immediate
   acknowledgement and a proactive reply?
4. **Lifecycle.** Notification payloads and their reliability, including manager-changed events.
5. **Quotas.** Container Apps limits per environment, and how many instances one environment
   holds.

## Risks

- **Cold starts** if instances scale to zero. Option: keep a warm minimum for active managers.
- **Provisioner privilege.** Keep it separate, with a minimal role on one resource group, audited,
  with no access to any instance's data.
- **Cost at scale.** A container per instance costs more than one shared agent, even at zero
  scale. Fixed costs (router, firewall if used) are shared. No estimate is made here.
- **Orphaned resources** after partial failures. The provisioner reconciles on a schedule.
- **Retention** when a manager leaves or an instance is retired (design verification item 10).

## Decisions taken (change them with the setting shown)

1. **Container Apps**, as recommended.
2. **Scale to zero** by default (`instanceMinReplicas=0`); set `1` to keep instances warm.
3. **No automatic deletion of state.** Retirement removes an instance's app and identity and keeps
   its storage. An operator deletes the `margo-i-…` container when the retention period agreed
   with the organisation has passed.

## Not done yet

- **Schedules (phase 4):** these wait on the hosted scheduler and on recording the principal chat
  ID at hire.
- **The verification list above:** especially routing (item 1) and the token exchange (item 2).
  The federated credential is added by `blueprint.sh` where the deployer may, otherwise by an
  Entra administrator.
- **Shared credential:** all instances share the blueprint credential identity, so a compromised
  instance could get Margo-the-agent's token for another instance. It could not get another
  manager's data, which needs that manager's own sign-in. Verification item 2 asks whether this
  can be per instance.
