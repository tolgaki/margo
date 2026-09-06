import hashlib
import os
import shutil
import sqlite3
import sys
import unittest
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = ROOT / "skills" / "chief-of-staff" / "scripts"
sys.path.insert(0, str(SCRIPTS))

from memory_store import MemoryStore, search_text
from margo_store import StateError, state_path
from work_ledger import Ledger


def stamp(minutes=0):
    return (datetime.now(timezone.utc) + timedelta(minutes=minutes)).isoformat()


def evidence(subject, revision=1, decision="confirm"):
    return {
        "kind": "human_confirmation",
        "actor": "alex@example.com",
        "statement": "I reviewed this exact synthetic memory change.",
        "evidence_ref": "conversation:synthetic/session/turn/1",
        "subject_id": subject,
        "revision": revision,
        "decision": decision,
        "decided_at": stamp(),
    }


def memory_data(**overrides):
    value = {
        "domain": "user",
        "kind": "episode",
        "title": "Synthetic review preparation",
        "text": "A fictional review benefited from preparation time.",
        "authority": "source_observed",
        "scope": "personal",
        "source_refs": [{"kind": "tool_result", "ref": "tool:synthetic-result"}],
    }
    value.update(overrides)
    return value


class MemoryStoreTests(unittest.TestCase):
    def setUp(self):
        self.root = ROOT / (".memory-store-test-" + uuid.uuid4().hex)
        self.root.mkdir(mode=0o700)
        self.environment = patch.dict(
            os.environ, {"MARGO_ALLOW_UNSAFE_STATE_DIR": "1"}
        )
        self.environment.start()
        self.store = MemoryStore(
            account="synthetic-memory-account", state_root=str(self.root)
        )

    def tearDown(self):
        self.store.close()
        self.environment.stop()
        shutil.rmtree(str(self.root))

    def test_idempotent_input_and_rejection_are_stable(self):
        data = memory_data()
        first = self.store.put("rejected-example", data, status="rejected")
        replay = self.store.put(
            "rejected-example", data, status="rejected", revision=99
        )
        self.assertEqual(first, replay)
        self.assertEqual(first["revision"], 1)
        self.assertEqual(self.store.health()["by_status"]["rejected"], 1)

    def test_file_sources_require_hash_and_become_ineligible_after_change(self):
        path = self.root / "profile.txt"
        path.write_text("Saved fact", encoding="utf-8")
        with self.assertRaises(StateError):
            self.store.put("no-hash", memory_data(source_refs=[{"kind": "file", "ref": str(path)}]), status="active")
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        self.store.put("file-fact", memory_data(source_refs=[
            {"kind": "file", "ref": str(path), "revision": digest}]), status="active")
        self.assertEqual(len(self.store.eligible()), 1)
        path.write_text("Changed fact", encoding="utf-8")
        self.assertEqual(self.store.eligible(), [])

    def test_old_import_approval_cannot_overwrite_newer_confirmed_preference(self):
        from memory_learning import import_preferences, preferences_plan
        path = self.root / "preferences.md"
        path.write_text("## Scheduling defaults\nProtect a preparation block.\n", encoding="utf-8")
        plan = preferences_plan(self.store, path)
        approval = evidence(plan["subject_id"], decision="import")
        imported = import_preferences(self.store, path, approval)
        self.assertTrue(import_preferences(self.store, path, approval)["replayed"])
        identity = imported["memory_ids"][0]
        current = self.store.show(identity)
        data = {key: value for key, value in current.items()
                if key not in {"id", "account", "revision", "status", "created_at", "updated_at"}}
        data["text"] = "A newer explicitly confirmed preference."
        self.store.put(plan["records"][0]["key"], data, status="active", revision=1,
                       evidence=evidence(identity, 1, "edit"))
        with self.assertRaises(StateError):
            import_preferences(self.store, path, approval)
        self.assertEqual(self.store.show(identity)["text"], data["text"])

    def test_ever_confirmed_memory_cannot_be_downgraded_without_evidence(self):
        data = memory_data(authority="user_confirmed")
        identity = self.store.record_id("user", "protected-history")
        self.store.put("protected-history", data, status="active", revision=1, evidence=evidence(identity))
        self.store.put("protected-history", data, status="stale", revision=1, evidence=evidence(identity, 1, "stale"))
        with self.assertRaises(StateError):
            self.store.put("protected-history", dict(data, authority="source_observed"), status="active", revision=2)

    def test_index_job_contract_and_conditional_completion(self):
        row = self.store.put("index-contract", memory_data(), status="active")
        jobs = self.store.pending_jobs()
        self.assertEqual(
            set(jobs[0]),
            {"memory_id", "revision", "operation", "content_hash"},
        )
        self.assertEqual(jobs[0]["operation"], "upsert")
        self.assertEqual(
            jobs[0]["content_hash"],
            hashlib.sha256(search_text(row).encode("utf-8")).hexdigest(),
        )
        self.assertFalse(self.store.complete_job(row["id"], row["revision"] + 1))
        self.assertTrue(self.store.complete_job(row["id"], row["revision"]))
        self.assertFalse(self.store.complete_job(row["id"], row["revision"]))

    def test_active_confirmation_is_bound_and_negative_evidence_is_rejected(self):
        data = memory_data(
            kind="preference", authority="user_confirmed",
            title="Synthetic scheduling preference",
            text="Keep a fictional preparation block.",
        )
        identity = self.store.record_id("user", "confirmed-preference")
        with self.assertRaises(StateError):
            self.store.put("confirmed-preference", data, status="active")
        with self.assertRaises(StateError):
            self.store.put(
                "confirmed-preference",
                data,
                status="active",
                revision=1,
                evidence=evidence(identity, decision="reject"),
            )
        active = self.store.put(
            "confirmed-preference",
            data,
            status="active",
            revision=1,
            evidence=evidence(identity),
        )
        changed = dict(data, text="Keep two fictional preparation blocks.")
        with self.assertRaises(StateError):
            self.store.put(
                "confirmed-preference", changed, status="active", revision=1
            )
        revised = self.store.put(
            "confirmed-preference",
            changed,
            status="active",
            revision=1,
            evidence=evidence(identity, revision=1, decision="edit"),
        )
        self.assertEqual(revised["revision"], 2)

    def test_forgetting_erases_history_queues_delete_and_denies_reimport(self):
        row = self.store.put("forget-me", memory_data(), status="active")
        forgotten = self.store.forget(
            row["id"], row["revision"], evidence(row["id"], decision="forget")
        )
        self.assertEqual(forgotten["status"], "forgotten")
        current = self.store.show(row["id"])
        self.assertEqual(current["status"], "forgotten")
        self.assertNotIn("title", current)
        self.assertNotIn("text", current)
        self.assertNotIn("evidence", current)
        listed = self.store.list(status="forgotten")
        self.assertEqual(listed, [current])
        self.assertNotIn("text", listed[0])
        revisions = self.store.conn.execute(
            "SELECT data,evidence,content_hash FROM memory_revisions WHERE memory_id=?",
            (row["id"],),
        ).fetchall()
        self.assertTrue(revisions)
        self.assertTrue(all(r["data"] == "{}" and r["evidence"] is None for r in revisions))
        job = self.store.pending_jobs()[0]
        self.assertEqual(job["operation"], "delete")
        self.assertNotIn("Synthetic", str(job))
        with self.assertRaises(StateError):
            self.store.put("forget-me", memory_data())

    def test_forgetting_recursively_invalidates_derived_memories(self):
        base = self.store.put("base", memory_data(title="Base"), status="active")
        child = self.store.put(
            "child", memory_data(domain="agent", kind="lesson", title="Derived"),
            status="candidate",
        )
        link = self.store.link(
            child["id"], base["id"], "derives_from", child["revision"]
        )
        result = self.store.forget(
            base["id"],
            base["revision"],
            evidence(base["id"], base["revision"], "forget"),
        )
        self.assertEqual(result["derived_forgotten_ids"], [child["id"]])
        self.assertEqual(self.store.show(child["id"])["status"], "forgotten")
        self.assertEqual(self.store.links(base["id"]), [])
        self.assertEqual(link["relation"], "derives_from")

    def test_links_return_only_bounded_graph_fields(self):
        base = self.store.put("linked-base", memory_data(title="Base"), status="active")
        child = self.store.put("linked-child", memory_data(title="Child"))
        self.store.link(child["id"], base["id"], "related_to", child["revision"])
        self.assertEqual(
            self.store.links(base["id"]),
            [{
                "source_id": child["id"],
                "target_id": base["id"],
                "relation": "related_to",
            }],
        )

    def test_work_source_revision_staleness_excludes_memory(self):
        ledger = Ledger(
            account="synthetic-memory-account", state_root=str(self.root)
        )
        try:
            source = ledger.source(
                "mail", "inbox", "message-1", "v1",
                {"quote": "Synthetic source evidence."},
                "https://example.invalid/message/1",
            )
            data = memory_data(
                source_refs=[{
                    "kind": "work_source",
                    "ref": source["source_id"],
                    "revision": source["revision"],
                    "verified_at": stamp(),
                }]
            )
            row = self.store.put("source-backed", data, status="active")
            self.assertEqual([r["id"] for r in self.store.eligible()], [row["id"]])
            ledger.source(
                "mail", "inbox", "message-1", "v2",
                {"quote": "Changed synthetic source evidence."},
                "https://example.invalid/message/1",
            )
            self.assertEqual(self.store.eligible(), [])
        finally:
            ledger.close()

    def test_routine_environment_and_time_filters(self):
        general = self.store.put("general", memory_data(title="General"), status="active")
        calendar = self.store.put(
            "calendar",
            memory_data(
                title="Calendar only",
                routines=["calendar"],
                metadata={"environment_requirements": {"host": "desktop", "tools": {"mail": "v2"}}},
            ),
            status="active",
        )
        self.store.put(
            "future",
            memory_data(title="Future", valid_from=stamp(60)),
            status="active",
        )
        self.store.put(
            "expired",
            memory_data(title="Expired", valid_to=stamp(-1)),
            status="active",
        )
        self.store.put(
            "review-overdue",
            memory_data(title="Review overdue", review_after=stamp(-1)),
            status="active",
        )
        self.assertEqual([r["id"] for r in self.store.eligible()], [general["id"]])
        selected = self.store.eligible(
            routine="calendar",
            environment={"host": "desktop", "tools": {"mail": "v2", "calendar": "v1"}},
        )
        self.assertEqual({r["id"] for r in selected}, {general["id"], calendar["id"]})
        self.assertNotIn(
            calendar["id"],
            {
                r["id"]
                for r in self.store.eligible(
                    routine="calendar",
                    environment={"host": "desktop", "tools": {"mail": "v1"}},
                )
            },
        )

    def test_accounts_are_isolated(self):
        row = self.store.put("same-key", memory_data())
        other = MemoryStore(
            account="different-synthetic-account", state_root=str(self.root)
        )
        try:
            other_row = other.put("same-key", memory_data())
            self.assertNotEqual(row["id"], other_row["id"])
            with self.assertRaises(StateError):
                other.show(row["id"])
        finally:
            other.close()

    def test_schema_loss_is_not_silently_recreated(self):
        account = self.store.account
        self.store.close()
        _, database = state_path(account, str(self.root))
        connection = sqlite3.connect(str(database))
        with connection:
            connection.execute("DROP TABLE memory_jobs")
        connection.close()
        with self.assertRaises(StateError):
            MemoryStore(account=account, state_root=str(self.root))
        self.store = MemoryStore.__new__(MemoryStore)
        self.store.conn = sqlite3.connect(":memory:")

    def test_concurrent_stale_revision_is_rejected(self):
        first = self.store.put("concurrent", memory_data())
        other = MemoryStore(
            account="synthetic-memory-account", state_root=str(self.root)
        )
        try:
            changed = memory_data(text="First concurrent edit.")
            self.store.put("concurrent", changed, revision=first["revision"])
            with self.assertRaises(StateError):
                other.put(
                    "concurrent",
                    memory_data(text="Conflicting concurrent edit."),
                    revision=first["revision"],
                )
        finally:
            other.close()


if __name__ == "__main__":
    unittest.main()
