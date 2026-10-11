#!/usr/bin/env bash
#
# Re-authenticate Margo's Work IQ sign-in on the host. Interactive, as root.
#
#   sudo /opt/margo/deploy/azure/scripts/reauth.sh
#
# Health `reauth_required` means a token can no longer be renewed. The harness
# stops retrying on purpose; a person has to sign Margo's identity in again,
# on this host, under the gate's identity context, so the renewed token state
# lands in /var/lib/margo-gate where only the gate can read it.
#
# The exact sign-in command is a Phase 0 result for your tenant (which identity
# type, which flow works without a browser on the VM). Record it as
# MARGO_WORKIQ_LOGIN_COMMAND in /etc/margo/bootstrap.env and this script runs
# it; otherwise it opens a shell in the right context and tells you what to do.
#
# Never copy token files from a laptop or another host. That would make Margo
# act as whoever owns those tokens, which is exactly what the identity design
# forbids.
#
set -euo pipefail

SELF_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
. "$SELF_DIR/lib.sh"

case "${1:-}" in
  -h|--help) sed -n '2,20p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
  '') ;;
  *) die "unknown argument: $1" ;;
esac

require_root reauth.sh
load_bootstrap_env
MARGO_WORKIQ_LOGIN_COMMAND="${MARGO_WORKIQ_LOGIN_COMMAND:-}"
[ -t 0 ] && [ -t 1 ] || die "reauth.sh needs an interactive terminal (sign-in is a manual step); run it from your Bastion session"

HARNESS_WAS="$(unit_state margo-harness.service)"
log "stopping margo-harness and margo-gate so no run or token refresh is in flight"
systemctl stop margo-harness.service || true
systemctl stop margo-gate.service || true

# The gate unit's effective identity: uid margo, group margo-gate, HOME in the
# token directory. A sign-in anywhere else leaves tokens where the gate cannot
# see them, or where the model's shell can.
run_as_gate_identity() {
  sudo -u "$MARGO_USER" -g "$MARGO_GATE_GROUP" -H env \
    "HOME=$MARGO_TOKEN_DIR" "COPILOT_HOME=$MARGO_COPILOT_HOME" "MARGO_DEPLOYMENT=$MARGO_DEPLOYMENT" \
    "PATH=/usr/local/bin:/usr/bin:/bin" "$@"
}

cat <<EOF

  Work IQ re-authentication for Margo's identity
  ------------------------------------------------
  1. Sign in AS MARGO'S IDENTITY (never your own). The gate confirms the
     signed-in object id against the deployment anchor; a mismatch is 'blocked'.
  2. The sign-in runs as uid $MARGO_USER with group $MARGO_GATE_GROUP and
     HOME=$MARGO_TOKEN_DIR, so renewed tokens land behind the group boundary.
  3. Do not copy token files from any other machine.

EOF

if [ -n "$MARGO_WORKIQ_LOGIN_COMMAND" ]; then
  log "running the recorded sign-in command under the gate identity"
  run_as_gate_identity bash -c "$MARGO_WORKIQ_LOGIN_COMMAND" \
    || warn "the sign-in command exited non-zero; health below says whether a usable token exists"
else
  cat <<EOF
  No MARGO_WORKIQ_LOGIN_COMMAND is recorded in $MARGO_BOOTSTRAP_ENV.
  A shell is opened now in the gate's identity context. Run the Work IQ
  sign-in flow your Phase 0 evidence recorded for this tenant, then exit the
  shell to continue the health check.

EOF
  run_as_gate_identity bash --noprofile --norc -i || true
fi

log "starting margo-gate"
systemctl start margo-gate.service
WAITED=0
while [ ! -S "$MARGO_GATE_SOCKET" ] && [ "$WAITED" -lt 60 ]; do sleep 2; WAITED=$((WAITED + 2)); done
[ -S "$MARGO_GATE_SOCKET" ] || die "gate socket did not appear within 60s (journalctl -u margo-gate)"

STATUS="$(as_margo python3 "$MARGO_SCRIPTS/margo_control.py" --socket "$MARGO_GATE_SOCKET" health 2>/dev/null \
  | python3 -I -c 'import json, sys
health = json.load(sys.stdin)
checks = health.get("checks", {})
print("%s identity=%s delegated=%s" % (health.get("status"), checks.get("workiq_identity", {}).get("status"),
                                       checks.get("delegated_access", {}).get("status")))' 2>/dev/null || echo "unavailable")"
log "gate health after sign-in: $STATUS"

case "${STATUS%% *}" in
  connected|degraded)
    systemctl start margo-harness.service
    log "margo-harness started; run verify-phase0.sh to record the evidence"
    [ "${STATUS%% *}" = "connected" ] || warn "health is degraded: delegated reads are not all working (see detail in margo_control.py health)"
    ;;
  *)
    if [ "$HARNESS_WAS" = "active" ]; then warn "margo-harness is left stopped because the gate is not connected"; fi
    die "re-authentication did not produce a connected gate (status: $STATUS); see journalctl -u margo-gate and RUNBOOKS.md"
    ;;
esac
