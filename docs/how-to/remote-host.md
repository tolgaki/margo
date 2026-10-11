# Run Margo on a remote host with her own identity and one manager

[How-to index](README.md) · [Remote-host plan](../remote-host-plan.md) ·
[Trust and safety](../safety.md#8-the-remote-host-profile) ·
[Deployment templates](../../deploy/azure/README.md)

## What this helps you do

Keep Margo running on an always-on Azure VM, signed in to Work IQ as her own Entra identity,
reading one bound manager's mailbox and calendar through delegated access, taking instructions
from that manager through verified channels, and performing writes under a tiered policy that a
local gate enforces. Local installs are unaffected: this profile exists only where a manager is
bound and the gate is installed.

| You get | You still decide |
| --- | --- |
| A startup sweep and scheduled routines that keep running while your laptop is closed | Every send, reply, post, RSVP, invite, share, delete or cancellation, per exact proposal |
| Private, reversible tidying (flag, file, draft, private hold) on your instruction or standing rule | Which standing rules exist; each is listed in every brief and can be revoked |
| Approval by replying `approve MA-xxxxxxxx` in your 1:1 chat, by email, or on the host | Who the manager is: one Entra object ID, changed only on the host |

## Before you start

- Complete [Phase 0 verification](../../deploy/azure/README.md#phase-0-verification) in a test
  tenant: browserless Work IQ sign-in for Margo's identity, delegated reads of the manager's
  mailbox and calendar, and the gate proxying a read while refusing an unapproved write. Nothing
  below is proven until that evidence exists for your tenant.
- Keep tenant, subscription and object IDs out of the repository. They live in the private
  parameters file and in `/etc/margo/deployment.json` on the host.
- MFA and conditional access on the manager's account are prerequisites: the manager's identity
  is Margo's only authority.

## Try it

### Manager binding

On the host, as the service user, bind the account and the manager once:

```sh
python3 skills/chief-of-staff/scripts/margo_store.py init --account margo@example.com \
  --manager MANAGER_OBJECT_ID --manager-principal dana@example.com --profile remote-host
python3 skills/chief-of-staff/scripts/margo_doctor.py | python3 -c 'import json,sys; print(json.load(sys.stdin)["binding"])'
```

Doctor reports `bound`, `unbound` or `migration-required` and never prints the identifiers. An
existing version-1 config is upgraded only by an explicit `migrate-config`; `init` refuses to
rewrite it. A different manager is refused; use `rebind-manager` with the current manager's id.
A config newer than this Margo understands reports `migration-required`; invalid JSON or a file
that is not private is an error (exit 2) and is never rewritten. `init --manager` without
`--profile` defaults to `remote-host`.

### Remote harness

The harness runs as a systemd service. Its boot preflight stops at the first failure and writes
`/var/lib/margo/health.json`:

| Check | Fails as |
| --- | --- |
| Deployment file loaded, profile `remote-host` | `blocked` |
| Manager bound and equal to the deployment file | `blocked` |
| Copilot CLI signed in | `blocked` |
| Gate reachable, Work IQ signed in as Margo | `blocked`, or `reauth_required` when a token cannot be renewed |
| Delegated reads of the manager's inbox and calendar | `degraded` |
| Doctor reports the binding | `blocked` |

Then it runs the startup sweep (`automations/startup.md`), and every few minutes syncs manager
directives, runs each new instruction as a bounded Copilot session (one hour limit; a timeout is
reported to you as unknown), and runs the scheduled automations on their cron slots.
`reauth_required` is waited out, never retried in a loop. While you have paused Margo, no session
starts; a new instruction is answered with a note asking you to re-send it after `resume`. The
health file lives at the deployment's `harness.health_path` (default `/var/lib/margo/health.json`).

> "What did you check at startup?" — the sweep's publication receipt and per-source coverage
> answer this; `python3 skills/chief-of-staff/scripts/remote_harness.py health` prints the file.

### Manager directives

Message Margo in your 1:1 chat or by email:

> file the vendor newsletters into Reading from now on

The gate verifies the message is yours (1:1 chat and your Entra id; or your address, passing
authentication results, and the same message in your Sent Items), records it, and the harness
runs it. "always ..." or "rule: ..." creates a standing rule; `revoke rule rule_...` ends it.
Quoted or forwarded text is never read as an instruction; anyone else's message is just mail.

The phrases the gate understands, at the start of your message: `approve MA-xxxxxxxx` (or
`ok`/`yes`), `reject MA-xxxxxxxx`, `always ...` or `rule: ...`, `revoke rule rule_...`, and
`pause` / `resume` on their own. Anything else is an instruction.

On the host, with your Entra SSH sign-in (`--socket` goes before the subcommand when
`MARGO_GATE_SOCKET` is not set):

```sh
python3 skills/chief-of-staff/scripts/margo_control.py directives --state active
python3 skills/chief-of-staff/scripts/margo_control.py rules
python3 skills/chief-of-staff/scripts/margo_control.py pause
```

### Work IQ action gate

Every Work IQ call from Copilot goes through the gate (`workiq-gate` MCP server). Reads pass.
Private reversible writes need the directive or rule that asked for them. Communicating and
destructive writes need an approved action desk proposal:

> Margo: "Reply to Rafa confirming Thursday — approval reference MA-1a2b3c4d, recipient
> rafa@example.com, text as drafted."
>
> You: `approve MA-1a2b3c4d`

The gate re-reads the target, claims one execution, forwards the exact call, and records the
receipt. A second attempt is refused. A timeout is recorded as `outcome_unknown` and never
retried blindly; the manager is told to check the Sent Items before approving again.

```sh
python3 skills/chief-of-staff/scripts/margo_control.py pending
python3 skills/chief-of-staff/scripts/margo_control.py approve MA-1a2b3c4d
python3 skills/chief-of-staff/scripts/workiq_gate.py classify --tool do_action \
  --arguments '{"path": "/users/dana@example.com/messages/ID/move", "method": "POST", "body": {"destinationId": "deleteditems"}}'
```

### Azure deployment

`deploy/azure/` holds Bicep, cloud-init, systemd units and operator scripts. Deployment, Phase 0
verification, runbooks (re-authentication, rotation, revocation, manager change, restore,
decommission) and the pilot checklist are in [its README](../../deploy/azure/README.md) and
[RUNBOOKS](../../deploy/azure/RUNBOOKS.md). CI compiles the templates; it cannot exercise a tenant.

## What you will see

| State | Meaning |
| --- | --- |
| `connected` | Identity confirmed as Margo, delegated reads work, gate and ledger healthy |
| `degraded` | Some sources missing or slow; coverage records which |
| `reauth_required` | A token cannot be renewed; Margo waits for you |
| `blocked` | Identity mismatch, missing binding, schema problem or gate failure; nothing runs |
| directive `active` → `completed` | Verified instruction, then its run finished and a note was sent |
| action `ready` → `approved` → `succeeded` / `outcome_unknown` | Proposal, your approval, the gate's receipt |

## What needs your decision

- Every T2/T3 action, by exact reference. A reference changes whenever the proposal changes.
- Every standing rule you create, and when to revoke it.
- Re-authentication, manager changes, restores and decommissioning: all on the host, none by chat.

## Change your mind

`reject MA-xxxxxxxx` dismisses a proposal. `revoke rule rule_...` stops a rule. `pause` stops the
harness from starting new work; `resume` continues. None of these undo an effect already
recorded; the effects journal shows what a rule changed so you can reverse it deliberately.

## Your data

Margo's private state (ledger, directives, coverage, receipts) lives on the encrypted data disk
under `/var/lib/margo` (`COPILOT_HOME` is `/var/lib/margo/copilot`). Work IQ tokens live under
`/var/lib/margo-gate`, readable only by the gate service through its supplementary group. The
gate and harness run from the root-owned pinned checkout at `/opt/margo`; the deployment anchor
is `/etc/margo/deployment.json`; the socket is `/run/margo/gate.sock`. The audit log records
decisions and digests, never message bodies. Backups use the SQLite backup API with writers
stopped (`deploy/azure/scripts/backup-state.sh`, daily timer, kept on the host unless you add an
off-host copy). Tokens are never backed up.

## If something goes wrong

| Problem | Safe response |
| --- | --- |
| Health says `reauth_required` | Follow the re-authentication runbook on the host; do not copy tokens from a laptop |
| Health says `blocked` with an identity mismatch | Stop. Confirm which identity Work IQ is signed in as before anything else runs |
| A message you sent was listed as "could not verify" | Check it came from your own account to Margo's address, not forwarded; re-send rather than relaxing verification |
| The gate refused a write as T3 | That is the design. Propose it; approve it by reference if you really want it |
| An execution is `outcome_unknown` | Check the target (Sent Items, the event) before approving a new attempt |
| You need a different manager | Update `/etc/margo/deployment.json` as an operator, then `rebind-manager` with the current id |

## Availability and limits

- Limited: every step above is deterministic and tested with synthetic Work IQ responses, but
  the exact Work IQ tool and argument shapes, Entra agent identity sign-in, licensing and Teams
  delegated coverage must be confirmed per tenant in Phase 0. The deployment config carries the
  call templates so they can be corrected without code changes.
- The gate enforces policy for every call that goes through it. `curl` from a shell can still
  reach the network, but without the gate's tokens it cannot act as Margo. Copilot sessions and
  the gate share the service user; the token directory's group is the boundary, and a tampered
  MCP configuration is detected by the verification script, not prevented.
- A dead Work IQ child is not respawned: health reports `blocked` until systemd restarts the
  gate. Peer-credential roles need Linux.
- Delegated mailbox access is broad. Scope it where Exchange allows.
- No unattended run ever sends to anyone but the manager, and only through the control channel.

## Advanced reference

```sh
# gate and harness, as the service user
python3 skills/chief-of-staff/scripts/workiq_gate.py check-config --deployment /etc/margo/deployment.json
python3 skills/chief-of-staff/scripts/workiq_gate.py serve --deployment /etc/margo/deployment.json
python3 skills/chief-of-staff/scripts/remote_harness.py preflight
python3 skills/chief-of-staff/scripts/remote_harness.py run --once
python3 skills/chief-of-staff/scripts/remote_harness.py schedule --list

# binding maintenance
python3 skills/chief-of-staff/scripts/margo_store.py migrate-config --account margo@example.com \
  --manager MANAGER_OBJECT_ID --manager-principal dana@example.com --profile remote-host
python3 skills/chief-of-staff/scripts/margo_store.py rebind-manager --account margo@example.com \
  --current-manager OLD_OBJECT_ID --manager NEW_OBJECT_ID --manager-principal NEW_PRINCIPAL
```

## Feature reference

### Manager binding

Bind exactly one manager to Margo's account, with a versioned private config and explicit
migration — see [Try it](#manager-binding). Try it: `margo_store.py init --manager ...`. What
you'll see: doctor reporting `bound` without printing ids; `init` refusing a different manager or
an implicit upgrade. What needs your decision: who the manager is; changing it is an operator act
on the host. Change your mind: `rebind-manager` with the current manager's id. Your data: the
config file is private (0600) and holds identifiers, not credentials. If something goes wrong: a
malformed or newer config is reported as needing explicit migration, never rewritten. Implemented,
runtime (versioned config with tests). Since 1.3.0.

### Remote harness

Keep Margo alive on a remote host: ordered boot preflight, startup sweep, directive runs,
cron-scheduled automations and a truthful health file — see [Try it](#remote-harness). Try it:
`remote_harness.py preflight`. What you'll see: `connected`, `degraded`, `reauth_required` or
`blocked`, with the failing check named. What needs your decision: re-authentication and
anything `blocked`. Change your mind: `pause` and `resume` through the control CLI. Your data: the
health file and audit log hold states and digests, not message content. If something goes wrong:
the harness exits with code 3 when blocked so systemd does not restart it into a loop. Limited,
runtime (deterministic supervisor tested with a fake Copilot and gate; the Copilot and Work IQ
sign-in flows need tenant verification). Since 1.3.0.

### Manager directives

Take instructions only from the bound manager through verified Teams, email and CLI channels,
with standing rules and an effects journal — see [Try it](#manager-directives). Try it: message
Margo "file the vendor newsletters into Reading from now on". What you'll see: a directive or
rule id, a completion note, and unverifiable messages reported as such. What needs your decision:
which rules exist. Change your mind: `revoke rule`. Your data: directives and effects live in the
private account database. If something goes wrong: a spoofed sender, a forwarded instruction, a
group chat or an edited message is rejected with a reason and never acted on. Implemented, runtime
(verification matrix and store tested with synthetic Graph data). Since 1.3.0.

### Work IQ action gate

Enforce the action tiers for every Work IQ call: reads pass, private reversible writes need a
directive or rule, communicating and destructive writes need an exact approved proposal and
produce a receipt — see [Try it](#work-iq-action-gate). Try it: `workiq_gate.py classify`. What
you'll see: refusals that name the tier and reason; one execution per approved revision;
`outcome_unknown` on timeouts. What needs your decision: every T2/T3 action by reference. Change
your mind: edit or reject the proposal; the reference changes. Your data: the audit log stores
digests, not bodies. If something goes wrong: an unclassifiable write is T3; a failed pre-read
refuses the T1 write. Limited, runtime (policy and proxy tested against a scripted Work IQ; live
tool shapes need Phase 0 confirmation). Since 1.3.0.

### Azure deployment

Provision the VM, identity, vault, network, disk, monitoring and services from templates, with
runbooks for the rest of the lifecycle — see [Try it](#azure-deployment). Try it: follow
`deploy/azure/README.md`. What you'll see: a CI job that compiles the templates and checks the
units and placeholders. What needs your decision: tenant details in a private parameters file,
the manager's VM login, and the pilot stages. Change your mind: the decommission runbook. Your
data: never in the repository. If something goes wrong: CI proves the templates compile, not
that a tenant accepts them; keep the Phase 0 evidence file. Optional, runtime (template contract
tests; deployment itself needs a tenant). Since 1.3.0.
