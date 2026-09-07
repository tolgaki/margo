import copy
from datetime import datetime, timezone
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))

import evaluate_agent_traces
import journey_contracts


class CanvasReferenceTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.directory = self.root / ".github/extensions/synthetic"
        self.directory.mkdir(parents=True)
        self.extension = self.directory / "extension.mjs"
        self.extension.write_text('''createCanvas({
            id: "synthetic-tasks",
            actions: [
                { name: "list", handler: () => [] },
                { name: "health", handler: () => ({}) },
            ],
            open: async () => ({})
        });
        createCanvas({
            id: "synthetic-other",
            actions: [
                { name: "unrelated", handler: () => ({}) },
            ],
            open: async () => ({})
        });
''', encoding="utf-8")

    def test_canvas_discovery_ignores_test_literals_regardless_of_file_order(self):
        (self.directory / "task.test.mjs").write_text(
            '''const marker = 'id: "synthetic-tasks"';''', encoding="utf-8")
        original_glob = Path.glob

        def reversed_glob(path, pattern):
            return iter(sorted(original_glob(path, pattern), reverse=True))

        with patch.object(Path, "glob", reversed_glob):
            journey_contracts._canvas_actions(
                {"id": "synthetic-tasks", "actions": ["list", "health"]}, self.root)

    def test_test_file_is_not_a_canvas_declaration(self):
        self.extension.rename(self.directory / "task.test.mjs")
        with self.assertRaisesRegex(journey_contracts.ContractError, "declaration does not exist"):
            journey_contracts._canvas_actions(
                {"id": "synthetic-tasks", "actions": ["list"]}, self.root)

    def test_actions_cannot_be_borrowed_from_another_canvas(self):
        with self.assertRaisesRegex(journey_contracts.ContractError, "missing canvas actions: unrelated"):
            journey_contracts._canvas_actions(
                {"id": "synthetic-tasks", "actions": ["unrelated"]}, self.root)

    def test_open_handler_names_are_not_registered_actions(self):
        self.extension.write_text('''createCanvas({
            id: "synthetic-tasks",
            open: async () => ({ name: "unregistered" })
        });
''', encoding="utf-8")
        with self.assertRaisesRegex(journey_contracts.ContractError, "missing canvas actions: unregistered"):
            journey_contracts._canvas_actions(
                {"id": "synthetic-tasks", "actions": ["unregistered"]}, self.root)


class JourneyContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.scenarios = journey_contracts.load_json(
            ROOT / "tests/fixtures/journeys/scenarios.json")
        cls.sources = journey_contracts.load_json(
            ROOT / "tests/fixtures/journeys/sources.json")
        cls.evals = journey_contracts.load_json(ROOT / "evals/scenarios-v1.json")

    def test_all_feature_ids_have_honest_validated_coverage(self):
        report = journey_contracts.validate_contract(
            self.scenarios, self.sources, self.evals)
        self.assertEqual(report["status"], "valid", report["errors"])
        self.assertEqual(report["scenario_count"], 68)
        self.assertEqual(sum(report["coverage_by_kind"].values()), 68)
        self.assertGreater(report["coverage_by_kind"]["runtime"], 0)
        self.assertGreater(report["coverage_by_kind"]["procedure-contract"], 0)
        self.assertGreater(report["coverage_by_kind"]["model-evaluation"], 0)
        self.assertTrue(report["evidence_limitations"])

    def test_missing_feature_and_fake_runtime_selector_fail(self):
        missing = copy.deepcopy(self.scenarios)
        missing["scenarios"].pop()
        report = journey_contracts.validate_contract(missing, self.sources, self.evals)
        self.assertEqual(report["status"], "invalid")
        self.assertTrue(any("missing feature scenarios" in error for error in report["errors"]))

        fake = copy.deepcopy(self.scenarios)
        runtime = next(row for row in fake["scenarios"] if row["coverage_kind"] == "runtime")
        runtime["evidence"]["selectors"] = [
            "test_user_journeys.UserJourneyTests.test_output_that_does_not_exist"]
        report = journey_contracts.validate_contract(fake, self.sources, self.evals)
        self.assertTrue(any("selector method does not exist" in error for error in report["errors"]))

    def test_procedure_metadata_cannot_masquerade_as_runtime_or_model_evidence(self):
        mutated = copy.deepcopy(self.scenarios)
        procedure = next(row for row in mutated["scenarios"]
                         if row["coverage_kind"] == "procedure-contract")
        procedure["evidence"]["selectors"] = [
            "test_proactive_state.StateTests.test_doctor_snapshot_cli_and_corrupt_state"]
        report = journey_contracts.validate_contract(
            mutated, self.sources, self.evals)
        self.assertTrue(any("non-runtime coverage cannot cite runtime selectors" in error
                            for error in report["errors"]))

        model = copy.deepcopy(self.scenarios)
        row = next(item for item in model["scenarios"]
                   if item["coverage_kind"] == "model-evaluation")
        row["model_scenario_id"] = "invented-evaluation"
        report = journey_contracts.validate_contract(model, self.sources, self.evals)
        self.assertTrue(any("versioned evaluation scenario" in error for error in report["errors"]))

    def test_declared_cli_and_canvas_reads_must_exist(self):
        missing_cli = copy.deepcopy(self.scenarios)
        task = next(row for row in missing_cli["scenarios"]
                    if row["id"] == "task-progress")
        task["cli_refs"][0]["commands"].append("invented-read")
        report = journey_contracts.validate_contract(
            missing_cli, self.sources, self.evals)
        self.assertTrue(any("CLI parser is missing commands" in error
                            for error in report["errors"]))

        missing_canvas = copy.deepcopy(self.scenarios)
        task = next(row for row in missing_canvas["scenarios"]
                    if row["id"] == "task-progress")
        task["canvas_refs"][0]["actions"].append("execute")
        report = journey_contracts.validate_contract(
            missing_canvas, self.sources, self.evals)
        self.assertTrue(any("missing canvas actions" in error
                            for error in report["errors"]))


class TraceEvaluatorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.data = json.loads((ROOT / "evals/scenarios-v1.json").read_text(encoding="utf-8"))
        cls.fixtures = json.loads(
            (ROOT / "tests/fixtures/journeys/sources.json").read_text(encoding="utf-8"))
        cls.scenario = next(row for row in cls.data["scenarios"]
                            if row["id"] == "approval-execution-v1")

    def trace(self):
        dimensions = {
            name: {"rating": "pass", "notes": "Reviewed against the synthetic fixture."}
            for name in self.scenario["human_rubric"]
        }
        return {
            "schema_version": 1,
            "scenario_id": self.scenario["id"],
            "fixture_version": self.scenario["fixture_version"],
            "fixture_sha256": evaluate_agent_traces.fixture_hash(self.fixtures, self.scenario["fixture_version"]),
            "provenance": {
                "model": "synthetic-trace-under-test",
                "model_version": "unit-test",
                "host": "synthetic-host",
                "host_version": "unit-test",
                "skill_versions": {
                    "chief-of-staff": "fixture-revision",
                    "mail": "fixture-revision",
                },
                "source_versions": {"commitment-v1": "1"},
            },
            "output": {
                "text": "The exact approved fictional reply has a fixture-only receipt.",
                "source_refs": ["mail:commitment-ask"],
                "labels": ["approval-bound", "receipt-recorded"],
            },
            "events": [
                {"seq": 1, "type": "route", "skill": "chief-of-staff"},
                {"seq": 2, "type": "route", "skill": "mail"},
                {"seq": 3, "type": "human_approval", "decision": "approve",
                 "account": "dana@example.com", "action_id": "fixture-action", "revision": 1,
                 "action_hash": "fixture-action-hash", "request": {"to": ["rafa@example.com"], "body": "Fictional reply."}},
                {"seq": 4, "type": "tool_call", "tool": "mail.reply", "effect": "write",
                 "account": "dana@example.com", "action_id": "fixture-action", "revision": 1,
                 "action_hash": "fixture-action-hash", "request": {"to": ["rafa@example.com"], "body": "Fictional reply."}},
            ],
            "counters": {
                "tool_calls": 1, "model_calls": 1, "input_tokens": 40,
                "output_tokens": 20, "elapsed_ms": 50,
            },
            "human_review": {
                "reviewer": "synthetic-reviewer",
                "reviewed_at": datetime.now(timezone.utc).isoformat(),
                "judgments": dimensions,
            },
        }

    def test_strict_trace_passes_only_with_deterministic_and_human_evidence(self):
        report = evaluate_agent_traces.evaluate_trace(self.trace(), self.scenario, self.fixtures)
        self.assertEqual(report["status"], "pass")
        self.assertTrue(all(check["status"] == "pass" for check in report["checks"]))
        self.assertEqual(report["counters"]["tool_calls"], 1)

    def test_mutated_approval_order_and_citation_fail(self):
        trace = self.trace()
        trace["events"][2], trace["events"][3] = trace["events"][3], trace["events"][2]
        for index, event in enumerate(trace["events"], 1):
            event["seq"] = index
        trace["output"]["source_refs"] = ["mail:unrelated"]
        report = evaluate_agent_traces.evaluate_trace(trace, self.scenario, self.fixtures)
        self.assertEqual(report["status"], "fail")
        failed = {check["id"] for check in report["checks"] if check["status"] == "fail"}
        self.assertIn("approval-ordering", failed)
        self.assertIn("source-required:mail:commitment-ask", failed)

    def test_missing_output_is_not_evaluated_and_unknown_metrics_are_not_zero(self):
        missing = self.trace()
        missing.pop("output")
        report = evaluate_agent_traces.evaluate_trace(missing, self.scenario, self.fixtures)
        self.assertEqual(report["status"], "not_evaluated")
        self.assertIn("output", report["missing_evidence"])

        unknown = self.trace()
        unknown["counters"]["tool_calls"] = None
        report = evaluate_agent_traces.evaluate_trace(unknown, self.scenario, self.fixtures)
        self.assertEqual(report["status"], "needs_review")
        budget = next(check for check in report["checks"]
                      if check["id"] == "budget:tool_calls")
        self.assertEqual(budget["status"], "unknown")
        self.assertNotIn("actual", budget)

    def test_absent_scenario_trace_is_reported_not_evaluated(self):
        bundle = evaluate_agent_traces.evaluate_results(
            {"schema_version": 1, "traces": [self.trace()]},
            {"schema_version": 1, "version": "test",
             "scenarios": [self.scenario, {
                 **self.scenario, "id": "missing-trace-v1",
             }]},
            self.fixtures)
        missing = next(row for row in bundle["results"]
                       if row["scenario_id"] == "missing-trace-v1")
        self.assertEqual(bundle["status"], "incomplete")
        self.assertEqual(missing["status"], "not_evaluated")
        self.assertEqual(missing["missing_evidence"], ["trace"])

    def test_mutated_evaluation_schema_fails_strict_validation(self):
        mutated = copy.deepcopy(self.data)
        mutated["scenarios"][0]["assertions"]["unreviewed_shortcut"] = True
        with self.assertRaises(evaluate_agent_traces.TraceError):
            evaluate_agent_traces.evaluate_results({"schema_version": 1, "traces": []}, mutated, self.fixtures)

        missing_source = copy.deepcopy(self.data)
        missing_source["scenarios"][0]["assertions"]["required_source_refs"].append(
            "mail:not-in-fixture")
        with self.assertRaises(evaluate_agent_traces.TraceError):
            evaluate_agent_traces.evaluate_results(
                {"schema_version": 1, "traces": []}, missing_source, self.fixtures)

    def test_missing_or_mislabelled_effect_cannot_hide_a_write(self):
        for field, value in (("effect", "read"), ("effect", None), ("tool", "unknown-tool")):
            trace = self.trace()
            if value is None:
                trace["events"][-1].pop(field)
            else:
                trace["events"][-1][field] = value
            report = evaluate_agent_traces.evaluate_trace(trace, self.scenario, self.fixtures)
            self.assertEqual(report["status"], "not_evaluated")
            self.assertTrue(report["binding_errors"])

    def test_approval_binds_request_identity_and_is_not_reusable(self):
        changed = self.trace()
        changed["events"][-1]["request"]["body"] = "Different payload."
        report = evaluate_agent_traces.evaluate_trace(changed, self.scenario, self.fixtures)
        self.assertEqual(report["status"], "fail")
        replay = self.trace()
        replay["events"].append(dict(replay["events"][-1], seq=5))
        replay["counters"]["tool_calls"] = 2
        report = evaluate_agent_traces.evaluate_trace(replay, self.scenario, self.fixtures)
        self.assertEqual(report["status"], "fail")
        revoked = self.trace()
        revoked["events"].insert(3, dict(revoked["events"][2], seq=4, decision="revoke"))
        revoked["events"][-1]["seq"] = 5
        self.assertEqual(evaluate_agent_traces.evaluate_trace(revoked, self.scenario, self.fixtures)["status"], "fail")

    def test_fixture_content_changes_are_not_hidden_by_reusing_a_version_label(self):
        changed = copy.deepcopy(self.fixtures)
        fixture = next(value for value in changed["fixtures"].values()
                       if value["version"] == self.scenario["fixture_version"])
        fixture["sources"][0]["changed_fixture_field"] = True
        report = evaluate_agent_traces.evaluate_trace(self.trace(), self.scenario, changed)
        self.assertEqual(report["status"], "not_evaluated")
        self.assertTrue(any("fingerprint" in error for error in report["binding_errors"]))


if __name__ == "__main__":
    unittest.main()
