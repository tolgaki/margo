"""Offline tests for the hosted autopilot's Work IQ guard (fictional identifiers only)."""

import importlib
import json
import shutil
import sqlite3
import sys
import unittest
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "skills" / "chief-of-staff" / "scripts"))
sys.path.insert(0, str(ROOT / "autopilot" / "src"))
work = importlib.import_module("work_ledger")
autopilot = importlib.import_module("margo_autopilot")
claims = importlib.import_module("margo_autopilot.claims")
instance = importlib.import_module("margo_autopilot.instance")
replies = importlib.import_module("margo_autopilot.replies")

ATTENDED, UNATTENDED, THIRD_PARTY = autopilot.ATTENDED, autopilot.UNATTENDED, autopilot.THIRD_PARTY
PRINCIPAL_TOKEN, MARGO_TOKEN = "principal-token-fictional", "margo-token-fictional"


def stamp(minutes=0):
    return (datetime.now(timezone.utc) + timedelta(minutes=minutes)).isoformat()


class FakeTokens:
    def __init__(self, missing=()):
        self.missing, self.requested = set(missing), []

    def token_for(self, subject):
        self.requested.append(subject)
        if subject in self.missing:
            raise autopilot.TokenUnavailable("consent revoked")
        return {"principal": PRINCIPAL_TOKEN, "margo": MARGO_TOKEN}[subject]


class FakeTransport:
    endpoint = autopilot.WORKIQ_MCP_URL

    def __init__(self, result=None, error=None):
        self.result = result if result is not None else {"statusCode": 200, "data": {"value": []}}
        self.error, self.calls = error, []

    def call(self, tool, arguments, *, bearer):
        self.calls.append((tool, arguments, bearer))
        if self.error:
            raise self.error
        return self.result


BINDING = autopilot.InstanceBinding(agent_identity_id="agent-identity-1", agent_user_id="margo-user-1",
                                    principal_id="principal-1", principal_chat_id="chat-principal-1")


class GuardPolicyTests(unittest.TestCase):
    def guard(self, **kwargs):
        self.tokens = kwargs.pop("tokens", FakeTokens())
        self.transport = kwargs.pop("transport", FakeTransport())
        return autopilot.WorkIQGuard(BINDING, self.tokens, self.transport, **kwargs)

    def test_only_the_workiq_endpoint_is_accepted(self):
        other = FakeTransport()
        other.endpoint = "https://agent365.example.com/agents/servers/mcp_MailTools"
        with self.assertRaises(autopilot.GuardConfigError):
            autopilot.WorkIQGuard(BINDING, FakeTokens(), other)

    def test_intent_selects_token_and_labels_result(self):
        guard = self.guard()
        mine = guard.call("fetch", {"on_data_of": "principal", "entityUrls": ["/me/messages?$top=5"]}, mode=UNATTENDED)
        hers = guard.call("fetch", {"on_data_of": "self", "entityUrls": ["/me/messages"]}, mode=UNATTENDED)
        self.assertEqual((mine["status"], mine["read_as"]), ("ok", "principal"))
        self.assertEqual((hers["status"], hers["read_as"]), ("ok", "margo"))
        self.assertEqual([call[2] for call in self.transport.calls], [PRINCIPAL_TOKEN, MARGO_TOKEN])
        self.assertNotIn("on_data_of", self.transport.calls[0][1])
        self.assertEqual(self.transport.calls[0][1]["entityUrls"], ["/me/messages?$top=5"])

    def test_missing_principal_token_is_blocked_without_fallback(self):
        guard = self.guard(tokens=FakeTokens(missing={"principal"}))
        result = guard.call("retrieve", {"on_data_of": "principal", "query": "launch risks",
                                         "capabilities": [{"name": "TeamsMessages"}]}, mode=UNATTENDED)
        self.assertEqual(result["status"], "blocked")
        self.assertEqual(self.tokens.requested, ["principal"])
        self.assertEqual(self.transport.calls, [])

    def test_path_policy_refuses_other_people_and_traversal(self):
        guard = self.guard()
        refused = [
            "/users/colleague-1/messages", "/users/colleague-1/calendar/events", "/users",
            "/me/../users/colleague-1/messages", "/me/%2e%2e/users/x/messages", "/me//messages",
            "https://graph.example.com/v1.0/me/messages", "/servicePrincipals", "/me/authentication/methods",
            "/groups/team-1/conversations", "me/messages", "/me/mess ages",
        ]
        for path in refused:
            with self.subTest(path=path):
                result = guard.call("fetch", {"on_data_of": "principal", "entityUrls": [path]}, mode=ATTENDED)
                self.assertEqual(result["status"], "refused")
        allowed = ["/users/colleague-1", "/users/colleague-1/manager", "/users/colleague-1/directReports",
                   "/me/chats/chat-shared-1/messages", "/sites/site-1/drive/root/children"]
        result = guard.call("fetch", {"on_data_of": "principal", "entityUrls": allowed}, mode=ATTENDED)
        self.assertEqual(result["status"], "ok")
        self.assertEqual(len(self.transport.calls), 1)

    def test_self_intent_cannot_reach_principal_only_roots(self):
        result = self.guard().call("fetch", {"on_data_of": "self", "entityUrls": ["/sites/site-1/drive"]},
                                   mode=ATTENDED)
        self.assertEqual(result["status"], "refused")

    def test_unknown_tools_arguments_and_agent_routing_are_refused(self):
        guard = self.guard()
        cases = [
            ("send_mail", {"on_data_of": "principal"}),
            ("fetch", {"on_data_of": "principal", "entityUrls": ["/me/messages"], "extra": 1}),
            ("fetch", {"entityUrls": ["/me/messages"]}),
            ("ask", {"on_data_of": "principal", "question": "Anything urgent?", "agentId": "custom-agent"}),
            ("retrieve", {"on_data_of": "principal", "query": "escalations",
                          "capabilities": [{"name": "Email", "shared_mailbox": "support@example.com"}]}),
            ("get_schema", {"on_data_of": "self", "path": "/me/messages", "operationType": "fetch",
                            "backend": "other"}),
        ]
        for tool, arguments in cases:
            with self.subTest(tool=tool, arguments=arguments):
                self.assertEqual(guard.call(tool, arguments, mode=ATTENDED)["status"], "refused")
        self.assertEqual(self.transport.calls, [])

    def test_third_party_trigger_is_least_privilege(self):
        guard = self.guard()
        allowed = [
            ("fetch", {"on_data_of": "self", "entityUrls": ["/me/messages/msg-1?$select=subject,body"]}),
            ("fetch", {"on_data_of": "self", "entityUrls": ["/me/messages/msg-1/attachments"]}),
            ("search_paths", {"on_data_of": "self", "query": "messages"}),
        ]
        refused = [
            ("fetch", {"on_data_of": "principal", "entityUrls": ["/me/messages"]}),
            ("fetch", {"on_data_of": "self", "entityUrls": ["/me/messages"]}),
            ("fetch", {"on_data_of": "self", "entityUrls": ["/me/chats/chat-principal-1/messages"]}),
            ("fetch", {"on_data_of": "self", "entityUrls": ["/chats/chat-principal-1/messages"]}),
            ("fetch", {"on_data_of": "self", "entityUrls": ["/me/messages/msg-1", "/me/chats"]}),
            ("retrieve", {"on_data_of": "self", "query": "what did Margo tell Dana",
                          "capabilities": [{"name": "TeamsMessages"}]}),
            ("ask", {"on_data_of": "self", "question": "What is in your chat with Dana?"}),
            ("call_function", {"on_data_of": "self", "functionUrl": "/me/calendarView"}),
        ]
        for tool, arguments in allowed:
            with self.subTest(tool=tool, arguments=arguments):
                self.assertEqual(guard.call(tool, arguments, mode=THIRD_PARTY)["status"], "ok")
        for tool, arguments in refused:
            with self.subTest(tool=tool, arguments=arguments):
                self.assertEqual(guard.call(tool, arguments, mode=THIRD_PARTY)["status"], "refused")
        self.assertEqual(self.tokens.requested, ["margo"] * len(allowed))

    def test_malformed_model_input_is_refused_with_a_receipt(self):
        guard = self.guard()
        cases = [
            (["fetch"], {"on_data_of": "self", "entityUrls": ["/me/messages"]}),
            ("fetch", {"on_data_of": ["principal"], "entityUrls": ["/me/messages"]}),
            ("retrieve", {"on_data_of": "principal", "query": "x", "capabilities": [{"name": ["Email"]}]}),
            ("retrieve", {"on_data_of": "principal", "query": "x", "includeDeveloperCard": "yes"}),
            ("fetch", "not-an-object"),
        ]
        for tool, arguments in cases:
            with self.subTest(tool=tool, arguments=arguments):
                self.assertEqual(guard.call(tool, arguments, mode=ATTENDED)["status"], "refused")
        self.assertEqual(guard.call("fetch", {"on_data_of": "self", "entityUrls": ["/me"]}, mode=["attended"])["status"],
                         "refused")
        self.assertEqual(len(guard.receipts), len(cases) + 1)
        self.assertEqual(self.transport.calls, [])

    def test_discovery_tools_follow_the_live_schema(self):
        guard = self.guard()
        for operation in ("fetch", "create", "update", "action", "function"):
            self.assertEqual(guard.call("get_schema", {"on_data_of": "principal", "path": "/me/messages",
                                                       "operationType": operation}, mode=ATTENDED)["status"], "ok")
        self.assertEqual(guard.call("search_paths", {"on_data_of": "principal", "query": "calendar events"},
                                    mode=ATTENDED)["status"], "ok")
        self.assertEqual(guard.call("search_paths", {"on_data_of": "principal", "query": "a", "filter": "b"},
                                    mode=ATTENDED)["status"], "refused")

    def test_principal_chat_post_is_the_only_claimless_write(self):
        guard = self.guard(transport=FakeTransport({"statusCode": 201}))
        body = json.dumps({"body": {"content": "Your brief is ready."}})
        for path in ("/chats/chat-principal-1/messages", "/me/chats/chat-principal-1/messages"):
            result = guard.call("create_entity", {"on_data_of": "self", "parentUrl": path, "jsonBody": body},
                                mode=UNATTENDED)
            self.assertEqual(result["status"], "ok")
        refused = [
            ("create_entity", {"on_data_of": "self", "parentUrl": "/chats/chat-other-1/messages", "jsonBody": body}),
            ("create_entity", {"on_data_of": "principal", "parentUrl": "/chats/chat-principal-1/messages",
                               "jsonBody": body}),
            ("do_action", {"on_data_of": "principal", "actionUrl": "/me/messages/m-1/send"}),
            ("create_entity", {"on_data_of": "self", "parentUrl": "/me/messages", "jsonBody": body}),
        ]
        for tool, arguments in refused:
            for mode in (UNATTENDED, THIRD_PARTY, ATTENDED):
                with self.subTest(tool=tool, path=arguments.get("parentUrl"), mode=mode):
                    self.assertEqual(guard.call(tool, arguments, mode=mode)["status"], "refused")
        self.assertEqual(len(self.transport.calls), 2)

    def test_budget_is_charged_before_the_call(self):
        guard = self.guard(budget=autopilot.CallBudget(1))
        request = {"on_data_of": "principal", "entityUrls": ["/me/calendarView"]}
        self.assertEqual(guard.call("fetch", request, mode=UNATTENDED)["status"], "ok")
        self.assertEqual(guard.call("fetch", request, mode=UNATTENDED)["status"], "budget_exhausted")
        self.assertEqual(len(self.transport.calls), 1)

    def test_receipts_omit_tokens_bodies_queries_and_results(self):
        guard = self.guard(transport=FakeTransport({"statusCode": 200, "data": {"subject": "fictional secret"}}))
        guard.call("fetch", {"on_data_of": "principal",
                             "entityUrls": ["/me/messages?$search=\"fictional query\""]}, mode=ATTENDED)
        guard.call("create_entity", {"on_data_of": "self", "parentUrl": "/chats/chat-principal-1/messages",
                                     "jsonBody": json.dumps({"body": {"content": "fictional body"}})}, mode=UNATTENDED)
        recorded = json.dumps(guard.receipts)
        for secret in (PRINCIPAL_TOKEN, MARGO_TOKEN, "fictional secret", "fictional query", "fictional body"):
            self.assertNotIn(secret, recorded)
        self.assertEqual(guard.receipts[0]["paths"], ["/me/messages"])

    def test_model_tools_require_a_subject(self):
        tools = {tool["name"]: tool for tool in autopilot.model_tools()}
        self.assertEqual(set(tools), set(autopilot.guard.TOOL_ARGS))
        for tool in tools.values():
            self.assertEqual(tool["input_schema"]["required"], ["on_data_of"])
            self.assertNotIn("agentId", tool["input_schema"]["properties"])
        self.assertIn("attempt_id", tools["do_action"]["input_schema"]["properties"])
        self.assertNotIn("attempt_id", tools["fetch"]["input_schema"]["properties"])


class InstanceBindingTests(unittest.TestCase):
    def test_binding_rejects_ambiguous_identity(self):
        with self.assertRaises(instance.BindingError):
            autopilot.InstanceBinding("agent-identity-1", "same-1", "same-1", "chat-1")
        with self.assertRaises(instance.BindingError):
            autopilot.InstanceBinding("agent identity", "margo-user-1", "principal-1", "chat-1")
        with self.assertRaises(instance.BindingError):
            autopilot.InstanceBinding("agent-identity-1", "margo-user-1", "", "chat-1")

    def test_account_key_and_principal_check(self):
        self.assertEqual(BINDING.account, "autopilot:agent-identity-1:principal-1")
        other = autopilot.InstanceBinding("agent-identity-2", "margo-user-2", "principal-1", "chat-2")
        self.assertNotEqual(BINDING.account, other.account)
        self.assertTrue(BINDING.is_principal("principal-1"))
        self.assertFalse(BINDING.is_principal("Principal One"))
        self.assertFalse(BINDING.is_principal(None))


class GuardedWriteTests(unittest.TestCase):
    """Claims come from the real approval journal; only the database location is synthetic."""

    SEND = {"tool": "do_action", "arguments": {"actionUrl": "/me/messages/draft-1/send"}}

    def setUp(self):
        self.path = ROOT / (".autopilot-guard-test-" + uuid.uuid4().hex)
        self.path.mkdir(mode=0o700)

        def connect(account=None, state_root=None):
            connection = sqlite3.connect(str(self.path / (work.digest(account) + ".sqlite")), timeout=3)
            connection.row_factory = sqlite3.Row
            connection.execute("PRAGMA foreign_keys=ON")
            return connection

        self.patches = [patch.object(work.margo_store, "connect", side_effect=connect),
                        patch.object(work.margo_store, "resolve_account", side_effect=lambda account: account)]
        for item in self.patches:
            item.start()
        self.ledger = BINDING.open_ledger(str(self.path))
        self.ref = self.ledger.source("mail", "inbox", "message-1", "v1",
                                      {"quote": "Please send the fictional summary."}, "https://example.com/mail/1")

    def tearDown(self):
        self.ledger.close()
        for item in reversed(self.patches):
            item.stop()
        shutil.rmtree(str(self.path))

    def started(self, workiq=None, on_data_of="principal", payload=None, actor="principal-1"):
        item = self.ledger.ingest({"title": "Send fictional summary", "owner": None, "due": None,
                                   "direction": "owe", "source_refs": [self.ref]}, "claim-" + uuid.uuid4().hex)
        action = self.ledger.propose({
            "kind": "mail.send", "target": {"message_id": "draft-1", "on_data_of": on_data_of},
            "payload": payload or {"workiq": workiq or self.SEND}, "why": "Explicit request",
            "source_refs": [self.ref], "target_fingerprint": "target-v1", "work_item_id": item["id"]})
        evidence = {"kind": "human_confirmation", "actor": actor,
                    "statement": "Send this exact message.", "evidence_ref": "host-interaction:fictional/teams-card/1",
                    "subject_id": action["id"], "revision": 1, "decision": "approve",
                    "action_hash": action["action_hash"], "decided_at": stamp()}
        self.ledger.approve(action["id"], 1, action["action_hash"], evidence, stamp(60))
        attempt = self.ledger.begin(action["id"], 1, {"checked_at": stamp(), "target_fingerprint": "target-v1",
                                                      "source_refs": [self.ref]})
        return action["id"], attempt["attempt_id"]

    def guard(self, transport):
        self.transport = transport
        return autopilot.WorkIQGuard(BINDING, FakeTokens(), transport,
                                     journal=claims.LedgerClaimJournal(self.ledger))

    def send(self, guard, action_id, attempt_id, mode=ATTENDED, **overrides):
        arguments = {"on_data_of": "principal", "actionUrl": "/me/messages/draft-1/send",
                     "action_id": action_id, "attempt_id": attempt_id}
        arguments.update(overrides)
        return guard.call("do_action", arguments, mode=mode)

    def test_exact_claim_executes_once_and_records_receipt(self):
        action_id, attempt_id = self.started()
        guard = self.guard(FakeTransport({"statusCode": 202}))
        self.assertEqual(self.send(guard, action_id, attempt_id)["status"], "ok")
        self.assertEqual(self.transport.calls[0][2], PRINCIPAL_TOKEN)
        action = self.ledger.show(action_id)
        self.assertEqual(action["state"], "succeeded")
        self.assertEqual(action["executions"][0]["receipt"]["status_code"], 202)
        self.assertEqual(self.send(guard, action_id, attempt_id)["status"], "refused")
        self.assertEqual(len(self.transport.calls), 1)

    def test_mismatched_call_or_subject_is_refused_and_claim_stays_live(self):
        action_id, attempt_id = self.started()
        guard = self.guard(FakeTransport({"statusCode": 202}))
        self.assertEqual(self.send(guard, action_id, attempt_id, actionUrl="/me/messages/draft-2/send")["status"],
                         "refused")
        self.assertEqual(self.send(guard, action_id, attempt_id, on_data_of="self")["status"], "refused")
        self.assertEqual(self.transport.calls, [])
        self.assertEqual(self.ledger.show(action_id)["state"], "executing")

    def test_unattended_run_cannot_use_a_live_claim(self):
        action_id, attempt_id = self.started()
        guard = self.guard(FakeTransport({"statusCode": 202}))
        for mode in (UNATTENDED, THIRD_PARTY):
            self.assertEqual(self.send(guard, action_id, attempt_id, mode=mode)["status"], "refused")
        self.assertEqual(self.transport.calls, [])

    def test_timeout_is_unknown_and_never_retried(self):
        action_id, attempt_id = self.started()
        guard = self.guard(FakeTransport(error=autopilot.TransportTimeout("no response")))
        self.assertEqual(self.send(guard, action_id, attempt_id)["status"], "unknown")
        self.assertEqual(self.ledger.show(action_id)["state"], "outcome_unknown")
        self.assertEqual(self.send(guard, action_id, attempt_id)["status"], "refused")
        self.assertEqual(len(self.transport.calls), 1)

    def test_rejected_request_is_a_no_effect_failure_but_server_error_is_unknown(self):
        rejected_id, rejected_attempt = self.started()
        guard = self.guard(FakeTransport(error=autopilot.TransportError("forbidden", status_code=403)))
        self.assertEqual(self.send(guard, rejected_id, rejected_attempt)["status"], "error")
        self.assertEqual(self.ledger.show(rejected_id)["state"], "failed")
        server_id, server_attempt = self.started()
        guard = self.guard(FakeTransport(error=autopilot.TransportError("unavailable", status_code=503)))
        self.assertEqual(self.send(guard, server_id, server_attempt)["status"], "error")
        self.assertEqual(self.ledger.show(server_id)["state"], "outcome_unknown")

    def test_success_without_a_status_code_stays_unknown(self):
        action_id, attempt_id = self.started()
        guard = self.guard(FakeTransport({"data": {}}))
        self.send(guard, action_id, attempt_id)
        self.assertEqual(self.ledger.show(action_id)["state"], "outcome_unknown")

    def test_action_without_exact_workiq_call_cannot_execute(self):
        item = self.ledger.ingest({"title": "Reply", "owner": None, "due": None, "direction": "owe",
                                   "source_refs": [self.ref]}, "legacy-claim")
        action = self.ledger.propose({
            "kind": "mail.reply", "target": {"message_id": "message-1"}, "payload": {"body": "Fictional reply."},
            "why": "Legacy shape", "source_refs": [self.ref], "target_fingerprint": "target-v1",
            "work_item_id": item["id"]})
        evidence = {"kind": "human_confirmation", "actor": "principal-1", "statement": "Send it.",
                    "evidence_ref": "host-interaction:fictional/teams-card/2", "subject_id": action["id"], "revision": 1,
                    "decision": "approve", "action_hash": action["action_hash"], "decided_at": stamp()}
        self.ledger.approve(action["id"], 1, action["action_hash"], evidence, stamp(60))
        attempt = self.ledger.begin(action["id"], 1, {"checked_at": stamp(), "target_fingerprint": "target-v1",
                                                      "source_refs": [self.ref]})
        guard = self.guard(FakeTransport({"statusCode": 202}))
        self.assertEqual(self.send(guard, action["id"], attempt["attempt_id"])["status"], "refused")
        self.assertEqual(self.transport.calls, [])

    def test_missing_token_settles_claim_as_no_effect(self):
        action_id, attempt_id = self.started()
        self.transport = FakeTransport({"statusCode": 202})
        guard = autopilot.WorkIQGuard(BINDING, FakeTokens(missing={"principal"}), self.transport,
                                      journal=claims.LedgerClaimJournal(self.ledger))
        self.assertEqual(self.send(guard, action_id, attempt_id)["status"], "blocked")
        self.assertEqual(self.ledger.show(action_id)["state"], "failed")
        self.assertEqual(self.transport.calls, [])

    def test_unexpected_transport_failure_is_unknown_and_never_resent(self):
        action_id, attempt_id = self.started()
        first = self.guard(FakeTransport(error=ConnectionResetError("reset by peer")))
        self.assertEqual(self.send(first, action_id, attempt_id)["status"], "unknown")
        self.assertEqual(self.ledger.show(action_id)["state"], "outcome_unknown")
        second = self.guard(FakeTransport({"statusCode": 202}))
        self.assertEqual(self.send(second, action_id, attempt_id)["status"], "refused")
        self.assertEqual(self.transport.calls, [])

    def test_dispatch_is_durable_across_guards_after_a_crash(self):
        action_id, attempt_id = self.started()
        journal = claims.LedgerClaimJournal(self.ledger)
        journal.mark_dispatched(journal.load(action_id, attempt_id))  # process died after this point
        self.assertEqual(self.ledger.show(action_id)["state"], "executing")
        guard = self.guard(FakeTransport({"statusCode": 202}))
        result = self.send(guard, action_id, attempt_id)
        self.assertEqual(result["status"], "refused")
        self.assertIn("reconcile", result["reason"])
        self.assertEqual(self.transport.calls, [])
        with self.assertRaises(claims.ClaimError):
            journal.mark_dispatched({"action_id": action_id, "attempt_id": attempt_id,
                                     "action_hash": self.ledger.show(action_id)["action_hash"]})

    def test_token_provider_crash_is_blocked_and_settles_without_sending(self):
        class Broken:
            def token_for(self, subject):
                raise RuntimeError("vault unreachable")

        action_id, attempt_id = self.started()
        self.transport = FakeTransport({"statusCode": 202})
        guard = autopilot.WorkIQGuard(BINDING, Broken(), self.transport,
                                      journal=claims.LedgerClaimJournal(self.ledger))
        result = self.send(guard, action_id, attempt_id)
        self.assertEqual(result["status"], "blocked")
        self.assertNotIn("vault unreachable", result["reason"])
        self.assertEqual(self.ledger.show(action_id)["state"], "failed")
        self.assertEqual(self.transport.calls, [])

    def test_approval_must_come_from_the_manager(self):
        action_id, attempt_id = self.started(actor="colleague-1")
        guard = self.guard(FakeTransport({"statusCode": 202}))
        result = self.send(guard, action_id, attempt_id)
        self.assertEqual(result["status"], "refused")
        self.assertIn("manager", result["reason"])
        self.assertEqual(self.transport.calls, [])

    def test_manager_can_authorise_mail_from_margos_own_mailbox(self):
        own = {"tool": "do_action", "arguments": {"actionUrl": "/me/sendMail", "jsonBody": json.dumps(
            {"message": {"subject": "Agenda", "toRecipients": [{"emailAddress": {"address": "rafa@example.com"}}]}})}}
        action_id, attempt_id = self.started(workiq=own, on_data_of="self")
        guard = self.guard(FakeTransport({"statusCode": 202}))
        arguments = dict(own["arguments"], on_data_of="self", action_id=action_id, attempt_id=attempt_id)
        self.assertEqual(guard.call("do_action", arguments, mode=ATTENDED)["status"], "ok")
        self.assertEqual(self.transport.calls[0][2], MARGO_TOKEN)
        self.assertEqual(self.ledger.show(action_id)["state"], "succeeded")
        self.assertEqual(guard.call("do_action", dict(arguments, on_data_of="principal"), mode=ATTENDED)["status"],
                         "refused")

    def test_reply_gate_approve_mode_sends_only_the_approved_text(self):
        reply = {"to": "colleague-1", "text": "Dana can meet on Thursday."}
        action_id, attempt_id = self.started(payload={"reply": reply})
        gate = autopilot.ReplyGate(BINDING, replies.APPROVE, journal=claims.LedgerClaimJournal(self.ledger))
        changed = gate.decide("colleague-1", "conv-1", "Dana can meet on Friday.", manager_name="Dana",
                              action_id=action_id, attempt_id=attempt_id)
        self.assertFalse(changed["send"])
        other = gate.decide("colleague-2", "conv-1", reply["text"], manager_name="Dana",
                            action_id=action_id, attempt_id=attempt_id)
        self.assertFalse(other["send"])
        decision = gate.decide("colleague-1", "conv-1", reply["text"], manager_name="Dana",
                               action_id=action_id, attempt_id=attempt_id)
        self.assertEqual((decision["send"], decision["text"]), (True, reply["text"]))
        gate.settle(decision["claim"], "succeeded", 200)
        self.assertEqual(self.ledger.show(action_id)["state"], "succeeded")
        again = gate.decide("colleague-1", "conv-1", reply["text"], manager_name="Dana",
                            action_id=action_id, attempt_id=attempt_id)
        self.assertFalse(again["send"])

    def test_reply_gate_rejects_approval_by_anyone_else_and_workiq_claims(self):
        reply = {"to": "colleague-1", "text": "Approved by the wrong person."}
        action_id, attempt_id = self.started(payload={"reply": reply}, actor="colleague-1")
        gate = autopilot.ReplyGate(BINDING, replies.APPROVE, journal=claims.LedgerClaimJournal(self.ledger))
        self.assertFalse(gate.decide("colleague-1", "conv-1", reply["text"], manager_name="Dana",
                                     action_id=action_id, attempt_id=attempt_id)["send"])
        workiq_id, workiq_attempt = self.started()
        self.assertFalse(gate.decide("colleague-1", "conv-1", "x", manager_name="Dana",
                                     action_id=workiq_id, attempt_id=workiq_attempt)["send"])


class ReplyPolicyTests(unittest.TestCase):
    def test_setting_is_parsed_strictly_and_defaults_to_none(self):
        line = "- **Replies to other people:** %s\n"
        self.assertEqual(autopilot.load_reply_policy(line % "acknowledge")["mode"], "acknowledge")
        self.assertEqual(autopilot.load_reply_policy(line % "`approve`")["mode"], "approve")
        for text in ("", line % "autonomous", line % "none" + line % "approve", None,
                     "Replies to other people: approve", "- **Replies to other people:** approve please"):
            with self.subTest(text=text):
                policy = autopilot.load_reply_policy(text)
                self.assertEqual(policy["mode"], "none")
                self.assertTrue(policy["issues"])

    def test_template_ships_with_replies_off(self):
        template = (ROOT / "skills" / "chief-of-staff" / "preferences.md").read_text(encoding="utf-8")
        self.assertEqual(autopilot.load_reply_policy(template), {"mode": "none", "issues": []})

    def test_none_and_acknowledge_never_send_model_text(self):
        off = autopilot.ReplyGate(BINDING, replies.NONE)
        self.assertFalse(off.decide("colleague-1", "conv-1", "Dana's salary is ...", manager_name="Dana")["send"])
        ack = autopilot.ReplyGate(BINDING, replies.ACKNOWLEDGE)
        first = ack.decide("colleague-1", "conv-1", "Dana's private calendar says ...", manager_name="Dana")
        self.assertEqual(first, {"send": True, "text": replies.ACKNOWLEDGEMENT.format(manager="Dana")})
        self.assertFalse(ack.decide("colleague-1", "conv-1", "again", manager_name="Dana")["send"])
        self.assertTrue(ack.decide("colleague-1", "conv-2", "new thread", manager_name="Dana")["send"])
        self.assertFalse(ack.decide("margo-user-1", "conv-3", "loop", manager_name="Dana")["send"])

    def test_manager_conversation_is_not_gated(self):
        gate = autopilot.ReplyGate(BINDING, replies.NONE)
        self.assertTrue(gate.decide("principal-1", "conv-1", "Your brief.", manager_name="Dana")["send"])


if __name__ == "__main__":
    unittest.main()
