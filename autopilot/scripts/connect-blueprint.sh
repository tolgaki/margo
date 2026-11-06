#!/bin/sh
# Record the Agent 365 blueprint in the azd environment, so the next 'azd provision' points the
# router and the instance containers at it.
#   sh scripts/connect-blueprint.sh path/to/a365.generated.config.json
#   sh scripts/connect-blueprint.sh BLUEPRINT_CLIENT_ID
# Reads only the blueprint ID (agentBlueprintId). The Agent 365 CLI's files may hold a client secret:
# keep them outside this repository. Margo never uses that secret.
. "$(dirname -- "$0")/lib.sh"
[ $# -eq 1 ] || die "usage: sh scripts/connect-blueprint.sh A365_GENERATED_CONFIG_JSON | BLUEPRINT_CLIENT_ID"

if [ -f "$1" ]; then
  blueprint=$(json_get "$1" 'd.get("agentBlueprintId") or ""')
  [ -n "$blueprint" ] || die "$1 has no agentBlueprintId; run the Agent 365 CLI's blueprint setup first"
else
  blueprint=$1
fi
printf '%s' "$blueprint" | grep -Eq '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$' \
  || die "not a client ID: $blueprint"

azd env set MARGO_BLUEPRINT_CLIENT_ID "$blueprint" >/dev/null
say "blueprint $blueprint recorded. Next: azd provision"
