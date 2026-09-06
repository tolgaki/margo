"""Behavior regressions from the adversarial memory review; synthetic state only."""

import contextlib
import hashlib
import io
import json
import os
import sqlite3
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = ROOT / "skills/chief-of-staff/scripts"
sys.path.insert(0, str(SCRIPTS))

from memory_store import MAX_MEMORY_TEXT_CHARS, MAX_SOURCE_FILE_BYTES, MemoryStore, search_text
from memory_governance import policy_preview, set_policy
from memory_learning import import_preferences, preferences_plan
from memory_search import MemorySearch
from margo_store import StateError, canonical_json
from test_memory_store import evidence, memory_data


def data_only(record):
    return {key: value for key, value in record.items()
            if key not in {"id", "account", "revision", "status", "created_at", "updated_at"}}


class ReviewRegressionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()
        self.env = dict(os.environ, MARGO_ALLOW_UNSAFE_STATE_DIR="1",
                        COPILOT_HOME=str(self.root / "home"),
                        MARGO_EMBEDDING_MODEL_DIR=str(self.root / "model"))
        self.environment = patch.dict(os.environ, self.env)
        self.environment.start()
        self.store = MemoryStore("synthetic-dual-review", str(self.root / "state"))

    def tearDown(self):
        self.store.close()
        self.environment.stop()
        self.temp.cleanup()

    def cli(self, *args, state_root=None, timeout=5):
        return subprocess.run(
            [sys.executable, "-B", str(SCRIPTS / "memory_state.py"), "--account", self.store.account,
             "--state-dir", str(state_root or self.root / "state"), *args],
            env=self.env, capture_output=True, text=True, timeout=timeout)

    def configure_capture(self):
        rules = self.store.policy()["data"]
        rules["capture"] = {"enabled": True, "domains": ["user"], "kinds": ["episode"],
                            "scopes": ["personal"], "source_kinds": ["tool_result"]}
        preview = policy_preview(self.store, rules)
        set_policy(self.store, rules, evidence(preview["subject_id"], decision="configure"))

    def test_passive_capture_keeps_dispute_and_stale_review_flags(self):
        self.configure_capture()
        for status in ("disputed", "stale"):
            with self.subTest(status=status):
                row = self.store.capture(status, memory_data())
                flagged = self.store.revise(row["id"], data_only(row), status, row["revision"])
                updated = self.store.capture(status, memory_data(text="A newer observation, still requiring review."),
                                             revision=flagged["revision"])
                self.assertEqual(updated["status"], status)
                self.assertNotIn(row["id"], [item["id"] for item in self.store.eligible()])
                with self.assertRaises(StateError):
                    self.store.revise(updated["id"], data_only(updated), "active", updated["revision"])
                active = self.store.revise(updated["id"], data_only(updated), "active", updated["revision"],
                                           evidence(updated["id"], updated["revision"], "confirm"))
                self.assertEqual(active["status"], "active")

    def test_memory_representation_limit_includes_title_scope_and_entities(self):
        data = memory_data(text="x", entities=["user"])
        prefix = len(search_text(data)) - 1
        data["text"] = "x" * (MAX_MEMORY_TEXT_CHARS - prefix)
        self.assertEqual(len(search_text(data)), MAX_MEMORY_TEXT_CHARS)
        self.store.put("at-limit", data)
        with self.assertRaisesRegex(StateError, "representation exceeds"):
            self.store.put("over-limit", dict(data, text=data["text"] + "x"))

    def test_new_bare_entity_names_are_rejected(self):
        for value in ("Dana", "person:", ":person", "person:display name"):
            with self.subTest(value=value), self.assertRaisesRegex(StateError, "scoped identifiers"):
                self.store.put("invalid-" + value, memory_data(entities=[value]))

    def test_legacy_bare_aliases_cannot_bridge_scoped_people(self):
        from memory_context import identity_key
        first_identity = {"provider": "directory", "scope": "first-scope", "external_id": "person-a"}
        records = []
        for key, identity in (("first", first_identity),
                              ("second", dict(first_identity, scope="second-scope", external_id="person-b"))):
            record = self.store.put(key, memory_data(kind="person", title="Dana", identity=identity), status="active")
            # Simulate persisted data accepted before entity validation, without rewriting history.
            legacy = dict(data_only(record), entities=["Dana"])
            with self.store.conn:
                self.store.conn.execute("UPDATE memory_records SET data=? WHERE id=?",
                                        (canonical_json(legacy), record["id"]))
            records.append(record)
        packet = MemorySearch(self.store).context("unmatched-query", entities=[identity_key(first_identity)],
                                                  mode="lexical")
        self.assertEqual([entry["id"] for entry in packet["entries"]], [records[0]["id"]])

    def test_context_and_graph_preserve_non_copyable_policy(self):
        row = self.store.put("reasoning", memory_data(title="Private rationale", routines=["drafting"],
                              allowed_uses=["reasoning"], sensitivity="private"), status="active")
        search = MemorySearch(self.store)
        packet = search.context("Private rationale", routine="drafting", mode="lexical")
        for entry in (packet["entries"][0], search.graph(row["id"], routine="drafting")["nodes"][0]):
            self.assertEqual(entry["allowed_uses"], ["reasoning"])
            self.assertEqual(entry["sensitivity"], "private")
            self.assertFalse(entry["copyable_to_draft"])
        self.assertEqual(search.search("Private rationale", routine="drafting", usage="drafting",
                                       mode="lexical")["results"], [])

    def test_source_hash_is_shared_only_within_each_eligibility_pass(self):
        source = self.root / "shared.txt"
        content = b"a" * 65536
        source.write_bytes(content)
        refs = [{"kind": "file", "ref": str(source), "revision": hashlib.sha256(content).hexdigest()}]
        for number in range(20):
            self.store.put("source-%d" % number, memory_data(source_refs=refs), status="active")
        calls = []
        original = Path.open

        def tracked(path, *args, **kwargs):
            if path == source and args and args[0] == "rb":
                calls.append(path)
            return original(path, *args, **kwargs)

        with patch.object(Path, "open", tracked):
            self.assertEqual(len(self.store.eligible()), 20)
            self.assertEqual(len(calls), 1)
            self.assertEqual(len(self.store.eligible()), 20)
            self.assertEqual(len(calls), 2)
        oversized = self.root / "oversized.txt"
        oversized.write_bytes(b"b" * (MAX_SOURCE_FILE_BYTES + 1))
        self.store.put("large-source", memory_data(source_refs=[{
            "kind": "file", "ref": str(oversized),
            "revision": hashlib.sha256(oversized.read_bytes()).hexdigest(),
        }]), status="active")
        self.assertEqual(len(self.store.eligible()), 20)

    def test_source_change_during_inference_survives_memoization(self):
        source = self.root / "changing.txt"
        source.write_text("before")
        refs = [{"kind": "file", "ref": str(source),
                 "revision": hashlib.sha256(source.read_bytes()).hexdigest()}]
        self.store.put("changing", memory_data(title="Review preparation", source_refs=refs), status="active")
        info = source.stat()

        def changed(_texts):
            source.write_text("after!")
            os.utime(source, ns=(info.st_atime_ns, info.st_mtime_ns))
            return {"model_fingerprint": "synthetic", "dimensions": 2, "vectors": [[1, 0]]}

        self.assertEqual(MemorySearch(self.store, encoder=changed).search("Review preparation")["results"], [])

    def test_read_commands_do_not_initialize_and_record_ids_are_pure(self):
        missing = self.root / "never-created"
        for command in ("list", "status", "policy"):
            with self.subTest(command=command):
                result = self.cli(command, state_root=missing)
                self.assertEqual(result.returncode, 2, result.stdout)
                self.assertEqual(json.loads(result.stderr)["code"], "not_initialized")
                self.assertFalse(missing.exists())
        identity = self.cli("record-id", "user", "future", state_root=missing)
        self.assertEqual(identity.returncode, 0, identity.stderr)
        self.assertEqual(json.loads(identity.stdout)["id"], self.store.record_id("user", "future"))
        self.assertFalse(missing.exists())

    def test_read_only_store_and_cli_work_while_writer_is_open(self):
        row = self.store.put("readable", memory_data(), status="active")
        with self.store.transaction():
            self.store.conn.execute("INSERT INTO memory_meta VALUES('synthetic-write','pending')")
            for command in (("list",), ("policy",), ("inspect", row["id"])):
                result = self.cli(*command, timeout=3)
                self.assertEqual(result.returncode, 0, result.stderr)
        database = next((self.root / "state").rglob("margo.sqlite3"))
        before = database.read_bytes()
        for command in (("list",), ("policy",), ("inspect", row["id"])):
            self.assertEqual(self.cli(*command).returncode, 0)
        self.assertEqual(database.read_bytes(), before)
        reader = MemoryStore(self.store.account, str(self.root / "state"), read_only=True)
        try:
            self.assertEqual(reader.show(row["id"])["id"], row["id"])
            with self.assertRaisesRegex(sqlite3.OperationalError, "readonly"):
                reader.conn.execute("INSERT INTO memory_meta VALUES('forbidden-write','no')")
        finally:
            reader.close()

    def test_lexical_reads_do_not_create_a_missing_semantic_index(self):
        self.store.put("indexless", memory_data(title="Indexless context"), status="active")
        result = self.cli("search", "Indexless", "--mode", "lexical")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len(json.loads(result.stdout)["results"]), 1)
        self.assertEqual(self.store.conn.execute(
            "SELECT count(*) FROM sqlite_master WHERE name LIKE 'semantic_%'").fetchone()[0], 0)

    def test_optional_runtime_absence_does_not_fail_core_doctor_health(self):
        import margo_doctor
        with patch.object(margo_doctor, "state_health", return_value={
                "status": "ok", "account_configured": True, "all_clear": True}), \
                patch.object(margo_doctor, "inspect_snapshot", return_value={"status": "healthy"}), \
                patch.object(margo_doctor, "inspect_installation_manifest", return_value={"status": "healthy"}), \
                patch.object(margo_doctor, "configuration_fields", return_value={"status": "complete"}):
            for configured, expected in ((False, 0), (True, 1)):
                with self.subTest(configured=configured), patch("memory_encoder.status_local", return_value={
                        "status": "missing_runtime", "configured": configured}):
                    output = io.StringIO()
                    with contextlib.redirect_stdout(output):
                        code = margo_doctor.main(["--strict", "--account", self.store.account,
                                                 "--state-dir", str(self.root / "state")])
                    self.assertEqual(code, expected, output.getvalue())
                    self.assertEqual(json.loads(output.getvalue())["memory"]["embedding_runtime"]["status"], "missing_runtime")

    def test_changed_preferences_keep_guard_and_explain_recovery(self):
        path = self.root / "preferences.md"
        path.write_text("## About me\nI prefer preparation before reviews.\n")
        preview = preferences_plan(self.store, path)
        import_preferences(self.store, path, evidence(preview["subject_id"], decision="import"))
        path.write_text(path.read_text() + "\nDo not schedule before 09:00.\n")
        result = MemorySearch(self.store).context("review", mode="lexical")
        self.assertEqual(result["status"], "blocked")
        self.assertEqual(result["entries"], [])
        self.assertIn("preferences-preview", canonical_json(result))
        self.assertIn("preferences-import", canonical_json(result))
        self.assertNotIn("Do not schedule before", canonical_json(result))
        self.assertIn("preferences-preview", self.store.health()["recovery_actions"][0])

    def test_explicit_rebuild_recovers_missing_cache_table_without_losing_memory(self):
        row = self.store.put("cache-recovery", memory_data(), status="active")
        encoder = lambda texts: {"model_fingerprint": "synthetic", "dimensions": 2,
                                 "vectors": [[1, 0] for _ in texts]}
        search = MemorySearch(self.store, encoder=encoder)
        search.index()
        with self.store.conn:
            self.store.conn.execute("DROP TABLE semantic_documents")
        self.assertEqual(self.cli("list").returncode, 0)
        self.assertEqual(self.cli("search", "review", "--mode", "lexical").returncode, 2)
        repaired = MemorySearch(self.store, encoder=encoder, rebuild_schema=True)
        self.assertEqual(repaired.index(rebuild=True)["indexed"], 1)
        self.assertEqual(self.store.show(row["id"])["text"], row["text"])

    def test_cli_rebuild_repairs_empty_cache_and_drains_deletes_without_model(self):
        row = self.store.put("cache-delete", memory_data(), status="active")
        MemorySearch(self.store)
        self.store.forget(row["id"], row["revision"], evidence(row["id"], row["revision"], "forget"))
        with self.store.conn:
            self.store.conn.execute("DROP TABLE semantic_documents")
        result = self.cli("index", "--rebuild")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["remaining_jobs"], 0)
        self.assertEqual(self.store.show(row["id"])["status"], "forgotten")

    def test_stale_delete_snapshot_cannot_purge_a_newer_completed_index(self):
        row = self.store.put("delete-race", memory_data(), status="active")
        encoder = lambda texts: {"model_fingerprint": "synthetic", "dimensions": 2,
                                 "vectors": [[1, 0] for _ in texts]}
        search = MemorySearch(self.store, encoder=encoder)
        search.index()
        stale = self.store.revise(row["id"], data_only(row), "stale", row["revision"])
        old_jobs = self.store.pending_jobs()
        current = self.store.revise(row["id"], data_only(stale), "active", stale["revision"],
                                    evidence(row["id"], stale["revision"], "confirm"))
        search.index()
        with patch.object(self.store, "pending_jobs", side_effect=[old_jobs, []]):
            result = search.index(rebuild=True)
        self.assertEqual(result["deleted_jobs"], 0)
        self.assertEqual(result["skipped_changed"], 1)
        indexed = self.store.conn.execute("SELECT revision FROM semantic_vectors WHERE memory_id=?", (row["id"],)).fetchone()
        self.assertEqual(indexed["revision"], current["revision"])


if __name__ == "__main__":
    unittest.main()
