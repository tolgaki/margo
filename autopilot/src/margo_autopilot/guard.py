"""The Work IQ guard: the only path from a hosted Margo's model to Microsoft 365.

Why it exists. In an autopilot, Work IQ's delegated permission is scoped to the token's
subject. Margo holds two tokens: her own agent-user token and an on-behalf-of token for her
principal. ``/me`` means whichever subject signed the call. A brief that silently used
Margo's token would summarise her empty inbox and report a quiet day: plausible, success-shaped
and wrong. Prompt instructions reduce that risk; this guard removes it.

What it enforces (fails closed):

0. One endpoint: ``https://workiq.svc.cloud.microsoft/mcp``. No other MCP server.
1. The model declares ``on_data_of`` (``principal`` or ``self``); the guard selects the token.
   The model never sees or chooses a token.
2. Path policy: relative Graph paths only, no traversal or encoded separators, allowed roots
   per intent, and ``/users/{id}`` limited to directory reads (profile, manager, reports).
3. Writes need a live execution claim from the approval journal whose stored Work IQ call
   equals this call exactly. Unattended runs get no write path, except Margo posting to the
   fixed principal chat recorded at hire. Third-party runs get no write path at all: a
   stranger's message must not be able to steer what reaches the principal.
4. Requests triggered by someone other than the principal never read as the principal, and
   as Margo may only open the triggering message. Claims are consumed durably in the ledger
   before dispatch, so no attempt is ever sent twice.
5. A missing token is ``blocked``. The guard never falls back to the other subject.
6. Every attempt leaves a receipt: subject, tool, path (no query text), status and outcome.
   Tokens, bodies and results are never recorded.
"""

import json
from datetime import datetime, timezone

WORKIQ_MCP_URL = "https://workiq.svc.cloud.microsoft/mcp"
WORKIQ_RESOURCE = "api://workiq.svc.cloud.microsoft"

PRINCIPAL = "principal"
SELF = "self"
MARGO = "margo"
INTENTS = {PRINCIPAL, SELF}

ATTENDED = "attended"        # the authenticated principal is in the conversation
UNATTENDED = "unattended"    # schedule or the principal's own source notifications
THIRD_PARTY = "third_party"  # triggered by anyone else's message to Margo
MODES = {ATTENDED, UNATTENDED, THIRD_PARTY}

OBO = "obo"                # principal intent uses the principal's on-behalf-of token
DELEGATION = "delegation"  # fallback: Margo's token on mailbox/calendar the principal delegated
PRINCIPAL_ACCESS = {OBO, DELEGATION}
# Exchange delegation covers mail and calendar only. Teams, files and Copilot search do not
# follow it, so under delegation those principal sources are reported unavailable, not empty.
DELEGABLE = {"messages", "mailfolders", "calendar", "calendars", "calendarview", "events"}
PATH_KEYS = ("entityUrls", "path", "functionUrl", "parentUrl", "entityUrl", "actionUrl")

TOOL_ARGS = {
    "fetch": {"entityUrls", "agentId"},
    "fetch_blob": {"path", "format", "agentId"},
    "call_function": {"functionUrl", "parentUrl", "agentId"},
    "create_entity": {"parentUrl", "jsonBody", "agentId"},
    "update_entity": {"entityUrl", "jsonBody", "agentId"},
    "delete_entity": {"entityUrl", "agentId"},
    "do_action": {"actionUrl", "jsonBody", "agentId"},
    "ask": {"question", "agentId", "fileUrls", "conversationId", "timeZone"},
    "retrieve": {"query", "agentId", "strategy", "capabilities", "includeDeveloperCard"},
    "list_agents": set(),
    "get_schema": {"operationIds", "path", "operationType", "format", "backend", "agentId"},
    "search_paths": {"query", "filter", "backend", "agentId"},
}
WRITE_TOOLS = {"create_entity", "update_entity", "delete_entity", "do_action"}
SINGLE_PATH_ARG = {"fetch_blob": "path", "create_entity": "parentUrl", "update_entity": "entityUrl",
                   "delete_entity": "entityUrl", "do_action": "actionUrl"}
# Reserved or routing parameters that would send the request to another agent or backend.
MUST_BE_EMPTY = ("agentId", "backend")
CONTROL_ARGS = {"on_data_of", "action_id", "attempt_id"}

ROOTS = {
    PRINCIPAL: {"me", "users", "sites", "drives", "chats", "teams", "planner"},
    SELF: {"me", "users", "chats"},
}
USER_DIRECTORY_READS = {"manager", "directreports"}
BLOCKED_SEGMENTS = {"authentication", "serviceprincipals"}
RETRIEVE_CAPABILITIES = {"People", "Meetings", "OneDriveAndSharePoint", "Email", "TeamsMessages",
                         "GraphConnectors"}
# Graph rejected these before acting; anything else after a write was sent stays unknown.
REJECTED_NO_EFFECT = {400, 401, 403, 404, 405, 409, 412, 413, 415, 422, 429}
THIRD_PARTY_TOOLS = {"get_schema", "search_paths", "list_agents"}
UNKNOWN_WRITE = "the write may have taken effect; outcome unknown, not retried"
MAX_FETCH_PATHS = 20
MAX_RETRIEVE_QUERIES = 10
MAX_PATH = 2048


class GuardConfigError(ValueError):
    """The guard was wired to something other than the one permitted endpoint."""


class TokenUnavailable(Exception):
    """The requested subject's token cannot be produced (revoked, expired, policy)."""


class TransportError(Exception):
    def __init__(self, message, status_code=None):
        super().__init__(message)
        self.status_code = status_code


class TransportTimeout(TransportError):
    """No response; the effect of a write is unknown."""


class _Refused(Exception):
    pass


class _Unavailable(Exception):
    """A principal source this access mode cannot reach; reported as unavailable, not empty."""


class CallBudget:
    """Charged before each provider call, so work is bounded before it starts."""

    def __init__(self, limit):
        if not isinstance(limit, int) or limit < 0:
            raise ValueError("budget limit must be a non-negative integer")
        self.limit, self.used = limit, 0

    def charge(self):
        if self.used >= self.limit:
            return False
        self.used += 1
        return True


def _utc_now():
    return datetime.now(timezone.utc).isoformat(timespec="microseconds")


def _canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def _check_path(value, intent, write, delegated_to=None):
    """Return the path without its query string, or refuse.

    ``delegated_to`` is the bound principal's object ID when the guard itself rewrote ``/me`` to
    their delegated mailbox; only that exact mailbox is then reachable under ``/users``."""
    if not isinstance(value, str) or not value or len(value) > MAX_PATH:
        raise _Refused("resource path must be a non-empty string")
    if not value.startswith("/") or value.startswith("//") or "://" in value:
        raise _Refused("only relative Work IQ resource paths are allowed")
    if any(ord(ch) < 32 or ord(ch) == 127 for ch in value) or "\\" in value:
        raise _Refused("control characters and backslashes are not allowed in paths")
    path = value.split("?", 1)[0]
    lowered = path.lower()
    if any(code in lowered for code in ("%2e", "%2f", "%5c", "%00")):
        raise _Refused("encoded path separators are not allowed")
    if any(ch.isspace() for ch in path):
        raise _Refused("whitespace is not allowed in the path")
    trimmed = path[:-1] if path.endswith("/") and len(path) > 1 else path
    segments = trimmed.split("/")[1:]
    if not segments or any(segment in ("", ".", "..") for segment in segments):
        raise _Refused("empty or relative path segments are not allowed")
    names = [segment.lower() for segment in segments]
    if names[0] not in ROOTS[intent]:
        raise _Refused("'/%s' is not an allowed root for on_data_of=%s" % (segments[0], intent))
    if BLOCKED_SEGMENTS.intersection(names):
        raise _Refused("path reaches a blocked resource")
    if names[0] == "users" and delegated_to is not None and len(names) >= 3 and names[2] in DELEGABLE:
        if segments[1] != delegated_to:
            raise _Refused("delegated access reaches only the bound principal's mailbox")
        return path
    if names[0] == "users":
        # Colleagues' mail, calendars, chats and files are never reachable; the principal's
        # own data uses /me under the principal token. Directory lookups stay available.
        if write or len(segments) not in (2, 3) or (len(segments) == 3 and names[2] not in USER_DIRECTORY_READS):
            raise _Refused("/users/{id} allows only profile, manager and directReports reads")
    return path


def _is_message(path, message_id):
    """``/me/messages/{message_id}`` or its attachments. Graph message IDs are case-sensitive."""
    segments = path.strip("/").split("/")
    return (isinstance(message_id, str) and len(segments) in (3, 4)
            and [segment.lower() for segment in segments[:2]] == ["me", "messages"]
            and segments[2] == message_id and (len(segments) == 3 or segments[3].lower() == "attachments"))


def _check_json_body(value):
    if value is None:
        return
    if not isinstance(value, str):
        raise _Refused("jsonBody must be a JSON-encoded string")
    try:
        json.loads(value)
    except ValueError as exc:
        raise _Refused("jsonBody is not valid JSON") from exc


def _validate(tool, args, intent, write, delegated_to=None):
    """Validate one call's arguments and return the resource paths it touches."""
    for key in MUST_BE_EMPTY:
        if args.get(key) not in (None, ""):
            raise _Refused("'%s' would route outside the built-in Work IQ path" % key)
    if tool == "fetch":
        urls = args.get("entityUrls")
        if not isinstance(urls, list) or not urls or len(urls) > MAX_FETCH_PATHS:
            raise _Refused("entityUrls must list 1-%d paths" % MAX_FETCH_PATHS)
        return [_check_path(url, intent, write, delegated_to) for url in urls]
    if tool == "call_function":
        present = [key for key in ("functionUrl", "parentUrl") if key in args]
        if len(present) != 1:
            raise _Refused("call_function needs exactly one of functionUrl or parentUrl")
        return [_check_path(args[present[0]], intent, write, delegated_to)]
    if tool in SINGLE_PATH_ARG:
        _check_json_body(args.get("jsonBody"))
        return [_check_path(args.get(SINGLE_PATH_ARG[tool]), intent, write, delegated_to)]
    if tool == "retrieve":
        query = args.get("query")
        queries = [query] if isinstance(query, str) else query
        # Work IQ takes a list of queries, as the playbooks write it; a single string also works.
        if not isinstance(queries, list) or not 1 <= len(queries) <= MAX_RETRIEVE_QUERIES \
                or not all(isinstance(item, str) and item.strip() for item in queries):
            raise _Refused("retrieve needs a query: a string or a list of 1-%d strings" % MAX_RETRIEVE_QUERIES)
        if args.get("strategy") not in (None, "grounding", "copilot"):
            raise _Refused("unknown retrieve strategy")
        capabilities = args.get("capabilities", [])
        if not isinstance(capabilities, list):
            raise _Refused("capabilities must be a list")
        for capability in capabilities:
            if not isinstance(capability, dict) or not isinstance(capability.get("name"), str) \
                    or capability["name"] not in RETRIEVE_CAPABILITIES:
                raise _Refused("unsupported retrieve capability")
            if any("mailbox" in str(key).lower() for key in capability):
                raise _Refused("other people's or shared mailboxes are not searchable in v1")
        if not isinstance(args.get("includeDeveloperCard", False), bool):
            raise _Refused("includeDeveloperCard must be a boolean")
        return []
    if tool == "ask":
        if not isinstance(args.get("question"), str) or not args["question"].strip():
            raise _Refused("ask needs a question")
        urls = args.get("fileUrls", [])
        if not isinstance(urls, list) or not all(isinstance(u, str) and u.startswith("https://") for u in urls):
            raise _Refused("fileUrls must be https document URLs")
        for key in ("conversationId", "timeZone"):
            if key in args and not isinstance(args[key], str):
                raise _Refused("%s must be a string" % key)
        return []
    if tool == "get_schema" and args.get("operationType") not in ("fetch", "create", "update", "action", "function"):
        raise _Refused("get_schema needs operationType fetch, create, update, action or function")
    if tool == "search_paths":
        present = [key for key in ("query", "filter") if key in args]
        if len(present) != 1 or not isinstance(args[present[0]], str) or not args[present[0]].strip():
            raise _Refused("search_paths needs exactly one non-empty query")
    return []


def _status_codes(result):
    if not isinstance(result, dict):
        return []
    codes = []
    if isinstance(result.get("statusCode"), int):
        codes.append(result["statusCode"])
    for entry in result.get("results", []) if isinstance(result.get("results"), list) else []:
        if isinstance(entry, dict) and isinstance(entry.get("statusCode"), int):
            codes.append(entry["statusCode"])
    return codes


class WorkIQGuard:
    """Validate, authorise, execute and record one Work IQ call at a time."""

    def __init__(self, binding, tokens, transport, *, journal=None, recorder=None, budget=None,
                 principal_access=OBO, trigger_message_id=None):
        """``trigger_message_id``: in a third-party run, the one message in Margo's mailbox that
        started it. Without it, a third-party run cannot open any message."""
        if getattr(transport, "endpoint", None) != WORKIQ_MCP_URL:
            raise GuardConfigError("the guard only connects to " + WORKIQ_MCP_URL)
        if principal_access not in PRINCIPAL_ACCESS:
            raise GuardConfigError("principal_access must be 'obo' or 'delegation'")
        self.principal_access = principal_access
        self.binding, self.tokens, self.transport = binding, tokens, transport
        self.journal, self.budget = journal, budget
        self.trigger_message_id = trigger_message_id
        self.receipts = []
        self._recorder = recorder
        self._consumed = set()

    # -- public -------------------------------------------------------------------------

    def call(self, tool, arguments, *, mode):
        """Return ``{"status", "read_as", "tool", ...}``. Never raises; refusals are results."""
        intent = arguments.get("on_data_of") if isinstance(arguments, dict) else None
        intent = intent if isinstance(intent, str) else None
        receipt = {"at": _utc_now(), "mode": mode if isinstance(mode, str) else None,
                   "tool": tool if isinstance(tool, str) else None, "on_data_of": intent,
                   "read_as": PRINCIPAL if intent == PRINCIPAL else MARGO if intent == SELF else None,
                   "paths": [], "status_codes": []}
        try:
            args, claim, paths = self._authorise(tool, arguments, intent, mode)
        except _Refused as exc:
            return self._finish(receipt, "refused", reason=str(exc))
        except _Unavailable as exc:
            return self._finish(receipt, "unavailable", reason=str(exc))
        except Exception:  # malformed model input is refused, never a crash without a receipt
            return self._finish(receipt, "refused", reason="malformed tool call")
        receipt["paths"] = paths
        write = tool in WRITE_TOOLS
        if claim:
            receipt["action_id"], receipt["attempt_id"] = claim["action_id"], claim["attempt_id"]
        if self.budget is not None and not self.budget.charge():
            self._settle(receipt, claim, "failed", None)
            return self._finish(receipt, "budget_exhausted", reason="call budget exhausted before the call")
        delegated = intent == PRINCIPAL and self.principal_access == DELEGATION
        if delegated:
            receipt["via"] = "delegation"
        try:
            bearer = self.tokens.token_for(PRINCIPAL if intent == PRINCIPAL and not delegated else MARGO)
        except Exception as exc:  # nothing was sent, so a claim settles as a no-effect failure
            self._settle(receipt, claim, "failed", None)
            detail = str(exc) if isinstance(exc, TokenUnavailable) else "token provider error"
            return self._finish(receipt, "blocked", reason="token unavailable for %s: %s" % (receipt["read_as"], detail))
        if claim:
            try:
                self.journal.mark_dispatched(claim)
            except Exception:
                return self._finish(receipt, "error", reason="could not record dispatch; nothing was sent")
        try:
            result = self.transport.call(tool, args, bearer=bearer)
        except TransportTimeout:
            self._settle(receipt, claim, "outcome_unknown", None)
            return self._finish(receipt, "unknown", reason="no response; effect unknown, not retried")
        except TransportError as exc:
            code = exc.status_code
            receipt["status_codes"] = [code] if isinstance(code, int) else []
            if write and code not in REJECTED_NO_EFFECT:
                self._settle(receipt, claim, "outcome_unknown", code)
                return self._finish(receipt, "unknown", reason=UNKNOWN_WRITE)
            self._settle(receipt, claim, "failed", code)
            return self._finish(receipt, "error", reason="Work IQ error")
        except Exception:  # connection reset, TLS, decoding: the request may have landed
            self._settle(receipt, claim, "outcome_unknown", None)
            return self._finish(receipt, "unknown", reason="transport failure; effect unknown, not retried")
        codes = _status_codes(result)
        receipt["status_codes"] = codes
        good = [code for code in codes if 200 <= code < 300]
        status = "ok" if len(good) == len(codes) else "partial" if good else "error"
        if not write:
            return self._finish(receipt, status, data=result)
        # A write is only "ok" with a success code, and only "error" when Graph rejected it before
        # acting. Anything else (no code, mixed, 5xx) may have landed: the model is told so.
        if status == "ok" and codes:
            self._settle(receipt, claim, "succeeded", codes[0])
        elif status == "error" and all(code in REJECTED_NO_EFFECT for code in codes):
            self._settle(receipt, claim, "failed", codes[0])
        else:
            self._settle(receipt, claim, "outcome_unknown", None)
            return self._finish(receipt, "unknown", reason=UNKNOWN_WRITE, data=result)
        return self._finish(receipt, status, data=result)

    # -- policy -------------------------------------------------------------------------

    def _authorise(self, tool, arguments, intent, mode):
        if not isinstance(mode, str) or mode not in MODES:
            raise _Refused("unknown run mode")
        if not isinstance(arguments, dict):
            raise _Refused("arguments must be an object")
        if not isinstance(tool, str) or tool not in TOOL_ARGS:
            raise _Refused("not a Work IQ tool")
        if intent not in INTENTS:
            raise _Refused("on_data_of must be 'principal' or 'self'")
        args = {key: value for key, value in arguments.items() if key not in CONTROL_ARGS}
        unknown = sorted(set(args) - TOOL_ARGS[tool])
        if unknown:
            raise _Refused("unexpected arguments: %s" % ", ".join(unknown))
        if mode == THIRD_PARTY and intent == PRINCIPAL:
            raise _Refused("a request from someone other than the principal never reads as the principal")
        write = tool in WRITE_TOOLS
        claimed = "action_id" in arguments or "attempt_id" in arguments
        if claimed and not write:
            raise _Refused("execution claims apply only to write tools")
        paths = _validate(tool, args, intent, write)
        if mode == THIRD_PARTY and write:
            raise _Refused("a run triggered by someone other than the principal cannot write")
        if mode == THIRD_PARTY:
            self._check_third_party_read(tool, paths)
        send_args = args
        if intent == PRINCIPAL and self.principal_access == DELEGATION:
            send_args = self._delegated(tool, args)
            paths = _validate(tool, send_args, PRINCIPAL, write, delegated_to=self.binding.principal_id)
        if not write:
            return send_args, None, paths
        if not claimed and self._is_principal_chat_post(tool, intent, paths):
            body = args.get("jsonBody")
            if not isinstance(body, str) or not isinstance(json.loads(body), dict):
                raise _Refused("a chat message needs a JSON object body")
            return send_args, None, paths
        if mode != ATTENDED:
            raise _Refused("unattended runs can only post to the principal's chat recorded at hire")
        if self.journal is None:
            raise _Refused("no approval journal is configured; writes are unavailable")
        try:
            claim = self.journal.load(arguments.get("action_id"), arguments.get("attempt_id"))
        except Exception as exc:  # ClaimError or ledger failure
            raise _Refused("no live execution claim: %s" % exc) from exc
        if claim["attempt_id"] in self._consumed:
            raise _Refused("execution claim already used")
        if claim["account"] != self.binding.account:
            raise _Refused("execution claim belongs to another instance")
        if claim.get("approved_by") != self.binding.principal_id:
            raise _Refused("the approval was not given by this instance's manager")
        if claim["on_data_of"] != intent:
            raise _Refused("approved action was for on_data_of=%s" % claim["on_data_of"])
        if claim.get("call_key") != "workiq" or _canonical(claim["call"]) != _canonical({"tool": tool, "arguments": args}):
            raise _Refused("call differs from the approved Work IQ call; revise and re-approve")
        self._consumed.add(claim["attempt_id"])
        return send_args, claim, paths

    def _delegated(self, tool, args):
        """Rewrite principal ``/me/...`` mail and calendar paths to the delegated mailbox."""
        if tool in ("retrieve", "ask"):
            raise _Unavailable("Copilot search of the principal's data needs on-behalf-of access")
        if tool in ("list_agents", "get_schema", "search_paths"):
            return args

        def rewrite(path):
            segments = path.split("?", 1)[0].strip("/").split("/")
            if len(segments) < 2 or segments[0].lower() != "me" or segments[1].lower() not in DELEGABLE:
                raise _Unavailable("only the principal's mail and calendar are reachable through delegation")
            return "/users/%s/%s" % (self.binding.principal_id, path[len("/me/"):])

        rewritten = dict(args)
        for key in PATH_KEYS:
            if key in rewritten:
                value = rewritten[key]
                rewritten[key] = [rewrite(v) for v in value] if isinstance(value, list) else rewrite(value)
        return rewritten

    def _check_third_party_read(self, tool, paths):
        """Least privilege when someone else triggered the run.

        Margo's own chats, search and Copilot can surface what she has posted to the
        principal, and her mailbox holds other people's requests, so a third-party-triggered
        run may only open the triggering message and use schema discovery.
        """
        if tool in THIRD_PARTY_TOOLS:
            return
        if tool != "fetch" or not all(_is_message(path, self.trigger_message_id) for path in paths):
            raise _Refused("a third-party-triggered run may only open the message that triggered it")

    def _is_principal_chat_post(self, tool, intent, paths):
        """The chat ID must be the Graph chat ID recorded at hire; unbound means no such post."""
        if tool != "create_entity" or intent != SELF or len(paths) != 1 or not self.binding.principal_chat_id:
            return False
        segments = paths[0].strip("/").split("/")
        if segments and segments[0].lower() == "me":
            segments = segments[1:]
        return (len(segments) == 3 and segments[0].lower() == "chats"
                and segments[1] == self.binding.principal_chat_id and segments[2].lower() == "messages")

    # -- records ------------------------------------------------------------------------

    def _settle(self, receipt, claim, state, status_code):
        if not claim:
            return
        try:
            self.journal.settle(claim, state, status_code)
            receipt["journal"] = state
        except Exception:  # the dispatch event still blocks a resend; reconciliation is needed
            receipt["journal"] = "unsettled"

    def _finish(self, receipt, status, *, reason=None, data=None):
        receipt["status"] = status
        if reason:
            receipt["reason"] = reason
        self.receipts.append(receipt)
        if self._recorder is not None:
            self._recorder(dict(receipt))
        response = {"status": status, "read_as": receipt["read_as"], "tool": receipt["tool"]}
        if reason:
            response["reason"] = reason
        if data is not None:
            response["data"] = data
        return response


def model_tools():
    """Tool definitions shown to the model: Work IQ's tools plus an explicit subject."""
    intent = {"type": "string", "enum": [PRINCIPAL, SELF],
              "description": "Whose data this call is about: the principal's, or Margo's own."}
    claim = {"action_id": {"type": "string", "description": "Approved action being executed."},
             "attempt_id": {"type": "string", "description": "Live execution attempt from the journal."}}
    tools = []
    for name in sorted(TOOL_ARGS):
        properties = {"on_data_of": intent}
        properties.update({arg: {} for arg in sorted(TOOL_ARGS[name] - set(MUST_BE_EMPTY))})
        if name in WRITE_TOOLS:
            properties.update(claim)
        tools.append({"name": name, "input_schema": {"type": "object", "properties": properties,
                                                     "required": ["on_data_of"]}})
    return tools
