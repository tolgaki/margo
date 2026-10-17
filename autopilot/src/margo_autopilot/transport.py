"""Minimal MCP client for the Work IQ endpoint (streamable HTTP, JSON-RPC 2.0). Standard library only.

It does one thing: send a ``tools/call`` to ``https://workiq.svc.cloud.microsoft/mcp`` with the
bearer token the guard chose, and return a plain dict. It never chooses tokens, never retries a
call that may have been processed, and maps failures onto the guard's transport errors so the
guard can record truthful outcomes.

The only retry is the one the MCP specification requires: if the server answers ``404`` for a
session it no longer knows, that request was not processed, so the client starts a new session
and sends it once more.
"""

import hashlib
import itertools
import json
import socket
import threading
import urllib.error
import urllib.request

from .guard import WORKIQ_MCP_URL, TransportError, TransportTimeout

PROTOCOL_VERSION = "2025-06-18"
CLIENT_INFO = {"name": "margo-autopilot", "version": "0.1.0"}


def _parse_body(raw, content_type, request_id):
    """Return the JSON-RPC message answering ``request_id`` from a JSON or SSE body."""
    text = raw.decode("utf-8", errors="replace")
    if "text/event-stream" in (content_type or ""):
        messages = []
        for block in text.replace("\r\n", "\n").split("\n\n"):
            data = "\n".join(line[5:].lstrip() for line in block.split("\n") if line.startswith("data:"))
            if data:
                try:
                    messages.append(json.loads(data))
                except ValueError:
                    continue
    else:
        try:
            parsed = json.loads(text) if text.strip() else None
        except ValueError as exc:
            raise TransportError("Work IQ returned a non-JSON body") from exc
        messages = parsed if isinstance(parsed, list) else [parsed] if parsed is not None else []
    for message in messages:
        if isinstance(message, dict) and message.get("id") == request_id:
            return message
    raise TransportError("Work IQ response did not answer the request")


def _normalise(result):
    """Turn an MCP tool result into the dict shape the guard reads (``statusCode``/``results``)."""
    if not isinstance(result, dict):
        raise TransportError("malformed tool result")
    structured = result.get("structuredContent")
    payload = structured if isinstance(structured, dict) else None
    if payload is None:
        texts = [item.get("text") for item in result.get("content", []) if isinstance(item, dict)
                 and item.get("type") == "text" and isinstance(item.get("text"), str)]
        joined = "\n".join(texts)
        try:
            parsed = json.loads(joined) if joined else {}
            payload = parsed if isinstance(parsed, dict) else {"value": parsed}
        except ValueError:
            payload = {"text": joined}
    if result.get("isError"):
        code = payload.get("statusCode") if isinstance(payload.get("statusCode"), int) else None
        raise TransportError("Work IQ tool error", status_code=code)
    return payload


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    """Never follow a redirect: it would carry the bearer token to another URL."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


_OPENER = urllib.request.build_opener(_NoRedirect())


class McpHttpTransport:
    """One MCP session per bearer token; thread-safe; no retries of possibly-processed calls."""

    def __init__(self, url=WORKIQ_MCP_URL, *, timeout=60, allow_loopback_http=False):
        insecure_ok = allow_loopback_http and url.startswith(("http://127.0.0.1:", "http://localhost:"))
        if not url.startswith("https://") and not insecure_ok:
            raise ValueError("Work IQ transport requires https")
        self.endpoint = url
        self.timeout = timeout
        self._sessions = {}
        self._ids = itertools.count(1)
        self._lock = threading.Lock()

    # -- public -------------------------------------------------------------------------

    def call(self, tool, arguments, *, bearer):
        key = hashlib.sha256(bearer.encode("utf-8")).hexdigest()
        session = self._session(key, bearer)
        request = {"jsonrpc": "2.0", "id": next(self._ids), "method": "tools/call",
                   "params": {"name": tool, "arguments": arguments}}
        try:
            message = self._post(request, bearer, session)
        except _UnknownSession:
            with self._lock:
                self._sessions.pop(key, None)
            message = self._post(request, bearer, self._session(key, bearer))
        if "error" in message:
            raise TransportError("Work IQ JSON-RPC error")
        return _normalise(message.get("result"))

    # -- internals ----------------------------------------------------------------------

    def _session(self, key, bearer):
        with self._lock:
            if key in self._sessions:
                return self._sessions[key]
        init = {"jsonrpc": "2.0", "id": next(self._ids), "method": "initialize",
                "params": {"protocolVersion": PROTOCOL_VERSION, "capabilities": {}, "clientInfo": CLIENT_INFO}}
        headers = {}
        message = self._post(init, bearer, None, capture=headers)
        if "error" in message:
            raise TransportError("Work IQ refused the MCP session")
        session = {"id": headers.get("mcp-session-id"),
                   "version": (message.get("result") or {}).get("protocolVersion", PROTOCOL_VERSION)}
        self._post({"jsonrpc": "2.0", "method": "notifications/initialized"}, bearer, session, notify=True)
        with self._lock:
            self._sessions[key] = session
        return session

    def _post(self, message, bearer, session, *, capture=None, notify=False):
        headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream",
                   "Authorization": "Bearer " + bearer}
        if session:
            headers["MCP-Protocol-Version"] = session["version"]
            if session.get("id"):
                headers["Mcp-Session-Id"] = session["id"]
        request = urllib.request.Request(self.endpoint, data=json.dumps(message).encode("utf-8"),
                                         headers=headers, method="POST")
        try:
            with _OPENER.open(request, timeout=self.timeout) as response:
                if capture is not None:
                    capture.update({name.lower(): value for name, value in response.headers.items()})
                body = response.read()
                if notify:
                    return None
                return _parse_body(body, response.headers.get("Content-Type"), message.get("id"))
        except urllib.error.HTTPError as exc:
            exc.close()
            if exc.code == 404 and session and session.get("id"):
                raise _UnknownSession() from exc
            raise TransportError("Work IQ HTTP %d" % exc.code, status_code=exc.code) from exc
        except (socket.timeout, TimeoutError) as exc:
            raise TransportTimeout("Work IQ timed out") from exc
        except urllib.error.URLError as exc:
            if isinstance(exc.reason, (socket.timeout, TimeoutError)):
                raise TransportTimeout("Work IQ timed out") from exc
            raise TransportError("Work IQ unreachable") from exc


class _UnknownSession(Exception):
    """The server no longer recognises the session; the request was not processed."""
