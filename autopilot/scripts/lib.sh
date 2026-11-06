#!/bin/sh
# Shared helpers for the Margo autopilot deployment scripts. POSIX sh; needs az, azd, python3.
set -eu

HERE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
AUTOPILOT=$(CDPATH= cd -- "$HERE/.." && pwd)
REPO=$(CDPATH= cd -- "$AUTOPILOT/.." && pwd)

say() { printf '%s\n' "==> $*"; }
die() { printf '%s\n' "ERROR: $*" >&2; exit 1; }

need() {
  for name in "$@"; do
    eval "value=\${$name:-}"
    [ -n "$value" ] || die "$name is not set. Run from 'azd provision' or 'azd env get-values'."
  done
}

# json_get FILE EXPRESSION  -> prints a value from a JSON file using a Python expression on `d`.
json_get() {
  python3 - "$1" "$2" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
value = eval(sys.argv[2], {"__builtins__": {}}, {"d": d})
print("" if value is None else value)
PY
}

# assign_role ASSIGNEE_OBJECT_ID ROLE_NAME SCOPE  -- idempotent, by role *name*.
assign_role() {
  output=$(az role assignment create --assignee-object-id "$1" --assignee-principal-type ServicePrincipal \
    --role "$2" --scope "$3" 2>&1) && { say "granted '$2'"; return 0; }
  case "$output" in
    *RoleAssignmentExists*|*"already exists"*) say "'$2' already granted" ;;
    *) printf '%s\n' "$output" >&2; die "could not grant '$2'" ;;
  esac
}

# role_id NAME -> role definition GUID, looked up by name in this tenant.
role_id() {
  id=$(az role definition list --name "$1" --query "[0].name" -o tsv 2>/dev/null || true)
  [ -n "$id" ] || die "role '$1' not found in this tenant"
  printf '%s' "$id"
}

GRAPH_BETA="https://graph.microsoft.com/beta"
# Agent identity blueprints are agentIdentityBlueprint applications, which 'az ad app' (the plain
# applications endpoint) does not find; address them through the Graph type cast instead.
BLUEPRINTS="$GRAPH_BETA/applications/graph.agentIdentityBlueprint"
