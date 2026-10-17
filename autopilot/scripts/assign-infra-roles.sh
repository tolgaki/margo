#!/bin/sh
# Roles for the Foundry project's managed identity: pull the image, use the models, write telemetry.
. "$(dirname -- "$0")/lib.sh"
need PROJECT_PRINCIPAL_ID AZURE_CONTAINER_REGISTRY_ID ACCOUNT_ID APPLICATIONINSIGHTS_ID

assign_role "$PROJECT_PRINCIPAL_ID" "AcrPull" "$AZURE_CONTAINER_REGISTRY_ID"
assign_role "$PROJECT_PRINCIPAL_ID" "Cognitive Services User" "$ACCOUNT_ID"
assign_role "$PROJECT_PRINCIPAL_ID" "Monitoring Metrics Publisher" "$APPLICATIONINSIGHTS_ID"
