from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parents[1] / "skills/chief-of-staff/scripts"
sys.path.insert(0, str(SCRIPTS))

import decision_workspace as desk
from margo_store import NotInitialized, StateError
from task_runs import TaskStore
from work_ledger import Ledger


class DecisionWorkspaceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.env = patch.dict(os.environ, {"MARGO_ALLOW_UNSAFE_STATE_DIR": "1",
                                          "COPILOT_HOME": str(self.root / "private-install"),
                                          "MARGO_CONFIG": str(self.root / "private-install" / "config.json")})
        self.env.start()
        import margo_store
        margo_store.initialize_config("desk-fixture")
        self.tasks = TaskStore("desk-fixture", str(self.root / "state"))
        self.ledger = self.tasks.ledger
        self.ref = self.ledger.source("mail", "inbox", "fictional-ask", "1",
                                     {"quote": "Please review the design."}, "https://example.com/source")
        self.item = self.ledger.ingest({
            "title": "Review the design", "direction": "owe", "owner": "Dana",
            "due": "2026-09-12", "next_step": "Choose an approach", "source_refs": [self.ref],
        }, "design")
        self.input = {"id": self.item["id"], "expected_revision": 1, "intent": "recommend", "host": "synthetic-sdk"}
        self.preferences = self.root / "preferences.md"
        self.preferences.write_text("- **Time zone & working hours:** America/Los_Angeles; Mon-Fri; 09:00-18:00\n",
                                    encoding="utf-8")

    def tearDown(self):
        self.tasks.close()
        self.env.stop()
        self.temp.cleanup()

    def accepted(self, request):
        return desk.dispatched(self.ledger, dict(request["claim"], accepted=True,
                                                reference="conversation:synthetic-message"))

    def test_request_lifecycle_is_durable_bounded_and_never_approval(self):
        request = desk.request(self.ledger, self.input)
        self.assertEqual(request["request"]["phase"], "dispatching")
        self.assertEqual(self.accepted(request)["request"]["phase"], "accepted")
        with self.assertRaisesRegex(StateError, "host changed"):
            desk.start(self.ledger, request["request"]["run_id"], "another-host")
        claim = desk.start(self.ledger, request["request"]["run_id"], self.input["host"])
        snapshot = desk.snapshot(self.ledger, self.preferences)
        self.assertEqual(snapshot["requests"][self.item["id"]]["recommend"]["phase"], "working")
        finished = self.tasks.finish(claim["attempt_id"], claim["token"], "succeeded", {
            "kind": "local_result", "reference": "conversation:synthetic-preparation",
            "summary": "Choose approach A after reviewing the recorded tradeoff.",
            "work_ids": [self.item["id"]], "source_refs": [self.ref],
        })
        self.assertTrue(all(step["kind"] == "local" for step in finished["plan"]["steps"]))
        self.assertEqual(finished["plan"]["limits"]["max_attempts_per_step"], 1)
        reopened = Ledger("desk-fixture", str(self.root / "state"), read_only=True)
        try:
            state = desk.snapshot(reopened, self.preferences)
            status = state["requests"][self.item["id"]]["recommend"]
            self.assertEqual(status["phase"], "ready")
            self.assertFalse(status["approved"])
            self.assertNotIn("token", json.dumps(state))
            self.assertFalse(reopened.show(self.item["id"])["confirmed"])
            self.assertEqual(reopened.show(self.item["id"])["state"], "candidate")
        finally:
            reopened.close()
        self.assertTrue(desk.request(self.ledger, self.input)["replayed"])
        with self.assertRaises(StateError):
            desk.start(self.ledger, request["request"]["run_id"], self.input["host"])

    def test_unknown_dispatch_is_persisted_and_never_resent(self):
        request = desk.request(self.ledger, self.input)
        result = desk.dispatched(self.ledger, dict(request["claim"], accepted=False,
                                                  reference="canvas:dispatch-unknown"))
        self.assertEqual(result["request"]["phase"], "outcome_unknown")
        repeated = desk.request(self.ledger, self.input)
        self.assertTrue(repeated["replayed"])
        self.assertNotIn("claim", repeated)
        with self.assertRaises(StateError):
            desk.start(self.ledger, request["request"]["run_id"], self.input["host"])

    def test_concurrent_panels_claim_dispatch_only_once(self):
        def click(_):
            ledger = Ledger("desk-fixture", str(self.root / "state"))
            try:
                return desk.request(ledger, self.input)
            finally:
                ledger.close()
        with ThreadPoolExecutor(max_workers=4) as pool:
            results = list(pool.map(click, range(8)))
        self.assertEqual(sum(not result["replayed"] for result in results), 1)
        self.assertEqual(len({result["request"]["run_id"] for result in results}), 1)

    def test_subject_revision_and_action_hash_are_rechecked_before_start(self):
        request = desk.request(self.ledger, self.input)
        self.accepted(request)
        self.ledger.update_item(self.item["id"], 1, patch={"next_step": "Review the changed approach"})
        with self.assertRaisesRegex(StateError, "Revision conflict"):
            desk.start(self.ledger, request["request"]["run_id"], self.input["host"])
        action = self.ledger.propose({
            "kind": "mail.reply", "target": {"to": ["dana@example.com"]}, "payload": {"body": "Unsent draft"},
            "why": "Synthetic ask", "source_refs": [self.ref], "target_fingerprint": "v1", "work_item_id": self.item["id"],
        })
        value = dict(self.input, id=action["id"], expected_hash="a" * 64)
        with self.assertRaisesRegex(StateError, "hash conflict"):
            desk.request(self.ledger, value)
        value["expected_hash"] = action["action_hash"]
        self.assertFalse(desk.request(self.ledger, value)["replayed"])
        self.assertEqual(self.ledger.show(action["id"])["approvals"], [])

    def test_read_only_snapshot_does_not_initialize_optional_namespaces(self):
        ledger = Ledger("work-only", str(self.root / "state"))
        before = [tuple(row) for row in ledger.conn.execute("SELECT name FROM sqlite_master")]
        try:
            snapshot = desk.snapshot(ledger, self.preferences)
            self.assertFalse(snapshot["request_capability"]["available"])
            self.assertEqual(snapshot["coverage"]["status"], "unavailable")
            with self.assertRaises(StateError):
                desk.request(ledger, dict(self.input, id="missing"))
            self.assertEqual(before, [tuple(row) for row in ledger.conn.execute("SELECT name FROM sqlite_master")])
        finally:
            ledger.close()
        with self.assertRaises(NotInitialized):
            Ledger("missing-account", str(self.root / "missing"), read_only=True)
        self.assertFalse((self.root / "missing").exists())

    def test_readonly_cli_and_installed_copy_use_adjacent_preferences(self):
        result = subprocess.run([sys.executable, "-B", str(SCRIPTS / "work_state.py"),
                                 "--account", "desk-fixture", "--state-root", str(self.root / "state"), "desk"],
                                capture_output=True, text=True, check=True)
        state = json.loads(result.stdout)
        self.assertEqual(state["account"], "desk-fixture")
        self.assertEqual(state["time_preferences"]["status"], "missing")
        self.assertEqual(len(state["items"]), 1)
        unknown = self.root / "not-created"
        result = subprocess.run([sys.executable, "-B", str(SCRIPTS / "work_state.py"),
                                 "--account", "unknown", "--state-root", str(unknown), "desk"],
                                capture_output=True, text=True)
        self.assertEqual(result.returncode, 2)
        self.assertFalse(unknown.exists())

    def test_preferences_are_narrow_and_snapshot_limits_are_explicit(self):
        self.preferences.write_text(
            "- **Name / preferred name:** Private fixture\n"
            "- **Time zone & working hours:** UTC; Sun,Mon; 08:00-12:00\n",
            encoding="utf-8")
        value = desk.time_preferences(self.preferences)
        self.assertNotIn("Private fixture", json.dumps(value))
        self.assertEqual(value["value"], "UTC; Sun,Mon; 08:00-12:00")
        self.preferences.write_text("- **Time zone & working hours:** {not configured}\n", encoding="utf-8")
        self.assertEqual(desk.time_preferences(self.preferences)["status"], "missing")
        for index in range(desk.LIMIT):
            self.ledger.ingest(dict(self.item["data"], title="Synthetic work %s" % index), "bounded-%s" % index)
        result = desk.snapshot(self.ledger, self.preferences)
        self.assertEqual(len(result["items"]), desk.LIMIT)
        self.assertEqual(result["truncated"], ["items"])

    def test_stale_preparation_result_does_not_stay_ready(self):
        request = desk.request(self.ledger, self.input)
        self.accepted(request)
        claim = desk.start(self.ledger, request["request"]["run_id"], self.input["host"])
        self.tasks.finish(claim["attempt_id"], claim["token"], "succeeded", {
            "kind": "local_result", "reference": "conversation:synthetic-result",
            "summary": "Recorded recommendation.", "source_refs": [self.ref],
        })
        self.ledger.source("mail", "inbox", "fictional-ask", "2",
                           {"quote": "The design changed."}, "https://example.com/source")
        self.assertEqual(desk.snapshot(self.ledger, self.preferences)["requests"][self.item["id"]]["recommend"]["phase"], "blocked")
