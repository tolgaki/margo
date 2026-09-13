from concurrent.futures import ThreadPoolExecutor
import hashlib
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
import margo_locations as locations
import margo_store as store
from memory_store import MemoryStore
from memory_search import MemorySearch
from task_runs import TaskStore


class LocationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()
        self.install = self.root / "Installed tools"
        self.private = self.root / "Private area"
        self.state = self.private / "state"
        self.state.mkdir(mode=0o700, parents=True)
        self.private.chmod(0o700)
        self.install.mkdir(mode=0o700)
        self.config = self.private / "config.json"
        self.account = "location-fixture@example.com"
        self.config.write_text(store.canonical_json({"account": self.account,
            "profiles": {self.account: {"assistant_name": "Rowan"}}}), encoding="utf-8")
        self.config.chmod(0o600)
        env = {key: value for key, value in os.environ.items() if not key.startswith("MARGO_") and key != "COPILOT_HOME"}
        env.update(COPILOT_HOME=str(self.install), MARGO_ALLOW_UNSAFE_STATE_DIR="1")
        self.env = patch.dict(os.environ, env, clear=True)
        self.env.start()

    def tearDown(self):
        self.env.stop()
        self.temp.cleanup()

    def bind(self, revision="missing"):
        return locations.bind(str(self.config), str(self.state), self.account, revision)

    def test_no_binding_preserves_legacy_default_and_does_not_discover_other_config(self):
        self.assertEqual(store.configuration_path(), self.install / "margo/config.json")
        # Isolate default selection from POSIX's intentional rejection of /tmp ancestors.
        with patch.object(store, "_safe_directory", side_effect=lambda path, **_kwargs: Path(path)):
            self.assertEqual(store.private_state_root(), self.install / "margo/state")
        self.assertEqual(locations.inspect()["revision"], "missing")
        with self.assertRaises(store.SetupRequired):
            store.resolve_account()
        self.assertFalse((self.install / "margo").exists())

    def test_bound_root_without_margo_environment_preserves_identity_and_creates_no_old_database(self):
        original = self.config.read_bytes()
        result = self.bind()
        self.assertEqual(result["status"], "bound")
        self.assertEqual(store.resolve_account(), self.account)
        self.assertEqual(store.configuration_path(), self.config)
        self.assertEqual(store.private_state_root(), self.state)
        self.assertEqual(store.state_path()[1], self.state / hashlib.sha256(self.account.encode()).hexdigest() / "margo.sqlite3")
        self.assertFalse(any(self.state.iterdir()), "Binding never initializes state")
        with self.assertRaises(store.NotInitialized):
            TaskStore(read_only=True)
        self.assertEqual(original, self.config.read_bytes())
        self.assertFalse((self.install / "margo/state").exists())

    def test_explicit_invocation_then_environment_then_binding_precedence(self):
        self.bind()
        alternate = self.root / "other"
        alternate.mkdir(mode=0o700)
        with patch.dict(os.environ, {"MARGO_CONFIG": str(alternate / "config.json"), "MARGO_STATE_DIR": str(alternate / "state"),
                                    "MARGO_ACCOUNT": "explicit-env-owner"}):
            self.assertEqual(store.configuration_path(), alternate / "config.json")
            self.assertEqual(store.private_state_root(), alternate / "state")
            self.assertEqual(store.configuration_path(str(self.config)), self.config)
            self.assertEqual(store.private_state_root(str(self.state)), self.state)
            self.assertEqual(store.resolve_account(), "explicit-env-owner")
            self.assertEqual(store.resolve_account("invoking-owner"), "invoking-owner")
            self.assertEqual(locations.inspect()["effective"]["state_root"]["source"], "MARGO_STATE_DIR")
        self.assertEqual(store.resolve_account(), self.account)

    def test_missing_unsafe_and_malformed_binding_is_an_error_not_legacy_fallback(self):
        self.bind()
        binding = locations.binding_path()
        for value in [[], {"schema_version": 3}, {"schema_version": 1, "config_path": "relative", "state_root": str(self.state)},
                      {"schema_version": 1, "config_path": str(self.config), "state_root": str(self.private / "missing")}]:
            binding.write_text(store.canonical_json(value), encoding="utf-8")
            with self.subTest(value=value):
                with self.assertRaises(store.LocationError):
                    store.configuration_path()
                self.assertEqual(locations.inspect()["status"], "blocked")
        binding.write_text("{invalid-json", encoding="utf-8")
        revision = locations.inspect()["revision"]
        cleared = locations.clear(revision)
        self.assertEqual(cleared["revision"], "missing")
        self.assertIsNone(cleared["binding"])
        self.assertFalse(binding.exists())
        self.bind()
        self.config.unlink()
        with self.assertRaises(store.LocationError):
            store.resolve_account()
        self.assertFalse((self.install / "margo/state").exists())

    def test_binding_read_is_bounded_and_explicit_overrides_do_not_use_a_broken_binding(self):
        self.bind()
        binding = locations.binding_path()
        binding.write_bytes(b"x" * (locations.LIMIT + 1))
        with self.assertRaisesRegex(store.LocationError, "16 KiB"):
            store.configuration_path()
        self.assertEqual(store.configuration_path(self.config), self.config)
        self.assertEqual(store.private_state_root(self.state), self.state)
        with patch.dict(os.environ, {"MARGO_CONFIG": str(self.config), "MARGO_STATE_DIR": str(self.state)}):
            self.assertEqual(store.resolve_account(), self.account)
            self.assertEqual(store.private_state_root(), self.state)
        binding.unlink()
        with patch.object(store, "_private", side_effect=PermissionError("denied")), self.assertRaises(store.LocationError):
            locations.read_binding()

    def test_bind_requires_owner_match_revision_and_single_winner(self):
        with self.assertRaisesRegex(store.StateError, "confirmed owner"):
            locations.bind(str(self.config), str(self.state), "wrong-owner", "missing")
        def bind_once(_):
            try:
                return self.bind()["status"]
            except store.StateError:
                return "conflict"
        with ThreadPoolExecutor(max_workers=4) as pool:
            outcomes = list(pool.map(bind_once, range(8)))
        self.assertEqual(outcomes.count("bound"), 1)
        with self.assertRaisesRegex(store.StateError, "revision conflict"):
            self.bind()
        revision = locations.inspect()["revision"]
        self.assertEqual(self.bind(revision)["status"], "bound")
        self.assertTrue(self.config.is_file())

    def test_production_guards_reject_synced_repo_links_and_unprivate_paths(self):
        synced = self.root / "OneDrive - Example"
        synced.mkdir(mode=0o700)
        with patch.dict(os.environ, {"MARGO_ALLOW_UNSAFE_STATE_DIR": "0"}):
            with self.assertRaises(store.LocationError):
                locations.bind(str(self.config), str(synced), self.account, "missing")
        (self.state / ".git").mkdir()
        with patch.dict(os.environ, {"MARGO_ALLOW_UNSAFE_STATE_DIR": "0"}), self.assertRaises(store.LocationError):
            self.bind()
        (self.state / ".git").rmdir()
        with patch.object(store, "_private", side_effect=PermissionError("fixture denial")), self.assertRaises(store.LocationError):
            self.bind()
        if os.name != "nt":
            self.config.chmod(0o644)
            with self.assertRaises(store.LocationError):
                self.bind()

    def test_junction_and_symlink_paths_are_rejected(self):
        class Redirect:
            parents = ()
            def is_symlink(self):
                return False
            def lstat(self):
                class Info:
                    st_reparse_tag = 0xA0000003
                return Info()
        with self.assertRaisesRegex(store.StateError, "redirected"):
            store._no_symlinks(Redirect())

    def test_basic_memory_init_and_keyword_read_need_no_model_capture_or_network(self):
        self.bind()
        tasks = TaskStore()
        tasks.close()
        with self.assertRaises(store.NotInitialized):
            MemoryStore(read_only=True)
        with patch("memory_encoder.encode_local", side_effect=AssertionError("must not encode")), \
                patch("socket.socket", side_effect=AssertionError("must not network")):
            memory = MemoryStore()
            MemorySearch(memory)
            self.assertFalse(memory.policy()["data"]["capture"]["enabled"])
            record = memory.put("synthetic-only", {"domain": "user", "kind": "episode",
                "title": "Fictional preparation", "text": "Review the fictional design first.", "scope": "fixture",
                "authority": "source_observed", "source_refs": [{"kind": "tool_result", "ref": "synthetic:source"}]},
                status="active")
            memory.close()
            memory = MemoryStore(read_only=True)
            try:
                result = MemorySearch(memory).search("fictional", mode="lexical", domain="user")
                self.assertEqual(result["results"][0]["memory"]["id"], record["id"])
                self.assertEqual(record["authority"], "source_observed")
                self.assertFalse(memory.policy()["data"]["capture"]["enabled"])
            finally:
                memory.close()

    def test_copied_installed_cli_finds_binding_from_nonrepo_cwd_with_no_environment(self):
        scripts = self.install / "skills/chief-of-staff/scripts"
        shutil.copytree(SCRIPTS, scripts, ignore=shutil.ignore_patterns("__pycache__"))
        (self.install / ".margo-install").write_text("mode=copy\n", encoding="utf-8")
        env = dict(os.environ)
        for key in ["COPILOT_HOME", "MARGO_CONFIG", "MARGO_STATE_DIR", "MARGO_ACCOUNT"]:
            env.pop(key, None)
        def cli(file, *args, success=True):
            result = subprocess.run([sys.executable, "-B", str(scripts / file), *args],
                                    cwd=self.root, env=env, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0 if success else 2, result.stderr)
            return json.loads(result.stdout if success else result.stderr)
        show = cli("margo_store.py", "locations")
        self.assertEqual(show["binding_path"], str(self.install / "margo/locations.json"))
        cli("margo_store.py", "locations-bind", "--expected-revision", "missing",
            "--account", self.account, "--config-path", str(self.config), "--state-dir", str(self.state))
        self.assertEqual(cli("margo_store.py", "profile-show")["assistant_name"], "Rowan")
        cli("task_state.py", "init")
        self.assertEqual(cli("work_state.py", "desk")["account"], self.account)
        self.assertEqual(cli("task_state.py", "list")["account"], self.account)
        self.assertEqual(cli("memory_state.py", "list", success=False)["code"], "not_initialized")
        cli("memory_state.py", "init")
        self.assertEqual(cli("memory_state.py", "list")["memories"], [])
        self.assertEqual(cli("memory_state.py", "search", "fixture", "--mode", "lexical", "--domain", "user")["results"], [])
        health = cli("memory_state.py", "status")
        self.assertEqual(health["basic_memory"]["status"], "available")
        self.assertFalse(health["policy"]["data"]["capture"]["enabled"])
        self.assertEqual(health["semantic_search"]["status"], "unavailable")
        self.assertEqual(health["m365_authentication"], "not_checked")
        self.assertFalse((self.install / "margo/state").exists())
