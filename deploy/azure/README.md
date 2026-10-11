# Margo on an Azure VM: deployment templates

[Operator guide](../../docs/how-to/remote-host.md) ·
[Plan and decisions](../../docs/remote-host-plan.md) · [Runbooks](RUNBOOKS.md) ·
[Trust and safety](../../docs/safety.md) · [Contribution rules](../../CONTRIBUTING.md)

Everything needed to provision and operate the always-on host from the
[remote-host plan](../../docs/remote-host-plan.md): Bicep for the Azure resources, cloud-init
for the first boot, systemd units for the gate, the harness and the backup, and operator scripts
for verification, backup, re-authentication and decommissioning. Nothing in this directory names
a tenant. The owner supplies tenant details in `main.local.bicepparam` and a filled
`deployment.json`, both gitignored.

**Status:** templates compile and the units verify in CI; no tenant has run them yet. Treat every
"it works" below as "it is designed to"; the [evidence table](#phase-0-evidence-record) is where
a real result goes.

## What is here

| Path | Purpose |
| --- | --- |
| `main.bicep` | VM (Ubuntu 24.04 LTS, system-assigned identity, no public IP, Trusted Launch), data disk, NSG, NAT gateway or optional firewall, Bastion, Key Vault, role assignments, monitoring module |
| `modules/firewall.bicep` | Optional Azure Firewall with an FQDN allow-list; the only real egress allow-list |
| `modules/monitoring.bicep` | Log Analytics, syslog collection, action group, VM availability, heartbeat and harness-error alerts |
| `main.example.bicepparam` | Template for the private parameters file; every tenant value is a `{placeholder}` |
| `cloud-init.yaml` | First boot: writes `/etc/margo/bootstrap.env` (names and versions only), clones the pinned revision to `/opt/margo`, runs `bootstrap.sh` |
| `deployment.example.json` | Template for the deployment anchor the gate and harness read; uploaded to Key Vault, never committed filled in |
| `systemd/margo-gate.service` | The Work IQ action gate, user `margo` plus group `margo-gate`, hardened |
| `systemd/margo-harness.service` | The harness, user `margo` only, `Requires=margo-gate.service`, exit 3 is never restarted |
| `systemd/margo-backup.service`, `.timer` | Daily SQLite backup with the harness stopped |
| `scripts/bootstrap.sh` | Root, idempotent provisioning; ends by running `verify-phase0.sh` |
| `scripts/verify-phase0.sh` | The Phase 0 checklist as checks; writes evidence JSON under `/var/lib/margo/evidence/` |
| `scripts/backup-state.sh`, `reauth.sh`, `decommission.sh` | Operations; see [RUNBOOKS](RUNBOOKS.md) |
| `scripts/lib.sh` | Shared helpers: logging, bootstrap.env parsing, IMDS token, Key Vault REST |

## What is verified and what is assumed

| Claim | How it is checked | Where it is not |
| --- | --- | --- |
| Templates compile and parameters match | CI `deploy-templates` job (`az bicep build`, `build-params`); `tests/test_deploy_templates.py` | Compiling is not deploying. A tenant may still reject a SKU, a region or a policy |
| Units are syntactically valid and hardened | `systemd-analyze verify` in CI with any output treated as failure; directive assertions in the tests | Behaviour under `ProtectSystem=strict` with the real Copilot and Work IQ binaries is a Phase 0 result |
| Example files hold placeholders only | CI step plus `./tools/check-clean.sh` (GUIDs, addresses, home paths) | A filled private copy is the owner's responsibility; it is gitignored so it cannot be committed by accident |
| Scripts parse and are warning-free | `bash -n` in CI, ShellCheck at severity warning | They are not executed in CI: they need root, systemd, a managed identity and a vault |
| Copilot talks to Work IQ only through the gate | The MCP writer in `bootstrap.sh` is executed by the tests against fresh, merged and tampered configs | Whether this Copilot version honours `COPILOT_HOME` and `mcp-config.json` as assumed is a Phase 0 result |
| Work IQ sign-in without a browser, token location, delegated reads, Teams coverage, licensing, GitHub seat policy | Not checkable here | Phase 0, by hand, recorded in the [evidence table](#phase-0-evidence-record) |

## Before you deploy

1. Read [§1 Decisions](../../docs/remote-host-plan.md#1-decisions-recorded) and
   [Phase 0](../../docs/remote-host-plan.md#phase-0-platform-verification) of the plan. The templates
   assume those decisions; if Phase 0 overturns one (for example, browserless sign-in is
   impossible), stop and revisit the plan rather than working around it here.
2. Have ready, outside the repository: the subscription and resource group, Margo's Entra identity
   (principal and object id, with a mailbox and the licences the tenant needs), the manager's object
   id, delegated access on the manager's mailbox and calendar granted by an Exchange administrator,
   the Teams 1:1 chat id between Margo and the manager (or decide on email only), and a GitHub
   credential that holds a Copilot seat Margo may use under your organisation's policy.
3. MFA and conditional access on the manager's account. The manager's identity is Margo's only
   authority; this is a prerequisite, not an option.
4. The Azure CLI signed in as someone who can create the resource group's resources and role
   assignments (Owner or User Access Administrator plus Contributor).

## Deploy

1. **Parameters.** Copy the template and fill every `{placeholder}`:

   ```sh
   cp deploy/azure/main.example.bicepparam deploy/azure/main.local.bicepparam
   # Role definition ids are Azure's built-in GUIDs; parameters, so none is in the repository.
   az role definition list --name "Virtual Machine Administrator Login" --query "[].name" -o tsv
   az role definition list --name "Key Vault Secrets Officer" --query "[].name" -o tsv
   az role definition list --name "Key Vault Secrets User" --query "[].name" -o tsv
   ```

   `repoRevision` is the 40-character commit the host runs; `copilotVersion` is the
   `@github/copilot` version to pin; `nodeVersion` defaults to a Node 22 release. Set `nodeSha256`
   to the published tarball checksum for your architecture if you want the download verified
   independently of nodejs.org (bootstrap otherwise warns that only same-origin verification ran).

2. **Resources.**

   ```sh
   az group create --name rg-margo-example --location eastus
   az deployment group create --resource-group rg-margo-example \
     --template-file deploy/azure/main.bicep --parameters deploy/azure/main.local.bicepparam
   ```

   Outputs: `principalId` (the VM identity), `keyVaultUri`, `vmId`, `egressPublicIp`. With
   `deployFirewall = true`, compare `routeNextHopIp` with `firewallPrivateIp`; they must be equal.

3. **Deployment anchor.** Copy `deployment.example.json` to `deployment.local.json`, fill the
   placeholders (both object ids, the Teams chat id, `control.cli_logins` with the manager's VM
   login names, the Work IQ package version) and upload it as a secret. The host validates it with
   the shipped loader before installing it; a leftover placeholder is refused.

   ```sh
   az keyvault secret set --vault-name kv-margo-example --name margo-deployment \
     --file deploy/azure/deployment.local.json
   ```

4. **Copilot credential (optional file).** An environment file with `KEY=value` lines that give
   Copilot CLI its GitHub credential, installed as `/etc/margo/copilot.env` (root:margo, 0640).
   Which variable Copilot CLI reads is a Phase 0 fact for the version you pinned; the host does not
   assume one.

   ```sh
   az keyvault secret set --vault-name kv-margo-example --name margo-copilot-env \
     --file copilot.local.env
   ```

   Skip this if the Copilot seat is provided another way; `verify-phase0.sh` then reports
   `copilot_check` honestly.

5. **First boot.** cloud-init installs packages, clones the pinned revision to `/opt/margo` and runs
   `bootstrap.sh`, which waits up to 20 minutes for the deployment secret. Follow it through Bastion:

   ```sh
   az network bastion ssh --name vm-margo-example-bastion --resource-group rg-margo-example \
     --target-resource-id "$VM_ID" --auth-type AAD
   sudo tail -f /var/log/cloud-init-output.log
   ```

   Expect the final verification to report `gate_health`, `workiq_identity` and `delegated_reads`
   as failed on a first boot: Work IQ is not signed in yet. That is the honest state, and
   bootstrap exits non-zero to say so.

6. **Sign Work IQ in** with `sudo /opt/margo/deploy/azure/scripts/reauth.sh`
   ([runbook](RUNBOOKS.md#re-authentication)). The sign-in runs under the gate's identity context so
   the tokens land in `/var/lib/margo-gate`.

7. **Verify and record.** `sudo /opt/margo/deploy/azure/scripts/verify-phase0.sh` must print
   `result: PASS`. Copy its table into the [evidence record](#phase-0-evidence-record) along with
   the manual findings.

Re-running `bootstrap.sh` is safe at any time; every step checks before it changes. A different
revision is a rebuilt host, not an in-place edit (see [Upgrade](RUNBOOKS.md#upgrade)).

## Host layout

| Path | Owner, mode | Holds |
| --- | --- | --- |
| `/opt/margo` | root, read-only to others | The pinned checkout; the gate and harness run from here |
| `/etc/margo/bootstrap.env` | root, 0644 | Names and versions from the parameters, never secrets |
| `/etc/margo/deployment.json` | root, 0644 | The deployment anchor, read by gate and harness |
| `/etc/margo/copilot.env` | root:margo, 0640 | Copilot CLI credential environment, loaded only by the harness unit |
| `/var/lib/margo` | margo, 0750, data disk | Service home: `copilot/` (COPILOT_HOME: agent, skill, automations, `mcp-config.json`, `margo/config.json`, `margo/state/`), `health.json`, `gate-audit.jsonl`, `evidence/` |
| `/var/lib/margo-gate` | root:margo-gate, 0770 | Work IQ token state; HOME of the gate unit; unreadable to the harness and Copilot sessions |
| `/var/lib/margo-backups` | root, 0700 | Backups; the service user cannot read, rename or delete them |
| `/run/margo/gate.sock` | created by the gate unit | The gate socket; peer credentials decide the caller's role |

The whole boundary between the model's shell and Margo's tokens is that `margo` is not a member of
`margo-gate`; only `margo-gate.service` receives the group through `SupplementaryGroups`.
`bootstrap.sh` and `verify-phase0.sh` both refuse a host where that membership exists.

## Phase 0 verification

`verify-phase0.sh` turns the plan's Phase 0 table into checks. Required checks fail the run; optional
ones are reported. Evidence is written to `/var/lib/margo/evidence/<timestamp>.json` with statuses
and versions only, never identifiers or tokens, and stays on the host.

| Check | Required | What a pass proves |
| --- | --- | --- |
| `imds_identity` | yes | The VM's managed identity issues a Key Vault token |
| `vault_deployment_secret`, `vault_copilot_secret` | yes / no | The identity can read the secrets (RBAC in place) |
| `deployment_file` | yes | `/etc/margo/deployment.json` is root 0644 and passes the strict loader |
| `python_version`, `node_version`, `copilot_version` | yes | Python ≥ 3.9; Node and `@github/copilot` are the pinned versions |
| `copilot_check` | yes | `harness.copilot_check` exits 0 as `margo` with the credential file loaded |
| `mcp_config` | yes | Copilot's MCP config is root-owned, registers `workiq-gate` and no direct Work IQ server |
| `token_dir` | yes | `/var/lib/margo-gate` is root:margo-gate 0770 and `margo` is not in the group |
| `checkout_pinned` | yes | `/opt/margo` is root-owned at the pinned commit |
| `unit_margo-gate`, `unit_margo-harness`, `unit_margo-backup`, `gate_socket` | yes | Units active and enabled, socket present |
| `gate_health` | yes | `margo_control.py health` reports `connected` (not `degraded`, `reauth_required` or `blocked`) |
| `workiq_identity` | yes | Work IQ is signed in as the configured Margo principal, per the gate's `/me` probe |
| `delegated_reads` | yes | The manager's inbox and calendar are readable through `/users/{manager}` paths |
| `classifier` | yes | `workiq_gate.py classify` places `sendMail` at T2 and `fetch` at T0 |
| `harness_health_file` | no | `/var/lib/margo/health.json` says `connected` |
| `egress_graph`, `egress_github` | no | Graph and the GitHub API answer from the VM |

What the script cannot know, and the owner records by hand:

| Phase 0 question | Evidence to record |
| --- | --- |
| Which identity type signs in without a browser | The flow used by `reauth.sh` (`MARGO_WORKIQ_LOGIN_COMMAND`), renewal across a reboot, behaviour after revocation |
| Where the Work IQ child stores tokens | The path under `/var/lib/margo-gate`; if it is elsewhere, the gate unit's `HOME` must change, not the boundary |
| Delegated surfaces | Mail, calendar and Teams reads per `/users/{manager}` path, including the ones that fail |
| A proxied read and a refused write | The gate audit log lines for one `fetch` and one unapproved `do_action` |
| Copilot seat and policy | Which GitHub identity holds it, and the policy statement that allows it |
| Licensing | Confirmed for Margo's mailbox and Work IQ |

### Phase 0 evidence record

Keep this table in the private operations notes, not in the repository. Dates matter: the plan says
Phase 0 evidence is rechecked before each phase that depends on it.

| Date | Tenant (private name) | Revision | Check or question | Result | Evidence file or note |
| --- | --- | --- | --- | --- | --- |
| {date} | {test tenant} | {commit} | `verify-phase0.sh` | PASS / FAIL (`n` required failed) | `/var/lib/margo/evidence/{timestamp}.json` |
| {date} | {test tenant} | {commit} | Browserless sign-in | identity type, flow | {note} |
| {date} | {test tenant} | {commit} | Token location | path | {note} |
| {date} | {test tenant} | {commit} | Delegated surfaces | mail ok / calendar ok / teams {result} | {note} |
| {date} | {test tenant} | {commit} | Proxied read, refused write | audit refs | {note} |
| {date} | {test tenant} | {commit} | Copilot seat, licensing | {result} | {note} |

## Pilot checklist

Three stages, in order, in the test tenant. Do not start a stage until the previous one has run
for the stated period without an unexplained entry in the audit log. Report what was actually
exercised; a passing fixture is not a live result.

| Stage | Gate policy exercised | Enter when | Watch | Exit when |
| --- | --- | --- | --- | --- |
| 1. Read-only | T0 only; no directives, no standing rules | Phase 0 evidence recorded and dated | Startup sweep and scheduled routines complete; coverage per source; health stays `connected` across a reboot; every `do_action`/`create_entity` attempt appears in the audit log as refused | Five working days with no refusal that should have been allowed and no outward effect |
| 2. T1 under instruction | Private reversible writes with a directive or a standing rule | Stage 1 exit; the manager sends one instruction through each channel (CLI, Teams 1:1, email) | `directive_effects` show before and after for each change; a spoofed, forwarded and quoted instruction are each refused; one rule is revoked and stops taking effect | Ten working days; every effect reversible from the journal; verification refusals explained to the manager |
| 3. Supervised T2 | Communicating actions by exact approval | Stage 2 exit; the manager is available during each attempt | One send to the manager's own address, approved by reference; an edited proposal invalidates the approval; a second execution is refused; one timeout is recorded as `outcome_unknown` and not retried | Two weeks; receipts complete; no message to anyone but the manager unless approved by reference |

T3 (delete, cancel, permissions, inbox rules) is not part of the pilot. It stays approval-only and is
exercised, if at all, on a disposable mailbox after the pilot report.

## What CI validates and what needs a tenant

| CI validates (`deploy-templates` job and `tests/test_deploy_templates.py`) | Needs a tenant |
| --- | --- |
| `az bicep build` of every template and `build-params` of the example | The deployment itself: quotas, SKU availability, policy, feature registration (`encryptionAtHost`) |
| `systemd-analyze verify` of every unit with any output failing the job; required hardening directives | The units running the real Copilot and Work IQ processes under `ProtectSystem=strict` |
| Example files are templates (placeholders in every identity field); no GUID-shaped literal anywhere under `deploy/` | The filled private files, which never enter the repository |
| `bash -n` and ShellCheck (warning) on every script; scripts are executable with `set -euo pipefail` | Running them: root, systemd, IMDS, Key Vault, the data disk |
| The MCP config writer registers `workiq-gate` and refuses a direct Work IQ server, executed against fresh, merged and tampered configs | Whether the pinned Copilot CLI reads `mcp-config.json` from `COPILOT_HOME` as assumed |
| Links and anchors in these documents resolve | Everything in the [Phase 0 table](#phase-0-verification) and the pilot |

## Security notes and limits

- **Same uid for gate and Copilot.** Per the design, both the gate and the harness (and so every
  Copilot session) run as `margo`; the token directory is the boundary, enforced by the
  supplementary group only the gate unit has. Processes of one uid can signal each other, so a
  model-driven shell could stop the gate; it cannot read its tokens, and Ubuntu's default
  `ptrace_scope` keeps it out of the gate's memory. The gate and harness code runs from the
  root-owned checkout, not from the writable install tree.
- **`mcp-config.json` is in a directory the service user owns.** It is root-owned so it cannot be
  edited in place, and `verify-phase0.sh` fails if it is replaced, but a shell as `margo` could
  rename it. A direct Work IQ server registered that way has no tokens and cannot act as Margo; the
  check exists so tampering is noticed, not because it is the boundary.
- **Egress is not an allow-list without the firewall.** NSG service tags cannot name GitHub, npm or
  nodejs.org, so the default deployment uses a NAT gateway with unrestricted outbound; new virtual
  networks have no default outbound access at all, which is why the NAT gateway exists. Set
  `deployFirewall = true` for a real allow-list and add Work IQ's endpoints to `firewallExtraFqdns`
  from Phase 0 observations. `restrictOutboundToServiceTags` without the firewall breaks
  provisioning and Copilot; it is there for a measured steady state only.
- **Disk encryption.** Managed disks are encrypted at rest with platform keys by default.
  `diskEncryptionSetId` (customer-managed keys) and `encryptionAtHost` (needs the subscription
  feature) are the stronger options; `shred` in `decommission.sh` is best effort on a managed disk,
  and deleting the disk plus revoking the identity is what counts.
- **Identifiers on command lines.** `bootstrap.sh` passes the manager's object id to
  `margo_store.py init` as an argument, visible in `ps` for a moment to other local users. They
  are identifiers, not credentials, and the only other local users are the manager's logins.
- **The harness logs at syslog severity.** `remote_harness.py` prefixes fatal lines with `<3>`, which
  journald records as `err`; the harness-error alert in `modules/monitoring.bicep` depends on that
  convention and on rsyslog forwarding the journal, which Ubuntu does by default.
- **Alerts are email.** The action group emails `alertEmail`; a broken Margo cannot post her own
  alert, so this path must not depend on her.
