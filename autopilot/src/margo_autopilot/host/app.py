"""Margo autopilot host: Microsoft 365 Agents SDK activity endpoint on a Foundry hosted agent.

What this file does, and deliberately does not do:

- Translates Teams messages and Agent 365 notifications into ``Orchestrator`` events. Identity
  comes from the authenticated activity (``from.aadObjectId``, ``recipient.agenticAppId``),
  never from message text.
- Acquires Work IQ tokens only when the orchestrator asks: Margo's own through the agentic-user
  handler; the manager's through an operator-configured on-behalf-of handler, and only after
  the orchestrator has confirmed the sender is the manager.
- Calls the model with function tools only. It never gives the model service an MCP server or
  a bearer token, so every Work IQ call passes the guard.
- Never logs request bodies, message text or tokens, and turns GenAI content capture off.
"""

import asyncio
import json
import logging
import os
from pathlib import Path

os.environ.setdefault("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", "false")

from aiohttp.web import Application, Request, Response, json_response, run_app  # noqa: E402
from aiohttp.web_middlewares import middleware  # noqa: E402
from microsoft_agents.activity import Activity, load_configuration_from_env  # noqa: E402
from microsoft_agents.authentication.msal import MsalConnectionManager  # noqa: E402
from microsoft_agents.hosting.aiohttp import (  # noqa: E402
    CloudAdapter,
    jwt_authorization_middleware,
    start_agent_process,
)
from microsoft_agents.hosting.core import (  # noqa: E402
    AgentApplication,
    AgentAuthConfiguration,
    AuthenticationConstants,
    Authorization,
    ClaimsIdentity,
    MemoryStorage,
    TurnContext,
    TurnState,
)
from microsoft_agents_a365.notifications.agent_notification import (  # noqa: E402
    AgentNotification,
    AgentNotificationActivity,
    ChannelId,
)

from ..activity import is_email as _is_email  # noqa: E402
from ..engine import Engine  # noqa: E402
from ..orchestrator import Orchestrator  # noqa: E402
from ..transport import McpHttpTransport  # noqa: E402
from .azure_store import AzureBlobLeaseStore  # noqa: E402

WORKIQ_SCOPE = "api://workiq.svc.cloud.microsoft/.default"
AGENTIC_HANDLER = os.getenv("AUTH_HANDLER_NAME", "AGENTIC")
PRINCIPAL_HANDLER = os.getenv("PRINCIPAL_AUTH_HANDLER", "")  # on-behalf-of handler; see deployment guide
HEALTH_PATHS = {"/", "/liveness", "/readiness"}

logging.basicConfig(level=getattr(logging, os.getenv("LOG_LEVEL", "INFO").upper(), logging.INFO),
                    format="%(asctime)s %(levelname)s %(name)s | %(message)s")
logger = logging.getLogger("margo.autopilot")


def _configure_telemetry():
    connection = os.getenv("APPLICATIONINSIGHTS_CONNECTION_STRING")
    if not connection:
        logger.warning("Application Insights not configured; running without telemetry.")
        return
    try:
        from azure.monitor.opentelemetry import configure_azure_monitor

        configure_azure_monitor(connection_string=connection)
        logger.info("Application Insights configured (message content capture is off).")
    except Exception:
        logger.exception("Telemetry configuration failed; continuing without it.")


def _credential():
    from azure.identity import DefaultAzureCredential, ManagedIdentityCredential

    client_id = os.getenv("FOUNDRY_AGENT_DEFAULT_INSTANCE_CLIENT_ID")
    return ManagedIdentityCredential(client_id=client_id) if client_id else DefaultAzureCredential()


def _model_client(credential):
    from azure.ai.projects.aio import AIProjectClient

    endpoint = os.getenv("FOUNDRY_PROJECT_ENDPOINT") or os.getenv("AZURE_AI_PROJECT_ENDPOINT")
    if not endpoint:
        raise RuntimeError("FOUNDRY_PROJECT_ENDPOINT is required")
    return AIProjectClient(endpoint=endpoint, credential=credential).get_openai_client()


def _receipt_logger(receipt):
    logger.info("workiq %s", json.dumps(receipt, sort_keys=True, default=str))


def _state_root():
    root = Path(os.getenv("MARGO_STATE_DIR") or (Path.home() / "margo" / "state"))
    for directory in (root.parent, root):
        directory.mkdir(mode=0o700, parents=True, exist_ok=True)
        os.chmod(str(directory), 0o700)
    return root


def build_orchestrator():
    from azure.identity.aio import DefaultAzureCredential as AsyncDefault
    from azure.identity.aio import ManagedIdentityCredential as AsyncManaged

    client_id = os.getenv("FOUNDRY_AGENT_DEFAULT_INSTANCE_CLIENT_ID")
    async_credential = AsyncManaged(client_id=client_id) if client_id else AsyncDefault()
    deployment = os.getenv("ModelDeployment") or os.getenv("MARGO_MODEL_DEPLOYMENT")
    if not deployment:
        raise RuntimeError("ModelDeployment is required")
    blob_url = os.getenv("MARGO_STATE_BLOB_URL")
    if not blob_url:
        raise RuntimeError("MARGO_STATE_BLOB_URL is required: state must not live only in one session")
    engine = Engine(model=_model_client(async_credential), deployment=deployment,
                    repo_root=os.getenv("MARGO_REPO_ROOT", "/app"),
                    timezone=os.getenv("MARGO_TIMEZONE", "America/Los_Angeles"))
    return Orchestrator(engine=engine, transport=McpHttpTransport(), state_root=_state_root(),
                        store_for=AzureBlobLeaseStore.factory(blob_url, _credential()),
                        principal_access=os.getenv("MARGO_PRINCIPAL_ACCESS", "obo"),
                        recorder=_receipt_logger)


def _identity(activity):
    recipient = activity.recipient
    sender = activity.from_property
    return {
        "agent_identity_id": getattr(recipient, "agentic_app_id", None),
        "agent_user_id": getattr(recipient, "agentic_user_id", None) or getattr(recipient, "aad_object_id", None),
        "sender_id": getattr(sender, "aad_object_id", None) if sender else None,
        "sender_name": getattr(sender, "name", None) if sender else None,
    }


class MargoHost:
    def __init__(self):
        _configure_telemetry()
        blueprint = os.getenv("FOUNDRY_AGENT_BLUEPRINT_CLIENT_ID")
        if blueprint:
            os.environ["CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID"] = blueprint
        config = load_configuration_from_env(os.environ)
        self.storage = MemoryStorage()
        self.connections = MsalConnectionManager(**config)
        self.adapter = CloudAdapter(connection_manager=self.connections)
        self.authorization = Authorization(self.storage, self.connections, **config)
        self.app = AgentApplication[TurnState](storage=self.storage, adapter=self.adapter,
                                               authorization=self.authorization, **config)
        self.notifications = AgentNotification(self.app)
        self.orchestrator = build_orchestrator()
        self._register()

    def _token_getter(self, context):
        async def get_token(kind):
            handler = AGENTIC_HANDLER if kind == "margo" else PRINCIPAL_HANDLER
            if not handler:
                return None
            try:
                exchanged = await self.app.auth.exchange_token(context, scopes=[WORKIQ_SCOPE], auth_handler_id=handler)
            except Exception:
                logger.warning("token exchange failed for %s", kind)
                return None
            return getattr(exchanged, "token", None) or getattr(exchanged, "access_token", None)
        return get_token

    def _register(self):
        handlers = {"auth_handlers": [AGENTIC_HANDLER]}

        @self.app.activity("message", **handlers)
        async def on_message(context: TurnContext, _: TurnState):
            activity = context.activity
            event = _identity(activity)
            email = _is_email(activity)
            event.update({"conversation_id": getattr(activity.conversation, "id", None), "activity_id": activity.id,
                          "channel": "mail" if email else "teams", "text": activity.text or ""})
            if not event["agent_identity_id"] or not event["agent_user_id"] or not event["conversation_id"]:
                logger.warning("message without agent identity or conversation; ignored")
                return
            typing = asyncio.ensure_future(self._typing(context))
            try:
                result = await self.orchestrator.handle(event, self._token_getter(context))
            except Exception:
                logger.exception("turn failed")
                result = {"reply": None, "status": "error"}
            finally:
                typing.cancel()
            logger.info("turn status=%s", result.get("status"))
            if result.get("reply") and not email:  # this version never answers mail
                await context.send_activity(Activity(type="message", text=result["reply"], text_format="markdown"))

        @self.notifications.on_agent_notification(channel_id=ChannelId(channel="agents", sub_channel="*"), **handlers)
        async def on_notification(context: TurnContext, _: TurnState, notification: AgentNotificationActivity):
            event = _identity(context.activity)
            sender = getattr(notification, "from_property", None) or getattr(context.activity, "from_property", None)
            event.update({"sender_id": getattr(sender, "aad_object_id", None) or getattr(sender, "id", None),
                          "sender_name": getattr(sender, "name", None),
                          "conversation_id": getattr(context.activity.conversation, "id", None) or "notification",
                          "activity_id": context.activity.id or "notification", "channel": "mail",
                          "text": getattr(notification, "text", None) or getattr(context.activity, "text", None) or ""})
            if not event["agent_identity_id"] or not event["agent_user_id"]:
                return
            try:
                result = await self.orchestrator.handle(event, self._token_getter(context))
                logger.info("notification status=%s", result.get("status"))
            except Exception:
                logger.exception("notification failed")
            # No reply: this version never answers mail.

        @self.app.activity("installationUpdate")
        async def on_installation(context: TurnContext, _: TurnState):
            logger.info("installation update: %s", getattr(context.activity, "action", None))

    async def _typing(self, context):
        try:
            while True:
                await context.send_activity(Activity(type="typing"))
                await asyncio.sleep(4)
        except asyncio.CancelledError:
            pass
        except Exception:
            pass

    def web_app(self):
        async def messages(request: Request) -> Response:
            return await start_agent_process(request, self.app, self.adapter)

        async def health(_request: Request) -> Response:
            return json_response({"status": "ok"})

        secret = os.getenv("CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTSECRET")
        client_id = os.getenv("CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID")
        tenant_id = os.getenv("CONNECTIONS__SERVICE_CONNECTION__SETTINGS__TENANTID")
        scope = os.getenv("CONNECTIONS__SERVICE_CONNECTION__SETTINGS__SCOPES__0")
        auth_configuration = None
        if secret and client_id and tenant_id and scope:
            auth_configuration = AgentAuthConfiguration(client_id=client_id, tenant_id=tenant_id,
                                                        client_secret=secret, scopes=[scope])
        middlewares = []
        if auth_configuration:
            @middleware
            async def jwt(request, handler):
                if request.path in HEALTH_PATHS:
                    return await handler(request)
                return await jwt_authorization_middleware(request, handler)
            middlewares.append(jwt)
        else:
            # Hosted agents authenticate inbound calls at the platform edge (BotServiceRbac),
            # as in Microsoft's sample; the container sees pre-authorised requests.
            @middleware
            async def platform_claims(request, handler):
                request["claims_identity"] = ClaimsIdentity(
                    {AuthenticationConstants.AUDIENCE_CLAIM: "anonymous",
                     AuthenticationConstants.APP_ID_CLAIM: "anonymous-app"}, False, "Anonymous")
                return await handler(request)
            middlewares.append(platform_claims)
        app = Application(middlewares=middlewares)
        if auth_configuration:
            app["agent_configuration"] = auth_configuration
        app.router.add_post("/activity/messages", messages)
        app.router.add_get("/activity/messages", lambda _request: Response(status=200))
        for path in HEALTH_PATHS:
            app.router.add_get(path, health)
        return app


def main():
    host = MargoHost()
    run_app(host.web_app(), host=os.getenv("HOST", "0.0.0.0"), port=int(os.getenv("PORT", "8088")))
    return 0
