from pathlib import Path
import contextlib
import copy
import hashlib
import json
import os
import shutil
import sys
import unittest
import uuid
from datetime import datetime, timedelta, timezone
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = ROOT / "skills/chief-of-staff/scripts"
sys.path.insert(0, str(SCRIPTS))
from memory_learning import (
    DEFAULT_TREND, MAX_FILE_BYTES, activate_lesson, capabilities, classify_error, configure_trend,
    import_preferences, lesson_plan, preference_sections, preferences_plan, trend, validation,
)
from memory_store import MemoryStore
from margo_store import StateError
from work_ledger import Ledger
from work_productivity import Productivity


@contextlib.contextmanager
def fixture_directory():
    root = ROOT / (".memory-learning-test-" + uuid.uuid4().hex)
    root.mkdir(mode=0o700)
    try:
        yield str(root)
    finally:
        shutil.rmtree(root)


def stamp(minutes=0):
    return (datetime.now(timezone.utc) + timedelta(minutes=minutes)).isoformat()


def human(subject, revision=1, decision="confirm"):
    return {"kind": "human_confirmation", "actor": "dana@example.com",
            "subject_id": subject, "revision": revision, "decision": decision,
            "statement": "I reviewed this exact synthetic proposal.",
            "evidence_ref": "conversation:synthetic/review/1", "decided_at": stamp()}


class CaptureMemory:
    def __init__(self):
        self.records = []

    def record_id(self, domain, key):
        return domain + ":" + key

    def list(self, domain=None):
        return [row for row in self.records if not domain or row["domain"] == domain]

    def put(self, key, data, status="candidate", revision=None, evidence=None):
        identity = self.record_id(data["domain"], key)
        row = dict(data, id=identity, status=status, revision=1 if revision is None else revision + 1)
        self.records = [old for old in self.records if old["id"] != identity] + [row]
        return row


class LearningTests(unittest.TestCase):
    def test_capability_is_installed_not_validated_and_hash_change_creates_revision(self):
        memory = CaptureMemory()
        with fixture_directory() as temporary:
            root = Path(temporary)
            (root / "example").mkdir()
            path = root / "example/SKILL.md"
            path.write_text("# Example skill\n", encoding="utf-8")
            self.assertEqual(capabilities(memory, root)["observed"], 1)
            first = memory.records[0]
            self.assertEqual(first["metadata"]["validation"], "not_validated")
            capabilities(memory, root)
            self.assertEqual(memory.records[0]["revision"], 1)
            path.write_text("# Example skill\nNew version", encoding="utf-8")
            capabilities(memory, root)
            self.assertEqual(memory.records[0]["revision"], 2)
            self.assertNotEqual(first["metadata"]["content_hash"], memory.records[0]["metadata"]["content_hash"])

    @patch("memory_learning.utc_now", return_value="2026-09-06T12:00:00+00:00")
    def test_trends_require_coverage_population_and_distinct_events(self, _clock):
        memory = CaptureMemory()
        data = {"title": "Repeated preparation gap", "text": "Preparation gaps occurred in three observed meetings.",
                "scope": "meeting preparation", "window_start": "2026-08-01T00:00:00Z",
                "window_end": "2026-09-01T00:00:00Z", "coverage": "complete",
                "event_ids": ["evt1", "evt2", "evt3"], "population": 10,
                "source_refs": [{"kind": "tool_result", "ref": "synthetic-evidence"}]}
        row = trend(memory, "prep-gap", data)
        self.assertEqual(row["status"], "candidate")
        self.assertEqual(row["authority"], "inferred")
        self.assertEqual(row["metadata"]["rate"], 0.3)
        for invalid in (
            dict(data, coverage="partial"), dict(data, population=2),
            dict(data, event_ids=["evt1", "evt1", "evt1"]), dict(data, event_ids=["evt1"]),
        ):
            with self.assertRaises(StateError):
                trend(memory, "invalid", invalid)

    def test_preference_import_does_not_treat_empty_template_examples_as_facts(self):
        with fixture_directory() as temporary:
            path = Path(temporary) / "preferences.md"
            path.write_text(
                "# Profile template\n## About me\n"
                "- **Name:** Dana\n- **Role:** {fill me in}\n"
                "- **Hours:** (e.g. 9 to 5)\n\n## Scheduling defaults\n"
                "Keep 90-minute focus blocks where possible.\n", encoding="utf-8")
            sections = preference_sections(path)
            joined = "\n".join(body for _, body in sections)
            self.assertIn("Dana", joined)
            self.assertNotIn("{fill me in}", joined)
            self.assertNotIn("(e.g.", joined)
            self.assertIn("90-minute", joined)

    def test_wrapped_and_parenthesized_template_fields_are_fully_removed(self):
        with fixture_directory() as temporary:
            path = Path(temporary) / "preferences.md"
            path.write_text(
                "## Standing rules for triage\n"
                "- **Always flag:** {e.g. a direct ask,\n"
                "  customer escalations}\n"
                "- **Delegate to:** (who handles what)\n"
                "- **Hours:** (e.g. mornings,\n  or evenings)\n",
                encoding="utf-8")
            self.assertEqual(preference_sections(path), [])


class LearningStoreTests(unittest.TestCase):
    def setUp(self):
        self.folder = fixture_directory()
        self.root = Path(self.folder.__enter__())
        self.environment_patch = patch.dict(os.environ, {"MARGO_ALLOW_UNSAFE_STATE_DIR": "1"})
        self.environment_patch.start()
        self.memory = MemoryStore(account="synthetic-learning-account", state_root=self.root)
        self.ledger = Ledger(account=self.memory.account, state_root=self.root)
        self.products = Productivity(self.ledger)
        self.env = {"host": "synthetic-cli", "account": self.memory.account, "runtime": "python-synthetic"}

    def tearDown(self):
        self.ledger.close()
        self.memory.close()
        self.environment_patch.stop()
        self.folder.__exit__(None, None, None)

    def tool(self, name="synthetic-fetch", *, host=None, version="v1", exposed=True, schema=None):
        environment = dict(self.env, host=host or self.env["host"])
        path = self.root / (environment["host"] + "-tools.json")
        manifest = {
            "environment": environment, "observed_at": "2026-08-01T00:00:00Z", "complete": True,
            "tools": [{"name": name, "version": version, "exposed": exposed,
                       "input_schema": schema or {
                           "type": "object", "required": ["query"],
                           "properties": {"query": {"type": "string", "minLength": 1}},
                           "additionalProperties": False},
                       "required_permissions": ["read.synthetic"], "dependencies": {"transport": "v1"}}],
        }
        path.write_text(json.dumps(manifest), encoding="utf-8")
        result = capabilities(self.memory, environment=environment, tools=path)
        return self.memory.show(result["memory_ids"][0])

    def fixture(self, cap, *, valid=True, name="observation"):
        path = self.root / (name + ".json")
        document = {
            "synthetic": True, "environment": cap["metadata"]["environment_requirements"],
            "capability_id": cap["id"], "input": {"query": "synthetic query"} if valid else {"query": 42},
        }
        path.write_text(json.dumps(document), encoding="utf-8")
        return {"kind": "synthetic_observation",
                "fixture": {"path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}}

    def validated(self, **kwargs):
        cap = self.tool(**kwargs)
        observation = self.fixture(cap, name="success-" + cap["id"])
        cap = validation(self.memory, cap["id"], cap["revision"], observation)
        return cap, observation

    def lesson_data(self, cap, observation, **overrides):
        data = {
            "title": "Use the exposed synthetic query input shape",
            "scope": "synthetic query preparation", "trigger": "Before preparing the synthetic query",
            "goal": "Avoid an invalid input shape", "preconditions": ["Use the pinned host and tool schema"],
            "procedure": ["Provide a nonempty string query.", "Keep every external action behind its existing approval."],
            "risk": "read_only", "capability_refs": [{"id": cap["id"], "revision": cap["revision"]}],
            "evidence": [{"capability_id": cap["id"], "observation": observation}], "counterexamples": [],
        }
        data.update(overrides)
        return data

    def receipt(self, cap, *, outcome="succeeded", receipt_changes=None):
        ref = self.ledger.source("synthetic", "test", "fixture-" + uuid.uuid4().hex, "v1",
                                 {"synthetic": True}, "https://example.com/synthetic")
        item = self.ledger.ingest(
            {"title": "Synthetic action receipt fixture", "owner": None, "due": None,
             "direction": "owe", "source_refs": [ref]}, uuid.uuid4().hex)
        action = self.ledger.propose({
            "kind": "mail.reply", "target": {"message_id": "synthetic-message", "recipients": ["dana@example.com"]},
            "payload": {"body": "Synthetic fixture; never delivered."},
            "why": "Local receipt regression test, not a live tool call",
            "source_refs": [ref], "target_fingerprint": "synthetic-v1", "work_item_id": item["id"],
        })
        approval = dict(human(action["id"], decision="approve"), action_hash=action["action_hash"])
        self.ledger.approve(action["id"], 1, action["action_hash"], approval, stamp(30))
        attempt = self.ledger.begin(action["id"], 1, {
            "checked_at": stamp(), "target_fingerprint": "synthetic-v1", "source_refs": [ref]})
        receipt = {
            "kind": "tool_result", "reference": "tool:synthetic-existing-receipt",
            "outcome": outcome, "recorded_at": stamp(),
            "environment": cap["metadata"]["environment_requirements"],
            "capability_id": cap["id"], "capability_hash": cap["metadata"]["content_hash"],
        }
        if outcome == "failed":
            receipt.update(definitive_no_effect=True, error_code="policy_denied")
        receipt.update(receipt_changes or {})
        self.ledger.finish(attempt["attempt_id"], outcome, receipt)
        return {"kind": "execution_receipt", "attempt_id": attempt["attempt_id"]}, action

    def test_tool_discovery_exposure_and_retirement_are_not_competence(self):
        discovered = self.tool(exposed=False)
        self.assertEqual(discovered["metadata"]["state"], "discovered")
        with self.assertRaises(StateError):
            validation(self.memory, discovered["id"], discovered["revision"], {"success": True})
        installed = self.tool(exposed=True)
        self.assertEqual(installed["id"], discovered["id"])
        self.assertEqual(installed["metadata"]["state"], "installed")
        self.assertEqual(installed["metadata"]["validation"], "not_validated")
        path = self.root / "synthetic-cli-tools.json"
        manifest = json.loads(path.read_text())
        manifest["tools"] = []
        manifest["complete"] = False
        path.write_text(json.dumps(manifest), encoding="utf-8")
        self.assertEqual(capabilities(self.memory, tools=path)["retired_ids"], [])
        manifest["complete"] = True
        path.write_text(json.dumps(manifest), encoding="utf-8")
        self.assertEqual(capabilities(self.memory, tools=path)["retired_ids"], [installed["id"]])
        self.assertEqual(self.memory.show(installed["id"])["metadata"]["state"], "retired")

    def test_inventory_refresh_preserves_failure_history_and_consumed_receipts(self):
        cap = self.tool()
        success, _ = self.receipt(cap)
        cap = validation(self.memory, cap["id"], cap["revision"], success)
        failed, _ = self.receipt(cap, outcome="failed")
        cap = validation(self.memory, cap["id"], cap["revision"], failed)
        manifest_path = self.root / "synthetic-cli-tools.json"
        manifest = json.loads(manifest_path.read_text())
        manifest["observed_at"] = "2026-08-02T00:00:00Z"
        manifest_path.write_text(json.dumps(manifest))
        capabilities(self.memory, tools=manifest_path)
        refreshed = self.memory.show(cap["id"])
        replay = validation(self.memory, cap["id"], refreshed["revision"], success)
        self.assertNotEqual(replay["metadata"]["state"], "validated")
        self.assertTrue(any(event["outcome"] == "failed" for event in replay["metadata"]["validation_history"]))

    def test_inventory_clock_rejects_old_exposure_and_old_complete_empty_snapshots(self):
        cap = self.tool()
        path = self.root / "synthetic-cli-tools.json"
        original = path.read_text()
        newer = json.loads(original)
        newer["observed_at"] = "2026-08-03T00:00:00Z"
        newer["tools"][0]["exposed"] = False
        path.write_text(json.dumps(newer))
        capabilities(self.memory, tools=path)
        for old in (json.loads(original), dict(json.loads(original), tools=[])):
            path.write_text(json.dumps(old))
            with self.assertRaises(StateError):
                capabilities(self.memory, tools=path)
        self.assertEqual(self.memory.show(cap["id"])["metadata"]["availability"], "documented_not_exposed")

    def test_reinstalled_identical_skill_is_observed_again_but_not_validated(self):
        root = self.root / "skills"
        directory = root / "example"
        directory.mkdir(parents=True)
        path = directory / "SKILL.md"
        contents = "# Synthetic skill\n"
        path.write_text(contents)
        result = capabilities(self.memory, root, environment=self.env)
        identity = result["memory_ids"][0]
        path.unlink()
        capabilities(self.memory, root, environment=self.env)
        self.assertEqual(self.memory.show(identity)["metadata"]["state"], "retired")
        path.write_text(contents)
        capabilities(self.memory, root, environment=self.env)
        self.assertEqual(self.memory.show(identity)["metadata"]["state"], "installed")
        self.assertEqual(self.memory.show(identity)["metadata"]["validation"], "not_validated")

    def test_new_report_cannot_reverse_an_existing_independent_event_outcome(self):
        refs = [{"kind": "tool_result", "ref": "synthetic:trend"}]
        events = [{"id": "report-%d" % number, "independence_key": "event-%d" % number,
                   "occurred_at": "2026-08-02T00:00:00Z", "matched": True, "source_refs": refs}
                  for number in range(4)]
        data = {"title": "Synthetic preparation pattern", "text": "Four observed matches.",
                "scope": "review preparation", "trend_type": "meeting_preparation",
                "window_start": "2026-08-01T00:00:00Z", "window_end": "2026-08-04T00:00:00Z",
                "population": 10, "coverage": "complete", "events": events}
        first = trend(self.memory, "independent-contradiction", data)
        events[0] = dict(events[0], id="another-source-report", matched=False)
        with self.assertRaises(StateError):
            trend(self.memory, "independent-contradiction", data, revision=first["revision"])

    def test_trends_reject_future_window_ends_for_legacy_and_resolved_events(self):
        now = "2026-09-06T12:00:00+00:00"
        legacy = {
            "title": "Synthetic legacy trend", "text": "Three observed synthetic events.",
            "scope": "synthetic", "trend_type": "tool_failures",
            "window_start": "2026-09-01T00:00:00Z", "window_end": now,
            "population": 3, "coverage": "complete",
            "event_ids": ["legacy-1", "legacy-2", "legacy-3"],
            "source_refs": [{"kind": "tool_result", "ref": "synthetic:legacy"}],
        }
        resolved = self.trend_data()
        resolved["window_start"] = "2026-09-01T00:00:00Z"
        resolved["window_end"] = now
        for index, event in enumerate(resolved["events"], 1):
            event["occurred_at"] = "2026-09-0%dT00:00:00Z" % index
        with patch("memory_learning.utc_now", return_value=now):
            for key, data in (("closed-legacy", legacy), ("closed-resolved", resolved)):
                closed = trend(self.memory, key, data)
                self.assertEqual(
                    datetime.fromisoformat(closed["metadata"]["window_end"]),
                    datetime.fromisoformat(now))
            future = "2026-09-06T12:00:01+00:00"
            with self.assertRaisesRegex(StateError, "end at or before"):
                trend(self.memory, "future-legacy", dict(legacy, window_end=future))
            with self.assertRaisesRegex(StateError, "end at or before"):
                trend(self.memory, "future-resolved", dict(resolved, window_end=future))

    def test_validation_reproduces_input_checks_not_asserted_success(self):
        cap = self.tool()
        for invented in (
                {"outcome": "succeeded"}, {"kind": "tool_result", "ref": "fictional"},
                {"kind": "execution_receipt", "attempt_id": "nonexistent"},
                dict(self.fixture(cap), outcome="succeeded")):
            with self.assertRaises(StateError):
                validation(self.memory, cap["id"], cap["revision"], invented)
        failed = self.fixture(cap, valid=False, name="bad-input")
        degraded = validation(self.memory, cap["id"], cap["revision"], failed)
        self.assertEqual(degraded["metadata"]["state"], "degraded")
        self.assertEqual(degraded["metadata"]["last_validation"]["error_category"], "bad_data")
        succeeded = validation(self.memory, cap["id"], degraded["revision"], self.fixture(cap))
        self.assertEqual(succeeded["metadata"]["state"], "validated")
        self.assertFalse(succeeded["metadata"]["last_validation"]["runtime_validated"])
        replay = validation(self.memory, cap["id"], succeeded["revision"], self.fixture(cap))
        self.assertEqual(replay["revision"], succeeded["revision"])
        capabilities(self.memory, tools=self.root / "synthetic-cli-tools.json")
        self.assertEqual(self.memory.show(cap["id"])["revision"], succeeded["revision"])

    def test_synthetic_fixture_is_pinned_and_scope_cannot_be_forged(self):
        cap = self.tool()
        evidence = self.fixture(cap)
        path = Path(evidence["fixture"]["path"])
        content = json.loads(path.read_text())
        content["environment"]["host"] = "other-host"
        path.write_text(json.dumps(content), encoding="utf-8")
        with self.assertRaises(StateError):
            validation(self.memory, cap["id"], 1, evidence)
        evidence["fixture"]["sha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
        with self.assertRaises(StateError):
            validation(self.memory, cap["id"], 1, evidence)
        content["environment"] = cap["metadata"]["environment_requirements"]
        content["synthetic"] = False
        path.write_text(json.dumps(content), encoding="utf-8")
        evidence["fixture"]["sha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
        with self.assertRaises(StateError):
            validation(self.memory, cap["id"], 1, evidence)

    def test_old_success_replay_cannot_erase_a_new_counterexample(self):
        cap, success = self.validated()
        failure = self.fixture(cap, valid=False, name="new-counterexample")
        failed = validation(self.memory, cap["id"], cap["revision"], failure)
        replay = validation(self.memory, cap["id"], failed["revision"], success)
        self.assertEqual(replay["metadata"]["state"], "degraded")
        self.assertEqual(replay["revision"], failed["revision"])
        self.assertEqual(len(replay["metadata"]["validation_history"]), 2)

    def test_unimplemented_schema_cannot_be_reported_validated(self):
        cap = self.tool(schema={"type": "object", "oneOf": [{"required": ["query"]}]})
        with self.assertRaises(StateError):
            validation(self.memory, cap["id"], cap["revision"], self.fixture(cap))
        self.assertEqual(self.memory.show(cap["id"])["metadata"]["state"], "installed")

    def test_existing_receipt_is_resolved_and_scoped_not_caller_asserted(self):
        cap = self.tool()
        result, _ = self.receipt(cap)
        validated = validation(self.memory, cap["id"], cap["revision"], result)
        self.assertTrue(validated["metadata"]["last_validation"]["runtime_validated"])
        other = self.tool(host="other-host")
        with self.assertRaises(StateError):
            validation(self.memory, other["id"], other["revision"], result)
        bad, _ = self.receipt(other, receipt_changes={"environment": self.env})
        with self.assertRaises(StateError):
            validation(self.memory, other["id"], other["revision"], bad)
        with MemoryFixture(self.root, "other-synthetic-account") as other_account:
            with self.assertRaises(StateError):
                other_account.show(validated["id"])
        failure, _ = self.receipt(validated, outcome="failed")
        degraded = validation(self.memory, cap["id"], validated["revision"], failure)
        self.assertEqual(degraded["metadata"]["last_validation"]["error_category"], "policy_denial")
        self.assertEqual(other["metadata"]["state"], "installed")

    def test_changed_skill_file_invalidates_lesson_without_inventory_or_human_mutation(self):
        root = self.root / "skills"
        (root / "synthetic-skill").mkdir(parents=True)
        path = root / "synthetic-skill/SKILL.md"
        path.write_text("# Synthetic skill version one\n", encoding="utf-8")
        observed = capabilities(self.memory, root, self.env)
        cap = self.memory.show(observed["memory_ids"][0])
        receipt, _ = self.receipt(cap)
        cap = validation(self.memory, cap["id"], cap["revision"], receipt)
        lesson = lesson_plan(self.memory, "skill-lesson", self.lesson_data(cap, receipt))
        lesson = activate_lesson(self.memory, lesson["id"], 1, human(lesson["id"]))
        environment = cap["metadata"]["environment_requirements"]
        self.assertIn(lesson["id"], [row["id"] for row in self.memory.eligible(environment=environment)])
        path.write_text("# Synthetic skill version two\n", encoding="utf-8")
        self.assertNotIn(lesson["id"], [row["id"] for row in self.memory.eligible(environment=environment)])
        self.assertEqual(self.memory.show(lesson["id"])["revision"], lesson["revision"])
        self.assertEqual(self.memory.show(lesson["id"])["status"], "active")

    def test_reviewed_lesson_is_recalled_only_in_its_pinned_environment(self):
        cap, observation = self.validated()
        data = self.lesson_data(cap, observation)
        candidate = lesson_plan(self.memory, "synthetic-lesson", data)
        self.assertEqual(candidate["status"], "candidate")
        self.assertNotIn(candidate["id"], [row["id"] for row in self.memory.eligible(
            environment=cap["metadata"]["environment_requirements"])])
        with self.assertRaises(StateError):
            activate_lesson(self.memory, candidate["id"], candidate["revision"], {})
        with self.assertRaises(StateError):
            activate_lesson(self.memory, candidate["id"], candidate["revision"],
                            human(candidate["id"], candidate["revision"] + 1))
        accepted = activate_lesson(self.memory, candidate["id"], candidate["revision"],
                                   human(candidate["id"], candidate["revision"]))
        environment = cap["metadata"]["environment_requirements"]
        self.assertIn(accepted["id"], [row["id"] for row in self.memory.eligible(environment=environment)])
        self.assertNotIn(accepted["id"], [row["id"] for row in self.memory.eligible(
            environment=dict(environment, host="other-host"))])
        self.memory.close()
        self.memory = MemoryStore(account=self.env["account"], state_root=self.root)
        self.assertIn(accepted["id"], [row["id"] for row in self.memory.eligible(environment=environment)])
        new_cap = self.tool(version="v2")
        self.assertNotEqual(new_cap["metadata"]["content_hash"], cap["metadata"]["content_hash"])
        self.assertNotIn(accepted["id"], [row["id"] for row in self.memory.eligible(environment=environment)])
        self.assertEqual(self.memory.show(accepted["id"])["status"], "active")
        with self.assertRaises(StateError):
            lesson_plan(self.memory, "synthetic-lesson", data, revision=accepted["revision"])

    def test_proposal_replay_is_stable_after_store_canonicalization(self):
        cap, observation = self.validated()
        data = self.lesson_data(cap, observation, entities=["tool:z", "tool:a"], routines=["z", "a"])
        first = lesson_plan(self.memory, "canonical-lesson", data)
        self.assertEqual(lesson_plan(self.memory, "canonical-lesson", data)["revision"], first["revision"])
        values = dict(self.trend_data(), entities=["tool:z", "tool:a"], routines=["z", "a"])
        first = trend(self.memory, "canonical-trend", values)
        self.assertEqual(trend(self.memory, "canonical-trend", values)["revision"], first["revision"])

    def test_lessons_capture_real_counterexamples_and_do_not_count_duplicates(self):
        cap, observation = self.validated()
        bad = self.fixture(cap, valid=False, name="counterexample")
        example = {"capability_id": cap["id"], "observation": bad, "condition": "Query is not a string."}
        candidate = lesson_plan(self.memory, "lesson-with-counterexample",
                                self.lesson_data(cap, observation, counterexamples=[example]))
        self.assertEqual(candidate["metadata"]["counterexamples"][0]["error_category"], "bad_data")
        self.assertEqual(len(candidate["metadata"]["evidence_ids"]), 2)
        self.assertIn("Query is not a string.", candidate["text"])
        self.assertIn("remote execution is not validated", candidate["text"])
        self.assertIn("no stored lesson grants permissions", candidate["text"])
        self.assertIn("Use the pinned host and tool schema", candidate["text"])
        with self.assertRaises(StateError):
            lesson_plan(self.memory, "duplicate-observation", self.lesson_data(
                cap, observation, evidence=[{"capability_id": cap["id"], "observation": observation}] * 2))
        with self.assertRaises(StateError):
            lesson_plan(self.memory, "invented-counterexample", self.lesson_data(
                cap, observation, counterexamples=[dict(example, observation={"success": False})]))
        failed_only = lesson_plan(self.memory, "unproven-recovery", self.lesson_data(cap, bad))
        with self.assertRaises(StateError):
            activate_lesson(self.memory, failed_only["id"], 1, human(failed_only["id"]))

    def test_feedback_must_be_existing_reviewed_and_not_opted_out(self):
        cap = self.tool()
        observation, action = self.receipt(cap)
        cap = validation(self.memory, cap["id"], cap["revision"], observation)
        feedback_id = self.products.record_id("feedback", "synthetic-feedback")
        feedback = self.products.put(
            "feedback", "synthetic-feedback",
            {"subject_id": action["id"], "subject_revision": 1,
             "correction": "Use a nonempty string query."},
            evidence=human(feedback_id))
        candidate = lesson_plan(self.memory, "feedback-lesson", self.lesson_data(
            cap, observation, feedback_refs=[{"id": feedback["id"], "revision": 1}]))
        self.assertEqual(candidate["metadata"]["feedback_refs"], [{"id": feedback["id"], "revision": 1}])
        opted_id = self.products.record_id("feedback", "opted-out")
        opted = self.products.put(
            "feedback", "opted-out", {"subject_id": action["id"], "subject_revision": 1, "do_not_learn": True},
            evidence=human(opted_id))
        for reference in ({"id": "nonexistent", "revision": 1},
                          {"id": opted["id"], "revision": 1}, {"id": feedback["id"], "revision": 99}):
            with self.assertRaises(StateError):
                lesson_plan(self.memory, "bad-feedback", self.lesson_data(
                    cap, observation, feedback_refs=[reference]))

    def trend_data(self, count=3):
        return {
            "title": "Repeated synthetic input failures", "text": "Synthetic input-contract failures in the supplied events.",
            "scope": "synthetic tool checks", "trend_type": "tool_failures",
            "window_start": "2026-08-01T00:00:00Z", "window_end": "2026-09-01T00:00:00Z",
            "population": 10, "coverage": "complete",
            "events": [{"id": "event-" + str(index), "independence_key": "execution-" + str(index),
                        "occurred_at": "2026-08-%02dT00:00:00Z" % (index + 1), "matched": True,
                        "source_refs": [{"kind": "tool_result", "ref": "synthetic-result-" + str(index)}]}
                       for index in range(count)],
        }

    def test_resolved_trends_deduplicate_events_and_corroborating_sources(self):
        data = self.trend_data()
        data["events"].append(copy.deepcopy(data["events"][0]))
        corroboration = copy.deepcopy(data["events"][0])
        corroboration.update(id="another-report", source_refs=[{"kind": "tool_result", "ref": "another-source"}])
        data["events"].append(corroboration)
        row = trend(self.memory, "input-failures", data)
        self.assertEqual(row["metadata"]["observations"], 3)
        self.assertEqual(row["metadata"]["rate"], 0.3)
        self.assertEqual(trend(self.memory, "input-failures", data)["revision"], 1)
        copied = self.trend_data(1)
        copied["events"] *= 3
        with self.assertRaises(StateError):
            trend(self.memory, "manufactured", copied)
        for changes in ({"occurred_at": "2026-10-01T00:00:00Z"}, {"matched": False}):
            invalid = self.trend_data()
            invalid["events"].append(dict(invalid["events"][0], **changes))
            with self.assertRaises(StateError):
                trend(self.memory, "conflicting", invalid)

    def test_trend_definitions_require_exact_review_and_control_thresholds(self):
        definition = dict(DEFAULT_TREND, min_independent_events=4, min_population=5,
                          min_coverage=0.75, require_complete=False, window_days=31)
        preview = configure_trend(self.memory, "tool_failures", definition)
        self.assertEqual(preview["expected_revision"], 0)
        with self.assertRaises(StateError):
            configure_trend(self.memory, "tool_failures", definition, evidence=human("wrong", decision="configure"))
        configure_trend(self.memory, "tool_failures", definition,
                        evidence=human(preview["subject_id"], decision="configure"))
        with self.assertRaises(StateError):
            configure_trend(self.memory, "tool_failures", definition, revision=0)
        with self.assertRaises(StateError):
            trend(self.memory, "not-enough", self.trend_data())
        data = self.trend_data(4)
        data["coverage"] = {"status": "partial", "observed_population": 10, "expected_population": 12}
        row = trend(self.memory, "bounded-partial", data)
        self.assertEqual(row["metadata"]["coverage_fraction"], 10 / 12)
        self.assertEqual(row["metadata"]["rate_scope"], "observed_population_only")
        data["coverage"]["expected_population"] = 20
        with self.assertRaises(StateError):
            trend(self.memory, "too-partial", data)
        data = self.trend_data(4)
        data["window_start"] = "2026-07-01T00:00:00Z"
        with self.assertRaises(StateError):
            trend(self.memory, "too-long", data)

    def test_trend_updates_require_revision_and_do_not_rewrite_events(self):
        row = trend(self.memory, "stable-trend", self.trend_data())
        with self.assertRaises(StateError):
            trend(self.memory, "stable-trend", self.trend_data(4))
        updated = trend(self.memory, "stable-trend", self.trend_data(4), revision=row["revision"])
        self.assertEqual(updated["revision"], 2)
        bad = self.trend_data(4)
        bad["events"][0]["matched"] = False
        with self.assertRaises(StateError):
            trend(self.memory, "stable-trend", bad, revision=2)
        with self.assertRaises(StateError):
            trend(self.memory, "scoring", dict(self.trend_data(), trend_type="colleague_competence"))
        with self.assertRaises(StateError):
            trend(self.memory, "person-scoring", dict(self.trend_data(), entities=["person:fictional"]))

    def test_error_classification_never_generalizes_unknown_errors(self):
        expected = {"400": "caller_mistake", "tool_not_found": "stale_host_binding",
                    "403": "policy_denial", "not_implemented": "missing_capability",
                    "invalid_json": "bad_data", "timeout": "transient_environment_failure"}
        for code, category in expected.items():
            self.assertEqual(classify_error(code), category)
        self.assertEqual(classify_error("unknown_failure"), "unclassified")
        self.assertEqual(classify_error("confirmed_defect"), "unclassified")
        self.assertEqual(classify_error("confirmed_defect", confirmed_defect=True), "actual_defect")

    def test_preference_import_preserves_exact_review_and_stale_replay_protection(self):
        path = self.root / "preferences.md"
        path.write_text("## Scheduling defaults\nProtect synthetic focus blocks.\n", encoding="utf-8")
        preview = preferences_plan(self.memory, path)
        approval = human(preview["subject_id"], decision="import")
        result = import_preferences(self.memory, path, approval)
        self.assertEqual(result["imported"], 1)
        self.assertTrue(import_preferences(self.memory, path, approval)["replayed"])
        path.write_text("## Scheduling defaults\nChanged synthetic preference.\n", encoding="utf-8")
        with self.assertRaises(StateError):
            import_preferences(self.memory, path, approval)
        self.assertEqual(self.memory.show(result["memory_ids"][0])["text"], "Protect synthetic focus blocks.")

    def test_preference_chunks_are_nonempty_bounded_ordered_and_importable(self):
        for length in (1498, 1499, 1500):
            with self.subTest(length=length):
                path = self.root / ("preferences-%d.md" % length)
                body = "x" * length
                path.write_text("## Communication preferences\n" + body + "\n", encoding="utf-8")
                preview = preferences_plan(self.memory, path)
                self.assertEqual([item["data"]["text"] for item in preview["records"]], [body])
                imported = import_preferences(
                    self.memory, path, human(preview["subject_id"], decision="import"))
                self.assertEqual(imported["imported"], 1)
                self.assertEqual(self.memory.show(imported["memory_ids"][0])["text"], body)

        oversized = self.root / "preferences-1501.md"
        oversized.write_text(
            "## Communication preferences\n" + ("private-synthetic-" * 100)[:1501] + "\n",
            encoding="utf-8")
        with self.assertRaisesRegex(StateError, "preference section 1") as raised:
            preferences_plan(self.memory, oversized)
        self.assertNotIn("private-synthetic", str(raised.exception))

        invalid_heading = self.root / "preferences-invalid-heading.md"
        invalid_heading.write_text(
            "## " + ("private-heading-" * 300) + "\nSynthetic preference.\n",
            encoding="utf-8")
        with self.assertRaisesRegex(StateError, "preference section 1") as raised:
            preferences_plan(self.memory, invalid_heading)
        self.assertNotIn("private-heading", str(raised.exception))

        paragraphs = self.root / "preferences-paragraphs.md"
        first, second, third = "a" * 749, "b" * 749, "c" * 10
        paragraphs.write_text(
            "## Communication preferences\n%s\n\n%s\n\n%s\n" % (first, second, third),
            encoding="utf-8")
        preview = preferences_plan(self.memory, paragraphs)
        texts = [item["data"]["text"] for item in preview["records"]]
        self.assertEqual(texts, [first + "\n\n" + second, third])
        self.assertTrue(all(text and len(text) <= 1500 for text in texts))
        imported = import_preferences(
            self.memory, paragraphs, human(preview["subject_id"], decision="import"))
        self.assertEqual(
            [self.memory.show(identity)["text"] for identity in imported["memory_ids"]],
            texts)

        lines = self.root / "preferences-lines.md"
        first_line, second_line = "d" * 1000, "e" * 500
        lines.write_text(
            "## Communication preferences\n%s\n%s\n" % (first_line, second_line),
            encoding="utf-8")
        preview = preferences_plan(self.memory, lines)
        texts = [item["data"]["text"] for item in preview["records"]]
        self.assertEqual(texts, [first_line, second_line])
        self.assertTrue(all(text and len(text) <= 1500 for text in texts))
        imported = import_preferences(
            self.memory, lines, human(preview["subject_id"], decision="import"))
        self.assertEqual(
            [self.memory.show(identity)["text"] for identity in imported["memory_ids"]],
            texts)

    def test_preference_preview_uses_the_bounded_source_payload(self):
        path = self.root / "bounded-preferences.md"
        path.write_text(
            "## Communication preferences\nKeep synthetic summaries concise.\n",
            encoding="utf-8")
        with patch.object(Path, "read_bytes", side_effect=AssertionError("unbounded read")):
            preview = preferences_plan(self.memory, path)
        self.assertEqual(len(preview["records"]), 1)

        oversized = self.root / "oversized-preferences.md"
        oversized.write_bytes(b"x" * (MAX_FILE_BYTES + 1))
        metadata_before = self.memory.conn.execute("SELECT COUNT(*) FROM memory_meta").fetchone()[0]
        with self.assertRaisesRegex(StateError, "1 MiB source budget"):
            preferences_plan(self.memory, oversized)
        self.assertEqual(
            self.memory.conn.execute("SELECT COUNT(*) FROM memory_meta").fetchone()[0],
            metadata_before)


class MemoryFixture:
    def __init__(self, root, account):
        self.memory = MemoryStore(account=account, state_root=root)

    def __enter__(self):
        return self.memory

    def __exit__(self, *args):
        self.memory.close()


if __name__ == "__main__":
    unittest.main()
