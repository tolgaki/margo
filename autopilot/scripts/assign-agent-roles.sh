#!/bin/sh
# Roles for the agent's instance identity: call Foundry models, read/write its private state.
. "$(dirname -- "$0")/lib.sh"
need SUBSCRIPTION_ID RESOURCE_GROUP ACCOUNT_NAME PROJECT_NAME STORAGE_ACCOUNT_ID
AGENT_INSTANCE_CLIENT_ID=${AGENT_INSTANCE_CLIENT_ID:-$(azd env get-value AGENT_INSTANCE_CLIENT_ID 2>/dev/null || true)}
need AGENT_INSTANCE_CLIENT_ID

object_id=$(az ad sp show --id "$AGENT_INSTANCE_CLIENT_ID" --query id -o tsv)
[ -n "$object_id" ] || die "could not resolve the agent instance identity"
project_scope="/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.CognitiveServices/accounts/$ACCOUNT_NAME/projects/$PROJECT_NAME"

# 'Foundry User' was previously named 'Azure AI User'; try the new name first.
if ! az role assignment create --assignee-object-id "$object_id" --assignee-principal-type ServicePrincipal \
     --role "Foundry User" --scope "$project_scope" >/dev/null 2>&1; then
  assign_role "$object_id" "Azure AI User" "$project_scope"
else
  say "granted 'Foundry User'"
fi
assign_role "$object_id" "Storage Blob Data Contributor" "$STORAGE_ACCOUNT_ID"
