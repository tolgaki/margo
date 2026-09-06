import copy
import json
import unittest

import test_memory_learning as learning
from memory_consolidation import consolidation_plan, surface_consolidation
from memory_learning import activate_lesson, lesson_plan, trend
from margo_store import StateError


class ConsolidationTests(unittest.TestCase):
    setUp = learning.LearningStoreTests.setUp
    tearDown = learning.LearningStoreTests.tearDown
    tool = learning.LearningStoreTests.tool
    fixture = learning.LearningStoreTests.fixture
    validated = learning.LearningStoreTests.validated
    lesson_data = learning.LearningStoreTests.lesson_data
    trend_data = learning.LearningStoreTests.trend_data

    def candidate(self, key, text=None):
        return self.memory.put(key, {
            "domain": "agent", "kind": "episode", "authority": "source_observed",
            "title": "Synthetic bounded review", "text": text or key,
            "scope": "synthetic", "source_refs": [{"kind": "tool_result", "ref": "synthetic:" + key}],
        })

    def test_trend_surfaces_material_corrections_and_ignores_wording_only_changes(self):
        record = trend(self.memory, "synthetic-trend", self.trend_data())
        plan = consolidation_plan(self.memory)
        self.assertEqual(len(plan["items"]), 1)
        result = surface_consolidation(self.memory, plan)
        self.assertEqual(len(result["surfaced"]), 1)
        self.assertEqual(surface_consolidation(self.memory, plan)["surfaced"], [])
        self.assertEqual(consolidation_plan(self.memory)["items"], [])
        self.memory.close()
        self.memory = learning.MemoryStore(account=self.env["account"], state_root=self.root)
        self.assertEqual(consolidation_plan(self.memory)["items"], [])
        changed_denominator = self.trend_data()
        changed_denominator["population"] = 100
        record = trend(self.memory, "synthetic-trend", changed_denominator, revision=record["revision"])
        corrected = consolidation_plan(self.memory)
        self.assertEqual(corrected["items"][0]["id"], plan["items"][0]["id"])
        self.assertEqual(len(surface_consolidation(self.memory, corrected)["surfaced"]), 1)
        self.assertEqual(consolidation_plan(self.memory)["items"], [])
        wording_only = dict(changed_denominator, title="Reworded synthetic title",
                            text="Reworded synthetic explanation.")
        record = trend(self.memory, "synthetic-trend", wording_only, revision=record["revision"])
        self.assertEqual(consolidation_plan(self.memory)["items"], [])
        record = trend(self.memory, "synthetic-trend", self.trend_data(), revision=record["revision"])
        reverted = consolidation_plan(self.memory)
        self.assertEqual(len(reverted["items"]), 1)
        self.assertEqual(len(surface_consolidation(self.memory, reverted)["surfaced"]), 1)
        self.assertEqual(consolidation_plan(self.memory)["items"], [])
        self.assertEqual(self.memory.show(record["id"])["status"], "candidate")

    def test_trend_window_coverage_definition_and_independent_events_are_material(self):
        record = trend(self.memory, "material-trend", self.trend_data())
        surface_consolidation(self.memory, consolidation_plan(self.memory))

        changed_window = self.trend_data()
        changed_window["window_end"] = "2026-09-02T00:00:00Z"
        record = trend(self.memory, "material-trend", changed_window, revision=record["revision"])
        plan = consolidation_plan(self.memory)
        self.assertEqual(len(plan["items"]), 1)
        surface_consolidation(self.memory, plan)

        definition = dict(learning.DEFAULT_TREND, min_coverage=0.5, require_complete=False)
        preview = learning.configure_trend(self.memory, "tool_failures", definition)
        learning.configure_trend(
            self.memory, "tool_failures", definition,
            evidence=learning.human(preview["subject_id"], decision="configure"))
        definition_changed = dict(changed_window)
        record = trend(self.memory, "material-trend", definition_changed, revision=record["revision"])
        plan = consolidation_plan(self.memory)
        self.assertEqual(len(plan["items"]), 1)
        surface_consolidation(self.memory, plan)

        changed_coverage = dict(changed_window)
        changed_coverage["coverage"] = {
            "status": "partial", "observed_population": 10, "expected_population": 12}
        record = trend(self.memory, "material-trend", changed_coverage, revision=record["revision"])
        plan = consolidation_plan(self.memory)
        self.assertEqual(len(plan["items"]), 1)
        surface_consolidation(self.memory, plan)

        added_event = copy.deepcopy(changed_coverage)
        added_event["events"].append({
            "id": "event-3", "independence_key": "execution-3",
            "occurred_at": "2026-08-04T00:00:00Z", "matched": True,
            "source_refs": [{"kind": "tool_result", "ref": "synthetic-result-3"}],
        })
        record = trend(self.memory, "material-trend", added_event, revision=record["revision"])
        plan = consolidation_plan(self.memory)
        self.assertEqual(len(plan["items"]), 1)
        surface_consolidation(self.memory, plan)
        self.assertEqual(consolidation_plan(self.memory)["items"], [])
        self.assertEqual(self.memory.show(record["id"])["status"], "candidate")

    def test_stale_plan_and_stale_replay_fail_without_mutations(self):
        record = self.candidate("one")
        plan = consolidation_plan(self.memory)
        surface_consolidation(self.memory, plan)
        changed = {key: value for key, value in record.items()
                   if key not in {"id", "account", "revision", "status", "created_at", "updated_at"}}
        changed["text"] = "A material correction"
        self.memory.revise(record["id"], changed, "candidate", 1)
        with self.assertRaises(StateError):
            surface_consolidation(self.memory, plan)
        fresh = consolidation_plan(self.memory)
        corrupted = copy.deepcopy(fresh)
        corrupted["items"][0]["reason"] = "activate_without_review"
        with self.assertRaises(StateError):
            surface_consolidation(self.memory, corrupted)
        self.assertEqual(self.memory.show(record["id"])["status"], "candidate")

    def test_bounded_pagination_does_not_drop_unsurfaced_candidates(self):
        expected = {self.candidate("candidate-" + str(index))["id"] for index in range(9)}
        surfaced, after = set(), None
        while True:
            plan = consolidation_plan(self.memory, limit=2, scan_limit=3, after=after)
            self.assertLessEqual(plan["scanned"], 3)
            self.assertLessEqual(len(plan["items"]), 2)
            result = surface_consolidation(self.memory, plan)
            surfaced.update(target["id"] for item in result["surfaced"] for target in item["targets"])
            after = plan["next_after"]
            if after is None:
                break
        self.assertEqual(surfaced, expected)
        self.assertTrue(all(record["status"] == "candidate" for record in self.memory.list()))
        for arguments in ({"limit": 0}, {"limit": 51}, {"scan_limit": 201}, {"scan_limit": True}):
            with self.assertRaises(StateError):
                consolidation_plan(self.memory, **arguments)

    def test_exact_duplicate_proposal_does_not_merge_or_confuse_environments(self):
        first = self.candidate("duplicate-a", text="Identical synthetic observation")
        second = self.candidate("duplicate-b", text="Identical synthetic observation")
        plan = consolidation_plan(self.memory)
        self.assertEqual(len(plan["items"]), 1)
        self.assertEqual(plan["items"][0]["reason"], "review_exact_duplicates")
        self.assertEqual({row["id"] for row in plan["items"][0]["targets"]}, {first["id"], second["id"]})
        surface_consolidation(self.memory, plan)
        self.assertEqual(len(self.memory.list()), 2)
        self.assertEqual(consolidation_plan(self.memory)["items"], [])
        self.assertEqual(self.memory.links(first["id"]), [])

    def test_duplicate_group_pagination_does_not_resurface_an_unchanged_member(self):
        expected = {self.candidate("duplicate-" + str(index), text="Same exact synthetic observation")["id"]
                    for index in range(3)}
        first = consolidation_plan(self.memory, limit=1, scan_limit=3)
        self.assertEqual({target["id"] for target in first["items"][0]["targets"]}, expected)
        surface_consolidation(self.memory, first)
        page = consolidation_plan(self.memory, limit=1, scan_limit=1, after=first["next_after"])
        self.assertEqual(page["items"], [])
        self.assertEqual(consolidation_plan(self.memory, scan_limit=1)["items"], [])

    def test_changed_capability_proposes_revalidation_without_fake_human_downgrade(self):
        cap, observation = self.validated()
        candidate = lesson_plan(self.memory, "reviewed-lesson", self.lesson_data(cap, observation))
        lesson = activate_lesson(self.memory, candidate["id"], 1, learning.human(candidate["id"]))
        environment = cap["metadata"]["environment_requirements"]
        self.assertEqual(consolidation_plan(self.memory, environment=environment)["items"], [])
        self.tool(version="v2")
        plan = consolidation_plan(self.memory, environment=environment)
        self.assertEqual(plan["items"][0]["reason"], "revalidate_lesson")
        surface_consolidation(self.memory, plan)
        self.assertEqual(self.memory.show(lesson["id"])["status"], "active")
        self.assertEqual(self.memory.show(lesson["id"])["revision"], lesson["revision"])
        self.assertEqual(consolidation_plan(self.memory, environment=environment)["items"], [])
        self.assertEqual(consolidation_plan(self.memory, environment=dict(
            environment, host="unrelated-host"))["items"], [])

    def test_file_drift_after_preview_and_forgotten_targets_are_rejected(self):
        cap, observation = self.validated()
        candidate = lesson_plan(self.memory, "candidate-lesson", self.lesson_data(cap, observation))
        plan = consolidation_plan(self.memory)
        path = learning.Path(observation["fixture"]["path"])
        path.write_text(json.dumps({"synthetic": True, "changed": True}), encoding="utf-8")
        with self.assertRaises(StateError):
            surface_consolidation(self.memory, plan)
        record = self.candidate("forgettable")
        fresh = consolidation_plan(self.memory)
        self.memory.forget(record["id"], 1, learning.human(record["id"], decision="forget"))
        with self.assertRaises(StateError):
            surface_consolidation(self.memory, fresh)
        self.assertNotIn(record["id"], [
            target["id"] for item in consolidation_plan(self.memory)["items"] for target in item["targets"]])
        self.assertEqual(self.memory.show(candidate["id"])["status"], "candidate")

    def test_delivery_receipts_keep_only_hashes_and_ids_not_source_text(self):
        self.candidate("private-candidate", text="Private synthetic wording should not be in delivery markers.")
        plan = consolidation_plan(self.memory)
        surface_consolidation(self.memory, plan)
        rows = self.memory.conn.execute(
            "SELECT value FROM memory_meta WHERE key LIKE 'learning:surfaced:%' OR key LIKE 'learning:delivery:%'").fetchall()
        self.assertTrue(rows)
        self.assertNotIn("Private synthetic wording", "\n".join(row[0] for row in rows))
        self.assertEqual(self.memory.conn.execute("SELECT COUNT(*) FROM memory_usage").fetchone()[0], 0)

    def test_deep_dependencies_are_bounded_and_proposed_for_review(self):
        record = None
        for index in range(15):
            sources = ([{"kind": "memory_record", "ref": record["id"], "revision": str(record["revision"])}]
                       if record else [{"kind": "tool_result", "ref": "synthetic-root"}])
            record = self.memory.put("bounded-chain-" + str(index), {
                "domain": "agent", "kind": "episode", "authority": "source_observed",
                "title": "Synthetic dependency", "text": "Synthetic layer " + str(index),
                "scope": "synthetic", "source_refs": sources,
            }, status="active")
        lesson = self.memory.put("bounded-chain-lesson", {
            "domain": "agent", "kind": "lesson", "authority": "inferred",
            "title": "Synthetic bounded proposal", "text": "Review this deeply derived candidate.",
            "scope": "synthetic", "source_refs": [
                {"kind": "memory_record", "ref": record["id"], "revision": str(record["revision"])}],
        })
        with learning.patch.object(self.memory, "_work_sources_current", side_effect=AssertionError("unbounded traversal")):
            plan = consolidation_plan(self.memory)
        self.assertEqual(plan["items"][0]["reason"], "revalidate_lesson")
        self.assertEqual(plan["items"][0]["targets"][0]["id"], lesson["id"])


if __name__ == "__main__":
    unittest.main()
