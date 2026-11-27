#!/bin/sh
# Before 'azd provision'. Safe to repeat.
#   1. Register the resource providers Margo Cloud needs.
#   2. Remove a provisioner job that an earlier version created with a schedule trigger: the job is now started by
#      the provisioning queue (main.bicep), and a job's trigger type cannot be relied on to change in place. The
#      provision that follows recreates it; requests wait on the queue meanwhile and nothing else is touched.
. "$(dirname -- "$0")/lib.sh"
for provider in Microsoft.CognitiveServices Microsoft.ContainerRegistry Microsoft.Storage \
                Microsoft.OperationalInsights Microsoft.Insights Microsoft.App Microsoft.ManagedIdentity \
                Microsoft.BotService; do
  say "registering $provider"
  az provider register --namespace "$provider" --wait >/dev/null
done

# Known only after a first provision (main.bicep outputs PROVISIONER_NAME and RESOURCE_GROUP).
if [ -n "${PROVISIONER_NAME:-}" ] && [ -n "${RESOURCE_GROUP:-}" ]; then
  # --only-show-errors: stderr is captured with the value (to tell "not found" from other failures), and az warnings,
  # such as the containerapp extension's notice, would otherwise become part of it.
  if trigger=$(az containerapp job show -n "$PROVISIONER_NAME" -g "$RESOURCE_GROUP" --only-show-errors \
      --query "properties.configuration.triggerType" -o tsv 2>&1); then
    case "$trigger" in
      Event) ;;
      Schedule|Manual)
        say "provisioner job: replacing the $trigger trigger with the queue trigger (the job is recreated)"
        az containerapp job delete -n "$PROVISIONER_NAME" -g "$RESOURCE_GROUP" --yes >/dev/null \
          || die "could not remove the earlier provisioner job $PROVISIONER_NAME; remove it and run 'azd provision' again"
        ;;
      *) die "provisioner job $PROVISIONER_NAME has an unexpected trigger type: $trigger" ;;
    esac
  else
    case "$trigger" in
      *ResourceNotFound*|*ResourceGroupNotFound*|*"not found"*|*"could not be found"*) ;;
      *) printf '%s\n' "$trigger" >&2
         die "could not read the provisioner job $PROVISIONER_NAME; if it still has a schedule trigger the provision may fail" ;;
    esac
  fi
fi
