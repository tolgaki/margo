# Margo Cloud image. One image serves all three roles; the container command picks one:
#   instance (default)  dotnet /app/host/Margo.Cloud.Host.dll
#   router              dotnet /app/router/Margo.Cloud.Router.dll
#   provisioner job     dotnet /app/provisioner/Margo.Cloud.Provisioner.dll run
# Build context: the repository root.
#   az acr build --registry REGISTRY --image margo-cloud:TAG --file cloud/Dockerfile .
#   docker build --file cloud/Dockerfile --tag margo-cloud:dev .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src/cloud
# Only what the four applications need: shared build settings, the sources and the instance template the provisioner
# carries. No tests, contracts or state.
COPY cloud/global.json cloud/nuget.config cloud/Directory.Build.props cloud/Directory.Packages.props ./
COPY cloud/src/ src/
COPY cloud/infra/instance.json infra/instance.json
# Release never includes the contract probes (Margo.Cloud.State.Probes runs arbitrary SQL); the host's build fails if
# it would, and the check below refuses the image if any probe assembly reached an output.
RUN dotnet publish src/Margo.Cloud.Host/Margo.Cloud.Host.csproj -c Release -o /out/host \
 && dotnet publish src/Margo.Cloud.StateCli/Margo.Cloud.StateCli.csproj -c Release -o /out/host \
 && dotnet publish src/Margo.Cloud.Router/Margo.Cloud.Router.csproj -c Release -o /out/router \
 && dotnet publish src/Margo.Cloud.Provisioner/Margo.Cloud.Provisioner.csproj -c Release -o /out/provisioner \
 && test -f /out/host/Margo.Cloud.Host.dll && test -f /out/host/margo-state.dll \
 && test -f /out/host/margo-state.runtimeconfig.json && test -f /out/router/Margo.Cloud.Router.dll \
 && test -f /out/provisioner/Margo.Cloud.Provisioner.dll && test -f /out/provisioner/instance.json \
 && if find /out -iname '*Probes*' | grep .; then echo 'contract probes must not ship' >&2; exit 1; fi

# The smallest ASP.NET Core runtime image that works: Alpine, plus tzdata for the IANA zones the persona's time,
# the Dream window and quiet hours use (the chiseled images lack it). InvariantGlobalization is on, so no ICU.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
RUN apk add --no-cache tzdata && test -f /usr/share/zoneinfo/America/Los_Angeles
WORKDIR /app
COPY --from=build /out/ /app/
# The persona and every skill, unchanged (hosted Margo is the same Margo). Nothing private: .dockerignore excludes
# state/, local configuration and every build output.
COPY agents/ /app/agents/
COPY skills/ /app/skills/
# The per-instance template the provisioner deploys (one container per hired Margo), also its default path.
COPY cloud/infra/instance.json /app/provisioner/instance.json

ENV MARGO_REPO_ROOT=/app \
    HOST=0.0.0.0 \
    PORT=8088 \
    OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=false \
    DOTNET_NOLOGO=1

# Microsoft 365 Agents SDK configuration. Names must be upper case with SERVICE_CONNECTION. The blueprint client ID,
# tenant and sign-in type are set per deployment: instance containers act as the blueprint through a federated
# credential (infra/instance.bicep). The scope below is the SDK's fixed public service scope, not a tenant identifier.
ENV CONNECTIONS__SERVICE_CONNECTION__SETTINGS__SCOPES__0=5a807f24-c9de-44ee-a3a7-329e88a00ffc/.default \
    CONNECTIONSMAP__0__SERVICEURL=* \
    CONNECTIONSMAP__0__CONNECTION=SERVICE_CONNECTION \
    AGENTAPPLICATION__STARTTYPINGTIMER=false \
    AGENTAPPLICATION__USERAUTHORIZATION__AUTOSIGNIN=false \
    AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__AGENTIC__TYPE=AgenticUserAuthorization \
    AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__AGENTIC__SETTINGS__SCOPES=5a807f24-c9de-44ee-a3a7-329e88a00ffc/.default \
    AUTH_HANDLER_NAME=AGENTIC

# The non-root user the .NET images provide (APP_UID, with its own home directory). The working state lives under
# that home (~/margo/state, or MARGO_STATE_DIR) and in Blob storage, never in the image; /app stays read-only to it.
USER $APP_UID
EXPOSE 8088
CMD ["dotnet", "/app/host/Margo.Cloud.Host.dll"]
