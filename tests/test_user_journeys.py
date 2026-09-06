import copy
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import shutil
import sys
import unittest
import uuid
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = ROOT / "skills/chief-of-staff/scripts"
sys.path.insert(0, str(SCRIPTS))

import proactive_state
import task_runs
from margo_store import StateError
from task_runs import TaskStore, zero
from work_productivity import Productivity


def stamp(seconds=0):
    return (datetime.now(timezone.utc) + timedelta(seconds=seconds)).isoformat()


def decision(subject, revision=1, choice="confirm", action_hash=None):
    value = {
        "kind": "human_confirmation",
        "actor": "dana@example.com",
        "subject_id": subject,
        "revision": revision,
        "decision": choice,
        "statement": "I reviewed this exact fictional fixture.",
        "evidence_ref": "conversation:synthetic-user-journey",
        "decided_at": stamp(),
    }
    if action_hash:
        value["action_hash"] = action_hash
    return value


class FakeProvider:
    """Credential-free provider whose writes only return fixture receipts."""

    def __init__(self):
        data = json.loads(
            (ROOT / "tests/fixtures/journeys/sources.json").read_text(encoding="utf-8"))
        self.fixtures = data["fixtures"]
        self.read_calls = []
        self.write_calls = []

    def read_brief_source(self, family):
        self.read_calls.append(family)
        rows = [row for row in self.fixtures["brief-partial-v1"]["sources"]
                if row["id"].startswith(family + ":")]
        if not rows:
            raise AssertionError("unknown fake source family")
        row = copy.deepcopy(rows[0])
        if row.get("status") == "unavailable":
            return {"status": "failed", "error_class": row["error_class"], "observations": []}
        return {"status": "complete", "observations": [row]}

    def execute_fixture_action(self, execution):
        self.write_calls.append(copy.deepcopy(execution))
        return {
            "kind": "provider_receipt",
            "reference": "fixture-only:mail.reply:receipt-1",
            "outcome": "succeeded",
            "recorded_at": stamp(),
            "fixture_only": True,
        }


class UserJourneyTests(unittest.TestCase):
    def setUp(self):
        self.root = ROOT / (".user-journey-" + uuid.uuid4().hex)
        self.root.mkdir(mode=0o700)
        self.environment = patch.dict(os.environ, {"MARGO_ALLOW_UNSAFE_STATE_DIR": "1"})
        self.environment.start()
        self.store = TaskStore("dana@example.com", str(self.root / "state"))
        self.provider = FakeProvider()
        self.binding = {
            "account": self.store.account,
            "host": "synthetic-host",
            "capabilities": {
                "mail.read": "fixture-v1",
                "calendar.read": "fixture-v1",
                "teams.read": "fixture-v1",
                "local.prepare": "fixture-v1",
                "mail.reply": "fixture-v1",
            },
            "observed_at": stamp(),
        }

    def tearDown(self):
        self.store.close()
        self.environment.stop()
        shutil.rmtree(self.root)

    def plan(self, routine, steps, **limits):
        budget = {
            "max_steps": 10,
            "max_attempts_per_step": 3,
            "max_parallel": 3,
            "tool_calls": 20,
            "pages": 10,
            "items": 100,
            "model_calls": 2,
            "output_chars": 12000,
            "deadline_at": stamp(900),
            "lease_seconds": 30,
        }
        budget.update(limits)
        return {
            "goal": "Complete a fictional synthetic journey",
            "routine": routine,
            "request_ref": "conversation:synthetic-user-journey",
            "mode": "foreground",
            "environment": copy.deepcopy(self.binding),
            "window": {"start": stamp(-3600), "end": stamp()},
            "limits": budget,
            "steps": steps,
        }

    def read_step(self, family, capability):
        return {
            "key": family,
            "title": "Read fictional " + family,
            "kind": "read",
            "capability": capability,
            "depends_on": [],
            "allow_partial": False,
            "cost": dict(zero(), tool_calls=1, pages=1, items=10),
            "source": {
                "family": family,
                "scope": {"fixture": "brief-partial-v1", "source": family},
                "kind": "enumeration",
                "query_version": "fixture-v1",
            },
        }

    def local_step(self, key="prepare", dependencies=None, partial=False):
        return {
            "key": key,
            "title": "Prepare fictional private output",
            "kind": "local",
            "capability": "local.prepare",
            "depends_on": dependencies or [],
            "allow_partial": partial,
            "cost": dict(zero(), output_chars=4000),
        }

    def current_step(self, run_id, key):
        run = self.store.show(run_id)
        return run, next(step for step in run["steps"] if step["key"] == key)

    def start(self, run_id, key, preflight=None):
        run, step = self.current_step(run_id, key)
        return self.store.start(
            run_id, key, step["revision"], run["plan_hash"], self.binding, preflight)

    def complete_read(self, claim, family):
        grant = self.store.charge(
            claim["attempt_id"], claim["token"], "fixture-read",
            cost=dict(zero(), tool_calls=1))
        self.assertTrue(grant["execute"])
        result = self.provider.read_brief_source(family)
        observations = result["observations"]
        cost = dict(zero(), tool_calls=1)
        if observations:
            cost.update(pages=1, items=len(observations))
        coverage = self.store.conn.execute(
            "SELECT id,run_id FROM proactive_attempts WHERE id=?",
            (claim["coverage_attempt_id"],)).fetchone()
        self.assertIsNotNone(coverage)
        self.assertEqual(coverage["run_id"], claim["attempt_id"])
        self.assertEqual(self.store.conn.execute(
            "SELECT count(*) FROM proactive_attempts WHERE run_id=?",
            (claim["attempt_id"],)).fetchone()[0], 1)
        source_refs = []
        if result["status"] == "complete":
            page = []
            for observation in observations:
                page.append({
                    "id": observation["id"],
                    "revision": observation["revision"],
                    "fixture_kind": observation["kind"],
                })
                source_refs.append(self.store.ledger.source(
                    observation["kind"], "brief-partial-v1", observation["id"],
                    observation["revision"], observation,
                    "https://example.com/sources/" + observation["id"].replace(":", "/")))
            proactive_state.coverage_page(
                self.store.conn, claim["coverage_attempt_id"],
                {"page": 1, "observations": page, "final": True, "continuation": None})
            proactive_state.coverage_finish(
                self.store.conn, claim["coverage_attempt_id"],
                {"status": "complete", "checkpoint_at": stamp()})
            outcome = "succeeded"
        else:
            proactive_state.coverage_finish(
                self.store.conn, claim["coverage_attempt_id"],
                {"status": "failed", "error_class": result["error_class"]})
            outcome = "failed"
        receipt = {
            "kind": "tool_result",
            "reference": "fixture-only:read:" + claim["step_key"],
            "summary": "Synthetic source read; no live service was called.",
            "coverage": result["status"],
            "coverage_attempt_id": claim["coverage_attempt_id"],
            "source_refs": source_refs,
        }
        if result.get("error_class"):
            receipt["error_class"] = result["error_class"]
        settled = self.store.finish(
            claim["attempt_id"], claim["token"], outcome, receipt, actual=cost)
        stored_step = next(
            step for step in settled["steps"] if step["key"] == claim["step_key"])
        self.assertEqual(
            stored_step["result"]["coverage_attempt_id"],
            claim["coverage_attempt_id"])
        return settled, source_refs

    def create_approved_action(self, suffix):
        source = self.store.ledger.source(
            "mail", "inbox", "commitment-" + suffix, "m1",
            {"from": "rafa@example.com", "ask": "Send the revised fictional checklist."},
            "https://example.com/mail/commitment-" + suffix)
        item = self.store.ledger.ingest({
            "title": "Send the revised fictional checklist",
            "owner": None,
            "due": None,
            "direction": "owe",
            "source_refs": [source],
        }, "commitment-" + suffix)
        item = self.store.ledger.update_item(
            item["id"], 1, state="confirmed",
            evidence=decision(item["id"], choice="confirm"))
        action = self.store.ledger.propose({
            "kind": "mail.reply",
            "target": {"message_id": "commitment-" + suffix,
                       "to": ["rafa@example.com"]},
            "payload": {"body": "Attached is the revised fictional checklist."},
            "why": "Fulfil the exactly confirmed fictional commitment.",
            "source_refs": [source],
            "target_fingerprint": "recipient:rafa@example.com",
            "work_item_id": item["id"],
        })
        action = self.store.ledger.approve(
            action["id"], action["revision"], action["action_hash"],
            decision(action["id"], action["revision"], "approve", action["action_hash"]),
            stamp(300))
        return source, item, action

    def test_partial_brief_uses_real_coverage_and_publication_primitives(self):
        steps = [
            self.read_step("mail", "mail.read"),
            self.read_step("calendar", "calendar.read"),
            self.read_step("teams", "teams.read"),
            self.local_step(dependencies=["mail", "calendar", "teams"], partial=True),
        ]
        run = self.store.create("journey:partial-brief", self.plan("daily-brief", steps))
        refs = []
        for family in ("mail", "calendar", "teams"):
            settled, source_refs = self.complete_read(
                self.start(run["id"], family), family)
            refs.extend(source_refs)
            if family == "teams":
                self.assertEqual(settled["steps"][2]["state"], "failed")

        content = (
            "Partial fictional brief.\n"
            "- Decision needed on the launch checklist [mail:brief-ask].\n"
            "- Synthetic design review at 17:00Z [calendar:brief-review].\n"
            "- Teams source unavailable; no all-clear and no Teams checkpoint."
        )
        publication = proactive_state.publication_record(
            self.store.conn, None,
            {"id": "fixture-partial-brief", "content": content,
             "status": "available", "receipt": {"kind": "local"}})
        claim = self.start(run["id"], "prepare")
        result = self.store.finish(
            claim["attempt_id"], claim["token"], "succeeded",
            {"kind": "local_result", "reference": "private:fixture-partial-brief",
             "summary": content, "source_refs": refs,
             "publication_id": publication["publication_id"]})

        self.assertEqual(result["status"], "partial")
        self.assertIn("Teams source unavailable", result["steps"][3]["result"]["summary"])
        self.assertNotIn("all clear", result["steps"][3]["result"]["summary"].lower())
        source = self.store.conn.execute(
            "SELECT status,checkpoint_at FROM proactive_sources "
            "WHERE latest_attempt_id=(SELECT id FROM proactive_attempts "
            "WHERE capability='teams.read')").fetchone()
        self.assertEqual((source["status"], source["checkpoint_at"]), ("failed", None))
        stored = self.store.conn.execute(
            "SELECT status,receipt FROM proactive_publications WHERE id=?",
            (publication["publication_id"],)).fetchone()
        self.assertEqual(stored["status"], "available")
        self.assertIn('"kind":"local"', stored["receipt"])
        self.assertEqual(self.provider.write_calls, [])

    def test_commitment_confirmation_exact_approval_and_stale_edit(self):
        source, item, action = self.create_approved_action("journey")
        changed = dict(action)
        edited_data = {
            "kind": action["kind"],
            "target": {"message_id": "commitment-journey",
                       "to": ["ines@example.com"]},
            "payload": {"body": "Revised fictional checklist with accessibility notes."},
            "why": action["why"],
            "source_refs": [source],
            "target_fingerprint": "recipient:ines@example.com",
            "work_item_id": item["id"],
        }
        edited = self.store.ledger.edit_action(
            action["id"], action["revision"], edited_data,
            expected_hash=action["action_hash"])
        self.assertEqual(edited["state"], "ready")
        self.assertTrue(all(row["invalidated_at"] for row in edited["approvals"]))
        with self.assertRaises(StateError):
            self.store.ledger.begin(action["id"], action["revision"], {
                "checked_at": stamp(),
                "target_fingerprint": action["target_fingerprint"],
                "source_refs": [source],
            })
        self.assertEqual(self.provider.write_calls, [])
        self.assertNotEqual(changed["action_hash"], edited["action_hash"])

        approved = self.store.ledger.approve(
            edited["id"], edited["revision"], edited["action_hash"],
            decision(edited["id"], edited["revision"], "approve", edited["action_hash"]),
            stamp(300))
        action_step = {
            "key": "deliver",
            "title": "Deliver the exact approved fictional reply",
            "kind": "action",
            "capability": "mail.reply",
            "depends_on": [],
            "allow_partial": False,
            "cost": dict(zero(), tool_calls=1),
            "action_id": approved["id"],
            "action_revision": approved["revision"],
            "action_hash": approved["action_hash"],
        }
        run = self.store.create(
            "journey:commitment-delivery", self.plan("follow-through", [action_step]))
        claim = self.start(run["id"], "deliver", {
            "checked_at": stamp(),
            "target_fingerprint": approved["target_fingerprint"],
            "source_refs": [source],
        })
        receipt = self.provider.execute_fixture_action(claim["execution"])
        finished = self.store.finish(
            claim["attempt_id"], claim["token"], "succeeded", receipt)

        self.assertEqual(finished["status"], "succeeded")
        self.assertEqual(len(self.provider.write_calls), 1)
        self.assertEqual(self.provider.write_calls[0]["target"]["to"], ["ines@example.com"])
        self.assertTrue(
            self.store.ledger.show(approved["id"])["executions"][0]["receipt"]["fixture_only"])
        self.assertEqual(self.store.ledger.show(item["id"])["state"], "confirmed")

    def test_meeting_occurrence_delayed_recap_and_carry_forward(self):
        ledger = self.store.ledger
        products = Productivity(ledger)
        occurrence = ledger.source(
            "calendar", "occurrences", "occurrence-2026-09-06", "c1",
            {"response": "accepted", "attendance": "unknown"},
            "https://example.com/calendar/occurrence-2026-09-06")
        pre_read = ledger.source(
            "file", "meeting-prep", "fictional-pre-read", "f1",
            {"summary": "Two open questions; no approved decisions."},
            "https://example.com/files/fictional-pre-read")
        meeting_data = {
            "series_id": "series-synthetic-review",
            "occurrence_id": "occurrence-2026-09-06",
            "scheduled_start": stamp(-1800),
            "scheduled_end": stamp(-900),
            "source_refs": [occurrence, pre_read],
            "attendance": "unknown",
            "work_item_ids": [],
            "topics": [{
                "id": "topic-accessibility", "title": "Accessibility review",
                "owner": None, "created_at": stamp(-3600),
                "why": "The pre-read leaves this open.", "state": "open",
            }],
        }
        meeting = products.put(
            "meeting", meeting_data["occurrence_id"], meeting_data, state="scheduled")
        meeting = products.put(
            "meeting", meeting_data["occurrence_id"], meeting["data"],
            state="prepped", revision=meeting["revision"])
        meeting = products.put(
            "meeting", meeting_data["occurrence_id"], meeting["data"],
            state="occurred", revision=meeting["revision"])
        pending_data = dict(meeting["data"], recap_retry={
            "attempts": 0, "max_attempts": 3, "status": "pending",
            "next_check_at": stamp(-1),
        })
        meeting = products.put(
            "meeting", meeting_data["occurrence_id"], pending_data,
            state="recap_pending", revision=meeting["revision"])
        meeting = products.recap_retry(
            meeting["id"], meeting["revision"], "pending",
            "fixture-only:no-recap-yet", stamp(60))
        self.assertEqual(meeting["data"]["attendance"], "unknown")
        self.assertEqual(meeting["data"]["recap_retry"]["status"], "pending")
        self.assertEqual(meeting["data"]["work_item_ids"], [])

        with patch("work_productivity.utc_now", return_value=stamp(120)):
            meeting = products.recap_retry(
                meeting["id"], meeting["revision"], "available",
                "fixture-only:delayed-recap-arrived")
        recap = ledger.source(
            "meeting-recap", "occurrences", "occurrence-2026-09-06", "r1",
            {"summary": "Dana will revise the fictional checklist; no due date stated."},
            "https://example.com/recaps/occurrence-2026-09-06")
        candidate = ledger.ingest({
            "title": "Revise the fictional checklist",
            "owner": None,
            "due": None,
            "direction": "owe",
            "source_refs": [recap],
        }, "recap-action-candidate")
        proposed = dict(meeting["data"], work_item_ids=[candidate["id"]])
        meeting = products.put(
            "meeting", meeting_data["occurrence_id"], proposed,
            state="debrief_proposed", revision=meeting["revision"])
        meeting = products.put(
            "meeting", meeting_data["occurrence_id"], meeting["data"],
            state="reviewed", revision=meeting["revision"],
            evidence=decision(meeting["id"], meeting["revision"], "confirm"))

        next_data = {
            "series_id": meeting_data["series_id"],
            "occurrence_id": "occurrence-2026-09-13",
            "scheduled_start": stamp(604800),
            "scheduled_end": stamp(606600),
            "source_refs": [ledger.source(
                "calendar", "occurrences", "occurrence-2026-09-13", "c1",
                {"response": "accepted", "attendance": "unknown"},
                "https://example.com/calendar/occurrence-2026-09-13")],
            "attendance": "unknown",
            "work_item_ids": [],
            "topics": [],
        }
        next_meeting = products.put(
            "meeting", next_data["occurrence_id"], next_data, state="scheduled")
        carried = products.carry_forward(
            meeting["id"], meeting["revision"], next_meeting["id"],
            next_meeting["revision"],
            decision(meeting["id"], meeting["revision"], "carry_forward"))

        self.assertEqual(carried["source"]["state"], "carried_forward")
        self.assertEqual(carried["target"]["state"], "agenda_accumulating")
        self.assertEqual(carried["target"]["data"]["work_item_ids"], [candidate["id"]])
        self.assertEqual(carried["target"]["data"]["attendance"], "unknown")
        self.assertEqual(ledger.show(candidate["id"])["state"], "candidate")
        self.assertIsNone(ledger.show(candidate["id"])["data"]["due"])

    def test_task_pause_cancel_expiry_retry_unknown_write_and_resume(self):
        pause_run = self.store.create(
            "recovery:pause", self.plan("task-progress", [self.local_step()]))
        paused = self.store.pause(pause_run["id"], "Review the fictional plan.")
        with self.assertRaises(StateError):
            self.start(pause_run["id"], "prepare")
        resumed = self.store.resume(
            pause_run["id"], paused["revision"], self.binding)
        self.assertEqual(resumed["state"], "active")
        local = self.start(pause_run["id"], "prepare")
        self.store.finish(
            local["attempt_id"], local["token"], "succeeded",
            {"kind": "local_result", "reference": "private:fixture-resume",
             "summary": "Fictional task resumed without repeating completed work."})

        cancel_run = self.store.create(
            "recovery:cancel", self.plan("task-recovery", [self.local_step("cancel-me")]))
        cancelled = self.store.cancel(cancel_run["id"], "The fictional user will handle it.")
        self.assertEqual(cancelled["status"], "cancelled")
        with self.assertRaises(StateError):
            self.store.resume(cancel_run["id"], cancelled["revision"], self.binding)

        read_run = self.store.create(
            "recovery:expired-read",
            self.plan("task-recovery", [self.read_step("mail", "mail.read")],
                      lease_seconds=1))
        self.start(read_run["id"], "mail")
        future = datetime.now(timezone.utc) + timedelta(seconds=2)
        with patch("task_runs.clock", return_value=future):
            recovered = self.store.recover(read_run["id"])
        failed = recovered["steps"][0]
        self.assertEqual(failed["state"], "failed")
        with self.assertRaises(StateError):
            self.store.retry(
                read_run["id"], "mail", failed["revision"], self.binding)
        retry_at = datetime.fromisoformat(failed["next_retry_at"]) + timedelta(seconds=1)
        with patch("task_runs.clock", return_value=retry_at):
            retryable = self.store.retry(
                read_run["id"], "mail", failed["revision"], self.binding)
        self.assertEqual(retryable["steps"][0]["state"], "pending")

        source, _, action = self.create_approved_action("unknown")
        action_step = {
            "key": "uncertain-write",
            "title": "Execute a fictional approved write",
            "kind": "action",
            "capability": "mail.reply",
            "depends_on": [],
            "allow_partial": False,
            "cost": dict(zero(), tool_calls=1),
            "action_id": action["id"],
            "action_revision": action["revision"],
            "action_hash": action["action_hash"],
        }
        write_run = self.store.create(
            "recovery:unknown-write",
            self.plan("task-recovery", [action_step], lease_seconds=1))
        self.start(write_run["id"], "uncertain-write", {
            "checked_at": stamp(),
            "target_fingerprint": action["target_fingerprint"],
            "source_refs": [source],
        })
        with patch("task_runs.clock",
                   return_value=datetime.now(timezone.utc) + timedelta(seconds=2)):
            unknown = self.store.recover(write_run["id"])
        step = unknown["steps"][0]
        self.assertEqual((unknown["status"], step["state"]),
                         ("outcome_unknown", "outcome_unknown"))
        with self.assertRaises(StateError):
            self.store.retry(
                write_run["id"], step["key"], step["revision"], self.binding)
        receipt = {
            "kind": "reconciliation_read",
            "reference": "fixture-only:provider-history:receipt-unknown",
            "outcome": "succeeded",
            "recorded_at": stamp(),
            "fixture_only": True,
        }
        settled = self.store.reconcile(
            write_run["id"], step["key"], step["revision"], "succeeded", receipt)
        self.assertEqual(settled["status"], "succeeded")
        self.assertEqual(len(self.store.ledger.show(action["id"])["executions"]), 1)


if __name__ == "__main__":
    unittest.main()
