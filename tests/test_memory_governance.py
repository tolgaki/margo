import hashlib
import os
import shutil
import sys
import unittest
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "skills/chief-of-staff/scripts"))

import margo_store
from memory_store import MemoryStore, SCHEMA_V1
from memory_governance import (
    export_preview, export_recipe, maintain, policy_preview, restore_tombstones,
    set_policy, tombstones, tombstones_preview,
)
from test_memory_store import evidence, memory_data, stamp


class GovernanceTests(unittest.TestCase):
    def setUp(self):
        self.root = ROOT / (".memory-governance-" + uuid.uuid4().hex)
        self.root.mkdir(mode=0o700)
        self.environment = patch.dict(os.environ, {"MARGO_ALLOW_UNSAFE_STATE_DIR": "1"})
        self.environment.start()
        self.memory = MemoryStore(account="synthetic-governance", state_root=str(self.root))

    def tearDown(self):
        self.memory.close()
        self.environment.stop()
        shutil.rmtree(self.root)

    def configure(self, **overrides):
        data = self.memory.policy()["data"]
        data.update(overrides)
        preview = policy_preview(self.memory, data)
        return set_policy(self.memory, data, evidence(preview["subject_id"], decision="configure"))

    def test_capture_is_opt_in_scoped_and_cannot_confirm_a_preference(self):
        with self.assertRaises(margo_store.StateError):
            self.memory.capture("disabled", memory_data())
        self.configure(capture={"enabled": True, "domains": ["user"], "kinds": ["episode", "preference"],
                                "scopes": ["personal"], "source_kinds": ["tool_result"]},
                       review_days={"episode": 7})
        captured = self.memory.capture("allowed", memory_data())
        self.assertEqual(captured["status"], "active")
        self.assertIn("review_after", captured)
        with self.assertRaises(margo_store.StateError):
            self.memory.capture("scope-denied", memory_data(scope="another-scope"))
        with self.assertRaises(margo_store.StateError):
            self.memory.capture("fake-confirmation", memory_data(kind="preference", authority="user_confirmed"))
        candidate = self.memory.capture("inference", memory_data(kind="preference", authority="inferred"))
        self.assertEqual(candidate["status"], "candidate")

    def test_stale_policy_approval_cannot_expand_capture(self):
        data = self.memory.policy()["data"]
        preview = policy_preview(self.memory, data)
        self.configure(usage_enabled=True)
        with self.assertRaises(margo_store.StateError):
            set_policy(self.memory, data, evidence(preview["subject_id"], decision="configure"))

    def test_doctor_distinguishes_current_staleness_from_suppressed_history(self):
        from margo_doctor import memory_health
        data = memory_data(review_after=stamp(-60))
        row = self.memory.put("stale-health", data, status="active")
        with patch("memory_encoder.status_local", return_value={"status": "available"}):
            health = memory_health(self.memory.account, str(self.root))
            self.assertEqual(health["status"], "attention-needed")
            self.memory.revise(row["id"], data, "suppressed", 1)
            health = memory_health(self.memory.account, str(self.root))
            self.assertEqual(health["status"], "available")

    def test_recapture_does_not_reactivate_a_suppressed_observation(self):
        self.configure(capture={"enabled": True, "domains": ["user"], "kinds": ["episode"],
                                "scopes": ["personal"], "source_kinds": ["tool_result"]})
        row = self.memory.capture("suppressed-observation", memory_data())
        data = {key: value for key, value in row.items()
                if key not in {"id", "account", "revision", "status", "created_at", "updated_at"}}
        self.memory.revise(row["id"], data, "suppressed", 1)
        with self.assertRaises(margo_store.StateError):
            self.memory.capture("suppressed-observation", memory_data(), revision=2)
        self.assertEqual(self.memory.show(row["id"])["status"], "suppressed")

    def test_work_source_recapture_is_idempotent_without_an_explicit_source_revision(self):
        from work_ledger import Ledger
        ledger = Ledger(account=self.memory.account, state_root=str(self.root))
        try:
            source = ledger.source("mail", "inbox", "synthetic", "1", {"quote": "Synthetic source"},
                                   "https://example.com/source")
            self.configure(capture={"enabled": True, "domains": ["user"], "kinds": ["episode"],
                                    "scopes": ["personal"], "source_kinds": ["work_source"]},
                           review_days={"episode": 7})
            data = memory_data(source_refs=[{"kind": "work_source", "ref": source["source_id"]}])
            first = self.memory.capture("same-source", data)
            second = self.memory.capture("same-source", data)
            self.assertEqual(first, second)
        finally:
            ledger.close()

    def test_suppression_preserves_history_and_requires_review_to_reenable(self):
        data = memory_data(kind="preference", authority="user_confirmed")
        identity = self.memory.record_id("user", "preference")
        row = self.memory.put("preference", data, status="active", revision=1, evidence=evidence(identity))
        self.memory.revise(identity, data, "suppressed", row["revision"], evidence(identity, decision="suppress"))
        self.assertEqual(self.memory.eligible(), [])
        self.assertEqual(len(self.memory.history(identity)), 2)
        with self.assertRaises(margo_store.StateError):
            self.memory.revise(identity, data, "active", 2)
        self.memory.revise(identity, data, "active", 2, evidence(identity, 2, "confirm"))
        self.assertEqual(len(self.memory.eligible()), 1)

    def test_temporal_edges_are_reviewed_and_supersession_is_not_implicit(self):
        first = self.memory.put("project-a", memory_data(kind="project"), status="active")
        second = self.memory.put("project-b", memory_data(kind="project"), status="active")
        edge = {"valid_from": stamp(60), "source_refs": first["source_refs"]}
        with self.assertRaises(margo_store.StateError):
            self.memory.link(first["id"], second["id"], "depends_on", 1, data=edge, target_revision=1)
        self.memory.link(first["id"], second["id"], "depends_on", 1,
                         evidence(first["id"], decision="relate"), edge, 1)
        self.assertEqual(self.memory.active_links(first["id"]), [])
        self.assertEqual(len(self.memory.links(first["id"])), 1)
        self.memory.unlink(first["id"], second["id"], "depends_on", 2,
                           evidence(first["id"], 2, "unrelate"))
        self.assertEqual(self.memory.links(first["id"]), [])

    def test_retention_is_bounded_and_never_assumes_a_policy(self):
        first = self.memory.put("old-a", memory_data(), status="active")
        second = self.memory.put("old-b", memory_data(), status="active")
        old = (datetime.now(timezone.utc) - timedelta(days=30)).isoformat()
        with self.memory.conn:
            self.memory.conn.execute("UPDATE memory_records SET updated_at=?", (old,))
        self.assertEqual(maintain(self.memory)["forgotten_ids"], [])
        self.configure(retention_days={"episode": 7})
        result = maintain(self.memory, limit=1)
        self.assertEqual(result["processed"], 1)
        self.assertEqual(len(result["forgotten_ids"]), 1)
        self.assertIsNotNone(result["next_cursor"])
        final = maintain(self.memory, limit=1, after=result["next_cursor"])
        self.assertEqual(set(result["forgotten_ids"] + final["forgotten_ids"]), {first["id"], second["id"]})

    def test_derived_memory_sources_are_revalidated_and_erased_atomically(self):
        from memory_search import MemorySearch
        source = self.memory.put("source", memory_data(), status="active")
        data = memory_data(source_refs=[{"kind": "memory_record", "ref": source["id"], "revision": "1"}])
        derived = self.memory.put("derived", data, status="active")
        search = MemorySearch(self.memory, encoder=lambda texts: {
            "model_fingerprint": "synthetic", "dimensions": 2, "vectors": [[1, 0] for _ in texts]})
        search.index()
        result = self.memory.forget(source["id"], 1, evidence(source["id"], decision="forget"))
        self.assertIn(derived["id"], result["derived_forgotten_ids"])
        self.assertEqual(search.health()["vectors"], 0)
        self.assertEqual(self.memory.eligible(), [])
        self.assertTrue(all(not item["data"] for item in self.memory.history(derived["id"])))

    def test_old_derivation_history_is_not_left_behind_after_correction(self):
        root = self.memory.put("original-source", memory_data(text="Synthetic private phrase"), status="active")
        derived = self.memory.put("historically-derived", memory_data(
            text="Synthetic private phrase",
            source_refs=[{"kind": "memory_record", "ref": root["id"], "revision": "1"}]), status="active")
        self.memory.revise(derived["id"], memory_data(text="New independent observation"), "active", 1)
        preview = self.memory.forget_preview(root["id"])
        self.assertIn(derived["id"], [row["id"] for row in preview["affected"]])
        self.memory.forget(root["id"], 1, evidence(root["id"], decision="forget"))
        self.assertNotIn("Synthetic private phrase", str(self.memory.history(derived["id"])))
        with self.assertRaises(margo_store.StateError):
            self.memory.put("new-derived-copy", memory_data(
                source_refs=[{"kind": "memory_record", "ref": root["id"], "revision": "1"}]))

    def test_usage_is_opt_in_idempotent_and_contains_no_prompt(self):
        row = self.memory.put("usage", memory_data(), status="active")
        refs = [{"id": row["id"], "revision": 1}]
        with self.assertRaises(margo_store.StateError):
            self.memory.record_usage(refs, "drafting")
        self.configure(usage_enabled=True)
        self.memory.record_usage(refs, "drafting", event_id="synthetic-usage")
        self.assertTrue(self.memory.record_usage(refs, "drafting", event_id="synthetic-usage")["replayed"])
        with self.assertRaises(margo_store.StateError):
            self.memory.record_usage(refs, "calendar", event_id="synthetic-usage")
        saved = self.memory.usage(row["id"])
        self.assertNotIn(row["text"], str(saved))
        self.memory.forget(row["id"], 1, evidence(row["id"], decision="forget"))
        self.assertEqual(self.memory.usage(row["id"]), [])

    def test_usage_policy_read_is_serialized_with_policy_updates(self):
        row = self.memory.put("serialized-usage", memory_data(), status="active")
        self.configure(usage_enabled=True)
        original = self.memory.policy

        def assert_locked():
            self.assertTrue(self.memory.conn.in_transaction)
            return original()

        with patch.object(self.memory, "policy", side_effect=assert_locked):
            self.memory.record_usage([{"id": row["id"], "revision": 1}], "drafting")

    def test_reviewed_tombstones_prevent_backup_resurrection(self):
        row = self.memory.put("resurrected", memory_data(), status="active")
        key_hash = self.memory._row(row["id"])["key_hash"]
        bundle = {"format": 1, "account": self.memory.account,
                  "tombstones": [{"id": row["id"], "key_hash": key_hash, "forgotten_at": stamp()}]}
        preview = tombstones_preview(self.memory, bundle)
        restore_tombstones(self.memory, bundle, evidence(preview["subject_id"], decision="restore-erasures"))
        self.assertEqual(self.memory.show(row["id"])["status"], "forgotten")
        with self.assertRaises(margo_store.StateError):
            self.memory.put("resurrected", memory_data())
        self.assertEqual(len(tombstones(self.memory)["tombstones"]), 1)

    def test_sanitized_export_is_exact_reviewed_private_and_never_overwrites(self):
        identity = self.memory.record_id("agent", "recipe")
        data = memory_data(domain="agent", kind="lesson", authority="user_confirmed")
        self.memory.put("recipe", data, status="active", revision=1, evidence=evidence(identity))
        recipe = {"title": "Prepare a review", "goal": "Allow preparation time.",
                  "preconditions": "The user agreed a preparation policy.",
                  "steps": "Read the current context; prepare a private agenda.",
                  "limitations": "Never move a meeting without exact approval."}
        preview = export_preview(self.memory, identity, recipe)
        consent = evidence(preview["subject_id"], decision="export")
        destination = self.root / "recipe.md"
        changed = dict(recipe, steps="Different instructions.")
        with self.assertRaises(margo_store.StateError):
            export_recipe(self.memory, identity, changed, consent, destination)
        result = export_recipe(self.memory, identity, recipe, consent, destination)
        self.assertFalse(result["published"])
        self.assertNotIn(identity, destination.read_text())
        self.assertNotIn("source_refs", destination.read_text())
        with self.assertRaises(FileExistsError):
            export_recipe(self.memory, identity, recipe, consent, destination)
        with self.assertRaises(margo_store.StateError):
            export_preview(self.memory, identity, dict(recipe, steps="Contact dana@example.com"))

    def test_explicit_v1_migration_preserves_records_and_defaults_capture_off(self):
        account = "synthetic-legacy-memory"
        connection = margo_store.connect(account, str(self.root))
        connection.executescript(SCHEMA_V1)
        data = memory_data()
        key_hash = hashlib.sha256((account + "\0user\0legacy").encode()).hexdigest()
        identity = "mem_" + key_hash[:40]
        with connection:
            connection.execute("INSERT INTO memory_meta VALUES('schema_version','1')")
            connection.execute("INSERT INTO margo_meta VALUES('memory_schema_version','1')")
            connection.execute("INSERT INTO memory_records VALUES(?,?,?,?,?,?,?,?,?,?,?)",
                               (identity, account, key_hash, 1, "user", "episode", "active",
                                margo_store.canonical_json(data), "synthetic-hash", stamp(), stamp()))
        connection.close()
        with self.assertRaises(margo_store.StateError):
            MemoryStore(account, str(self.root))
        migrated = MemoryStore(account, str(self.root), migrate=True)
        try:
            self.assertEqual(migrated.show(identity)["text"], data["text"])
            self.assertFalse(migrated.policy()["data"]["capture"]["enabled"])
            self.assertEqual(migrated.health()["schema_version"], "2")
        finally:
            migrated.close()


if __name__ == "__main__":
    unittest.main()
