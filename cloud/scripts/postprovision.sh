#!/bin/sh
# After 'azd provision' (docs/autopilot-deployment.md). Margo is self-hosted for Agent 365: the router
# is the agent blueprint's messaging endpoint and each hired Margo runs in her own container.
#   1. roles for the router and the provisioner (by name; the provisioner may grant only an instance's four roles,
#      on the resources they apply to, to service principals other than Margo's own identities), and the four
#      instance role IDs recorded in the azd environment (MARGO_*_ROLE_ID) for the provisioner job;
#   2. build the Margo image, pin it by digest and record it (MARGO_PINNED_IMAGE): the next provision starts the
#      router and the provisioner job on it (main.bicep margoImage) instead of the public placeholder;
#   3. once the Agent 365 CLI has created the blueprint (MARGO_BLUEPRINT_CLIENT_ID) and this provision started the
#      router on a pinned image, switch the router and provisioner to the new image and connect the blueprint
#      (scripts/blueprint.sh).
# If this stops early (an uncommitted change, a failed build), the router and the job keep the image, registry and
# settings the provision gave them: main.bicep sets all of them, and step 3 only moves both to a newer image.
# Every step is idempotent; re-run with 'azd hooks run postprovision'.
. "$(dirname -- "$0")/lib.sh"
need TENANT_ID SUBSCRIPTION_ID RESOURCE_GROUP AZURE_CONTAINER_REGISTRY_ENDPOINT AZURE_CONTAINER_REGISTRY_ID \
  ROUTER_NAME ROUTER_URL MESSAGING_ENDPOINT ROUTER_PRINCIPAL_ID ROUTER_IDENTITY_ID PROVISIONER_NAME \
  PROVISIONER_PRINCIPAL_ID PROVISIONER_IDENTITY_ID ROUTES_CONTAINER_ID PROVISION_QUEUE_ID \
  STORAGE_ACCOUNT_ID ACCOUNT_ID BLUEPRINT_CREDENTIAL_PRINCIPAL_ID

group_scope="/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP"
# The image this provision gave the router (main.bicep margoImage); empty: the router still runs the placeholder, on
# its port and without the router's command, so its image must not be switched in place.
deployed_image=${MARGO_PINNED_IMAGE:-}

blob_role=$(role_id "Storage Blob Data Contributor")
pull_role=$(role_id "AcrPull")
model_role=$(role_id "Cognitive Services OpenAI User")
speech_role=$(role_id "Cognitive Services User")
# Recorded before anything below can fail, so every later provision gives the job these IDs (main.bicep
# provisionerConfig) whether or not this script finishes.
azd env set MARGO_STORAGE_ROLE_ID "$blob_role" >/dev/null
azd env set MARGO_PULL_ROLE_ID "$pull_role" >/dev/null
azd env set MARGO_MODEL_ROLE_ID "$model_role" >/dev/null
azd env set MARGO_SPEECH_ROLE_ID "$speech_role" >/dev/null

say "router: read routes, queue provisioning requests, pull its image"
assign_role "$ROUTER_PRINCIPAL_ID" "Storage Blob Data Reader" "$ROUTES_CONTAINER_ID"
assign_role "$ROUTER_PRINCIPAL_ID" "Storage Queue Data Message Sender" "$PROVISION_QUEUE_ID"
assign_role "$ROUTER_PRINCIPAL_ID" "AcrPull" "$AZURE_CONTAINER_REGISTRY_ID"

say "provisioner: deploy instances into this resource group, keep the route table, drain the queue"
assign_role "$PROVISIONER_PRINCIPAL_ID" "Contributor" "$group_scope"
assign_role "$PROVISIONER_PRINCIPAL_ID" "Storage Blob Data Contributor" "$ROUTES_CONTAINER_ID"
assign_role "$PROVISIONER_PRINCIPAL_ID" "Storage Queue Data Message Processor" "$PROVISION_QUEUE_ID"
# The job's queue trigger reads the queue's length with the job's identity (main.bicep eventTriggerConfig).
assign_role "$PROVISIONER_PRINCIPAL_ID" "Storage Queue Data Reader" "$PROVISION_QUEUE_ID"
assign_role "$PROVISIONER_PRINCIPAL_ID" "AcrPull" "$AZURE_CONTAINER_REGISTRY_ID"
# Role administration, constrained by ABAC conditions, on each resource an instance needs a role on: the storage
# account (Storage Blob Data Contributor, on one instance's state container), the registry (AcrPull) and the AI account
# (the model and Voice Live roles). It may grant those roles only, only to service principals, and never to the
# router, the provisioner or the blueprint credential, so it cannot widen its own or the router's access or hand state
# to a person. Removal is limited to the same roles.
rbac_admin="Role Based Access Control Administrator"
# role_administration SCOPE  -> the provisioner's role-administration assignments listed for SCOPE, as JSON.
role_administration() {
  az role assignment list --assignee "$PROVISIONER_PRINCIPAL_ID" --role "$rbac_admin" --scope "$1" -o json
}
grant_role_administration() { # scope roles label
  write_check="@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {$2} AND @Request[Microsoft.Authorization/roleAssignments:PrincipalType] ForAnyOfAnyValues:StringEqualsIgnoreCase {'ServicePrincipal'} AND @Request[Microsoft.Authorization/roleAssignments:PrincipalId] ForAnyOfAllValues:GuidNotEquals {$PROVISIONER_PRINCIPAL_ID, $ROUTER_PRINCIPAL_ID, $BLUEPRINT_CREDENTIAL_PRINCIPAL_ID}"
  delete_check="@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {$2}"
  condition="((!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})) OR ($write_check)) AND ((!(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'})) OR ($delete_check))"
  listed=$(role_administration "$1") || die "could not read the provisioner's role administration on $3"
  found=$(printf '%s' "$listed" | assignments_at "$1" "$condition") || die "could not read the provisioner's role administration on $3"
  # An assignment whose stored condition is not the one wanted now (an identity was recreated, or an earlier version
  # wrote it) is replaced: the same role cannot be assigned twice at one scope, so the old one goes first.
  for id in $(printf '%s\n' "$found" | sed -n 's/^other //p'); do
    az role assignment delete --ids "$id" >/dev/null \
      || die "could not remove the outdated role administration on $3 ($id)"
    say "removed outdated role administration on $3"
  done
  if printf '%s\n' "$found" | grep -q '^same '; then
    say "constrained role administration on $3 already granted"
    return 0
  fi
  output=$(az role assignment create --assignee-object-id "$PROVISIONER_PRINCIPAL_ID" --assignee-principal-type ServicePrincipal \
    --role "$rbac_admin" --scope "$1" \
    --condition "$condition" --condition-version "2.0" 2>&1) \
    || { printf '%s\n' "$output" >&2; die "could not grant constrained role administration on $3; the provisioner cannot create instances until it is granted (run 'azd hooks run postprovision' again)"; }
  say "granted constrained role administration on $3"
}
grant_role_administration "$STORAGE_ACCOUNT_ID" "$blob_role" "the storage account"
grant_role_administration "$AZURE_CONTAINER_REGISTRY_ID" "$pull_role" "the registry"
grant_role_administration "$ACCOUNT_ID" "$model_role, $speech_role" "the AI account"
# Earlier versions granted it on the whole resource group, for any principal: remove that grant, and stop if it is
# still there afterwards (it would let the provisioner grant any role in the group).
group_wide() {
  listed=$(role_administration "$group_scope") || return 1
  found=$(printf '%s' "$listed" | assignments_at "$group_scope") || return 1
  printf '%s\n' "$found" | sed -n 's/^[a-z]* //p'
}
old=$(group_wide) || die "could not check for the earlier resource-group-wide role administration"
for id in $old; do
  az role assignment delete --ids "$id" >/dev/null \
    || die "could not remove the earlier resource-group-wide role administration ($id)"
  say "removed the earlier resource-group-wide role administration"
done
left=$(group_wide) || die "could not check for the earlier resource-group-wide role administration"
[ -z "$left" ] || die "the provisioner still has resource-group-wide role administration: $left"

sh "$HERE/build-image.sh"
MARGO_IMAGE=$(azd env get-value MARGO_IMAGE 2>/dev/null || true)
need MARGO_IMAGE
registry=$(printf '%s' "$AZURE_CONTAINER_REGISTRY_ENDPOINT" | cut -d. -f1)
repository_tag=${MARGO_IMAGE#*/}
digest=$(az acr repository show --name "$registry" --image "$repository_tag" --query digest -o tsv)
[ -n "$digest" ] || die "could not resolve the image digest"
pinned="$AZURE_CONTAINER_REGISTRY_ENDPOINT/${repository_tag%%:*}@$digest"
say "pinned image: $pinned"
azd env set MARGO_PINNED_IMAGE "$pinned" >/dev/null

BLUEPRINT_CLIENT_ID=${MARGO_BLUEPRINT_CLIENT_ID:-}
if [ -z "$BLUEPRINT_CLIENT_ID" ]; then
  cat <<EOF
==> Azure is ready; the router waits for an agent blueprint. Create it with the Agent 365 CLI
    (docs/autopilot-deployment.md, step 2), with this messaging endpoint:

      $MESSAGING_ENDPOINT

    Then, from the cloud folder:

      sh scripts/connect-blueprint.sh path/to/a365.generated.config.json
      azd provision
EOF
  exit 0
fi
case "$BLUEPRINT_CLIENT_ID" in
  *[!0-9a-fA-F-]*|"") die "MARGO_BLUEPRINT_CLIENT_ID is not a client ID: $BLUEPRINT_CLIENT_ID" ;;
esac
if [ -z "$deployed_image" ]; then
  cat <<EOF
==> The image is pinned, but this provision started the router on the placeholder (no image was pinned yet).
    Run 'azd provision' once more: it starts the router on the pinned image and finishes the setup.
EOF
  exit 0
fi

say "router: switch to the Margo image; check activity tokens against blueprint $BLUEPRINT_CLIENT_ID"
az containerapp registry set -n "$ROUTER_NAME" -g "$RESOURCE_GROUP" --server "$AZURE_CONTAINER_REGISTRY_ENDPOINT" \
  --identity "$ROUTER_IDENTITY_ID" >/dev/null
az containerapp update -n "$ROUTER_NAME" -g "$RESOURCE_GROUP" --image "$pinned" \
  --set-env-vars "CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID=$BLUEPRINT_CLIENT_ID" >/dev/null

say "provisioner: switch to the Margo image and record what instances run"
az containerapp job registry set -n "$PROVISIONER_NAME" -g "$RESOURCE_GROUP" --server "$AZURE_CONTAINER_REGISTRY_ENDPOINT" \
  --identity "$PROVISIONER_IDENTITY_ID" >/dev/null
az containerapp job update -n "$PROVISIONER_NAME" -g "$RESOURCE_GROUP" --image "$pinned" \
  --set-env-vars "MARGO_INSTANCE_IMAGE=$pinned" "MARGO_STORAGE_ROLE_ID=$blob_role" "MARGO_PULL_ROLE_ID=$pull_role" \
  "MARGO_MODEL_ROLE_ID=$model_role" "MARGO_SPEECH_ROLE_ID=$speech_role" \
  "MARGO_BLUEPRINT_CLIENT_ID=$BLUEPRINT_CLIENT_ID" >/dev/null

export BLUEPRINT_CLIENT_ID
notes=$(sh "$HERE/blueprint.sh")

cat <<EOF
==> Done. Margo's messaging endpoint is $MESSAGING_ENDPOINT (blueprint $BLUEPRINT_CLIENT_ID).
    Instance containers appear within a few minutes of an instance's hire event or first message.
    Voice: $ROUTER_URL/i/<instance>/voice/ (Margo replies to "/voice" with the link).
    Remaining steps (docs/autopilot-deployment.md, steps 4-6):
$notes
    - Publish with the Agent 365 CLI; a Microsoft 365 admin approves Margo in the admin center.
    - Create an instance for each person who hires Margo; license the agent user.
EOF
