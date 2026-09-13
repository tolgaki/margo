import copy
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parents[1] / "skills/chief-of-staff/scripts"
sys.path.insert(0, str(SCRIPTS))
import app_proactive as app
import margo_profile
import margo_store as store
import proactive_state
from task_runs import TaskStore


def policy():
    return {"enabled": True, "timezone": "UTC", "workdays": [0, 1, 2, 3, 4],
            "slots": {"morning": ["08:00"], "sweep": ["09:00", "10:00"], "eod": ["17:00"]}, "grace_minutes": 10}


class AppProactiveTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()
        self.install = self.root / "install"
        self.env = patch.dict(os.environ, {"COPILOT_HOME": str(self.install), "MARGO_ACCOUNT": "app-fixture@example.com",
            "MARGO_CONFIG": str(self.install / "margo/config.json"), "MARGO_STATE_DIR": str(self.root / "state"),
            "MARGO_ALLOW_UNSAFE_STATE_DIR": "1"})
        self.env.start()
        store.initialize_config("app-fixture@example.com")
        self.store = TaskStore()
        app.configure(policy(), margo_profile.show()["revision"])
        self.host = "sdk:synthetic"
        self.now = datetime(2026, 9, 14, 8, 1, tzinfo=timezone.utc)
        self.clocks = [patch("app_proactive.clock", side_effect=lambda: self.now),
                       patch("task_runs.clock", side_effect=lambda: self.now),
                       patch("task_runs.utc_now", side_effect=lambda: self.now.isoformat(timespec="microseconds")),
                       patch("margo_store.utc_now", side_effect=lambda: self.now.isoformat(timespec="microseconds")),
                       patch("proactive_state.utc_now", side_effect=lambda: self.now.isoformat(timespec="microseconds")),
                       patch("work_ledger.now", side_effect=lambda: self.now)]
        for item in self.clocks:
            item.start()

    def tearDown(self):
        self.store.close()
        for item in reversed(self.clocks):
            item.stop()
        self.env.stop()
        self.temp.cleanup()

    def start(self, routine):
        return app.start({"routine": routine, "identity": {"principal": self.store.account,
            "observed_at": self.now.isoformat(), "evidence_ref": "tool:synthetic-me"}}, self.host)

    def content(self, urgent=False):
        observation = {"id": "mail-1", "revision": "r1", "title": "Fictional design review", "summary": "A design choice is requested.",
                       "web_link": "https://example.com/design", "sensitivity": "private", "ask": "Choose a design"}
        if urgent:
            observation["due_at"] = (self.now + timedelta(minutes=20)).isoformat()
        return {"summary": "Review the sourced design choice.", "candidates": [
            {"source": "mail", "source_id": "mail-1", "claim_key": "design-choice", "title": "Choose the design",
             "owner": None, "direction": "owe", "due": None, "next_step": "Review the choices"},
        ], "artifact": {"title": "Private design note", "markdown": "# Design\n\nReview the two choices.",
                       "source_keys": ["mail:mail-1"], "work_item_id": None},
            "sources": [{"source": source, "status": "complete", "kind": "enumeration",
                         "evidence_ref": "tool:synthetic-" + source, "observations": [observation] if source == "mail" else []}
                        for source in app.SOURCES]}

    def test_morning_sweep_eod_private_receipts_no_confirm_send_or_memory(self):
        with patch("socket.socket", side_effect=AssertionError("local code must not network")):
            started = self.start("morning")
            self.assertEqual(started["status"], "started")
            first = app.finish(self.content(), started["claim"], self.host)
            self.assertEqual(first["status"], "ready")
            self.assertEqual(first["display"], "notify")
            self.assertFalse(first["host_delivered"])
            replay = app.finish(self.content(), started["claim"], self.host)
            self.assertTrue(replay["replayed"])
            self.assertEqual(replay["display"], "silent")
            self.assertEqual(self.start("morning")["reason"], "slot_already_recorded")
            self.now = self.now.replace(hour=9)
            second = self.start("sweep")
            silent = app.finish(self.content(), second["claim"], self.host)
            self.assertEqual(silent["display"], "silent")
            self.now = self.now.replace(hour=17)
            last = app.finish(self.content(), self.start("eod")["claim"], self.host)
            self.assertEqual(last["display"], "notify")
            items = self.store.ledger.list("all")
            self.assertEqual(len(items["items"]), 1)
            self.assertFalse(items["items"][0]["confirmed"])
            self.assertEqual(items["items"][0]["state"], "candidate")
            self.assertEqual(items["actions"], [])
            self.assertTrue(all(item["state"] == "prepared" for item in items["records"]))
            self.assertEqual(len(proactive_state.publication_list(self.store.conn)), 3)
            tables = [row[0] for row in self.store.conn.execute("SELECT name FROM sqlite_master")]
            self.assertNotIn("memory_records", tables)
            self.assertFalse((self.install / "margo/embeddings").exists())

    def test_new_urgent_revision_notifies_once_and_does_not_consume_other_queue(self):
        self.now = self.now.replace(hour=9)
        proactive_state.queue_add(self.store.conn, {"id": "unrelated", "revision": "1", "family": "other", "scope": "fixture", "title": "Unrelated"})
        value = self.content(urgent=True)
        first = app.finish(value, self.start("sweep")["claim"], self.host)
        self.assertEqual(first["display"], "notify")
        self.assertIn("Choose a design", first["output"])
        self.now = self.now.replace(hour=10)
        second = app.finish(value, self.start("sweep")["claim"], self.host)
        self.assertEqual(second["display"], "silent")
        other = proactive_state.queue_drain(self.store.conn, family="other")
        self.assertEqual(other[0]["id"], "unrelated")

    def test_preparation_does_not_change_confirmed_work_or_approved_action(self):
        ledger = self.store.ledger
        ref = ledger.source("mail", "inbox", "prior-confirmed", "1", {"title": "Existing commitment"}, "https://example.com/prior")
        candidate = ledger.ingest({"title": "Existing commitment", "owner": None, "direction": "owe", "due": None, "source_refs": [ref]}, "prior-claim")
        def evidence(subject, decision):
            return {"kind": "human_confirmation", "actor": "dana@example.com", "statement": "I approve this exact synthetic fixture.",
                    "evidence_ref": "conversation:synthetic/turn/1", "subject_id": subject["id"], "revision": subject["revision"],
                    "decision": decision, "decided_at": self.now.isoformat()}
        confirmed = ledger.update_item(candidate["id"], candidate["revision"], "confirmed", evidence=evidence(candidate, "confirm"))
        action = ledger.propose({"kind": "mail.reply", "target": {"to": ["dana@example.com"]}, "payload": {"body": "A synthetic response."},
            "why": "Existing exact approval", "source_refs": [ref], "target_fingerprint": "prior-1", "work_item_id": confirmed["id"]})
        approved = ledger.approve(action["id"], action["revision"], action["action_hash"],
            dict(evidence(action, "approve"), action_hash=action["action_hash"]), (self.now + timedelta(minutes=30)).isoformat())
        value = self.content()
        value["artifact"]["work_item_id"] = confirmed["id"]
        app.finish(value, self.start("morning")["claim"], self.host)
        self.assertEqual(ledger.show(confirmed["id"]), confirmed)
        self.assertEqual(ledger.show(approved["id"]), approved)

    def test_partial_source_preserves_checkpoint_and_cannot_be_healthy_silence(self):
        app.finish(self.content(), self.start("morning")["claim"], self.host)
        checkpoint = {row["family"]: row["checkpoint_at"] for row in proactive_state.coverage_status(self.store.conn)}
        self.now = self.now.replace(hour=9)
        value = self.content()
        value["sources"][0].update(status="partial", error_class="timeout")
        result = app.finish(value, self.start("sweep")["claim"], self.host)
        self.assertEqual(result["status"], "partial")
        self.assertEqual(result["display"], "notify")
        mail = next(row for row in proactive_state.coverage_status(self.store.conn) if row["family"] == "app-mail")
        self.assertEqual(mail["checkpoint_at"], checkpoint["app-mail"])
        self.assertEqual(mail["status"], "partial")

    def test_identity_policy_and_claim_scope_fail_closed(self):
        wrong = {"routine": "morning", "identity": {"principal": "other@example.com",
                 "observed_at": self.now.isoformat(), "evidence_ref": "tool:identity"}}
        with self.assertRaisesRegex(store.StateError, "provider identity"):
            app.start(wrong, self.host)
        started = self.start("morning")
        with self.assertRaisesRegex(store.StateError, "another routine or host"):
            app.finish(self.content(), started["claim"], "another-host")
        change = dict(policy(), enabled=False)
        app.configure(change, margo_profile.show()["revision"])
        with self.assertRaisesRegex(store.StateError, "disabled"):
            app.finish(self.content(), started["claim"], self.host)
        self.assertEqual(self.store.ledger.list("all")["items"], [])

    def test_invalid_fields_and_approval_attempts_are_never_writes(self):
        started = self.start("morning")
        for extra in ["account", "sql", "path", "command", "approved", "execute", "confirmed"]:
            with self.subTest(extra=extra), self.assertRaises(store.StateError):
                app.finish(dict(self.content(), **{extra: True}), started["claim"], self.host)
        value = self.content()
        value["candidates"][0]["state"] = "confirmed"
        with self.assertRaises(store.StateError):
            app.finish(value, started["claim"], self.host)
        value = self.content()
        value["sources"][0]["observations"][0]["sensitivity"] = "restricted"
        with self.assertRaises(store.StateError):
            app.finish(value, started["claim"], self.host)
        self.assertEqual(self.store.ledger.list("all")["items"], [])
        self.assertEqual(proactive_state.publication_list(self.store.conn), [])

    def test_duplicate_start_and_overlapping_slots_are_bounded(self):
        def begin(_):
            return self.start("morning")
        with ThreadPoolExecutor(max_workers=4) as pool:
            results = list(pool.map(begin, range(4)))
        self.assertEqual(sum(item["status"] == "started" for item in results), 1)
        self.now += timedelta(minutes=1)
        self.assertEqual(self.start("morning")["reason"], "slot_already_recorded")
        self.now = self.now.replace(hour=9)
        self.assertEqual(self.start("morning")["reason"], "outside_slot_grace")
        self.assertEqual(self.start("sweep")["status"], "started", "Expired claim cannot duplicate old slot; next due slot may proceed")

    def test_policy_defaults_and_weekday_slot_boundaries_dst(self):
        value = policy()
        self.assertEqual(app.due(dict(value, enabled=False), "morning", self.now)["reason"], "local_preparation_disabled")
        self.assertEqual(app.due(value, "morning", self.now.replace(hour=7, minute=59))["status"], "skipped")
        self.assertEqual(app.due(value, "morning", self.now.replace(minute=10))["status"], "skipped")
        self.assertEqual(app.due(value, "morning", self.now.replace(day=13))["reason"], "outside_workdays")
        eastern = dict(value, timezone="America/New_York", workdays=[6],
                       slots={"morning": ["01:30"], "sweep": ["02:30"], "eod": ["17:00"]})
        first = app.due(eastern, "morning", datetime(2026, 11, 1, 5, 31, tzinfo=timezone.utc))
        repeated = app.due(eastern, "morning", datetime(2026, 11, 1, 6, 31, tzinfo=timezone.utc))
        self.assertEqual(first["key"], repeated["key"], "Repeated DST wall slot has one identity")
        self.assertEqual(app.due(eastern, "sweep", datetime(2026, 3, 8, 7, 31, tzinfo=timezone.utc))["status"], "skipped")
        midnight = app.due(dict(value, timezone="Pacific/Auckland", slots={"morning": ["00:05"], "sweep": ["09:00"], "eod": ["17:00"]}),
                           "morning", datetime(2026, 9, 13, 12, 6, tzinfo=timezone.utc))
        self.assertEqual(midnight["local_date"], "2026-09-14")

    def test_budget_reservation_exhaustion_no_generic_retry(self):
        started = self.start("morning")
        for index in range(16):
            app.charge({"event_id": "read:%s" % index}, started["claim"], self.host)
        with self.assertRaisesRegex(store.StateError, "reservation exhausted"):
            app.charge({"event_id": "read:overflow"}, started["claim"], self.host)
        self.now += timedelta(minutes=15)
        with self.assertRaises(store.StateError):
            app.finish(self.content(), started["claim"], self.host)
        self.assertEqual(self.store.ledger.list("all")["items"], [])
