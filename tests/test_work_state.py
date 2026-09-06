import importlib
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import threading
import unittest
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = ROOT / "skills" / "chief-of-staff" / "scripts"
sys.path.insert(0, str(SCRIPTS))
work = importlib.import_module("work_ledger")
products = importlib.import_module("work_productivity")
cli = importlib.import_module("work_state")


def stamp(minutes=0):
    return (datetime.now(timezone.utc) + timedelta(minutes=minutes)).isoformat()


def evidence(subject, revision=1, decision="confirm", action_hash=None):
    value = {"kind": "human_confirmation", "actor": "alex@example.com",
             "statement": "I reviewed this exact revision and approve the stated change.",
             "evidence_ref": "conversation:fictional/session/turn/42", "subject_id": subject,
             "revision": revision, "decision": decision, "decided_at": stamp()}
    if action_hash:
        value["action_hash"] = action_hash
    return value


class WorkLedgerTests(unittest.TestCase):
    """Real transactional SQLite; root policy is independently owned by margo_store tests."""

    def setUp(self):
        self.path = ROOT / (".work-state-test-" + uuid.uuid4().hex)
        self.path.mkdir(mode=0o700)

        def connect(account=None, state_root=None):
            if not account:
                raise work.StateError("explicit configured account required")
            connection = sqlite3.connect(str(self.path / (work.digest(account) + ".sqlite")), timeout=3)
            connection.row_factory = sqlite3.Row
            connection.execute("PRAGMA foreign_keys=ON")
            return connection

        self.patches = [patch.object(work.margo_store, "connect", side_effect=connect),
                        patch.object(work.margo_store, "resolve_account", side_effect=lambda account: account)]
        for item in self.patches:
            item.start()
        self.ledger = work.Ledger(account="fictional-account", state_root=str(self.path))
        self.products = products.Productivity(self.ledger)
        self.ref = self.ledger.source("mail", "inbox", "message-1", "v1",
                                      {"quote": "Please review the fictional specification."},
                                      "https://example.com/mail/1")

    def tearDown(self):
        self.ledger.close()
        for item in reversed(self.patches):
            item.stop()
        shutil.rmtree(str(self.path))

    def item(self, key="claim-1"):
        return self.ledger.ingest(
            {"title": "Review fictional specification", "owner": None, "due": None,
             "direction": "owe", "source_refs": [self.ref]}, key,
        )

    def action_data(self):
        return {"kind": "mail.reply", "target": {"message_id": "message-1", "recipients": ["blair@example.com"]},
                "payload": {"body": "The fictional review is complete."}, "why": "Explicit follow-up request",
                "source_refs": [self.ref], "target_fingerprint": "target-v1", "work_item_id": self.item()["id"]}

    def approved(self):
        action = self.ledger.propose(self.action_data())
        return self.ledger.approve(action["id"], 1, action["action_hash"],
                                   evidence(action["id"], decision="approve", action_hash=action["action_hash"]), stamp(60))

    def fresh(self):
        return {"checked_at": stamp(), "target_fingerprint": "target-v1", "source_refs": [self.ref]}

    def receipt(self, state="succeeded"):
        value = {"kind": "tool_result", "reference": "tool:fictional-result-1", "outcome": state, "recorded_at": stamp()}
        if state == "failed":
            value["definitive_no_effect"] = True
        return value

    def test_default_account_fails_and_accounts_isolated(self):
        with self.assertRaises(work.StateError):
            work.Ledger()
        item = self.item()
        other = work.Ledger(account="other-fictional-account")
        try:
            with self.assertRaises(work.StateError):
                other.show(item["id"])
            source = other.source("mail", "inbox", "message-1", "v1", {"quote": "Different account"}, "https://example.com/1")
            self.assertNotEqual(source["source_id"], self.ref["source_id"])
        finally:
            other.close()

    def test_evidence_only_bootstrap_remains_candidate_with_partial_coverage(self):
        observed = {"identity_kind": "evidence_only", "canonical_id": None,
                    "excerpt": "A fictional review was requested.",
                    "coverage": {"later_resolution": "partial", "sent_mail": "partial"}}
        identity = "evidence:" + work.digest(observed)
        ref = self.ledger.source("review-evidence", "fictional-bootstrap", identity,
                                 work.digest(observed), observed, "https://example.com/evidence-only")
        data = {"title": "Review a possible obligation", "owner": None, "due": None,
                "direction": "owe", "source_refs": [ref],
                "notes": "Review only. Current obligation status unknown; later-resolution and sent-mail coverage partial."}
        candidate = self.ledger.ingest(data, "bootstrap-claim")
        replay = self.ledger.ingest(data, "bootstrap-claim")
        self.assertEqual(candidate["id"], replay["id"])
        self.assertEqual(candidate["state"], "candidate")
        self.assertFalse(candidate["confirmed"])
        self.assertIsNone(candidate["confirmation"])
        self.assertIsNone(candidate["data"]["due"])
        self.assertIsNone(candidate["sources"][0]["evidence"]["canonical_id"])
        self.assertEqual(self.ledger.list("all")["actions"], [])

    def test_exact_replay_restart_and_semantic_nonmerge(self):
        original = self.item()
        self.assertEqual(self.item()["id"], original["id"])
        self.assertNotEqual(self.item("other-extraction-key")["id"], original["id"])
        self.ledger.close()
        self.ledger = work.Ledger(account="fictional-account")
        self.assertEqual(self.item()["id"], original["id"])
        data = self.action_data()
        changed = self.ledger.show(original["id"])["data"]
        changed["title"] = "A different claim"
        with self.assertRaises(work.StateError):
            self.ledger.ingest(changed, "claim-1")
        self.assertEqual(len(self.ledger.list("all")["items"]), 2)
        self.assertEqual(data["work_item_id"], original["id"])

    def test_rejected_revision_is_not_proposed_again(self):
        item = self.item()
        old_ref = self.ref
        self.ledger.update_item(item["id"], 1, state="rejected")
        self.assertEqual(self.item()["state"], "rejected")
        self.ref = self.ledger.source("mail", "inbox", "message-1", "v2", {"quote": "Changed request"}, "https://example.com/mail/1")
        self.assertNotEqual(self.item()["id"], item["id"])
        self.ref = old_ref
        self.assertEqual(self.item()["state"], "rejected")

    def test_confirmation_and_conditional_mutation(self):
        item = self.item()
        with self.assertRaises(work.StateError):
            self.ledger.update_item(item["id"], 1, state="confirmed")
        confirmed = self.ledger.update_item(item["id"], 1, state="confirmed", evidence=evidence(item["id"]))
        self.assertTrue(confirmed["confirmed"])
        for kwargs in ({"state": "resolved"}, {"patch": {"owner": "blair@example.com"}}, {"state": "deferred", "patch": {"deferred_until": stamp(10)}}):
            with self.assertRaises(work.StateError):
                self.ledger.update_item(item["id"], 2, **kwargs)
        with self.assertRaises(work.StateError):
            self.ledger.update_item(item["id"], 1, state="resolved", evidence=evidence(item["id"]))
        resolved = self.ledger.update_item(item["id"], 2, state="resolved", evidence=evidence(item["id"], 2, "resolve"))
        self.assertEqual(resolved["state"], "resolved")
        self.assertEqual(len(self.ledger.history(item["id"])["revisions"]), 3)

    def test_invalid_transitions_and_cycles(self):
        a, b, c = self.item("a"), self.item("b"), self.item("c")
        with self.assertRaises(work.StateError):
            self.ledger.update_item(a["id"], 1, state="resolved", evidence=evidence(a["id"]))
        self.ledger.relate(a["id"], "depends_on", b["id"], 1)
        self.ledger.relate(b["id"], "depends_on", c["id"], 1)
        with self.assertRaises(work.StateError):
            self.ledger.relate(c["id"], "depends_on", a["id"], 1)
        with self.assertRaises(work.StateError):
            self.ledger.relate(a["id"], "blocks", c["id"], 2)
        self.ledger.relate(a["id"], "tracked_in", "planner:fictional-task", 2)
        self.assertEqual(self.ledger.show(a["id"])["state"], "candidate")

    def test_source_revision_marks_approval_stale_and_old_replay_does_not_revert(self):
        action = self.approved()
        updated = self.ledger.source("mail", "inbox", "message-1", "v2", {"quote": "No reply is needed now"}, "https://example.com/mail/1")
        self.assertEqual(self.ledger.show(action["id"])["state"], "stale")
        with self.assertRaises(work.StateError):
            self.ledger.begin(action["id"], 1, self.fresh())
        self.ledger.source("mail", "inbox", "message-1", "v1",
                           {"quote": "Please review the fictional specification."}, "https://example.com/mail/1")
        self.assertEqual(self.ledger.show(self.ref["source_id"])["current_revision"], updated["revision"])
        with self.assertRaises(work.StateError):
            self.ledger.source("mail", "inbox", "message-1", "v2", "Another changed body", "https://example.com/mail/1")

    def test_edit_invalidates_exact_human_approval(self):
        action = self.approved()
        data = self.action_data()
        data["target"]["recipients"] = ["casey@example.com"]
        changed = self.ledger.edit_action(action["id"], 1, data)
        self.assertEqual(changed["revision"], 2)
        self.assertNotEqual(changed["action_hash"], action["action_hash"])
        self.assertIsNotNone(changed["approvals"][0]["invalidated_at"])
        with self.assertRaises(work.StateError):
            self.ledger.begin(action["id"], 2, self.fresh())
        with self.assertRaises(work.StateError):
            self.ledger.approve(action["id"], 2, action["action_hash"], evidence(action["id"], 2), stamp(30))
        history = self.ledger.history(action["id"])
        self.assertEqual(history["revisions"][0]["data"]["target"]["recipients"], ["blair@example.com"])

    def test_approval_requires_human_scope_hash_and_expiry(self):
        action = self.ledger.propose(self.action_data())
        valid = evidence(action["id"], decision="approve", action_hash=action["action_hash"])
        for change in ({"kind": "observed_mail"}, {"subject_id": "other-id"}, {"revision": 99},
                       {"action_hash": "other"}, {"evidence_ref": "mail:message-1"}):
            invalid = dict(valid, **change)
            with self.assertRaises(work.StateError):
                self.ledger.approve(action["id"], 1, action["action_hash"], invalid, stamp(30))
        with self.assertRaises(work.StateError):
            self.ledger.approve(action["id"], 1, action["action_hash"], valid, stamp(-1))
        with self.assertRaises(work.StateError):
            self.ledger.approve(action["id"], 1, action["action_hash"], valid, stamp(8 * 24 * 60))

    def test_freshness_checks_and_transactional_double_start(self):
        for fresh in (dict(self.fresh(), target_fingerprint="changed"), dict(self.fresh(), checked_at=stamp(-6)),
                      dict(self.fresh(), source_refs=[])):
            action = self.approved()
            with self.assertRaises(work.StateError):
                self.ledger.begin(action["id"], 1, fresh)
        action = self.approved()
        results = []
        barrier = threading.Barrier(2)

        def start():
            ledger = work.Ledger(account="fictional-account")
            try:
                barrier.wait()
                results.append(ledger.begin(action["id"], 1, self.fresh()))
            except work.StateError:
                results.append("blocked")
            finally:
                ledger.close()

        threads = [threading.Thread(target=start) for _ in range(2)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join(timeout=5)
        self.assertEqual(results.count("blocked"), 1)
        self.assertEqual(sum(isinstance(r, dict) for r in results), 1)
        self.assertEqual(len(self.ledger.show(action["id"])["executions"]), 1)

    def test_uncertain_write_never_retries_and_receipt_reconciliation(self):
        action = self.approved()
        attempt = self.ledger.begin(action["id"], 1, self.fresh())
        self.ledger.finish(attempt["attempt_id"], "outcome_unknown")
        for operation in (lambda: self.ledger.begin(action["id"], 1, self.fresh()),
                          lambda: self.ledger.edit_action(action["id"], 1, self.action_data()),
                          lambda: self.ledger.finish(attempt["attempt_id"], "succeeded", reconcile=True)):
            with self.assertRaises(work.StateError):
                operation()
        self.ledger.close()
        self.ledger = work.Ledger(account="fictional-account")
        result = self.ledger.finish(attempt["attempt_id"], "succeeded", self.receipt(), reconcile=True)
        self.assertEqual(result["state"], "succeeded")
        with self.assertRaises(work.StateError):
            self.ledger.finish(attempt["attempt_id"], "succeeded", self.receipt(), reconcile=True)

    def test_interrupted_execution_and_expired_approval_fail_closed(self):
        action = self.approved()
        self.ledger.conn.execute("UPDATE work_approvals SET expires_at=?", (stamp(-1),))
        self.ledger.conn.commit()
        with self.assertRaises(work.StateError):
            self.ledger.begin(action["id"], 1, self.fresh())
        self.assertEqual(self.ledger.show(action["id"])["executions"], [])
        action = self.approved()
        attempt = self.ledger.begin(action["id"], 1, self.fresh())
        self.ledger.close()
        self.ledger = work.Ledger(account="fictional-account")
        self.assertEqual(self.ledger.show(action["id"])["state"], "executing")
        with self.assertRaises(work.StateError):
            self.ledger.begin(action["id"], 1, self.fresh())
        settled = self.ledger.finish(attempt["attempt_id"], "failed", self.receipt("failed"), reconcile=True)
        self.assertEqual(settled["state"], "failed")

    def test_failed_requires_no_effect_and_completed_sources_are_history(self):
        action = self.approved()
        attempt = self.ledger.begin(action["id"], 1, self.fresh())
        no_proof = self.receipt("failed")
        no_proof.pop("definitive_no_effect")
        with self.assertRaises(work.StateError):
            self.ledger.finish(attempt["attempt_id"], "failed", no_proof)
        self.ledger.finish(attempt["attempt_id"], "succeeded", self.receipt())
        self.ledger.source("mail", "inbox", "message-1", "v2", "Later message", "https://example.com/mail/1")
        result = self.ledger.show(action["id"])
        self.assertFalse(result["stale"])
        self.assertTrue(result["historical_sources_changed"])
        self.assertNotIn(action["id"], [a["id"] for a in self.ledger.list("problems")["actions"]])

    def test_partial_and_failed_outcomes_do_not_automatically_retry(self):
        action = self.approved()
        attempt = self.ledger.begin(action["id"], 1, self.fresh())
        result = self.ledger.finish(attempt["attempt_id"], "partial", self.receipt("partial"))
        self.assertEqual(result["state"], "partial")
        with self.assertRaises(work.StateError):
            self.ledger.edit_action(action["id"], 1, self.action_data())
        with self.assertRaises(work.StateError):
            self.ledger.finish(attempt["attempt_id"], "failed", self.receipt("failed"), reconcile=True)
        self.ledger.finish(attempt["attempt_id"], "succeeded", self.receipt(), reconcile=True)
        separate = self.approved()
        attempt = self.ledger.begin(separate["id"], 1, self.fresh())
        self.ledger.finish(attempt["attempt_id"], "failed", self.receipt("failed"))
        changed = self.ledger.edit_action(separate["id"], 1, self.action_data())
        self.assertEqual(changed["state"], "ready")
        with self.assertRaises(work.StateError):
            self.ledger.begin(separate["id"], 2, self.fresh())

    def test_dismiss_and_defer_preserve_work_and_sources(self):
        action = self.approved()
        item_id = action["work_item_id"]
        self.ledger.disposition(action["id"], 1, "dismissed")
        self.assertEqual(self.ledger.show(item_id)["state"], "candidate")
        self.assertEqual(len(self.ledger.show(self.ref["source_id"])["revisions"]), 1)
        other = self.ledger.propose(self.action_data())
        self.ledger.disposition(other["id"], 1, "deferred", stamp(60))
        self.assertNotIn(other["id"], [r["id"] for r in self.ledger.list("decisions")["actions"]])
        self.assertIn(other["id"], [r["id"] for r in self.ledger.list("waiting")["actions"]])

    def test_panel_hash_preconditions_and_disposition_revision_conflicts(self):
        action = self.ledger.propose(self.action_data())
        for mutation in (
                lambda: self.ledger.edit_action(action["id"], 1, self.action_data(), expected_hash="wrong-hash"),
                lambda: self.ledger.disposition(action["id"], 1, "dismissed", expected_hash="wrong-hash"),
                lambda: self.ledger.disposition(action["id"], 1, "deferred", stamp(60), expected_hash="wrong-hash")):
            with self.assertRaises(work.StateError):
                mutation()
        deferred = self.ledger.disposition(action["id"], 1, "deferred", stamp(60), expected_hash=action["action_hash"])
        self.assertEqual(deferred["revision"], 2)
        self.assertNotEqual(deferred["action_hash"], action["action_hash"])
        with self.assertRaises(work.StateError):
            self.ledger.disposition(action["id"], 1, "dismissed", expected_hash=action["action_hash"])
        dismissed = self.ledger.disposition(action["id"], 2, "dismissed", expected_hash=deferred["action_hash"])
        self.assertEqual(dismissed["revision"], 3)
        with self.assertRaises(work.StateError):
            self.ledger.edit_action(action["id"], 2, self.action_data(), expected_hash=deferred["action_hash"])

    def test_import_preview_repeat_and_hand_edit_detection(self):
        path = self.path / "legacy.md"
        path.write_text("# Commitments\n- Maybe a promise someday.\n", encoding="utf-8")
        preview = self.ledger.import_preview(path)
        self.assertFalse(preview["safe_structured"])
        with self.assertRaises(work.StateError):
            self.ledger.import_commitments(path, preview["digest"], evidence("import:" + preview["digest"], decision="import"))
        structured = self.path / "reviewed.json"
        data = {"title": "Confirmed fictional commitment", "owner": "alex@example.com", "direction": "owe", "due": None,
                "source_refs": [self.ref]}
        structured.write_text(json.dumps({"schema_version": 1, "items": [{"legacy_id": "legacy-1", "state": "confirmed", "data": data}]}), encoding="utf-8")
        preview = self.ledger.import_preview(structured)
        approval = evidence("import:" + preview["digest"], decision="import")
        result = self.ledger.import_commitments(structured, preview["digest"], approval)
        self.assertTrue(result["imported"])
        self.assertFalse(self.ledger.import_commitments(structured, preview["digest"], approval)["imported"])
        self.assertEqual(len(self.ledger.list("all")["items"]), 1)
        with self.assertRaises(work.StateError):
            self.ledger.export_commitments(path)
        view = self.path / "view.md"
        self.ledger.export_commitments(view)
        self.ledger.export_commitments(view)
        self.assertEqual(os.stat(view).st_mode & 0o777, 0o600)
        view.write_text(view.read_text() + "- Hand edit\n")
        with self.assertRaises(work.StateError):
            self.ledger.export_commitments(view)
        self.assertIn("Hand edit", view.read_text())

    def test_import_is_atomic_and_changes_require_explicit_review(self):
        path = self.path / "bad.json"
        valid = {"title": "Fictional", "owner": None, "due": None, "direction": "own", "source_refs": [self.ref]}
        items = [{"legacy_id": "one", "state": "confirmed", "data": valid},
                 {"legacy_id": "two", "state": "confirmed", "data": {"title": "Missing required data"}}]
        path.write_text(json.dumps({"schema_version": 1, "items": items}))
        preview = self.ledger.import_preview(path)
        with self.assertRaises(work.StateError):
            self.ledger.import_commitments(path, preview["digest"], evidence("import:" + preview["digest"], decision="import"))
        self.assertEqual(self.ledger.list("all")["items"], [])

    def outcome(self, title="Fictional delivery"):
        return {"week": "2026-09-07", "title": title, "owner": "alex@example.com", "definition_of_done": "Review accepted",
                "due": "2026-09-11", "effort": {"min_minutes": 60, "max_minutes": 120},
                "next_step": "Read the specification", "allocation": None, "blocker": "Awaiting specification",
                "source_refs": [self.ref]}

    def test_outcomes_unknowns_explicit_approval_and_three_limit(self):
        data = self.outcome()
        data["effort"] = None
        record = self.products.put("outcome", "first", data)
        with self.assertRaises(work.StateError):
            self.products.put("outcome", "first", data, "agreed", 1, evidence(record["id"]))
        for number in range(3):
            identity = "goal-%s" % number
            record_id = self.products.record_id("outcome", identity)
            self.products.put("outcome", identity, self.outcome(), "agreed", evidence=evidence(record_id))
        last = self.products.record_id("outcome", "fourth")
        with self.assertRaises(work.StateError):
            self.products.put("outcome", "fourth", self.outcome(), "agreed", evidence=evidence(last))
        active = self.products.record_id("outcome", "goal-0")
        with self.assertRaises(work.StateError):
            self.products.put("outcome", "goal-0", self.outcome(), "active", revision=1)

    def test_negative_evidence_never_confirms_work_or_outcomes(self):
        item = self.item()
        with self.assertRaises(work.StateError):
            self.ledger.update_item(item["id"], 1, state="confirmed",
                                    evidence=evidence(item["id"], decision="reject"))
        self.assertFalse(self.ledger.show(item["id"])["confirmed"])
        outcome_id = self.products.record_id("outcome", "negative-decision")
        with self.assertRaises(work.StateError):
            self.products.put("outcome", "negative-decision", self.outcome(), "agreed",
                              evidence=evidence(outcome_id, decision="reject"))

    def test_missing_work_history_is_not_recreated_on_open(self):
        item = self.item()
        self.ledger.conn.execute("DROP TABLE work_events")
        self.ledger.conn.execute("DROP TABLE work_item_revisions")
        self.ledger.conn.commit()
        self.ledger.close()
        with self.assertRaises(work.StateError):
            work.Ledger(account="fictional-account")
        connection = sqlite3.connect(str(self.path / (work.digest("fictional-account") + ".sqlite")))
        try:
            tables = {row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")}
            self.assertNotIn("work_events", tables)
            self.assertEqual(connection.execute("SELECT revision FROM work_items WHERE id=?", (item["id"],)).fetchone()[0], 1)
        finally:
            connection.close()

    def test_work_schema_creation_is_atomic(self):
        with patch.object(work, "SCHEMA", work.SCHEMA + "\nTHIS IS NOT SQL;"):
            with self.assertRaises(sqlite3.Error):
                work.Ledger(account="second-fictional-account")
        connection = sqlite3.connect(str(self.path / (work.digest("second-fictional-account") + ".sqlite")))
        try:
            self.assertEqual(connection.execute(
                "SELECT count(*) FROM sqlite_master WHERE name LIKE 'work_%'").fetchone()[0], 0)
        finally:
            connection.close()

    def test_complete_legacy_schema_can_be_adopted_once(self):
        item = self.item()
        self.ledger.conn.execute("CREATE TABLE margo_meta (key TEXT PRIMARY KEY,value TEXT NOT NULL)")
        self.ledger.conn.execute("DROP TABLE work_meta")
        self.ledger.conn.commit()
        self.ledger.close()
        self.ledger = work.Ledger(account="fictional-account")
        self.assertEqual(self.ledger.show(item["id"])["revision"], 1)
        self.assertEqual(self.ledger.conn.execute(
            "SELECT value FROM margo_meta WHERE key='work_schema_version'").fetchone()[0], "1")
        self.ledger.conn.execute("DROP TABLE work_meta")
        self.ledger.conn.commit()
        self.ledger.close()
        with self.assertRaises(work.StateError):
            work.Ledger(account="fictional-account")

    def test_deferred_candidate_must_be_confirmed_before_activation(self):
        item = self.item()
        self.ledger.update_item(item["id"], 1, state="deferred", patch={"deferred_until": stamp(60)})
        with self.assertRaises(work.StateError):
            self.ledger.update_item(item["id"], 2, state="active", evidence=evidence(item["id"], 2))
        self.ledger.update_item(item["id"], 2, state="confirmed", evidence=evidence(item["id"], 2))
        active = self.ledger.update_item(item["id"], 3, state="active",
                                        evidence=evidence(item["id"], 3, "activate"))
        self.assertTrue(active["confirmed"])
        self.assertIsNotNone(active["confirmation"])
        with self.assertRaises(work.StateError):
            self.ledger.update_item(item["id"], 4, state="cancelled")

    def test_explicit_opt_out_state_strips_learning_text_without_flag(self):
        item = self.item()
        record_id = self.products.record_id("feedback", "explicit-opt-out")
        record = self.products.put(
            "feedback", "explicit-opt-out",
            {"subject_id": item["id"], "subject_revision": 1,
             "correction": "PRIVATE_CORRECTION_SENTINEL", "reason": "PRIVATE_REASON_SENTINEL"},
            state="do_not_learn", evidence=evidence(record_id),
        )
        self.assertTrue(record["data"]["do_not_learn"])
        self.assertNotIn("correction", record["data"])
        self.assertNotIn("PRIVATE_CORRECTION_SENTINEL", json.dumps(self.ledger.history(record_id)))
        self.assertNotIn("PRIVATE_REASON_SENTINEL", json.dumps(self.ledger.history(record_id)))

    def meeting(self, occurrence="occurrence-1"):
        return {"series_id": "series-1", "occurrence_id": occurrence,
                "scheduled_start": "2026-09-07T10:00:00-07:00", "scheduled_end": "2026-09-07T11:00:00-07:00",
                "source_refs": [self.ref], "attendance": "unknown", "work_item_ids": [self.item()["id"]]}

    def test_meeting_recap_persistence_budget_and_series(self):
        data = self.meeting()
        meeting = self.products.put("meeting", "occurrence-1", data)
        data["recap_retry"] = {"attempts": 0, "max_attempts": 1, "status": "pending", "next_check_at": stamp(-1)}
        meeting = self.products.put("meeting", "occurrence-1", data, "recap_pending", 1)
        result = self.products.recap_retry(meeting["id"], 2, "pending", "tool:fictional-no-recap", stamp(60))
        self.assertEqual(result["data"]["recap_retry"]["status"], "exhausted")
        self.assertEqual(result["data"]["attendance"], "unknown")
        with self.assertRaises(work.StateError):
            self.products.recap_retry(meeting["id"], 3, "pending", "tool:fictional-no-recap", stamp(120))
        self.assertEqual(len(self.ledger.list("all")["items"]), 1)
        updated = dict(result["data"], occurrence_id="different-occurrence")
        with self.assertRaises(work.StateError):
            self.products.put("meeting", "occurrence-1", updated, revision=3)

    def test_recap_policy_blocked_stops_and_retry_time_is_enforced(self):
        data = self.meeting()
        data["recap_retry"] = {"attempts": 0, "max_attempts": 3, "status": "pending", "next_check_at": stamp(60)}
        pending = self.products.put("meeting", "occurrence-1", data, "recap_pending")
        with self.assertRaises(work.StateError):
            self.products.recap_retry(pending["id"], 1, "pending", "tool:too-early", stamp(120))
        data["recap_retry"]["next_check_at"] = stamp(-1)
        self.products.put("meeting", "occurrence-1", data, "recap_pending", 1)
        blocked = self.products.recap_retry(pending["id"], 2, "blocked", "tool:fictional-policy-denial")
        self.assertEqual(blocked["data"]["recap_retry"]["status"], "blocked")
        with self.assertRaises(work.StateError):
            self.products.recap_retry(pending["id"], 3, "pending", "tool:retry-denial", stamp(120))

    def feedback(self, identity="feedback-1", do_not_learn=False):
        action = self.ledger.propose(self.action_data())
        record_id = self.products.record_id("feedback", identity)
        return self.products.put("feedback", identity,
                                 {"subject_id": action["id"], "subject_revision": 1,
                                  "correction": "Use shorter invitations for this project only.",
                                  "reason": None, "do_not_learn": do_not_learn},
                                 evidence=evidence(record_id))

    def rule(self, feedback_id):
        return {"wording": "Use concise invitations for fictional project syncs.", "scope": "project:fictional",
                "routines": ["meeting-prep"], "authority": "advisory_only", "conflict_check": "No conflicts found",
                "feedback_ids": [feedback_id], "source_refs": []}

    def test_feedback_opt_out_and_rule_activation_revoke(self):
        omitted = self.feedback("omitted", True)
        self.assertEqual(omitted["state"], "do_not_learn")
        self.assertNotIn("correction", omitted["data"])
        self.assertNotIn("shorter invitations", json.dumps(self.ledger.history(omitted["id"])))
        with self.assertRaises(work.StateError):
            self.products.put("rule", "invalid", self.rule(omitted["id"]))
        feedback = self.feedback()
        rule = self.products.put("rule", "short-invites", self.rule(feedback["id"]))
        with self.assertRaises(work.StateError):
            self.products.put("rule", "short-invites", rule["data"], "active", 1)
        active = self.products.put("rule", "short-invites", rule["data"], "active", 1, evidence(rule["id"]))
        revoked = self.products.put("rule", "short-invites", active["data"], "revoked", 2, evidence(rule["id"], 2, "revoke"))
        self.assertEqual(revoked["state"], "revoked")
        restored = self.products.put("rule", "short-invites", revoked["data"], "active", 3, evidence(rule["id"], 3))
        self.assertEqual(restored["state"], "active")
        with self.assertRaises(work.StateError):
            self.products.put("rule", "authority", dict(self.rule(feedback["id"]), authority="allow_sending"))

    def test_feedback_can_reference_old_revision_but_never_silent_learning(self):
        action = self.ledger.propose(self.action_data())
        self.ledger.edit_action(action["id"], 1, self.action_data())
        feedback_id = self.products.record_id("feedback", "historical-correction")
        data = {"subject_id": action["id"], "subject_revision": 1, "correction": "Make this one invitation shorter."}
        with self.assertRaises(work.StateError):
            self.products.put("feedback", "historical-correction", data)
        recorded = self.products.put("feedback", "historical-correction", data, evidence=evidence(feedback_id))
        self.assertEqual(recorded["data"]["subject_revision"], 1)
        self.assertEqual(self.products.put("feedback", "historical-correction", data)["revision"], 1)
        self.assertFalse(any(r["kind"] == "rule" for r in self.ledger.list("all")["records"]))

    def test_rule_supersession_cycle_is_rejected(self):
        feedback = self.feedback()
        first = self.products.put("rule", "first-rule", self.rule(feedback["id"]))
        second_data = dict(self.rule(feedback["id"]), supersedes=first["id"])
        second = self.products.put("rule", "second-rule", second_data)
        with self.assertRaises(work.StateError):
            self.products.put("rule", "first-rule", dict(first["data"], supersedes=second["id"]), revision=1)

    def test_meeting_carry_forward_reuses_work_and_only_open_topics(self):
        item = self.item()
        data = self.meeting()
        data["topics"] = [{"id": "topic-1", "title": "Review issue", "owner": None, "created_at": stamp(),
                           "why": "No decision yet", "state": "open"},
                          {"id": "topic-2", "title": "Closed question", "owner": None, "created_at": stamp(),
                           "why": "Already answered", "state": "resolved"}]
        source_id = self.products.record_id("meeting", "occurrence-1")
        source = self.products.put("meeting", "occurrence-1", data, "reviewed", evidence=evidence(source_id))
        next_data = self.meeting("occurrence-2")
        next_data["scheduled_start"] = "2026-09-14T10:00:00-07:00"
        next_data["scheduled_end"] = "2026-09-14T11:00:00-07:00"
        target = self.products.put("meeting", "occurrence-2", next_data)
        carried = self.products.carry_forward(source["id"], 1, target["id"], 1, evidence(source["id"], decision="carry_forward"))
        self.assertEqual(carried["target"]["data"]["work_item_ids"], [item["id"]])
        self.assertEqual([t["id"] for t in carried["target"]["data"]["topics"]], ["topic-1"])
        self.assertEqual(carried["source"]["state"], "carried_forward")
        self.assertEqual(len(self.ledger.list("all")["items"]), 1)
        with self.assertRaises(work.StateError):
            self.products.carry_forward(source["id"], 1, target["id"], 1, evidence(source["id"], decision="carry_forward"))

    def artifact(self):
        return {"artifact_kind": "decision_memo", "title": "Fictional review decision",
                "markdown": "# Decision\n\nEvidence: [request](https://example.com/mail/1)\n\nRecommendation: review.",
                "audience": "Alex", "purpose": "Choose the next review step", "sensitivity": "unknown",
                "proposed_next_action": "Review privately", "open_questions": ["No due date stated"],
                "work_item_id": self.item()["id"], "source_refs": [self.ref]}

    def test_artifact_private_versioning_approval_delivery_and_staleness(self):
        artifact = self.products.put("artifact", "memo-1", self.artifact())
        self.assertEqual(artifact["state"], "prepared")
        approved = self.products.put("artifact", "memo-1", artifact["data"], "approved", 1, evidence(artifact["id"]))
        self.assertEqual(approved["state"], "approved")
        action_data = dict(self.action_data(), artifact_id=artifact["id"], artifact_revision=2)
        action = self.ledger.propose(action_data)
        action = self.ledger.approve(action["id"], 1, action["action_hash"],
                                     evidence(action["id"], decision="approve", action_hash=action["action_hash"]), stamp(60))
        with self.assertRaises(work.StateError):
            self.products.share_receipt(artifact["id"], 2, "missing-attempt")
        attempt = self.ledger.begin(action["id"], 1, self.fresh())
        self.ledger.finish(attempt["attempt_id"], "succeeded", self.receipt())
        result = self.products.share_receipt(artifact["id"], 2, attempt["attempt_id"])
        self.assertEqual(result["sharing_state"], "shared")
        self.assertEqual(self.ledger.show(artifact["id"])["state"], "approved")
        edited = dict(approved["data"], markdown="# Revised private decision")
        with self.assertRaises(work.StateError):
            self.products.put("artifact", "memo-1", edited, "approved", 2, evidence(artifact["id"], 2))
        revised = self.products.put("artifact", "memo-1", edited, "prepared", 2)
        self.assertEqual(revised["revision"], 3)
        self.ledger.source("mail", "inbox", "message-1", "v2", "Changed question", "https://example.com/mail/1")
        self.assertTrue(self.ledger.show(artifact["id"])["stale"])
        with self.assertRaises(work.StateError):
            self.products.put("artifact", "memo-1", edited, "approved", 3, evidence(artifact["id"], 3))

    def test_cli_json_surface_and_no_standalone_yes(self):
        args = cli.parser().parse_args(["--account", "fictional-account", "list", "--view", "all", "--json"])
        result = cli.dispatch(self.ledger, args)
        self.assertEqual(result["schema_version"], 1)
        self.assertEqual(set(result), {"schema_version", "account", "items", "actions", "records"})
        action = self.ledger.propose(self.action_data())
        path = self.path / "action.json"
        path.write_text(json.dumps(self.action_data()))
        args = cli.parser().parse_args(["edit", action["id"], "--revision", "1", "--input", str(path)])
        self.assertEqual(cli.dispatch(self.ledger, args)["revision"], 2)
        self.assertNotIn("--yes", cli.parser().format_help())
        self.assertNotIn("approve-all", cli.parser()._subparsers._group_actions[0].choices)

    def test_real_shared_store_cli_roundtrip(self):
        """Subprocesses bypass unit-test mocks and reopen the actual shared account store."""
        environment = dict(os.environ, MARGO_ALLOW_UNSAFE_STATE_DIR="1")
        base = [sys.executable, str(SCRIPTS / "work_state.py"), "--account", "fictional-cli-account",
                "--state-dir", str(self.path / "cli-state")]

        def invoke(command, payload=None):
            args = list(command)
            if payload is not None:
                input_path = self.path / ("input-" + uuid.uuid4().hex + ".json")
                input_path.write_text(json.dumps(payload), encoding="utf-8")
                args.extend(["--input", str(input_path)])
            result = subprocess.run(base + args, capture_output=True, text=True, env=environment)
            self.assertEqual(result.returncode, 0, result.stderr)
            return json.loads(result.stdout)

        ref = invoke(["source"], {"family": "mail", "scope": "inbox", "external_id": "fictional-cli-message",
                                  "revision": "v1", "evidence": {"quote": "Please review the example."},
                                  "web_link": "https://example.com/fictional-cli-message"})
        candidate = invoke(["ingest"], {"claim_key": "review-example", "data": {
            "title": "Review the example", "owner": None, "due": None, "direction": "owe", "source_refs": [ref]}})
        action = invoke(["propose"], {"kind": "mail.reply", "target": {"message_id": "fictional-cli-message"},
                                      "payload": {"body": "Reviewed example."}, "why": "Fictional fixture",
                                      "source_refs": [ref], "target_fingerprint": "fixture-target-v1",
                                      "work_item_id": candidate["id"]})
        approved = invoke(["approve", action["id"], "--revision", "1"],
                          {"action_hash": action["action_hash"], "expires_at": stamp(60),
                           "evidence": evidence(action["id"], decision="approve", action_hash=action["action_hash"])})
        self.assertEqual(approved["state"], "approved")
        attempt = invoke(["begin", action["id"], "--revision", "1"],
                         {"checked_at": stamp(), "target_fingerprint": "fixture-target-v1", "source_refs": [ref]})
        finished = invoke(["finish", attempt["attempt_id"]],
                          {"state": "succeeded", "receipt": self.receipt()})
        self.assertEqual(finished["state"], "succeeded")
        listed = invoke(["list", "--view", "all", "--json"])
        self.assertEqual(len(listed["actions"]), 1)
        self.assertEqual(listed["items"][0]["state"], "candidate")


if __name__ == "__main__":
    unittest.main()
