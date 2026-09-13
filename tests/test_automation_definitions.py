from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parents[1] / "skills/chief-of-staff/scripts"
sys.path.insert(0, str(SCRIPTS))
import automation_definitions as manager
import margo_profile
import margo_store as store


class AutomationDefinitionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()
        self.workspace = self.root / "Daily Work"
        self.workspace.mkdir()
        self.env = patch.dict(os.environ, {"COPILOT_HOME": str(self.root / "installed"),
            "MARGO_CONFIG": str(self.root / "installed/config.json"), "MARGO_ACCOUNT": "automation-fixture",
            "MARGO_STATE_DIR": str(self.root / "private-state"), "MARGO_ALLOW_UNSAFE_STATE_DIR": "1"})
        self.env.start()
        store.initialize_config("automation-fixture")
        margo_profile.configure(None, None, margo_profile.show()["revision"], root=str(self.workspace))

    def tearDown(self):
        self.env.stop()
        self.temp.cleanup()

    def change(self, identity="example-new-scenario"):
        snapshot = manager.list_definitions()
        return {"operation": "create", "id": identity, "expected_revision": "missing",
                "controller_revision": snapshot["controller_revision"], "profile_revision": snapshot["profile_revision"],
                "patch": {"title": "Synthetic scenario", "timezone": "Etc/UTC", "schedule": {"kind": "cron", "expression": "15 9 * * 1-5"},
                          "sections": {heading: "Synthetic instructions for " + heading for heading in manager.HEADINGS}}}

    def save(self, change):
        return manager.commit(change, manager.preview(change)["preview_hash"])

    def test_create_update_list_same_registry_no_native_or_runtime_state(self):
        first = self.save(self.change())
        self.assertFalse(first["metadata"]["enabled"])
        self.assertEqual(first["metadata"]["review_status"], "review_required")
        self.assertFalse(first["native_execution_authorized"])
        listed = manager.list_definitions()
        self.assertEqual(len(listed["scenarios"]), 1)
        self.assertIsNone(listed["native_controller"]["enabled"])
        self.assertIsNone(listed["native_controller"]["workflow_id"])
        self.assertEqual(first["schedules_registered"], 0)
        with self.assertRaisesRegex(store.StateError, "cannot be enabled"):
            self.save({"operation": "enable", "id": first["metadata"]["id"], "expected_revision": first["revision"],
                "controller_revision": first["controller_revision"], "profile_revision": first["profile_revision"], "patch": {}})
        self.assertFalse((self.root / "private-state").exists())
        with self.assertRaises(store.StateError):
            self.save(self.change())

    def test_approved_descriptor_toggle_not_execution_then_content_invalidates_review(self):
        record = self.save(self.change())
        path = self.workspace / "automations/example-new-scenario.md"
        meta = dict(record["metadata"], review_status="approved", review_issues=[], approval_ref="synthetic-review")
        path.write_bytes(manager.serialize(meta, record["body"]))
        record = manager.show(meta["id"])
        change = {"operation": "enable", "id": meta["id"], "expected_revision": record["revision"],
                  "controller_revision": record["controller_revision"], "profile_revision": record["profile_revision"], "patch": {}}
        enabled = self.save(change)
        self.assertTrue(enabled["metadata"]["enabled"])
        self.assertFalse(enabled["native_execution_authorized"], "Descriptor ref is not actual runtime approval")
        updated = self.save(dict(change, operation="update", expected_revision=enabled["revision"],
                                 patch={"schedule": {"kind": "cron", "expression": "30 8 * * 1-5"}}))
        self.assertFalse(updated["metadata"]["enabled"])
        self.assertEqual(updated["metadata"]["review_status"], "review_required")
        self.assertIsNone(updated["metadata"]["approval_ref"])

    def test_metadata_update_preserves_mixed_fenced_source_bytes_and_controller_appendix(self):
        change = self.change()
        record = self.save(change)
        path = self.workspace / "automations/example-new-scenario.md"
        body = record["body"].replace("Synthetic instructions for Steps", "````text\r\n## Fake heading\r\n```inner\npreserved bytes\r\n````")
        path.write_bytes(manager.serialize(record["metadata"], body))
        controller = self.workspace / "AUTOMATIONS.md"
        controller.write_bytes(controller.read_bytes() + b"\r\n## Shared appendix\r\nPreserve this exact source.\n")
        before = controller.read_bytes()
        current = manager.show(change["id"])
        result = self.save({"operation": "update", "id": change["id"], "expected_revision": current["revision"],
            "controller_revision": current["controller_revision"], "profile_revision": current["profile_revision"],
            "patch": {"timezone": "Europe/London"}})
        self.assertEqual(result["body"], body)
        self.assertEqual(controller.read_bytes(), before)
        self.save(self.change("example-second"))
        self.assertTrue(controller.read_bytes().endswith(b"\r\n## Shared appendix\r\nPreserve this exact source.\n"))

    def test_title_edit_changes_only_real_heading_not_quoted_source_heading(self):
        record = self.save(self.change())
        quoted = "```text\n# " + record["metadata"]["title"] + "\n```\n"
        path = self.workspace / "automations/example-new-scenario.md"
        path.write_bytes(manager.serialize(record["metadata"], quoted + record["body"]))
        record = manager.show(record["metadata"]["id"])
        result = self.save({"operation": "update", "id": record["metadata"]["id"], "expected_revision": record["revision"],
            "controller_revision": record["controller_revision"], "profile_revision": record["profile_revision"], "patch": {"title": "New synthetic title"}})
        self.assertTrue(result["body"].startswith(quoted))
        self.assertIn("\n# New synthetic title\n", result["body"])

    def test_concurrent_edits_and_workspace_change_fail_without_overwrite(self):
        record = self.save(self.change())
        def update(title):
            change = {"operation": "update", "id": record["metadata"]["id"], "expected_revision": record["revision"],
                      "controller_revision": record["controller_revision"], "profile_revision": record["profile_revision"], "patch": {"title": title}}
            try:
                self.save(change)
                return True
            except (store.StateError, FileExistsError):
                return False
        with ThreadPoolExecutor(max_workers=2) as pool:
            self.assertEqual(sum(pool.map(update, ["First edit", "Second edit"])), 1)
        stale = self.change("another-scenario")
        margo_profile.configure(None, None, margo_profile.show()["revision"], assistant_name="Rowan")
        with self.assertRaisesRegex(store.StateError, "profile/controller revision conflict"):
            self.save(stale)

    def test_invalid_sources_are_diagnostics_not_silently_ignored(self):
        self.save(self.change())
        path = self.workspace / "automations/example-new-scenario.md"
        path.write_bytes(b"---\n{bad}\n---\n")
        result = manager.list_definitions()
        self.assertEqual(result["status"], "partial")
        self.assertEqual(result["errors"][0]["id"], "example-new-scenario")
        self.assertEqual(path.read_bytes(), b"---\n{bad}\n---\n")
        (path.parent / "unregistered.md").write_text("not executable")
        self.assertEqual(len(manager.list_definitions()["errors"]), 1)

    def test_path_extra_fields_and_payload_limits(self):
        for identity in ["../outside", "has/slash", "Uppercase", "ab", "x" * 81]:
            with self.subTest(identity=identity), self.assertRaises(store.StateError):
                self.save(self.change(identity))
        value = self.change()
        value["patch"]["enabled"] = True
        with self.assertRaises(store.StateError):
            manager.preview(value)
        value = self.change()
        value["patch"]["sections"]["Steps"] = "x" * 60001
        with self.assertRaises(store.StateError):
            manager.preview(value)
        value = self.change()
        value["patch"]["timezone"] = "Mars/Unknown"
        with self.assertRaises(store.StateError):
            manager.preview(value)
        self.assertEqual(list(self.workspace.iterdir()), [])

    def test_schedule_validation_and_draft_nulls(self):
        for expression in ["*/15 9-16 * * 1-5", "0 0 29 2 *", "15 9 * * 1-5"]:
            self.assertEqual(manager.cron(expression), expression)
        for expression in ["60 9 * * *", "0 9 31 2 *", "0 9 * * 7", "@daily", "0  9 * * *",
                           "0 9 * * MON", "1,1 9 * * *", "*/0 9 * * *", "0 9-8 * * *", "0 9 * * * *"]:
            with self.subTest(expression=expression), self.assertRaises(store.StateError):
                manager.cron(expression)
        value = self.change()
        value["patch"].update(timezone=None, schedule=None)
        self.assertEqual(self.save(value)["metadata"]["timezone"], None)

    def test_provenance_schema_and_duplicate_json_are_strict(self):
        record = self.save(self.change())
        value = dict(record["metadata"], account="forbidden")
        with self.assertRaises(store.StateError):
            manager.serialize(value, record["body"])
        raw = manager.serialize(record["metadata"], record["body"]).replace(b'"schema_version": 1,', b'"schema_version": 1,\n"schema_version": 1,')
        with self.assertRaises(store.StateError):
            manager.parse(raw)

    def test_malformed_metadata_is_a_named_diagnostic_not_a_global_crash(self):
        record = self.save(self.change())
        path = self.workspace / "automations/example-new-scenario.md"
        for key, malformed in [("review_issues", [[]]), ("review_status", {}), ("provenance", dict(record["metadata"]["provenance"], kind=[]))]:
            value = dict(record["metadata"], **{key: malformed})
            path.write_bytes(("---\n" + json.dumps(value) + "\n---\n" + record["body"]).encode("utf-8"))
            with self.subTest(key=key):
                result = manager.list_definitions()
                self.assertEqual(result["status"], "partial")
                self.assertEqual(result["errors"][0]["id"], record["metadata"]["id"])
