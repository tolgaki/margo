#!/bin/sh
# Run the provisioner job once with a command of your choice, keeping everything else the job has.
#   sh scripts/provisioner.sh redeploy-all
#   sh scripts/provisioner.sh enqueue ensure|suspend|retire AGENT_IDENTITY_ID AGENT_USER_ID [--redeploy]
#   sh scripts/provisioner.sh run
# 'az containerapp job start --image/--command/--args' replaces the job's whole container (its environment included),
# so such a run fails for want of its settings. This copies the job's own container, changes only the arguments,
# and starts the execution through the Container Apps API.
. "$(dirname -- "$0")/lib.sh"
[ $# -ge 1 ] || die "usage: sh scripts/provisioner.sh redeploy-all | run | enqueue ensure|suspend|retire AGENT_IDENTITY_ID AGENT_USER_ID [--redeploy]"

value() { azd env get-value "$1" 2>/dev/null || true; }
JOB=${PROVISIONER_NAME:-$(value PROVISIONER_NAME)}
RG=${RESOURCE_GROUP:-$(value RESOURCE_GROUP)}
SUB=${SUBSCRIPTION_ID:-$(value SUBSCRIPTION_ID)}
[ -n "$JOB" ] && [ -n "$RG" ] && [ -n "$SUB" ] || die "run from cloud/ with the azd environment selected (PROVISIONER_NAME, RESOURCE_GROUP, SUBSCRIPTION_ID)"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT INT TERM
az containerapp job show -n "$JOB" -g "$RG" --only-show-errors --query "properties.template" -o json > "$work/template.json" \
  || die "could not read the provisioner job $JOB"
python3 - "$work/template.json" "$work/start.json" "$@" <<'PY'
import json, sys
template = json.load(open(sys.argv[1]))
containers = template.get("containers") or []
if len(containers) != 1:
    sys.exit("the provisioner job should have exactly one container, found %d" % len(containers))
container = containers[0]
if not str(container.get("image", "")).count("@sha256:"):
    sys.exit("the provisioner job is not on a pinned Margo image yet (%s); run 'azd provision' first" % container.get("image"))
container["command"] = ["dotnet"]
container["args"] = ["/app/provisioner/Margo.Cloud.Provisioner.dll"] + sys.argv[3:]
json.dump({"containers": [container]}, open(sys.argv[2], "w"))
PY
execution=$(az rest --method POST --only-show-errors \
  --url "https://management.azure.com/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.App/jobs/$JOB/start?api-version=2024-03-01" \
  --body "@$work/start.json" --query name -o tsv) || die "could not start the provisioner job $JOB"
say "started $execution: Margo.Cloud.Provisioner $*"
say "follow it with: az containerapp job execution show -n $JOB -g $RG --job-execution-name $execution --query properties.status -o tsv"
