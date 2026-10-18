"""The router service: one public endpoint in front of every hired Margo's own container.

- ``POST /api/messages`` (and ``/activity/messages``): the platform token is validated here with
  the Agents SDK middleware, then the activity is forwarded unchanged (with its token) to the
  instance, which validates it again. Lifecycle notifications become provisioning messages.
- ``/i/{key}/voice/...``: the voice page and its WebSocket, forwarded to the instance. The
  instance authenticates the person; the router only forwards bytes.

The router never logs bodies, message text or tokens.
"""

import asyncio
import json
import logging
import os
import time

from aiohttp import ClientSession, ClientTimeout, WSMsgType
from aiohttp.web import Application, Response, WebSocketResponse, json_response, run_app
from aiohttp.web_middlewares import middleware
from microsoft_agents.hosting.aiohttp import jwt_authorization_middleware
from microsoft_agents.hosting.core import AgentAuthConfiguration

from ..instances import RouteTable
from .core import MAX_ACTIVITY_BYTES, decide, voice_target

HEALTH_PATHS = {"/", "/liveness", "/readiness"}
ACTIVITY_PATHS = {"/api/messages", "/activity/messages"}
ROUTES_TTL_SECONDS = 15
FORWARD_TIMEOUT = ClientTimeout(total=300, connect=10)
MAX_VOICE_FRAME = 256 * 1024

logging.basicConfig(level=getattr(logging, os.getenv("LOG_LEVEL", "INFO").upper(), logging.INFO),
                    format="%(asctime)s %(levelname)s %(name)s | %(message)s")
logger = logging.getLogger("margo.router")


def _credential():
    from azure.identity.aio import DefaultAzureCredential, ManagedIdentityCredential

    client_id = os.getenv("AZURE_CLIENT_ID")
    return ManagedIdentityCredential(client_id=client_id) if client_id else DefaultAzureCredential()


class Router:
    def __init__(self, *, load_routes, send_message, session=None):
        self._load_routes, self._send = load_routes, send_message
        self._session = session
        self._table, self._loaded = RouteTable(), 0.0
        self._lock = asyncio.Lock()

    async def table(self):
        async with self._lock:
            if time.monotonic() - self._loaded > ROUTES_TTL_SECONDS:
                try:
                    self._table = RouteTable.loads(await self._load_routes())
                    self._loaded = time.monotonic()
                except Exception:  # keep serving the last good table rather than none
                    logger.exception("route table could not be loaded")
            return self._table

    def session(self):
        if self._session is None:
            self._session = ClientSession(timeout=FORWARD_TIMEOUT)
        return self._session

    async def messages(self, request):
        body = await request.content.read(MAX_ACTIVITY_BYTES + 1)
        if len(body) > MAX_ACTIVITY_BYTES:
            return Response(status=413)
        try:
            activity = json.loads(body)
        except ValueError:
            return Response(status=400)
        decision = decide(activity, await self.table())
        kind = decision["kind"]
        logger.info("activity kind=%s key=%s", kind, decision.get("key"))
        if kind in ("lifecycle", "provision"):
            await self._send(json.dumps(decision["message"], sort_keys=True))
        if kind != "forward":
            headers = {"Retry-After": str(decision["retry_after"])} if "retry_after" in decision else None
            return Response(status=decision["status"], headers=headers)
        headers = {name: request.headers[name] for name in ("Authorization", "Content-Type") if name in request.headers}
        async with self.session().post(decision["url"] + "/activity/messages", data=body, headers=headers) as upstream:
            payload = await upstream.read()
            return Response(status=upstream.status, body=payload,
                            content_type=upstream.content_type if payload else None)

    async def voice(self, request):
        target = voice_target(request.path, await self.table())
        if target["status"] != 200:
            headers = {"Retry-After": str(target["retry_after"])} if "retry_after" in target else None
            return Response(status=target["status"], headers=headers)
        # No query string is forwarded: the page's sign-in redirect carries a one-time code that
        # only the browser needs.
        url = target["url"]
        if request.headers.get("Upgrade", "").lower() == "websocket":
            return await self._voice_socket(request, url)
        async with self.session().get(url) as upstream:
            payload = await upstream.read()
            headers = {name: upstream.headers[name] for name in
                       ("Content-Type", "Cache-Control", "Content-Security-Policy") if name in upstream.headers}
            return Response(status=upstream.status, body=payload, headers=headers)

    async def _voice_socket(self, request, url):
        downstream = WebSocketResponse(max_msg_size=MAX_VOICE_FRAME, heartbeat=20)
        await downstream.prepare(request)
        ws_url = "ws" + url[len("http"):]
        try:
            async with self.session().ws_connect(ws_url, max_msg_size=MAX_VOICE_FRAME, heartbeat=20) as upstream:
                async def pump(source, sink):
                    async for message in source:
                        if message.type == WSMsgType.TEXT:
                            await sink.send_str(message.data)
                        elif message.type == WSMsgType.BINARY:
                            await sink.send_bytes(message.data)
                        else:
                            break
                tasks = [asyncio.ensure_future(pump(downstream, upstream)), asyncio.ensure_future(pump(upstream, downstream))]
                await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
                for task in tasks:
                    task.cancel()
        except Exception:
            logger.warning("voice connection to the instance failed")
        finally:
            await downstream.close()
        return downstream


def build_app(router, auth_configuration):
    @middleware
    async def authenticate(request, handler):
        if request.path in ACTIVITY_PATHS:
            return await jwt_authorization_middleware(request, handler)
        return await handler(request)

    app = Application(middlewares=[authenticate], client_max_size=MAX_ACTIVITY_BYTES)
    app["agent_configuration"] = auth_configuration
    for path in ACTIVITY_PATHS:
        app.router.add_post(path, router.messages)

    async def health(_request):
        return json_response({"status": "ok"})

    for path in HEALTH_PATHS:
        app.router.add_get(path, health)
    app.router.add_route("GET", r"/i/{key:[0-9a-f]{20}}/voice{rest:.*}", router.voice)
    return app


def main():
    from azure.storage.blob.aio import BlobClient
    from azure.storage.queue.aio import QueueClient

    client_id = os.environ["CONNECTIONS__SERVICE_CONNECTION__SETTINGS__CLIENTID"]
    tenant_id = os.environ["CONNECTIONS__SERVICE_CONNECTION__SETTINGS__TENANTID"]
    auth = AgentAuthConfiguration(client_id=client_id, tenant_id=tenant_id)
    credential = _credential()
    routes = BlobClient.from_blob_url(os.environ["MARGO_ROUTES_URL"], credential=credential)
    queue = QueueClient.from_queue_url(os.environ["MARGO_PROVISION_QUEUE_URL"], credential=credential)

    async def load_routes():
        try:
            return (await (await routes.download_blob()).readall()).decode("utf-8")
        except Exception as exc:
            if type(exc).__name__ == "ResourceNotFoundError":
                return ""
            raise

    async def send_message(text):
        await queue.send_message(text)

    run_app(build_app(Router(load_routes=load_routes, send_message=send_message), auth),
            host=os.getenv("HOST", "0.0.0.0"), port=int(os.getenv("PORT", "8088")))
    return 0
