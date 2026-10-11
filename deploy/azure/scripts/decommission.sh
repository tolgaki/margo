#!/usr/bin/env bash
#
# Take a Margo host out of service. Root, explicit confirmation required.
#
#   sudo /opt/margo/deploy/azure/scripts/decommission.sh --yes [--purge-state]
#
# Local effects: stops and disables the units, removes them, shreds the token
# directory (/var/lib/margo-gate) and the Copilot credential file, removes
# /etc/margo. Private state under /var/lib/margo and the backups stay unless
# --purge-state is given, because they are the record of what Margo did.
#
# What it deliberately does NOT do: revoke anything in Entra, Exchange, Key
# Vault, GitHub or Azure RBAC. Those are the real revocations and they need an
# administrator with the right role; the script prints the list so none is
# forgotten. Shredding files on a managed disk is best effort (the platform
# may keep copies); deleting the disk and revoking the identity are what count.
#
set -euo pipefail

SELF_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
. "$SELF_DIR/lib.sh"

CONFIRMED=0
PURGE_STATE=0
while [ $# -gt 0 ]; do
  case "$1" in
    --yes) CONFIRMED=1 ;;
    --purge-state) PURGE_STATE=1 ;;
    -h|--help) sed -n '2,17p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
  shift
done

require_root decommission.sh
[ "$CONFIRMED" -eq 1 ] || die "refusing without --yes: this stops Margo and destroys her token state on this host"

shred_tree() {
  # $1 = directory. Overwrite each file once, unlink, then remove the tree.
  [ -d "$1" ] || return 0
  find "$1" -type f -exec shred -u -n 1 -- {} + 2>/dev/null || true
  rm -rf -- "$1"
}

log "stopping and disabling units"
for unit in margo-harness.service margo-backup.timer margo-backup.service margo-gate.service; do
  systemctl disable --now "$unit" >/dev/null 2>&1 || true
  rm -f "/etc/systemd/system/$unit"
done
systemctl daemon-reload
systemctl reset-failed margo-harness.service margo-gate.service margo-backup.service >/dev/null 2>&1 || true

log "shredding the token directory $MARGO_TOKEN_DIR"
shred_tree "$MARGO_TOKEN_DIR"

if [ -f "$MARGO_COPILOT_ENV_FILE" ]; then
  log "shredding $MARGO_COPILOT_ENV_FILE"
  shred -u -n 1 -- "$MARGO_COPILOT_ENV_FILE" || rm -f -- "$MARGO_COPILOT_ENV_FILE"
fi
rm -rf -- "$MARGO_ETC"
rm -f -- "$MARGO_GATE_SOCKET"

if [ "$PURGE_STATE" -eq 1 ]; then
  log "purging private state $MARGO_HOME and backups $MARGO_BACKUP_DIR (--purge-state)"
  if mountpoint -q "$MARGO_HOME"; then
    shred_tree "$MARGO_HOME"
    mkdir -p "$MARGO_HOME"
  else
    shred_tree "$MARGO_HOME"
  fi
  shred_tree "$MARGO_BACKUP_DIR"
else
  log "private state kept at $MARGO_HOME and backups at $MARGO_BACKUP_DIR (re-run with --purge-state to destroy them)"
fi

cat <<'EOF'

Local decommission done. The identity is still live until an administrator does
the following; none of it can be done from this host:

  Entra ID
    1. Revoke sessions / refresh tokens for Margo's identity, then disable or
       delete the identity (agent identity or dedicated account).
    2. Remove Margo's identity from any group that granted it licences or roles.
  Exchange Online
    3. Remove delegated Full Access and Send on Behalf / Send As for Margo on
       the manager's mailbox; remove Margo's own mailbox if it is not kept as a
       record.
  Key Vault
    4. Delete, then purge, the deployment and Copilot environment secrets; if
       the vault was dedicated to this host, delete it after the retention you need.
  GitHub
    5. Revoke the token that held the Copilot seat for the VM; release the seat.
  Azure
    6. Remove the manager's role assignments on the VM and the vault.
    7. Delete the VM, its disks (the data disk holds the private state), the
       Bastion and the network, or the whole resource group; keep a backup of
       the private state first if you must retain the record.
  Teams
    8. Nothing to revoke; the 1:1 chat history remains with the manager.

Verify each item in its own portal or audit log rather than trusting this list.
EOF
