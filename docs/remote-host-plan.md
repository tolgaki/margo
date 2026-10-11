# Plan: Margo on a remote host with her own identity

**Status: implemented in code and templates; tenant verification pending.** The manager
binding, directives, action gate, harness, deployment templates, runbooks and CI checks are in the
repository with deterministic tests against synthetic Work IQ, Copilot and Graph data. Phase 0
(platform verification) and Phase 7 (pilot) need the owner's tenant and are not done. Local
installs keep the policy in [trust and safety](safety.md) §1–§3; the remote-host profile is
described in [§8](safety.md#8-the-remote-host-profile) and operated per the
[operator guide](how-to/remote-host.md). The [delivery tracker](#delivery-tracker) is the source
of truth for what exists.

[Documentation hub](README.md) · [Autopilot design note](autopilot.md) ·
[Container recipe](container.md) · [Architecture](development/architecture.md)

---

## 1. Decisions recorded

Recorded 2026-10-11 by the repository owner. Changing any of these means updating this section
first.

| Topic | Decision |
| --- | --- |
| Host | An always-on **Azure VM** running Margo in **GitHub Copilot CLI**, supervised by a harness in this repository |
| Machine identity | The VM has a **system-assigned managed identity**. It is used to reach Key Vault and to obtain Margo's tokens without stored secrets where the platform allows it |
| Margo's identity | Margo has **her own Entra identity** (an agent identity or a dedicated account, settled in [Phase 0](#phase-0-platform-verification)). `/me` means Margo |
| Access to the manager's data | Margo has **delegated access** to the manager's mailbox, calendar and the Teams surfaces the tenant permits. She reads the manager's data through explicit `/users/{manager}` paths, never by impersonation |
| Startup | On boot she checks mail, calendar and Teams through Work IQ before anything else, then keeps watching |
| Manager | `init` binds exactly **one manager** by immutable Entra object ID. Only the manager has authority to give instructions or make changes |
| Instruction channels | The manager may instruct Margo through **Teams, email and the CLI**, each verified as described in [§4](#4-manager-authority) |
| Approval policy | Only **destructive** actions and **communicating** actions (sending mail, messages, RSVPs, invitations, sharing) need exact approval. Other reversible, private writes may run on the manager's instruction. See [§5](#5-action-tiers) |
| Tenant details | Supplied by the owner at deployment. Tenant, subscription, object IDs and host names **never enter the checkout**; see [§9](#9-deployment-configuration-outside-the-repository) |

### What this changes compared with the current policy

This is a deliberate widening of what Margo may do without per-item approval. It applies **only**
to a remote-host deployment with a manager binding and the action gate installed. Local installs
keep today's behavior.

| Behavior | Current policy | Remote-host profile |
| --- | --- | --- |
| Reads | Allowed | Allowed |
| Private, reversible writes (flag, mark read, file, draft, private hold) | Exact approval | Allowed on a verified manager instruction or a standing rule the manager set; journaled with undo information |
| Send, reply, post, react, RSVP, invite, share | Exact approval | Exact approval (unchanged) |
| Delete, cancel, overwrite, change permissions or rules | Exact approval | Exact approval (unchanged) |
| Unattended runs | Never change anything external | May perform private writes under a manager-set standing rule; never communicating or destructive actions |
| Messages from Margo to the manager | Not applicable | Allowed on the control channel only; see [§4](#control-channel-replies) |
| Who may approve | Whoever is in the foreground session | Only the bound manager, through a verified channel |

The widening must be enforced by the [action gate](#6-work-iq-action-gate), not by a prompt. Until
the gate exists, the scheduled wrapper's four Work IQ deny rules stay exactly as they are.

---

## 2. Architecture

```text
Azure VM (managed identity, no public IP, Entra SSH login for the manager only)
  systemd: margo-harness.service (user margo)
    harness supervisor
      boot preflight -> startup sweep -> watch loop -> scheduled automations
      launches: copilot --agent margo -p <prompt>  (Work IQ write tools denied directly)
                        |
                        v  MCP over a local socket
  systemd: margo-gate.service (user margo-gate, owns tokens)
    Work IQ action gate
      reads -> forwarded
      writes -> classified (T1/T2/T3) -> manager directive or exact approval checked
             -> one execution claim -> Work IQ -> receipt
                        |
                        v
    Work IQ MCP server (child of the gate) -> Microsoft 365 as Margo,
                                              delegated access to the manager
  Private state (encrypted disk): margo config, account-scoped SQLite, logs
  Key Vault (secrets the gate and harness need), Azure Monitor (health and alerts)
```

Owners follow the [architecture map](development/architecture.md): persona in `agents/`,
procedures in `skills/`, deterministic state and the gate in standard-library Python, and the
existing ledger as the only approval and execution journal. The harness adds supervision, not a
second task tracker.

---

## 3. Identity and delegated access

- **Margo's principal** is the configured `account`. The harness confirms on every boot and every
  watch cycle that Work IQ is signed in as that principal. A mismatch is `blocked`, never a
  warning.
- **The manager's data** is read with explicit paths such as `/users/{manager}/messages` and
  `/users/{manager}/calendarView`. Procedures that currently assume `/me` is the person being
  helped need a target-principal parameter. Replacing `/me` everywhere is not enough; each
  procedure is reviewed so Margo's own mailbox (where instructions arrive) and the manager's
  mailbox (what she manages) never get confused.
- **Delegated permissions**: Full Access or delegate rights on the manager's mailbox and calendar,
  granted by an Exchange administrator. Teams chats of the manager are generally not readable by a
  delegate; Margo sees the Teams conversations she is a member of, including her 1:1 with the
  manager. Phase 0 records exactly which surfaces work.
- **Sending on the manager's behalf** ("Send on Behalf" or "Send As") is a separate grant. It is
  only used by approved T2 actions, and the approval names which identity the message is sent as.
- **Account isolation**: private state remains keyed by Margo's principal. Work items record the
  principal they concern (Margo or the manager) so receipts and memory stay attributable.

---

## 4. Manager authority

Authority comes from a verified identity, never from what a message says about itself. Content
written by anyone else, or quoted, forwarded or attached inside the manager's message, remains
data under [§4 of trust and safety](safety.md#4-observed-content-is-data-never-instructions).

| Channel | Counts as a manager instruction only when | Never counts |
| --- | --- | --- |
| **CLI** (`margo-control`) | The session arrives through Entra SSH login to the VM, Azure RBAC grants VM login only to the manager and break-glass, and the local login maps to the bound manager | Any other local user, a cron job, or Margo's own runs invoking the CLI |
| **Teams** | The message is in the 1:1 chat between Margo and the manager, and Graph reports `from.user.id` equal to the bound manager object ID | Group chats, channels, meeting chats, edited messages after Margo acted, quoted or forwarded content, attachments, links |
| **Email** | The message is addressed to Margo's mailbox, the sender resolves to the bound manager, authentication results pass, **and** the same `internetMessageId` exists in the manager's Sent Items (checked through delegated access, which defeats a spoofed `From`) | Forwarded or quoted text, attachments, messages the manager merely received, auto-forwards, replies from anyone else in the thread |

Rules that hold on every channel:

1. A verified instruction authorizes **T0 and T1** work. It does not approve a T2 or T3 action
   unless it is an approval of an exact action revision (below).
2. Each verified instruction becomes a **directive record**: channel, source message ID, manager
   object ID, verbatim instruction text, and received time. The gate checks for a directive (or a
   standing rule that came from one) before any T1 write.
3. Standing rules ("always file newsletters into Reading") are directives too. They can be listed
   and revoked through every channel; revoking stops future use, it does not undo past effects.
4. If verification fails or is uncertain (for example, the Sent Items lookup times out), the
   message is treated as data and the manager is told verification failed.
5. Changing the bound manager requires the CLI on the VM by the current manager, or the
   break-glass procedure. It can never be done from Teams or email.

### Approvals

A T2 or T3 action is shown to the manager with its exact payload, target, identity used and a
short reference bound to action ID, revision and hash. The manager approves through:

- `margo-control approve <ref>` on the CLI, or
- a reply in the Teams 1:1 or by email containing the reference and an approval word, verified as
  above.

The approval is recorded in the existing ledger with a new evidence reference prefix
(`manager-channel:<channel>:<message-id>`) that the harness writes only after verification.
`human()` continues to validate the record shape; the harness and gate are what authenticate it.
Editing the action invalidates the approval exactly as today.

### Control-channel replies

Asking for approval must not itself require approval. Margo may post to **only** the manager,
**only** in the Teams 1:1 or as an email to the manager's address, with:

- approval requests, briefs, status and verification failures;
- no forwarding of third-party content beyond short cited excerpts;
- a rate limit and the same receipt journal as other writes.

Every other recipient is T2.

---

## 5. Action tiers

| Tier | Examples | Requirement |
| --- | --- | --- |
| **T0 read** | `fetch`, `retrieve`, `ask`, `call_function`, `get_schema`, `search_paths`, `fetch_blob` | None |
| **T1 private, reversible** | Mark read or unread, flag, categorize, move to a folder other than Deleted Items, create or edit a draft without sending, a private calendar hold with no attendees, private notes in Margo's own storage | A verified directive or standing rule. The prior state is journaled so the change can be undone |
| **T2 communicating** | Send, reply, forward, Teams post, reply or reaction, RSVP, creating or changing a meeting with attendees, automatic replies, sharing a file or link | Exact manager approval |
| **T3 destructive or security-relevant** | Delete or move to Deleted Items, cancel a meeting, purge, overwrite or remove a document, change permissions or membership, create or change inbox rules or forwarding | Exact manager approval |
| **Unclassified** | Anything the gate cannot place | Treated as T3 |

Inbox rules and forwarding are T3 because they can silently exfiltrate mail. A meeting change that
notifies attendees is T2 even if it looks like an edit.

---

## 6. Work IQ action gate

Copilot CLI tool denial works per tool, not per payload, so it cannot express the tiers above. The
gate is a local MCP server in this repository that stands in front of Work IQ:

- It runs as a **separate OS user** (`margo-gate`) that alone can read Margo's Work IQ token
  state. Copilot runs as `margo`, so the model's shell cannot read the tokens and route around
  the gate.
- Copilot is configured with the gate as its Work IQ server, and the direct Work IQ write tools
  stay denied.
- Reads are forwarded unchanged within budget. Writes are classified from tool, path, method and
  body; the classifier fails closed.
- T1 writes require a directive ID. T2 and T3 writes require an unused exact approval and use the
  ledger's existing claim, re-read and receipt flow. A timeout is recorded as unknown and never
  retried blindly.
- Network egress from the VM is limited to the GitHub, Copilot, Entra, Graph and Work IQ endpoints
  the deployment needs. `curl` from the model can still reach those hosts, but without the gate's
  token it cannot act as Margo.

Whether Work IQ's MCP server can run as a child process of the gate, and how it stores tokens, is a
[Phase 0](#phase-0-platform-verification) question.

---

## 7. Harness lifecycle

**Boot preflight**, in order, stopping at the first failure:

1. Read deployment config and confirm the manager binding exists.
2. Obtain secrets through the managed identity.
3. Confirm Copilot CLI is signed in.
4. Start the gate and confirm Work IQ is signed in as Margo.
5. Confirm delegated read access to the manager's mailbox and calendar.
6. Run `margo_doctor.py` and the schema checks without repairing anything.

**Startup sweep:** a new read-only automation checks Margo's inbox and Teams 1:1 for manager
instructions, then the manager's mail, calendar and Teams mentions, and records source coverage.

**Watch loop:** every few minutes, delta queries pick up new manager directives and changes. The
existing `automations/` schedules run as before. Directives start bounded task runs.

**Health states**, written to a local file and Azure Monitor:

| State | Meaning |
| --- | --- |
| `connected` | Every preflight check passed in the last cycle |
| `degraded` | Some sources are missing or slow; coverage records which |
| `reauth_required` | A token cannot be renewed. Margo stops retrying and waits for the manager |
| `blocked` | Identity mismatch, missing manager binding, schema problem or gate failure |

"Staying connected" means renewing tokens and checking them each cycle. Copilot CLI runs one
prompt per invocation, so there is no single long-lived session to keep open. Health alerts go to
the manager through Azure Monitor, because a broken Margo cannot be relied on to post her own
alert.

---

## 8. Azure infrastructure

Delivered as Bicep, cloud-init and systemd units under `deploy/azure/`:

- Ubuntu LTS VM, system-assigned managed identity, no public IP, Azure Bastion, Entra SSH login.
- RBAC: VM login, Key Vault administration and resource-group writes only for the manager and a
  break-glass account. The managed identity gets Key Vault secret read and nothing else.
- Key Vault with RBAC, purge protection, and no secrets in templates.
- Egress through an allow-list (firewall or NSG with service tags and FQDN rules).
- Encrypted data disk for `/var/lib/margo`, mode 0700, owned by the service users; SQLite backups
  through the backup API.
- Pinned versions of Node, Copilot CLI and Python. Margo installed with `install.sh`. Rebuild to
  update rather than editing in place.
- systemd hardening: `NoNewPrivileges`, `ProtectSystem=strict`, `ProtectHome`, explicit
  `ReadWritePaths`, `Restart=on-failure` with a start limit.

---

## 9. Deployment configuration outside the repository

The owner supplies tenant information at deployment time. It lives in a private parameters file
and on the VM, for example `/etc/margo/deployment.json`, never in the checkout. The repository
ships only a template with `example.com` and placeholder values, and `./tools/check-clean.sh`
must keep passing.

| Value | Where it lives |
| --- | --- |
| Tenant ID, subscription, resource group, region | Private Bicep parameters file |
| Margo's principal and the manager's object ID | VM deployment config and `margo_store.py init` |
| GitHub credential for Copilot | Key Vault |
| Work IQ token state | The gate user's private directory on the encrypted disk |

---

## 10. Phases

### Phase 0: Platform verification

Manual, in the owner's test tenant, separately authorized. Nothing tenant-specific is committed.

| Question | Exit evidence |
| --- | --- |
| Which Entra identity type can sign Margo in to Work IQ without a browser (agent identity with a managed-identity credential, or a dedicated account) | A read-only Work IQ call from the VM after reboot, with renewal and revocation behavior recorded |
| Does Work IQ accept `/users/{manager}` paths with delegated access | Mail, calendar and Teams reads that succeed or fail, recorded per surface |
| Can the Work IQ MCP server run under the gate as a child process, and where are tokens stored | A proxied read and a denied write |
| Which GitHub identity holds the Copilot seat for the VM, and is that within the org's policy | Copilot CLI runs headless with a credential from Key Vault |
| Licensing for Margo's identity and mailbox | Confirmed for the tenant |

If browserless sign-in is impossible, stop and revisit the decision with the owner rather than
copying a human's tokens.

### Phase 1: Manager binding

- `margo_store.py init --account <margo> --manager <manager-object-id>`, a versioned config
  schema with an explicit migration, and `margo_doctor.py` reporting the binding.
- A missing manager keeps local behavior and blocks the remote harness.
- Tests: binding, migration, mismatch, read commands never initialize.

### Phase 2: Harness supervisor

- `skills/chief-of-staff/scripts/remote_harness.py` (shipped with the skill like every other
  deterministic owner; the earlier `tools/harness/` location was dropped because copy-mode installs
  do not ship tools subdirectories): preflight, startup sweep, watch loop, schedules, health file,
  bounded backoff.
- New `automations/startup.md` (read-only).
- Tests with a fake `copilot`, fake metadata endpoint and fake Work IQ.
- Feature catalog entry, how-to guide and scenario land with this phase.

### Phase 3: Manager channels and directives

- Verification for the CLI, Teams 1:1 and email as in [§4](#4-manager-authority), directive and
  standing-rule records, `margo-control` CLI, the new approval evidence prefix and control-channel
  replies.
- Skill and persona updates: the target-principal distinction, directive handling, and non-manager
  requests remaining data.
- Tests: spoofed email, forwarded instruction, group chat, edited message, verification timeout.

### Phase 4: Work IQ action gate

- The gate, tier classifier, separate OS user and Copilot configuration.
- Only now is the [policy change](#what-this-changes-compared-with-the-current-policy) enabled,
  and only for the remote-host profile. `safety.md`, `proactive.md` and the skill's approval rules
  are updated in the same change.
- Tests: every tier, unclassified writes, missing directive, stale approval, uncertain write.

### Phase 5: Azure deployment

- `deploy/azure/` Bicep, cloud-init and systemd units; CI runs `bicep build`, shellcheck and the
  harness tests.

### Phase 6: Operations

- Azure Monitor alerts, runbooks for reauthentication, rotation, revocation, manager change,
  restore and decommissioning.

### Phase 7: Pilot

- In the test tenant: read-only first, then T1, then supervised T2 approvals. Report what was
  actually exercised; a passing fixture is not a live result.

---

## Delivery tracker

Update this table in the same change that delivers or alters a phase.

| Phase | Status | Delivered in |
| --- | --- | --- |
| 0. Platform verification | Tooling delivered (`deploy/azure/scripts/verify-phase0.sh`, evidence template); verification waits for tenant details from the owner | this change |
| 1. Manager binding | Delivered: `margo_store.py init --manager`, `migrate-config`, `rebind-manager`, doctor `binding` | this change |
| 2. Harness supervisor | Delivered: `remote_harness.py`, `automations/startup.md`, health states, cron slots, tests with fake Copilot and gate | this change |
| 3. Manager channels and directives | Delivered: `manager_directives.py` verification matrix and store, `margo_control.py`, `manager-channel:` evidence, `manager-directive:` task requests | this change |
| 4. Work IQ action gate | Delivered: `workiq_gate.py` tiers, directive checks, exact approvals, receipts, peer-credential roles; policy change scoped to the profile | this change |
| 5. Azure deployment | Delivered as templates and units under `deploy/azure/`; compiled in CI, not yet deployed to a tenant | this change |
| 6. Operations | Delivered as runbooks and alert templates; not yet exercised | this change |
| 7. Pilot | Not started; needs Phase 0 evidence first | — |

## Keeping the docs current

Each phase updates, in the same change:

- this plan's tracker and any decision it revisits;
- [trust and safety](safety.md) and [proactive](proactive.md) when behavior or policy changes;
- [autopilot](autopilot.md) and [container](container.md) status notes;
- the [feature catalog](feature-catalog.json), generated [features](features.md), a how-to guide
  and the [architecture map](development/architecture.md) when a runtime owner is added;
- the [changelog](../CHANGELOG.md).

## Open risks

- **Manager account compromise** gives an attacker Margo's authority. Conditional access and MFA
  on the manager's account are prerequisites, not options.
- **Delegated mailbox access is broad.** Full Access lets Margo read everything the manager can.
  Scope it to what the routines need where Exchange allows.
- **Prompt injection gets a direct line.** Anyone in the tenant can email Margo. The verification
  rules and the gate are the defence; the persona's judgement is not.
- **T1 actions can still surprise.** Filing a message the manager was about to read is reversible
  but noticeable. Standing rules are listed in every brief so the manager sees what runs.
- **Platform availability.** Agent identities and Work IQ identity modes may change. Phase 0
  evidence is dated and rechecked before each phase that depends on it.
