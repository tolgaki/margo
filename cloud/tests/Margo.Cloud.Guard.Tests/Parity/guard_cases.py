"""Record what the Python Work IQ guard decides for many calls, for the C# port to match.

    python3 cloud/tests/Margo.Cloud.Guard.Tests/Parity/guard_cases.py --write   # regenerate
    python3 cloud/tests/Margo.Cloud.Guard.Tests/Parity/guard_cases.py --check   # fail if stale

Each case is a scenario: one guard (binding, access mode, trigger message, budget, token provider,
scripted transport and scripted claim journal) and a sequence of calls. The expected outcomes come
from running ``margo_autopilot.guard.WorkIQGuard`` itself: the response, the receipt (without its
time stamp), what reached the transport (tool, arguments, bearer), which token subjects were asked
for, and what the journal was asked to do. ``GuardParityTests`` replays every case against the C#
port. The journal is a fake here; the real ledger-backed journal is exercised by the xUnit ports of
``GuardedWriteTests``. Fictional data only.

Inputs are hand-written cases for every branch plus seeded random ones (paths, argument shapes,
modes, payload status codes, transport failures and claim mismatches).
"""

import argparse
import json
import random
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
sys.path.insert(0, str(ROOT / "skills" / "chief-of-staff" / "scripts"))
sys.path.insert(0, str(ROOT / "autopilot" / "src"))

from margo_autopilot import guard as guard_mod  # noqa: E402
from margo_autopilot.claims import ClaimError  # noqa: E402
from margo_autopilot.instance import InstanceBinding  # noqa: E402

OUTPUT = HERE / "guard_cases.json"
SEED = 20261107
RANDOM_CASES = 900
TOKENS = {"principal": "principal-token-fictional", "margo": "margo-token-fictional"}
ACCOUNT = "autopilot:agent-identity-1:principal-1"


# -- fakes (mirrored by GuardParityTests) ------------------------------------------------


class Tokens:
    def __init__(self, spec):
        self.missing, self.broken, self.requested = set(spec.get("missing", [])), spec.get("broken", False), []

    def token_for(self, subject):
        self.requested.append(subject)
        if self.broken:
            raise RuntimeError("vault unreachable")
        if subject in self.missing:
            raise guard_mod.TokenUnavailable("consent revoked")
        return TOKENS[subject]


class Transport:
    endpoint = guard_mod.WORKIQ_MCP_URL

    def __init__(self, spec):
        self.spec, self.sent = spec, []

    def call(self, tool, arguments, *, bearer):
        self.sent.append([tool, arguments, bearer])
        error = self.spec.get("error")
        if error == "timeout":
            raise guard_mod.TransportTimeout("no response")
        if error == "transport":
            raise guard_mod.TransportError("Work IQ HTTP error", status_code=self.spec.get("status_code"))
        if error == "other":
            raise ConnectionResetError("reset by peer")
        return json.loads(json.dumps(self.spec.get("result", {"statusCode": 200})))


class Journal:
    """Scripted claims: ``load`` follows LedgerClaimJournal's argument check, then returns the configured claim."""

    def __init__(self, spec):
        self.spec, self.log = spec, []

    def load(self, action_id, attempt_id):
        self.log.append(["load", action_id, attempt_id])
        if not isinstance(action_id, str) or not isinstance(attempt_id, str):
            raise ClaimError("action_id and attempt_id are required for a guarded write")
        if self.spec.get("load_error"):
            raise ClaimError(self.spec["load_error"])
        claim = {"account": self.spec.get("account", ACCOUNT), "action_id": action_id, "attempt_id": attempt_id,
                 "revision": 1, "action_hash": "hash-1", "on_data_of": self.spec.get("on_data_of"),
                 "call_key": self.spec.get("call_key", "workiq"), "call": self.spec.get("call", {}),
                 "approved_by": self.spec.get("approved_by")}
        return claim

    def mark_dispatched(self, claim):
        self.log.append(["dispatch", claim["attempt_id"]])
        if self.spec.get("dispatch_error"):
            raise ClaimError("attempt was already dispatched; reconcile it, never resend")

    def settle(self, claim, state, status_code=None):
        self.log.append(["settle", claim["attempt_id"], state, status_code])
        if self.spec.get("settle_error"):
            raise RuntimeError("database is locked")
        return {}


def run(case):
    binding = InstanceBinding("agent-identity-1", "margo-user-1", "principal-1", case.get("principal_chat_id"))
    tokens, transport = Tokens(case.get("tokens", {})), Transport(case.get("transport", {}))
    journal = Journal(case["journal"]) if "journal" in case else None
    budget = guard_mod.CallBudget(case["budget"]) if case.get("budget") is not None else None
    guard = guard_mod.WorkIQGuard(binding, tokens, transport, journal=journal, budget=budget,
                                  principal_access=case.get("principal_access", "obo"),
                                  trigger_message_id=case.get("trigger_message_id"))
    calls = []
    for call in case["calls"]:
        before = len(transport.sent)
        response = guard.call(call["tool"], json.loads(json.dumps(call["arguments"])), mode=call["mode"])
        receipt = dict(guard.receipts[-1])
        receipt.pop("at")
        sent = transport.sent[before:]
        calls.append({"response": response, "receipt": receipt, "sent": sent[0] if sent else None})
    return {"calls": calls, "requested": tokens.requested, "journal": journal.log if journal else None}


# -- inputs ------------------------------------------------------------------------------

OK = {"result": {"statusCode": 200, "data": {"value": []}}}


def one(name, tool, arguments, mode="attended", **setup):
    case = {"name": name, "calls": [{"tool": tool, "arguments": arguments, "mode": mode}]}
    case.update(setup)
    return case


def fetch(*paths, on="principal", **extra):
    return dict({"on_data_of": on, "entityUrls": list(paths)}, **extra)


def claim_for(tool, arguments, **spec):
    call = {"tool": tool, "arguments": {k: v for k, v in arguments.items() if k not in guard_mod.CONTROL_ARGS}}
    return dict({"on_data_of": arguments.get("on_data_of"), "approved_by": "principal-1", "call": call}, **spec)


def write_case(name, tool, arguments, journal=None, transport=None, mode="attended", **setup):
    arguments = dict(arguments, action_id="action-1", attempt_id="attempt-1")
    return one(name, tool, arguments, mode, journal=journal or claim_for(tool, arguments),
               transport=transport or {"result": {"statusCode": 202}}, **setup)


def handwritten():
    cases = []
    paths = [
        "/me/messages", "/me/messages?$top=5", "/me/messages/", "/ME/Messages", "/me", "/", "/?x", "",
        "me/messages", "//me/messages", "https://graph.example.com/me", "/me/x://y", "/me/\x01", "/me/\x7f",
        "/me\\messages", "/me/%2e%2e/x", "/me/%2E/x", "/me/%2F", "/me/%5c", "/me/%00", "/me/mess ages",
        "/me/mess\u00a0ages", "/me/mess\u3000ages", "/me/messages?$search=\"a b\"", "/me/messages?x=%2e",
        "/me//messages", "/me/./messages", "/me/../users", "/me/messages//", "/users", "/users/",
        "/users/colleague-1", "/users/colleague-1/", "/users/colleague-1/manager", "/users/colleague-1/Manager",
        "/users/colleague-1/directReports", "/users/colleague-1/messages", "/users/colleague-1/manager/x",
        "/users/principal-1/messages", "/sites/s-1/drive", "/drives/d-1/root", "/chats/c-1/messages",
        "/teams/t-1/channels", "/planner/plans", "/groups/g-1", "/servicePrincipals", "/me/authentication",
        "/me/Authentication/methods", "/me/drive/servicePrincipals", "/\u212ae/messages", "/m\u0130/messages",
        "/" + "a" * 2047, "/" + "a" * 2048, "/me/\U0001F600", "/me/caf\u00e9",
    ]
    for intent in ("principal", "self"):
        for index, path in enumerate(paths):
            cases.append(one("path %s %d %r" % (intent, index, path), "fetch", fetch(path, on=intent)))
            cases.append(one("delegated path %s %d %r" % (intent, index, path), "fetch", fetch(path, on=intent),
                             principal_access="delegation"))
    for value in (None, 1, True, [], {}, ["/me"], 2.5):
        cases.append(one("entityUrls item %r" % (value,), "fetch", {"on_data_of": "principal", "entityUrls": [value]}))
    for urls in (None, "/me/messages", [], {"a": "/me"}, ["/me"] * 20, ["/me"] * 21):
        cases.append(one("entityUrls %r" % (urls if not isinstance(urls, list) or len(urls) < 5 else len(urls),),
                         "fetch", {"on_data_of": "principal", "entityUrls": urls}))
    # arguments, tools, modes, intents
    for tool in sorted(guard_mod.TOOL_ARGS) + ["send_mail", "", "FETCH", None, 3, ["fetch"]]:
        cases.append(one("bare tool %r" % (tool,), tool, {"on_data_of": "principal"}))
    for mode in ("attended", "unattended", "third_party", "ATTENDED", "", None, 1, ["attended"]):
        cases.append(one("mode %r" % (mode,), "fetch", fetch("/me/messages", on="self"), mode,
                         trigger_message_id="msg-1"))
    for arguments in (None, "x", 1, [], ["on_data_of"], {}):
        cases.append(one("arguments %r" % (arguments,), "fetch", arguments))
    for intent in ("principal", "self", "margo", "PRINCIPAL", "", None, 1, ["principal"], {"a": 1}):
        cases.append(one("intent %r" % (intent,), "fetch", {"on_data_of": intent, "entityUrls": ["/me"]}))
    cases.append(one("intent missing", "fetch", {"entityUrls": ["/me"]}))
    for extra in ({"zeta": 1, "alpha": 2, "Beta": 3}, {"agentId": ""}, {"agentId": None}, {"agentId": "x"},
                  {"agentId": 0}, {"agentId": False}, {"agentId": []}, {"backend": "x"}, {"\u00e9": 1, "z": 1},
                  {"action_id": "a"}, {"attempt_id": None}, {"action_id": "a", "attempt_id": "b"}):
        cases.append(one("extra %r" % (extra,), "fetch", fetch("/me/messages", **extra)))
    cases.append(one("backend on get_schema", "get_schema",
                     {"on_data_of": "self", "path": "/x", "operationType": "fetch", "backend": "other"}))
    cases.append(one("empty backend on get_schema", "get_schema",
                     {"on_data_of": "self", "path": "/x", "operationType": "fetch", "backend": ""}))
    # call_function, single-path tools, json bodies
    for arguments in ({"functionUrl": "/me/calendarView?startDateTime=a"}, {"parentUrl": "/me/events"},
                      {"functionUrl": "/me/x", "parentUrl": "/me/y"}, {}, {"functionUrl": None},
                      {"functionUrl": "/users/u-1/calendarView"}):
        for access in ("obo", "delegation"):
            cases.append(one("call_function %r %s" % (arguments, access), "call_function",
                             dict({"on_data_of": "principal"}, **arguments), principal_access=access))
    for body in (None, "{}", "[]", "1", "null", "nope", "{\"a\": NaN}", "{\"a\":", 3, {"a": 1}, ["x"], "\"s\"",
                 "[" * 20000 + "]" * 20000, "1" * 4301):
        cases.append(one("fetch_blob/json body %r" % (body if not isinstance(body, str) or len(body) < 30 else len(body),),
                         "update_entity", {"on_data_of": "principal", "entityUrl": "/me/events/e-1", "jsonBody": body}))
    for tool in ("fetch_blob", "delete_entity", "create_entity", "do_action", "update_entity"):
        arg = guard_mod.SINGLE_PATH_ARG[tool]
        for path in ("/me/messages/m-1", "/users/u-1", "/users/u-1/manager", "/chats/c-1/messages", "x", None):
            for access in ("obo", "delegation"):
                cases.append(one("single %s %r %s" % (tool, path, access), tool,
                                 {"on_data_of": "principal", arg: path}, principal_access=access))
    cases.append(one("fetch_blob format", "fetch_blob", {"on_data_of": "self", "path": "/me/drive/items/i-1/content",
                                                         "format": "text"}))
    # retrieve
    for query in ("launch", " ", "", ["a"], ["a", "b"], [], [""], [" \u3000"], ["a", 3], ["q"] * 10, ["q"] * 11,
                  {"q": "x"}, None, 3):
        cases.append(one("retrieve query %r" % (query if not isinstance(query, list) or len(query) < 5 else len(query),),
                         "retrieve", {"on_data_of": "principal", "query": query}))
    cases.append(one("retrieve without query", "retrieve", {"on_data_of": "principal"}))
    for strategy in ("grounding", "copilot", None, "Copilot", "", 1, ["copilot"]):
        cases.append(one("retrieve strategy %r" % (strategy,), "retrieve",
                         {"on_data_of": "principal", "query": "x", "strategy": strategy}))
    for capabilities in ([], None, "Email", {"name": "Email"}, [{"name": "Email"}], [{"name": "email"}],
                         [{"name": "Email"}, {"name": "People"}], [{"name": "Email", "MailBox": "x"}],
                         [{"name": "Email", "sharedMAILBOX": 1}], [{"name": "Email", "mai\u0130lbox": 1}],
                         [{"name": "Email", "scope": "x"}], [{"Name": "Email"}], [{"name": None}], ["Email"],
                         [{"name": "GraphConnectors"}], [{"name": "Unknown"}]):
        cases.append(one("retrieve capabilities %r" % (capabilities,), "retrieve",
                         {"on_data_of": "self", "query": ["x"], "capabilities": capabilities}))
    for card in (True, False, None, "yes", 1, 0):
        cases.append(one("retrieve developer card %r" % (card,), "retrieve",
                         {"on_data_of": "principal", "query": "x", "includeDeveloperCard": card}))
    # ask
    for extra in ({"question": "Anything urgent?"}, {"question": " "}, {"question": None}, {}, {"question": 1},
                  {"question": "q", "fileUrls": []}, {"question": "q", "fileUrls": ["https://contoso.example.com/d"]},
                  {"question": "q", "fileUrls": ["http://contoso.example.com/d"]}, {"question": "q", "fileUrls": "https://x"},
                  {"question": "q", "fileUrls": None}, {"question": "q", "conversationId": "c-1", "timeZone": "UTC"},
                  {"question": "q", "conversationId": None}, {"question": "q", "timeZone": 1}):
        cases.append(one("ask %r" % (extra,), "ask", dict({"on_data_of": "principal"}, **extra)))
    # discovery
    for operation in ("fetch", "create", "update", "action", "function", "delete", None, "", ["fetch"]):
        cases.append(one("get_schema %r" % (operation,), "get_schema",
                         {"on_data_of": "principal", "path": "/me", "operationType": operation}))
    cases.append(one("get_schema without operation", "get_schema", {"on_data_of": "principal", "path": "/me"}))
    for extra in ({"query": "q"}, {"filter": "f"}, {"query": "q", "filter": "f"}, {}, {"query": " "},
                  {"query": None}, {"filter": 3}):
        cases.append(one("search_paths %r" % (extra,), "search_paths", dict({"on_data_of": "self"}, **extra)))
    cases.append(one("list_agents", "list_agents", {"on_data_of": "self"}))
    cases.append(one("list_agents with an argument", "list_agents", {"on_data_of": "self", "agentId": ""}))
    # third-party runs
    third = [
        fetch("/me/messages/msg-1", on="self"), fetch("/me/messages/msg-1?$select=body", on="self"),
        fetch("/me/messages/msg-1/attachments", on="self"), fetch("/me/messages/msg-1/Attachments/", on="self"),
        fetch("/ME/MESSAGES/msg-1", on="self"), fetch("/me/messages/MSG-1", on="self"),
        fetch("/me/messages/msg-1/attachments/a-1", on="self"), fetch("/me/messages", on="self"),
        fetch("/me/messages/msg-1", "/me/messages/msg-1", on="self"), fetch("/messages/msg-1", on="self"),
        fetch("/me/mailFolders/inbox/messages/msg-1", on="self"), fetch("/me/messages/msg-1"),
    ]
    for index, arguments in enumerate(third):
        for trigger in ("msg-1", None, "MSG-1"):
            cases.append(one("third party %d %r" % (index, trigger), "fetch", arguments, "third_party",
                             trigger_message_id=trigger))
    for tool, arguments in (("get_schema", {"on_data_of": "self", "path": "/me", "operationType": "fetch"}),
                            ("list_agents", {"on_data_of": "self"}), ("search_paths", {"on_data_of": "self", "query": "q"}),
                            ("retrieve", {"on_data_of": "self", "query": "q"}), ("ask", {"on_data_of": "self", "question": "q"}),
                            ("fetch_blob", {"on_data_of": "self", "path": "/me/messages/msg-1"}),
                            ("call_function", {"on_data_of": "self", "functionUrl": "/me/messages/msg-1"}),
                            ("get_schema", {"on_data_of": "principal", "path": "/me", "operationType": "fetch"})):
        cases.append(one("third party tool %s %s" % (tool, arguments["on_data_of"]), tool, arguments, "third_party",
                         trigger_message_id="msg-1"))
    # delegation
    for tool, arguments in (("retrieve", {"on_data_of": "principal", "query": "q"}),
                            ("ask", {"on_data_of": "principal", "question": "q"}),
                            ("get_schema", {"on_data_of": "principal", "path": "/me/chats", "operationType": "fetch"}),
                            ("search_paths", {"on_data_of": "principal", "query": "q"}),
                            ("list_agents", {"on_data_of": "principal"}),
                            ("fetch", fetch("/me/mailFolders/inbox/messages", "/me/calendars/c-1/events", "/me/Events")),
                            ("fetch", fetch("/me/messages", "/me/drive")), ("fetch", fetch("/me")),
                            ("fetch", fetch("/me/messages", on="self")),
                            ("fetch", fetch("/users/principal-1/messages")), ("fetch", fetch("/users/colleague-1")),
                            ("fetch_blob", {"on_data_of": "principal", "path": "/me/messages/m-1/$value"}),
                            ("retrieve", {"on_data_of": "self", "query": "q"})):
        cases.append(one("delegation %s %r" % (tool, arguments), tool, arguments, principal_access="delegation"))
    # principal chat post
    body = json.dumps({"body": {"content": "Your brief is ready."}})
    for path in ("/chats/chat-principal-1/messages", "/me/chats/chat-principal-1/messages", "/Me/Chats/chat-principal-1/Messages",
                 "/chats/chat-principal-1/messages/", "/chats/CHAT-PRINCIPAL-1/messages", "/chats/chat-other-1/messages",
                 "/chats/chat-principal-1/messages/m-1/replies", "/chats/chat-principal-1", "/me/messages",
                 "/chats/chat-principal-1/messages?x=1"):
        for mode in ("unattended", "attended", "third_party"):
            for chat in ("chat-principal-1", None):
                cases.append(one("chat post %r %s %r" % (path, mode, chat), "create_entity",
                                 {"on_data_of": "self", "parentUrl": path, "jsonBody": body}, mode,
                                 principal_chat_id=chat, transport={"result": {"statusCode": 201}}))
    for json_body in ("[]", "1", "null", None, "{}", "nope"):
        cases.append(one("chat post body %r" % (json_body,), "create_entity",
                         {"on_data_of": "self", "parentUrl": "/chats/chat-principal-1/messages", "jsonBody": json_body},
                         "unattended", principal_chat_id="chat-principal-1"))
    cases.append(one("chat post as principal", "create_entity",
                     {"on_data_of": "principal", "parentUrl": "/chats/chat-principal-1/messages", "jsonBody": body},
                     "unattended", principal_chat_id="chat-principal-1"))
    cases.append(one("chat post with a claim argument", "create_entity",
                     {"on_data_of": "self", "parentUrl": "/chats/chat-principal-1/messages", "jsonBody": body,
                      "action_id": None}, "unattended", principal_chat_id="chat-principal-1"))
    cases.append(one("chat post through update_entity", "update_entity",
                     {"on_data_of": "self", "entityUrl": "/chats/chat-principal-1/messages", "jsonBody": body},
                     "unattended", principal_chat_id="chat-principal-1"))
    # claimed writes through a scripted journal
    send = {"on_data_of": "principal", "actionUrl": "/me/messages/draft-1/send"}
    sent = dict(send, action_id="action-1", attempt_id="attempt-1")
    cases.append(one("write without a journal", "do_action", sent))
    for mode in ("attended", "unattended", "third_party"):
        cases.append(write_case("claimed write %s" % mode, "do_action", send, mode=mode))
    for name, spec in (("load error", {"load_error": "unknown action"}),
                       ("other account", {"account": "autopilot:agent-identity-2:principal-1"}),
                       ("approved by a colleague", {"approved_by": "colleague-1"}),
                       ("approved by nobody", {"approved_by": None}), ("approved by a list", {"approved_by": ["principal-1"]}),
                       ("approved for self", {"on_data_of": "self"}), ("approved for nothing", {"on_data_of": None}),
                       ("approved for a list", {"on_data_of": ["principal"]}), ("approved for a number", {"on_data_of": 1.5}),
                       ("approved for a dict", {"on_data_of": {"a": True}}),
                       ("reply claim", {"call_key": "reply"}),
                       ("different url", {"call": {"tool": "do_action", "arguments": {"actionUrl": "/me/messages/draft-2/send"}}}),
                       ("different tool", {"call": {"tool": "create_entity", "arguments": {"actionUrl": "/me/messages/draft-1/send"}}}),
                       ("extra argument", {"call": {"tool": "do_action", "arguments": {"actionUrl": "/me/messages/draft-1/send", "jsonBody": "{}"}}}),
                       ("reordered call", {"call": {"arguments": {"actionUrl": "/me/messages/draft-1/send"}, "tool": "do_action"}}),
                       ("dispatch fails", {"dispatch_error": True}), ("settle fails", {"settle_error": True})):
        journal = dict(claim_for("do_action", sent), **spec)
        cases.append(write_case("claim %s" % name, "do_action", send, journal=journal))
    numeric = dict(send, jsonBody="{\"n\": 1}")
    for stored in ("{\"n\": 1}", "{\"n\": 1.0}", "{\"n\":1}"):
        journal = claim_for("do_action", dict(numeric, jsonBody=stored))
        cases.append(write_case("claim body %r" % stored, "do_action", numeric, journal=journal))
    nested = {"on_data_of": "principal", "entityUrl": "/me/events/e-1", "jsonBody": "{\"b\": 2, \"a\": [1, 2.5, true, null, \"\u00e9\"]}"}
    cases.append(write_case("claim nested body", "update_entity", nested))
    for ids in ((None, "attempt-1"), ("action-1", None), (1, "attempt-1"), ("action-1", ["attempt-1"])):
        arguments = dict(send, action_id=ids[0], attempt_id=ids[1])
        cases.append(one("claim ids %r" % (ids,), "do_action", arguments, journal=claim_for("do_action", arguments)))
    cases.append(one("claim action id only", "do_action", dict(send, action_id="action-1"),
                     journal=claim_for("do_action", send)))
    same = {"name": "claim used twice", "journal": claim_for("do_action", sent), "transport": {"result": {"statusCode": 202}},
            "calls": [{"tool": "do_action", "arguments": sent, "mode": "attended"}] * 2}
    cases.append(same)
    same_after_refusal = {"name": "claim refused then used", "journal": claim_for("do_action", sent),
                          "transport": {"result": {"statusCode": 202}},
                          "calls": [{"tool": "do_action", "arguments": dict(sent, actionUrl="/me/messages/draft-2/send"),
                                     "mode": "attended"}, {"tool": "do_action", "arguments": sent, "mode": "attended"}]}
    cases.append(same_after_refusal)
    for intent in ("principal", "self"):
        own = {"on_data_of": intent, "actionUrl": "/me/sendMail", "jsonBody": "{\"message\": {}}"}
        for access in ("obo", "delegation"):
            cases.append(write_case("claimed %s %s" % (intent, access), "do_action", own, principal_access=access))
    cases.append(write_case("claimed delegated write outside mail", "create_entity",
                            {"on_data_of": "principal", "parentUrl": "/me/drive/root/children", "jsonBody": "{}"},
                            principal_access="delegation"))
    cases.append(write_case("claimed write to a colleague", "do_action",
                            {"on_data_of": "principal", "actionUrl": "/users/colleague-1/sendMail"}))
    # outcomes: payload status codes and transport failures, for reads and writes
    payloads = [
        {"statusCode": 200}, {"statusCode": 204}, {"statusCode": 299}, {"statusCode": 300}, {"statusCode": 199},
        {"statusCode": 201, "data": {"id": "m-1"}}, {}, {"data": {}}, {"statusCode": "200"}, {"statusCode": 200.0},
        {"statusCode": True}, {"statusCode": False}, {"statusCode": None}, {"statusCode": 10 ** 30},
        {"statusCode": -1}, {"statusCode": 404}, {"statusCode": 429}, {"statusCode": 500}, {"statusCode": 503},
        {"results": [{"statusCode": 200}, {"statusCode": 404}]}, {"results": [{"statusCode": 404}, {"statusCode": 403}]},
        {"results": [{"statusCode": 404}, {"statusCode": 500}]}, {"results": [{"statusCode": 200}, {"statusCode": 201}]},
        {"statusCode": 207, "results": [{"statusCode": 200}, {"statusCode": 409}]},
        {"statusCode": 200, "results": "x"}, {"results": [1, "x", None, {"statusCode": "404"}, {"code": 1}]},
        {"results": [{"statusCode": True}]}, {"results": []}, {"value": [1, 2]}, {"text": "not json"},
        {"statusCode": 412, "results": [{"statusCode": 413}, {"statusCode": 415}, {"statusCode": 422}]},
        {"statusCode": 400}, {"statusCode": 401}, {"statusCode": 405}, {"statusCode": 408},
    ]
    failures = [{"error": "timeout"}, {"error": "other"}] + [
        {"error": "transport", "status_code": code} for code in (None, 400, 401, 403, 404, 405, 408, 409, 412, 413,
                                                                  415, 422, 429, 500, 502, 503, 200, -5, 0)]
    for index, transport in enumerate([{"result": payload} for payload in payloads] + failures):
        cases.append(one("read outcome %d %r" % (index, transport), "fetch", fetch("/me/messages"),
                         transport=transport))
        cases.append(write_case("write outcome %d %r" % (index, transport), "do_action", send, transport=transport))
        cases.append(one("chat post outcome %d %r" % (index, transport), "create_entity",
                         {"on_data_of": "self", "parentUrl": "/chats/chat-principal-1/messages", "jsonBody": body},
                         "unattended", principal_chat_id="chat-principal-1", transport=transport))
    cases.append(write_case("write outcome with settle failing", "do_action", send, journal=dict(claim_for(
        "do_action", sent), settle_error=True), transport={"error": "timeout"}))
    # tokens and budgets
    for spec in ({"missing": ["principal"]}, {"missing": ["margo"]}, {"missing": ["principal", "margo"]}, {"broken": True}):
        for intent in ("principal", "self"):
            for access in ("obo", "delegation"):
                cases.append(one("tokens %r %s %s" % (spec, intent, access), "fetch", fetch("/me/messages", on=intent),
                                 tokens=spec, principal_access=access))
        cases.append(write_case("tokens %r claimed" % (spec,), "do_action", send, tokens=spec))
    for limit in (0, 1, 2):
        cases.append({"name": "budget %d" % limit, "budget": limit, "transport": OK,
                      "calls": [{"tool": "fetch", "arguments": fetch("/me/messages"), "mode": "attended"},
                                {"tool": "fetch", "arguments": fetch("/users"), "mode": "attended"},
                                {"tool": "fetch", "arguments": fetch("/me/events", on="self"), "mode": "attended"}]})
    cases.append(write_case("budget 0 claimed", "do_action", send, budget=0))
    return cases


# -- seeded random cases -------------------------------------------------------------------

# "USERS", not "Users": "/Users/<name>" reads as a macOS home path to tools/check-clean.sh.
SEGMENTS = ["me", "ME", "Me", "users", "USERS", "sites", "drives", "chats", "teams", "planner", "groups", "messages",
            "Messages", "mailFolders", "calendar", "calendars", "calendarView", "events", "manager", "directReports",
            "authentication", "servicePrincipals", "attachments", "drive", "root", "children", "send", "sendMail",
            "colleague-1", "principal-1", "PRINCIPAL-1", "chat-principal-1", "msg-1", "MSG-1", "draft-1", "", ".",
            "..", "%2e", "%2F", "%5C", "%00", "a b", "a\u00a0b", "\u212aind", "\u0130d", "mE", "caf\u00e9", "\U0001F600",
            "$value", "x" * 40]
QUERIES = ["", "?$top=5", "?$search=\"q r\"", "?x=%2e", "?/../users", "?a=b?c", "?"]
VALUES = [None, True, False, 0, 1, -1, 2.5, "", " ", "x", [], ["x"], {}, {"a": 1}]


def random_path(rng):
    roll = rng.random()
    if roll < 0.04:
        return rng.choice(["", "me/messages", "//me", "https://graph.example.com/me", "/me\\x", "/me/\x05", "/" + "a" * 2048])
    path = "/" + "/".join(rng.choice(SEGMENTS) for _ in range(rng.randint(1, 5)))
    if rng.random() < 0.1:
        path += "/"
    if rng.random() < 0.25:
        path += rng.choice(QUERIES)
    return path


def random_value(rng):
    return rng.choice(VALUES)


def random_arguments(rng, tool):
    intent = rng.choice(["principal", "self", "principal", "self", None, "margo", 1])
    arguments = {} if rng.random() < 0.05 else {"on_data_of": intent}
    if tool == "fetch":
        arguments["entityUrls"] = [random_path(rng) for _ in range(rng.choice([1, 1, 1, 2, 3, 0]))]
    elif tool in guard_mod.SINGLE_PATH_ARG:
        arguments[guard_mod.SINGLE_PATH_ARG[tool]] = random_path(rng)
        if tool in ("create_entity", "update_entity", "do_action") and rng.random() < 0.7:
            arguments["jsonBody"] = rng.choice(["{}", "{\"a\": 1}", "[]", "x", None, 1, "{\"body\": {\"content\": \"c\"}}"])
    elif tool == "call_function":
        for key in rng.sample(["functionUrl", "parentUrl"], rng.choice([1, 1, 2])):
            arguments[key] = random_path(rng)
    elif tool == "retrieve":
        arguments["query"] = rng.choice(["q", ["q"], ["a", "b"], [], [""], None, 3, ["q"] * 11])
        if rng.random() < 0.4:
            arguments["capabilities"] = rng.choice([[{"name": "Email"}], [{"name": "x"}], [{"name": "People", "mailbox": 1}], None])
        if rng.random() < 0.2:
            arguments["strategy"] = rng.choice(["grounding", "copilot", "x", None])
    elif tool == "ask":
        arguments["question"] = rng.choice(["q", " ", None, 1])
        if rng.random() < 0.3:
            arguments["fileUrls"] = rng.choice([[], ["https://contoso.example.com/d"], ["ftp://x"], None])
    elif tool == "get_schema":
        arguments["operationType"] = rng.choice(["fetch", "create", "x", None])
        arguments["path"] = random_path(rng)
    elif tool == "search_paths":
        for key in rng.sample(["query", "filter"], rng.choice([0, 1, 1, 2])):
            arguments[key] = rng.choice(["q", " ", None, 1])
    if rng.random() < 0.1:
        arguments[rng.choice(["agentId", "backend", "extra", "Zeta", "format"])] = random_value(rng)
    if rng.random() < 0.08:
        arguments[rng.choice(["action_id", "attempt_id"])] = rng.choice(["action-1", None])
    return arguments


def random_cases(rng):
    tools = sorted(guard_mod.TOOL_ARGS)
    transports = [OK, {"result": {"statusCode": 201}}, {"result": {"statusCode": 404}}, {"result": {}},
                  {"result": {"results": [{"statusCode": 200}, {"statusCode": 500}]}}, {"error": "timeout"},
                  {"error": "transport", "status_code": 403}, {"error": "transport", "status_code": 503},
                  {"error": "transport", "status_code": None}, {"error": "other"}]
    cases = []
    for index in range(RANDOM_CASES):
        tool = rng.choice(tools)
        arguments = random_arguments(rng, tool)
        case = one("random %04d %s" % (index, tool), tool, arguments,
                   rng.choice(["attended", "attended", "unattended", "third_party"]),
                   principal_access=rng.choice(["obo", "obo", "delegation"]),
                   principal_chat_id=rng.choice(["chat-principal-1", None]),
                   trigger_message_id=rng.choice(["msg-1", None]), transport=rng.choice(transports))
        if tool in guard_mod.WRITE_TOOLS and rng.random() < 0.6:
            arguments.update(action_id="action-1", attempt_id="attempt-1")
            spec = claim_for(tool, arguments)
            if rng.random() < 0.3:
                spec.update(rng.choice([{"approved_by": "colleague-1"}, {"on_data_of": "self"}, {"settle_error": True},
                                        {"dispatch_error": True}, {"call_key": "reply"}]))
            case["journal"] = spec
        cases.append(case)
    return cases


def build():
    rng = random.Random(SEED)
    cases = handwritten() + random_cases(rng)
    names = [case["name"] for case in cases]
    duplicates = {name for name in names if names.count(name) > 1}
    if duplicates:
        raise SystemExit("duplicate case names: %r" % sorted(duplicates)[:5])
    for case in cases:
        case["expect"] = run(case)
    checks = {
        "model_tools": guard_mod.model_tools(),
        "status_codes": [[payload, guard_mod._status_codes(payload)] for payload in (
            None, [], "x", {}, {"statusCode": 200}, {"statusCode": True}, {"statusCode": 2.0},
            {"results": [{"statusCode": 1}, {"statusCode": None}, 5, {"statusCode": 10 ** 20}]},
            {"statusCode": 1, "results": {"statusCode": 2}})],
    }
    return {"generated_by": "cloud/tests/Margo.Cloud.Guard.Tests/Parity/guard_cases.py", "seed": SEED,
            "checks": checks, "cases": cases}


def render(fixture):
    head = {key: value for key, value in fixture.items() if key != "cases"}
    lines = ["{"]
    for key, value in head.items():
        lines.append(" %s: %s," % (json.dumps(key), json.dumps(value, sort_keys=False)))
    lines.append(' "cases": [')
    lines.append(",\n".join("  " + json.dumps(case) for case in fixture["cases"]))
    lines.append(" ]")
    lines.append("}")
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--write", action="store_true")
    group.add_argument("--check", action="store_true")
    options = parser.parse_args()
    text = render(build())
    if options.write:
        OUTPUT.write_text(text, encoding="utf-8")
        print("wrote %d cases to %s" % (text.count('\n  {"name"') + 1, OUTPUT))
        return 0
    if not OUTPUT.exists() or OUTPUT.read_text(encoding="utf-8") != text:
        print("guard_cases.json is stale; run with --write", file=sys.stderr)
        return 1
    print("guard_cases.json matches guard.py")
    return 0


if __name__ == "__main__":
    sys.exit(main())
