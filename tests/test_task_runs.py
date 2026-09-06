import copy
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = ROOT / "skills/chief-of-staff/scripts"
sys.path.insert(0, str(SCRIPTS))

import proactive_state
from margo_store import NotInitialized, StateError, canonical_json
from task_runs import METRICS, TaskStore, zero
from work_ledger import Ledger


def stamp(seconds=0):
    return (datetime.now(timezone.utc) + timedelta(seconds=seconds)).isoformat()


def consent(subject, revision=1, decision="confirm", **extra):
    return dict(kind="human_confirmation", actor="dana@example.com", subject_id=subject,
                revision=revision, decision=decision, statement="Confirm this exact synthetic fixture.",
                evidence_ref="conversation:synthetic-task-test", decided_at=stamp(), **extra)


class TaskRunTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()
        self.env = patch.dict(os.environ, {"MARGO_ALLOW_UNSAFE_STATE_DIR": "1"})
        self.env.start()
        self.store = TaskStore("task-fixture@example.com", str(self.root / "state"))
        self.binding = {"account": self.store.account, "host": "synthetic-cli",
                        "capabilities": {"mail.read": "v1", "local.prepare": "v1", "mail.reply": "v1"},
                        "observed_at": stamp()}

    def tearDown(self):
        self.store.close()
        self.env.stop()
        self.temp.cleanup()

    def plan(self, steps=None, **limits):
        budget = dict(max_steps=10, max_attempts_per_step=3, max_parallel=3,
                      tool_calls=10, pages=10, items=100, model_calls=2, output_chars=10000,
                      deadline_at=stamp(600), lease_seconds=30)
        budget.update(limits)
        return {"goal": "Prepare a useful synthetic result", "routine": "daily-brief",
                "request_ref": "conversation:synthetic-task-test", "mode": "foreground",
                "environment": copy.deepcopy(self.binding), "window": {"start": stamp(-3600), "end": stamp()},
                "limits": budget, "steps": steps or [self.read_step()]}

    def read_step(self, key="mail"):
        return {"key": key, "title": "Read synthetic mail", "kind": "read", "capability": "mail.read",
                "depends_on": [], "allow_partial": False,
                "cost": dict(zero(), tool_calls=1, pages=1, items=5),
                "source": {"family": "mail", "scope": {"folder": key}, "kind": "enumeration", "query_version": "v1"}}

    def local_step(self, key="prepare", dependencies=None, partial=False):
        return {"key": key, "title": "Prepare private output", "kind": "local", "capability": "local.prepare",
                "depends_on": dependencies or [], "allow_partial": partial,
                "cost": dict(zero(), output_chars=1000)}

    def start(self, run, key):
        current = self.store.show(run["id"])
        step = next(item for item in current["steps"] if item["key"] == key)
        return self.store.start(run["id"], key, step["revision"], current["plan_hash"], self.binding)

    def complete_read(self, claim, complete=True, error="timeout"):
        self.store.charge(claim["attempt_id"], claim["token"], "fetch", dict(zero(), tool_calls=1, pages=1, items=5))
        coverage = claim["coverage_attempt_id"]
        if complete:
            proactive_state.coverage_page(self.store.conn, coverage, {
                "page": 1, "observations": [{"id": "synthetic-message", "revision": "v1"}],
                "final": True, "continuation": None})
            proactive_state.coverage_finish(self.store.conn, coverage, {"status": "complete", "checkpoint_at": stamp()})
        else:
            proactive_state.coverage_finish(self.store.conn, coverage, {"status": "failed", "error_class": error})
        return self.store.finish(claim["attempt_id"], claim["token"], "succeeded" if complete else "failed", {
            "kind": "tool_result", "reference": "synthetic:fetch", "coverage_attempt_id": coverage,
            "coverage": "complete" if complete else "failed", "summary": "A sourced read result.",
        })

    def action(self):
        source = self.store.ledger.source("mail", "inbox", "synthetic-message", "1",
                                          {"summary": "Synthetic ask."}, "https://example.com/source")
        action = self.store.ledger.propose({
            "kind": "mail.reply", "target": {"message_id": "synthetic-message", "to": ["dana@example.com"]},
            "payload": {"body": "Synthetic draft, never sent."}, "why": "Fixture only", "source_refs": [source],
            "target_fingerprint": "target-v1", "work_item_id": None})
        self.store.ledger.approve(action["id"], 1, action["action_hash"],
                                  consent(action["id"], decision="approve", action_hash=action["action_hash"]), stamp(300))
        step = {"key": "reply", "title": "Deliver the approved reply", "kind": "action",
                "capability": "mail.reply", "depends_on": [], "allow_partial": False,
                "cost": dict(zero(), tool_calls=1), "action_id": action["id"],
                "action_revision": 1, "action_hash": action["action_hash"]}
        fresh = {"checked_at": stamp(), "target_fingerprint": "target-v1", "source_refs": [source]}
        return action, step, fresh

    def test_create_idempotent_and_fresh_read_resumes_across_instances(self):
        plan = self.plan()
        run = self.store.create("day", plan)
        self.assertEqual(self.store.create("day", plan)["id"], run["id"])
        with self.assertRaises(StateError):
            self.store.create("day", dict(plan, goal="A different request"))
        claim = self.start(run, "mail")
        result = self.complete_read(claim)
        self.assertEqual(result["status"], "succeeded")
        self.assertEqual(result["usage"]["items"], 1)
        reader = TaskStore(self.store.account, str(self.root / "state"), read_only=True)
        try:
            self.assertEqual(reader.show(run["id"])["steps"][0]["state"], "succeeded")
        finally:
            reader.close()
        with self.assertRaises(StateError):
            self.start(run, "mail")

    def test_partial_source_can_prepare_a_partial_brief_not_an_all_clear(self):
        run = self.store.create("partial", self.plan([
            self.read_step(), self.local_step(dependencies=["mail"], partial=True)]))
        after_read = self.complete_read(self.start(run, "mail"), complete=False)
        self.assertIn("prepare", after_read["ready_steps"])
        claim = self.start(run, "prepare")
        result = self.store.finish(claim["attempt_id"], claim["token"], "succeeded",
                                   {"kind": "local_result", "reference": "private:partial-brief",
                                    "summary": "Mail coverage failed; no all-clear can be established."})
        self.assertEqual(result["status"], "partial")
        self.assertEqual(result["steps"][0]["result"]["coverage"], "failed")

    def test_coverage_cannot_be_replaced_with_an_unrelated_success(self):
        run = self.store.create("coverage", self.plan([self.read_step("mail"), self.read_step("other")]))
        first, other = self.start(run, "mail"), self.start(run, "other")
        self.complete_read(other)
        with self.assertRaisesRegex(StateError, "this claim"):
            self.store.finish(first["attempt_id"], first["token"], "succeeded", {
                "kind": "tool_result", "reference": "synthetic", "coverage": "complete",
                "coverage_attempt_id": other["coverage_attempt_id"]})

    def test_charges_are_bounded_nonempty_and_never_granted_twice(self):
        run = self.store.create("charges", self.plan())
        claim = self.start(run, "mail")
        with self.assertRaises(StateError):
            self.store.charge(claim["attempt_id"], claim["token"], "empty", zero())
        values = dict(zero(), tool_calls=1)
        self.assertTrue(self.store.charge(claim["attempt_id"], claim["token"], "one", values)["execute"])
        self.assertFalse(self.store.charge(claim["attempt_id"], claim["token"], "one", values)["execute"])
        with self.assertRaises(StateError):
            self.store.charge(claim["attempt_id"], claim["token"], "two", values)
        with self.assertRaises(StateError):
            self.store.charge(claim["attempt_id"], "wrong-token", "other", values)

    def test_parallel_claims_reserve_global_budget_and_one_step_has_one_winner(self):
        run = self.store.create("parallel", self.plan([self.local_step()], max_parallel=1))

        def claim():
            worker = TaskStore(self.store.account, str(self.root / "state"))
            try:
                return worker.start(run["id"], "prepare", 1, run["plan_hash"], self.binding)["attempt_id"]
            except StateError:
                return None
            finally:
                worker.close()

        with ThreadPoolExecutor(max_workers=2) as executor:
            results = list(executor.map(lambda _: claim(), range(2)))
        self.assertEqual(sum(value is not None for value in results), 1)
        row = self.store.show(run["id"])
        self.assertEqual(row["steps"][0]["attempts"], 1)
        self.assertEqual(row["usage"]["output_chars"], 1000)

    def test_deadline_and_binding_changes_do_not_admit_new_work(self):
        run = self.store.create("deadline", self.plan())
        with self.assertRaisesRegex(StateError, "binding changed"):
            self.store.start(run["id"], "mail", 1, run["plan_hash"], dict(self.binding, host="another-host"))
        with patch("task_runs.clock", return_value=datetime.now(timezone.utc) + timedelta(seconds=601)):
            newer = dict(self.binding, observed_at=stamp(601))
            with self.assertRaisesRegex(StateError, "deadline"):
                self.store.start(run["id"], "mail", 1, run["plan_hash"], newer)
        self.assertEqual(self.store.show(run["id"])["steps"][0]["attempts"], 0)

    def test_pause_stops_new_charges_and_resume_requires_fresh_binding(self):
        run = self.store.create("pause", self.plan())
        claim = self.start(run, "mail")
        paused = self.store.pause(run["id"], "Review the plan.")
        with self.assertRaises(StateError):
            self.store.charge(claim["attempt_id"], claim["token"], "fetch", dict(zero(), tool_calls=1))
        with self.assertRaises(StateError):
            self.store.resume(run["id"], paused["revision"], dict(self.binding, observed_at=stamp(-600)))
        self.store.resume(run["id"], paused["revision"], self.binding)
        self.assertTrue(self.store.charge(claim["attempt_id"], claim["token"], "fetch", dict(zero(), tool_calls=1))["execute"])

    def test_expired_read_is_recoverable_but_obeys_backoff(self):
        run = self.store.create("recover-read", self.plan(lease_seconds=1))
        self.start(run, "mail")
        future = datetime.now(timezone.utc) + timedelta(seconds=2)
        with patch("task_runs.clock", return_value=future):
            recovered = self.store.recover(run["id"])
            step = recovered["steps"][0]
            self.assertEqual(step["state"], "failed")
            with self.assertRaises(StateError):
                self.store.retry(run["id"], "mail", step["revision"], self.binding)
        retry_time = datetime.fromisoformat(step["next_retry_at"]) + timedelta(seconds=1)
        with patch("task_runs.clock", return_value=retry_time):
            result = self.store.retry(run["id"], "mail", step["revision"], self.binding)
            self.assertEqual(result["steps"][0]["state"], "pending")

    def test_preflight_failure_preserves_canonical_approval_invalidation(self):
        action, definition, fresh = self.action()
        run = self.store.create("stale-action", self.plan([definition]))
        with self.assertRaisesRegex(StateError, "target changed"):
            self.store.start(run["id"], "reply", 1, run["plan_hash"], self.binding,
                              dict(fresh, target_fingerprint="changed"))
        current = self.store.ledger.show(action["id"])
        self.assertEqual(current["state"], "stale")
        self.assertTrue(all(row["invalidated_at"] is not None for row in current["approvals"]))
        self.assertEqual(self.store.show(run["id"])["steps"][0]["attempts"], 0)

    def test_action_claim_receipt_and_unknown_reconciliation_never_reexecute(self):
        action, definition, fresh = self.action()
        run = self.store.create("action", self.plan([definition], lease_seconds=1))
        claim = self.store.start(run["id"], "reply", 1, run["plan_hash"], self.binding, fresh)
        self.assertEqual(claim["execution"]["action_id"], action["id"])
        with self.assertRaises(StateError):
            self.store.charge(claim["attempt_id"], claim["token"], "extra", dict(zero(), tool_calls=1))
        with patch("task_runs.clock", return_value=datetime.now(timezone.utc) + timedelta(seconds=2)):
            unknown = self.store.recover(run["id"])
        self.assertEqual(unknown["status"], "outcome_unknown")
        self.assertEqual(self.store.ledger.show(action["id"])["state"], "outcome_unknown")
        with self.assertRaises(StateError):
            self.store.retry(run["id"], "reply", unknown["steps"][0]["revision"], self.binding)
        receipt = {"kind": "reconciliation_read", "reference": "synthetic:actual-result",
                   "recorded_at": stamp(), "outcome": "succeeded"}
        settled = self.store.reconcile(run["id"], "reply", unknown["steps"][0]["revision"], "succeeded", receipt)
        self.assertEqual(settled["status"], "succeeded")
        self.assertEqual(len(self.store.ledger.show(action["id"])["executions"]), 1)

    def test_cancel_revokes_unused_approval_but_preserves_completed_results(self):
        action, definition, fresh = self.action()
        run = self.store.create("cancel", self.plan([definition]))
        cancelled = self.store.cancel(run["id"], "The user will handle it.")
        self.assertEqual(cancelled["status"], "cancelled")
        self.assertEqual(self.store.ledger.show(action["id"])["state"], "stale")
        with self.assertRaises(StateError):
            self.store.resume(run["id"], cancelled["revision"], self.binding)
        self.assertEqual(self.store.ledger.show(action["id"])["executions"], [])

    def test_unattended_plan_cannot_add_execution_or_reauthentication(self):
        _, definition, _ = self.action()
        plan = self.plan([definition])
        plan.update(mode="unattended", request_ref="automation:synthetic")
        with self.assertRaisesRegex(StateError, "never execute"):
            self.store.create("unattended-action", plan)
        step = self.read_step()
        step["source"]["reauthenticated"] = True
        plan["steps"] = [step]
        with self.assertRaisesRegex(StateError, "foreground"):
            self.store.create("unattended-reauth", plan)

    def test_replan_preview_is_exact_and_preserves_completed_steps(self):
        original = self.plan([self.local_step("first"), self.local_step("second", ["first"])])
        run = self.store.create("replan", original)
        claim = self.start(run, "first")
        self.store.finish(claim["attempt_id"], claim["token"], "succeeded",
                          {"kind": "local_result", "reference": "private:done", "summary": "Prepared."})
        self.store.pause(run["id"], "Review remaining work")
        changed = copy.deepcopy(original)
        changed["limits"]["deadline_at"] = stamp(1200)
        preview = self.store.replan_preview(run["id"], changed)
        invalid = copy.deepcopy(changed)
        invalid["steps"][0]["title"] = "Rewrite history"
        with self.assertRaises(StateError):
            self.store.replan_preview(run["id"], invalid)
        applied = self.store.replan(run["id"], changed, consent(preview["subject_id"], decision="replan"))
        self.assertEqual(applied["steps"][0]["state"], "succeeded")
        self.assertEqual(applied["state"], "paused")
        with self.assertRaises(StateError):
            self.store.replan(run["id"], changed, consent(preview["subject_id"], decision="replan"))

    def test_read_views_never_initialize_and_account_ids_cannot_cross(self):
        missing = self.root / "missing"
        with self.assertRaises(NotInitialized):
            TaskStore(self.store.account, str(missing), read_only=True)
        self.assertFalse(missing.exists())
        run = self.store.create("isolation", self.plan())
        other = TaskStore("another-fixture@example.com", str(self.root / "state"))
        try:
            with self.assertRaises(StateError):
                other.show(run["id"])
        finally:
            other.close()
        reader = TaskStore(self.store.account, str(self.root / "state"), read_only=True)
        try:
            self.assertEqual(reader.show(run["id"])["plan_hash"], run["plan_hash"])
            with self.assertRaises(sqlite3.OperationalError):
                reader.conn.execute("DELETE FROM task_runs")
        finally:
            reader.close()

    def test_nested_ledger_and_coverage_operations_rollback_with_the_caller(self):
        before = self.store.conn.execute("SELECT count(*) FROM work_sources").fetchone()[0]
        with self.assertRaises(RuntimeError):
            with self.store.transaction():
                self.store.ledger.source("mail", "test", "temporary", "v1", {"quote": "Synthetic"}, "https://example.com/test")
                proactive_state.coverage_start(self.store.conn, {
                    "family": "mail", "scope": {"folder": "synthetic"}, "run_id": "synthetic-rollback",
                    "capability": "mail.read", "query_version": "v1", "window": {"start": stamp(-60), "end": stamp()}})
                raise RuntimeError("abort synthetic outer transaction")
        self.assertEqual(self.store.conn.execute("SELECT count(*) FROM work_sources").fetchone()[0], before)
        self.assertEqual(self.store.conn.execute(
            "SELECT count(*) FROM proactive_attempts WHERE run_id='synthetic-rollback'").fetchone()[0], 0)

    def test_misrouted_action_receipt_cannot_settle_either_journal(self):
        action, definition, fresh = self.action()
        run = self.store.create("receipt-routing", self.plan([definition]))
        claim = self.store.start(run["id"], "reply", 1, run["plan_hash"], self.binding, fresh)
        receipt = {"kind": "provider_receipt", "reference": "synthetic:receipt",
                   "recorded_at": stamp(), "outcome": "succeeded"}
        for key, value in (("account", "other@example.com"), ("action_id", "act_other"),
                           ("attempt_id", "exec_other"), ("revision", 999), ("revision", True),
                           ("action_hash", "different")):
            with self.subTest(key=key, value=value), self.assertRaisesRegex(StateError, "contradicts"):
                self.store.finish(claim["attempt_id"], claim["token"], "succeeded", dict(receipt, **{key: value}))
            self.assertEqual(self.store.ledger.show(action["id"])["state"], "executing")
            self.assertEqual(self.store.show(run["id"])["steps"][0]["state"], "running")

    def test_partial_cost_cannot_refund_durable_pages_or_items(self):
        run = self.store.create("partial-cost", self.plan(pages=1, items=5))
        claim = self.start(run, "mail")
        self.store.charge(claim["attempt_id"], claim["token"], "fetch", dict(zero(), tool_calls=1, pages=1, items=5))
        proactive_state.coverage_page(self.store.conn, claim["coverage_attempt_id"], {
            "page": 1, "observations": [{"id": "seen", "revision": "1"}],
            "final": False, "continuation": "synthetic-page-2"})
        proactive_state.coverage_finish(self.store.conn, claim["coverage_attempt_id"],
                                         {"status": "partial", "error_class": "timeout"})
        result = {"kind": "tool_result", "reference": "synthetic:partial", "coverage": "partial",
                  "coverage_attempt_id": claim["coverage_attempt_id"]}
        with self.assertRaisesRegex(StateError, "durable coverage"):
            self.store.finish(claim["attempt_id"], claim["token"], "partial", result,
                              actual=dict(zero(), tool_calls=1))
        settled = self.store.finish(claim["attempt_id"], claim["token"], "partial", result,
                                     actual=dict(zero(), tool_calls=1, pages=1, items=1))
        step = settled["steps"][0]
        with patch("task_runs.clock", return_value=datetime.fromisoformat(step["next_retry_at"]) + timedelta(seconds=1)):
            with self.assertRaisesRegex(StateError, "budget"):
                self.store.retry(run["id"], "mail", step["revision"], self.binding)

    def test_attempted_window_cannot_be_relabelled_by_replan(self):
        original = self.plan([self.read_step(), self.local_step(dependencies=["mail"])])
        run = self.store.create("window-change", original)
        self.complete_read(self.start(run, "mail"))
        self.store.pause(run["id"], "Expand the interval")
        changed = copy.deepcopy(original)
        changed["window"]["start"] = stamp(-86400)
        with self.assertRaisesRegex(StateError, "window is immutable"):
            self.store.replan_preview(run["id"], changed)

    def test_error_free_partial_page_can_continue_without_fabricating_an_error(self):
        run = self.store.create("pagination", self.plan())
        claim = self.start(run, "mail")
        self.store.charge(claim["attempt_id"], claim["token"], "page1", dict(zero(), tool_calls=1, pages=1, items=5))
        proactive_state.coverage_page(self.store.conn, claim["coverage_attempt_id"], {
            "page": 1, "observations": [], "final": False, "continuation": "synthetic-page-2"})
        proactive_state.coverage_finish(self.store.conn, claim["coverage_attempt_id"], {"status": "partial"})
        settled = self.store.finish(claim["attempt_id"], claim["token"], "partial", {
            "kind": "tool_result", "reference": "synthetic:page1", "coverage": "partial",
            "coverage_attempt_id": claim["coverage_attempt_id"]})
        step = settled["steps"][0]
        with patch("task_runs.clock", return_value=datetime.fromisoformat(step["next_retry_at"]) + timedelta(seconds=1)):
            self.store.retry(run["id"], "mail", step["revision"], self.binding)
            continued = self.start(run, "mail")
        self.assertEqual(continued["source_contract"]["resume_attempt"], claim["coverage_attempt_id"])

    def test_own_provider_backoff_survives_another_run_collecting_the_source(self):
        first = self.store.create("throttle-a", self.plan())
        second = self.store.create("throttle-b", self.plan())
        claim = self.start(first, "mail")
        proactive_state.coverage_finish(self.store.conn, claim["coverage_attempt_id"],
                                         {"status": "failed", "error_class": "throttled", "retry_after_seconds": 3600})
        self.start(second, "mail")
        settled = self.store.finish(claim["attempt_id"], claim["token"], "failed", {
            "kind": "tool_result", "reference": "synthetic:throttle", "coverage": "failed",
            "coverage_attempt_id": claim["coverage_attempt_id"]})
        step = settled["steps"][0]
        self.assertGreater(datetime.fromisoformat(step["next_retry_at"]),
                           datetime.now(timezone.utc) + timedelta(seconds=3500))
        with patch("task_runs.clock", return_value=datetime.now(timezone.utc) + timedelta(seconds=3)):
            with self.assertRaisesRegex(StateError, "retry time"):
                self.store.retry(first["id"], "mail", step["revision"], self.binding)

    def test_removed_action_later_execution_does_not_change_completed_task(self):
        action, definition, fresh = self.action()
        original = self.plan([definition, self.local_step()])
        run = self.store.create("removed-action", original)
        self.store.pause(run["id"], "Do preparation only")
        replacement = dict(original, steps=[self.local_step()])
        preview = self.store.replan_preview(run["id"], replacement)
        applied = self.store.replan(run["id"], replacement, consent(preview["subject_id"], decision="replan"))
        self.store.resume(run["id"], applied["revision"], self.binding)
        claim = self.start(run, "prepare")
        done = self.store.finish(claim["attempt_id"], claim["token"], "succeeded",
                                  {"kind": "local_result", "reference": "private:prepared", "summary": "Prepared."})
        self.assertEqual(done["status"], "succeeded")
        allowed = {"kind", "target", "payload", "why", "source_refs", "target_fingerprint", "work_item_id",
                   "affected_people", "artifact_id", "artifact_revision", "dependencies"}
        new_action = self.store.ledger.edit_action(action["id"], 1,
                                                   {key: value for key, value in action.items() if key in allowed})
        self.store.ledger.approve(new_action["id"], new_action["revision"], new_action["action_hash"],
                                  consent(new_action["id"], new_action["revision"], "approve",
                                          action_hash=new_action["action_hash"]), stamp(300))
        self.store.ledger.begin(new_action["id"], new_action["revision"], fresh)
        current = self.store.show(run["id"])
        self.assertEqual(current["status"], "succeeded")
        self.assertEqual(current["unresolved_effects"], [])

    def test_completed_source_can_be_reconciled_after_lost_task_bookmark(self):
        run = self.store.create("lost-bookmark", self.plan(lease_seconds=1))
        claim = self.start(run, "mail")
        self.store.charge(claim["attempt_id"], claim["token"], "fetch", dict(zero(), tool_calls=1, pages=1, items=5))
        proactive_state.coverage_page(self.store.conn, claim["coverage_attempt_id"], {
            "page": 1, "observations": [], "final": True, "continuation": None})
        proactive_state.coverage_finish(self.store.conn, claim["coverage_attempt_id"],
                                         {"status": "complete", "checkpoint_at": stamp()})
        with patch("task_runs.clock", return_value=datetime.now(timezone.utc) + timedelta(seconds=2)):
            recovered = self.store.recover(run["id"])
        self.assertEqual(recovered["steps"][0]["state"], "partial")
        restored = self.store.reconcile(run["id"], "mail", recovered["steps"][0]["revision"], "succeeded", {
            "kind": "tool_result", "reference": "synthetic:restored-bookmark", "coverage": "complete",
            "coverage_attempt_id": claim["coverage_attempt_id"], "summary": "The recorded source had no matches."})
        self.assertEqual(restored["status"], "succeeded")
        self.assertEqual(restored["steps"][0]["attempts"], 1)


if __name__ == "__main__":
    unittest.main()
