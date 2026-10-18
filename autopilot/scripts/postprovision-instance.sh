#!/bin/sh
# After 'azd provision' with MARGO_HOSTING=instance: roles by name, image, router and provisioner.
# Every step is idempotent. Instances themselves are created by the provisioner job, not here.
. "$(dirname -- "$0")/lib.sh"
need TENANT_ID SUBSCRIPTION_ID RESOURCE_GROUP AZURE_CONTAINER_REGISTRY_ENDPOINT AZURE_CONTAINER_REGISTRY_ID \
  ACCOUNT_ID ROUTER_NAME ROUTER_URL ROUTER_PRINCIPAL_ID ROUTER_IDENTITY_ID PROVISIONER_NAME \
  PROVISIONER_PRINCIPAL_ID PROVISIONER_IDENTITY_ID ROUTES_CONTAINER_ID PROVISION_QUEUE_ID BLUEPRINT_CREDENTIAL_CLIENT_ID

group_scope="/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP"

role_id() {  # role_id NAME [FALLBACK_NAME] -> role definition GUID
  id=$(az role definition list --name "$1" --query "[0].name" -o tsv 2>/dev/null || true)
  [ -n "$id" ] || [ -z "${2:-}" ] || id=$(az role definition list --name "$2" --query "[0].name" -o tsv 2>/dev/null || true)
  [ -n "$id" ] || die "role '$1' not found in this tenant"
  printf '%s' "$id"
}

model_role=$(role_id "Foundry User" "Azure AI User")
speech_role=$(role_id "Cognitive Services User")
blob_role=$(role_id "Storage Blob Data Contributor")
pull_role=$(role_id "AcrPull")

say "router: read routes, queue provisioning requests, pull its image"
assign_role "$ROUTER_PRINCIPAL_ID" "Storage Blob Data Reader" "$ROUTES_CONTAINER_ID"
assign_role "$ROUTER_PRINCIPAL_ID" "Storage Queue Data Message Sender" "$PROVISION_QUEUE_ID"
assign_role "$ROUTER_PRINCIPAL_ID" "AcrPull" "$AZURE_CONTAINER_REGISTRY_ID"

say "provisioner: deploy instances into this resource group, keep the route table, drain the queue"
assign_role "$PROVISIONER_PRINCIPAL_ID" "Contributor" "$group_scope"
assign_role "$PROVISIONER_PRINCIPAL_ID" "Storage Blob Data Contributor" "$ROUTES_CONTAINER_ID"
assign_role "$PROVISIONER_PRINCIPAL_ID" "Storage Queue Data Message Processor" "$PROVISION_QUEUE_ID"
assign_role "$PROVISIONER_PRINCIPAL_ID" "AcrPull" "$AZURE_CONTAINER_REGISTRY_ID"
# It may grant and remove only the four roles an instance needs, nothing else (an ABAC condition).
roles="$blob_role, $pull_role, $model_role, $speech_role"
condition="((!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})) OR (@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {$roles})) AND ((!(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'})) OR (@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {$roles}))"
output=$(az role assignment create --assignee-object-id "$PROVISIONER_PRINCIPAL_ID" --assignee-principal-type ServicePrincipal \
  --role "Role Based Access Control Administrator" --scope "$group_scope" \
  --condition "$condition" --condition-version "2.0" 2>&1) && say "granted constrained role administration" || case "$output" in
    *RoleAssignmentExists*|*"already exists"*) say "constrained role administration already granted" ;;
    *) printf '%s\n' "$output" >&2; die "could not grant constrained role administration" ;;
  esac

sh "$HERE/build-image.sh"
MARGO_IMAGE=${MARGO_IMAGE:-$(azd env get-value MARGO_IMAGE 2>/dev/null || true)}
need MARGO_IMAGE
registry=$(printf '%s' "$AZURE_CONTAINER_REGISTRY_ENDPOINT" | cut -d. -f1)
repository_tag=${MARGO_IMAGE#*/}
digest=$(az acr repository show --name "$registry" --image "$repository_tag" --query digest -o tsv)
[ -n "$digest" ] || die "could not resolve the image digest"
pinned="$AZURE_CONTAINER_REGISTRY_ENDPOINT/${repository_tag%%:*}@$digest"
say "pinned image: $pinned"

say "router: switch to the Margo image"
az containerapp registry set -n "$ROUTER_NAME" -g "$RESOURCE_GROUP" --server "$AZURE_CONTAINER_REGISTRY_ENDPOINT" \
  --identity "$ROUTER_IDENTITY_ID" >/dev/null
az containerapp update -n "$ROUTER_NAME" -g "$RESOURCE_GROUP" --image "$pinned" >/dev/null

say "provisioner: switch to the Margo image and record what instances run"
az containerapp job registry set -n "$PROVISIONER_NAME" -g "$RESOURCE_GROUP" --server "$AZURE_CONTAINER_REGISTRY_ENDPOINT" \
  --identity "$PROVISIONER_IDENTITY_ID" >/dev/null
az containerapp job update -n "$PROVISIONER_NAME" -g "$RESOURCE_GROUP" --image "$pinned" \
  --set-env-vars "MARGO_INSTANCE_IMAGE=$pinned" "MARGO_STORAGE_ROLE_ID=$blob_role" "MARGO_PULL_ROLE_ID=$pull_role" \
  "MARGO_MODEL_ROLE_ID=$model_role" "MARGO_SPEECH_ROLE_ID=$speech_role" >/dev/null

cat <<EOF
==> Done. Steps outside Azure Resource Manager (see docs/autopilot-deployment.md, "One container per instance"):
    1. Point the agent blueprint's messaging endpoint at: $ROUTER_URL/api/messages
    2. Add a federated credential on the blueprint for the managed identity with client ID
       $BLUEPRINT_CREDENTIAL_CLIENT_ID (issuer and subject from that identity).
    3. For voice: expose a delegated scope on the blueprint and add the SPA redirect URI
       $ROUTER_URL/i/<instance>/voice/ for each instance (Margo replies with her link to "/voice").
    New instances are created by the provisioner within about two minutes of their first activity.
EOF
