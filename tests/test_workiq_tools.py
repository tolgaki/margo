import io
import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))

import workiq_receipt_stats  # noqa: E402
import workiq_trace_vocabulary as vocab  # noqa: E402
from evaluate_agent_traces import TOOL_EFFECTS  # noqa: E402


class TraceVocabularyTest(unittest.TestCase):
    CASES = [
        ("workiq-fetch", {"entityUrls": ["/me/messages?$top=5"]}, ("mail.get", "read")),
        ("fetch", {"entityUrls": ["/me/calendarView?startDateTime=a&endDateTime=b"]}, ("calendar.list", "read")),
        ("fetch", {"entityUrls": ["/chats/19:abc/messages"]}, ("teams.get-thread", "read")),
        ("fetch", {"entityUrls": ["/me/drive/recent"]}, ("files.read", "read")),
        ("fetch", {"entityUrls": ["/me/manager"]}, ("people.lookup", "read")),
        ("retrieve", {"query": ["launch risk"], "capabilities": [{"name": "Email"}]}, ("mail.search", "read")),
        ("retrieve", {"query": ["launch risk"]}, (None, "read")),
        ("ask", {"question": "What changed?"}, (None, "read")),
        ("do_action", {"actionUrl": "/me/sendMail"}, ("mail.send", "write")),
        ("do_action", {"actionUrl": "/me/messages/m1/replyAll"}, ("mail.reply", "write")),
        ("do_action", {"actionUrl": "/me/events/e1/cancel"}, ("calendar.cancel", "write")),
        ("create_entity", {"parentUrl": "/me/events"}, ("calendar.create", "write")),
        ("update_entity", {"entityUrl": "/me/events/e1"}, ("calendar.update", "write")),
        ("create_entity", {"parentUrl": "/chats/19:abc/messages"}, ("teams.post", "write")),
        ("do_action", {"actionUrl": "/drives/d1/items/i1/invite"}, ("files.share", "write")),
        ("do_action", {"actionUrl": "/pages/create_page"}, (None, "write")),
        ("send_mail", {}, (None, None)),
    ]

    def test_work_iq_calls_map_onto_the_evaluator_vocabulary(self):
        for tool, arguments, expected in self.CASES:
            self.assertEqual(vocab.evaluator_tool(tool, arguments), expected, (tool, arguments))

    def test_every_mapped_name_is_one_the_evaluator_knows_with_the_same_effect(self):
        for tool, arguments, _ in self.CASES:
            name, effect = vocab.evaluator_tool(tool, arguments)
            if name is not None:
                self.assertEqual(TOOL_EFFECTS[name], effect, name)

    def test_unmapped_calls_are_kept_and_reported(self):
        out = io.StringIO()
        lines = [json.dumps({"tool": "ask", "arguments": {"question": "?"}}), json.dumps({"tool": "fetch", "arguments": {"entityUrls": ["/me/messages"]}})]
        vocab.main(io.StringIO("\n".join(lines)), out)
        rows = [json.loads(line) for line in out.getvalue().splitlines()]
        self.assertEqual([row["evaluator_tool"] for row in rows], [None, "mail.get"])


class ReceiptStatsTest(unittest.TestCase):
    def test_receipts_are_counted_by_tool_status_code_and_path(self):
        log = "\n".join([
            'info: margo.autopilot[0] workiq {"tool": "fetch", "status": "ok", "status_codes": [200], "paths": ["/me/messages"]}',
            'info: margo.autopilot[0] workiq {"tool": "fetch", "status": "error", "status_codes": [429], "paths": ["/me/messages"]}',
            'info: margo.autopilot[0] turn status=answered',
            'info: margo.autopilot[0] workiq {not json',
        ])
        summary = workiq_receipt_stats.summarize(io.StringIO(log))
        self.assertEqual(summary["calls"], 2)
        self.assertEqual(summary["by_tool"], {"fetch": {"error": 1, "ok": 1}})
        self.assertEqual(summary["status_codes"], {"200": 1, "429": 1})
        self.assertEqual(summary["top_paths"], {"/me/messages": 2})

    def test_local_state_command_receipts_are_not_work_iq_calls(self):
        log = "\n".join([
            'info: margo.autopilot[0] workiq {"tool": "fetch", "status": "ok", "status_codes": [200], "paths": ["/me/messages"]}',
            'info: margo.autopilot[0] workiq {"at": "2026-01-01T00:00:00.000000+00:00", "tool": "margo-state", '
            '"script": "work_state.py", "command": "list", "mode": "chat", "status": "ok", "exit_code": 0, "ms": 12}',
        ])
        summary = workiq_receipt_stats.summarize(io.StringIO(log))
        self.assertEqual(summary["calls"], 1)
        self.assertEqual(summary["state_commands"], 1)
        self.assertEqual(summary["by_tool"], {"fetch": {"ok": 1}})


if __name__ == "__main__":
    unittest.main()
