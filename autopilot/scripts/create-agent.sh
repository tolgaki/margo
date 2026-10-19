#!/bin/sh
# Create a hosted-agent version that speaks the Teams activity protocol, wait for it to become
# active, then require Bot Service RBAC on its endpoint. Mirrors Microsoft's autopilot sample.
. "$(dirname -- "$0")/lib.sh"
need AZURE_AI_PROJECT_ENDPOINT AGENT_NAME TENANT_ID MODEL_NAME MARGO_STATE_BLOB_URL
MARGO_IMAGE=${MARGO_IMAGE:-$(azd env get-value MARGO_IMAGE 2>/dev/null || true)}
need MARGO_IMAGE

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

MARGO_PRINCIPAL_ACCESS=${MARGO_PRINCIPAL_ACCESS:-obo}
case "$MARGO_PRINCIPAL_ACCESS" in obo|delegation) ;; *) die "MARGO_PRINCIPAL_ACCESS must be obo or delegation" ;; esac

export MARGO_IMAGE MARGO_PRINCIPAL_ACCESS
python3 - "$work/body.json" <<'PY'
import json, os, sys
env = {
    "CONNECTIONS__SERVICE_CONNECTION__SETTINGS__AUTHORITY": "https://login.microsoftonline.com/" + os.environ["TENANT_ID"],
    "CONNECTIONS__SERVICE_CONNECTION__SETTINGS__TENANTID": os.environ["TENANT_ID"],
    "ModelDeployment": os.environ["MODEL_NAME"],
    "MARGO_STATE_BLOB_URL": os.environ["MARGO_STATE_BLOB_URL"],
    "MARGO_PRINCIPAL_ACCESS": os.environ["MARGO_PRINCIPAL_ACCESS"],
    "MARGO_TIMEZONE": os.environ.get("MARGO_TIMEZONE", "America/Los_Angeles"),
    "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT": "false",
}
if os.environ.get("PRINCIPAL_AUTH_HANDLER"):
    env["PRINCIPAL_AUTH_HANDLER"] = os.environ["PRINCIPAL_AUTH_HANDLER"]
if os.environ.get("MARGO_HOSTING") == "instance":
    # Hybrid hosting: this Foundry agent keeps serving chat, stores each instance's state where that
    # instance's own container reads it, and asks the provisioner to create those containers.
    env.update({"MARGO_STATE_LAYOUT": "instance",
                "MARGO_PROVISION_QUEUE_URL": os.environ["PROVISION_QUEUE_URL"],
                "MARGO_PUBLIC_BASE_URL": os.environ["ROUTER_URL"]})
    if os.environ.get("MARGO_VOICE_LIVE_MODEL"):
        env["VOICE_LIVE_MODEL"] = os.environ["MARGO_VOICE_LIVE_MODEL"]
body = {
    "definition": {
        "kind": "hosted", "image": os.environ["MARGO_IMAGE"], "cpu": "1", "memory": "2Gi",
        "environment_variables": env,
        "container_protocol_versions": [{"protocol": "activity_protocol", "version": "2.0.0"}],
    },
    "metadata": {"enableVnextExperience": "true"},
    "description": "Margo: a personal chief-of-staff autopilot. Proposes; never sends without approval.",
    "agent_endpoint": {"protocols": ["activity"],
                       "protocol_configuration": {"activity": {"enable_m365_public_endpoint": True}}},
    "digital_worker_type": "m365",
}
json.dump(body, open(sys.argv[1], "w"))
PY

base="$AZURE_AI_PROJECT_ENDPOINT/agents/$AGENT_NAME"
say "creating agent version for $AGENT_NAME"
status=$(foundry_call POST "$base/versions?api-version=$API_VERSION" "$work/body.json" "$work/created.json" \
  "Foundry-Features: DigitalWorker=V1Preview")
case "$status" in 2*) ;; *) cat "$work/created.json" >&2; die "agent creation failed (HTTP $status)" ;; esac

version=$(json_get "$work/created.json" 'd.get("version")')
[ -n "$version" ] || die "agent creation returned no version"
state=$(json_get "$work/created.json" 'd.get("status")')
tries=0
while [ "$state" != "active" ] && [ "$state" != "failed" ] && [ $tries -lt 60 ]; do
  sleep 10; tries=$((tries + 1))
  token=$(foundry_token)
  curl -sS -o "$work/poll.json" "$base/versions/$version?api-version=$API_VERSION" \
    -H "Authorization: Bearer $token" -H "Foundry-Features: DigitalWorker=V1Preview" || true
  state=$(json_get "$work/poll.json" 'd.get("status")' 2>/dev/null || echo unknown)
  say "version $version: $state"
  [ -s "$work/poll.json" ] && cp "$work/poll.json" "$work/created.json"
done
[ "$state" = "active" ] || die "agent version $version is '$state', not active"

blueprint=$(json_get "$work/created.json" '(d.get("blueprint") or {}).get("client_id")')
instance=$(json_get "$work/created.json" '(d.get("instance_identity") or {}).get("client_id")')
[ -n "$blueprint" ] && [ -n "$instance" ] || die "agent identities were not returned"
azd env set BLUEPRINT_CLIENT_ID "$blueprint" >/dev/null
azd env set AGENT_INSTANCE_CLIENT_ID "$instance" >/dev/null
azd env set AGENT_VERSION "$version" >/dev/null

printf '%s' '{"agent_endpoint":{"protocols":["activity"],"protocol_configuration":{"activity":{"enable_m365_public_endpoint":true}},"authorization_schemes":[{"type":"BotServiceRbac"}]}}' > "$work/patch.json"
status=$(foundry_call PATCH "$base?api-version=$API_VERSION" "$work/patch.json" "$work/patched.json" \
  "Foundry-Features: DigitalWorker=V1Preview")
case "$status" in 2*) say "endpoint requires Bot Service RBAC" ;; *) cat "$work/patched.json" >&2; die "endpoint patch failed (HTTP $status)" ;; esac
say "agent version $version active"
