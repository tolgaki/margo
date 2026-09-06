import copy
import io
import json
import os
from pathlib import Path
import shutil
import sys
import unittest
import uuid
from concurrent.futures import ThreadPoolExecutor
from threading import Barrier
from contextlib import redirect_stdout, redirect_stderr
from datetime import datetime, timedelta, timezone
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "skills/chief-of-staff/scripts"))

import memory_dream as dream
import memory_state
from memory_governance import policy_preview, set_policy
from memory_store import MemoryStore
from memory_search import MemorySearch
from margo_store import StateError
from task_runs import TaskStore
from test_memory_store import evidence


class DreamTests(unittest.TestCase):
    def setUp(self):
        self.root = ROOT / (".dream-fixture-" + uuid.uuid4().hex)
        self.root.mkdir(mode=0o700)
        self.env = patch.dict(os.environ, {"MARGO_ALLOW_UNSAFE_STATE_DIR": "1"})
        self.env.start()
        self.tasks = TaskStore("dana@example.com", str(self.root))
        self.memory = MemoryStore("dana@example.com", str(self.root))
        self.day = (datetime.now(timezone.utc) - timedelta(days=1)).date().isoformat()
        self.request = {"host": "fixture-host", "workspace": "fixture-workspace", "day": self.day,
                        "timezone": "UTC", "cutoff": "00:00", "lookback_days": 1, "after": None}
        self.event = {
            "capability": {"account": self.memory.account, "host": self.request["host"],
                           "workspace": self.request["workspace"], "adapter": "margo-session-checkpoint-v1",
                           "content_access": "current-session", "session_list": False, "authorized": True},
            "session": "fixture-session", "event": "event-1", "revision": "host-rev-1",
            "expected_revision": None, "timestamp": self.day + "T12:00:00+00:00", "speaker": "user",
            "locator": "conversation:fixture-session/event-1", "text": "Consider the Cedar option. Timing is open.",
            "origin": "margo-session", "sensitivity": "private", "allowed_uses": ["reasoning"],
            "entities": ["project:cedar"], "work_refs": [],
        }
        fixture = json.loads((ROOT / "tests/fixtures/journeys/sources.json").read_text())["fixtures"]["dream-v1"]["sources"][0]
        self.event.update({field: fixture[field] for field in ("speaker", "locator", "text")})

    def tearDown(self):
        self.memory.close()
        self.tasks.close()
        self.env.stop()
        shutil.rmtree(self.root)

    def configure(self, enabled=True):
        policy = self.memory.policy()["data"]
        policy["capture"] = {"enabled": enabled, "domains": ["user"], "kinds": ["episode", "decision"],
                             "scopes": [dream.scope_for(self.memory.account, self.request["host"], self.request["workspace"])],
                             "source_kinds": ["session_checkpoint", "memory_record"]}
        preview = policy_preview(self.memory, policy)
        return set_policy(self.memory, policy, evidence(preview["subject_id"], decision="configure"))

    def begin(self, key="daily"):
        preview = dream.plan(self.memory, self.request)
        return dream.start(self.memory, key, {"request": self.request, "snapshot_hash": preview["snapshot_hash"],
                                             "request_ref": "conversation:fixture-dream-request"})

    def finish_input(self, started, event):
        return {"snapshot_id": started["snapshot_id"], "attempt_id": started["claim"]["attempt_id"],
                "token": started["claim"]["token"], "claims": [
                    {"key": "open-timing", "type": "question", "text": "The timing remains unresolved.",
                     "citations": [{"id": event["id"], "revision": event["revision"], "quote": "Timing is open."}]}]}

    def cli(self, *args, value=None):
        output, errors = io.StringIO(), io.StringIO()
        with patch("sys.stdin", io.StringIO(json.dumps(value))), redirect_stdout(output), redirect_stderr(errors):
            code = memory_state.main(["--account", self.memory.account, "--state-dir", str(self.root), *args])
        return code, json.loads(output.getvalue() or errors.getvalue())

    def test_daily_checkpoint_reflection_and_task_context_journey(self):
        self.configure()
        code, event = self.cli("dream-checkpoint", "--input", "-", value=self.event)
        self.assertEqual(code, 0, event)
        code, preview = self.cli("dream-plan", "--input", "-", value=self.request)
        self.assertEqual(code, 0, preview)
        code, started = self.cli("dream-start", "daily", "--input", "-",
                                 value={"request": self.request, "snapshot_hash": preview["snapshot_hash"],
                                        "request_ref": "conversation:fixture-dream-request"})
        self.assertEqual(code, 0, started)
        code, result = self.cli("dream-finish", "--input", "-", value=self.finish_input(started, event))
        self.assertEqual(code, 0, result)
        self.assertEqual(result["reflection"], "completed")
        self.assertEqual(result["collection"]["status"], "partial")
        self.assertEqual(result["availability"], "prepared")
        self.assertEqual(result["human_review"], "not-established")
        self.assertEqual(len(result["output_ids"]), 2)
        candidate = next(row for row in result["outputs"] if row["authority"] == "inferred")
        self.assertEqual(candidate["status"], "candidate")
        search = MemorySearch(self.memory)
        environment = {"account": self.memory.account, "host": self.request["host"], "workspace": self.request["workspace"]}
        packet = search.context("Cedar timing", mode="lexical", entities=["project:cedar"], environment=environment)
        self.assertIn("Dream sourced episode", json.dumps(packet))
        self.assertNotIn(candidate["id"], {entry["id"] for entry in packet["entries"]})
        self.assertNotIn(candidate["text"], json.dumps(packet))
        self.assertNotIn("Dream sourced episode", json.dumps(search.context("Cedar", mode="lexical")))
        replay = dream.finish(self.memory, self.finish_input(started, event))
        self.assertEqual(replay["reflection"], "completed")
        self.assertEqual(len(replay["outputs"]), 2)

    def test_policy_scope_capabilities_and_source_revision_guards(self):
        with self.assertRaises(StateError):
            dream.checkpoint(self.memory, self.event)
        self.configure()
        for override in ({"content_access": "session-list-only"}, {"account": "rafa@example.com"},
                         {"adapter": "invented-history-tool"}, {"authorized": False}):
            invalid = copy.deepcopy(self.event)
            invalid["capability"].update(override)
            with self.assertRaises(StateError):
                dream.checkpoint(self.memory, invalid)
        with self.assertRaises(StateError):
            dream.checkpoint(self.memory, dict(self.event, origin="dream"))
        row = dream.checkpoint(self.memory, self.event)
        self.assertEqual(dream.checkpoint(self.memory, self.event)["revision"], 1)
        with self.assertRaises(StateError):
            dream.checkpoint(self.memory, dict(self.event, text="Changed under the same revision"))
        with self.assertRaises(StateError):
            dream.checkpoint(self.memory, dict(self.event, revision="host-rev-2"))
        changed = dream.checkpoint(self.memory, dict(self.event, revision="host-rev-2",
                                                   expected_revision=1, text="Timing remains open."))
        self.assertEqual(changed["revision"], 2)
        self.assertEqual(changed["id"], row["id"])
        with self.assertRaises(StateError):
            dream.checkpoint(self.memory, dict(self.event, expected_revision=2))
        self.configure(False)
        data = {key: row[key] for key in ("domain", "kind", "title", "text", "authority", "scope",
                                        "source_refs", "metadata", "entities", "sensitivity", "allowed_uses")}
        with self.assertRaises(StateError):
            self.memory.put("capture-bypass", data, status="active")

    def test_changed_policy_source_suppression_and_claims_block_persistence(self):
        self.configure()
        event = dream.checkpoint(self.memory, self.event)
        started = self.begin()
        other = MemoryStore(self.memory.account, str(self.root))
        try:
            with self.assertRaises(StateError):
                self.begin()
            value = self.finish_input(started, event)
            with self.assertRaises(StateError):
                dream.finish(other, dict(value, token="not-the-token"))
            dream.checkpoint(other, dict(self.event, revision="host-rev-2", expected_revision=1,
                                         text="A corrected observation."))
            with self.assertRaises(StateError):
                dream.finish(self.memory, value)
        finally:
            other.close()
        started = self.begin("corrected")
        self.configure(False)
        with self.assertRaises(StateError):
            dream.finish(self.memory, dict(self.finish_input(started, event), claims=[]))
        self.configure()
        started = self.begin("suppressed")
        current = self.memory.show(event["id"])
        data = {key: value for key, value in current.items()
                if key not in {"id", "account", "revision", "status", "created_at", "updated_at"}}
        self.memory.revise(event["id"], data, "suppressed", current["revision"])
        with self.assertRaises(StateError):
            dream.finish(self.memory, dict(self.finish_input(started, event), claims=[]))

    def test_forgetting_erases_outputs_and_blocks_alternate_key_and_interrupted_run(self):
        self.configure()
        event = dream.checkpoint(self.memory, self.event)
        alternate_data = {key: value for key, value in event.items()
                          if key not in {"id", "account", "revision", "status", "created_at", "updated_at"}}
        alternate = self.memory.put("preexisting-alternate", alternate_data, status="active")
        started = self.begin()
        result = dream.finish(self.memory, self.finish_input(started, event))
        interrupted = self.begin("interrupted")
        erased = self.memory.forget(event["id"], event["revision"], evidence(event["id"], decision="forget"))
        self.assertTrue(set(result["output_ids"]) <= set(erased["forgotten_ids"]))
        self.assertIn(alternate["id"], erased["forgotten_ids"])
        self.assertEqual(dream.inspect(self.memory, started["snapshot_id"])["available"], False)
        for attempt in (started, interrupted):
            with self.assertRaises(StateError):
                dream.finish(self.memory, self.finish_input(attempt, event))
        data = {key: value for key, value in event.items()
                if key not in {"id", "account", "revision", "status", "created_at", "updated_at"}}
        data["source_refs"][0]["revision"] = "alternate-source-revision"
        with self.assertRaises(StateError):
            self.memory.put("alternate-memory-key", data, status="active")
        with self.assertRaises(StateError):
            dream.checkpoint(self.memory, self.event)
        with self.assertRaises(StateError):
            dream.checkpoint(self.memory, dict(self.event, session="alternate-session", event="alternate-event"))
        data["source_refs"][0]["ref"] = json.dumps(json.loads(data["source_refs"][0]["ref"]))
        with self.assertRaises(StateError):
            self.memory.put("alternate-serialization", data, status="active")
        self.assertNotIn("Cedar", json.dumps(self.memory.history(event["id"])))

    def test_late_arrivals_bounds_citations_and_exact_account(self):
        self.configure()
        event = dream.checkpoint(self.memory, self.event)
        preview = dream.plan(self.memory, self.request)
        earlier = dict(self.event, event="event-earlier", revision="earlier-r1",
                       locator="conversation:fixture-session/event-earlier",
                       timestamp=(datetime.fromisoformat(self.event["timestamp"]) - timedelta(days=1)).isoformat())
        dream.checkpoint(self.memory, earlier)
        with self.assertRaises(StateError):
            dream.start(self.memory, "stale-plan", {"request": self.request, "snapshot_hash": preview["snapshot_hash"],
                                                   "request_ref": "conversation:fixture-dream-request"})
        plan = dream.plan(self.memory, self.request)
        self.assertEqual(plan["events"][0]["metadata"]["event"], "event-earlier")
        with patch.object(dream, "MAX_SCAN", 1):
            self.assertTrue(dream.plan(self.memory, self.request)["coverage"]["scan_truncated"])
        started = self.begin()
        value = self.finish_input(started, event)
        value["claims"][0]["citations"][0]["quote"] = "Approved and confirmed."
        with self.assertRaises(StateError):
            dream.finish(self.memory, value)
        self.assertEqual(self.tasks.show(started["claim"]["run_id"])["state"], "active")
        other = MemoryStore("rafa@example.com", str(self.root))
        try:
            with self.assertRaises(StateError):
                dream.inspect(other, started["snapshot_id"])
        finally:
            other.close()

    def test_corrected_outputs_are_not_recalled_and_duplicate_runs_do_not_duplicate_episodes(self):
        from test_memory_search import unit_encoder
        self.configure()
        event = dream.checkpoint(self.memory, self.event)
        started = self.begin()
        dream.finish(self.memory, self.finish_input(started, event))
        second = self.begin("second")
        result = dream.finish(self.memory, dict(self.finish_input(second, event), claims=[]))
        self.assertEqual(len([row for row in self.memory.list()
                              if row.get("metadata", {}).get("dream") == "episode"]), 1)
        search = MemorySearch(self.memory, encoder=unit_encoder)
        self.assertEqual(search.index()["indexed"], 1)
        dream.checkpoint(self.memory, dict(self.event, revision="correction-r2", expected_revision=1,
                                           text="Cedar was discussed, not selected."))
        self.assertFalse(dream.inspect(self.memory, started["snapshot_id"])["sources_current"])
        self.assertEqual(dream.inspect(self.memory, second["snapshot_id"])["availability"], "unavailable")
        eligible = self.memory.eligible(environment={"account": self.memory.account, "host": self.request["host"],
                                                     "workspace": self.request["workspace"]})
        self.assertFalse(set(result["output_ids"]) & {row["id"] for row in eligible})
        self.assertEqual(search.health()["vectors"], 0)
        self.assertEqual(search.index(rebuild=True)["indexed"], 0)

    def test_source_correction_during_index_inference_cannot_reinsert_old_episode(self):
        from test_memory_search import unit_encoder
        self.configure()
        event = dream.checkpoint(self.memory, self.event)
        started = self.begin()
        dream.finish(self.memory, dict(self.finish_input(started, event), claims=[]))

        def correct_then_encode(texts):
            dream.checkpoint(self.memory, dict(self.event, revision="corrected", expected_revision=1,
                                               text="No selection was made."))
            return unit_encoder(texts)

        search = MemorySearch(self.memory, encoder=correct_then_encode)
        self.assertEqual(search.index()["indexed"], 0)
        self.assertEqual(search.health()["vectors"], 0)

    def test_output_review_does_not_confirm_memory_and_foreground_revision_does(self):
        import proactive_state
        self.configure()
        event = dream.checkpoint(self.memory, self.event)
        started = self.begin()
        result = dream.finish(self.memory, self.finish_input(started, event))
        candidate = next(row for row in result["outputs"] if row["status"] == "candidate")
        publication_id = result["output_receipt"]
        proactive_state.publication_publish(self.memory.conn, publication_id,
                                             {"status": "available", "receipt": {"kind": "local"}})
        proactive_state.publication_review(self.memory.conn, publication_id)
        self.assertEqual(dream.inspect(self.memory, started["snapshot_id"])["human_review"], "output-reviewed")
        self.assertEqual(self.memory.show(candidate["id"])["status"], "candidate")
        data = {key: value for key, value in candidate.items()
                if key not in {"id", "account", "revision", "status", "created_at", "updated_at"}}
        data["authority"] = "user_confirmed"
        with self.assertRaises(StateError):
            self.memory.revise(candidate["id"], data, "active", 1)
        reviewed = self.memory.revise(candidate["id"], data, "active", 1, evidence(candidate["id"]))
        self.assertEqual(reviewed["authority"], "user_confirmed")

    def test_read_commands_do_not_initialize_missing_memory(self):
        output, errors = io.StringIO(), io.StringIO()
        with redirect_stdout(output), redirect_stderr(errors):
            code = memory_state.main(["--account", "unconfigured@example.com", "--state-dir", str(self.root),
                                      "dream-status", "--host", "fixture", "--workspace", "fixture"])
        self.assertEqual(code, 2)
        self.assertEqual(json.loads(errors.getvalue())["code"], "not_initialized")

    def test_concurrent_corrections_have_one_revision_owner(self):
        self.configure()
        original = dream.checkpoint(self.memory, self.event)
        barrier = Barrier(2)

        def revise(number):
            memory = MemoryStore(self.memory.account, str(self.root))
            try:
                barrier.wait()
                return dream.checkpoint(memory, dict(self.event, revision="revision-" + str(number),
                                                      expected_revision=1, text="Changed " + str(number)))["revision"]
            except StateError:
                return "conflict"
            finally:
                memory.close()

        with ThreadPoolExecutor(max_workers=2) as executor:
            results = list(executor.map(revise, [2, 3]))
        self.assertCountEqual(results, [2, "conflict"])
        self.assertEqual(self.memory.show(original["id"])["revision"], 2)

    def test_expired_and_cancelled_claims_cannot_publish(self):
        self.configure()
        event = dream.checkpoint(self.memory, self.event)
        started = self.begin("expired")
        with patch("task_runs.clock", return_value=datetime.now(timezone.utc) + timedelta(hours=2)):
            with self.assertRaises(StateError):
                dream.finish(self.memory, self.finish_input(started, event))
        started = self.begin("cancelled")
        self.tasks.cancel(started["claim"]["run_id"], "User stopped preparation")
        with self.assertRaises(StateError):
            dream.finish(self.memory, self.finish_input(started, event))
        self.assertFalse(any(row.get("metadata", {}).get("dream") == "episode" for row in self.memory.list()))

    def test_forget_journal_erases_preexisting_alternate_source_identity(self):
        from memory_governance import tombstones, tombstones_preview, restore_tombstones
        self.configure()
        event = dream.checkpoint(self.memory, self.event)
        data = {key: value for key, value in event.items()
                if key not in {"id", "account", "revision", "status", "created_at", "updated_at"}}
        with self.memory.transaction():
            erased = self.memory.forget(event["id"], 1, evidence(event["id"], decision="forget"))
            bundle = tombstones(self.memory)
            self.assertIn(event["id"], erased["forgotten_ids"])
        # A separate synthetic backup contains the same evidence under a different memory key.
        backup = MemoryStore(self.memory.account, str(self.root / "backup"))
        try:
            policy = self.memory.policy()["data"]
            approval = policy_preview(backup, policy)
            set_policy(backup, policy, evidence(approval["subject_id"], decision="configure"))
            alternate = backup.put("backup-alternate", data, status="active")
            preview = tombstones_preview(backup, bundle)
            self.assertIn(alternate["id"], {row["id"] for row in preview["affected"]})
            restore_tombstones(backup, bundle, evidence(preview["subject_id"], decision="restore-erasures"))
            self.assertEqual(backup.show(alternate["id"])["status"], "forgotten")
        finally:
            backup.close()


if __name__ == "__main__":
    unittest.main()
