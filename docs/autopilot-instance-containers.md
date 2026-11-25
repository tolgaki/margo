# One container per hired Margo

Every hired Margo runs in a container of her own. Deploy it with the [deployment guide](autopilot-deployment.md).

## Self-hosted for Agent 365

Margo runs in your own Azure subscription; Agent 365 provides the agent around her.

| | Who does it |
| --- | --- |
| Blueprint, messaging endpoint registration | **The Agent 365 CLI** (`a365 setup blueprint --no-endpoint`, then `--endpoint-only`; see the [deployment guide](autopilot-deployment.md#2-create-the-blueprint-with-the-agent-365-cli)) |
| Publishing, admin approval, hiring, agent identity and agent user | **Agent 365** and the Microsoft 365 admin center |
| Teams chat, mail and notifications | Delivered by Agent 365 to **the router** (`/api/messages`), which forwards each to **the instance's own container** |
| Lifecycle events (hired, suspended, deleted) | **The router** turns them into provisioning requests; they never reach an instance |
| Each instance's state | **That instance's own Blob container**, written only by that instance |
| Creating and retiring instance containers | **The provisioner** job |
| The model and Voice Live | **Azure AI Services**, called by each instance with its own identity |

Margo Cloud uses the same persona and skills as desktop Margo, with no general shell (design decision 10 in the
[design record](autopilot.md#11-decisions)).

## What is built

| Part | Where | Tested |
| --- | --- | --- |
| Names, route table, lifecycle mapping, plans | [`Margo.Cloud.Core`](../cloud/src/Margo.Cloud.Core/Instances.cs) | Offline (`Margo.Cloud.Core.Tests`) |
| Router: platform-token check, forwarding, lifecycle to the queue, voice proxy | [`Margo.Cloud.Router`](../cloud/src/Margo.Cloud.Router/) | Decisions offline; the ASP.NET Core app against a fake instance (`Margo.Cloud.Router.Tests`) |
| Provisioner job: deploy, suspend, retire, route table with ETags | [`Margo.Cloud.Provisioner`](../cloud/src/Margo.Cloud.Provisioner/) | Steps offline with a fake cloud; SDK calls checked against the libraries |
| One instance: identity, scoped storage role, Container App | [`infra/instance.bicep`](../cloud/infra/instance.bicep) → `instance.json` | Compiled with the Bicep CLI; a test compares the two |
| Shared: model, environment, router, provisioner job, queue, route table | [`infra/main.bicep`](../cloud/infra/main.bicep) | Compiled with the Bicep CLI; deployed to a test tenant |
| One image for all three roles | [`cloud/Dockerfile`](../cloud/Dockerfile) | Built with `az acr build` and run in a test tenant; the ReadyToRun build has not been built in Azure |
| Roles by name, image, router and job update | [`scripts/postprovision.sh`](../cloud/scripts/postprovision.sh) | Syntax; its required values checked against the Bicep outputs |
| Blueprint: federated credential, Work IQ scope and consent | [`scripts/blueprint.sh`](../cloud/scripts/blueprint.sh), [`connect-blueprint.sh`](../cloud/scripts/connect-blueprint.sh) | Syntax only |
| Instance mode in the host and orchestrator | `Margo.Cloud.Host`, `Margo.Cloud.Orchestrator` (`StateSync`) | Offline; the host started in instance mode |

One deployment to a test tenant proved the core path: the router, provisioning, agentic sign-in, a
Teams chat turn and delegated calendar reads. See the deployment guide's
[known limits](autopilot-deployment.md#known-limits).

## Why

With one shared process serving every hired instance,
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
| **Provisioner** | Creates and retires an instance's identity, storage, container and route; reconciles drift | Contributor on the resource group (it creates apps and identities), plus role administration limited to the instance roles on the storage account, registry and AI account, for service principals other than Margo's own identities. Never a token for anyone's data, but Contributor could reconfigure storage access |
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

**Built:** instances have internal ingress only; inbound traffic comes only from the router. Storage, the registry and
the AI account allow Entra sign-in only (no shared keys, no anonymous access) but accept connections from public
networks.

**Target, not built:** outbound traffic limited to the Work IQ endpoint, Entra sign-in, the AI Services account,
the instance's Blob container (private endpoint) and Application Insights.

## Hosting choice

| Option | Fit | Notes |
| --- | --- | --- |
| **Azure Container Apps** (recommended) | One app per instance, at most one replica, scale to zero, its own managed identity, internal ingress, jobs or scale rules for cron | Verify apps-per-environment limits and cold-start time |
| AKS | One StatefulSet pod per instance with an Azure Disk | Most control, most operations work |
| Container Apps dynamic sessions | Wrong shape | Built for short untrusted-code sessions, not a long-lived agent |

## Code changes

- **Orchestrator:** the instance is fixed by configuration (`MARGO_INSTANCE_AGENT_ID`, `MARGO_INSTANCE_USER_ID`);
  any activity for another instance is refused, as defense in depth behind the router.
- **Host:** accepts only router-forwarded requests (internal token check), and gets its Blob
  container name and identity from configuration.
- **New `router` service:** a small ASP.NET Core app (`Margo.Cloud.Router`);
  stateless; route table from the provisioner.
- **New `provisioner`:** C# with the Azure management SDKs (`Margo.Cloud.Provisioner`), plus a per-instance Bicep module
  (identity, container, role assignment, Container App from a pinned image digest).
- **Unchanged:** guard, engine, skills, state tool, parity test, `StateSync`.

## Lifecycle

| Event | What happens |
| --- | --- |
| Instance hired (lifecycle notification or admin action) | The provisioner creates identity, storage, app and route; until it is ready the router answers activities for it with 503 and `Retry-After: 30`, so the platform retries; Margo does not message first |
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
   acknowledgment and a proactive reply?
4. **Lifecycle.** Notification payloads and their reliability, including manager-changed events.
5. **Quotas.** Container Apps limits per environment, and how many instances one environment
   holds.

## Risks

- **Cold starts** if instances scale to zero. Option: keep a warm minimum for active managers.
- **Provisioner privilege.** It is separate and has no Microsoft 365 access, but holds Contributor on the resource
  group, which could re-enable shared-key access to storage and so reach every instance's state. Replacing it with
  narrower roles is open.
- **Cost at scale.** A container per instance costs more than one shared agent, even at zero
  scale. Fixed costs (router, firewall if used) are shared. No estimate is made here.
- **Orphaned resources** after partial failures. The provisioner reconciles on a schedule.
- **Retention** when a manager leaves or an instance is retired (design verification item 10).

## Decisions taken (change them with the setting shown)

1. **Container Apps**, as recommended.
2. **Scale to zero** by default; `azd env set MARGO_INSTANCE_MIN_REPLICAS 1` keeps instances warm. A turn in flight
   when an instance is stopped gets up to nine minutes to finish.
3. **No automatic deletion of a retired instance's state.** Retirement removes an instance's app and identity and
   keeps its storage. An operator deletes the `margo-i-…` container when the organization decides. Within a live
   instance, replaced versions of the state bundle and daily copies expire after 30 days
   (`MARGO_STATE_VERSION_RETENTION_DAYS`, `MARGO_STATE_DAILY_RETENTION_DAYS`).

## Not done yet

- **Schedules (phase 4):** these wait on the hosted scheduler and on recording the principal chat
  ID at hire.
- **The verification list above:** especially routing (item 1) and the token exchange (item 2).
  The federated credential is added by `blueprint.sh` where the deployer may, otherwise by an
  Entra administrator.
- **Shared credential:** all instances share the blueprint credential identity, so a compromised
  instance could get another instance's agent token. In delegation mode that agent user has delegate access to its
  own manager's mailbox and calendar, so **a compromised instance could reach another manager's mail and calendar**.
  Isolation between managers rests on the software checks in each container. Verification item 2 asks whether the
  credential can be per instance; a token broker that issues only the caller's own token is the alternative.
