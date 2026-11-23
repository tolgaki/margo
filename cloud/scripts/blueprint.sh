#!/bin/sh
# Connect the Agent 365 blueprint (created by the Agent 365 CLI) to Margo's containers. Idempotent.
#   1. A federated credential on the blueprint that trusts the shared, role-less blueprint-credential
#      managed identity, so instance containers act as the blueprint without any secret.
#   2. Work IQ's delegated scopes (WorkIQAgent.Ask, WorkIQ.Read, WorkIQ.Act): consented for the blueprint and inheritable by the
#      agent identities created from it, so each hired Margo can call Work IQ as her agent user.
# Progress goes to stderr; any step that needs an administrator instead is printed to stdout.
# A Microsoft Graph read that fails stops the script (exit 1) with the error: "could not read" is never treated as
# "not there", which would report the setup as done or overwrite what exists.
. "$(dirname -- "$0")/lib.sh"
BLUEPRINT_CLIENT_ID=${BLUEPRINT_CLIENT_ID:-${MARGO_BLUEPRINT_CLIENT_ID:-}}
need TENANT_ID BLUEPRINT_CLIENT_ID BLUEPRINT_CREDENTIAL_CLIENT_ID BLUEPRINT_CREDENTIAL_PRINCIPAL_ID

progress() { say "$@" >&2; }
fic_name="margo-instance-containers"
fic_issuer="https://login.microsoftonline.com/$TENANT_ID/v2.0"
# WorkIQ.Act lets the token act; Margo's guard still refuses every write without the manager's approval of that exact
# action, so the permission alone sends nothing.
workiq_scopes="WorkIQAgent.Ask WorkIQ.Read WorkIQ.Act"
workiq_scope=$workiq_scopes
# has_scopes "GRANTED SPACE-SEPARATED" -> 0 when every wanted scope is present
has_scopes() { for wanted in $workiq_scopes; do case " $1 " in *" $wanted "*) ;; *) return 1 ;; esac; done; return 0; }
# graph_get URL -> the response JSON; a failed read stops the script (az prints the reason to stderr).
graph_get() { az rest --method get --url "$1" -o json; }

found=$(graph_get "$BLUEPRINTS?\$filter=appId%20eq%20%27$BLUEPRINT_CLIENT_ID%27&\$select=id") \
  || die "could not read blueprint $BLUEPRINT_CLIENT_ID from Microsoft Graph (signed in to tenant $TENANT_ID?); nothing was changed"
blueprint_id=$(printf '%s' "$found" | json_field '((d.get("value") or [{}])[0]).get("id")')
[ -n "$blueprint_id" ] || die "blueprint $BLUEPRINT_CLIENT_ID is not an agent identity blueprint in tenant $TENANT_ID. Check the ID the
       Agent 365 CLI wrote (agentBlueprintId) and the tenant you are signed in to; no instance can sign in until it is connected."

progress "blueprint: federated credential for instance containers"
credentials=$(graph_get "$BLUEPRINTS/$blueprint_id/federatedIdentityCredentials") \
  || die "could not read the federated credentials of blueprint $BLUEPRINT_CLIENT_ID"
fic_filter='[c for c in (d.get("value") or []) if c.get("name") == "'"$fic_name"'"]'
fic_id=$(printf '%s' "$credentials" | json_field "(($fic_filter or [{}])[0]).get('id')")
fic_subject=$(printf '%s' "$credentials" | json_field "(($fic_filter or [{}])[0]).get('subject')")
fic_current_issuer=$(printf '%s' "$credentials" | json_field "(($fic_filter or [{}])[0]).get('issuer')")
fic_body="\"issuer\": \"$fic_issuer\", \"subject\": \"$BLUEPRINT_CREDENTIAL_PRINCIPAL_ID\", \"audiences\": [\"api://AzureADTokenExchange\"], \"description\": \"Margo instance containers act as this blueprint\""
fic_note="issuer $fic_issuer,
      subject $BLUEPRINT_CREDENTIAL_PRINCIPAL_ID, audience api://AzureADTokenExchange
      (the managed identity $BLUEPRINT_CREDENTIAL_CLIENT_ID)."
if [ -n "$fic_id" ] && [ "$fic_subject" = "$BLUEPRINT_CREDENTIAL_PRINCIPAL_ID" ] && [ "$fic_current_issuer" = "$fic_issuer" ]; then
  progress "federated credential already present"
elif [ -n "$fic_id" ]; then
  # Same name, another identity (the managed identity was recreated, or the environment moved): instances would be
  # refused at sign-in although a credential "exists".
  if az rest --method patch --url "$BLUEPRINTS/$blueprint_id/federatedIdentityCredentials/$fic_id" \
      --headers "Content-Type=application/json" --body "{$fic_body}" >/dev/null; then
    progress "federated credential updated to the current blueprint-credential identity"
  else
    printf '%s\n' "    - The blueprint's federated credential '$fic_name' trusts another identity (subject $fic_subject) and could
      not be updated. An Entra administrator (Agent ID Administrator) sets it on blueprint $BLUEPRINT_CLIENT_ID to:
      $fic_note Until then no instance can sign in."
  fi
elif az rest --method post --url "$BLUEPRINTS/$blueprint_id/federatedIdentityCredentials" \
    --headers "Content-Type=application/json" --body "{\"name\": \"$fic_name\", $fic_body}" >/dev/null; then
  progress "federated credential added"
else
  printf '%s\n' "    - An Entra administrator (Agent ID Administrator) adds a federated credential to blueprint
      $BLUEPRINT_CLIENT_ID: $fic_note Until then no instance can sign in."
fi

progress "blueprint: Work IQ access ($workiq_scope)"
# 'az ad sp show' fails both when Work IQ has no service principal here and when the directory cannot be read.
if workiq=$(az ad sp show --id "api://workiq.svc.cloud.microsoft" -o json 2>&1); then
  workiq_app=$(printf '%s' "$workiq" | json_field 'd.get("appId")')
  workiq_sp=$(printf '%s' "$workiq" | json_field 'd.get("id")')
else
  case "$workiq" in
    *"does not exist"*|*"doesn't exist"*|*"not found"*|*"Not Found"*|*Request_ResourceNotFound*) workiq_app=""; workiq_sp="" ;;
    *) printf '%s\n' "$workiq" >&2; die "could not look up Work IQ's service principal in tenant $TENANT_ID" ;;
  esac
fi
if [ -z "$workiq_app" ] || [ -z "$workiq_sp" ]; then
  printf '%s\n' "    - Work IQ is not enabled in this tenant (no service principal for api://workiq.svc.cloud.microsoft).
      Enable Work IQ, then re-run: sh scripts/blueprint.sh (with the azd environment loaded)."
  exit 0
fi

# Inheritable: agent identities created from the blueprint inherit the blueprint's consented Work IQ scopes.
# Agent 365 expects kind=allAllowed (the consent grant below is what limits it to these scopes); an enumerated
# entry is reported as broken inheritance by 'a365 query-entra inheritance'.
inheritable=$(graph_get "$BLUEPRINTS/$blueprint_id/inheritablePermissions") \
  || die "could not read the inheritable permissions of blueprint $BLUEPRINT_CLIENT_ID"
entry_filter='[p for p in (d.get("value") or []) if p.get("resourceAppId") == "'"$workiq_app"'"]'
inherited_kind=$(printf '%s' "$inheritable" | json_field "(((($entry_filter or [{}])[0]).get('inheritableScopes')) or {}).get('kind')")
# What is there now, as a body that puts it back (the entry without the fields Graph adds).
previous=$(printf '%s' "$inheritable" | json_field "json.dumps({k: v for k, v in (($entry_filter or [{}])[0]).items() if k in ('resourceAppId', 'inheritableScopes', 'inheritableRoles') and v is not None})")
all_allowed="\"inheritableScopes\": {\"@odata.type\": \"microsoft.graph.allAllowedScopes\", \"kind\": \"allAllowed\"}, \"inheritableRoles\": {\"@odata.type\": \"microsoft.graph.allAllowedRoles\", \"kind\": \"allAllowed\"}"
inheritable_url="$BLUEPRINTS/$blueprint_id/inheritablePermissions"
inheritable_note="    - An Entra administrator makes Work IQ (resource app $workiq_app) inheritable on blueprint
      $BLUEPRINT_CLIENT_ID: a365 setup permissions custom --resource-app-id $workiq_app --scopes $(printf '%s' "$workiq_scopes" | tr ' ' ',')"
add_inheritable() {
  az rest --method post --url "$inheritable_url" --headers "Content-Type=application/json" \
    --body "{\"resourceAppId\": \"$workiq_app\", $all_allowed}" >/dev/null 2>&1
}
if [ "$inherited_kind" = "allAllowed" ]; then
  progress "Work IQ scopes already inheritable"
elif [ -z "$inherited_kind" ]; then
  if add_inheritable; then progress "Work IQ scopes made inheritable"; else printf '%s\n' "$inheritable_note"; fi
# An entry of another kind exists, and agents inherit Work IQ through it today. Change it in place if Graph allows;
# otherwise replace it, and put it back if the replacement is refused: a failure never leaves agents without it.
elif az rest --method patch --url "$inheritable_url/$workiq_app" --headers "Content-Type=application/json" \
    --body "{$all_allowed}" >/dev/null 2>&1; then
  progress "Work IQ scopes made inheritable (entry updated)"
elif ! az rest --method delete --url "$inheritable_url/$workiq_app" >/dev/null 2>&1; then
  printf '%s\n' "$inheritable_note
      (the existing '$inherited_kind' entry was left as it is)."
elif add_inheritable; then
  progress "Work IQ scopes made inheritable (entry replaced)"
elif az rest --method post --url "$inheritable_url" --headers "Content-Type=application/json" --body "$previous" >/dev/null; then
  printf '%s\n' "$inheritable_note
      (the existing '$inherited_kind' entry was put back)."
else
  die "Work IQ inheritance was removed from blueprint $BLUEPRINT_CLIENT_ID and could not be put back: agents created from it
       cannot call Work IQ. Restore it now: a365 setup permissions custom --resource-app-id $workiq_app --scopes $(printf '%s' "$workiq_scopes" | tr ' ' ',')
       (it was: $previous)"
fi

# Tenant-wide consent for the blueprint's principal; an administrator's sign-in is needed here.
principals=$(graph_get "$GRAPH_BETA/servicePrincipals?\$filter=appId%20eq%20%27$BLUEPRINT_CLIENT_ID%27&\$select=id") \
  || die "could not look up the service principal of blueprint $BLUEPRINT_CLIENT_ID"
principal=$(printf '%s' "$principals" | json_field '((d.get("value") or [{}])[0]).get("id")')
grant_id=""; granted=""
if [ -n "$principal" ]; then
  grants=$(graph_get "https://graph.microsoft.com/v1.0/oauth2PermissionGrants?\$filter=clientId%20eq%20%27$principal%27%20and%20resourceId%20eq%20%27$workiq_sp%27") \
    || die "could not read the Work IQ consent of blueprint $BLUEPRINT_CLIENT_ID"
  grant_filter='[g for g in (d.get("value") or []) if g.get("consentType") == "AllPrincipals"]'
  grant_id=$(printf '%s' "$grants" | json_field "(($grant_filter or [{}])[0]).get('id')")
  granted=$(printf '%s' "$grants" | json_field "(($grant_filter or [{}])[0]).get('scope')")
fi
if [ -n "$grant_id" ] && has_scopes "$granted"; then
  progress "Work IQ consent already granted"
elif [ -n "$grant_id" ] && az rest --method patch --url "https://graph.microsoft.com/v1.0/oauth2PermissionGrants/$grant_id" \
    --headers "Content-Type=application/json" \
    --body "{\"scope\": \"$(printf '%s %s' "$granted" "$workiq_scopes" | tr ' ' '\n' | awk 'NF && !seen[$0]++' | tr '\n' ' ' | sed 's/ $//')\"}" >/dev/null 2>&1; then
  progress "Work IQ consent extended to: $workiq_scopes"
elif [ -z "$grant_id" ] && [ -n "$principal" ] && az rest --method post --url "https://graph.microsoft.com/v1.0/oauth2PermissionGrants" \
    --headers "Content-Type=application/json" \
    --body "{\"clientId\": \"$principal\", \"consentType\": \"AllPrincipals\", \"resourceId\": \"$workiq_sp\", \"scope\": \"$workiq_scope\"}" >/dev/null 2>&1; then
  progress "Work IQ consent granted"
else
  printf '%s\n' "    - A Global or Privileged Role Administrator grants admin consent for blueprint $BLUEPRINT_CLIENT_ID
      to Work IQ's $workiq_scope (delegated). Without it, Margo answers but every Work IQ read is blocked."
fi
