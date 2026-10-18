# Plan: one container per hired Margo

**Status: proposal, 18 October 2026. Nothing here is built.** It extends
[Margo as a Foundry autopilot](autopilot.md) and keeps decision 10: hosted Margo is desktop Margo,
with the same persona and skills, and no general shell.

## Why

Today one Foundry hosted agent serves every hired instance. Sessions follow conversations, so a
manager's chat and a colleague's message can land in different sandboxes, and state is shared
through a leased Blob bundle. One managed identity holds **Storage Blob Data Contributor on the
whole storage account** (`autopilot/scripts/assign-agent-roles.sh`), so anything that compromises
the shared process can read every manager's state.

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
                         └─► Margo container for instance B …      └─► Foundry model endpoint
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
- **Foundry User** on the project, for the model;
- whatever the agentic token exchange for **its own** instance needs, and nothing for any other
  instance (verification item 2).

This replaces today's account-wide storage role.

### Network

Outbound traffic is limited to the Work IQ endpoint, Entra sign-in, the Foundry project, the
instance's Blob container (private endpoint) and Application Insights. Inbound traffic comes
only from the router (internal ingress).

## Hosting choice

| Option | Fit | Notes |
| --- | --- | --- |
| **Azure Container Apps** (recommended) | One app per instance, at most one replica, scale to zero, its own managed identity, internal ingress, jobs or scale rules for cron | Verify apps-per-environment limits and cold-start time |
| AKS | One StatefulSet pod per instance with an Azure Disk | Most control, most operations work |
| Foundry hosted agents | Not possible | One agent serves every instance; sessions follow conversations |
| Container Apps dynamic sessions | Wrong shape | Built for short untrusted-code sessions, not a long-lived agent |

## Code changes

- **Orchestrator:** the instance is fixed by configuration (`MARGO_INSTANCE_ID`); any activity
  for another instance is refused, as defence in depth behind the router.
- **Host:** accepts only router-forwarded requests (internal token check), and gets its Blob
  container name and identity from configuration.
- **New `router` service:** a small aiohttp app; stateless; route table from the provisioner.
- **New `provisioner`:** Python with the Azure management SDKs, plus a per-instance Bicep module
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

1. **Routing.** Can Agent 365 send each instance's activities to its own endpoint, or must one
   router forward them?
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

## Decisions needed

1. Container Apps (recommended) or AKS.
2. Scale to zero (cheaper, cold starts) or a warm minimum (faster, costs more).
3. Retention period for a retired instance's state, and for a previous manager's state.
