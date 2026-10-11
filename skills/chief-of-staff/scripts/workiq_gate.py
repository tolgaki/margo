#!/usr/bin/env python3
"""Work IQ action gate: a local MCP proxy that enforces the remote-host action tiers.

On a remote host Copilot never talks to Work IQ directly. ``workiq_gate_client.py`` relays its
stdio to this gate over a unix socket; the gate identifies every caller by peer credentials, owns
the single Work IQ child process, and decides each ``tools/call`` from tool, path, method and
body. Reads pass unchanged. Private reversible writes (T1) need an active manager directive or
standing rule and are journaled with the state read just before the write. Anything that
communicates or destroys (T2/T3) needs an unused exact approval in the work ledger, which also
records the execution claim and the receipt. The classifier fails closed, an uncertain upstream
outcome is recorded as ``outcome_unknown`` and never retried, and every decision is appended to
an audit log that carries digests, never bodies.

    python3 workiq_gate.py serve --deployment /etc/margo/deployment.json
    python3 workiq_gate.py classify --tool do_action --arguments '{"path": "/me/sendMail", "method": "POST"}'
    python3 workiq_gate.py check-config --deployment /etc/margo/deployment.json

Authorization is by peer uid, not by what a message says: ``manager`` is a uid listed in
``control.cli_logins``, ``service`` is the gate's own uid (Copilot runs as that user), anything
else is denied. Because Copilot shares that uid, ``margo/shutdown`` is honoured only when the
gate was started with ``--allow-shutdown`` (tests); a service stops on SIGTERM. No network or
model call ever happens inside a database transaction.
"""

import argparse
import hashlib
import json
import os
import re
import shutil
import signal
import socket
import struct
import subprocess
import sys
import threading
import time
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

import margo_store
from margo_store import StateError, canonical_json, utc_now

SERVER_NAME = "margo-workiq-gate"
SERVER_VERSION = "1"
DEFAULT_SOCKET = "/run/margo/gate.sock"
PROTOCOL_FALLBACK = "2024-11-05"
WRITE_PREFIX = "[margo-gate] T1 needs margo_directive/margo_rule; T2/T3 need margo_action."
MAX_LINE = 16 * 1024 * 1024
MAX_NOTIFY_TEXT = 4000
GRANT_KEYS = ("margo_directive", "margo_rule", "margo_action")
TIERS = ("T0", "T1", "T2", "T3")
# JSON-RPC error codes fixed by the remote-host contract.
DENIED, POLICY, UPSTREAM, INVALID_PARAMS = -32001, -32002, -32003, -32602
METHOD_NOT_FOUND, PARSE_ERROR, INVALID_REQUEST = -32601, -32700, -32600
# Read-only MCP methods a client may send through to Work IQ besides tools/*.
FORWARDED_METHODS = {"ping", "resources/list", "resources/read", "resources/templates/list", "prompts/list",
                     "prompts/get", "completion/complete", "logging/setLevel"}
IMPLIED_METHOD = {"create_entity": "POST", "update_entity": "PATCH", "delete_entity": "DELETE", "do_action": "POST"}
DELETE_ACTIONS = {"delete", "remove", "purge", "cancel", "permanentdelete"}
DELETED_FOLDERS = {"deleteditems", "recoverableitemsdeletions"}
T1_MESSAGE_KEYS = {"isRead", "flag", "categories"}
T1_EVENT_KEYS = {"categories", "showAs", "isReminderOn", "reminderMinutesBeforeStart"}
T3_PATHS = re.compile(r"/(deleteditems|messagerules|inboxrules|permissions|members|cancel|subscriptions|mailboxsettings)(?:/|$)")
T2_PATHS = re.compile(r"/(send|sendmail|reply|replyall|forward|accept|decline|tentativelyaccept|createlink|invite|"
                      r"onlinemeetings|reactions|setreaction|unsetreaction|presence|teamwork)(?:/|$)"
                      r"|/chats/[^/]+/messages(?:/|$)|/channels/[^/]+/messages(?:/|$)")
EVENT_PATH = re.compile(r"/events(?:/[^/]+)?$")
MESSAGE_PATH = re.compile(r"/messages/[^/]+$")
MOVE_PATH = re.compile(r"/messages/[^/]+/move$")
DRAFT_COLLECTION = re.compile(r"(/messages|/mailfolders/[^/]+/messages)$")
FOLDER_COLLECTION = re.compile(r"(/mailfolders|/mailfolders/[^/]+/childfolders)$")
# HTTP status detection in tool results; anything ambiguous stays "outcome unknown".
STATUS_PATTERNS = (
    re.compile(r"(?i)\b(?:http(?:/\d(?:\.\d)?)?|status(?:\s*code)?|statuscode|status_code|http_status)\s*[:=]?\s*\(?(\d{3})\b"),
    re.compile(r"(?i)\b(\d{3})\s+(?:bad request|unauthorized|payment required|forbidden|not found|method not allowed|"
               r"not acceptable|request timeout|conflict|gone|length required|precondition failed|payload too large|"
               r"unsupported media type|unprocessable|locked|too many requests|internal server error|not implemented|"
               r"bad gateway|service unavailable|gateway timeout)\b"),
)
STATUS_KEYS = {"status", "statuscode", "status_code", "httpstatus", "http_status", "httpstatuscode"}
AUTH_MARKERS = ("invalid_grant", "interaction_required", "aadsts", "token has expired", "unauthenticated")


class GateError(StateError):
    """A refusal or failure with its JSON-RPC code; the message is what the caller sees."""

    def __init__(self, message, code=POLICY, data=None):
        super().__init__(message)
        self.code = code
        self.data = data


class UpstreamUnavailable(GateError):
    def __init__(self, message="upstream work iq server is unavailable"):
        super().__init__(message, UPSTREAM)


class UpstreamTimeout(UpstreamUnavailable):
    def __init__(self, message="upstream work iq call timed out; outcome unknown"):
        super().__init__(message)


def digest(value):
    return hashlib.sha256(canonical_json(value).encode("utf-8")).hexdigest()


def now():
    return datetime.now(timezone.utc)


def _directives():
    import manager_directives
    return manager_directives


def _ledger_module():
    import work_ledger
    return work_ledger


# ---------------------------------------------------------------------------
# Policy: pure functions over tool, arguments and the deployment anchor
# ---------------------------------------------------------------------------

def _first(arguments, keys):
    for key in keys:
        if key in arguments:
            return arguments[key]
    return None


def extract_call(arguments, deployment):
    """Return (path, method, body, action) using the deployment's argument keys; path is lowercased."""
    if not isinstance(arguments, dict):
        return None, None, None, None
    keys = deployment["workiq"]["argument_keys"]
    path = _first(arguments, keys["path"])
    if isinstance(path, str) and path.strip():
        path = re.sub(r"^https?://[^/]+(?:/v1\.0|/beta)?", "", path.strip()).casefold()
        if not path.startswith("/"):
            path = "/" + path
    else:
        path = None
    method = _first(arguments, keys["method"])
    method = method.strip().upper() if isinstance(method, str) and method.strip() else None
    action = _first(arguments, keys["action"])
    action = action.strip().casefold() if isinstance(action, str) and action.strip() else None
    return path, method, _first(arguments, keys["body"]), action


def path_scope(path, deployment):
    """Whose data a path touches: margo's own, the manager's (delegated), or other."""
    if not path:
        return None
    gate = deployment["gate"]
    for name, roots in (("margo", gate["margo_root_paths"]), ("manager", gate["manager_root_paths"])):
        if any(path.startswith(root.casefold()) for root in roots):
            return name
    return "other"


def _body_value(body, name):
    if not isinstance(body, dict):
        return None
    for key, value in body.items():
        if isinstance(key, str) and key.casefold() == name:
            return value
    return None


def _has_attendees_or_meeting(body):
    attendees = _body_value(body, "attendees")
    if isinstance(attendees, list) and attendees:
        return True
    if attendees not in (None, [], {}):
        return True  # anything unexpected under attendees is treated as inviting people
    if _body_value(body, "isonlinemeeting") is True or _body_value(body, "onlinemeeting") not in (None, {}):
        return True
    return _body_value(body, "onlinemeetingprovider") not in (None, "", "unknown")


def classify(tool, arguments, deployment):
    """Place one tools/call in a tier without side effects; anything the rules cannot name is T3."""
    workiq = deployment["workiq"]
    result = {"tier": "T3", "reason": "unclassified write", "path": None, "method": None, "scope": None}
    path, method, body, action = extract_call(arguments, deployment)
    if tool in workiq["read_tools"]:
        return dict(result, tier="T0", reason="read tool", path=path, method=method, scope=path_scope(path, deployment))
    if tool not in workiq["write_tools"]:
        return dict(result, reason="unknown tool")
    if method is None:
        method = IMPLIED_METHOD.get(tool)
    result.update(path=path, method=method, scope=path_scope(path, deployment))
    if path is None:
        return dict(result, reason="write without a path")
    bare = path.split("?", 1)[0].split("#", 1)[0].rstrip("/") or "/"
    destination = _body_value(body, "destinationid")
    destination = destination.strip().casefold() if isinstance(destination, str) else None

    def tier(name, reason):
        return dict(result, tier=name, reason=reason)

    # T3: destructive or security-relevant, whatever the scope.
    if method == "DELETE":
        return tier("T3", "delete")
    if action in DELETE_ACTIONS:
        return tier("T3", "destructive action " + action)
    if T3_PATHS.search(bare):
        return tier("T3", "security-relevant path")
    if method in {"DELETE", "PATCH"} and re.search(r"/mailfolders/[^/]+$", bare):
        return tier("T3", "mail folder change")
    if "/drive" in bare and method in {"PUT", "PATCH", "DELETE"}:
        return tier("T3", "drive overwrite or change")
    if bare.endswith("/move") and destination in DELETED_FOLDERS:
        return tier("T3", "move to deleted items")
    if method == "PATCH" and re.search(r"/users/[^/]+$", bare):
        return tier("T3", "user profile change")
    # T2: anything that reaches another person.
    if T2_PATHS.search(bare):
        return tier("T2", "communicating path")
    is_event = EVENT_PATH.search(bare) is not None
    if is_event and method in {"POST", "PATCH"} and _has_attendees_or_meeting(body):
        return tier("T2", "meeting with attendees or an online meeting")
    # T1: private, reversible, and only inside margo's or the manager's own data.
    if result["scope"] in {"margo", "manager"}:
        if method == "PATCH" and MESSAGE_PATH.search(bare):
            if isinstance(body, dict) and body and set(body) <= T1_MESSAGE_KEYS:
                return tier("T1", "message flags")
            if _body_value(body, "isdraft") is True or "/mailfolders/drafts/" in bare:
                return tier("T1", "draft edit")
            return tier("T3", "unclassified write")
        if method == "POST" and MOVE_PATH.search(bare):
            return tier("T1", "move")
        if method == "POST" and DRAFT_COLLECTION.search(bare):
            if "sendmail" in canonical_json(body).casefold() if body is not None else False:
                return tier("T3", "unclassified write")
            return tier("T1", "draft create")
        if method == "POST" and bare.endswith("/events"):
            return tier("T1", "private hold")
        if "/todo/" in bare:
            return tier("T1", "to-do")
        if method == "POST" and FOLDER_COLLECTION.search(bare):
            return tier("T1", "folder create")
        if method == "PATCH" and is_event and isinstance(body, dict) and body and set(body) <= T1_EVENT_KEYS:
            return tier("T1", "event flags")
    return tier("T3", "unclassified write")


def preread_path(path, method):
    """The resource whose state is journaled before a T1 write: the message for a move, the same path for a patch."""
    bare = path.split("?", 1)[0].rstrip("/")
    if bare.endswith("/move"):
        return bare[:-len("/move")]
    if method == "PATCH":
        return bare
    return bare + "?$top=1&$select=id"  # a create: prove the collection is reachable, bounded


def fingerprint(target, fetch):
    """Hash what an approval was given for; a resource is re-read so a changed target invalidates it."""
    if not isinstance(target, dict) or not target:
        raise StateError("preflight target must be a nonempty object")
    resource = target.get("resource")
    if resource is None:
        return digest({"target": target})
    if not isinstance(resource, str) or not resource.strip():
        raise StateError("preflight resource must be a graph path")
    resource = resource.strip()
    query = resource + ("&" if "?" in resource else "?") + "$select=id,changeKey,lastModifiedDateTime"
    try:
        data = fetch(query)
    except Exception as exc:  # any failure, including a refusal, leaves the target state unknown
        raise StateError("preflight read failed; target state unknown") from exc
    exists = isinstance(data, dict) and bool(data.get("id"))
    etag = "none"
    if exists:
        etag = data.get("changeKey") or data.get("lastModifiedDateTime") or "none"
    return digest({"resource": resource, "etag": etag, "exists": exists})


def http_status(result):
    """One unambiguous HTTP status from a tool result's structured content or text, else None."""
    found = set()
    if not isinstance(result, dict):
        return None
    stack = [result.get("structuredContent")]
    while stack:
        node = stack.pop()
        if not isinstance(node, dict):
            continue
        for key, value in node.items():
            if isinstance(key, str) and key.casefold() in STATUS_KEYS:
                if isinstance(value, int) and not isinstance(value, bool) and 100 <= value <= 599:
                    found.add(value)
                elif isinstance(value, str) and re.fullmatch(r"\d{3}", value.strip()):
                    found.add(int(value))
            elif isinstance(value, dict):
                stack.append(value)
    for item in result.get("content") or []:
        if isinstance(item, dict) and item.get("type") == "text" and isinstance(item.get("text"), str):
            for pattern in STATUS_PATTERNS:
                for match in pattern.finditer(item["text"][:4000]):
                    found.add(int(match.group(1)))
    return found.pop() if len(found) == 1 else None


def result_text(result):
    parts = []
    for item in (result.get("content") or []) if isinstance(result, dict) else []:
        if isinstance(item, dict) and isinstance(item.get("text"), str):
            parts.append(item["text"])
    return "\n".join(parts)


def outcome_of(result):
    """succeeded, failed (provably no effect) or outcome_unknown; only a clear non-retry 4xx proves no effect."""
    if not isinstance(result, dict):
        return "outcome_unknown"
    if not result.get("isError"):
        return "succeeded"
    status = http_status(result)
    if status is not None and 400 <= status <= 499 and status not in {408, 409, 429}:
        return "failed"
    return "outcome_unknown"


def receipt_for(tool, request_id, result, outcome, recorded_at):
    receipt = {"kind": "tool_result", "reference": "workiq-gate:%s:%s" % (tool, request_id), "outcome": outcome,
               "recorded_at": recorded_at, "result_digest": digest(result)}
    if outcome == "failed":
        receipt["definitive_no_effect"] = True
    return receipt


def refusal(reason):
    """A refusal the model can read; it is a tool result, not a transport error."""
    return {"isError": True, "content": [{"type": "text", "text": "margo-gate: " + reason}]}


def resolve_roles(peer_uid, cli_uids, gate_uid):
    """Roles a peer uid holds; an empty list is a denial. Manager and service can coincide."""
    roles = []
    if isinstance(peer_uid, int) and not isinstance(peer_uid, bool):
        if peer_uid in cli_uids:
            roles.append("manager")
        if peer_uid == gate_uid:
            roles.append("service")
    return roles


def render_template(value, substitutions):
    if isinstance(value, dict):
        return {key: render_template(item, substitutions) for key, item in value.items()}
    if isinstance(value, list):
        return [render_template(item, substitutions) for item in value]
    if isinstance(value, str):
        for name, replacement in substitutions.items():
            value = value.replace("{" + name + "}", replacement)
    return value


def tool_result_json(result):
    """The Graph document inside a fetch tool result, or StateError when it cannot be read."""
    if not isinstance(result, dict):
        raise StateError("fetch result is not an object")
    if result.get("isError"):
        raise StateError("fetch failed: " + (result_text(result)[:300] or "no detail"))
    structured = result.get("structuredContent")
    if isinstance(structured, (dict, list)):
        return structured
    text = result_text(result)
    try:
        return json.loads(text)
    except ValueError as exc:
        raise StateError("fetch result is not json") from exc


# ---------------------------------------------------------------------------
# Upstream: the single Work IQ child, multiplexed by gate-owned request ids
# ---------------------------------------------------------------------------

class Upstream:
    """One Work IQ MCP child over stdio. ``call`` returns the raw response; notifications fan out."""

    def __init__(self, command, env, on_notification, timeout=60.0):
        self.command = list(command)
        self.env = env
        self.on_notification = on_notification
        self.timeout = timeout
        self.process = None
        self.pending = {}
        self.lock = threading.Lock()
        self.write_lock = threading.Lock()
        self.counter = 0
        self.initialize_result = {}
        self.tool_names = []
        self.reader = None
        self.late = 0

    def start(self, startup_timeout):
        try:
            self.process = subprocess.Popen(self.command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                            stderr=None, env=self.env, bufsize=0)
        except OSError as exc:
            raise UpstreamUnavailable("cannot start the work iq command (%s)" % type(exc).__name__) from exc
        self.reader = threading.Thread(target=self._read_loop, name="workiq-upstream", daemon=True)
        self.reader.start()
        self.initialize_result = self.result("initialize", {
            "protocolVersion": PROTOCOL_FALLBACK, "capabilities": {},
            "clientInfo": {"name": SERVER_NAME, "version": SERVER_VERSION}}, timeout=startup_timeout)
        if not isinstance(self.initialize_result, dict):
            raise UpstreamUnavailable("upstream initialize returned no object")
        self.notify("notifications/initialized", {})
        self.tool_names = [tool.get("name") for tool in self.tools(timeout=startup_timeout)]

    def alive(self):
        return self.process is not None and self.process.poll() is None

    def _read_loop(self):
        stream = self.process.stdout
        while True:
            try:
                line = stream.readline(MAX_LINE + 1)
            except (OSError, ValueError):
                break
            if not line:
                break
            if len(line) > MAX_LINE or not line.strip():
                continue
            try:
                message = json.loads(line.decode("utf-8"))
            except (ValueError, UnicodeDecodeError):
                continue
            if not isinstance(message, dict):
                continue
            if "id" in message and ("result" in message or "error" in message):
                with self.lock:
                    waiter = self.pending.pop(message["id"], None)
                if waiter is None:
                    self.late += 1
                    continue
                waiter["response"] = message
                waiter["event"].set()
            elif "method" in message:
                try:
                    self.on_notification(message)
                except Exception:  # a misbehaving session must not stop the reader
                    pass
        with self.lock:
            waiters = list(self.pending.values())
            self.pending.clear()
        for waiter in waiters:
            waiter["event"].set()

    def _write(self, message):
        data = (canonical_json(message) + "\n").encode("utf-8")
        with self.write_lock:
            if not self.alive():
                raise UpstreamUnavailable()
            try:
                self.process.stdin.write(data)
                self.process.stdin.flush()
            except (OSError, ValueError) as exc:
                raise UpstreamUnavailable("upstream work iq server closed its input") from exc

    def notify(self, method, params):
        self._write({"jsonrpc": "2.0", "method": method, "params": params})

    def call(self, method, params, timeout=None):
        """Send one request and return the raw response object (result or error) or raise."""
        with self.lock:
            self.counter += 1
            request_id = self.counter
            waiter = {"event": threading.Event(), "response": None}
            self.pending[request_id] = waiter
        try:
            self._write({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params})
        except UpstreamUnavailable:
            with self.lock:
                self.pending.pop(request_id, None)
            raise
        if not waiter["event"].wait(self.timeout if timeout is None else timeout):
            with self.lock:
                self.pending.pop(request_id, None)
            raise UpstreamTimeout()
        if waiter["response"] is None:
            raise UpstreamUnavailable("upstream work iq server exited before answering")
        return request_id, waiter["response"]

    def result(self, method, params, timeout=None):
        _, response = self.call(method, params, timeout)
        if "error" in response:
            error = response["error"] if isinstance(response["error"], dict) else {}
            raise GateError("upstream error: " + str(error.get("message", "unknown")), UPSTREAM, error)
        return response.get("result")

    def tools(self, timeout=None):
        listed = self.result("tools/list", {}, timeout)
        tools = listed.get("tools") if isinstance(listed, dict) else None
        return [tool for tool in tools or [] if isinstance(tool, dict) and isinstance(tool.get("name"), str)]

    def stop(self):
        if self.process is None:
            return
        try:
            if self.process.stdin:
                self.process.stdin.close()
        except OSError:
            pass
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(3)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(3)


# ---------------------------------------------------------------------------
# The gate
# ---------------------------------------------------------------------------

class Gate:
    """Socket server, policy and state glue. One instance per process; sessions run on threads."""

    def __init__(self, deployment, account, state_root, socket_path, audit_log, workiq_command=None,
                 upstream_timeout=60.0, startup_timeout=120.0, allow_shutdown=False):
        self.deployment = deployment
        # Copilot sessions run as the gate's uid, so a socket shutdown is opt-in (tests);
        # production stops the service with SIGTERM.
        self.allow_shutdown = bool(allow_shutdown)
        self.account = margo_store.resolve_account(account)
        self.state_root = state_root
        self.socket_path = str(socket_path)
        self.audit_path = Path(audit_log)
        self.upstream_timeout = float(upstream_timeout)
        self.startup_timeout = float(startup_timeout)
        self.command = list(workiq_command) if workiq_command else list(deployment["workiq"]["command"])
        self.gate_uid = os.getuid() if hasattr(os, "getuid") else None
        self.cli_uids = {}
        self.unknown_logins = []
        self.sessions = {}
        self.sessions_lock = threading.Lock()
        self.state_lock = threading.Lock()
        self.audit_lock = threading.Lock()
        self.stopping = threading.Event()
        self.listener = None
        self.upstream = None
        self.started_at = utc_now()

    # -- lifecycle -------------------------------------------------------------

    def start(self):
        if not sys.platform.startswith("linux") or not hasattr(socket, "SO_PEERCRED"):
            raise StateError("the gate needs linux peer credentials (SO_PEERCRED); refusing to serve without them")
        resolved = _directives().cli_uids(self.deployment)
        self.cli_uids, self.unknown_logins = dict(resolved["uids"]), list(resolved["unknown"])
        self._prepare_audit()
        self._initialize_state()
        env = dict(os.environ)
        env.update(self.deployment["workiq"]["env"])
        self.upstream = Upstream(self.command, env, self._fan_out_notification, self.upstream_timeout)
        try:
            self.upstream.start(self.startup_timeout)
        except GateError:
            self.upstream.stop()
            raise
        self._bind()
        self.audit(rpc="serve", decision="started", upstream_tools=len(self.upstream.tool_names),
                   cli_logins_resolved=len(self.cli_uids), cli_logins_unknown=len(self.unknown_logins))

    def _prepare_audit(self):
        parent = self.audit_path.parent
        if not parent.is_dir():
            raise StateError("audit log directory does not exist: " + str(parent))
        descriptor = os.open(str(self.audit_path), os.O_CREAT | os.O_APPEND | os.O_WRONLY, 0o600)
        os.close(descriptor)

    def _initialize_state(self):
        """Explicit, one-time namespace initialization; every later access borrows an initialized store."""
        with self.state_lock:
            ledger = _ledger_module().Ledger(self.account, self.state_root)
            try:
                _directives().DirectiveStore.from_connection(ledger.conn, self.account)
            except margo_store.NotInitialized:
                store = _directives().DirectiveStore(self.account, self.state_root)
                store.close()
            finally:
                ledger.close()

    def _bind(self):
        path = Path(self.socket_path)
        if not path.parent.is_dir():
            raise StateError("socket directory does not exist: " + str(path.parent))
        if path.exists():
            probe = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            try:
                probe.settimeout(1.0)
                probe.connect(self.socket_path)
                raise StateError("another gate is already serving " + self.socket_path)
            except OSError:
                path.unlink()
            finally:
                probe.close()
        listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        listener.bind(self.socket_path)
        # Authorization is by peer uid inside the protocol; the socket itself admits local callers.
        os.chmod(self.socket_path, 0o666)
        listener.listen(16)
        listener.settimeout(0.5)
        self.listener = listener

    def serve_forever(self):
        try:
            while not self.stopping.is_set():
                try:
                    connection, _ = self.listener.accept()
                except socket.timeout:
                    continue
                except OSError:
                    break
                session = Session(self, connection)
                threading.Thread(target=session.run, name="gate-session-" + session.id, daemon=True).start()
        finally:
            self.shutdown()

    def shutdown(self):
        self.stopping.set()
        if self.listener is not None:
            try:
                self.listener.close()
            except OSError:
                pass
            self.listener = None
            try:
                Path(self.socket_path).unlink()
            except OSError:
                pass
        with self.sessions_lock:
            sessions = list(self.sessions.values())
        for session in sessions:
            session.close()
        if self.upstream is not None:
            self.upstream.stop()

    # -- shared helpers ----------------------------------------------------------

    def audit(self, **entry):
        """Append one decision as a JSON line; digests and identifiers only, never bodies."""
        entry = dict(entry, at=utc_now())
        line = (canonical_json(entry) + "\n").encode("utf-8")
        with self.audit_lock:
            descriptor = os.open(str(self.audit_path), os.O_CREAT | os.O_APPEND | os.O_WRONLY, 0o600)
            try:
                os.write(descriptor, line)
            finally:
                os.close(descriptor)

    def with_state(self, operation):
        """Run ``operation(ledger, directives)`` on freshly borrowed stores; never across an upstream call."""
        with self.state_lock:
            ledger = _ledger_module().Ledger(self.account, self.state_root)
            try:
                store = _directives().DirectiveStore.from_connection(ledger.conn, self.account)
                return operation(ledger, store)
            finally:
                ledger.close()

    def fetch_argument(self, path):
        return {self.deployment["workiq"]["argument_keys"]["path"][0]: path}

    def graph_fetch(self, path, missing_ok=False, timeout=None):
        """Read one Graph path through upstream ``fetch``; returns the document or raises StateError."""
        if self.upstream is None or not self.upstream.alive():
            raise UpstreamUnavailable()
        _, response = self.upstream.call("tools/call", {"name": "fetch", "arguments": self.fetch_argument(path)}, timeout)
        if "error" in response:
            raise GateError("fetch was rejected upstream", UPSTREAM)
        result = response.get("result")
        if isinstance(result, dict) and result.get("isError") and missing_ok and http_status(result) == 404:
            return None
        return tool_result_json(result)

    def probe(self, path):
        """Health read: ok, denied, auth, error or unavailable with a short detail, plus the document."""
        try:
            return {"status": "ok", "detail": None, "data": self.graph_fetch(path)}
        except UpstreamUnavailable as exc:
            return {"status": "unavailable", "detail": str(exc), "data": None}
        except StateError as exc:
            detail = str(exc)[:300]
            lowered = detail.casefold()
            status = re.search(r"\b(40[13])\b", lowered)
            if status and status.group(1) == "401" or any(marker in lowered for marker in AUTH_MARKERS):
                return {"status": "auth", "detail": detail, "data": None}
            if status and status.group(1) == "403":
                return {"status": "denied", "detail": detail, "data": None}
            return {"status": "error", "detail": detail, "data": None}

    def _fan_out_notification(self, message):
        with self.sessions_lock:
            sessions = list(self.sessions.values())
        for session in sessions:
            session.send(message)

    def register(self, session):
        with self.sessions_lock:
            self.sessions[session.id] = session

    def unregister(self, session):
        with self.sessions_lock:
            self.sessions.pop(session.id, None)

    # -- tools/list and tools/call ------------------------------------------------

    def filtered_tools(self):
        workiq = self.deployment["workiq"]
        allowed = set(workiq["read_tools"]) | set(workiq["write_tools"])
        tools = []
        for tool in self.upstream.tools():
            if tool["name"] not in allowed:
                continue
            tool = dict(tool)
            if tool["name"] in workiq["write_tools"]:
                tool["description"] = WRITE_PREFIX + " " + str(tool.get("description") or "")
            tools.append(tool)
        return {"tools": tools}

    def tools_call(self, session, params):
        name, arguments = params.get("name"), params.get("arguments", {})
        if not isinstance(name, str) or not name.strip():
            raise GateError("tools/call requires a tool name", INVALID_PARAMS)
        if arguments is None:
            arguments = {}
        if not isinstance(arguments, dict):
            raise GateError("tools/call arguments must be an object", INVALID_PARAMS)
        decision = classify(name, arguments, self.deployment)
        clean = {key: value for key, value in arguments.items() if key not in GRANT_KEYS}
        entry = {"session": session.id, "roles": session.roles, "peer_uid": session.peer_uid, "rpc": "tools/call",
                 "tool": name, "tier": decision["tier"], "reason": decision["reason"], "path": decision["path"],
                 "http_method": decision["method"], "scope": decision["scope"], "arguments_digest": digest(clean)}
        try:
            if decision["tier"] == "T0":
                return self._forward_call(name, arguments, entry)
            if decision["tier"] == "T1":
                return self._execute_t1(name, arguments, clean, decision, entry)
            return self._execute_approved(name, arguments, decision, entry)
        except UpstreamUnavailable as exc:
            self.audit(decision="error", error=str(exc), **entry)
            raise
        except GateError as exc:
            if exc.code != POLICY:
                self.audit(decision="error", error=str(exc), **entry)
                raise
            self.audit(decision="refused", error=str(exc), **entry)
            return refusal(str(exc))

    def _forward_call(self, name, arguments, entry):
        request_id, response = self.upstream.call("tools/call", {"name": name, "arguments": arguments})
        if "error" in response:
            self.audit(decision="forwarded", upstream_id=request_id, outcome="upstream_error", **entry)
            error = response["error"] if isinstance(response["error"], dict) else {}
            raise GateError(str(error.get("message", "upstream error")), UPSTREAM, error.get("data"))
        result = response.get("result")
        self.audit(decision="forwarded", upstream_id=request_id, result_digest=digest(result),
                   outcome=outcome_of(result), **entry)
        return result

    def _execute_t1(self, name, arguments, clean, decision, entry):
        directive_id, rule_id = arguments.get("margo_directive"), arguments.get("margo_rule")
        if (directive_id is None) == (rule_id is None):
            raise GateError("private write (T1) needs exactly one of margo_directive (an active manager directive) "
                            "or margo_rule (an active standing rule)")
        grant = directive_id if directive_id is not None else rule_id
        if not isinstance(grant, str) or not grant.strip():
            raise GateError("margo_directive/margo_rule must be an id string")
        entry.update(directive_id=directive_id, rule_id=rule_id)

        def check(ledger, store):
            if store.paused():
                raise GateError("unattended writes are paused by the manager; resume before T1 writes")
            try:
                if directive_id is not None:
                    return store.active(directive_id)
                return store.active_rule(rule_id)
            except GateError:
                raise
            except StateError as exc:
                raise GateError("grant not usable: " + str(exc)) from exc

        self.with_state(check)
        read_path = preread_path(decision["path"], decision["method"])
        try:
            before = self.graph_fetch(read_path)
        except StateError as exc:  # includes an upstream timeout: without a prior state there is no undo
            raise GateError("pre-read of %s failed; refusing the write (%s)" % (read_path, exc)) from exc
        request_id = None
        try:
            request_id, response = self.upstream.call("tools/call", {"name": name, "arguments": clean})
            if "error" in response:
                error = response["error"] if isinstance(response["error"], dict) else {}
                result = refusal("upstream rejected the call; outcome unknown (%s)" % str(error.get("message", "unknown")))
            else:
                result = response.get("result")
        except UpstreamUnavailable as exc:
            result = refusal("%s; the write may have happened, do not retry" % exc)
        # The effect is journaled whatever the answer was, so an uncertain write is never invisible.
        effect = self.with_state(lambda ledger, store: store.record_effect(
            directive_id=directive_id, rule_id=rule_id, tool=name, arguments=clean, before=before, after=result))
        self.audit(decision="forwarded", upstream_id=request_id, effect_id=effect.get("id"),
                   result_digest=digest(result), outcome=outcome_of(result), **entry)
        return result

    def _execute_approved(self, name, arguments, decision, entry):
        grant = arguments.get("margo_action")
        tier = decision["tier"]
        if not isinstance(grant, dict) or set(grant) != {"action_id", "revision", "action_hash"}:
            raise GateError("approved write (%s) needs margo_action {action_id, revision, action_hash} from an "
                            "approved ledger action (%s)" % (tier, decision["reason"]))
        action_id, revision, action_hash = grant["action_id"], grant["revision"], grant["action_hash"]
        if (not isinstance(action_id, str) or type(revision) is not int or not isinstance(action_hash, str)):
            raise GateError("margo_action needs a string action_id, integer revision and string action_hash")
        clean = {key: value for key, value in arguments.items() if key != "margo_action"}
        payload = json.loads(canonical_json({"tool": name, "arguments": clean}))
        entry.update(action_id=action_id, revision=revision)

        def load(ledger, store):
            try:
                action = ledger.show(action_id)
            except StateError as exc:
                raise GateError("approval check failed: " + str(exc)) from exc
            if action.get("type") != "action":
                raise GateError("margo_action does not name a ledger action")
            if action["revision"] != revision or action["action_hash"] != action_hash:
                raise GateError("margo_action names a stale revision or hash; reload the current action")
            if action["payload"] != payload:
                raise GateError("tool call differs from the approved payload; propose a new revision")
            target_path = action.get("target", {}).get("path") if isinstance(action.get("target"), dict) else None
            if not isinstance(target_path, str) or target_path.casefold() != decision["path"]:
                raise GateError("tool call path differs from the approved target path")
            if action["state"] != "approved":
                raise GateError("action is %s, not approved; it needs an unused exact manager approval" % action["state"])
            return action

        action = self.with_state(load)
        try:
            fresh_fingerprint = fingerprint(action["target"], lambda path: self.graph_fetch(path, missing_ok=True))
        except StateError as exc:
            raise GateError(str(exc)) from exc
        fresh = {"checked_at": utc_now(), "target_fingerprint": fresh_fingerprint, "source_refs": action["source_refs"]}

        def claim(ledger, store):
            try:
                return ledger.begin(action_id, revision, fresh)
            except StateError as exc:
                raise GateError("execution claim refused: " + str(exc)) from exc

        attempt = self.with_state(claim)
        entry.update(attempt_id=attempt["attempt_id"])
        request_id = None
        try:
            request_id, response = self.upstream.call("tools/call", {"name": name, "arguments": clean})
            if "error" in response:
                error = response["error"] if isinstance(response["error"], dict) else {}
                result = refusal("upstream rejected the call after the claim; outcome unknown (%s)"
                                 % str(error.get("message", "unknown")))
                outcome = "outcome_unknown"
            else:
                result = response.get("result")
                outcome = outcome_of(result)
        except UpstreamUnavailable as exc:
            result = refusal("%s; recorded as outcome_unknown for %s, do not retry" % (exc, attempt["attempt_id"]))
            outcome = "outcome_unknown"
        receipt = receipt_for(name, request_id if request_id is not None else "none", result, outcome, utc_now())
        try:
            self.with_state(lambda ledger, store: ledger.finish(attempt["attempt_id"], outcome, receipt))
            settled = True
        except StateError as exc:
            settled = False
            self.audit(decision="error", error="receipt not recorded: " + str(exc), outcome=outcome, **entry)
        self.audit(decision="forwarded", upstream_id=request_id, result_digest=digest(result), outcome=outcome,
                   receipt_recorded=settled, **entry)
        return result

    # -- control methods ------------------------------------------------------------

    def control(self, session, method, params):
        handlers = {
            "margo/health": (None, self.health),
            "margo/preflight": (None, self.preflight),
            "margo/pending": (None, self.pending),
            "margo/directives/sync": ({"service", "manager"}, self.directives_sync),
            "margo/directives/list": (None, self.directives_list),
            "margo/directives/complete": ({"service"}, self.directives_complete),
            "margo/approve": ({"manager"}, self.approve),
            "margo/reject": ({"manager"}, self.reject),
            "margo/rules/list": (None, self.rules_list),
            "margo/rules/revoke": ({"manager"}, self.rules_revoke),
            "margo/pause": ({"manager"}, self.pause),
            "margo/resume": ({"manager"}, self.resume),
            "margo/notify": ({"service", "manager"}, self.notify),
            "margo/shutdown": ({"service"}, self.shutdown_request),
        }
        if method not in handlers:
            raise GateError("unknown control method " + method, METHOD_NOT_FOUND)
        required, handler = handlers[method]
        if required is not None and not (required & set(session.roles)):
            self.audit(session=session.id, roles=session.roles, peer_uid=session.peer_uid, rpc=method, decision="denied")
            raise GateError("this method needs the %s role; the caller holds %s"
                            % ("/".join(sorted(required)), "+".join(session.roles) or "none"), DENIED)
        result = handler(session, params)
        self.audit(session=session.id, roles=session.roles, peer_uid=session.peer_uid, rpc=method, decision="ok")
        return result

    def health(self, session, params):
        upstream_alive = self.upstream is not None and self.upstream.alive()
        checks = {}
        margo = self.deployment["margo"]
        manager = self.deployment["manager"]["principal"]
        if upstream_alive:
            identity = self.probe("/me?$select=id,userPrincipalName")
            data = identity.pop("data", None)
            if identity["status"] == "ok":
                data = data if isinstance(data, dict) else {}
                matches = (str(data.get("id", "")).casefold() == margo["object_id"].casefold()
                           or str(data.get("userPrincipalName", "")).casefold() == margo["principal"].casefold())
                identity = {"status": "ok" if matches else "mismatch", "detail": None if matches else
                            "work iq is signed in as someone other than the configured margo principal"}
            checks["workiq_identity"] = identity
            start = now().isoformat(timespec="seconds").replace("+00:00", "Z")
            end = (now() + timedelta(days=1)).isoformat(timespec="seconds").replace("+00:00", "Z")
            surfaces = {
                "mail": self.probe("/users/%s/mailFolders/inbox?$select=id" % manager),
                "calendar": self.probe("/users/%s/calendarView?startDateTime=%s&endDateTime=%s&$top=1" % (manager, start, end)),
            }
            statuses = [value["status"] for value in surfaces.values()]
            overall = "ok" if all(status == "ok" for status in statuses) else next(
                (candidate for candidate in ("auth", "unavailable", "denied", "error") if candidate in statuses), "error")
            checks["delegated_access"] = {"status": overall,
                                          "surfaces": {key: {"status": value["status"], "detail": value["detail"]}
                                                       for key, value in surfaces.items()}}
        else:
            checks["workiq_identity"] = {"status": "unavailable", "detail": "upstream work iq server is not running"}
            checks["delegated_access"] = {"status": "unavailable", "surfaces": {}}

        def state(ledger, store):
            pending = len(ledger.list("approval")["actions"])
            return ({"status": "ok", "pending_approvals": pending},
                    {"status": "ok", "active": len(store.list(state="active", limit=500)),
                     "rules": len(store.rules()), "paused": store.paused()}, pending)

        try:
            checks["ledger"], checks["directives"], pending = self.with_state(state)
        except StateError as exc:
            checks["ledger"] = checks["directives"] = {"status": "error", "detail": str(exc)[:300]}
            pending = 0
        paused = bool(checks["directives"].get("paused", False))
        probes = [checks["workiq_identity"]["status"], checks["delegated_access"]["status"]]
        if not upstream_alive or checks["ledger"]["status"] != "ok" or checks["directives"]["status"] != "ok":
            status = "blocked"
        elif "auth" in probes:
            status = "reauth_required"
        elif checks["workiq_identity"]["status"] != "ok":
            status = "blocked"
        elif checks["delegated_access"]["status"] != "ok":
            status = "degraded"
        else:
            status = "connected"
        return {"status": status, "paused": paused, "checks": checks,
                "upstream": {"alive": upstream_alive, "tools": len(self.upstream.tool_names) if self.upstream else 0},
                "pending_approvals": pending, "cli_logins_unknown": list(self.unknown_logins),
                "started_at": self.started_at, "checked_at": utc_now()}

    def preflight(self, session, params):
        target = params.get("target")
        if not isinstance(target, dict) or not target:
            raise GateError("preflight requires a target object", INVALID_PARAMS)
        try:
            value = fingerprint(target, lambda path: self.graph_fetch(path, missing_ok=True))
        except StateError as exc:
            raise GateError(str(exc)) from exc
        return {"target_fingerprint": value, "checked_at": utc_now()}

    @staticmethod
    def _pending_actions(ledger):
        approval_ref = _directives().approval_ref
        actions = []
        for action in ledger.list("approval")["actions"]:
            current = [a["expires_at"] for a in action["approvals"]
                       if a["revision"] == action["revision"] and not a["invalidated_at"]]
            actions.append({"ref": approval_ref(action["action_hash"]), "action_id": action["id"],
                            "revision": action["revision"], "kind": action["kind"], "target": action["target"],
                            "why": action["why"], "state": action["state"], "expires_at": max(current) if current else None,
                            "action_hash": action["action_hash"]})
        return actions

    def pending(self, session, params):
        return {"actions": self.with_state(lambda ledger, store: self._pending_actions(ledger))}

    def directives_list(self, session, params):
        state = params.get("state")
        limit = params.get("limit", 50)
        return {"directives": self.with_state(lambda ledger, store: store.list(state=state, limit=limit))}

    def directives_complete(self, session, params):
        directive_id, summary = params.get("id"), params.get("summary")
        if not isinstance(directive_id, str) or not isinstance(summary, str):
            raise GateError("complete requires id and summary strings", INVALID_PARAMS)
        return self.with_state(lambda ledger, store: store.complete(directive_id, summary))

    # -- directive application (shared by sync and the CLI-role methods) ------------

    def _apply_verified(self, ledger, store, verified, report):
        """Record one verified message and apply approvals/rejections; everything inside one transaction."""
        md = _directives()
        with margo_store.transaction(ledger.conn):
            directive = store.record(verified)
            kind = directive["kind"]
            if kind not in {"approval", "rejection"}:
                if directive.get("created"):
                    report["new"].append(directive)
                return directive
            if directive["state"] != "active":
                return directive  # already applied by an earlier sync
            matches = [a for a in self._pending_actions(ledger) if a["ref"] == directive["ref"]]
            summary, outcome = None, None
            if len(matches) != 1:
                summary = ("no pending action matches " if not matches else "more than one pending action matches ") + str(directive["ref"])
            else:
                match = matches[0]
                try:
                    action = ledger.show(match["action_id"])
                    if kind == "approval":
                        # The manager decided when the message was sent; a Graph clock ahead of
                        # this host falls back to the verification time rather than a future stamp.
                        decided_at = directive["received_at"]
                        if datetime.fromisoformat(decided_at.replace("Z", "+00:00")) > now():
                            decided_at = utc_now()
                        evidence = md.build_approval_evidence(directive, action, decided_at)
                        ttl = self.deployment["control"]["approval_ttl_hours"]
                        expires_at = (now() + timedelta(hours=ttl)).isoformat(timespec="microseconds")
                        outcome = ledger.approve(action["id"], action["revision"], action["action_hash"], evidence, expires_at)
                        summary = "approved action %s revision %s" % (action["id"], action["revision"])
                    else:
                        outcome = ledger.disposition(action["id"], action["revision"], "dismissed")
                        summary = "dismissed action %s revision %s" % (action["id"], action["revision"])
                except StateError as exc:
                    summary, outcome = "%s failed: %s" % (kind, exc), None
            if outcome is None:
                store.complete(directive["id"], summary, state="rejected")
                report["errors"].append({"channel": directive["channel"], "source_ref": directive["source_ref"],
                                         "directive_id": directive["id"], "reason": summary})
            else:
                store.complete(directive["id"], summary)
                report["approvals" if kind == "approval" else "rejected"].append(
                    {"directive_id": directive["id"], "ref": directive["ref"], "action_id": outcome["id"],
                     "revision": outcome["revision"], "state": outcome["state"], "channel": directive["channel"]})
            return store.show(directive["id"])

    def _collect_teams(self, known, report):
        md = _directives()
        control = self.deployment["control"]
        chat_id = control["teams_chat_id"]
        verified = []
        if not control["teams_enabled"] or not chat_id:
            report["sources"]["teams"] = "disabled"
            return verified
        try:
            chat = self.graph_fetch("/chats/%s?$expand=members" % chat_id)
            listed = self.graph_fetch("/chats/%s/messages?$top=50&$orderby=createdDateTime desc" % chat_id)
        except StateError as exc:
            report["errors"].append({"channel": "teams", "reason": "teams fetch failed: " + str(exc)[:200]})
            report["sources"]["teams"] = "error"
            return verified
        manager = self.deployment["manager"]["object_id"].casefold()
        messages = listed.get("value") if isinstance(listed, dict) else None
        for message in messages if isinstance(messages, list) else []:
            if not isinstance(message, dict) or not isinstance(message.get("id"), str):
                continue
            if "teams:" + message["id"].strip() in known:
                continue
            sender = message.get("from") if isinstance(message.get("from"), dict) else {}
            user = sender.get("user") if isinstance(sender.get("user"), dict) else {}
            if message.get("messageType") != "message" or str(user.get("id", "")).casefold() != manager:
                report["ignored"] += 1  # Margo's own replies, system events and anyone else are data
                continue
            try:
                verified.append(md.verify_teams(message, chat, self.deployment))
            except StateError as exc:
                report["errors"].append({"channel": "teams", "source_ref": "teams:" + message["id"], "reason": str(exc)})
        report["sources"]["teams"] = "ok"
        return verified

    def _sent_items_lookup(self, manager):
        def lookup(message_id):
            escaped = message_id.replace("'", "''")
            path = ("/users/%s/mailFolders/sentitems/messages?$filter=internetMessageId eq '%s'&$select=id,internetMessageId"
                    % (manager, escaped))
            try:
                return self.graph_fetch(path)
            except UpstreamTimeout as exc:
                raise TimeoutError(str(exc)) from exc
        return lookup

    def _collect_email(self, known, report):
        md = _directives()
        control = self.deployment["control"]
        verified = []
        if not control["email_enabled"]:
            report["sources"]["email"] = "disabled"
            return verified
        try:
            listed = self.graph_fetch("/me/messages?$top=50&$orderby=receivedDateTime desc&$select=id,internetMessageHeaders,"
                                      "internetMessageId,from,sender,toRecipients,body,subject,receivedDateTime")
        except StateError as exc:
            report["errors"].append({"channel": "email", "reason": "inbox fetch failed: " + str(exc)[:200]})
            report["sources"]["email"] = "error"
            return verified
        manager = self.deployment["manager"]["principal"].casefold()
        lookup = self._sent_items_lookup(self.deployment["manager"]["principal"])
        messages = listed.get("value") if isinstance(listed, dict) else None
        for message in messages if isinstance(messages, list) else []:
            if not isinstance(message, dict):
                continue
            message_id = message.get("internetMessageId")
            if not isinstance(message_id, str) or not message_id.strip() or "email:" + message_id.strip() in known:
                continue
            sender = message.get("from") if isinstance(message.get("from"), dict) else {}
            address = sender.get("emailAddress", {}).get("address") if isinstance(sender.get("emailAddress"), dict) else None
            if not isinstance(address, str) or address.strip().casefold() != manager:
                report["ignored"] += 1  # mail from anyone else is observed data, never an instruction
                continue
            try:
                verified.append(md.verify_email(message, lookup, self.deployment))
            except StateError as exc:
                report["errors"].append({"channel": "email", "source_ref": "email:" + message_id.strip(), "reason": str(exc)})
        report["sources"]["email"] = "ok"
        return verified

    def directives_sync(self, session, params):
        """Pull the control channels, verify every candidate, then record and apply what verified."""
        report = {"new": [], "approvals": [], "rejected": [], "errors": [], "ignored": 0, "sources": {}}
        known = self.with_state(lambda ledger, store: {d["source_ref"] for d in store.list(limit=500)})
        # All network reads happen here, before any transaction is opened.
        verified = self._collect_teams(known, report) + self._collect_email(known, report)
        for record in verified:
            try:
                self.with_state(lambda ledger, store: self._apply_verified(ledger, store, record, report))
            except StateError as exc:
                report["errors"].append({"channel": record.get("channel"), "source_ref": record.get("source_ref"),
                                         "reason": "record failed: " + str(exc)})
        return report

    def _cli_record(self, session, instruction):
        """Verify the CLI caller by peer uid and shape the instruction as a verified record."""
        try:
            return _directives().verify_cli(session.peer_uid, self.deployment, instruction=instruction)
        except StateError as exc:
            raise GateError("cli verification failed: " + str(exc), DENIED) from exc

    def _decide(self, session, params, word):
        ref = params.get("ref")
        if not isinstance(ref, str) or not re.fullmatch(r"MA-[0-9A-Fa-f]{8}", ref.strip()):
            raise GateError("ref must look like MA-xxxxxxxx, exactly as shown by pending", INVALID_PARAMS)
        verified = self._cli_record(session, "%s %s" % (word, ref.strip()))
        report = {"new": [], "approvals": [], "rejected": [], "errors": []}
        directive = self.with_state(lambda ledger, store: self._apply_verified(ledger, store, verified, report))
        if report["errors"]:
            raise GateError(report["errors"][0]["reason"], POLICY, {"directive_id": directive["id"]})
        return {"directive": directive, "decision": (report["approvals"] or report["rejected"])[0]}

    def approve(self, session, params):
        return self._decide(session, params, "approve")

    def reject(self, session, params):
        return self._decide(session, params, "reject")

    def rules_list(self, session, params):
        state = params.get("state", "active")
        return {"rules": self.with_state(lambda ledger, store: store.rules(state=state))}

    def rules_revoke(self, session, params):
        rule_id = params.get("rule_id")
        if not isinstance(rule_id, str) or not re.fullmatch(r"rule_[0-9a-f]{32}", rule_id):
            raise GateError("rule_id must look like rule_<32 hex>", INVALID_PARAMS)
        verified = self._cli_record(session, "revoke rule " + rule_id)

        def apply(ledger, store):
            directive = store.record(verified)
            try:
                rule = store.show(rule_id)
            except StateError:
                rule = None
            return directive, rule

        directive, rule = self.with_state(apply)
        if directive["state"] == "rejected":
            raise GateError(directive.get("summary") or "revocation refused", POLICY, {"directive_id": directive["id"]})
        return {"directive": directive, "rule": rule}

    def _set_paused(self, session, word):
        verified = self._cli_record(session, word)

        def apply(ledger, store):
            directive = store.record(verified)
            return {"paused": store.paused(), "directive_id": directive["id"]}

        return self.with_state(apply)

    def pause(self, session, params):
        return self._set_paused(session, "pause")

    def resume(self, session, params):
        return self._set_paused(session, "resume")

    def notify(self, session, params):
        """Post to the manager on the control channel only, within the hourly reply budget."""
        text, kind = params.get("text"), params.get("kind", "status")
        if not isinstance(text, str) or not text.strip() or len(text) > MAX_NOTIFY_TEXT:
            raise GateError("notify requires text of at most %d characters" % MAX_NOTIFY_TEXT, INVALID_PARAMS)
        if not isinstance(kind, str) or not re.fullmatch(r"[a-z][a-z0-9_]{0,39}", kind):
            raise GateError("notify kind must be a short lowercase identifier", INVALID_PARAMS)
        control, workiq = self.deployment["control"], self.deployment["workiq"]
        manager = self.deployment["manager"]["principal"]
        if control["teams_enabled"] and control["teams_chat_id"]:
            channel, template_name = "teams", "teams_message"
        elif control["email_enabled"]:
            channel, template_name = "email", "email_message"
        else:
            raise GateError("no control channel is enabled for replies")
        template = workiq["call_templates"].get(template_name)
        if template is None:
            raise GateError("deployment has no %s call template" % template_name)
        rendered = render_template(template["arguments"], {
            "chat_id": control["teams_chat_id"] or "", "text": text, "address": manager,
            "subject": "Margo: " + kind.replace("_", " ")})
        path, _, body, _ = extract_call(rendered, self.deployment)
        # The recipient is structural: it comes from the deployment, and the rendered call is re-checked.
        if channel == "teams":
            if path is None or "/chats/%s/messages" % control["teams_chat_id"].casefold() not in path:
                raise GateError("teams template does not post to the configured control chat")
        else:
            recipients = []
            stack = [body]
            while stack:
                node = stack.pop()
                if isinstance(node, dict):
                    for key, value in node.items():
                        if isinstance(key, str) and key.casefold().endswith("recipients") and isinstance(value, list):
                            recipients.extend(value)
                        else:
                            stack.append(value)
                elif isinstance(node, list):
                    stack.extend(node)
            addresses = {str(_directives()._address(item)) for item in recipients}
            if path is None or not path.split("?", 1)[0].endswith("/sendmail") or path_scope(path, self.deployment) != "margo":
                raise GateError("email template does not send from margo's own mailbox")
            if not recipients or addresses != {manager.casefold()}:
                raise GateError("email template would reach someone other than the manager")
        limit = control["max_replies_per_hour"]

        def count(ledger, store):
            try:
                return store.record_reply(channel, text, limit=limit)
            except StateError as exc:
                raise GateError(str(exc)) from exc

        counted = self.with_state(count)
        entry = {"session": session.id, "roles": session.roles, "peer_uid": session.peer_uid, "rpc": "margo/notify",
                 "tool": template["tool"], "tier": "control", "channel": channel, "kind": kind,
                 "path": path, "arguments_digest": digest(rendered), "reply_id": counted.get("id")}
        try:
            request_id, response = self.upstream.call("tools/call", {"name": template["tool"], "arguments": rendered})
        except UpstreamUnavailable as exc:
            self.audit(decision="forwarded", outcome="outcome_unknown", error=str(exc), **entry)
            return {"channel": channel, "kind": kind, "outcome": "outcome_unknown", "detail": str(exc),
                    "replies_in_last_hour": counted.get("replies_in_last_hour")}
        if "error" in response:
            error = response["error"] if isinstance(response["error"], dict) else {}
            result, outcome = {"isError": True, "error": error}, "outcome_unknown"
        else:
            result = response.get("result")
            outcome = outcome_of(result)
        self.audit(decision="forwarded", upstream_id=request_id, outcome=outcome, result_digest=digest(result), **entry)
        return {"channel": channel, "kind": kind, "outcome": outcome, "result_digest": digest(result),
                "replies_in_last_hour": counted.get("replies_in_last_hour"),
                "detail": result_text(result)[:300] if outcome != "succeeded" else None}

    def shutdown_request(self, session, params):
        if not self.allow_shutdown:
            raise GateError("shutdown over the socket is disabled; stop the service with SIGTERM "
                            "(serve --allow-shutdown enables it for tests)", DENIED)
        if session.peer_uid != self.gate_uid:
            raise GateError("shutdown is only accepted from the gate's own uid", DENIED)
        self.stopping.set()
        return {"stopping": True}


class Session:
    """One client connection: peer credentials resolved once, requests served in order."""

    def __init__(self, gate, connection):
        self.gate = gate
        self.connection = connection
        self.id = uuid.uuid4().hex[:12]
        self.peer_uid = None
        self.roles = []
        self.write_lock = threading.Lock()
        self.closed = False
        self.initialized = False

    def run(self):
        try:
            credentials = self.connection.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize("3i"))
            _, self.peer_uid, _ = struct.unpack("3i", credentials)
        except OSError:
            self.peer_uid = None
        self.roles = resolve_roles(self.peer_uid, self.gate.cli_uids, self.gate.gate_uid)
        self.gate.register(self)
        self.gate.audit(session=self.id, roles=self.roles, peer_uid=self.peer_uid, rpc="connect",
                        decision="accepted" if self.roles else "denied")
        try:
            stream = self.connection.makefile("rb")
            while not self.gate.stopping.is_set():
                line = stream.readline(MAX_LINE + 1)
                if not line:
                    break
                if len(line) > MAX_LINE:
                    self.send_error(None, INVALID_REQUEST, "request line exceeds 16 MiB")
                    break
                if not line.strip():
                    continue
                self.handle_line(line)
        except (OSError, ValueError):
            pass
        finally:
            self.gate.unregister(self)
            self.close()

    def send(self, message):
        if self.closed:
            return
        data = (canonical_json(message) + "\n").encode("utf-8")
        with self.write_lock:
            try:
                self.connection.sendall(data)
            except OSError:
                self.closed = True

    def send_error(self, request_id, code, message, data=None):
        error = {"code": code, "message": message}
        if data is not None:
            error["data"] = data
        self.send({"jsonrpc": "2.0", "id": request_id, "error": error})

    def close(self):
        if not self.closed:
            self.closed = True
            try:
                self.connection.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            try:
                self.connection.close()
            except OSError:
                pass

    def handle_line(self, line):
        try:
            message = margo_store.parse_json(line.decode("utf-8"))
        except (StateError, UnicodeDecodeError):
            self.send_error(None, PARSE_ERROR, "parse error")
            return
        if not isinstance(message, dict) or not isinstance(message.get("method"), str):
            self.send_error(message.get("id") if isinstance(message, dict) else None, INVALID_REQUEST, "invalid request")
            return
        request_id = message.get("id")
        if not self.roles:
            if request_id is not None:
                self.send_error(request_id, DENIED, "peer uid %s is neither a configured manager login nor the gate's "
                                "service uid" % self.peer_uid)
            return
        params = message.get("params") if isinstance(message.get("params"), dict) else {}
        method = message["method"]
        if request_id is None:
            self.handle_notification(method, params)
            return
        try:
            result = self.dispatch(method, params)
        except GateError as exc:
            self.send_error(request_id, exc.code, str(exc), exc.data)
            return
        except StateError as exc:
            self.send_error(request_id, POLICY, str(exc))
            return
        except Exception as exc:  # never let one request kill the session silently
            self.gate.audit(session=self.id, rpc=method, decision="error", error=type(exc).__name__ + ": " + str(exc)[:300])
            self.send_error(request_id, -32000, "gate internal error: " + type(exc).__name__)
            return
        self.send({"jsonrpc": "2.0", "id": request_id, "result": result})

    def handle_notification(self, method, params):
        if method == "notifications/initialized":
            self.initialized = True
        # Cancellation carries a client request id that has no upstream equivalent once answered;
        # every other client notification stays local. Nothing is forwarded blindly.

    def dispatch(self, method, params):
        gate = self.gate
        if method == "initialize":
            upstream = gate.upstream.initialize_result if gate.upstream else {}
            return {"protocolVersion": upstream.get("protocolVersion", PROTOCOL_FALLBACK),
                    "capabilities": upstream.get("capabilities", {"tools": {}}),
                    "serverInfo": {"name": SERVER_NAME, "version": SERVER_VERSION},
                    "instructions": "Work IQ through Margo's action gate. Reads pass; T1 writes carry margo_directive "
                                    "or margo_rule; T2/T3 writes carry margo_action from an approved ledger action."}
        if method == "ping":
            return {}
        if method.startswith("margo/"):
            return gate.control(self, method, params)
        if gate.upstream is None or not gate.upstream.alive():
            raise UpstreamUnavailable()
        if method == "tools/list":
            return gate.filtered_tools()
        if method == "tools/call":
            return gate.tools_call(self, params)
        if method in FORWARDED_METHODS:
            return gate.upstream.result(method, params)
        raise GateError("method not available through the gate: " + method, METHOD_NOT_FOUND)


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def _load(deployment_path):
    return _directives().load_deployment(deployment_path)


def _account(explicit, deployment):
    return explicit or os.environ.get("MARGO_ACCOUNT") or deployment["margo"]["principal"]


def check_config(deployment_path=None):
    """Describe whether this host can serve; counts and flags only, never identifiers."""
    deployment = _load(deployment_path)
    md = _directives()
    resolved = md.cli_uids(deployment)
    socket_path = Path(os.environ.get("MARGO_GATE_SOCKET") or deployment["gate"]["socket"])
    audit_path = Path(deployment["gate"]["audit_log"])
    command = deployment["workiq"]["command"]
    problems = []
    peer = sys.platform.startswith("linux") and hasattr(socket, "SO_PEERCRED")
    if not peer:
        problems.append("peer credentials (SO_PEERCRED) are linux-only; the gate refuses to serve here")
    if not resolved["uids"]:
        problems.append("no control.cli_logins entry resolves to a local user; manager CLI methods would be denied")
    if resolved["unknown"]:
        problems.append("unresolved cli_logins: " + ", ".join(resolved["unknown"]))
    for label, path in (("socket", socket_path), ("audit_log", audit_path)):
        if not path.parent.is_dir():
            problems.append("%s directory does not exist: %s" % (label, path.parent))
        elif not os.access(str(path.parent), os.W_OK):
            problems.append("%s directory is not writable: %s" % (label, path.parent))
    executable = shutil.which(command[0]) if command else None
    if executable is None:
        problems.append("work iq command is not on PATH: " + (command[0] if command else "<empty>"))
    control = deployment["control"]
    if not control["teams_enabled"] and not control["email_enabled"]:
        problems.append("no control channel is enabled; the manager could not instruct or approve remotely")
    if control["teams_enabled"] and not control["teams_chat_id"]:
        problems.append("teams is enabled without control.teams_chat_id; replies would fall back to email")
    for name in ("teams_message", "email_message"):
        if name not in deployment["workiq"]["call_templates"]:
            problems.append("missing call template " + name)
    try:
        margo_store.resolve_account(_account(None, deployment))
        account_ok = True
    except StateError as exc:
        account_ok = False
        problems.append("account cannot be resolved: " + str(exc))
    return {"status": "ok" if not problems else "problems", "profile": deployment["profile"],
            "manager_configured": True, "account_resolved": account_ok,
            "cli_logins": {"configured": len(control["cli_logins"]), "resolved": len(resolved["uids"]),
                           "unknown": list(resolved["unknown"])},
            "socket": str(socket_path), "audit_log": str(audit_path), "workiq_command_found": executable is not None,
            "peer_credentials": "linux" if peer else "unsupported",
            "teams_enabled": control["teams_enabled"], "teams_chat_configured": bool(control["teams_chat_id"]),
            "email_enabled": control["email_enabled"], "max_replies_per_hour": control["max_replies_per_hour"],
            "problems": problems}


def parser():
    root = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = root.add_subparsers(dest="command", required=True)
    serve = commands.add_parser("serve", help="run the gate until SIGTERM; one work iq child, one unix socket")
    serve.add_argument("--deployment", help="deployment anchor; otherwise MARGO_DEPLOYMENT or /etc/margo/deployment.json")
    serve.add_argument("--socket", help="unix socket path; otherwise MARGO_GATE_SOCKET or the deployment's gate.socket")
    serve.add_argument("--account", help="ledger owner principal; otherwise MARGO_ACCOUNT or the deployment's margo principal")
    serve.add_argument("--state-dir", help="private state base directory; account hash is appended")
    serve.add_argument("--workiq-command", help="JSON list overriding workiq.command (tests and pinned installs)")
    serve.add_argument("--audit-log", help="audit JSON-lines path; otherwise the deployment's gate.audit_log")
    serve.add_argument("--upstream-timeout", type=float, default=60.0, help="seconds to wait for one work iq answer")
    serve.add_argument("--startup-timeout", type=float, default=120.0, help="seconds to wait for work iq initialize")
    serve.add_argument("--allow-shutdown", action="store_true",
                       help="honour margo/shutdown from the gate's own uid (tests); otherwise only SIGTERM stops the gate")
    classify_command = commands.add_parser("classify", help="print the tier for one tool call without side effects")
    classify_command.add_argument("--tool", required=True)
    classify_command.add_argument("--arguments", required=True, help="JSON object exactly as the model would send it")
    classify_command.add_argument("--deployment")
    check = commands.add_parser("check-config", help="validate the deployment anchor and this host's readiness")
    check.add_argument("--deployment")
    return root


def run_serve(args):
    deployment = _load(args.deployment)
    command = None
    if args.workiq_command:
        command = margo_store.parse_json(args.workiq_command)
        if not isinstance(command, list) or not command or not all(isinstance(part, str) and part for part in command):
            raise StateError("--workiq-command must be a JSON list of nonempty strings")
    gate = Gate(deployment, _account(args.account, deployment), args.state_dir,
                args.socket or os.environ.get("MARGO_GATE_SOCKET") or deployment["gate"]["socket"],
                args.audit_log or deployment["gate"]["audit_log"], workiq_command=command,
                upstream_timeout=args.upstream_timeout, startup_timeout=args.startup_timeout,
                allow_shutdown=args.allow_shutdown)

    def stop(signum, frame):
        gate.stopping.set()

    for name in ("SIGTERM", "SIGINT"):
        if hasattr(signal, name):
            signal.signal(getattr(signal, name), stop)
    try:
        gate.start()
    except UpstreamUnavailable as exc:
        print(canonical_json({"error": str(exc), "command": "serve", "code": "upstream_unavailable"}), file=sys.stderr)
        return 3
    print(canonical_json({"status": "serving", "socket": gate.socket_path, "upstream_tools": len(gate.upstream.tool_names),
                          "cli_logins_resolved": len(gate.cli_uids), "cli_logins_unknown": gate.unknown_logins}), flush=True)
    gate.serve_forever()
    return 0


def main(argv=None):
    args = parser().parse_args(argv)
    try:
        if args.command == "serve":
            return run_serve(args)
        if args.command == "classify":
            deployment = _load(args.deployment)
            arguments = margo_store.parse_json(args.arguments)
            if not isinstance(arguments, dict):
                raise StateError("--arguments must be a JSON object")
            print(canonical_json(classify(args.tool, arguments, deployment)))
            return 0
        report = check_config(args.deployment)
        print(canonical_json(report))
        return 0 if report["status"] == "ok" else 1
    except (StateError, OSError, ValueError, TypeError, KeyError) as exc:
        print(canonical_json({"error": str(exc), "command": args.command}), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
