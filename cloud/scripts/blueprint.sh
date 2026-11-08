#!/bin/sh
# Connect the Agent 365 blueprint (created by the Agent 365 CLI) to Margo's containers. Idempotent.
#   1. A federated credential on the blueprint that trusts the shared, role-less blueprint-credential
#      managed identity, so instance containers act as the blueprint without any secret.
#   2. Work IQ's delegated scopes (WorkIQAgent.Ask, WorkIQ.Read, WorkIQ.Act): consented for the blueprint and inheritable by the
#      agent identities created from it, so each hired Margo can call Work IQ as her agent user.
# Progress goes to stderr; any step that needs an administrator instead is printed to stdout.
. "$(dirname -- "$0")/lib.sh"
BLUEPRINT_CLIENT_ID=${BLUEPRINT_CLIENT_ID:-${MARGO_BLUEPRINT_CLIENT_ID:-}}
need TENANT_ID BLUEPRINT_CLIENT_ID BLUEPRINT_CREDENTIAL_CLIENT_ID BLUEPRINT_CREDENTIAL_PRINCIPAL_ID

progress() { say "$@" >&2; }
fic_name="margo-instance-containers"
# WorkIQ.Act lets the token act; Margo's guard still refuses every write without the manager's approval of that exact
# action, so the permission alone sends nothing.
workiq_scopes="WorkIQAgent.Ask WorkIQ.Read WorkIQ.Act"
workiq_scope=$workiq_scopes
scopes_json=$(printf '%s' "$workiq_scopes" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read().split()))')
# has_scopes "GRANTED SPACE-SEPARATED" -> 0 when every wanted scope is present
has_scopes() { for wanted in $workiq_scopes; do case " $1 " in *" $wanted "*) ;; *) return 1 ;; esac; done; return 0; }

blueprint_id=$(az rest --method get \
  --url "$BLUEPRINTS?\$filter=appId%20eq%20%27$BLUEPRINT_CLIENT_ID%27&\$select=id" \
  --query "value[0].id" -o tsv 2>/dev/null || true)
if [ -z "$blueprint_id" ]; then
  printf '%s\n' "    - Blueprint $BLUEPRINT_CLIENT_ID was not found as an agent identity blueprint. Check the ID the
      Agent 365 CLI wrote (agentBlueprintId) and that you are signed in to tenant $TENANT_ID."
  exit 0
fi

progress "blueprint: federated credential for instance containers"
existing=$(az rest --method get --url "$BLUEPRINTS/$blueprint_id/federatedIdentityCredentials" \
  --query "value[?name=='$fic_name'] | length(@)" -o tsv 2>/dev/null || true)
if [ "$existing" = "1" ]; then
  progress "federated credential already present"
elif az rest --method post --url "$BLUEPRINTS/$blueprint_id/federatedIdentityCredentials" \
    --headers "Content-Type=application/json" \
    --body "{\"name\": \"$fic_name\", \"issuer\": \"https://login.microsoftonline.com/$TENANT_ID/v2.0\", \"subject\": \"$BLUEPRINT_CREDENTIAL_PRINCIPAL_ID\", \"audiences\": [\"api://AzureADTokenExchange\"], \"description\": \"Margo instance containers act as this blueprint\"}" >/dev/null 2>&1; then
  progress "federated credential added"
else
  printf '%s\n' "    - An Entra administrator (Agent ID Administrator) adds a federated credential to blueprint
      $BLUEPRINT_CLIENT_ID: issuer https://login.microsoftonline.com/$TENANT_ID/v2.0,
      subject $BLUEPRINT_CREDENTIAL_PRINCIPAL_ID, audience api://AzureADTokenExchange
      (the managed identity $BLUEPRINT_CREDENTIAL_CLIENT_ID). Until then no instance can sign in."
fi

progress "blueprint: Work IQ access ($workiq_scope)"
workiq_app=$(az ad sp show --id "api://workiq.svc.cloud.microsoft" --query appId -o tsv 2>/dev/null || true)
workiq_sp=$(az ad sp show --id "api://workiq.svc.cloud.microsoft" --query id -o tsv 2>/dev/null || true)
if [ -z "$workiq_app" ] || [ -z "$workiq_sp" ]; then
  printf '%s\n' "    - Work IQ is not enabled in this tenant (no service principal for api://workiq.svc.cloud.microsoft).
      Enable Work IQ, then re-run: sh scripts/blueprint.sh (with the azd environment loaded)."
  exit 0
fi

# Inheritable: agent identities created from the blueprint may request this scope, and only it.
inherited=$(az rest --method get --url "$BLUEPRINTS/$blueprint_id/inheritablePermissions" \
  --query "value[?resourceAppId=='$workiq_app'] | [0].inheritableScopes.scopes" -o tsv 2>/dev/null | tr '\t\n' '  ' || true)
if [ -n "$inherited" ] && has_scopes "$inherited"; then
  progress "Work IQ scopes already inheritable"
elif [ -n "$inherited" ]; then
  printf '%s\n' "    - Work IQ is inheritable on blueprint $BLUEPRINT_CLIENT_ID with only: $inherited. An Entra administrator
      adds the missing ones of: $workiq_scopes (resource app $workiq_app)."
elif az rest --method post --url "$BLUEPRINTS/$blueprint_id/inheritablePermissions" \
    --headers "Content-Type=application/json" \
    --body "{\"resourceAppId\": \"$workiq_app\", \"inheritableScopes\": {\"@odata.type\": \"microsoft.graph.enumeratedScopes\", \"scopes\": $scopes_json}}" >/dev/null 2>&1; then
  progress "Work IQ scope made inheritable"
else
  printf '%s\n' "    - An Entra administrator makes Work IQ's $workiq_scope scope (resource app $workiq_app)
      inheritable on blueprint $BLUEPRINT_CLIENT_ID, for example with the Agent 365 CLI's permission setup."
fi

# Tenant-wide consent for the blueprint's principal; an administrator's sign-in is needed here.
principal=$(az rest --method get \
  --url "$GRAPH_BETA/servicePrincipals?\$filter=appId%20eq%20%27$BLUEPRINT_CLIENT_ID%27&\$select=id" \
  --query "value[0].id" -o tsv 2>/dev/null || true)
grant_id=""; granted=""
if [ -n "$principal" ]; then
  grant=$(az rest --method get \
    --url "https://graph.microsoft.com/v1.0/oauth2PermissionGrants?\$filter=clientId%20eq%20%27$principal%27%20and%20resourceId%20eq%20%27$workiq_sp%27" \
    --query "value[?consentType=='AllPrincipals'] | [0].[id, scope]" -o tsv 2>/dev/null || true)
  grant_id=$(printf '%s' "$grant" | cut -f1); granted=$(printf '%s' "$grant" | cut -f2)
fi
if [ -n "$grant_id" ] && has_scopes "$granted"; then
  progress "Work IQ consent already granted"
elif [ -n "$grant_id" ] && az rest --method patch --url "https://graph.microsoft.com/v1.0/oauth2PermissionGrants/$grant_id" \
    --headers "Content-Type=application/json" \
    --body "{\"scope\": \"$(printf '%s %s' "$granted" "$workiq_scopes" | tr ' ' '\n' | awk 'NF && !seen[$0]++' | tr '\n' ' ' | sed 's/ $//')\"}" >/dev/null 2>&1; then
  progress "Work IQ consent extended to: $workiq_scopes"
elif [ -n "$principal" ] && az rest --method post --url "https://graph.microsoft.com/v1.0/oauth2PermissionGrants" \
    --headers "Content-Type=application/json" \
    --body "{\"clientId\": \"$principal\", \"consentType\": \"AllPrincipals\", \"resourceId\": \"$workiq_sp\", \"scope\": \"$workiq_scope\"}" >/dev/null 2>&1; then
  progress "Work IQ consent granted"
else
  printf '%s\n' "    - A Global or Privileged Role Administrator grants admin consent for blueprint $BLUEPRINT_CLIENT_ID
      to Work IQ's $workiq_scope (delegated). Without it, Margo answers but every Work IQ read is blocked."
fi
