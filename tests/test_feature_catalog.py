"""Tests for tools/feature_catalog.py.

These use synthetic fixtures and check structured failures, plus the actual catalog/guide/scenario
integration. Missing or malformed integration artifacts are failures, never temporary passes.
"""
import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
TOOLS_DIR = ROOT / "tools"
sys.path.insert(0, str(TOOLS_DIR))

import feature_catalog as fc  # noqa: E402


def load_real_catalog():
    return json.loads(fc.CATALOG_PATH.read_text(encoding="utf-8"))


def synthetic_scenario_manifest(feature_ids):
    """A minimal, complete scenario manifest covering every given feature id."""
    return {
        "schema_version": 1,
        "contract_version": "1.0.0",
        "scenarios": [
            {
                "id": fid,
                "feature_id": fid,
                "title": f"Synthetic scenario for {fid}",
                "coverage_kind": "procedure-contract",
                "source_fixture": "sources.json#synthetic",
                "routing": {"expected_skills": ["chief-of-staff"], "forbidden_skills": []},
                "expected": {
                    "user_visible_invariants": ["Synthetic invariant."],
                    "forbidden_effects": ["Synthetic forbidden effect."],
                    "evidence_limitations": ["Synthetic evidence limitation."],
                },
                "evidence": {"selectors": [], "proves": [], "does_not_prove": ["Everything."]},
            }
            for fid in feature_ids
        ],
    }


def write_json(directory, name, data):
    path = Path(directory) / name
    path.write_text(json.dumps(data), encoding="utf-8")
    return path


class RealCatalogTests(unittest.TestCase):
    """The shipped catalog must validate cleanly against a synthetic, complete manifest."""

    def test_real_catalog_has_exactly_67_features_matching_the_shared_contract(self):
        catalog = load_real_catalog()
        ids = [f["id"] for f in catalog["features"]]
        self.assertEqual(len(ids), 67)
        self.assertEqual(set(ids), set(fc.EXPECTED_FEATURE_IDS))
        self.assertEqual(len(ids), len(set(ids)), "duplicate feature id present")

    def test_real_catalog_passes_full_validation_against_a_synthetic_manifest(self):
        catalog = load_real_catalog()
        with tempfile.TemporaryDirectory() as tmp:
            manifest_path = write_json(
                tmp, "scenarios.json", synthetic_scenario_manifest(fc.EXPECTED_FEATURE_IDS)
            )
            errors = fc.validate_catalog(catalog, scenario_path=manifest_path)
        self.assertEqual(errors, [], "\n".join(errors))

    def test_cli_check_passes_against_the_real_repository(self):
        # End-to-end: real guides, real router files, real CLI modules, real canvas extension.
        result = subprocess.run(
            [sys.executable, str(TOOLS_DIR / "feature_catalog.py"), "--check"],
            cwd=ROOT, capture_output=True, text=True, timeout=60,
        )
        self.assertEqual(result.returncode, 0, f"{result.stdout}\n{result.stderr}")

    def test_reference_paths_cannot_leave_the_repository(self):
        for path in ("../private-guide.md", "/private/guide.md"):
            catalog = load_real_catalog()
            catalog["features"][0]["guide"] = {"path": path, "anchor": "anything"}
            errors = fc.validate_catalog(catalog)
            self.assertTrue(any("repository-relative" in error for error in errors), errors)

    def test_malformed_containers_and_design_availability_are_rejected(self):
        catalog = load_real_catalog()
        catalog["features"][0]["cli_refs"] = 7
        errors = fc.validate_catalog(catalog)
        self.assertTrue(any("must be a list" in error for error in errors), errors)
        catalog = load_real_catalog()
        catalog["features"][0]["implementation_kind"] = "design"
        errors = fc.validate_catalog(catalog)
        self.assertTrue(any("design-only" in error for error in errors), errors)

    def test_duplicate_json_fields_are_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "catalog.json"
            path.write_text('{"schema_version":1,"schema_version":2,"features":[]}', encoding="utf-8")
            with self.assertRaises(fc.CatalogError):
                fc.load_catalog(path)


class SlugifyTests(unittest.TestCase):
    def test_slugify_matches_github_heading_anchor_rules(self):
        self.assertEqual(fc.slugify("Daily Brief"), "daily-brief")
        self.assertEqual(fc.slugify("1. Choose copy or link"), "1-choose-copy-or-link")
        self.assertEqual(fc.slugify("One-on-ones"), "one-on-ones")
        self.assertEqual(fc.slugify("Do not learn"), "do-not-learn")
        self.assertEqual(fc.slugify("Approval, execution!"), "approval-execution")

    def test_extract_headings_ignores_fenced_code_blocks(self):
        text = "# Title\n\n```\n# not a heading\n```\n\n## Real heading\n"
        self.assertEqual(fc.extract_headings(text), ["Title", "Real heading"])

    def test_anchors_for_file_deduplicates_like_github(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "doc.md"
            path.write_text("## Repeat\n\ntext\n\n## Repeat\n", encoding="utf-8")
            anchors = fc.anchors_for_file(path)
        self.assertEqual(anchors, {"repeat", "repeat-1"})


class MutationTests(unittest.TestCase):
    """Meaningful negative cases: each mutates a valid catalog in one specific way."""

    def setUp(self):
        self.catalog = load_real_catalog()
        self.tmpdir = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmpdir.cleanup)
        self.manifest_path = write_json(
            self.tmpdir.name, "scenarios.json",
            synthetic_scenario_manifest(fc.EXPECTED_FEATURE_IDS),
        )

    def validate(self, catalog):
        return fc.validate_catalog(catalog, scenario_path=self.manifest_path)

    def find(self, catalog, feature_id):
        return next(f for f in catalog["features"] if f["id"] == feature_id)

    def test_dropped_feature_is_reported_missing(self):
        catalog = copy.deepcopy(self.catalog)
        catalog["features"] = [f for f in catalog["features"] if f["id"] != "daily-brief"]
        errors = self.validate(catalog)
        self.assertTrue(any("missing required feature ids" in e and "daily-brief" in e for e in errors))

    def test_unexpected_extra_feature_id_is_rejected(self):
        catalog = copy.deepcopy(self.catalog)
        bogus = copy.deepcopy(self.find(catalog, "daily-brief"))
        bogus["id"] = "not-a-real-feature"
        bogus["scenario_ids"] = ["not-a-real-feature"]
        catalog["features"].append(bogus)
        errors = self.validate(catalog)
        self.assertTrue(any("unexpected feature ids" in e and "not-a-real-feature" in e for e in errors))

    def test_duplicate_feature_id_is_rejected(self):
        catalog = copy.deepcopy(self.catalog)
        catalog["features"].append(copy.deepcopy(self.find(catalog, "daily-brief")))
        errors = self.validate(catalog)
        self.assertTrue(any("duplicate feature ids" in e and "daily-brief" in e for e in errors))

    def test_omitted_router_row_is_detected(self):
        catalog = copy.deepcopy(self.catalog)
        # "Daily Brief" is claimed only by the daily-brief feature; clearing it uncovers the row.
        self.find(catalog, "daily-brief")["router_refs"] = []
        errors = self.validate(catalog)
        self.assertTrue(any("Core routines row(s) not covered" in e and "Daily Brief" in e for e in errors))

    def test_omitted_automation_manifest_is_detected(self):
        catalog = copy.deepcopy(self.catalog)
        # automations/morning-brief.md is claimed by daily-brief and automation-morning only.
        self.find(catalog, "daily-brief")["automation_refs"] = []
        self.find(catalog, "automation-morning")["automation_refs"] = []
        errors = self.validate(catalog)
        self.assertTrue(any(
            "automations not referenced by any feature" in e and "morning-brief.md" in e
            for e in errors
        ))

    def test_scheduled_claim_without_automation_is_rejected(self):
        catalog = copy.deepcopy(self.catalog)
        self.find(catalog, "engage")["surfaces"].append("scheduled")
        errors = self.validate(catalog)
        self.assertTrue(any(
            "engage" in e and "scheduled surface requires an automation_refs mapping" in e
            for e in errors
        ), errors)

    def test_malformed_enum_containers_are_reported_without_crashing(self):
        for field in ("availability", "implementation_kind"):
            for value in ([], {}, None):
                with self.subTest(field=field, value=value):
                    catalog = copy.deepcopy(self.catalog)
                    self.find(catalog, "engage")[field] = value
                    errors = self.validate(catalog)
                    self.assertTrue(any(f"invalid {field}" in e for e in errors), errors)

    def test_omitted_cli_command_group_is_detected(self):
        catalog = copy.deepcopy(self.catalog)
        commitments = self.find(catalog, "commitments")
        for ref in commitments["cli_refs"]:
            if ref["path"].endswith("work_state.py"):
                ref["commands"] = [c for c in ref["commands"] if c != "source"]
        errors = self.validate(catalog)
        self.assertTrue(any(
            "work_state.py: shipped subcommands not covered" in e and "'source'" in e
            for e in errors
        ))

    def test_fabricated_cli_command_is_rejected(self):
        catalog = copy.deepcopy(self.catalog)
        commitments = self.find(catalog, "commitments")
        for ref in commitments["cli_refs"]:
            if ref["path"].endswith("work_state.py"):
                ref["commands"] = ref["commands"] + ["does-not-exist"]
        errors = self.validate(catalog)
        self.assertTrue(any("name commands that do not exist" in e and "does-not-exist" in e for e in errors))

    def test_omitted_canvas_action_is_detected(self):
        catalog = copy.deepcopy(self.catalog)
        self.find(catalog, "action-desk-canvas")["canvas_refs"] = [
            {"id": "margo-action-desk", "actions": ["list", "show"]}
        ]
        errors = self.validate(catalog)
        self.assertTrue(any(
            "margo-action-desk': registered actions not covered" in e and "refresh" in e
            for e in errors
        ))

    def test_invalid_guide_anchor_is_rejected(self):
        catalog = copy.deepcopy(self.catalog)
        self.find(catalog, "daily-brief")["guide"]["anchor"] = "this-anchor-does-not-exist"
        errors = self.validate(catalog)
        self.assertTrue(any("guide anchor" in e and "daily-brief" in e for e in errors))

    def test_invalid_guide_path_is_rejected(self):
        catalog = copy.deepcopy(self.catalog)
        self.find(catalog, "daily-brief")["guide"]["path"] = "docs/how-to/does-not-exist.md"
        errors = self.validate(catalog)
        self.assertTrue(any("guide path does not exist" in e for e in errors))

    def test_missing_source_path_is_rejected(self):
        catalog = copy.deepcopy(self.catalog)
        self.find(catalog, "daily-brief")["sources"].append("skills/chief-of-staff/references/does-not-exist.md")
        errors = self.validate(catalog)
        self.assertTrue(any("referenced path does not exist" in e for e in errors))

    def test_planned_status_with_existing_cli_integration_is_rejected(self):
        catalog = copy.deepcopy(self.catalog)
        feature = self.find(catalog, "commitments")
        self.assertTrue(feature["cli_refs"], "fixture must already carry cli_refs")
        feature["availability"] = "planned"
        errors = self.validate(catalog)
        self.assertTrue(any("'planned' must not carry cli_refs" in e for e in errors))

    def test_planned_status_with_existing_canvas_integration_is_rejected(self):
        catalog = copy.deepcopy(self.catalog)
        feature = self.find(catalog, "action-desk-canvas")
        self.assertTrue(feature["canvas_refs"], "fixture must already carry canvas_refs")
        feature["availability"] = "planned"
        errors = self.validate(catalog)
        self.assertTrue(any("'planned' must not carry canvas_refs" in e for e in errors))

    def test_scenario_id_not_in_manifest_is_rejected(self):
        catalog = copy.deepcopy(self.catalog)
        self.find(catalog, "daily-brief")["scenario_ids"] = ["daily-brief", "no-such-scenario"]
        errors = self.validate(catalog)
        self.assertTrue(any("scenario id 'no-such-scenario' not found" in e for e in errors))

    def test_scenario_feature_id_mismatch_is_rejected(self):
        catalog = copy.deepcopy(self.catalog)
        manifest = synthetic_scenario_manifest(fc.EXPECTED_FEATURE_IDS)
        # Corrupt one scenario so its feature_id no longer matches its own id.
        for scenario in manifest["scenarios"]:
            if scenario["id"] == "daily-brief":
                scenario["feature_id"] = "catch-up"
        with tempfile.TemporaryDirectory() as tmp:
            manifest_path = write_json(tmp, "scenarios.json", manifest)
            errors = fc.validate_catalog(catalog, scenario_path=manifest_path)
        self.assertTrue(any("declares feature_id 'catch-up'" in e for e in errors))

    def test_planned_feature_cannot_claim_runtime_scenario_evidence(self):
        catalog = copy.deepcopy(self.catalog)
        feature = self.find(catalog, "daily-brief")
        feature["availability"] = "planned"
        feature["cli_refs"] = []
        feature["canvas_refs"] = []
        feature["automation_refs"] = []
        manifest = synthetic_scenario_manifest(fc.EXPECTED_FEATURE_IDS)
        for scenario in manifest["scenarios"]:
            if scenario["id"] == "daily-brief":
                scenario["coverage_kind"] = "runtime"
        with tempfile.TemporaryDirectory() as tmp:
            manifest_path = write_json(tmp, "scenarios.json", manifest)
            errors = fc.validate_catalog(catalog, scenario_path=manifest_path)
        self.assertTrue(any("cannot have working runtime evidence" in e for e in errors))

    def test_missing_own_id_in_scenario_ids_is_rejected(self):
        catalog = copy.deepcopy(self.catalog)
        self.find(catalog, "daily-brief")["scenario_ids"] = []
        errors = self.validate(catalog)
        self.assertTrue(any("scenario_ids must include the feature's own id 'daily-brief'" in e for e in errors))

    def test_missing_required_field_is_rejected(self):
        catalog = copy.deepcopy(self.catalog)
        del self.find(catalog, "daily-brief")["approval"]
        errors = self.validate(catalog)
        self.assertTrue(any("missing or empty required string field 'approval'" in e for e in errors))

    def test_invalid_availability_value_is_rejected(self):
        catalog = copy.deepcopy(self.catalog)
        self.find(catalog, "daily-brief")["availability"] = "sometimes"
        errors = self.validate(catalog)
        self.assertTrue(any("invalid availability 'sometimes'" in e for e in errors))

    def test_invalid_implementation_kind_is_rejected(self):
        catalog = copy.deepcopy(self.catalog)
        self.find(catalog, "daily-brief")["implementation_kind"] = "magic"
        errors = self.validate(catalog)
        self.assertTrue(any("invalid implementation_kind 'magic'" in e for e in errors))

    def test_schema_version_mismatch_is_rejected(self):
        catalog = copy.deepcopy(self.catalog)
        catalog["schema_version"] = 2
        errors = self.validate(catalog)
        self.assertTrue(any("schema_version must be 1" in e for e in errors))

    def test_scenario_manifest_missing_reports_error_without_crashing(self):
        catalog = copy.deepcopy(self.catalog)
        errors = fc.validate_catalog(catalog, scenario_path=Path("/no/such/scenarios.json"))
        self.assertTrue(any("scenario manifest not found" in e for e in errors))
        # Every other check still ran; this is not the only error class possible, but it must
        # not have crashed before reaching feature-level and coverage checks.
        self.assertIsInstance(errors, list)

    def test_scenario_manifest_invalid_json_reports_error_without_crashing(self):
        catalog = copy.deepcopy(self.catalog)
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "scenarios.json"
            path.write_text("{not valid json", encoding="utf-8")
            errors = fc.validate_catalog(catalog, scenario_path=path)
        self.assertTrue(any("not valid JSON" in e for e in errors))


class GeneratedSectionTests(unittest.TestCase):
    """The --write/--check marker logic must be a faithful, non-self-grading round trip."""

    def test_apply_markers_replaces_only_the_marked_region(self):
        begin, end = "<!-- BEGIN GENERATED: x -->", "<!-- END GENERATED: x -->"
        original = f"before\n{begin}\nold body\n{end}\nafter\n"
        updated = fc.apply_markers(original, (begin, end), "new body")
        self.assertIn("before\n", updated)
        self.assertIn("new body", updated)
        self.assertNotIn("old body", updated)
        self.assertIn("after\n", updated)

    def test_apply_markers_raises_when_markers_absent(self):
        with self.assertRaises(fc.CatalogError):
            fc.apply_markers("no markers here", ("<!-- BEGIN -->", "<!-- END -->"), "body")

    def test_current_section_detects_staleness(self):
        begin, end = "<!-- BEGIN GENERATED: x -->", "<!-- END GENERATED: x -->"
        text = f"{begin}\nstale content\n{end}\n"
        self.assertEqual(fc.current_section(text, (begin, end)), "stale content")
        # A regenerated body that differs from the current section is exactly what --check
        # must flag as stale; this asserts the comparison actually distinguishes them.
        self.assertNotEqual(fc.current_section(text, (begin, end)), "fresh content")

    def test_write_then_check_round_trips_on_real_repository_docs(self):
        # Uses the real generator output against the real files; confirms --write is
        # idempotent (a second --write reports no changes) rather than perpetually rewriting.
        result_first = subprocess.run(
            [sys.executable, str(TOOLS_DIR / "feature_catalog.py"), "--write"],
            cwd=ROOT, capture_output=True, text=True, timeout=60,
        )
        self.assertEqual(result_first.returncode, 0, result_first.stdout + result_first.stderr)
        result_second = subprocess.run(
            [sys.executable, str(TOOLS_DIR / "feature_catalog.py"), "--write"],
            cwd=ROOT, capture_output=True, text=True, timeout=60,
        )
        self.assertEqual(result_second.returncode, 0, result_second.stdout + result_second.stderr)
        self.assertIn("already up to date", result_second.stdout)


class DiscoveryTests(unittest.TestCase):
    """Discovery helpers must reflect the real repository, not a fixed guess."""

    def test_discover_automation_files_excludes_readme(self):
        files = fc.discover_automation_files()
        self.assertIn("automations/morning-brief.md", files)
        self.assertNotIn("automations/README.md", files)
        self.assertEqual(len(files), 6)

    def test_discover_cli_commands_finds_known_subcommands(self):
        errors = []
        groups = fc.discover_cli_commands(errors)
        self.assertEqual(errors, [])
        self.assertIn("source", groups["skills/chief-of-staff/scripts/work_state.py"])
        self.assertIn("init", groups["skills/chief-of-staff/scripts/margo_store.py"])

    def test_discover_canvas_groups_finds_registered_canvases(self):
        errors = []
        groups = fc.discover_canvas_groups(errors)
        self.assertEqual(errors, [])
        self.assertIn("list", groups["margo-action-desk"])
        self.assertIn("search", groups["margo-memory"])

    def test_parse_router_rows_finds_core_routines(self):
        rows = fc.parse_router_rows(ROOT / "skills/chief-of-staff/SKILL.md")
        self.assertIn("Daily Brief", rows)
        self.assertIn("Task progress", rows)
        rows = fc.parse_router_rows(ROOT / "skills/decision-log/SKILL.md")
        self.assertIn("Extract", rows)


if __name__ == "__main__":
    unittest.main()
