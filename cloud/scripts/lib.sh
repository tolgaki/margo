#!/bin/sh
# Shared helpers for the Margo Cloud deployment scripts. POSIX sh; needs az, azd, python3.
set -eu

HERE=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
CLOUD=$(CDPATH='' cd -- "$HERE/.." && pwd)
# shellcheck disable=SC2034  # used by the scripts that source this file
REPO=$(CDPATH='' cd -- "$CLOUD/.." && pwd)

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

# assignments_at SCOPE [CONDITION]  -- reads 'az role assignment list -o json' on stdin and prints, for each
# assignment at exactly SCOPE (compared without case, as Azure returns scopes in either), "same ID" when its condition
# is CONDITION (compared without regard to white space) and "other ID" when it is not.
assignments_at() {
  python3 -c '
import json, sys
scope, wanted = sys.argv[1].rstrip("/").lower(), " ".join(sys.argv[2].split())
for assignment in json.load(sys.stdin):
    if (assignment.get("scope") or "").rstrip("/").lower() == scope:
        same = " ".join((assignment.get("condition") or "").split()) == wanted
        print("same" if same else "other", assignment["id"])
' "$1" "${2:-}"
}

# json_field EXPRESSION  -- reads JSON on stdin (empty input counts as null) and prints a Python expression on `d`.
json_field() {
  python3 -c '
import json, sys
text = sys.stdin.read().strip()
d = json.loads(text) if text else None
value = eval(sys.argv[1], {"__builtins__": {}, "d": d, "json": json})
print("" if value is None else value)
' "$1"
}

GRAPH_BETA="https://graph.microsoft.com/beta"
# Agent identity blueprints are agentIdentityBlueprint applications, which 'az ad app' (the plain
# applications endpoint) does not find; address them through the Graph type cast instead.
# shellcheck disable=SC2034  # used by the scripts that source this file
BLUEPRINTS="$GRAPH_BETA/applications/graph.agentIdentityBlueprint"
