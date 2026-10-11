"""Work IQ action gate: tier classifier, directive and approval enforcement, peer roles, control methods.

Every fixture is fictional (dana@example.com manages, margo@example.com is Margo, object ids come
from tests/fixtures/remote_host/deployment_fixture.py at runtime). Socket tests run the real gate
in a subprocess in front of tests/fixtures/remote_host/fake_workiq.py; they need Linux peer
credentials and are skipped elsewhere.
"""

import json
import os
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import unittest
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = ROOT / "skills/chief-of-staff/scripts"
FIXTURES = ROOT / "tests/fixtures/remote_host"
sys.path.insert(0, str(SCRIPTS))
sys.path.insert(0, str(FIXTURES))

import workiq_gate as gate  # noqa: E402
import manager_directives as md  # noqa: E402
from margo_store import StateError  # noqa: E402
from work_ledger import Ledger  # noqa: E402
from deployment_fixture import (CHAT_ID, MANAGER_OID, MANAGER_PRINCIPAL, MARGO_OID, MARGO_PRINCIPAL,  # noqa: E402
                                OUTSIDER_OID, make_deployment)

GATE = SCRIPTS / "workiq_gate.py"
CLIENT = SCRIPTS / "workiq_gate_client.py"
FAKE = FIXTURES / "fake_workiq.py"
LINUX = sys.platform.startswith("linux") and hasattr(socket, "SO_PEERCRED")
PASSING_HEADERS = [{"name": "Authentication-Results", "value": "spf=pass smtp.mailfrom=example.com; "
                                                                "dkim=pass header.d=example.com; dmarc=pass"}]


def graph_stamp(seconds=-90):
    """Graph-style timestamp (seven fractional digits) safely in the past so approvals are not 'future'."""
    moment = datetime.now(timezone.utc) + timedelta(seconds=seconds)
    return moment.strftime("%Y-%m-%dT%H:%M:%S.%f") + "0Z"


def teams_message(content, message_id=None, **overrides):
    message = {"id": message_id or "teams-" + uuid.uuid4().hex[:10], "messageType": "message", "chatId": CHAT_ID,
               "createdDateTime": graph_stamp(), "lastEditedDateTime": None, "deletedDateTime": None,
               "from": {"user": {"id": MANAGER_OID, "displayName": "Dana"}},
               "body": {"contentType": "text", "content": content}, "attachments": []}
    message.update(overrides)
    return message


def chat():
    return {"id": CHAT_ID, "chatType": "oneOnOne", "members": [{"userId": MANAGER_OID}, {"user": {"id": MARGO_OID}}]}


def email(content, message_id, **overrides):
    message = {"id": "mail-" + uuid.uuid4().hex[:8], "internetMessageId": message_id, "receivedDateTime": graph_stamp(),
               "subject": "For Margo", "from": {"emailAddress": {"address": MANAGER_PRINCIPAL}},
               "sender": {"emailAddress": {"address": MANAGER_PRINCIPAL}},
               "toRecipients": [{"emailAddress": {"address": MARGO_PRINCIPAL}}],
               "internetMessageHeaders": list(PASSING_HEADERS), "body": {"contentType": "text", "content": content}}
    message.update(overrides)
    return message


def base_rules(messages=(), inbox=(), extra=()):
    """Scripted upstream: health probes pass, the control chat and inbox hold the given messages."""
    return list(extra) + [
        {"tool": "fetch", "path": r"/users/[^/]+/mailfolders/inbox", "result": {"id": "inbox"}},
        {"tool": "fetch", "path": r"/users/[^/]+/calendarview", "result": {"value": []}},
        {"tool": "fetch", "path": r"^/chats/[^/]+\?", "result": chat()},
        {"tool": "fetch", "path": r"^/chats/[^/]+/messages", "result": {"value": list(messages)}},
        {"tool": "fetch", "path": r"^/me/messages\?", "result": {"value": list(inbox)}},
    ]


class Client:
    """Newline JSON-RPC over the gate socket; notifications are kept aside for assertions."""

    def __init__(self, path, timeout=20.0):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(timeout)
        for attempt in range(20):
            try:
                self.sock.connect(path)
                break
            except OSError:
                if attempt == 19:
                    raise
                time.sleep(0.05)
        self.stream = self.sock.makefile("rb")
        self.counter = 0
        self.notifications = []

    def send_raw(self, data):
        self.sock.sendall(data)

    def read(self):
        line = self.stream.readline()
        if not line:
            raise ConnectionError("gate closed the connection")
        return json.loads(line.decode("utf-8"))

    def call(self, method, params=None, request_id=None):
        self.counter += 1
        rid = self.counter if request_id is None else request_id
        self.sock.sendall((json.dumps({"jsonrpc": "2.0", "id": rid, "method": method, "params": params or {}}) + "\n").encode("utf-8"))
        while True:
            message = self.read()
            if message.get("id") == rid and ("result" in message or "error" in message):
                return message
            self.notifications.append(message)

    def result(self, method, params=None):
        message = self.call(method, params)
        if "result" not in message:
            raise AssertionError("expected a result for %s, got %s" % (method, message))
        return message["result"]

    def error(self, method, params=None):
        message = self.call(method, params)
        if "error" not in message:
            raise AssertionError("expected an error for %s, got %s" % (method, message))
        return message["error"]

    def tool(self, name, arguments):
        return self.result("tools/call", {"name": name, "arguments": arguments})

    def close(self):
        try:
            self.sock.close()
        except OSError:
            pass


class GateHarness:
    """One real gate process with its fake upstream, private state and audit log in a temp dir."""

    def __init__(self, **overrides):
        self.temp = tempfile.mkdtemp(prefix="margo-gate-")
        self.script = os.path.join(self.temp, "script.json")
        self.log = os.path.join(self.temp, "calls.jsonl")
        self.socket = os.path.join(self.temp, "gate.sock")
        self.audit = os.path.join(self.temp, "audit.jsonl")
        self.state = os.path.join(self.temp, "private")
        os.mkdir(self.state, 0o700)
        self.write_script(base_rules())
        self.deployment_path = make_deployment(self.temp, workiq={"command": [sys.executable, str(FAKE)]},
                                               gate={"socket": self.socket, "audit_log": self.audit}, **overrides)
        self.deployment = md.load_deployment(self.deployment_path)
        self.env = dict(os.environ, MARGO_ALLOW_UNSAFE_STATE_DIR="1", FAKE_WORKIQ_SCRIPT=self.script, FAKE_WORKIQ_LOG=self.log)
        for key in ("MARGO_ACCOUNT", "MARGO_GATE_SOCKET", "MARGO_DEPLOYMENT"):
            self.env.pop(key, None)
        self.process = None

    def write_script(self, rules, identity=None, **extra):
        script = {"identity": identity or {"id": MARGO_OID.upper(), "userPrincipalName": MARGO_PRINCIPAL}, "rules": rules}
        script.update(extra)
        Path(self.script).write_text(json.dumps(script), encoding="utf-8")

    def start(self, extra_args=(), allow_shutdown=True):
        flags = ["--allow-shutdown"] if allow_shutdown else []
        self.process = subprocess.Popen(
            [sys.executable, str(GATE), "serve", "--deployment", self.deployment_path, "--account", MARGO_PRINCIPAL,
             "--state-dir", self.state, "--upstream-timeout", "0.8", "--startup-timeout", "30", *flags, *extra_args],
            env=self.env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        deadline = time.time() + 30
        while not os.path.exists(self.socket):
            if self.process.poll() is not None:
                raise RuntimeError("gate exited during startup: " + self.process.stderr.read())
            if time.time() > deadline:
                raise RuntimeError("gate did not bind its socket")
            time.sleep(0.05)
        return self

    def log_size(self):
        return len(Path(self.log).read_text(encoding="utf-8").splitlines()) if os.path.exists(self.log) else 0

    def calls(self, since=0, tool=None):
        lines = Path(self.log).read_text(encoding="utf-8").splitlines() if os.path.exists(self.log) else []
        calls = [json.loads(line) for line in lines[since:]]
        calls = [call for call in calls if call.get("method") == "tools/call"]
        return [call for call in calls if tool is None or call.get("tool") == tool]

    def audit_lines(self):
        return [json.loads(line) for line in Path(self.audit).read_text(encoding="utf-8").splitlines()]

    def client(self):
        return Client(self.socket)

    def ledger(self):
        return Ledger(MARGO_PRINCIPAL, self.state)

    def stop(self):
        if self.process is not None and self.process.poll() is None:
            try:
                client = self.client()
                client.call("margo/shutdown")
                client.close()
            except Exception:
                pass
            try:
                self.process.wait(5)
            except subprocess.TimeoutExpired:
                self.process.terminate()  # a gate started without --allow-shutdown stops on SIGTERM
                try:
                    self.process.wait(10)
                except subprocess.TimeoutExpired:
                    self.process.kill()
                    self.process.wait(5)
        if self.process is not None:
            self.process.stdout.close()
            self.process.stderr.close()
        shutil.rmtree(self.temp, ignore_errors=True)


# ---------------------------------------------------------------------------
# Pure policy
# ---------------------------------------------------------------------------

class PolicyFixture(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.mkdtemp(prefix="margo-gate-policy-")
        cls.deployment = md.load_deployment(make_deployment(cls.temp))

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.temp, ignore_errors=True)


class ClassifierTests(PolicyFixture):
    def tier(self, tool, arguments):
        return gate.classify(tool, arguments, self.deployment)

    def test_every_row_of_the_tier_table(self):
        manager_path = "/users/%s/messages/m1" % MANAGER_PRINCIPAL
        rows = [
            ("T0", "fetch", {"path": "/me/messages"}),
            ("T0", "retrieve", {"query": "fictional"}),
            # T3: destructive or security-relevant
            ("T3", "do_action", {"path": "/me/messages/m1", "method": "DELETE"}),
            ("T3", "delete_entity", {"path": "/me/messages/m1"}),
            ("T3", "do_action", {"path": "/me/messages/m1", "action": "Delete"}),
            ("T3", "do_action", {"path": "/me/events/e1", "method": "POST", "action": "cancel"}),
            ("T3", "do_action", {"path": "/me/messages/m1", "action": "permanentDelete"}),
            ("T3", "create_entity", {"path": "/me/mailFolders/inbox/messageRules", "method": "POST", "body": {"displayName": "x"}}),
            ("T3", "update_entity", {"path": "/me/inboxRules/r1", "method": "PATCH", "body": {"isEnabled": False}}),
            ("T3", "do_action", {"path": "/me/drive/items/i1/permissions/p1", "method": "GET"}),
            ("T3", "do_action", {"path": "/groups/g1/members/$ref", "method": "POST"}),
            ("T3", "do_action", {"path": "/me/events/e1/cancel", "method": "POST"}),
            ("T3", "create_entity", {"path": "/subscriptions", "method": "POST"}),
            ("T3", "update_entity", {"path": "/me/mailboxSettings", "method": "PATCH", "body": {"automaticRepliesSetting": {}}}),
            ("T3", "update_entity", {"path": "/me/mailFolders/f1", "method": "PATCH", "body": {"displayName": "x"}}),
            ("T3", "do_action", {"path": "/me/drive/items/i1/content", "method": "PUT"}),
            ("T3", "update_entity", {"path": "/me/drive/items/i1", "method": "PATCH", "body": {"name": "x"}}),
            ("T3", "do_action", {"path": "/me/messages/m1/move", "method": "POST", "body": {"destinationId": "DeletedItems"}}),
            ("T3", "do_action", {"path": "/me/messages/m1/move", "method": "POST", "body": {"DestinationId": "recoverableitemsdeletions"}}),
            ("T3", "update_entity", {"path": "/users/" + MANAGER_OID, "method": "PATCH", "body": {"jobTitle": "x"}}),
            ("T3", "do_action", {"path": "/me/messages/m1/deletedItems", "method": "POST"}),
            # T2: communicating
            ("T2", "do_action", {"path": "/me/sendMail", "method": "POST", "body": {"message": {}}}),
            ("T2", "do_action", {"path": "/me/messages/m1/send", "method": "POST"}),
            ("T2", "do_action", {"path": "/me/messages/m1/reply", "method": "POST", "body": {"comment": "x"}}),
            ("T2", "do_action", {"path": "/me/messages/m1/replyAll", "method": "POST"}),
            ("T2", "do_action", {"path": "/me/messages/m1/forward", "method": "POST"}),
            ("T2", "create_entity", {"path": "/chats/19:fictional/messages", "method": "POST", "body": {"body": {"content": "x"}}}),
            ("T2", "create_entity", {"path": "/teams/t1/channels/c1/messages", "method": "POST"}),
            ("T2", "create_entity", {"path": "/chats/19:fictional/messages/m1/replies", "method": "POST"}),
            ("T2", "do_action", {"path": "/me/events/e1/accept", "method": "POST"}),
            ("T2", "do_action", {"path": "/me/events/e1/decline", "method": "POST"}),
            ("T2", "do_action", {"path": "/me/events/e1/tentativelyAccept", "method": "POST"}),
            ("T2", "do_action", {"path": "/me/drive/items/i1/createLink", "method": "POST"}),
            ("T2", "do_action", {"path": "/me/drive/items/i1/invite", "method": "POST"}),
            ("T2", "create_entity", {"path": "/me/onlineMeetings", "method": "POST"}),
            ("T2", "do_action", {"path": "/chats/19:fictional/messages/m1/setReaction", "method": "POST"}),
            ("T2", "do_action", {"path": "/me/presence/setPresence", "method": "POST"}),
            ("T2", "do_action", {"path": "/me/teamwork/installedApps", "method": "POST"}),
            ("T2", "create_entity", {"path": "/me/events", "method": "POST",
                                     "body": {"subject": "sync", "attendees": [{"emailAddress": {"address": "bob@example.com"}}]}}),
            ("T2", "create_entity", {"path": "/me/events", "method": "POST", "body": {"subject": "hold", "isOnlineMeeting": True}}),
            ("T2", "create_entity", {"path": "/me/calendar/events", "method": "POST",
                                     "body": {"subject": "x", "onlineMeeting": {"joinUrl": "https://example.com/j"}}}),
            ("T2", "update_entity", {"path": "/me/events/e1", "method": "PATCH",
                                     "body": {"attendees": [{"emailAddress": {"address": "bob@example.com"}}]}}),
            # T1: private and reversible inside margo's or the manager's data
            ("T1", "update_entity", {"path": "/me/messages/m1", "method": "PATCH", "body": {"isRead": True}}),
            ("T1", "update_entity", {"path": "/me/messages/m1", "method": "PATCH", "body": {"flag": {"flagStatus": "flagged"}, "categories": ["Red"]}}),
            ("T1", "update_entity", {"path": manager_path, "method": "PATCH", "body": {"isRead": True}}),
            ("T1", "update_entity", {"path": "/users/%s/messages/m1" % MANAGER_OID, "method": "PATCH", "body": {"categories": []}}),
            ("T1", "do_action", {"path": "/me/messages/m1/move", "method": "POST", "body": {"destinationId": "AAMkReading"}}),
            ("T1", "create_entity", {"path": "/me/messages", "method": "POST", "body": {"subject": "draft"}}),
            ("T1", "create_entity", {"path": "/me/mailFolders/drafts/messages", "method": "POST", "body": {"subject": "draft"}}),
            ("T1", "update_entity", {"path": "/me/messages/m1", "method": "PATCH", "body": {"isDraft": True, "subject": "edited"}}),
            ("T1", "update_entity", {"path": "/me/mailFolders/drafts/messages/m1", "method": "PATCH", "body": {"subject": "edited"}}),
            ("T1", "create_entity", {"path": "/me/events", "method": "POST", "body": {"subject": "focus"}}),
            ("T1", "create_entity", {"path": "/me/calendar/events", "method": "POST", "body": {"subject": "focus", "attendees": []}}),
            ("T1", "create_entity", {"path": "/me/todo/lists/l1/tasks", "method": "POST", "body": {"title": "x"}}),
            ("T1", "update_entity", {"path": "/me/todo/lists/l1/tasks/t1", "method": "PATCH", "body": {"status": "completed"}}),
            ("T1", "create_entity", {"path": "/me/mailFolders", "method": "POST", "body": {"displayName": "Reading"}}),
            ("T1", "create_entity", {"path": "/me/mailFolders/inbox/childFolders", "method": "POST", "body": {"displayName": "Reading"}}),
            ("T1", "update_entity", {"path": "/me/events/e1", "method": "PATCH", "body": {"showAs": "busy", "categories": ["Focus"]}}),
            ("T1", "create_entity", {"url": "https://graph.microsoft.com/v1.0/me/messages", "httpMethod": "post", "payload": {"subject": "d"}}),
            # T3 by default: unclassified or outside scope
            ("T3", "update_entity", {"path": "/me/messages/m1", "method": "PATCH", "body": {"isRead": True, "subject": "x"}}),
            ("T3", "update_entity", {"path": "/me/messages/m1", "method": "PATCH", "body": {}}),
            ("T3", "update_entity", {"path": "/me/events/e1", "method": "PATCH", "body": {"subject": "renamed"}}),
            ("T3", "create_entity", {"path": "/me/messages", "method": "POST", "body": {"subject": "x", "note": "then sendMail"}}),
            ("T3", "update_entity", {"path": "/users/bob@example.com/messages/m1", "method": "PATCH", "body": {"isRead": True}}),
            ("T3", "create_entity", {"path": "/users/bob@example.com/events", "method": "POST", "body": {"subject": "hold"}}),
            ("T3", "do_action", {"path": "/me/messages/m1/createReply", "method": "POST"}),
            ("T3", "do_action", {"method": "POST", "body": {"x": 1}}),
            ("T3", "do_action", {"path": 42, "method": "POST"}),
            ("T3", "create_entity", {"path": "/me/messages/m1"}),
            ("T3", "mystery_tool", {"path": "/me/messages"}),
            ("T3", "admin_reset", {}),
        ]
        for expected, tool, arguments in rows:
            with self.subTest(tool=tool, arguments=arguments):
                decision = self.tier(tool, arguments)
                self.assertEqual(decision["tier"], expected, decision)
                self.assertEqual(set(decision), {"tier", "reason", "path", "method", "scope"})

    def test_default_reason_and_details(self):
        decision = self.tier("do_action", {"path": "/me/messages/m1/createReply", "method": "post"})
        self.assertEqual((decision["tier"], decision["reason"]), ("T3", "unclassified write"))
        self.assertEqual((decision["path"], decision["method"], decision["scope"]), ("/me/messages/m1/createreply", "POST", "margo"))
        decision = self.tier("update_entity", {"path": "/users/%s/messages/m1?$select=id" % MANAGER_PRINCIPAL.upper(),
                                               "method": "PATCH", "body": {"isRead": True}})
        self.assertEqual((decision["tier"], decision["scope"]), ("T1", "manager"))
        self.assertEqual(self.tier("fetch", {"path": "/me/messages"})["path"], "/me/messages")
        self.assertEqual(self.tier("mystery_tool", {"path": "/me"})["reason"], "unknown tool")
        self.assertEqual(self.tier("do_action", {"method": "POST"})["reason"], "write without a path")

    def test_extract_call_uses_first_present_key_and_normalizes(self):
        path, method, body, action = gate.extract_call(
            {"url": "https://graph.microsoft.com/beta/Me/Messages/M1", "verb": "patch", "entity": {"isRead": True},
             "operation": " Delete "}, self.deployment)
        self.assertEqual((path, method, body, action), ("/me/messages/m1", "PATCH", {"isRead": True}, "delete"))
        self.assertEqual(gate.extract_call({"path": None, "url": "/me"}, self.deployment)[0], None)
        self.assertEqual(gate.extract_call("not an object", self.deployment), (None, None, None, None))
        self.assertEqual(gate.extract_call({"path": "me/messages"}, self.deployment)[0], "/me/messages")

    def test_preread_path(self):
        self.assertEqual(gate.preread_path("/me/messages/m1/move", "POST"), "/me/messages/m1")
        self.assertEqual(gate.preread_path("/me/messages/m1?$select=id", "PATCH"), "/me/messages/m1")
        self.assertEqual(gate.preread_path("/me/messages", "POST"), "/me/messages?$top=1&$select=id")


class PreflightAndReceiptTests(PolicyFixture):
    def test_fingerprint_without_resource_is_pure(self):
        target = {"path": "/me/sendmail", "recipients": ["bob@example.com"]}
        self.assertEqual(gate.fingerprint(target, None), gate.fingerprint(dict(target), None))
        self.assertNotEqual(gate.fingerprint(target, None), gate.fingerprint(dict(target, recipients=["eve@example.com"]), None))
        for bad in ({}, None, "x"):
            with self.assertRaises(StateError):
                gate.fingerprint(bad, None)

    def test_fingerprint_reads_the_resource_and_fails_closed(self):
        seen = []

        def fetch(path):
            seen.append(path)
            return {"id": "m1", "changeKey": "ck-1"}

        target = {"resource": "/me/messages/m1", "path": "/me/messages/m1/reply"}
        first = gate.fingerprint(target, fetch)
        self.assertEqual(seen, ["/me/messages/m1?$select=id,changeKey,lastModifiedDateTime"])
        self.assertNotEqual(first, gate.fingerprint(target, lambda path: {"id": "m1", "changeKey": "ck-2"}))
        self.assertNotEqual(first, gate.fingerprint(target, lambda path: None))  # gone
        self.assertEqual(gate.fingerprint(target, lambda path: {"id": "m1", "lastModifiedDateTime": "2026-10-11T00:00:00Z"}),
                         gate.fingerprint(target, lambda path: {"id": "m1", "lastModifiedDateTime": "2026-10-11T00:00:00Z"}))

        def failing(path):
            raise RuntimeError("socket closed")

        with self.assertRaisesRegex(StateError, "preflight read failed; target state unknown"):
            gate.fingerprint(target, failing)
        with self.assertRaises(StateError):
            gate.fingerprint({"resource": "  "}, fetch)

    def test_outcome_classification_only_trusts_a_clear_non_retry_4xx(self):
        ok = {"content": [{"type": "text", "text": "{}"}], "isError": False}
        self.assertEqual(gate.outcome_of(ok), "succeeded")
        cases = {
            "HTTP 404 Not Found: the message does not exist": "failed",
            "Request failed with status code 403": "failed",
            "400 Bad Request": "failed",
            "status: 401 unauthorized": "failed",
            "HTTP 409 Conflict": "outcome_unknown",
            "HTTP 408 Request Timeout": "outcome_unknown",
            "HTTP 429 Too Many Requests": "outcome_unknown",
            "HTTP 500 Internal Server Error": "outcome_unknown",
            "HTTP 502 Bad Gateway": "outcome_unknown",
            "something went wrong": "outcome_unknown",
            "status 404 then retried with status 500": "outcome_unknown",  # ambiguous
            "item 4040 not valid": "outcome_unknown",
        }
        for text, expected in cases.items():
            with self.subTest(text=text):
                result = {"isError": True, "content": [{"type": "text", "text": text}]}
                self.assertEqual(gate.outcome_of(result), expected)
        structured = {"isError": True, "content": [], "structuredContent": {"error": {"statusCode": 404}}}
        self.assertEqual(gate.outcome_of(structured), "failed")
        self.assertEqual(gate.outcome_of({"isError": True, "content": [], "structuredContent": {"status": "409"}}), "outcome_unknown")
        self.assertEqual(gate.outcome_of(None), "outcome_unknown")

    def test_receipt_shape_matches_the_ledger_rules(self):
        from work_ledger import Ledger as Journal
        result = {"isError": False, "content": []}
        stamp = datetime.now(timezone.utc).isoformat()
        receipt = gate.receipt_for("do_action", 7, result, "succeeded", stamp)
        self.assertEqual(receipt["reference"], "workiq-gate:do_action:7")
        self.assertEqual(receipt["result_digest"], gate.digest(result))
        Journal.validate_receipt(receipt, "succeeded")
        failed = gate.receipt_for("do_action", 8, result, "failed", stamp)
        self.assertTrue(failed["definitive_no_effect"])
        Journal.validate_receipt(failed, "failed")
        with self.assertRaises(StateError):
            Journal.validate_receipt(gate.receipt_for("do_action", 9, result, "succeeded", stamp), "failed")


class RoleAndTemplateTests(PolicyFixture):
    def test_roles_from_peer_uid(self):
        self.assertEqual(gate.resolve_roles(1001, {1001: "dana"}, 2000), ["manager"])
        self.assertEqual(gate.resolve_roles(2000, {1001: "dana"}, 2000), ["service"])
        self.assertEqual(gate.resolve_roles(1001, {1001: "dana"}, 1001), ["manager", "service"])
        self.assertEqual(gate.resolve_roles(3000, {1001: "dana"}, 2000), [])
        self.assertEqual(gate.resolve_roles(None, {1001: "dana"}, 2000), [])
        self.assertEqual(gate.resolve_roles(True, {1: "root"}, 2000), [])

    def test_template_rendering_and_refusal_shape(self):
        template = self.deployment["workiq"]["call_templates"]["teams_message"]["arguments"]
        rendered = gate.render_template(template, {"chat_id": CHAT_ID, "text": "hello {text}"})
        self.assertEqual(rendered["path"], "/chats/%s/messages" % CHAT_ID)
        self.assertEqual(rendered["body"]["body"]["content"], "hello {text}")
        self.assertEqual(gate.refusal("x"), {"isError": True, "content": [{"type": "text", "text": "margo-gate: x"}]})

    def test_tool_result_json(self):
        self.assertEqual(gate.tool_result_json({"content": [{"type": "text", "text": "{\"id\": 1}"}]}), {"id": 1})
        self.assertEqual(gate.tool_result_json({"structuredContent": {"id": 2}, "content": []}), {"id": 2})
        for bad in ({"isError": True, "content": [{"type": "text", "text": "HTTP 500"}]},
                    {"content": [{"type": "text", "text": "not json"}]}, None):
            with self.assertRaises(StateError):
                gate.tool_result_json(bad)


# ---------------------------------------------------------------------------
# The real gate over a socket
# ---------------------------------------------------------------------------

@unittest.skipUnless(LINUX, "the gate needs linux peer credentials (SO_PEERCRED)")
class GateServerTests(unittest.TestCase):
    """Manager-capable caller (cli_logins = current login), Teams and email enabled."""

    @classmethod
    def setUpClass(cls):
        cls.env = patch.dict(os.environ, {"MARGO_ALLOW_UNSAFE_STATE_DIR": "1"})
        cls.env.start()
        cls.harness = GateHarness().start()

    @classmethod
    def tearDownClass(cls):
        cls.harness.stop()
        cls.env.stop()

    def setUp(self):
        self.harness.write_script(base_rules())
        self.client = self.harness.client()
        self.addCleanup(self.client.close)

    # -- helpers ------------------------------------------------------------------

    def propose(self, tool, arguments, target, target_fingerprint):
        ledger = self.harness.ledger()
        try:
            ref = ledger.source("mail", "inbox", "message-" + uuid.uuid4().hex[:8], "v1",
                                {"quote": "Please send the fictional update."}, "https://example.com/mail/1")
            return ledger.propose({"kind": "mail.send", "target": target, "payload": {"tool": tool, "arguments": arguments},
                                   "why": "fictional scenario", "source_refs": [ref],
                                   "target_fingerprint": target_fingerprint, "work_item_id": None})
        finally:
            ledger.close()

    def show(self, action_id):
        ledger = self.harness.ledger()
        try:
            return ledger.show(action_id)
        finally:
            ledger.close()

    def approve_via_teams(self, action, extra=()):
        ref = md.approval_ref(action["action_hash"])
        self.harness.write_script(base_rules(messages=[teams_message("approve " + ref)], extra=extra))
        report = self.client.result("margo/directives/sync")
        self.assertEqual(len(report["approvals"]), 1, report)
        self.assertEqual(report["approvals"][0]["action_id"], action["id"])
        return report

    def directive_via_teams(self, text):
        self.harness.write_script(base_rules(messages=[teams_message(text)]))
        report = self.client.result("margo/directives/sync")
        self.assertEqual(len(report["new"]), 1, report)
        return report["new"][0]

    @staticmethod
    def refused(result):
        text = result.get("content", [{}])[0].get("text", "")
        return bool(result.get("isError")) and text.startswith("margo-gate: ")

    # -- protocol ---------------------------------------------------------------------

    def test_initialize_and_filtered_tool_list(self):
        init = self.client.result("initialize", {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "t"}})
        self.assertEqual(init["serverInfo"]["name"], "margo-workiq-gate")
        self.assertEqual(init["protocolVersion"], "2024-11-05")
        self.assertIn("tools", init["capabilities"])
        self.client.sock.sendall(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        tools = self.client.result("tools/list")["tools"]
        names = [tool["name"] for tool in tools]
        self.assertNotIn("admin_reset", names)
        self.assertEqual(set(names), set(self.harness.deployment["workiq"]["read_tools"])
                         | set(self.harness.deployment["workiq"]["write_tools"]))
        for tool in tools:
            if tool["name"] in self.harness.deployment["workiq"]["write_tools"]:
                self.assertTrue(tool["description"].startswith(gate.WRITE_PREFIX), tool)
            else:
                self.assertFalse(tool["description"].startswith("[margo-gate]"), tool)
        self.assertEqual(self.client.result("ping"), {})
        self.assertEqual(self.client.error("resources/write")["code"], -32601)
        self.assertEqual(self.client.error("tools/call", {"name": "fetch", "arguments": "nope"})["code"], -32602)
        self.client.sock.sendall(b"this is not json\n")
        self.assertEqual(self.client.read()["error"]["code"], -32700)
        # The fake initialize ran exactly once, by the gate, however many clients connected.
        initializations = [line for line in Path(self.harness.log).read_text().splitlines() if '"initialize"' in line]
        self.assertEqual(len(initializations), 1)

    def test_reads_are_forwarded_unchanged_and_ids_remapped(self):
        self.harness.write_script(base_rules(extra=[{"tool": "fetch", "path": "^/me/messages/m1", "result": {"id": "m1", "isRead": False}}]))
        mark = self.harness.log_size()
        message = self.client.call("tools/call", {"name": "fetch", "arguments": {"path": "/me/messages/m1", "margo_directive": "x"}},
                                   request_id="client-id-9")
        self.assertEqual(message["id"], "client-id-9")
        self.assertEqual(message["result"]["structuredContent"], {"id": "m1", "isRead": False})
        calls = self.harness.calls(mark)
        self.assertEqual([(call["tool"], call["arguments"]) for call in calls],
                         [("fetch", {"path": "/me/messages/m1", "margo_directive": "x"})])
        self.assertNotEqual(calls[0]["id"], "client-id-9")

    def test_upstream_notifications_reach_clients(self):
        self.harness.write_script(base_rules(extra=[{"tool": "fetch", "path": "^/me/noisy", "result": {"ok": True},
                                                     "notify": {"method": "notifications/message",
                                                                "params": {"level": "info", "data": "fixture"}}}]))
        self.client.tool("fetch", {"path": "/me/noisy"})
        self.assertIn({"jsonrpc": "2.0", "method": "notifications/message", "params": {"level": "info", "data": "fixture"}},
                      self.client.notifications)

    # -- T1 ----------------------------------------------------------------------------

    def test_t1_requires_an_active_directive_and_journals_before_and_after(self):
        arguments = {"path": "/me/messages/m1", "method": "PATCH", "body": {"isRead": True}}
        mark = self.harness.log_size()
        self.assertTrue(self.refused(self.client.tool("update_entity", arguments)))
        self.assertTrue(self.refused(self.client.tool("update_entity", dict(arguments, margo_directive="dir_nope"))))
        self.assertTrue(self.refused(self.client.tool("update_entity", dict(arguments, margo_directive="d", margo_rule="r"))))
        self.assertEqual(self.harness.calls(mark), [], "a refused T1 write must not reach upstream")
        directive = self.directive_via_teams("mark the fictional newsletter as read")
        self.assertEqual(directive["kind"], "instruction")
        self.harness.write_script(base_rules(extra=[
            {"tool": "fetch", "path": "^/me/messages/m1", "result": {"id": "m1", "isRead": False}},
            {"tool": "update_entity", "path": "^/me/messages/m1", "result": {"id": "m1", "isRead": True}}]))
        mark = self.harness.log_size()
        result = self.client.tool("update_entity", dict(arguments, margo_directive=directive["id"]))
        self.assertFalse(result.get("isError"), result)
        calls = self.harness.calls(mark)
        self.assertEqual([call["tool"] for call in calls], ["fetch", "update_entity"])
        self.assertEqual(calls[1]["arguments"], arguments, "margo_* keys are stripped before forwarding")
        listed = self.client.result("margo/directives/list", {"state": "active"})["directives"]
        recorded = next(item for item in listed if item["id"] == directive["id"])
        self.assertEqual(len(recorded["effects"]), 1)
        self.assertEqual(recorded["effects"][0]["before"], {"id": "m1", "isRead": False})
        self.assertEqual(recorded["effects"][0]["after"]["structuredContent"], {"id": "m1", "isRead": True})
        # A completed directive no longer authorizes anything.
        self.client.result("margo/directives/complete", {"id": directive["id"], "summary": "done in test"})
        mark = self.harness.log_size()
        self.assertTrue(self.refused(self.client.tool("update_entity", dict(arguments, margo_directive=directive["id"]))))
        self.assertEqual(self.harness.calls(mark), [])

    def test_t1_pre_read_failure_refuses_the_write(self):
        directive = self.directive_via_teams("file the fictional newsletter")
        arguments = {"path": "/me/messages/m404/move", "method": "POST", "body": {"destinationId": "AAMkReading"}}
        self.harness.write_script(base_rules(extra=[{"tool": "do_action", "path": "/move$", "result": {"id": "moved"}}]))
        mark = self.harness.log_size()
        result = self.client.tool("do_action", dict(arguments, margo_directive=directive["id"]))
        self.assertTrue(self.refused(result), result)
        self.assertIn("pre-read", result["content"][0]["text"])
        calls = self.harness.calls(mark)
        self.assertEqual([call["tool"] for call in calls], ["fetch"], "the write must not be forwarded")
        self.assertEqual(calls[0]["arguments"], {"path": "/me/messages/m404"})
        # Upstream silence on the pre-read is a failure too, not a pass.
        self.harness.write_script(base_rules(extra=[{"tool": "fetch", "path": "^/me/messages/m404", "hang": True}]))
        result = self.client.tool("do_action", dict(arguments, margo_directive=directive["id"]))
        self.assertEqual(result.get("isError"), True)

    def test_standing_rule_authorizes_until_revoked_and_pause_blocks_t1(self):
        self.harness.write_script(base_rules(messages=[teams_message("always file fictional newsletters into Reading")]))
        report = self.client.result("margo/directives/sync")
        self.assertEqual(report["new"][0]["kind"], "standing_rule")
        rules = self.client.result("margo/rules/list")["rules"]
        rule = next(item for item in rules if item["directive_id"] == report["new"][0]["id"])
        arguments = {"path": "/me/messages/m2/move", "method": "POST", "body": {"destinationId": "AAMkReading"}}
        self.harness.write_script(base_rules(extra=[
            {"tool": "fetch", "path": "^/me/messages/m2", "result": {"id": "m2", "parentFolderId": "inbox"}},
            {"tool": "do_action", "path": "^/me/messages/m2/move", "result": {"id": "m2", "parentFolderId": "AAMkReading"}}]))
        result = self.client.tool("do_action", dict(arguments, margo_rule=rule["id"]))
        self.assertFalse(result.get("isError"), result)
        paused = self.client.result("margo/pause")
        self.assertTrue(paused["paused"])
        self.assertTrue(self.client.result("margo/health")["paused"])
        result = self.client.tool("do_action", dict(arguments, margo_rule=rule["id"]))
        self.assertTrue(self.refused(result) and "paused" in result["content"][0]["text"], result)
        self.assertFalse(self.client.result("margo/resume")["paused"])
        revoked = self.client.result("margo/rules/revoke", {"rule_id": rule["id"]})
        self.assertEqual(revoked["rule"]["state"], "revoked")
        self.assertEqual(revoked["directive"]["channel"], "cli")
        mark = self.harness.log_size()
        result = self.client.tool("do_action", dict(arguments, margo_rule=rule["id"]))
        self.assertTrue(self.refused(result), result)
        self.assertEqual(self.harness.calls(mark), [])
        self.assertEqual(self.client.error("margo/rules/revoke", {"rule_id": rule["id"][:-1]})["code"], -32602)

    # -- T2/T3 ----------------------------------------------------------------------

    def test_t2_end_to_end_exact_approval_single_use_and_receipt(self):
        arguments = {"path": "/me/sendMail", "method": "POST",
                     "body": {"message": {"subject": "Fictional update", "toRecipients": [{"emailAddress": {"address": "bob@example.com"}}],
                                          "body": {"contentType": "text", "content": "FICTIONAL-BODY-7731"}}}}
        target = {"path": "/me/sendMail", "recipients": ["bob@example.com"]}
        action = self.propose("do_action", arguments, target, gate.fingerprint(target, None))
        grant = {"action_id": action["id"], "revision": 1, "action_hash": action["action_hash"]}
        mark = self.harness.log_size()
        for bad in (arguments, dict(arguments, margo_action=grant), dict(arguments, margo_action={"action_id": action["id"]}),
                    dict(arguments, margo_action=dict(grant, action_hash="0" * 64))):
            result = self.client.tool("do_action", bad)
            self.assertTrue(self.refused(result), result)
        self.assertEqual(self.harness.calls(mark), [], "nothing reaches upstream before an approval")
        pending = self.client.result("margo/pending")["actions"]
        ref = md.approval_ref(action["action_hash"])
        self.assertIn(ref, [item["ref"] for item in pending])
        entry = next(item for item in pending if item["ref"] == ref)
        self.assertEqual((entry["state"], entry["expires_at"], entry["kind"]), ("ready", None, "mail.send"))
        self.approve_via_teams(action, extra=[{"tool": "do_action", "path": "^/me/sendmail", "result": {}}])
        self.assertEqual(self.show(action["id"])["state"], "approved")
        # A payload or path that differs from the approved revision is refused even with a valid grant.
        altered = dict(arguments, body={"message": {"subject": "Changed"}})
        self.assertTrue(self.refused(self.client.tool("do_action", dict(altered, margo_action=grant))))
        mark = self.harness.log_size()
        result = self.client.tool("do_action", dict(arguments, margo_action=grant))
        self.assertFalse(result.get("isError"), result)
        calls = self.harness.calls(mark)
        self.assertEqual([(call["tool"], call["arguments"]) for call in calls], [("do_action", arguments)])
        shown = self.show(action["id"])
        self.assertEqual(shown["state"], "succeeded")
        receipt = shown["executions"][0]["receipt"]
        self.assertEqual((receipt["kind"], receipt["outcome"]), ("tool_result", "succeeded"))
        self.assertTrue(receipt["reference"].startswith("workiq-gate:do_action:"))
        self.assertEqual(receipt["result_digest"], gate.digest(result))
        history = self.harness.ledger()
        try:
            approved = [event for event in history.history(action["id"])["events"] if event["event"] == "approved"]
        finally:
            history.close()
        self.assertTrue(approved[0]["data"]["evidence"]["evidence_ref"].startswith("manager-channel:teams:"))
        # The approval is consumed: a replay is refused and never forwarded.
        mark = self.harness.log_size()
        replay = self.client.tool("do_action", dict(arguments, margo_action=grant))
        self.assertTrue(self.refused(replay), replay)
        self.assertEqual(self.harness.calls(mark), [])
        audit = Path(self.harness.audit).read_text(encoding="utf-8")
        self.assertNotIn("FICTIONAL-BODY-7731", audit)
        self.assertNotIn("bob@example.com", audit)
        forwarded = [line for line in self.harness.audit_lines()
                     if line.get("tier") == "T2" and line.get("decision") == "forwarded" and line.get("action_id") == action["id"]]
        self.assertEqual(len(forwarded), 1)
        self.assertEqual(forwarded[0]["outcome"], "succeeded")
        self.assertTrue(forwarded[0]["receipt_recorded"])

    def test_changed_target_invalidates_the_approval(self):
        arguments = {"path": "/me/messages/m9/reply", "method": "POST", "body": {"comment": "fictional"}}
        target = {"path": "/me/messages/m9/reply", "resource": "/me/messages/m9"}
        self.harness.write_script(base_rules(extra=[{"tool": "fetch", "path": "^/me/messages/m9", "result": {"id": "m9", "changeKey": "ck-1"}}]))
        proposed_fp = self.client.result("margo/preflight", {"target": target})["target_fingerprint"]
        self.assertEqual(proposed_fp, gate.fingerprint(target, lambda path: {"id": "m9", "changeKey": "ck-1"}))
        action = self.propose("do_action", arguments, target, proposed_fp)
        self.approve_via_teams(action, extra=[{"tool": "fetch", "path": "^/me/messages/m9", "result": {"id": "m9", "changeKey": "ck-2"}},
                                              {"tool": "do_action", "path": "/reply$", "result": {}}])
        mark = self.harness.log_size()
        result = self.client.tool("do_action", dict(arguments, margo_action={"action_id": action["id"], "revision": 1,
                                                                            "action_hash": action["action_hash"]}))
        self.assertTrue(self.refused(result), result)
        self.assertIn("target changed", result["content"][0]["text"])
        self.assertEqual([call["tool"] for call in self.harness.calls(mark)], ["fetch"])
        self.assertEqual(self.show(action["id"])["state"], "stale")

    def test_definitive_failure_and_unknown_outcomes_are_recorded_truthfully(self):
        cases = [
            ({"tool": "do_action", "path": "^/me/sendmail", "error": "HTTP 404 Not Found: mailbox folder missing"}, "failed", True),
            ({"tool": "do_action", "path": "^/me/sendmail", "error": "HTTP 409 Conflict"}, "outcome_unknown", False),
            ({"tool": "do_action", "path": "^/me/sendmail", "hang": True}, "outcome_unknown", False),
            ({"tool": "do_action", "path": "^/me/sendmail", "rpc_error": {"code": -32603, "message": "boom"}}, "outcome_unknown", False),
        ]
        for rule, expected, definitive in cases:
            with self.subTest(rule=rule):
                arguments = {"path": "/me/sendMail", "method": "POST", "body": {"message": {"subject": uuid.uuid4().hex}}}
                target = {"path": "/me/sendMail", "recipients": ["bob@example.com"]}
                action = self.propose("do_action", arguments, target, gate.fingerprint(target, None))
                self.approve_via_teams(action, extra=[rule])
                started = time.time()
                result = self.client.tool("do_action", dict(arguments, margo_action={
                    "action_id": action["id"], "revision": 1, "action_hash": action["action_hash"]}))
                self.assertTrue(result.get("isError"), result)
                shown = self.show(action["id"])
                self.assertEqual(shown["state"], expected)
                receipt = shown["executions"][0]["receipt"]
                self.assertEqual(receipt["outcome"], expected)
                self.assertEqual(receipt.get("definitive_no_effect", False), definitive)
                if rule.get("hang"):
                    self.assertGreaterEqual(time.time() - started, 0.5)
                    self.assertIn("do not retry", result["content"][0]["text"])
                # The replay is refused: an unknown outcome is never retried blindly.
                mark = self.harness.log_size()
                self.assertTrue(self.refused(self.client.tool("do_action", dict(arguments, margo_action={
                    "action_id": action["id"], "revision": 1, "action_hash": action["action_hash"]}))))
                self.assertEqual(self.harness.calls(mark), [])
        # The gate is still healthy after a hung upstream call.
        self.assertFalse(self.client.tool("fetch", {"path": "/me?$select=id"}).get("isError"))

    def test_t3_needs_the_same_exact_approval(self):
        arguments = {"path": "/me/messages/m3", "method": "DELETE"}
        mark = self.harness.log_size()
        result = self.client.tool("delete_entity", arguments)
        self.assertTrue(self.refused(result))
        self.assertIn("T3", result["content"][0]["text"])
        self.assertEqual(self.harness.calls(mark), [])
        target = {"path": "/me/messages/m3"}
        action = self.propose("delete_entity", arguments, target, gate.fingerprint(target, None))
        self.approve_via_teams(action, extra=[{"tool": "delete_entity", "path": "^/me/messages/m3", "result": {}}])
        result = self.client.tool("delete_entity", dict(arguments, margo_action={
            "action_id": action["id"], "revision": 1, "action_hash": action["action_hash"]}))
        self.assertFalse(result.get("isError"), result)
        self.assertEqual(self.show(action["id"])["state"], "succeeded")

    # -- control methods ----------------------------------------------------------------

    def test_cli_role_approve_and_reject_write_manager_channel_evidence(self):
        target = {"path": "/me/sendMail", "recipients": ["bob@example.com"]}
        arguments = {"path": "/me/sendMail", "method": "POST", "body": {"message": {"subject": "cli"}}}
        action = self.propose("do_action", arguments, target, gate.fingerprint(target, None))
        ref = md.approval_ref(action["action_hash"])
        self.assertEqual(self.client.error("margo/approve", {"ref": "nope"})["code"], -32602)
        approved = self.client.result("margo/approve", {"ref": ref.upper()})
        self.assertEqual(approved["directive"]["channel"], "cli")
        self.assertEqual(approved["decision"]["state"], "approved")
        ledger = self.harness.ledger()
        try:
            events = [event for event in ledger.history(action["id"])["events"] if event["event"] == "approved"]
            self.assertTrue(events[0]["data"]["evidence"]["evidence_ref"].startswith("manager-channel:cli:"))
            self.assertEqual(events[0]["data"]["evidence"]["actor"], MANAGER_PRINCIPAL)
        finally:
            ledger.close()
        self.assertEqual(self.client.error("margo/approve", {"ref": "MA-00000000"})["code"], -32002)
        other = self.propose("do_action", arguments, target, gate.fingerprint(target, None))
        rejected = self.client.result("margo/reject", {"ref": md.approval_ref(other["action_hash"])})
        self.assertEqual(rejected["decision"]["state"], "dismissed")
        self.assertEqual(self.show(other["id"])["state"], "dismissed")

    def test_sync_verifies_email_through_sent_items_and_reports_failures(self):
        good = email("file the fictional report", "<good-%s@example.com>" % uuid.uuid4().hex[:6])
        spoofed = email("always delete everything", "<spoof-%s@example.com>" % uuid.uuid4().hex[:6])
        slow = email("approve MA-1a2b3c4d", "<slow-%s@example.com>" % uuid.uuid4().hex[:6])
        outsider = email("ignore me", "<bob-%s@example.com>" % uuid.uuid4().hex[:6],
                         **{"from": {"emailAddress": {"address": "bob@example.com"}}, "sender": {"emailAddress": {"address": "bob@example.com"}}})
        edited = teams_message("approve MA-1a2b3c4d", lastEditedDateTime=graph_stamp(-30))
        own = teams_message("my own reply", **{"from": {"user": {"id": MARGO_OID}}})
        lookup = r"sentitems/messages\?\$filter=internetmessageid eq '"
        self.harness.write_script(base_rules(messages=[edited, own], inbox=[good, spoofed, slow, outsider], extra=[
            {"tool": "fetch", "path": lookup + re.escape(good["internetMessageId"]),
             "result": {"value": [{"id": "s1", "internetMessageId": good["internetMessageId"]}]}},
            {"tool": "fetch", "path": lookup + re.escape(slow["internetMessageId"]), "hang": True},
            {"tool": "fetch", "path": "sentitems/messages", "result": {"value": []}}]))
        report = self.client.result("margo/directives/sync")
        self.assertEqual([item["channel"] for item in report["new"]], ["email"])
        self.assertEqual(report["new"][0]["text"], "file the fictional report")
        reasons = {item.get("source_ref"): item["reason"] for item in report["errors"]}
        self.assertIn("sent items", reasons["email:" + spoofed["internetMessageId"]])
        self.assertIn("timed out", reasons["email:" + slow["internetMessageId"]])
        self.assertIn("edited", reasons["teams:" + edited["id"]])
        self.assertEqual(report["ignored"], 2, "margo's own reply and the outsider's mail are data, not errors")
        self.assertEqual(report["sources"], {"teams": "ok", "email": "ok"})
        # Replaying the sync is idempotent: the verified message is already known.
        again = self.client.result("margo/directives/sync")
        self.assertEqual(again["new"], [])

    def test_notify_posts_only_to_the_control_chat(self):
        self.harness.write_script(base_rules(extra=[{"tool": "create_entity", "path": "/chats/", "result": {"id": "posted"}}]))
        mark = self.harness.log_size()
        sent = self.client.result("margo/notify", {"text": "Fictional approval request MA-1a2b3c4d", "kind": "approval_request"})
        self.assertEqual((sent["channel"], sent["outcome"]), ("teams", "succeeded"))
        calls = self.harness.calls(mark)
        self.assertEqual(len(calls), 1)
        self.assertEqual(calls[0]["arguments"]["path"], "/chats/%s/messages" % CHAT_ID)
        self.assertEqual(calls[0]["arguments"]["body"]["body"]["content"], "Fictional approval request MA-1a2b3c4d")
        self.assertEqual(self.client.error("margo/notify", {"text": "", "kind": "status"})["code"], -32602)
        self.assertEqual(self.client.error("margo/notify", {"text": "x", "kind": "Shout!"})["code"], -32602)
        audit = Path(self.harness.audit).read_text(encoding="utf-8")
        self.assertNotIn("Fictional approval request", audit)

    def test_health_states(self):
        health = self.client.result("margo/health")
        self.assertEqual(health["status"], "connected", health)
        self.assertEqual(set(health["checks"]), {"workiq_identity", "delegated_access", "ledger", "directives"})
        self.assertTrue(health["upstream"]["alive"])
        self.assertEqual(health["upstream"]["tools"], 12)
        self.assertNotIn(MARGO_OID, json.dumps(health))
        self.harness.write_script(base_rules(extra=[{"tool": "fetch", "path": r"/users/[^/]+/calendarview", "error": "HTTP 403 Forbidden: ErrorAccessDenied"}]))
        health = self.client.result("margo/health")
        self.assertEqual(health["status"], "degraded")
        self.assertEqual(health["checks"]["delegated_access"]["surfaces"]["calendar"]["status"], "denied")
        self.harness.write_script(base_rules(extra=[{"tool": "fetch", "path": r"/users/[^/]+/mailfolders/inbox",
                                                     "error": "HTTP 401 Unauthorized: AADSTS50173 interaction_required"}]))
        self.assertEqual(self.client.result("margo/health")["status"], "reauth_required")
        self.harness.write_script(base_rules(), identity={"id": OUTSIDER_OID, "userPrincipalName": "someone@example.org"})
        health = self.client.result("margo/health")
        self.assertEqual(health["status"], "blocked")
        self.assertEqual(health["checks"]["workiq_identity"]["status"], "mismatch")
        self.harness.write_script(base_rules(), identity={"id": OUTSIDER_OID, "userPrincipalName": MARGO_PRINCIPAL.upper()})
        self.assertEqual(self.client.result("margo/health")["status"], "connected", "the UPN alone also proves identity")
        self.harness.write_script(base_rules(extra=[{"tool": "fetch", "path": "^/me\\?", "hang": True}]))
        self.assertEqual(self.client.result("margo/health")["status"], "blocked")


@unittest.skipUnless(LINUX, "the gate needs linux peer credentials (SO_PEERCRED)")
class ServiceOnlyGateTests(unittest.TestCase):
    """The test user is not a configured login: it is only the service uid, and Teams is off."""

    @classmethod
    def setUpClass(cls):
        cls.env = patch.dict(os.environ, {"MARGO_ALLOW_UNSAFE_STATE_DIR": "1"})
        cls.env.start()
        cls.harness = GateHarness(control={"cli_logins": [], "teams_chat_id": None, "max_replies_per_hour": 2}).start()

    @classmethod
    def tearDownClass(cls):
        cls.harness.stop()
        cls.env.stop()

    def setUp(self):
        self.harness.write_script(base_rules())
        self.client = self.harness.client()
        self.addCleanup(self.client.close)

    def test_manager_methods_are_denied_for_a_non_manager_uid(self):
        for method, params in (("margo/approve", {"ref": "MA-1a2b3c4d"}), ("margo/reject", {"ref": "MA-1a2b3c4d"}),
                               ("margo/pause", {}), ("margo/resume", {}), ("margo/rules/revoke", {"rule_id": "rule_" + "0" * 32})):
            with self.subTest(method=method):
                error = self.client.error(method, params)
                self.assertEqual(error["code"], -32001, error)
        health = self.client.result("margo/health")
        self.assertEqual(health["status"], "connected")
        self.assertEqual(health["cli_logins_unknown"], [])
        self.assertEqual(self.client.result("margo/directives/sync")["sources"], {"teams": "disabled", "email": "ok"})
        self.assertEqual(self.client.result("margo/rules/list"), {"rules": []})
        denied = [line for line in self.harness.audit_lines() if line.get("decision") == "denied"]
        self.assertEqual(len(denied), 5)

    def test_notify_falls_back_to_email_and_honours_the_hourly_budget(self):
        self.harness.write_script(base_rules(extra=[{"tool": "do_action", "path": "^/me/sendmail", "result": {}}]))
        mark = self.harness.log_size()
        for index in range(2):
            sent = self.client.result("margo/notify", {"text": "status %d" % index, "kind": "status"})
            self.assertEqual((sent["channel"], sent["outcome"], sent["replies_in_last_hour"]), ("email", "succeeded", index + 1))
        error = self.client.error("margo/notify", {"text": "status 3", "kind": "status"})
        self.assertEqual(error["code"], -32002)
        self.assertIn("rate limit", error["message"])
        calls = self.harness.calls(mark)
        self.assertEqual(len(calls), 2, "the third reply never reaches upstream")
        for call in calls:
            self.assertEqual(call["tool"], "do_action")
            self.assertEqual(call["arguments"]["path"], "/me/sendMail")
            recipients = call["arguments"]["body"]["message"]["toRecipients"]
            self.assertEqual(recipients, [{"emailAddress": {"address": MANAGER_PRINCIPAL}}])


@unittest.skipUnless(LINUX, "the gate needs linux peer credentials (SO_PEERCRED)")
class UpstreamFailureTests(unittest.TestCase):
    def setUp(self):
        self.env = patch.dict(os.environ, {"MARGO_ALLOW_UNSAFE_STATE_DIR": "1"})
        self.env.start()
        self.addCleanup(self.env.stop)

    def test_dead_upstream_blocks_calls_but_state_methods_still_answer(self):
        harness = GateHarness().start()
        self.addCleanup(harness.stop)
        client = harness.client()
        self.addCleanup(client.close)
        harness.write_script(base_rules(extra=[{"tool": "fetch", "path": "^/crash", "crash": True}]))
        error = client.error("tools/call", {"name": "fetch", "arguments": {"path": "/crash"}})
        self.assertEqual(error["code"], -32003)
        deadline = time.time() + 5
        while time.time() < deadline and client.result("margo/health")["upstream"]["alive"]:
            time.sleep(0.05)
        health = client.result("margo/health")
        self.assertEqual((health["status"], health["upstream"]["alive"]), ("blocked", False))
        self.assertEqual(client.error("tools/call", {"name": "fetch", "arguments": {"path": "/me"}})["code"], -32003)
        self.assertEqual(client.result("margo/pending"), {"actions": []})

    def test_upstream_that_fails_to_start_exits_3(self):
        harness = GateHarness()
        self.addCleanup(harness.stop)
        process = subprocess.run(
            [sys.executable, str(GATE), "serve", "--deployment", harness.deployment_path, "--account", MARGO_PRINCIPAL,
             "--state-dir", harness.state, "--startup-timeout", "5",
             "--workiq-command", json.dumps([sys.executable, "-c", "import sys; sys.exit(1)"])],
            env=harness.env, capture_output=True, text=True, timeout=60)
        self.assertEqual(process.returncode, 3, process.stderr)
        self.assertEqual(json.loads(process.stderr)["code"], "upstream_unavailable")
        self.assertFalse(os.path.exists(harness.socket))

    def test_socket_shutdown_is_denied_unless_enabled_and_sigterm_still_stops(self):
        harness = GateHarness().start(allow_shutdown=False)
        self.addCleanup(harness.stop)
        client = harness.client()
        self.addCleanup(client.close)
        error = client.error("margo/shutdown")
        self.assertEqual(error["code"], -32001)
        self.assertIn("SIGTERM", error["message"])
        self.assertEqual(client.result("margo/health")["upstream"]["alive"], True)
        self.assertIsNone(harness.process.poll())
        harness.process.terminate()
        self.assertEqual(harness.process.wait(15), 0)
        self.assertFalse(os.path.exists(harness.socket))

    def test_stdio_client_relays_and_refuses_without_a_gate(self):
        harness = GateHarness().start()
        self.addCleanup(harness.stop)
        request = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2024-11-05"}}) + "\n"
        process = subprocess.run([sys.executable, str(CLIENT), "--socket", harness.socket], input=request,
                                 capture_output=True, text=True, timeout=30, env=harness.env)
        self.assertEqual(process.returncode, 0, process.stderr)
        self.assertEqual(json.loads(process.stdout.splitlines()[0])["result"]["serverInfo"]["name"], "margo-workiq-gate")
        missing = subprocess.run([sys.executable, str(CLIENT), "--socket", os.path.join(harness.temp, "absent.sock")],
                                 input=request, capture_output=True, text=True, timeout=30, env=harness.env)
        self.assertEqual(missing.returncode, 2)
        self.assertIn("cannot reach the gate", missing.stderr)
        self.assertEqual(missing.stdout, "")


class CliTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.mkdtemp(prefix="margo-gate-cli-")
        self.addCleanup(shutil.rmtree, self.temp, True)
        self.deployment = make_deployment(self.temp, gate={"socket": os.path.join(self.temp, "gate.sock"),
                                                           "audit_log": os.path.join(self.temp, "audit.jsonl")},
                                          workiq={"command": [sys.executable, str(FAKE)]})
        self.env = dict(os.environ, MARGO_ALLOW_UNSAFE_STATE_DIR="1")

    def run_cli(self, *args):
        return subprocess.run([sys.executable, str(GATE), *args], capture_output=True, text=True, env=self.env, timeout=60)

    def test_classify_is_pure_and_prints_json(self):
        process = self.run_cli("classify", "--deployment", self.deployment, "--tool", "do_action",
                               "--arguments", json.dumps({"path": "/me/sendMail", "method": "POST"}))
        self.assertEqual(process.returncode, 0, process.stderr)
        self.assertEqual(json.loads(process.stdout)["tier"], "T2")
        process = self.run_cli("classify", "--deployment", self.deployment, "--tool", "do_action", "--arguments", "[]")
        self.assertEqual(process.returncode, 2)
        self.assertEqual(json.loads(process.stderr)["command"], "classify")

    def test_check_config_reports_readiness_without_identifiers(self):
        process = self.run_cli("check-config", "--deployment", self.deployment)
        report = json.loads(process.stdout)
        self.assertEqual(process.returncode, 0 if report["status"] == "ok" else 1, process.stdout)
        self.assertNotIn(MANAGER_OID, process.stdout)
        self.assertNotIn(MARGO_OID, process.stdout)
        self.assertTrue(report["manager_configured"])
        self.assertTrue(report["workiq_command_found"])
        if LINUX:
            self.assertEqual(report["status"], "ok", report)
            self.assertEqual(report["cli_logins"]["resolved"], 1)
        missing_dir = make_deployment(self.temp, path=os.path.join(self.temp, "bad.json"),
                                      gate={"socket": os.path.join(self.temp, "absent", "gate.sock"),
                                            "audit_log": os.path.join(self.temp, "audit.jsonl")},
                                      control={"cli_logins": ["{manager-vm-login}"]})
        process = self.run_cli("check-config", "--deployment", missing_dir)
        self.assertEqual(process.returncode, 1)
        report = json.loads(process.stdout)
        self.assertEqual(report["status"], "problems")
        self.assertTrue(any("socket directory" in item for item in report["problems"]))
        self.assertEqual(report["cli_logins"]["unknown"], ["{manager-vm-login}"])
        process = self.run_cli("check-config", "--deployment", os.path.join(self.temp, "nope.json"))
        self.assertEqual(process.returncode, 2)
        self.assertEqual(json.loads(process.stderr)["command"], "check-config")

    @unittest.skipIf(LINUX, "serve refuses only where peer credentials are missing")
    def test_serve_refuses_without_peer_credentials(self):
        process = self.run_cli("serve", "--deployment", self.deployment, "--account", MARGO_PRINCIPAL,
                               "--state-dir", os.path.join(self.temp, "private"))
        self.assertEqual(process.returncode, 2)
        self.assertIn("peer credentials", json.loads(process.stderr)["error"])


if __name__ == "__main__":
    unittest.main()
