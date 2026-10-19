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
)

from ..activity import is_email as _is_email  # noqa: E402
from ..activity import stable_ids  # noqa: E402
from ..engine import Engine  # noqa: E402
from ..instances import lifecycle_message  # noqa: E402
from ..orchestrator import Orchestrator  # noqa: E402
from ..transport import McpHttpTransport  # noqa: E402
from .azure_store import AzureBlobLeaseStore  # noqa: E402

WORKIQ_SCOPE = "api://workiq.svc.cloud.microsoft/.default"
HOSTING = os.getenv("MARGO_HOSTING", "foundry")  # "instance": this container serves one hired Margo
# Hybrid hosting (docs/autopilot-instance-containers.md): the Foundry agent keeps each instance's state in
# that instance's own container and asks the provisioner to create the container.
STATE_LAYOUT = os.getenv("MARGO_STATE_LAYOUT", "")
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
    instance = state_container = voice_url = None
    base = os.getenv("MARGO_PUBLIC_BASE_URL", "").rstrip("/")
    if HOSTING != "instance" and STATE_LAYOUT == "instance" and base and os.getenv("VOICE_LIVE_MODEL"):
        voice_url = base + "/i/{instance}/voice/"
    if HOSTING == "instance":
        instance = (os.environ["MARGO_INSTANCE_AGENT_ID"], os.environ["MARGO_INSTANCE_USER_ID"])
        state_container = os.environ["MARGO_STATE_CONTAINER"]
        base = os.getenv("MARGO_PUBLIC_BASE_URL", "").rstrip("/")
        if base and os.getenv("VOICE_LIVE_MODEL"):
            voice_url = "%s/i/%s/voice/" % (base, os.environ["MARGO_INSTANCE_KEY"])
    return Orchestrator(engine=engine, transport=McpHttpTransport(), state_root=_state_root(),
                        store_for=AzureBlobLeaseStore.factory(blob_url, _credential()),
                        principal_access=os.getenv("MARGO_PRINCIPAL_ACCESS", "obo"),
                        recorder=_receipt_logger, instance=instance, state_container=state_container,
                        voice_url=voice_url, per_instance_state=HOSTING != "instance" and STATE_LAYOUT == "instance")


class ProvisionRequests:
    """Hybrid hosting: ask the provisioner to create each instance's own container, once per
    instance per process, and pass on Agent 365 lifecycle events. Failures are logged, never fatal:
    chat keeps working through the Foundry agent either way."""

    def __init__(self, queue_url):
        from azure.storage.queue import QueueClient

        self.queue = QueueClient.from_queue_url(queue_url, credential=_credential())
        self._asked = set()

    async def ensure(self, agent_identity_id, agent_user_id):
        if (agent_identity_id, agent_user_id) in self._asked:
            return
        self._asked.add((agent_identity_id, agent_user_id))
        await self.send({"action": "ensure", "agent_identity_id": agent_identity_id,
                         "agent_user_id": agent_user_id, "event": "foundry-activity"})

    async def send(self, message):
        try:
            await asyncio.to_thread(self.queue.send_message, json.dumps(message, sort_keys=True))
        except Exception:
            logger.warning("could not queue a provisioning request")


def _identity(activity):
    recipient = activity.recipient
    sender = activity.from_property
    return {
        "agent_identity_id": getattr(recipient, "agentic_app_id", None),
        "agent_user_id": getattr(recipient, "agentic_user_id", None) or getattr(recipient, "aad_object_id", None),
        "sender_id": getattr(sender, "aad_object_id", None) if sender else None,
        "sender_name": getattr(sender, "name", None) if sender else None,
    }


def _email_reference(activity):
    """The Outlook message behind a mail activity, if the activity carries one."""
    try:
        return AgentNotificationActivity(activity).email
    except Exception:  # the wrapper rejects activities without a known notification type
        return None


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
        queue_url = os.getenv("MARGO_PROVISION_QUEUE_URL")
        self.provision = ProvisionRequests(queue_url) if HOSTING != "instance" and queue_url else None
        self.voice = None
        if HOSTING == "instance" and os.getenv("VOICE_LIVE_MODEL"):
            from ..voice.server import VoiceService

            self.voice = VoiceService(orchestrator=self.orchestrator, connections=self.connections,
                                      instance=self.orchestrator.instance, tenant_id=os.environ["MARGO_TENANT_ID"],
                                      client_id=os.environ["CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID"],
                                      scope=os.getenv("MARGO_VOICE_SCOPE", "Voice.Talk"))
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
            conversation = getattr(activity, "conversation", None)
            if not event["agent_identity_id"] or not event["agent_user_id"] or not getattr(conversation, "id", None):
                logger.warning("message without agent identity or conversation; ignored")
                return
            # Mail reaches this route first; its Outlook message ID survives redelivery.
            reference = _email_reference(activity) if email else None
            activity_id, conversation_id = stable_ids(getattr(reference, "id", None) or activity.id,
                                                      getattr(reference, "conversation_id", None) or conversation.id,
                                                      event["sender_id"], activity.text or "")
            # conversation_type decides whether this is the manager's one-to-one chat; a group chat
            # or channel never runs a turn that reads as the manager.
            event.update({"conversation_id": conversation_id, "activity_id": activity_id,
                          "conversation_type": getattr(conversation, "conversation_type", None),
                          "channel": "mail" if email else "teams", "text": activity.text or ""})
            if self.provision is not None:
                asyncio.ensure_future(self.provision.ensure(event["agent_identity_id"], event["agent_user_id"]))
            typing = asyncio.ensure_future(self._typing(context))
            try:
                result = await self.orchestrator.handle(event, self._token_getter(context))
            except Exception:  # handle() reports its own failures; this is a last resort
                logger.exception("turn failed")
                result = {"reply": None, "status": "error"}
            finally:
                typing.cancel()
            logger.info("turn status=%s", result.get("status"))
            if result.get("reply") and not email:  # this version never answers mail
                await context.send_activity(Activity(type="message", text=result["reply"], text_format="markdown"))

        # Only mail notifications become requests to the office. Lifecycle events and document
        # comments are not messages to Margo and are never queued.
        @self.notifications.on_email(**handlers)
        async def on_mail_notification(context: TurnContext, _: TurnState, notification: AgentNotificationActivity):
            activity = context.activity
            event = _identity(activity)
            sender = getattr(activity, "from_property", None)
            email = getattr(notification, "email", None)
            text = getattr(activity, "text", None) or ""
            sender_id = getattr(sender, "aad_object_id", None) or getattr(sender, "id", None)
            message_id, conversation_id = stable_ids(
                getattr(email, "id", None) or activity.id,
                getattr(email, "conversation_id", None) or getattr(getattr(activity, "conversation", None), "id", None),
                sender_id, text)
            event.update({"sender_id": sender_id, "sender_name": getattr(sender, "name", None),
                          "conversation_id": conversation_id, "activity_id": message_id, "channel": "mail",
                          "conversation_type": None, "text": text})
            if not event["agent_identity_id"] or not event["agent_user_id"]:
                return
            try:
                result = await self.orchestrator.handle(event, self._token_getter(context))
                logger.info("notification status=%s", result.get("status"))
            except Exception:
                logger.exception("notification failed")
            # No reply: this version never answers mail.

        @self.notifications.on_agent_lifecycle_notification("*")  # SDK's on_lifecycle() is broken in 2.0.0.dev0
        async def on_lifecycle(context: TurnContext, _: TurnState, notification: AgentNotificationActivity):
            logger.info("lifecycle notification: %s", getattr(context.activity, "value_type", None))
            if self.provision is not None:
                message = lifecycle_message(context.activity.model_dump(mode="json", by_alias=True, exclude_none=True))
                if message is not None:
                    await self.provision.send(message)

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
        elif HOSTING == "instance":
            # No platform edge in front of an instance container: every activity's token is checked
            # here, as the router already did. Without the blueprint's identity, refuse to start.
            if not (client_id and tenant_id):
                raise RuntimeError("instance hosting needs the blueprint client ID and tenant to check tokens")
            auth_configuration = AgentAuthConfiguration(client_id=client_id, tenant_id=tenant_id)
        middlewares = []
        if auth_configuration:
            @middleware
            async def jwt(request, handler):
                # The voice page authenticates the person with their own sign-in (voice/server.py).
                if request.path in HEALTH_PATHS or request.path == "/voice" or request.path.startswith("/voice/"):
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
        if self.voice is not None:
            from ..voice.server import mount

            mount(app, self.voice)
        return app


def main():
    host = MargoHost()
    run_app(host.web_app(), host=os.getenv("HOST", "0.0.0.0"), port=int(os.getenv("PORT", "8088")))
    return 0
