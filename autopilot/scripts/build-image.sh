#!/bin/sh
# Build the image in Azure Container Registry from the repository root. Tags are immutable
# (version + commit + time), so an agent version always names exactly what it runs.
. "$(dirname -- "$0")/lib.sh"
need AZURE_CONTAINER_REGISTRY_ENDPOINT

registry=$(printf '%s' "$AZURE_CONTAINER_REGISTRY_ENDPOINT" | cut -d. -f1)
version=$(cat "$REPO/VERSION" 2>/dev/null || echo 0.0.0)
commit=$(git -C "$REPO" rev-parse --short HEAD 2>/dev/null || echo nogit)
tag="${version}-${commit}-$(date -u +%Y%m%d%H%M%S)"

if [ -n "$(git -C "$REPO" status --porcelain 2>/dev/null)" ] && [ "${ALLOW_DIRTY:-false}" != "true" ]; then
  die "the checkout has uncommitted changes; commit them or set ALLOW_DIRTY=true for a test build"
fi
[ -x "$REPO/tools/check-clean.sh" ] && { say "privacy check"; "$REPO/tools/check-clean.sh" >/dev/null || die "check-clean failed; not uploading this tree"; }

say "building margo-autopilot:$tag in $registry"
az acr build --registry "$registry" --image "margo-autopilot:$tag" --file "$AUTOPILOT/Dockerfile" "$REPO"
azd env set MARGO_IMAGE "$AZURE_CONTAINER_REGISTRY_ENDPOINT/margo-autopilot:$tag" >/dev/null
say "image: $AZURE_CONTAINER_REGISTRY_ENDPOINT/margo-autopilot:$tag"
