from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parents[1] / "skills/chief-of-staff/scripts"
sys.path.insert(0, str(SCRIPTS))

import margo_profile as profile
import margo_store as store
from task_runs import TaskStore
from work_productivity import Productivity


class ProfileTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()
        self.config = self.root / "private-install" / "margo" / "config.json"
        self.workspace = self.root / "OneDrive - Example" / "Daily Work"
        self.workspace.mkdir(parents=True)
        self.env = patch.dict(os.environ, {
            "COPILOT_HOME": str(self.root / "private-install"),
            "MARGO_CONFIG": str(self.config), "MARGO_ACCOUNT": "profile-fixture",
            "MARGO_STATE_DIR": str(self.root / "private-runtime"), "MARGO_ALLOW_UNSAFE_STATE_DIR": "1",
        })
        self.env.start()
        store.initialize_config("profile-fixture")

    def tearDown(self):
        self.env.stop()
        self.temp.cleanup()

    def set(self, **kwargs):
        return profile.configure(None, None, profile.show()["revision"], **kwargs)

    def test_default_name_rename_and_work_root_survive_restart_without_identity_changes(self):
        initial = profile.show()
        self.assertEqual(initial["assistant_name"], "Margo")
        self.assertEqual(initial["work_root"]["status"], "not_configured")
        state_path = store.state_path()
        configured = self.set(assistant_name="Rowan", root=str(self.workspace))
        self.assertEqual(configured["assistant_name"], "Rowan")
        self.assertEqual(configured["work_root"]["path"], str(self.workspace))
        result = subprocess.run([sys.executable, "-B", str(SCRIPTS / "margo_store.py"), "profile-show"],
                                cwd=self.workspace, capture_output=True, text=True, check=True)
        self.assertEqual(json.loads(result.stdout)["assistant_name"], "Rowan")
        self.assertEqual(store.state_path(), state_path)
        self.assertFalse(state_path[1].exists())
        self.set(assistant_name="Morgan")
        self.assertEqual(profile.show()["work_root"]["path"], str(self.workspace))
        self.assertEqual(list(self.workspace.iterdir()), [])
        self.assertEqual(profile.show("different-account")["assistant_name"], "Margo")

    def test_user_edits_and_other_settings_are_preserved_and_conflicts_are_explicit(self):
        before = profile.show()
        data = store.read_json(self.config)
        data["unrelated_setting"] = {"keep": True}
        data["profiles"] = {"profile-fixture": {"assistant_name": "Alex"}}
        self.config.write_text(store.canonical_json(data), encoding="utf-8")
        self.assertEqual(profile.show()["assistant_name"], "Alex")
        with self.assertRaisesRegex(store.StateError, "revision conflict"):
            profile.configure(None, None, before["revision"], assistant_name="Old inferred name")
        self.set(root=str(self.workspace))
        self.assertEqual(store.read_json(self.config)["unrelated_setting"], {"keep": True})
        self.assertEqual(profile.show()["assistant_name"], "Alex")
        self.assertEqual(store.read_json(self.config)["account"], "profile-fixture")
        self.set(clear_root=True)
        self.assertEqual(profile.show()["work_root"]["status"], "not_configured")
        self.assertTrue(self.workspace.exists())

    def test_names_are_bounded_plain_text_never_instructions(self):
        for value in ["", " ", "x" * 61, "A\nB", "A\tB", "A\x00B", "A\u202eB", "A\u200bB",
                      "<script>", "{rename}", "`code`", "   Alex", ".", None]:
            with self.subTest(value=repr(value)), self.assertRaises(store.StateError):
                profile.display_name(value)
        self.assertEqual(profile.display_name("Jean-Luc"), "Jean-Luc")
        self.assertEqual(profile.display_name("Ren\u00e9e"), "Ren\u00e9e")
        data = store.read_json(self.config)
        data["profiles"] = {"profile-fixture": {"assistant_name": "Bad\nName"}}
        self.config.write_text(store.canonical_json(data), encoding="utf-8")
        with self.assertRaises(store.StateError):
            profile.show()

    def test_missing_offline_invalid_and_repository_roots_never_fall_back(self):
        for value in ["relative", str(self.workspace / ".." / "Daily Work"), str(self.root / "absent"),
                      str(Path(self.root.anchor)), str(Path.home())]:
            with self.subTest(value=value), self.assertRaises(store.StateError):
                self.set(root=value)
        with patch.object(profile.os, "access", return_value=False), self.assertRaises(store.StateError):
            self.set(root=str(self.workspace))
        runtime = self.root / "private-runtime"
        runtime.mkdir()
        with self.assertRaisesRegex(store.StateError, "installation/runtime"):
            self.set(root=str(runtime))
        (self.workspace / ".git").mkdir()
        with self.assertRaisesRegex(store.StateError, "repository"):
            self.set(root=str(self.workspace))
        (self.workspace / ".git").rmdir()
        self.set(root=str(self.workspace))
        self.workspace.rmdir()
        self.assertEqual(profile.show()["work_root"]["status"], "unavailable")
        with self.assertRaisesRegex(store.StateError, "must not fall back"):
            profile.output_path("note.md", profile.show()["revision"])
        self.set(assistant_name="Rowan")
        self.assertEqual(profile.show()["work_root"]["status"], "unavailable")

    def test_output_resolution_rejects_traversal_symlinks_existing_files_and_stale_roots(self):
        state = self.set(root=str(self.workspace))
        for value in ["../escape.md", "..\\escape.md", "C:\\escape.md", "/escape.md", "note.md:stream",
                      "NUL.md", "trailing.\\note.md", "missing\\note.md", "note\x00.md"]:
            with self.subTest(value=value), self.assertRaises(store.StateError):
                profile.output_path(value, state["revision"])
        self.assertEqual(profile.output_path("Fictional note.md", state["revision"]), self.workspace / "Fictional note.md")
        self.assertFalse((self.workspace / "Fictional note.md").exists())
        (self.workspace / "existing.md").write_text("Preserve me", encoding="utf-8")
        with self.assertRaisesRegex(store.StateError, "already exists"):
            profile.output_path("existing.md", state["revision"])
        self.set(assistant_name="Rowan")
        with self.assertRaisesRegex(store.StateError, "revision conflict"):
            profile.output_path("note.md", state["revision"])
    def test_symlinked_output_directories_are_rejected(self):
        self.set(root=str(self.workspace))
        link = self.workspace / "redirect"
        try:
            link.symlink_to(self.root, target_is_directory=True)
        except OSError:
            self.skipTest("Creating a symlink requires platform privileges; junction detection is tested separately.")
        with self.assertRaisesRegex(store.StateError, "Symlink"):
            profile.output_path("redirect/escape.md", profile.show()["revision"])

    def test_windows_junction_is_rejected_without_following_it(self):
        class Redirect:
            parents = ()
            def is_symlink(self):
                return False
            def lstat(self):
                class Info:
                    st_reparse_tag = 0xA0000003
                return Info()
        with self.assertRaisesRegex(store.StateError, "junction"):
            profile._no_redirects(Redirect())

    def test_markdown_export_is_explicit_no_overwrite_and_never_relocates_live_state(self):
        setting = self.set(root=str(self.workspace))
        tasks = TaskStore()
        try:
            source = tasks.ledger.source("file", "fixture", "design", "1", {"text": "Fictional design."}, "https://example.com/design")
            artifact = Productivity(tasks.ledger).put("artifact", "note", {
                "artifact_kind": "decision_memo", "title": "Design choice", "markdown": "# Decision\n\nFictional content.\n",
                "audience": "User", "purpose": "Review the tradeoff", "sensitivity": "Synthetic",
                "proposed_next_action": "Review locally", "open_questions": [], "source_refs": [source],
            })
            database = store.state_path()[1]
            result = profile.export_artifact(tasks.ledger, artifact["id"], artifact["revision"], "Design choice.md", setting["revision"])
            self.assertTrue(result["written"])
            self.assertEqual(result["publication"], "not_performed_by_helper")
            self.assertEqual((self.workspace / "Design choice.md").read_text(), artifact["data"]["markdown"])
            self.assertTrue(database.is_file())
            self.assertNotIn(self.workspace, database.parents)
            self.assertEqual(tasks.ledger.show(artifact["id"])["revision"], artifact["revision"])
            with self.assertRaises(store.StateError):
                profile.export_artifact(tasks.ledger, artifact["id"], artifact["revision"], "Design choice.md", setting["revision"])
        finally:
            tasks.close()
        with patch.dict(os.environ, {"MARGO_ALLOW_UNSAFE_STATE_DIR": "0"}):
            with self.assertRaisesRegex(store.StateError, "synchronised"):
                store.state_path(state_root=str(self.workspace))

    def test_profile_commands_work_from_non_repository_directory_with_spaces(self):
        initial = profile.show()
        result = subprocess.run([sys.executable, "-B", str(SCRIPTS / "margo_store.py"), "profile-set",
                                 "--expected-revision", initial["revision"], "--work-root", str(self.workspace)],
                                cwd=self.workspace, capture_output=True, text=True, check=True)
        saved = json.loads(result.stdout)
        path = subprocess.run([sys.executable, "-B", str(SCRIPTS / "margo_store.py"), "workspace-path", "A new note.md",
                               "--expected-revision", saved["revision"]],
                              cwd=self.workspace, capture_output=True, text=True, check=True)
        self.assertEqual(json.loads(path.stdout)["path"], str(self.workspace / "A new note.md"))
        self.assertEqual(list(self.workspace.iterdir()), [])
        settings = subprocess.run([sys.executable, "-B", str(SCRIPTS / "work_state.py"), "profile"],
                                  cwd=self.workspace, capture_output=True, text=True, check=True)
        self.assertEqual(json.loads(settings.stdout)["assistant_name"], "Margo")

    def test_concurrent_settings_updates_cannot_silently_overwrite_each_other(self):
        revision = profile.show()["revision"]
        def update(name):
            try:
                profile.configure(None, None, revision, assistant_name=name)
                return True
            except store.StateError:
                return False
        with ThreadPoolExecutor(max_workers=4) as pool:
            outcomes = list(pool.map(update, ["Alex", "Rowan", "Morgan", "Casey"]))
        self.assertEqual(sum(outcomes), 1)
        self.assertIn(profile.show()["assistant_name"], ["Alex", "Rowan", "Morgan", "Casey"])

    def test_copied_helpers_use_installed_preferences_from_a_non_repo_work_folder(self):
        skill = self.root / "private-install" / "skills" / "chief-of-staff"
        scripts = skill / "scripts"
        shutil.copytree(SCRIPTS, scripts, ignore=shutil.ignore_patterns("__pycache__"))
        (skill / "preferences.md").write_text(
            "- **Time zone & working hours:** UTC; Sun,Mon; 08:00-12:00\n", encoding="utf-8")
        self.set(assistant_name="Rowan", root=str(self.workspace))
        subprocess.run([sys.executable, "-B", str(scripts / "task_state.py"), "init"],
                       cwd=self.workspace, capture_output=True, text=True, check=True)
        result = subprocess.run([sys.executable, "-B", str(scripts / "work_state.py"), "desk"],
                                cwd=self.workspace, capture_output=True, text=True, check=True)
        snapshot = json.loads(result.stdout)
        self.assertEqual(snapshot["profile"]["assistant_name"], "Rowan")
        self.assertEqual(snapshot["profile"]["work_root"]["path"], str(self.workspace))
        self.assertEqual(snapshot["time_preferences"]["value"], "UTC; Sun,Mon; 08:00-12:00")
        self.assertEqual(list(self.workspace.iterdir()), [])
