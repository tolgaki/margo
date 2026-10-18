#!/bin/sh
# After 'azd provision': roles, image, hosted agent, agent roles, publish. Each step can be
# re-run on its own; set SKIP_PUBLISH=true to stop before the admin-approval request.
. "$(dirname -- "$0")/lib.sh"
need TENANT_ID SUBSCRIPTION_ID RESOURCE_GROUP AZURE_AI_PROJECT_ENDPOINT AZURE_CONTAINER_REGISTRY_ENDPOINT

if [ "${MARGO_HOSTING:-foundry}" = "instance" ]; then
  exec sh "$HERE/postprovision-instance.sh"
fi

sh "$HERE/assign-infra-roles.sh"
sh "$HERE/build-image.sh"
sh "$HERE/create-agent.sh"
sh "$HERE/assign-agent-roles.sh"
if [ "${SKIP_PUBLISH:-false}" = "true" ]; then
  say "SKIP_PUBLISH=true: not publishing. Run scripts/publish.sh when ready."
else
  sh "$HERE/publish.sh"
fi
say "Done. Next: a Microsoft 365 admin approves Margo in the admin center (see docs/autopilot-deployment.md)."
