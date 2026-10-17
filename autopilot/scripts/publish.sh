#!/bin/sh
# Publish Margo as an autopilot blueprint for admin approval. Uses config/publish.template.json,
# fills the version and blueprint ID, and requests only Work IQ's delegated WorkIQAgent.Ask scope.
# The Work IQ application ID is looked up in your tenant, not hard-coded.
. "$(dirname -- "$0")/lib.sh"
need AZURE_AI_PROJECT_ENDPOINT AGENT_NAME TENANT_ID
BLUEPRINT_CLIENT_ID=${BLUEPRINT_CLIENT_ID:-$(azd env get-value BLUEPRINT_CLIENT_ID 2>/dev/null || true)}
need BLUEPRINT_CLIENT_ID

workiq_app=$(az ad sp show --id "api://workiq.svc.cloud.microsoft" --query appId -o tsv 2>/dev/null || true)
[ -n "$workiq_app" ] || die "Work IQ is not enabled in this tenant (no service principal for api://workiq.svc.cloud.microsoft). Enable Work IQ first."

version=${MARGO_APP_VERSION:-$(cat "$REPO/VERSION" 2>/dev/null || echo 0.1.0)}
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
export BLUEPRINT_CLIENT_ID workiq_app version
python3 - "$AUTOPILOT/config/publish.template.json" "$work/publish.json" <<'PY'
import json, os, sys
body = json.load(open(sys.argv[1]))
body["appVersion"] = os.environ["version"]
body["agenticUserTemplate"]["AgentIdentityBlueprintId"] = os.environ["BLUEPRINT_CLIENT_ID"]
body["optionalPermissionScopes"] = [{"resourceAppId": os.environ["workiq_app"], "scopes": ["WorkIQAgent.Ask"]}]
left = [k for k, v in body.items() if isinstance(v, str) and v.startswith("{")]
if left:
    sys.exit("unfilled placeholders: " + ", ".join(left))
json.dump(body, open(sys.argv[2], "w"))
PY

say "publishing Margo $version for admin approval"
status=$(foundry_call POST "$AZURE_AI_PROJECT_ENDPOINT/agents/$AGENT_NAME/microsoft365/publish?api-version=$API_VERSION" \
  "$work/publish.json" "$work/published.json")
case "$status" in
  2*) say "published; an admin must now approve it in the Microsoft 365 admin center" ;;
  *)
    if grep -q "version already exists" "$work/published.json" 2>/dev/null; then
      say "version $version is already published; set MARGO_APP_VERSION to publish a new one"
    else
      cat "$work/published.json" >&2; die "publish failed (HTTP $status)"
    fi ;;
esac
