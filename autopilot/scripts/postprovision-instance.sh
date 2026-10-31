#!/bin/sh
# After 'azd provision' with MARGO_HOSTING=instance (hybrid hosting, docs/autopilot-instance-containers.md):
# 1. the Foundry autopilot exactly as in the default mode: hosted agent, blueprint, roles, publish;
# 2. the instance layer beside it: router, provisioner, and the blueprint credential for instances.
# Until the blueprint's messaging moves to the router, chat is served by the Foundry agent and voice by
# each instance's own container; both use the same per-instance state. Every step is idempotent.
. "$(dirname -- "$0")/lib.sh"
need TENANT_ID SUBSCRIPTION_ID RESOURCE_GROUP AZURE_CONTAINER_REGISTRY_ENDPOINT AZURE_CONTAINER_REGISTRY_ID \
  ACCOUNT_ID ROUTER_NAME ROUTER_URL ROUTER_PRINCIPAL_ID ROUTER_IDENTITY_ID PROVISIONER_NAME \
  PROVISIONER_PRINCIPAL_ID PROVISIONER_IDENTITY_ID ROUTES_CONTAINER_ID PROVISION_QUEUE_ID PROVISION_QUEUE_URL \
  BLUEPRINT_CREDENTIAL_CLIENT_ID BLUEPRINT_CREDENTIAL_PRINCIPAL_ID
export MARGO_HOSTING=instance ROUTER_URL PROVISION_QUEUE_URL

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

say "Foundry project: pull the image, use the models, write telemetry"
sh "$HERE/assign-infra-roles.sh"

sh "$HERE/build-image.sh"
MARGO_IMAGE=$(azd env get-value MARGO_IMAGE 2>/dev/null || true)
need MARGO_IMAGE
export MARGO_IMAGE

say "Foundry autopilot: hosted agent and its blueprint (serves chat until messaging moves)"
sh "$HERE/create-agent.sh"
sh "$HERE/assign-agent-roles.sh"
BLUEPRINT_CLIENT_ID=$(azd env get-value BLUEPRINT_CLIENT_ID 2>/dev/null || true)
AGENT_INSTANCE_CLIENT_ID=$(azd env get-value AGENT_INSTANCE_CLIENT_ID 2>/dev/null || true)
need BLUEPRINT_CLIENT_ID AGENT_INSTANCE_CLIENT_ID
agent_object_id=$(az ad sp show --id "$AGENT_INSTANCE_CLIENT_ID" --query id -o tsv)
[ -n "$agent_object_id" ] || die "could not resolve the agent instance identity"
assign_role "$agent_object_id" "Storage Queue Data Message Sender" "$PROVISION_QUEUE_ID"
registry=$(printf '%s' "$AZURE_CONTAINER_REGISTRY_ENDPOINT" | cut -d. -f1)
repository_tag=${MARGO_IMAGE#*/}
digest=$(az acr repository show --name "$registry" --image "$repository_tag" --query digest -o tsv)
[ -n "$digest" ] || die "could not resolve the image digest"
pinned="$AZURE_CONTAINER_REGISTRY_ENDPOINT/${repository_tag%%:*}@$digest"
say "pinned image: $pinned"

say "router: switch to the Margo image; check activity tokens against the Foundry-created blueprint"
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

# Instance containers act as the blueprint through a federated credential on it that trusts the shared,
# role-less blueprint-credential identity. Foundry may not let this deployer change the blueprint it
# manages; if so, an Entra administrator adds the same credential by hand.
say "blueprint: federated credential for instance containers"
fic_name="margo-instance-containers"
# A blueprint is an agentIdentityBlueprint, which 'az ad app' (the plain applications endpoint) does not
# find; address it through the Graph beta type cast instead.
blueprints="https://graph.microsoft.com/beta/applications/graph.agentIdentityBlueprint"
blueprint_object_id=$(az rest --method get \
  --url "$blueprints?\$filter=appId%20eq%20%27$BLUEPRINT_CLIENT_ID%27&\$select=id" \
  --query "value[0].id" -o tsv 2>/dev/null || true)
existing=""
if [ -n "$blueprint_object_id" ]; then
  existing=$(az rest --method get --url "$blueprints/$blueprint_object_id/federatedIdentityCredentials" \
    --query "value[?name=='$fic_name'] | length(@)" -o tsv 2>/dev/null || true)
fi
fic_note=""
if [ "$existing" = "1" ]; then
  say "federated credential already present"
elif [ -n "$blueprint_object_id" ] && az rest --method post \
    --url "$blueprints/$blueprint_object_id/federatedIdentityCredentials" \
    --headers "Content-Type=application/json" \
    --body "{\"name\": \"$fic_name\", \"issuer\": \"https://login.microsoftonline.com/$TENANT_ID/v2.0\", \"subject\": \"$BLUEPRINT_CREDENTIAL_PRINCIPAL_ID\", \"audiences\": [\"api://AzureADTokenExchange\"], \"description\": \"Margo instance containers act as this blueprint\"}" >/dev/null 2>&1; then
  say "federated credential added"
else
  fic_note="    - An Entra administrator adds a federated credential to blueprint $BLUEPRINT_CLIENT_ID:
      issuer https://login.microsoftonline.com/$TENANT_ID/v2.0, subject $BLUEPRINT_CREDENTIAL_PRINCIPAL_ID,
      audience api://AzureADTokenExchange (the managed identity $BLUEPRINT_CREDENTIAL_CLIENT_ID)."
  say "could not add the federated credential here; see the steps below"
fi

if [ "${SKIP_PUBLISH:-false}" = "true" ]; then
  say "SKIP_PUBLISH=true: not publishing. Run scripts/publish.sh when ready."
else
  sh "$HERE/publish.sh"
fi

cat <<EOF
==> Done. Margo is a Foundry autopilot: the blueprint is Foundry's, published for admin approval.
    Chat: served by the Foundry agent; each instance's state is kept in that instance's own container.
    Voice: each instance's own container, at $ROUTER_URL/i/<instance>/voice/ (Margo replies to "/voice").
    Instance containers appear within about two minutes of an instance's first message or hire event.
    Remaining steps (docs/autopilot-deployment.md, "One container per instance"):
$fic_note
    - For voice, expose a delegated Voice.Talk scope on the blueprint and add each voice page as a
      single-page-app redirect URI.
    - Verify first, in a test tenant: whether the blueprint's messaging endpoint can be moved to
      $ROUTER_URL/api/messages. If it can, chat moves to the instance containers with nothing to
      migrate; if not, Margo keeps chatting through the Foundry agent.
EOF
