#!/usr/bin/env bash
#
# Phase 0 platform verification as executable checks (docs/remote-host-plan.md,
# "Phase 0"). Runs as root on the host, prints a table, writes evidence JSON to
# /var/lib/margo/evidence/<timestamp>.json and exits 1 if any REQUIRED check
# failed. The evidence stays on the host; it is never part of the repository.
#
#   sudo /opt/margo/deploy/azure/scripts/verify-phase0.sh [--print-json]
#
# What a pass proves: the managed identity can read the vault; the deployment
# anchor is valid; pinned Node and Copilot versions are installed; Copilot's
# check command runs as the service user; the gate is serving, signed in to Work
# IQ as Margo and able to read the manager's inbox and calendar; the units are
# up; the token directory boundary is intact; the classifier places a send at
# T2. What it does not prove: anything about tenants, licences or Teams
# coverage beyond those probes, or that a model-driven run behaves. Record
# those by hand in the README's evidence table.
#
set -euo pipefail

SELF_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
. "$SELF_DIR/lib.sh"

PRINT_JSON=0
case "${1:-}" in
  -h|--help) sed -n '2,19p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
  --print-json) PRINT_JSON=1 ;;
  '') ;;
  *) die "unknown argument: $1" ;;
esac

require_root verify-phase0.sh
load_bootstrap_env
MARGO_REPO_REVISION="${MARGO_REPO_REVISION:-}"
MARGO_NODE_VERSION="${MARGO_NODE_VERSION:-}"
MARGO_COPILOT_VERSION="${MARGO_COPILOT_VERSION:-}"
MARGO_KEY_VAULT_NAME="${MARGO_KEY_VAULT_NAME:-}"
MARGO_DEPLOYMENT_SECRET="${MARGO_DEPLOYMENT_SECRET:-}"
MARGO_COPILOT_ENV_SECRET="${MARGO_COPILOT_ENV_SECRET:-}"

RECORDS="$(mktemp)"
trap 'rm -f "$RECORDS"' EXIT
FAILED_REQUIRED=0

record() {
  # $1 = name, $2 = required (1/0), $3 = ok|failed|skipped, $4 = detail.
  # Details are statuses and versions only: never a secret, token, object id or
  # message body, because the evidence file is readable by the service user.
  python3 -I -c 'import json, sys
print(json.dumps({"name": sys.argv[1], "required": sys.argv[2] == "1", "status": sys.argv[3],
                  "detail": sys.argv[4][:300]}, sort_keys=True))' "$1" "$2" "$3" "$4" >> "$RECORDS"
  if [ "$2" = "1" ] && [ "$3" = "failed" ]; then FAILED_REQUIRED=$((FAILED_REQUIRED + 1)); fi
}

# ----------------------------------------------------------------- checks ----

check_identity_and_vault() {
  if imds_token >/dev/null 2>&1; then
    record imds_identity 1 ok "managed identity token for $(vault_suffix) obtained"
  else
    record imds_identity 1 failed "no token from IMDS; system-assigned identity missing or not on Azure"
  fi
  if [ -z "$MARGO_KEY_VAULT_NAME" ] || [ -z "$MARGO_DEPLOYMENT_SECRET" ]; then
    record vault_deployment_secret 1 failed "MARGO_KEY_VAULT_NAME or MARGO_DEPLOYMENT_SECRET unset in $MARGO_BOOTSTRAP_ENV"
  else
    local status
    status="$(kv_secret_status "$MARGO_KEY_VAULT_NAME" "$MARGO_DEPLOYMENT_SECRET")"
    if [ "$status" = "ok" ]; then
      record vault_deployment_secret 1 ok "secret $MARGO_DEPLOYMENT_SECRET readable"
    else
      record vault_deployment_secret 1 failed "secret $MARGO_DEPLOYMENT_SECRET: $status"
    fi
  fi
  if [ -n "$MARGO_COPILOT_ENV_SECRET" ] && [ -n "$MARGO_KEY_VAULT_NAME" ]; then
    local copilot_status
    copilot_status="$(kv_secret_status "$MARGO_KEY_VAULT_NAME" "$MARGO_COPILOT_ENV_SECRET")"
    if [ "$copilot_status" = "ok" ]; then
      record vault_copilot_secret 0 ok "secret $MARGO_COPILOT_ENV_SECRET readable"
    else
      record vault_copilot_secret 0 failed "secret $MARGO_COPILOT_ENV_SECRET: $copilot_status"
    fi
  else
    record vault_copilot_secret 0 skipped "no MARGO_COPILOT_ENV_SECRET configured"
  fi
}

check_deployment_file() {
  if [ ! -f "$MARGO_DEPLOYMENT" ]; then
    record deployment_file 1 failed "$MARGO_DEPLOYMENT missing"
    return 0
  fi
  local owner mode summary
  owner="$(stat -c %U "$MARGO_DEPLOYMENT")"
  mode="$(stat -c %a "$MARGO_DEPLOYMENT")"
  if [ "$owner" != "root" ] || [ "$mode" != "644" ]; then
    record deployment_file 1 failed "$MARGO_DEPLOYMENT is $owner $mode, expected root 644"
    return 0
  fi
  if summary="$(python3 -I - "$MARGO_SCRIPTS" "$MARGO_DEPLOYMENT" <<'EOF' 2>&1
import sys
sys.path.insert(0, sys.argv[1])
import manager_directives
document = manager_directives.load_deployment(sys.argv[2])
print("profile %s; %d cli login(s); teams %s; email %s" % (
    document["profile"], len(document["control"]["cli_logins"]),
    "on" if document["control"]["teams_enabled"] else "off",
    "on" if document["control"]["email_enabled"] else "off"))
EOF
  )"; then
    record deployment_file 1 ok "$summary"
  else
    record deployment_file 1 failed "loader rejected the file: ${summary##*: }"
  fi
}

check_versions() {
  local python_version node_version copilot_version
  python_version="$(python3 -c 'import platform; print(platform.python_version())')"
  if python3 -c 'import sys; sys.exit(0 if sys.version_info >= (3, 9) else 1)'; then
    record python_version 1 ok "python $python_version"
  else
    record python_version 1 failed "python $python_version is older than 3.9"
  fi
  node_version="$(/usr/local/bin/node --version 2>/dev/null || true)"
  if [ -n "$MARGO_NODE_VERSION" ] && [ "$node_version" = "v$MARGO_NODE_VERSION" ]; then
    record node_version 1 ok "node $node_version"
  else
    record node_version 1 failed "node '${node_version:-missing}', pinned v${MARGO_NODE_VERSION:-unset}"
  fi
  copilot_version="$(npm ls -g --prefix /usr/local --json @github/copilot 2>/dev/null | python3 -I -c 'import json, sys
try:
    sys.stdout.write(json.load(sys.stdin).get("dependencies", {}).get("@github/copilot", {}).get("version", ""))
except ValueError:
    pass' || true)"
  if [ -n "$MARGO_COPILOT_VERSION" ] && [ "$copilot_version" = "$MARGO_COPILOT_VERSION" ] && [ -x /usr/local/bin/copilot ]; then
    record copilot_version 1 ok "@github/copilot $copilot_version"
  else
    record copilot_version 1 failed "@github/copilot '${copilot_version:-missing}', pinned ${MARGO_COPILOT_VERSION:-unset}"
  fi
}

check_copilot_check() {
  # deployment.harness.copilot_check, run as the service user with the same
  # environment file the harness unit loads. Output is not recorded (it may
  # name the signed-in GitHub account); only the exit code is.
  local -a command=()
  if [ ! -f "$MARGO_DEPLOYMENT" ]; then record copilot_check 1 skipped "no deployment file"; return 0; fi
  mapfile -d '' -t command < <(python3 -I -c 'import json, sys
for part in json.load(open(sys.argv[1], encoding="utf-8")).get("harness", {}).get("copilot_check", []):
    sys.stdout.write(part + "\0")' "$MARGO_DEPLOYMENT" 2>/dev/null || true)
  if [ "${#command[@]}" -eq 0 ]; then record copilot_check 1 failed "harness.copilot_check is empty"; return 0; fi
  if as_margo bash -c 'set -a; if [ -f "$1" ]; then . "$1"; fi; set +a; shift; exec "$@"' _ "$MARGO_COPILOT_ENV_FILE" "${command[@]}" >/dev/null 2>&1; then
    record copilot_check 1 ok "${command[*]} exited 0 as $MARGO_USER"
  else
    record copilot_check 1 failed "${command[*]} failed as $MARGO_USER (credential file present: $([ -f "$MARGO_COPILOT_ENV_FILE" ] && echo yes || echo no))"
  fi
}

check_mcp_config() {
  local path="$MARGO_COPILOT_HOME/mcp-config.json" client="$MARGO_COPILOT_HOME/skills/chief-of-staff/scripts/workiq_gate_client.py" problem
  if [ ! -f "$path" ]; then record mcp_config 1 failed "$path missing"; return 0; fi
  if [ "$(stat -c %U "$path")" != "root" ]; then
    record mcp_config 1 failed "$path is owned by $(stat -c %U "$path"), expected root (replaced since bootstrap?)"
    return 0
  fi
  if problem="$(python3 -I - "$path" "$client" <<'EOF' 2>&1
import json, sys
config = json.load(open(sys.argv[1], encoding="utf-8"))
servers = config.get("mcpServers", {})
gate = servers.get("workiq-gate")
if gate != {"type": "stdio", "command": "python3", "args": [sys.argv[2]]}:
    sys.exit("workiq-gate entry missing or changed")
for name, spec in servers.items():
    if name != "workiq-gate" and (name.lower() == "workiq" or "workiq" in json.dumps(spec).lower()):
        sys.exit("direct Work IQ server registered as %r" % name)
print("workiq-gate registered; %d server(s) total" % len(servers))
EOF
  )"; then
    record mcp_config 1 ok "$problem"
  else
    record mcp_config 1 failed "$problem"
  fi
}

check_token_dir() {
  local owner group mode
  if [ ! -d "$MARGO_TOKEN_DIR" ]; then record token_dir 1 failed "$MARGO_TOKEN_DIR missing"; return 0; fi
  owner="$(stat -c %U "$MARGO_TOKEN_DIR")"; group="$(stat -c %G "$MARGO_TOKEN_DIR")"; mode="$(stat -c %a "$MARGO_TOKEN_DIR")"
  if [ "$owner" != "root" ] || [ "$group" != "$MARGO_GATE_GROUP" ] || [ "$mode" != "770" ]; then
    record token_dir 1 failed "$MARGO_TOKEN_DIR is $owner:$group $mode, expected root:$MARGO_GATE_GROUP 770"
  elif id -nG "$MARGO_USER" 2>/dev/null | tr ' ' '\n' | grep -qx "$MARGO_GATE_GROUP"; then
    record token_dir 1 failed "$MARGO_USER is a member of $MARGO_GATE_GROUP; the model's shell could read tokens"
  else
    record token_dir 1 ok "root:$MARGO_GATE_GROUP 770; $MARGO_USER not in the group"
  fi
}

check_checkout() {
  local head
  if [ ! -d "$MARGO_CHECKOUT/.git" ]; then record checkout_pinned 1 failed "$MARGO_CHECKOUT is not a checkout"; return 0; fi
  head="$(git -C "$MARGO_CHECKOUT" rev-parse HEAD 2>/dev/null || echo unknown)"
  if [ "$(stat -c %U "$MARGO_CHECKOUT")" != "root" ]; then
    record checkout_pinned 1 failed "$MARGO_CHECKOUT is not owned by root"
  elif [[ "$MARGO_REPO_REVISION" =~ ^[0-9a-f]{40}$ ]] && [ "$head" != "$MARGO_REPO_REVISION" ]; then
    record checkout_pinned 1 failed "checkout at $head, pinned $MARGO_REPO_REVISION"
  elif [[ "$MARGO_REPO_REVISION" =~ ^[0-9a-f]{40}$ ]]; then
    record checkout_pinned 1 ok "checkout at pinned $head"
  else
    record checkout_pinned 1 failed "MARGO_REPO_REVISION is not a 40-character commit id (checkout at $head)"
  fi
}

check_units() {
  local unit state
  for unit in margo-gate.service margo-harness.service margo-backup.timer; do
    state="$(unit_state "$unit")"
    if [ "$state" = "active" ]; then
      record "unit_${unit%%.*}" 1 ok "$unit active, $(systemctl is-enabled "$unit" 2>/dev/null || echo unknown)"
    else
      record "unit_${unit%%.*}" 1 failed "$unit is ${state:-unknown} (journalctl -u $unit)"
    fi
  done
  if [ -S "$MARGO_GATE_SOCKET" ]; then
    record gate_socket 1 ok "$MARGO_GATE_SOCKET present"
  else
    record gate_socket 1 failed "$MARGO_GATE_SOCKET missing"
  fi
}

check_gate_health() {
  # margo_control.py health as the service user: identity, delegated reads and
  # the ledger/directive stores, in one round trip. Only statuses are recorded.
  local report summary
  if [ ! -S "$MARGO_GATE_SOCKET" ]; then
    record gate_health 1 failed "no gate socket"
    record workiq_identity 1 skipped "no gate"
    record delegated_reads 1 skipped "no gate"
    return 0
  fi
  if ! report="$(as_margo python3 "$MARGO_SCRIPTS/margo_control.py" --socket "$MARGO_GATE_SOCKET" health 2>/dev/null)"; then
    record gate_health 1 failed "margo_control.py health failed (gate not answering)"
    record workiq_identity 1 skipped "no health report"
    record delegated_reads 1 skipped "no health report"
    return 0
  fi
  summary="$(printf '%s' "$report" | python3 -I -c 'import json, sys
health = json.load(sys.stdin)
checks = health.get("checks", {})
identity = checks.get("workiq_identity", {}).get("status", "unknown")
delegated = checks.get("delegated_access", {})
surfaces = ",".join("%s=%s" % (name, value.get("status", "unknown"))
                    for name, value in sorted(delegated.get("surfaces", {}).items()))
upstream = health.get("upstream", {})
print("|".join([str(health.get("status")), str(identity), str(delegated.get("status", "unknown")), surfaces,
                "alive=%s tools=%s" % (upstream.get("alive"), upstream.get("tools")),
                str(checks.get("ledger", {}).get("status")), str(checks.get("directives", {}).get("status")),
                str(health.get("paused"))]))' 2>/dev/null)" || summary="unparseable|||||||"
  local status identity delegated surfaces upstream ledger directives paused
  IFS='|' read -r status identity delegated surfaces upstream ledger directives paused <<< "$summary"
  if [ "$status" = "connected" ]; then
    record gate_health 1 ok "status connected; upstream $upstream; ledger $ledger; directives $directives; paused $paused"
  else
    record gate_health 1 failed "status ${status:-unknown}; upstream $upstream; ledger $ledger; directives $directives"
  fi
  if [ "$identity" = "ok" ]; then
    record workiq_identity 1 ok "work iq is signed in as the configured margo principal"
  else
    record workiq_identity 1 failed "workiq_identity ${identity:-unknown}"
  fi
  if [ "$delegated" = "ok" ]; then
    record delegated_reads 1 ok "manager inbox and calendar readable (${surfaces:-no surface detail})"
  else
    record delegated_reads 1 failed "delegated_access ${delegated:-unknown} (${surfaces:-no surface detail})"
  fi
}

check_classifier() {
  # Pure classification from the root-owned checkout: a send must be T2 and a
  # fetch T0 before anyone relies on the gate's policy.
  local send read
  send="$(python3 "$MARGO_SCRIPTS/workiq_gate.py" classify --tool do_action \
    --arguments '{"path": "/me/sendMail", "method": "POST", "body": {}}' --deployment "$MARGO_DEPLOYMENT" 2>/dev/null \
    | python3 -I -c 'import json, sys; print(json.load(sys.stdin).get("tier"))' 2>/dev/null || echo error)"
  read="$(python3 "$MARGO_SCRIPTS/workiq_gate.py" classify --tool fetch \
    --arguments '{"path": "/me/messages"}' --deployment "$MARGO_DEPLOYMENT" 2>/dev/null \
    | python3 -I -c 'import json, sys; print(json.load(sys.stdin).get("tier"))' 2>/dev/null || echo error)"
  if [ "$send" = "T2" ] && [ "$read" = "T0" ]; then
    record classifier 1 ok "sendMail -> T2, fetch -> T0"
  else
    record classifier 1 failed "sendMail -> $send (want T2), fetch -> $read (want T0)"
  fi
}

check_health_file() {
  local path="$MARGO_HOME/health.json" state
  if [ ! -f "$path" ]; then record harness_health_file 0 failed "$path not written yet"; return 0; fi
  state="$(python3 -I -c 'import json, sys
health = json.load(open(sys.argv[1], encoding="utf-8"))
print("%s (checked %s, failures %s)" % (health.get("state"), health.get("checked_at"), health.get("consecutive_failures")))' "$path" 2>/dev/null || echo unreadable)"
  if [ "${state%% *}" = "connected" ]; then
    record harness_health_file 0 ok "$state"
  else
    record harness_health_file 0 failed "$state"
  fi
}

check_egress() {
  # Reachability only, no credentials: Graph's public metadata document and the
  # GitHub API root (which answers 200 or 403 to anonymous callers).
  local code
  code="$(curl -sS --max-time 15 -o /dev/null -w '%{http_code}' 'https://graph.microsoft.com/v1.0/$metadata' 2>/dev/null || echo 000)"
  if [ "$code" = "200" ]; then record egress_graph 0 ok "graph metadata HTTP $code"; else record egress_graph 0 failed "graph metadata HTTP $code"; fi
  code="$(curl -sS --max-time 15 -o /dev/null -w '%{http_code}' 'https://api.github.com/' 2>/dev/null || echo 000)"
  case "$code" in
    200|403) record egress_github 0 ok "api.github.com HTTP $code" ;;
    *) record egress_github 0 failed "api.github.com HTTP $code" ;;
  esac
}

check_identity_and_vault
check_deployment_file
check_versions
check_copilot_check
check_mcp_config
check_token_dir
check_checkout
check_units
check_gate_health
check_classifier
check_health_file
check_egress

# ---------------------------------------------------------------- report ----

mkdir -p "$MARGO_EVIDENCE_DIR"
chown "root:$MARGO_USER" "$MARGO_EVIDENCE_DIR"
chmod 0750 "$MARGO_EVIDENCE_DIR"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
EVIDENCE="$MARGO_EVIDENCE_DIR/$STAMP.json"
( umask 027; python3 -I - "$RECORDS" "$EVIDENCE" "$STAMP" "$MARGO_REPO_REVISION" "$MARGO_NODE_VERSION" "$MARGO_COPILOT_VERSION" <<'EOF'
import json, platform, socket, sys
records = [json.loads(line) for line in open(sys.argv[1], encoding="utf-8") if line.strip()]
summary = {"required_failed": sum(1 for r in records if r["required"] and r["status"] == "failed"),
           "required_ok": sum(1 for r in records if r["required"] and r["status"] == "ok"),
           "optional_failed": sum(1 for r in records if not r["required"] and r["status"] == "failed"),
           "skipped": sum(1 for r in records if r["status"] == "skipped")}
document = {"schema_version": 1, "kind": "margo-phase0-evidence", "recorded_at": sys.argv[3],
            "hostname": socket.gethostname(),
            "pinned": {"repo_revision": sys.argv[4], "node": sys.argv[5], "copilot": sys.argv[6],
                       "python": platform.python_version()},
            "checks": records, "summary": summary,
            "result": "pass" if summary["required_failed"] == 0 else "fail"}
with open(sys.argv[2], "w", encoding="utf-8") as stream:
    json.dump(document, stream, indent=2, sort_keys=True)
    stream.write("\n")
EOF
)
chown "root:$MARGO_USER" "$EVIDENCE"

printf '%-24s %-9s %-8s %s\n' CHECK REQUIRED STATUS DETAIL
python3 -I -c 'import json, sys
for line in open(sys.argv[1], encoding="utf-8"):
    if not line.strip():
        continue
    record = json.loads(line)
    print("%-24s %-9s %-8s %s" % (record["name"], "yes" if record["required"] else "no", record["status"], record["detail"][:90]))' "$RECORDS"
printf '\nevidence: %s\n' "$EVIDENCE"
if [ "$PRINT_JSON" -eq 1 ]; then cat "$EVIDENCE"; fi

if [ "$FAILED_REQUIRED" -gt 0 ]; then
  printf 'result: FAIL (%d required check(s) failed)\n' "$FAILED_REQUIRED"
  exit 1
fi
printf 'result: PASS (every required check ok; optional failures, if any, are listed above)\n'
