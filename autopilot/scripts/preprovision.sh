#!/bin/sh
# Register the resource providers the Margo autopilot needs. Safe to repeat.
. "$(dirname -- "$0")/lib.sh"
for provider in Microsoft.CognitiveServices Microsoft.ContainerRegistry Microsoft.Storage \
                Microsoft.OperationalInsights Microsoft.Insights; do
  say "registering $provider"
  az provider register --namespace "$provider" --wait >/dev/null
done
