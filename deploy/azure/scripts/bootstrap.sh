#!/usr/bin/env bash
#
# Provision a Margo remote host. Runs as root, from the pinned checkout that
# cloud-init placed at /opt/margo, and is idempotent: every step checks before
# it changes, so the first boot and an operator re-run after fixing a parameter
# take the same path.
#
#   sudo /opt/margo/deploy/azure/scripts/bootstrap.sh
#
# Inputs:
#   /etc/margo/bootstrap.env   names and versions from the Bicep parameters,
#                              written by cloud-init; never secrets
#   Key Vault (managed identity, REST):
#     $MARGO_DEPLOYMENT_SECRET   the deployment anchor, installed as
#                                /etc/margo/deployment.json (root, 0644)
#     $MARGO_COPILOT_ENV_SECRET  optional environment file with the Copilot CLI
#                                credential, installed as /etc/margo/copilot.env
#                                (root:margo, 0640)
#
# What this never does: add the service user to the token group, register the
# direct Work IQ server with Copilot, copy tokens from anywhere, fetch a
# different revision over the running checkout, or exit 0 when verification
# failed. The last step is verify-phase0.sh and its exit code is this script's.
#
set -euo pipefail

SELF_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
. "$SELF_DIR/lib.sh"

export DEBIAN_FRONTEND=noninteractive
MARGO_SECRET_WAIT_SECONDS="${MARGO_SECRET_WAIT_SECONDS:-1200}"
MARGO_SECRET_POLL_SECONDS="${MARGO_SECRET_POLL_SECONDS:-30}"
UNIT_DIR="/etc/systemd/system"

usage() {
  sed -n '2,24p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

case "${1:-}" in
  -h|--help) usage; exit 0 ;;
  '') ;;
  *) die "unknown argument: $1 (bootstrap.sh takes no arguments; configure $MARGO_BOOTSTRAP_ENV)" ;;
esac

require_root bootstrap.sh
load_bootstrap_env
require_env MARGO_KEY_VAULT_NAME MARGO_DEPLOYMENT_SECRET MARGO_REPO_REVISION MARGO_NODE_VERSION MARGO_COPILOT_VERSION
MARGO_COPILOT_ENV_SECRET="${MARGO_COPILOT_ENV_SECRET:-}"
MARGO_DATA_DISK_LUN="${MARGO_DATA_DISK_LUN:-}"
MARGO_NODE_SHA256="${MARGO_NODE_SHA256:-}"

# ------------------------------------------------------------ checkout ----

check_checkout() {
  # Root runs code from here, so the tree must be the pinned revision, owned by
  # root and writable by nobody else. A re-run never fetches over itself; a new
  # revision means a rebuilt host (see RUNBOOKS.md, "Upgrade").
  local here head offender
  here="$(cd "$SELF_DIR/../../.." && pwd -P)"
  [ "$here" = "$(cd "$MARGO_CHECKOUT" 2>/dev/null && pwd -P)" ] \
    || die "bootstrap.sh must run from $MARGO_CHECKOUT (it is running from $here)"
  [ -d "$MARGO_CHECKOUT/.git" ] || die "$MARGO_CHECKOUT is not a git checkout"
  git config --system --add safe.directory "$MARGO_CHECKOUT" >/dev/null 2>&1 || true
  head="$(git -C "$MARGO_CHECKOUT" rev-parse HEAD)"
  if [[ "$MARGO_REPO_REVISION" =~ ^[0-9a-f]{40}$ ]]; then
    [ "$head" = "$MARGO_REPO_REVISION" ] \
      || die "checkout is at $head, bootstrap.env pins $MARGO_REPO_REVISION; re-clone at the pinned revision rather than editing in place"
  else
    warn "MARGO_REPO_REVISION is not a 40-character commit id; the host is not reproducible until it is"
  fi
  offender="$(find "$MARGO_CHECKOUT" -path "$MARGO_CHECKOUT/.git" -prune -o \( ! -user root -o -perm /022 \) -print -quit)"
  if [ -n "$offender" ]; then
    chown -R root:root "$MARGO_CHECKOUT"
    chmod -R go-w "$MARGO_CHECKOUT"
    log "checkout ownership corrected (first offender was $offender)"
  fi
  log "checkout $MARGO_CHECKOUT at $head"
}

# -------------------------------------------------------- disk and users ----

mount_data_disk() {
  # The data disk holds /var/lib/margo. Whole-disk ext4, mounted by UUID with
  # nofail so a detached disk never blocks boot (the units then stay down because
  # ConditionPathExists fails on the empty mount point, which is the honest state).
  [ -n "$MARGO_DATA_DISK_LUN" ] || { warn "MARGO_DATA_DISK_LUN is empty; private state stays on the OS disk"; return 0; }
  local device="/dev/disk/azure/scsi1/lun$MARGO_DATA_DISK_LUN" waited=0 uuid
  while [ ! -e "$device" ] && [ "$waited" -lt 120 ]; do sleep 5; waited=$((waited + 5)); done
  [ -e "$device" ] || die "data disk $device did not appear; check the Bicep dataDiskLun parameter"
  if [ -z "$(blkid -o value -s TYPE "$device" 2>/dev/null)" ]; then
    log "formatting $device as ext4 (first boot)"
    mkfs.ext4 -q -L margo-data "$device"
  fi
  uuid="$(blkid -o value -s UUID "$device")"
  [ -n "$uuid" ] || die "could not read the UUID of $device"
  mkdir -p "$MARGO_HOME"
  if ! grep -q "UUID=$uuid " /etc/fstab; then
    printf 'UUID=%s %s ext4 defaults,nofail,nodev,nosuid 0 2\n' "$uuid" "$MARGO_HOME" >> /etc/fstab
  fi
  mountpoint -q "$MARGO_HOME" || mount "$MARGO_HOME"
  log "data disk mounted at $MARGO_HOME"
}

ensure_users() {
  getent group "$MARGO_GATE_GROUP" >/dev/null || groupadd --system "$MARGO_GATE_GROUP"
  if ! getent passwd "$MARGO_USER" >/dev/null; then
    useradd --system --user-group --home-dir "$MARGO_HOME" --shell /bin/bash \
      --comment 'Margo service user' "$MARGO_USER"
  fi
  # The whole boundary between the model's shell and Margo's tokens is that
  # `margo` is NOT in `margo-gate`; only the gate unit gets the group. Refuse to
  # continue if someone added it, rather than provisioning a host without the
  # boundary.
  if id -nG "$MARGO_USER" | tr ' ' '\n' | grep -qx "$MARGO_GATE_GROUP"; then
    die "user $MARGO_USER is a member of $MARGO_GATE_GROUP; remove it (gpasswd -d $MARGO_USER $MARGO_GATE_GROUP) before continuing"
  fi
  mkdir -p "$MARGO_HOME" "$MARGO_TOKEN_DIR" "$MARGO_ETC" "$MARGO_EVIDENCE_DIR"
  chown "$MARGO_USER:$MARGO_USER" "$MARGO_HOME"
  chmod 0750 "$MARGO_HOME"
  chown "root:$MARGO_GATE_GROUP" "$MARGO_TOKEN_DIR"
  chmod 0770 "$MARGO_TOKEN_DIR"
  chown root:root "$MARGO_ETC"
  chmod 0755 "$MARGO_ETC"
  chown "root:$MARGO_USER" "$MARGO_EVIDENCE_DIR"
  chmod 0750 "$MARGO_EVIDENCE_DIR"
  log "users and directories ready ($MARGO_USER, group $MARGO_GATE_GROUP, $MARGO_TOKEN_DIR 0770)"
}

# ------------------------------------------------------------ packages ----

install_packages() {
  apt-get -o DPkg::Lock::Timeout=300 -qq update
  apt-get -o DPkg::Lock::Timeout=300 -qq install -y --no-install-recommends \
    ca-certificates curl git python3 xz-utils rsyslog sudo
  log "packages present (git, curl, python3 $(python3 -c 'import platform; print(platform.python_version())'))"
}

install_node() {
  # Official tarball, pinned version, verified before anything is unpacked.
  # MARGO_NODE_SHA256 (Bicep nodeSha256) pins the archive independently of the
  # download origin; without it the SHASUMS256.txt from the same origin is the
  # only check, and that is recorded as a warning rather than hidden.
  local want="v$MARGO_NODE_VERSION" current="" arch tarball base scratch
  if [ -x /usr/local/bin/node ]; then current="$(/usr/local/bin/node --version 2>/dev/null || true)"; fi
  if [ "$current" = "$want" ]; then log "node $want already installed"; return 0; fi
  case "$(uname -m)" in
    x86_64) arch=x64 ;;
    aarch64) arch=arm64 ;;
    *) die "unsupported architecture $(uname -m)" ;;
  esac
  tarball="node-$want-linux-$arch.tar.xz"
  base="https://nodejs.org/dist/$want"
  scratch="$(mktemp -d)"
  curl -fsSL --max-time 900 -o "$scratch/$tarball" "$base/$tarball" || die "download of $base/$tarball failed"
  if [ -n "$MARGO_NODE_SHA256" ]; then
    printf '%s  %s\n' "$MARGO_NODE_SHA256" "$tarball" > "$scratch/pinned.sha256"
    (cd "$scratch" && sha256sum -c --quiet pinned.sha256) || die "node tarball does not match MARGO_NODE_SHA256"
    log "node tarball matches the pinned checksum"
  else
    curl -fsSL --max-time 60 -o "$scratch/SHASUMS256.txt" "$base/SHASUMS256.txt" || die "download of SHASUMS256.txt failed"
    (cd "$scratch" && grep -F -- "  $tarball" SHASUMS256.txt | sha256sum -c --quiet -) \
      || die "node tarball does not match SHASUMS256.txt"
    warn "node tarball verified against SHASUMS256.txt from the same origin only; set the Bicep nodeSha256 parameter to pin it independently"
  fi
  rm -rf "/opt/node-$want"
  tar -xJf "$scratch/$tarball" -C /opt
  mv "/opt/node-$want-linux-$arch" "/opt/node-$want"
  local bin
  for bin in node npm npx corepack; do ln -sfn "/opt/node-$want/bin/$bin" "/usr/local/bin/$bin"; done
  rm -rf "$scratch"
  log "node $(/usr/local/bin/node --version) installed under /opt/node-$want"
}

copilot_installed_version() {
  npm ls -g --prefix /usr/local --json @github/copilot 2>/dev/null | python3 -I -c 'import json, sys
try:
    data = json.load(sys.stdin)
except ValueError:
    sys.exit(0)
sys.stdout.write(data.get("dependencies", {}).get("@github/copilot", {}).get("version", ""))' || true
}

install_copilot() {
  local current
  current="$(copilot_installed_version)"
  if [ "$current" = "$MARGO_COPILOT_VERSION" ]; then log "copilot cli $current already installed"; return 0; fi
  npm install -g --prefix /usr/local --no-fund --no-audit "@github/copilot@$MARGO_COPILOT_VERSION" >/dev/null
  current="$(copilot_installed_version)"
  [ "$current" = "$MARGO_COPILOT_VERSION" ] || die "copilot cli version after install is '$current', wanted $MARGO_COPILOT_VERSION"
  log "copilot cli $current installed (/usr/local/bin/copilot)"
}

# ---------------------------------------------------------------- margo ----

install_margo() {
  # install.sh as the service user, into COPILOT_HOME, exactly like a laptop
  # install: agent, skill, automations and the scheduled wrapper. The gate and
  # the harness themselves run from the root-owned checkout (see the units), so
  # the enforcement code is never writable by the uid that runs the model's shell.
  as_margo "$MARGO_CHECKOUT/install.sh" --dest "$MARGO_COPILOT_HOME" --yes >/dev/null
  [ -f "$MARGO_COPILOT_HOME/skills/chief-of-staff/scripts/workiq_gate_client.py" ] \
    || die "install.sh did not place the gate client under $MARGO_COPILOT_HOME"
  log "margo installed into $MARGO_COPILOT_HOME"
}

fetch_secret_with_wait() {
  # $1 = secret name, $2 = destination, $3 = required (1/0). Waits for a missing
  # secret because the operator uploads it right after the deployment finishes;
  # a forbidden answer is final (RBAC missing, or not yet propagated: re-run).
  local name="$1" destination="$2" required="$3" waited=0 rc
  while :; do
    rc=0
    kv_secret_fetch "$MARGO_KEY_VAULT_NAME" "$name" "$destination" || rc=$?
    case "$rc" in
      0) return 0 ;;
      44)
        if [ "$required" != "1" ]; then return 44; fi
        if [ "$waited" -ge "$MARGO_SECRET_WAIT_SECONDS" ]; then
          die "secret $name is not in vault $MARGO_KEY_VAULT_NAME after ${waited}s; upload it (README: step 4) and re-run bootstrap.sh"
        fi
        log "waiting for secret $name in vault $MARGO_KEY_VAULT_NAME (${waited}s of ${MARGO_SECRET_WAIT_SECONDS}s)"
        sleep "$MARGO_SECRET_POLL_SECONDS"
        waited=$((waited + MARGO_SECRET_POLL_SECONDS)) ;;
      43) die "vault $MARGO_KEY_VAULT_NAME refused the VM identity (HTTP 403); grant Key Vault Secrets User to the VM identity, wait for propagation, re-run" ;;
      41) die "no managed-identity token from IMDS; is the VM's system-assigned identity enabled?" ;;
      *) die "reading secret $name from vault $MARGO_KEY_VAULT_NAME failed (network or vault error)" ;;
    esac
  done
}

install_deployment() {
  local scratch
  scratch="$(mktemp "$MARGO_ETC/.deployment.XXXXXX")"
  fetch_secret_with_wait "$MARGO_DEPLOYMENT_SECRET" "$scratch" 1
  # Strict validation with the shipped loader from the root-owned checkout:
  # placeholders, a wrong profile or an unknown key refuse the file before any
  # unit can read it.
  python3 -I - "$MARGO_SCRIPTS" "$scratch" <<'EOF' || { rm -f "$scratch"; die "deployment secret is not a valid deployment anchor (see message above)"; }
import sys
sys.path.insert(0, sys.argv[1])
import manager_directives
try:
    document = manager_directives.load_deployment(sys.argv[2])
except Exception as exc:  # the loader's StateError text says which field
    sys.stderr.write("deployment.json rejected: %s\n" % exc)
    sys.exit(1)
sys.stdout.write("profile %s; manager configured; %d cli login(s); teams %s; email %s\n" % (
    document["profile"], len(document["control"]["cli_logins"]),
    "on" if document["control"]["teams_enabled"] else "off",
    "on" if document["control"]["email_enabled"] else "off"))
EOF
  chown root:root "$scratch"
  chmod 0644 "$scratch"
  mv -f "$scratch" "$MARGO_DEPLOYMENT"
  log "deployment anchor installed at $MARGO_DEPLOYMENT"
}

install_copilot_env() {
  local scratch rc=0
  [ -n "$MARGO_COPILOT_ENV_SECRET" ] || { warn "MARGO_COPILOT_ENV_SECRET is empty; Copilot CLI must be authenticated some other way"; return 0; }
  scratch="$(mktemp "$MARGO_ETC/.copilot-env.XXXXXX")"
  fetch_secret_with_wait "$MARGO_COPILOT_ENV_SECRET" "$scratch" 0 || rc=$?
  if [ "$rc" = "44" ]; then
    rm -f "$scratch"
    warn "secret $MARGO_COPILOT_ENV_SECRET not found; no $MARGO_COPILOT_ENV_FILE written (verify-phase0 reports copilot_check)"
    return 0
  fi
  # KEY=value lines only; the unit loads it with EnvironmentFile and a stray
  # command would otherwise be ignored silently there, so refuse it here.
  if grep -qvE '^(#.*|[A-Za-z_][A-Za-z0-9_]*=.*)?$' "$scratch"; then
    rm -f "$scratch"
    die "secret $MARGO_COPILOT_ENV_SECRET must contain KEY=value lines only"
  fi
  chown "root:$MARGO_USER" "$scratch"
  chmod 0640 "$scratch"
  mv -f "$scratch" "$MARGO_COPILOT_ENV_FILE"
  log "copilot environment file installed at $MARGO_COPILOT_ENV_FILE (root:$MARGO_USER 0640)"
}

write_mcp_config() {
  # Copilot reaches Work IQ only through the gate's stdio shim. Any other entry
  # that looks like a direct Work IQ server is a refusal, not a merge.
  local path="$MARGO_COPILOT_HOME/mcp-config.json" client="$MARGO_COPILOT_HOME/skills/chief-of-staff/scripts/workiq_gate_client.py"
  python3 -I - "$path" "$client" <<'EOF' || die "mcp-config.json was not written (see message above)"
import json, os, sys
path, client = sys.argv[1], sys.argv[2]
config = {}
if os.path.exists(path):
    with open(path, encoding="utf-8") as stream:
        config = json.load(stream)
    if not isinstance(config, dict):
        sys.exit("%s is not a JSON object" % path)
servers = config.setdefault("mcpServers", {})
if not isinstance(servers, dict):
    sys.exit("%s: mcpServers is not an object" % path)
for name, spec in servers.items():
    if name == "workiq-gate":
        continue
    if name.lower() == "workiq" or "workiq" in json.dumps(spec).lower():
        sys.exit("%s registers a direct Work IQ server as %r; Copilot must reach Work IQ only through workiq-gate" % (path, name))
servers["workiq-gate"] = {"type": "stdio", "command": "python3", "args": [client]}
scratch = path + ".tmp"
with open(scratch, "w", encoding="utf-8") as stream:
    json.dump(config, stream, indent=2, sort_keys=True)
    stream.write("\n")
os.replace(scratch, path)
EOF
  chown "root:$MARGO_USER" "$path"
  chmod 0640 "$path"
  log "copilot mcp config registers workiq-gate ($path)"
}

install_units() {
  local unit
  for unit in margo-gate.service margo-harness.service margo-backup.service margo-backup.timer; do
    install -m 0644 -o root -g root "$SELF_DIR/../systemd/$unit" "$UNIT_DIR/$unit"
  done
  systemctl daemon-reload
  systemctl enable margo-gate.service margo-harness.service margo-backup.timer >/dev/null 2>&1
  log "units installed and enabled (margo-gate, margo-harness, margo-backup.timer)"
}

bind_manager() {
  # One manager, bound by immutable object id, from the deployment anchor. The
  # store refuses a different manager on a re-run; that is the manager-change
  # runbook's job (rebind-manager), never an implicit rewrite here.
  local principal manager manager_principal result
  principal="$(deployment_field margo.principal)" || die "deployment.json has no margo.principal"
  manager="$(deployment_field manager.object_id)" || die "deployment.json has no manager.object_id"
  manager_principal="$(deployment_field manager.principal)" || die "deployment.json has no manager.principal"
  result="$(as_margo python3 "$MARGO_SCRIPTS/margo_store.py" init --account "$principal" --manager "$manager" \
    --manager-principal "$manager_principal" --profile remote-host)" \
    || die "margo_store.py init refused the binding (a different manager is already bound? see RUNBOOKS.md, manager change)"
  log "manager binding: $result"
}

start_services() {
  local waited=0
  systemctl restart margo-gate.service
  while [ ! -S "$MARGO_GATE_SOCKET" ] && [ "$waited" -lt 60 ]; do sleep 2; waited=$((waited + 2)); done
  [ -S "$MARGO_GATE_SOCKET" ] || warn "gate socket $MARGO_GATE_SOCKET did not appear within 60s (journalctl -u margo-gate)"
  systemctl start margo-backup.timer
  # The harness may exit 3 (blocked) until Work IQ is signed in; systemd leaves
  # it down on purpose and verify-phase0 reports it.
  systemctl start margo-harness.service || true
  log "services started (gate $(unit_state margo-gate.service), harness $(unit_state margo-harness.service))"
}

check_checkout
mount_data_disk
ensure_users
install_packages
install_node
install_copilot
install_margo
install_deployment
install_copilot_env
write_mcp_config
install_units
bind_manager
start_services

log "provisioning complete; running verification"
rc=0
"$SELF_DIR/verify-phase0.sh" || rc=$?
if [ "$rc" -ne 0 ]; then
  warn "bootstrap finished provisioning, but verification reported failed required checks (exit $rc)."
  warn "On a first boot that is expected until Work IQ is signed in: see RUNBOOKS.md, re-authentication."
fi
exit "$rc"
