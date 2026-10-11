#!/usr/bin/env bash
#
# Shared helpers for the deploy/azure operator scripts. Sourced by bootstrap.sh,
# verify-phase0.sh, backup-state.sh, reauth.sh and decommission.sh; not run on
# its own.
#
# Everything here is deliberately boring: timestamps on every line, fail-closed
# helpers, and Key Vault access through the VM's managed identity (IMDS token,
# then a plain REST GET). No Azure CLI on the host, no stored credentials, and
# tokens never appear on a command line where `ps` could show them.
#
set -euo pipefail

# Layout shared with the systemd units and the Bicep template. Override only
# for tests; the units hard-code the same paths.
MARGO_ETC="${MARGO_ETC:-/etc/margo}"
MARGO_BOOTSTRAP_ENV="${MARGO_BOOTSTRAP_ENV:-$MARGO_ETC/bootstrap.env}"
MARGO_DEPLOYMENT="${MARGO_DEPLOYMENT:-$MARGO_ETC/deployment.json}"
MARGO_COPILOT_ENV_FILE="${MARGO_COPILOT_ENV_FILE:-$MARGO_ETC/copilot.env}"
MARGO_CHECKOUT="${MARGO_CHECKOUT:-/opt/margo}"
MARGO_HOME="${MARGO_HOME:-/var/lib/margo}"
MARGO_COPILOT_HOME="${MARGO_COPILOT_HOME:-$MARGO_HOME/copilot}"
MARGO_TOKEN_DIR="${MARGO_TOKEN_DIR:-/var/lib/margo-gate}"
MARGO_EVIDENCE_DIR="${MARGO_EVIDENCE_DIR:-$MARGO_HOME/evidence}"
MARGO_BACKUP_DIR="${MARGO_BACKUP_DIR:-/var/lib/margo-backups}"
MARGO_GATE_SOCKET="${MARGO_GATE_SOCKET:-/run/margo/gate.sock}"
MARGO_USER="${MARGO_USER:-margo}"
MARGO_GATE_GROUP="${MARGO_GATE_GROUP:-margo-gate}"
# shellcheck disable=SC2034  # used by the scripts that source this file
MARGO_SCRIPTS="$MARGO_CHECKOUT/skills/chief-of-staff/scripts"

# Azure endpoints. IMDS is link-local and must never go through a proxy. The
# vault DNS suffix and API version are read where they are used, so the value
# cloud-init records in bootstrap.env (MARGO_VAULT_SUFFIX, from the Bicep
# environment()) wins over the public-cloud default.
MARGO_IMDS_ENDPOINT="${MARGO_IMDS_ENDPOINT:-http://169.254.169.254}"
vault_suffix() { printf '%s' "${MARGO_VAULT_SUFFIX:-vault.azure.net}"; }
vault_api_version() { printf '%s' "${MARGO_VAULT_API_VERSION:-7.4}"; }

# ---------------------------------------------------------------- output ----

_stamp() { date -u +%Y-%m-%dT%H:%M:%SZ; }
log()  { printf '%s %s\n' "$(_stamp)" "$*"; }
warn() { printf '%s warning: %s\n' "$(_stamp)" "$*" >&2; }
die()  { printf '%s error: %s\n' "$(_stamp)" "$*" >&2; exit 1; }

require_root() {
  # $1 = script name for the message.
  [ "$(id -u)" -eq 0 ] || die "$1 must run as root (sudo); it manages users, units and root-owned files"
}

# ---------------------------------------------------------- bootstrap env ----

load_bootstrap_env() {
  # /etc/margo/bootstrap.env is root-owned KEY=value written by cloud-init from
  # the Bicep parameters. It holds names and versions, never secrets. Only
  # MARGO_* keys are accepted and the file is never `source`d, so a damaged or
  # tampered file cannot run commands as root. Values already set in the
  # environment win, which is how an operator overrides one value for a re-run.
  [ -f "$MARGO_BOOTSTRAP_ENV" ] \
    || die "missing $MARGO_BOOTSTRAP_ENV; cloud-init writes it from the Bicep parameters (see deploy/azure/README.md)"
  local line key value
  while IFS= read -r line || [ -n "$line" ]; do
    line="${line%$'\r'}"
    case "$line" in ''|'#'*) continue ;; esac
    key="${line%%=*}"
    value="${line#*=}"
    [[ "$key" =~ ^MARGO_[A-Z0-9_]+$ ]] || die "invalid line in $MARGO_BOOTSTRAP_ENV (expected MARGO_KEY=value): ${line%%=*}"
    if [ -z "${!key+x}" ]; then
      printf -v "$key" '%s' "$value"
      export "${key?}"
    fi
  done < "$MARGO_BOOTSTRAP_ENV"
}

require_env() {
  # $@ = variable names that must be non-empty after load_bootstrap_env.
  local name
  for name in "$@"; do
    [ -n "${!name:-}" ] || die "$name is empty; set it in $MARGO_BOOTSTRAP_ENV (Bicep parameter) or the environment"
  done
}

# ------------------------------------------------------------- key vault ----

imds_token() {
  # Print an access token for Key Vault from the VM's system-assigned identity.
  # Returns 1 (and prints nothing) when IMDS is unreachable or answers without
  # a token, which is the case off-Azure and when the identity is missing.
  local body code
  body="$(mktemp)"
  code=$(curl -sS --noproxy '*' --max-time 10 -H 'Metadata: true' -o "$body" -w '%{http_code}' \
    "$MARGO_IMDS_ENDPOINT/metadata/identity/oauth2/token?api-version=2018-02-01&resource=https%3A%2F%2F$(vault_suffix)" \
    2>/dev/null) || code=000
  if [ "$code" != "200" ]; then
    rm -f "$body"
    return 1
  fi
  python3 -I -c 'import json, sys
token = json.load(open(sys.argv[1], encoding="utf-8")).get("access_token")
if not isinstance(token, str) or not token:
    sys.exit(1)
sys.stdout.write(token)' "$body" || { rm -f "$body"; return 1; }
  rm -f "$body"
}

kv_secret_fetch() {
  # $1 = vault name, $2 = secret name, $3 = file that receives the secret VALUE
  # (created 0600). Prints nothing. Exit codes: 0 fetched; 44 secret not found;
  # 43 forbidden (RBAC not granted or not yet propagated); 41 no identity
  # token; 1 anything else. The bearer token travels in a curl config file,
  # never on the command line.
  local vault="$1" name="$2" destination="$3" token config body code
  token="$(imds_token)" || return 41
  config="$(mktemp)"
  body="$(mktemp)"
  printf 'header = "Authorization: Bearer %s"\n' "$token" > "$config"
  unset token
  code=$(curl -sS --max-time 30 -K "$config" -o "$body" -w '%{http_code}' \
    "https://$vault.$(vault_suffix)/secrets/$name?api-version=$(vault_api_version)" 2>/dev/null) || code=000
  rm -f "$config"
  case "$code" in
    200) ;;
    404) rm -f "$body"; return 44 ;;
    403) rm -f "$body"; return 43 ;;
    *)   rm -f "$body"; return 1 ;;
  esac
  ( umask 077; python3 -I -c 'import json, sys
value = json.load(open(sys.argv[1], encoding="utf-8")).get("value")
if not isinstance(value, str):
    sys.exit(1)
with open(sys.argv[2], "w", encoding="utf-8") as stream:
    stream.write(value)' "$body" "$destination" ) || { rm -f "$body"; return 1; }
  rm -f "$body"
}

kv_secret_status() {
  # $1 = vault, $2 = secret name. Prints one word describing readability without
  # keeping the value: ok | not-found | forbidden | no-identity | error.
  local scratch rc=0
  scratch="$(mktemp)"
  kv_secret_fetch "$1" "$2" "$scratch" || rc=$?
  rm -f "$scratch"
  case "$rc" in
    0)  printf 'ok' ;;
    44) printf 'not-found' ;;
    43) printf 'forbidden' ;;
    41) printf 'no-identity' ;;
    *)  printf 'error' ;;
  esac
}

# --------------------------------------------------------------- helpers ----

as_margo() {
  # Run a command as the service user with the same environment the units use.
  # Root never imports Margo's modules from the service user's writable tree;
  # callers that need Python as root use $MARGO_SCRIPTS (the root-owned checkout).
  sudo -u "$MARGO_USER" -H env \
    "HOME=$MARGO_HOME" "COPILOT_HOME=$MARGO_COPILOT_HOME" "MARGO_DEPLOYMENT=$MARGO_DEPLOYMENT" \
    "MARGO_GATE_SOCKET=$MARGO_GATE_SOCKET" "PATH=/usr/local/bin:/usr/bin:/bin" "$@"
}

unit_state() {
  # $1 = unit. Prints systemctl's active state, or "unknown" off-systemd.
  systemctl is-active "$1" 2>/dev/null || true
}

deployment_field() {
  # $1 = dotted path into /etc/margo/deployment.json (e.g. margo.principal).
  # Prints the string value; exits 1 when the file or the key is missing. Used
  # by root for values it must pass to commands; never log the result.
  python3 -I -c 'import json, sys
try:
    document = json.load(open(sys.argv[1], encoding="utf-8"))
except (OSError, ValueError):
    sys.exit(1)
for part in sys.argv[2].split("."):
    if not isinstance(document, dict) or part not in document:
        sys.exit(1)
    document = document[part]
if not isinstance(document, str) or not document:
    sys.exit(1)
sys.stdout.write(document)' "$MARGO_DEPLOYMENT" "$1"
}
