"""One voice session, independent of transport: configuration, events, tools and identity checks.

Voice is another way into the same Margo. The session's instructions are the same persona, skill
index and not-hosted list as a Teams turn, plus a short note on speaking. Its tools are the same
function tools, and every call is executed here by the engine's own dispatcher, so it passes the
same guard, state tool and budgets. Nothing is approved or sent by voice: the guard refuses writes
and the state tool keeps approvals for cards. No audio is stored, and nothing here keeps a
transcript; captions go to the person's own page only.
"""

import base64
import binascii
import os
import time
from dataclasses import dataclass

VOICE_ADDENDUM = """# Speaking

This conversation is spoken. Your manager hears you through a synthetic voice, and you hear them
through speech recognition, which can mishear.
- Begin your first reply by saying, in a few words, that you are Margo, an AI assistant.
- Speak in short sentences. No tables, Markdown, links read aloud or emoji. Say times and dates the
  way people say them, in your manager's time zone.
- Before a lookup that takes time, say in a few words what you are checking.
- Nothing is approved or sent by voice. When something needs approval, say exactly what you would
  do and that the approval happens in Teams.
- If you are unsure what you heard, or who is speaking, ask before acting on it.
"""
DISCLOSURE = "You're talking to Margo, an AI assistant. Her voice is synthetic. Audio is not recorded."
DEFAULT_VOICE = "en-US-Ava:DragonHDLatestNeural"
MAX_AUDIO_CHARS = 64 * 1024  # base64 of ~1 s of 24 kHz PCM16 mono; frames are 20 ms
LOCKED_TOOLS = frozenset({"margo-state", "office_requests"})
PROGRESS = (("workiq-", "Checking Microsoft 365..."), ("margo-state", "Checking my notes..."),
            ("office_requests", "Checking requests to the office..."))


@dataclass(frozen=True)
class VoiceSettings:
    model: str
    voice_name: str = DEFAULT_VOICE
    voice_endpoint_id: str = ""
    max_seconds: int = 1800
    max_tool_calls: int = 60

    @classmethod
    def from_env(cls, env=None):
        env = os.environ if env is None else env
        model = (env.get("VOICE_LIVE_MODEL") or "").strip()
        if not model:
            raise ValueError("VOICE_LIVE_MODEL is required for voice")
        return cls(model=model, voice_name=(env.get("MARGO_VOICE_NAME") or "").strip() or DEFAULT_VOICE,
                   voice_endpoint_id=(env.get("MARGO_VOICE_ENDPOINT_ID") or "").strip(),
                   max_seconds=int(env.get("MARGO_VOICE_MAX_SECONDS", "1800")),
                   max_tool_calls=int(env.get("MARGO_VOICE_MAX_TOOL_CALLS", "60")))

    def voice(self):
        if self.voice_endpoint_id:  # her trained custom voice, deployed on the same Foundry resource
            return {"type": "azure-custom", "name": self.voice_name, "endpoint_id": self.voice_endpoint_id}
        return {"type": "azure-standard", "name": self.voice_name}


def session_update(instructions, tools, settings):
    """The Voice Live ``session.update`` event: Margo's instructions and tools, her voice, and the
    service-side noise suppression, echo cancellation and semantic turn detection."""
    return {"type": "session.update", "session": {
        "modalities": ["text", "audio"],
        "instructions": instructions,
        "voice": settings.voice(),
        "input_audio_format": "pcm16",
        "output_audio_format": "pcm16",
        "turn_detection": {"type": "azure_semantic_vad"},
        "input_audio_noise_reduction": {"type": "azure_deep_noise_suppression"},
        "input_audio_echo_cancellation": {"type": "server_echo_cancellation"},
        "input_audio_transcription": {"model": "azure-speech"},
        "tools": [{"type": "function", "name": tool["name"], "description": tool.get("description", ""),
                   "parameters": tool.get("parameters", {"type": "object", "properties": {}})} for tool in tools],
        "tool_choice": "auto",
    }}


def _progress(name):
    for prefix, text in PROGRESS:
        if isinstance(name, str) and name.startswith(prefix):
            return text
    return ""


class VoiceBridge:
    """Moves events between the person's page and Voice Live, and runs Margo's tools.

    ``dispatch(name, raw_arguments)`` is the engine's dispatcher bound to this session's guard,
    state and skills. ``run_locked(coroutine_factory)`` runs a call that touches Margo's state under
    the state lease. ``clip(result)`` is the engine's output limiter.
    """

    def __init__(self, *, dispatch, upstream, downstream, settings, clip, run_locked=None, clock=time.monotonic):
        self._dispatch, self._up, self._down = dispatch, upstream, downstream
        self.settings, self._clip, self._run_locked = settings, clip, run_locked
        self._clock, self._started = clock, clock()
        self.tool_calls, self._outputs_pending, self._ready = 0, False, False

    async def start(self, instructions, tools):
        await self._up(session_update(instructions, tools, self.settings))

    def expired(self):
        return self._clock() - self._started > self.settings.max_seconds

    async def from_client(self, message):
        """Handle one message from the page. Returns ``"stop"`` when the person ends the call."""
        if not isinstance(message, dict):
            return None
        kind = message.get("type")
        if kind == "stop":
            return "stop"
        if kind == "audio" and self._ready:
            audio = message.get("audio")
            if not isinstance(audio, str) or not audio or len(audio) > MAX_AUDIO_CHARS:
                return None
            try:
                base64.b64decode(audio, validate=True)
            except (binascii.Error, ValueError):
                return None
            await self._up({"type": "input_audio_buffer.append", "audio": audio})
        return None

    async def from_service(self, event):
        kind = event.get("type") if isinstance(event, dict) else None
        if kind == "session.updated" and not self._ready:
            self._ready = True
            await self._down({"type": "ready", "disclosure": DISCLOSURE, "voice": self.settings.voice_name})
        elif kind == "response.audio.delta" and isinstance(event.get("delta"), str):
            await self._down({"type": "audio", "audio": event["delta"]})
        elif kind == "input_audio_buffer.speech_started":
            await self._down({"type": "interrupt"})  # barge-in: stop her mid-sentence
        elif kind == "conversation.item.input_audio_transcription.completed":
            await self._caption("you", event.get("transcript"))
        elif kind == "response.audio_transcript.done":
            await self._caption("margo", event.get("transcript"))
        elif kind == "response.function_call_arguments.done":
            await self._call(event)
        elif kind == "response.done" and self._outputs_pending:
            self._outputs_pending = False
            await self._up({"type": "response.create"})
            await self._down({"type": "status", "text": ""})
        elif kind == "error":
            await self._down({"type": "error", "fatal": False,
                              "message": "The voice service reported a problem; try saying that again."})

    async def _caption(self, who, text):
        if isinstance(text, str) and text.strip():
            await self._down({"type": "caption", "who": who, "text": text.strip()[:2000]})

    async def _call(self, event):
        name, call_id = event.get("name"), event.get("call_id")
        if not isinstance(call_id, str) or not call_id:
            return
        self.tool_calls += 1
        if self.tool_calls > self.settings.max_tool_calls:
            result = {"status": "budget_exhausted", "reason": "this voice session's lookup budget is used up"}
        else:
            progress = _progress(name)
            if progress:
                await self._down({"type": "status", "text": progress})
            arguments = event.get("arguments")

            async def run():
                return await self._dispatch(name, arguments)

            if name in LOCKED_TOOLS and self._run_locked is not None:
                result = await self._run_locked(run)
            else:
                result = await run()
        await self._up({"type": "conversation.item.create",
                        "item": {"type": "function_call_output", "call_id": call_id, "output": self._clip(result)}})
        self._outputs_pending = True


class TokenRejected(ValueError):
    """The person's sign-in does not authorise a voice session with this Margo."""


def check_claims(claims, *, tenant_id, client_id, scope, now=None):
    """Return the caller's object ID if a validated token's claims allow a voice session.

    The signature, issuer and expiry are checked by the JWT library first; this checks what the
    claims mean: the right tenant, audience and delegated scope, and a user (not an app) caller.
    """
    now = time.time() if now is None else now
    if not isinstance(claims, dict):
        raise TokenRejected("no claims")
    if claims.get("tid") != tenant_id:
        raise TokenRejected("token is from another tenant")
    if claims.get("aud") not in (client_id, "api://" + client_id):
        raise TokenRejected("token is for another application")
    if scope not in str(claims.get("scp", "")).split():
        raise TokenRejected("token lacks the voice scope")
    if not isinstance(claims.get("exp"), (int, float)) or claims["exp"] <= now:
        raise TokenRejected("token has expired")
    oid = claims.get("oid")
    if not isinstance(oid, str) or not oid or claims.get("idtyp") == "app":
        raise TokenRejected("token does not identify a person")
    return oid
