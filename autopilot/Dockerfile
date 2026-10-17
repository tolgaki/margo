# Margo autopilot image. Build context: the repository root.
#   az acr build --registry REGISTRY --image margo-autopilot:TAG --file autopilot/Dockerfile .
FROM python:3.12-slim

WORKDIR /app
COPY autopilot/requirements.txt /app/autopilot/requirements.txt
RUN pip install --no-cache-dir -r /app/autopilot/requirements.txt

# Persona, procedures and the deterministic core. Nothing private: .dockerignore excludes state.
COPY agents/ /app/agents/
COPY skills/chief-of-staff/ /app/skills/chief-of-staff/
COPY autopilot/src/ /app/autopilot/src/

ENV PYTHONPATH=/app/autopilot/src:/app/skills/chief-of-staff/scripts \
    PYTHONUNBUFFERED=1 \
    MARGO_REPO_ROOT=/app \
    HOST=0.0.0.0 \
    PORT=8088 \
    OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=false

# Microsoft 365 Agents SDK configuration, as in Microsoft's Foundry autopilot sample. Names must
# be upper case with SERVICE_CONNECTION; the client ID comes from FOUNDRY_AGENT_BLUEPRINT_CLIENT_ID
# at runtime. The scope below is the SDK's fixed public service scope, not a tenant identifier.
ENV CONNECTIONS__SERVICE_CONNECTION__SETTINGS__AUTHTYPE=IdentityProxyManager \
    CONNECTIONS__SERVICE_CONNECTION__SETTINGS__SCOPES__0=5a807f24-c9de-44ee-a3a7-329e88a00ffc/.default \
    CONNECTIONSMAP__0__SERVICEURL=* \
    CONNECTIONSMAP__0__CONNECTION=SERVICE_CONNECTION \
    AGENTAPPLICATION__STARTTYPINGTIMER=false \
    AGENTAPPLICATION__USERAUTHORIZATION__AUTOSIGNIN=false \
    AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__AGENTIC__TYPE=AgenticUserAuthorization \
    AGENTAPPLICATION__USERAUTHORIZATION__HANDLERS__AGENTIC__SETTINGS__SCOPES=5a807f24-c9de-44ee-a3a7-329e88a00ffc/.default \
    AUTH_HANDLER_NAME=AGENTIC

EXPOSE 8088
ENTRYPOINT ["python", "-m", "margo_autopilot.host"]
