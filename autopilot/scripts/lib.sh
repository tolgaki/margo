#!/bin/sh
# Shared helpers for the Margo autopilot deployment scripts. POSIX sh; needs az, azd, python3.
set -eu

HERE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
AUTOPILOT=$(CDPATH= cd -- "$HERE/.." && pwd)
REPO=$(CDPATH= cd -- "$AUTOPILOT/.." && pwd)
API_VERSION="2025-11-15-preview"

say() { printf '%s\n' "==> $*"; }
die() { printf '%s\n' "ERROR: $*" >&2; exit 1; }

need() {
  for name in "$@"; do
    eval "value=\${$name:-}"
    [ -n "$value" ] || die "$name is not set. Run from 'azd provision' or 'azd env get-values'."
  done
}

foundry_token() {
  az account get-access-token --resource https://ai.azure.com --tenant "$TENANT_ID" --query accessToken -o tsv
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

# foundry_call METHOD URL BODY_FILE OUT_FILE [EXTRA_HEADER]  -> HTTP status on stdout.
foundry_call() {
  token=$(foundry_token)
  if [ -n "${5:-}" ]; then
    curl -sS -o "$4" -w '%{http_code}' -X "$1" "$2" -H "Authorization: Bearer $token" \
      -H "Content-Type: application/json" -H "Accept: application/json" -H "$5" --data-binary "@$3"
  else
    curl -sS -o "$4" -w '%{http_code}' -X "$1" "$2" -H "Authorization: Bearer $token" \
      -H "Content-Type: application/json" -H "Accept: application/json" --data-binary "@$3"
  fi
}
