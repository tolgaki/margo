"""The voice endpoint inside one instance container: the page, its sign-in config and the session.

Order, each step failing closed:

1. The page signs the person in with Entra (authorization code + PKCE) for the blueprint's
   delegated voice scope and sends the token as its first WebSocket message.
2. The token's signature, issuer and expiry are verified (PyJWT, the tenant's published keys),
   then its tenant, audience, scope and caller (``check_claims``).
3. Margo's own token is obtained for this instance, and her manager is looked up in the directory
   exactly as in a Teams turn. Only the manager may talk to her.
4. The manager's token for Work IQ is obtained on behalf of the person's sign-in.
5. A Voice Live session starts with Margo's instructions and tools; every tool call runs through
   the engine's dispatcher, the guard and, for state, the state lease.

No audio or transcript is stored, and tokens, audio and text are never logged.
"""

import asyncio
import json
import logging
import os
from pathlib import Path

from aiohttp import WSMsgType
from aiohttp.web import FileResponse, Response, WebSocketResponse, json_response

from ..engine import _clip, function_tools, load_instructions
from ..guard import ATTENDED, CallBudget, WorkIQGuard
from ..instance import InstanceBinding
from ..office import OfficeReader
from ..orchestrator import TurnTokens
from ..skills import SkillLibrary
from .session import VOICE_ADDENDUM, TokenRejected, VoiceBridge, VoiceSettings, check_claims

WORKIQ_SCOPE = "api://workiq.svc.cloud.microsoft/.default"
STATIC = Path(__file__).resolve().parent / "static"
FILES = {"": ("index.html", "text/html"), "index.html": ("index.html", "text/html"),
         "voice.css": ("voice.css", "text/css"), "voice.js": ("voice.js", "text/javascript"),
         "voice-core.mjs": ("voice-core.mjs", "text/javascript"), "pcm-worklets.js": ("pcm-worklets.js", "text/javascript")}
CSP = ("default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self' https://login.microsoftonline.com; "
       "img-src 'self' data:; media-src 'self'; worker-src 'self'; base-uri 'none'; form-action 'none'; "
       "frame-ancestors 'none'")
VOICE_CALL_BUDGET, VOICE_STATE_BUDGET = 120, 60
HELLO_SECONDS = 15
MAX_FRAME = 256 * 1024

logger = logging.getLogger("margo.voice")


class VoiceService:
    def __init__(self, *, orchestrator, connections, instance, tenant_id, client_id, scope="Voice.Talk",
                 settings=None, endpoint=None):
        self.endpoint = endpoint or os.getenv("VOICE_LIVE_ENDPOINT")
        if not self.endpoint:
            raise RuntimeError("VOICE_LIVE_ENDPOINT is required for voice")  # fail at start-up, not mid-call
        self.orchestrator, self.engine = orchestrator, orchestrator.engine
        self.connections, self.instance = connections, instance
        self.tenant_id, self.client_id, self.scope = tenant_id, client_id, scope
        self.settings = settings or VoiceSettings.from_env()
        self._jwks = None
        self._initialised = set()

    # -- page --------------------------------------------------------------------------

    async def page(self, request):
        name = request.match_info.get("file", "")
        if name not in FILES:
            return Response(status=404)
        filename, content_type = FILES[name]
        return FileResponse(STATIC / filename, headers={"Content-Type": content_type, "Content-Security-Policy": CSP,
                                                        "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff"})

    async def config(self, _request):
        return json_response({"tenantId": self.tenant_id, "clientId": self.client_id,
                              "scope": "api://%s/%s" % (self.client_id, self.scope)},
                             headers={"Cache-Control": "no-store"})

    # -- identity ----------------------------------------------------------------------

    def _verify(self, token):
        import jwt

        if self._jwks is None:
            self._jwks = jwt.PyJWKClient("https://login.microsoftonline.com/%s/discovery/v2.0/keys" % self.tenant_id)
        try:
            key = self._jwks.get_signing_key_from_jwt(token).key
            claims = jwt.decode(token, key, algorithms=["RS256"], audience=[self.client_id, "api://" + self.client_id],
                                issuer=["https://login.microsoftonline.com/%s/v2.0" % self.tenant_id,
                                        "https://sts.windows.net/%s/" % self.tenant_id],
                                options={"require": ["exp", "iat", "aud", "iss"]}, leeway=60)
        except jwt.PyJWTError as exc:
            raise TokenRejected("token failed verification: %s" % type(exc).__name__) from None
        return check_claims(claims, tenant_id=self.tenant_id, client_id=self.client_id, scope=self.scope)

    async def _tokens(self, user_token):
        connection = self.connections.get_default_connection()
        margo = await connection.get_agentic_user_token(self.tenant_id, self.instance[0], self.instance[1], [WORKIQ_SCOPE])
        principal = None
        if self.orchestrator.principal_access == "obo":
            principal = await connection.acquire_token_on_behalf_of([WORKIQ_SCOPE], user_token)
        return margo, principal

    # -- session -----------------------------------------------------------------------

    async def socket(self, request):
        ws = WebSocketResponse(max_msg_size=MAX_FRAME, heartbeat=20)
        await ws.prepare(request)
        try:
            await self._session(ws)
        except TokenRejected as exc:
            logger.info("voice sign-in refused: %s", exc)
            await self._fatal(ws, "Your sign-in can't be used to talk to this Margo.")
        except Exception:
            logger.exception("voice session failed")
            await self._fatal(ws, "Something failed on my side, so the call ended. Nothing was sent.")
        finally:
            await ws.close()
        return ws

    async def _fatal(self, ws, message):
        if not ws.closed:
            await ws.send_json({"type": "error", "fatal": True, "message": message})

    async def _session(self, ws):
        hello = await asyncio.wait_for(ws.receive(), HELLO_SECONDS)
        data = json.loads(hello.data) if hello.type == WSMsgType.TEXT else {}
        token = data.get("token") if isinstance(data, dict) and data.get("type") == "hello" else None
        if not isinstance(token, str) or not token:
            raise TokenRejected("no token")
        caller = await asyncio.to_thread(self._verify, token)
        margo_token, principal_token = await self._tokens(token)
        if not margo_token:
            raise RuntimeError("no agent token")
        event = {"agent_identity_id": self.instance[0], "agent_user_id": self.instance[1]}
        manager = await self.orchestrator._manager(event, margo_token)
        if not manager or manager[0] != caller:
            raise TokenRejected("caller is not this instance's manager")
        binding = InstanceBinding(self.instance[0], self.instance[1], manager[0])
        sync, preferences = self.orchestrator.state_sync(binding)
        root = str(self.orchestrator.state_root)
        state = self.orchestrator._state_commands(binding, preferences, budget=CallBudget(VOICE_STATE_BUDGET))

        async def run_locked(work):
            return (await sync.run(work))["result"]

        if binding.account not in self._initialised:
            async def initialise():
                await asyncio.to_thread(lambda: binding.open_ledger(root).close())
                await asyncio.to_thread(state.initialise)
            await run_locked(initialise)
            self._initialised.add(binding.account)

        tokens = {"margo": margo_token}
        if principal_token:
            tokens["principal"] = principal_token
        guard = WorkIQGuard(binding, TurnTokens(tokens), self.orchestrator.transport,
                            budget=CallBudget(VOICE_CALL_BUDGET), principal_access=self.orchestrator.principal_access,
                            recorder=self.orchestrator.recorder)
        library = SkillLibrary(self.engine.repo_root, preferences)
        office = OfficeReader(lambda: binding.open_ledger(root))
        instructions = load_instructions(self.engine.repo_root, timezone=self.engine.timezone,
                                         library=library) + "\n\n" + VOICE_ADDENDUM

        async def dispatch(name, raw_arguments):
            return await self.engine._dispatch(name, raw_arguments, ATTENDED, guard, office, library, state)

        await self._converse(ws, instructions, function_tools(ATTENDED), dispatch, run_locked)

    async def _converse(self, ws, instructions, tools, dispatch, run_locked):
        from azure.ai.voicelive.aio import connect
        from azure.identity.aio import DefaultAzureCredential, ManagedIdentityCredential

        client_id = os.getenv("AZURE_CLIENT_ID")
        credential = ManagedIdentityCredential(client_id=client_id) if client_id else DefaultAzureCredential()
        async with credential, connect(credential=credential, endpoint=self.endpoint,
                                       model=self.settings.model) as connection:
            bridge = VoiceBridge(dispatch=dispatch, upstream=connection.send, downstream=ws.send_json,
                                 settings=self.settings, clip=_clip, run_locked=run_locked)
            await bridge.start(instructions, tools)

            async def from_service():
                async for event in connection:
                    await bridge.from_service(event.as_dict() if hasattr(event, "as_dict") else dict(event))

            async def from_client():
                async for message in ws:
                    if message.type != WSMsgType.TEXT:
                        break
                    try:
                        payload = json.loads(message.data)
                    except ValueError:
                        continue
                    if await bridge.from_client(payload) == "stop":
                        break

            tasks = [asyncio.ensure_future(from_service()), asyncio.ensure_future(from_client())]
            try:
                await asyncio.wait(tasks, timeout=self.settings.max_seconds, return_when=asyncio.FIRST_COMPLETED)
            finally:
                for task in tasks:
                    task.cancel()
            reason = "time limit reached" if bridge.expired() else "call ended"
            if not ws.closed:
                await ws.send_json({"type": "closed", "reason": reason})


def mount(app, service):
    """Add the voice routes to the instance's aiohttp application."""
    app.router.add_get("/voice/config", service.config)
    app.router.add_get("/voice/ws", service.socket)
    app.router.add_get("/voice", lambda request: Response(status=301, headers={"Location": "voice/"}))
    app.router.add_get("/voice/{file:[A-Za-z0-9._-]*}", service.page)
