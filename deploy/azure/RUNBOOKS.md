# Runbooks for the Margo host

[Deployment README](README.md) · [Operator guide](../../docs/how-to/remote-host.md) ·
[Plan](../../docs/remote-host-plan.md) · [Trust and safety](../../docs/safety.md)

Every procedure runs on the host through Bastion, as the manager (Entra SSH login or the
break-glass key), with `sudo` where the step says root. Nothing here can be done from Teams or email,
by design. Scripts live in `/opt/margo/deploy/azure/scripts/`; each prints its own `--help`.

## Health states and first response

| State (`margo_control.py health`, `health.json`) | Meaning | First response |
| --- | --- | --- |
| `connected` | Identity confirmed as Margo, delegated reads work, gate, ledger and directives healthy | None |
| `degraded` | Delegated reads partially failing; coverage records which source | Check Exchange delegation on the manager's mailbox; wait one cycle; do not relax anything |
| `reauth_required` | A token cannot be renewed; the harness waits and never retries sign-in | [Re-authentication](#re-authentication) |
| `blocked` | Identity mismatch, missing manager binding, schema problem or gate failure; harness exits 3 and stays down | Read `journalctl -u margo-harness -u margo-gate`; an identity mismatch means stop and confirm who Work IQ is signed in as before anything else runs |

Useful commands, as the manager:

```sh
sudo systemctl status margo-gate margo-harness margo-backup.timer
python3 /opt/margo/skills/chief-of-staff/scripts/margo_control.py status
python3 /opt/margo/skills/chief-of-staff/scripts/margo_control.py health
sudo journalctl -u margo-harness --since -1h
sudo tail -n 50 /var/lib/margo/gate-audit.jsonl
```

## Re-authentication

When: health is `reauth_required`, or after a password reset, conditional-access change or token
revocation on Margo's identity.

1. `sudo /opt/margo/deploy/azure/scripts/reauth.sh`. It stops the harness and the gate, then
   either runs the sign-in command recorded as `MARGO_WORKIQ_LOGIN_COMMAND` in
   `/etc/margo/bootstrap.env`, or opens a shell as uid `margo`, group `margo-gate`,
   `HOME=/var/lib/margo-gate` and tells you to run the flow your Phase 0 evidence recorded.
2. Sign in **as Margo's identity**, never your own. The gate compares the signed-in object id with
   the deployment anchor; your identity would make the harness `blocked`.
3. The script starts the gate, prints the health status, and starts the harness only when the gate
   is `connected` or `degraded`. Anything else leaves the harness down and exits non-zero.
4. `sudo /opt/margo/deploy/azure/scripts/verify-phase0.sh` and keep the evidence file reference.

Never copy token files from a laptop or another host: Margo would act as whoever owns them.

## Rotation

| What | How | Then |
| --- | --- | --- |
| Copilot credential | Update the secret: `az keyvault secret set --vault-name {vault} --name margo-copilot-env --file copilot.local.env` | `sudo /opt/margo/deploy/azure/scripts/bootstrap.sh` (re-installs `/etc/margo/copilot.env`), `sudo systemctl restart margo-harness` |
| Deployment anchor (chat id, call templates, rate limits) | Edit the private copy, upload as `margo-deployment` | `bootstrap.sh`, then `sudo systemctl restart margo-gate margo-harness`. Changing the manager is a separate runbook below |
| Manager's break-glass SSH key | Redeploy with a new `managerSshPublicKey` | Entra SSH login is unaffected |
| Node or Copilot version | New `nodeVersion`, `nodeSha256`, `copilotVersion` in the parameters file | Rebuild the host ([Upgrade](#upgrade)); do not edit in place |
| Work IQ sign-in | Revoke the old session in Entra | [Re-authentication](#re-authentication) |

Old secret versions stay in the vault's history; disable them after the rotation has verified.

## Revocation

Emergency stop, in order of speed:

1. `python3 /opt/margo/skills/chief-of-staff/scripts/margo_control.py pause`: the harness starts no
   new work; the gate still answers health and the manager's CLI. Reversible with `resume`.
2. `sudo systemctl stop margo-harness margo-gate`: nothing runs; tokens remain on disk.
3. In Entra, revoke sessions for Margo's identity (and disable it if needed). Every renewal fails
   from then on; the host reports `reauth_required` once restarted.
4. In Exchange, remove the delegated access on the manager's mailbox if the concern is data access
   rather than Margo's behaviour.

Standing rules: `margo_control.py rules`, then `margo_control.py revoke-rule {id}`. Revoking stops
future use; it does not undo past effects. The effects journal (`directive_effects`) records before
and after for each T1 change so a deliberate reversal is possible.

## Manager change

The manager is bound by immutable object id in two places, and changing it is an operator act on
the host; it can never be done from Teams or email.

1. Confirm the new manager's object id, VM login name and mailbox delegation out of band.
2. Update the private deployment anchor: `manager.object_id`, `manager.principal`,
   `manager.display_name`, `control.cli_logins`, `control.teams_chat_id` (a new 1:1 chat), and the
   `gate.manager_root_paths` entries. Upload it as `margo-deployment`.
3. Redeploy the Bicep with the new `adminObjectId` so the VM login and vault roles move. Remove the
   old assignments if the template did not (it adds, it does not prune).
4. Rebind the store as the service user, with the currently bound id as proof:

   ```sh
   sudo -u margo -H env COPILOT_HOME=/var/lib/margo/copilot \
     python3 /opt/margo/skills/chief-of-staff/scripts/margo_store.py rebind-manager \
     --account {margo-principal} --current-manager {old-object-id} --manager {new-object-id} \
     --manager-principal {new-principal}
   ```

5. `sudo /opt/margo/deploy/azure/scripts/bootstrap.sh` (installs the new anchor; `init` is
   idempotent against the rebound config), then `sudo systemctl restart margo-gate margo-harness`,
   then `verify-phase0.sh`.
6. Pending approvals from the old manager are void: `margo_control.py pending`, then `reject` each.

`bootstrap.sh` refuses to run `init` against a config bound to a different manager; that refusal is
the prompt to do step 4, not a reason to delete the config.

## Restore

Backups are produced by `margo-backup.timer` (daily, 03:15 plus up to 15 minutes) with
`backup-state.sh`: every account database copied through the SQLite backup API with the harness
stopped and `integrity_check` run on the copy, plus `config.json`, `deployment.json`,
`gate-audit.jsonl`, `health.json`, `mcp-config.json`, `sha256sums.txt` and `manifest.json`, under
`/var/lib/margo-backups/<timestamp>/` (root, 0700). Pair this with Azure Backup for the VM or copy
the directory off the host; a backup on the same disk is not disaster recovery.

To restore onto a host at the same revision:

```sh
sudo systemctl stop margo-harness margo-gate
B=/var/lib/margo-backups/{timestamp}
( cd "$B" && sudo sha256sum -c sha256sums.txt )
sudo install -d -m 0700 -o margo -g margo /var/lib/margo/copilot/margo/state
sudo cp -a "$B/state/." /var/lib/margo/copilot/margo/state/
sudo chown -R margo:margo /var/lib/margo/copilot/margo
sudo install -m 0600 -o margo -g margo "$B/config.json" /var/lib/margo/copilot/margo/config.json
sudo systemctl start margo-gate margo-harness
```

Do not restore `deployment.json` over a newer one without reading both; do not restore the audit
log over the live one (keep both). After a restore the ledger may hold `approved` actions whose
targets changed; the gate re-reads every target before executing, so they fail closed rather than
act on stale state. Tokens are not in the backup by design; re-authenticate if the token directory
was lost.

## Upgrade

The host is rebuilt, not patched in place:

1. Pin the new commit as `repoRevision` (and any new `nodeVersion`/`copilotVersion`) in the
   private parameters file.
2. Take a backup (`sudo systemctl start margo-backup.service`) and copy it off the host.
3. Redeploy. A new VM with the same name replaces the OS disk; the data disk is `Detach` on delete
   and can be re-attached, or the state restored from the backup.
4. `reauth.sh` if the token directory did not survive, then `verify-phase0.sh`.

Package updates on the OS are left to Ubuntu's unattended upgrades; Node and Copilot are pinned and
change only with the parameters file.

## Decommission

```sh
sudo /opt/margo/deploy/azure/scripts/decommission.sh --yes            # keep state and backups
sudo /opt/margo/deploy/azure/scripts/decommission.sh --yes --purge-state
```

The script stops and removes the units, shreds the token directory and the Copilot credential file,
removes `/etc/margo`, and prints the list of revocations it deliberately does not perform: Entra
sessions and identity, Exchange delegation, Key Vault secrets (delete, then purge), the GitHub
token and seat, the manager's role assignments, and the Azure resources. Do each one in its own
portal or audit log; deleting the disks and revoking the identity are what actually end Margo's
access.

## Pilot

The staged checklist (read-only, then T1 under instruction, then supervised T2) is in the
[README](README.md#pilot-checklist). Report what was exercised, with dates and evidence file names.
