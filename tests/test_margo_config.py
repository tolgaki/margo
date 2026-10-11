"""Versioned private config and manager binding; synthetic principals and runtime-built GUIDs only."""

import contextlib
import io
import json
import os
import stat
import subprocess
import sys
import tempfile
import unittest
import uuid
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = ROOT / "skills/chief-of-staff/scripts"
sys.path.insert(0, str(SCRIPTS))
import margo_store as store
import margo_doctor as doctor

ACCOUNT = "margo@example.com"
MANAGER_PRINCIPAL = "dana@example.com"
# GUIDs are derived at runtime so no GUID-shaped literal ever enters the tree.
MANAGER = str(uuid.uuid5(uuid.NAMESPACE_DNS, "manager.example.com"))
OTHER_MANAGER = str(uuid.uuid5(uuid.NAMESPACE_DNS, "other-manager.example.com"))
NIL_GUID = str(uuid.UUID(int=0))
PLACEHOLDERS = ("{manager-object-id}", "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx", "not-a-guid",
                "{" + MANAGER + "}", " " + MANAGER, MANAGER.replace("-", ""), "", NIL_GUID)
SECRETS = (ACCOUNT, MANAGER_PRINCIPAL, MANAGER, OTHER_MANAGER)


def v2_document(manager=MANAGER, principal=MANAGER_PRINCIPAL, profile="remote-host", account=ACCOUNT):
    return {"account": account, "config_version": 2,
            "manager": {"object_id": manager, "principal": principal}, "profile": profile}


class ConfigTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()
        self.config = self.root / "private-config" / "config.json"
        env = dict(os.environ)
        env.pop("MARGO_ACCOUNT", None)
        env.update({"MARGO_ALLOW_UNSAFE_STATE_DIR": "1", "MARGO_CONFIG": str(self.config),
                    "COPILOT_HOME": str(self.root / "copilot"),
                    "MARGO_DEPLOYMENT": str(self.root / "missing-deployment.json")})
        self.environment = patch.dict(os.environ, env, clear=True)
        self.environment.start()

    def tearDown(self):
        self.environment.stop()
        self.temp.cleanup()

    def cli(self, *args, success=True):
        result = subprocess.run([sys.executable, "-B", str(SCRIPTS / "margo_store.py"), *args],
                                env=dict(os.environ), capture_output=True, text=True, check=False)
        for secret in SECRETS:
            self.assertNotIn(secret, result.stdout)
            self.assertNotIn(secret, result.stderr)
        if success:
            self.assertEqual(result.returncode, 0, result.stderr)
            return json.loads(result.stdout)
        self.assertEqual(result.returncode, 2, result.stdout)
        self.assertTrue(result.stderr.startswith("ERROR: "), result.stderr)
        return result.stderr[len("ERROR: "):].strip()

    def write_raw(self, document):
        self.config.parent.mkdir(mode=0o700, exist_ok=True)
        self.config.write_text(store.canonical_json(document) + "\n", encoding="utf-8")
        self.config.chmod(0o600)
        return self.config.read_bytes()

    def assert_only_config(self):
        self.assertEqual(list(self.config.parent.iterdir()), [self.config])

    def doctor(self, *args, strict=False, deployment=None):
        env = dict(os.environ)
        if deployment is not None:
            env["MARGO_DEPLOYMENT"] = str(deployment)
        argv = ["--state-dir", str(self.root / "state"), "--install-root", str(self.root / "installation"),
                "--preferences", str(self.root / "missing"), "--decision-config", str(self.root / "missing"),
                *args] + (["--strict"] if strict else [])
        output = io.StringIO()
        with patch.dict(os.environ, env, clear=True), contextlib.redirect_stdout(output):
            code = doctor.main(argv)
        text = output.getvalue()
        for secret in SECRETS:
            self.assertNotIn(secret, text)
        self.assertFalse((self.root / "state").exists())
        self.assertFalse((self.root / "installation").exists())
        return code, json.loads(text)

    def test_init_with_manager_creates_private_v2_config_and_never_echoes_ids(self):
        result = self.cli("init", "--account", ACCOUNT, "--manager", MANAGER.upper(),
                          "--manager-principal", MANAGER_PRINCIPAL, "--profile", "remote-host")
        self.assertEqual({key: result[key] for key in ("status", "created", "account_configured",
                                                      "manager_configured", "config_version", "profile")},
                         {"status": "configured", "created": True, "account_configured": True,
                          "manager_configured": True, "config_version": 2, "profile": "remote-host"})
        self.assertEqual(store.read_json(self.config), v2_document())
        if os.name != "nt":
            self.assertEqual(stat.S_IMODE(self.config.stat().st_mode), 0o600)
            self.assertEqual(stat.S_IMODE(self.config.parent.stat().st_mode), 0o700)
        self.assert_only_config()
        original = self.config.read_bytes()
        replay = self.cli("init", "--account", ACCOUNT, "--manager", MANAGER,
                          "--manager-principal", MANAGER_PRINCIPAL, "--profile", "remote-host")
        self.assertFalse(replay["created"])
        self.assertTrue(replay["manager_configured"])
        # A plain init on a bound config is a no-op that still reports the binding.
        plain = store.initialize_config(ACCOUNT, self.config)
        self.assertEqual((plain["created"], plain["manager_configured"], plain["config_version"]),
                         (False, True, 2))
        self.assertEqual(self.config.read_bytes(), original)
        self.assertEqual(store.resolve_manager(), {"object_id": MANAGER, "principal": MANAGER_PRINCIPAL})
        self.assertEqual(store.resolve_profile(), "remote-host")
        self.assertEqual(store.require_manager()["object_id"], MANAGER)
        self.assertEqual(store.resolve_account(), ACCOUNT)
        with patch.dict(os.environ, {"MARGO_ACCOUNT": "env-owner@example.com"}):
            self.assertEqual(store.resolve_account(), "env-owner@example.com")
            self.assertEqual(store.resolve_manager()["object_id"], MANAGER)

    def test_init_defaults_the_profile_and_refuses_profile_without_manager(self):
        result = store.initialize_config(ACCOUNT, self.config, manager=MANAGER)
        self.assertEqual((result["profile"], result["manager_configured"]), ("remote-host", True))
        self.assertEqual(store.read_json(self.config), v2_document(principal=None))
        other = self.root / "other" / "config.json"
        with self.assertRaises(store.StateError):
            store.initialize_config(ACCOUNT, other, profile="remote-host")
        with self.assertRaises(store.StateError):
            store.initialize_config(ACCOUNT, other, manager_principal=MANAGER_PRINCIPAL)
        self.assertFalse(other.exists())
        self.assertIn("requires --manager", self.cli("init", "--account", ACCOUNT, "--config", str(other),
                                                     "--profile", "local", success=False)
                      .replace("requires a manager object id", "requires --manager"))
        self.assertFalse(other.exists())
        local = store.initialize_config(ACCOUNT, other, manager=MANAGER, profile="local")
        self.assertEqual(local["profile"], "local")
        self.assertEqual(store.resolve_profile(other), "local")

    def test_init_refuses_a_different_manager_or_account_and_leaves_the_file_intact(self):
        store.initialize_config(ACCOUNT, self.config, manager=MANAGER, manager_principal=MANAGER_PRINCIPAL)
        original = self.config.read_bytes()
        cases = (
            (dict(account=ACCOUNT, manager=OTHER_MANAGER),
             "existing config has a different manager; refusing to replace it"),
            (dict(account="someone-else@example.com", manager=MANAGER),
             "existing config has a different/missing account; refusing to replace it"),
            (dict(account="someone-else@example.com"),
             "existing config has a different/missing account; refusing to replace it"),
            (dict(account=ACCOUNT, manager=MANAGER, manager_principal="impostor@example.com"),
             "existing config has a different manager binding; refusing to replace it"),
            (dict(account=ACCOUNT, manager=MANAGER, profile="local"),
             "existing config has a different manager binding; refusing to replace it"),
        )
        for arguments, message in cases:
            with self.subTest(**arguments):
                with self.assertRaises(store.StateError) as caught:
                    store.initialize_config(config_path=self.config, **arguments)
                self.assertEqual(str(caught.exception), message)
                self.assertEqual(self.config.read_bytes(), original)
        self.assertEqual(self.cli("init", "--account", ACCOUNT, "--manager", OTHER_MANAGER, success=False),
                         "existing config has a different manager; refusing to replace it")
        self.assertEqual(self.config.read_bytes(), original)
        self.assert_only_config()

    def test_manager_object_id_validation_rejects_placeholders_and_the_nil_guid(self):
        self.assertEqual(store.validate_manager_object_id(MANAGER.upper()), MANAGER)
        for value in PLACEHOLDERS + (None, 5, [MANAGER], uuid.UUID(MANAGER)):
            with self.subTest(value=value):
                with self.assertRaises(store.StateError) as caught:
                    store.validate_manager_object_id(value)
                self.assertEqual(str(caught.exception), "manager must be an Entra object ID (GUID)")
                if value is not None:  # manager=None is a plain version-1 init, not a binding attempt
                    with self.assertRaises(store.StateError):
                        store.initialize_config(ACCOUNT, self.config, manager=value)
        self.assertFalse(self.config.exists())
        self.assertEqual(self.cli("init", "--account", ACCOUNT, "--manager", NIL_GUID, success=False),
                         "manager must be an Entra object ID (GUID)")
        self.assertFalse(self.config.exists())
        with self.assertRaises(store.StateError):
            store.initialize_config(ACCOUNT, self.config, manager=MANAGER, manager_principal="{manager}")
        self.assertFalse(self.config.exists())

    def test_v1_config_is_untouched_by_plain_init_and_still_resolves(self):
        first = store.initialize_config(ACCOUNT, self.config)
        self.assertEqual((first["created"], first["manager_configured"], first["config_version"], first["profile"]),
                         (True, False, 1, "local"))
        self.assertEqual(store.read_json(self.config), {"account": ACCOUNT})
        original = self.config.read_bytes()
        replay = self.cli("init", "--account", ACCOUNT)
        self.assertEqual((replay["created"], replay["manager_configured"], replay["config_version"]),
                         (False, False, 1))
        self.assertEqual(self.config.read_bytes(), original)
        self.assertEqual(store.resolve_account(), ACCOUNT)
        self.assertEqual(store.load_config(), {"account": ACCOUNT})
        self.assertIsNone(store.resolve_manager())
        self.assertEqual(store.resolve_profile(), "local")
        with self.assertRaises(store.ManagerRequired) as caught:
            store.require_manager()
        self.assertIn("migrate-config", str(caught.exception))
        self.assertTrue(issubclass(store.ManagerRequired, store.StateError))
        # Binding a manager on a version-1 file is an explicit migration, never a side effect of init.
        message = "existing config is version 1; run margo_store.py migrate-config explicitly"
        with self.assertRaises(store.StateError) as caught:
            store.initialize_config(ACCOUNT, self.config, manager=MANAGER)
        self.assertEqual(str(caught.exception), message)
        self.assertEqual(self.cli("init", "--account", ACCOUNT, "--manager", MANAGER, success=False), message)
        with self.assertRaises(store.StateError) as caught:
            store.rebind_manager(ACCOUNT, MANAGER, OTHER_MANAGER, self.config)
        self.assertEqual(str(caught.exception), message)
        self.assertEqual(self.config.read_bytes(), original)
        self.assert_only_config()

    def test_migrate_config_upgrades_v1_once_and_is_replay_safe(self):
        with self.assertRaises(store.StateError):
            store.migrate_config(ACCOUNT, MANAGER, self.config)
        self.assertFalse(self.config.exists())
        store.initialize_config(ACCOUNT, self.config)
        with self.assertRaises(store.StateError):
            store.migrate_config("someone-else@example.com", MANAGER, self.config)
        with self.assertRaises(store.StateError):
            store.migrate_config(ACCOUNT, "{manager-object-id}", self.config)
        with self.assertRaises(store.StateError):
            store.migrate_config(ACCOUNT, MANAGER, self.config, profile="cloud")
        self.assertEqual(store.read_json(self.config), {"account": ACCOUNT})
        result = self.cli("migrate-config", "--account", ACCOUNT, "--manager", MANAGER,
                          "--manager-principal", MANAGER_PRINCIPAL)
        self.assertEqual({key: result[key] for key in ("status", "migrated", "from_version", "to_version",
                                                      "manager_configured", "config_version", "profile")},
                         {"status": "migrated", "migrated": True, "from_version": 1, "to_version": 2,
                          "manager_configured": True, "config_version": 2, "profile": "remote-host"})
        self.assertEqual(store.read_json(self.config), v2_document())
        if os.name != "nt":
            self.assertEqual(stat.S_IMODE(self.config.stat().st_mode), 0o600)
        self.assert_only_config()
        migrated = self.config.read_bytes()
        replay = self.cli("migrate-config", "--account", ACCOUNT, "--manager", MANAGER.upper(),
                          "--manager-principal", MANAGER_PRINCIPAL)
        self.assertEqual((replay["status"], replay["migrated"], replay["from_version"]), ("configured", False, 2))
        self.assertEqual(self.config.read_bytes(), migrated)
        self.assertEqual(self.cli("migrate-config", "--account", ACCOUNT, "--manager", OTHER_MANAGER,
                                  "--manager-principal", MANAGER_PRINCIPAL, success=False),
                         "existing config has a different manager; refusing to replace it")
        self.assertEqual(self.cli("migrate-config", "--account", ACCOUNT, "--manager", MANAGER, success=False),
                         "existing config has a different manager binding; refusing to replace it")
        self.assertEqual(self.config.read_bytes(), migrated)
        self.assertEqual(store.resolve_account(), ACCOUNT)
        self.assertEqual(store.require_manager(), {"object_id": MANAGER, "principal": MANAGER_PRINCIPAL})

    def test_interrupted_migration_leaves_the_original_intact(self):
        store.initialize_config(ACCOUNT, self.config)
        original = self.config.read_bytes()
        with patch.object(store.os, "replace", side_effect=OSError("simulated interruption")):
            with self.assertRaises(store.StateError) as caught:
                store.migrate_config(ACCOUNT, MANAGER, self.config)
        self.assertEqual(str(caught.exception), "configuration unavailable; no existing config replaced")
        self.assertEqual(self.config.read_bytes(), original)
        self.assert_only_config()
        self.assertIsNone(store.resolve_manager())
        # A concurrent edit between the read and the swap is kept, not overwritten by a stale decision.
        edited = store.canonical_json({"account": ACCOUNT}).encode("utf-8") + b"\n\n"
        real_fsync = os.fsync

        def edit_then_fsync(descriptor):
            if self.config.read_bytes() == original:
                self.config.write_bytes(edited)
            return real_fsync(descriptor)

        with patch.object(store.os, "fsync", side_effect=edit_then_fsync):
            with self.assertRaises(store.StateError) as caught:
                store.migrate_config(ACCOUNT, MANAGER, self.config)
        self.assertEqual(str(caught.exception), "config changed during the rewrite; no replacement made")
        self.assertEqual(self.config.read_bytes(), edited)
        self.assert_only_config()
        store.migrate_config(ACCOUNT, MANAGER, self.config)
        self.assertEqual(store.resolve_manager()["object_id"], MANAGER)

    def test_rebind_manager_requires_the_current_manager(self):
        with self.assertRaises(store.StateError):
            store.rebind_manager(ACCOUNT, MANAGER, OTHER_MANAGER, self.config)
        store.initialize_config(ACCOUNT, self.config, manager=MANAGER, manager_principal=MANAGER_PRINCIPAL)
        original = self.config.read_bytes()
        refused = "current manager does not match the bound manager; refusing to rebind"
        with self.assertRaises(store.StateError) as caught:
            store.rebind_manager(ACCOUNT, OTHER_MANAGER, OTHER_MANAGER, self.config)
        self.assertEqual(str(caught.exception), refused)
        with self.assertRaises(store.StateError):
            store.rebind_manager("someone-else@example.com", MANAGER, OTHER_MANAGER, self.config)
        with self.assertRaises(store.StateError):
            store.rebind_manager(ACCOUNT, MANAGER, "{manager-object-id}", self.config)
        self.assertEqual(self.cli("rebind-manager", "--account", ACCOUNT, "--current-manager", OTHER_MANAGER,
                                  "--manager", OTHER_MANAGER, success=False), refused)
        self.assertEqual(self.config.read_bytes(), original)
        same = store.rebind_manager(ACCOUNT, MANAGER, MANAGER, self.config)
        self.assertEqual((same["status"], same["rebound"]), ("configured", False))
        self.assertEqual(self.config.read_bytes(), original)
        result = self.cli("rebind-manager", "--account", ACCOUNT, "--current-manager", MANAGER.upper(),
                          "--manager", OTHER_MANAGER)
        self.assertEqual((result["status"], result["rebound"], result["manager_configured"], result["profile"]),
                         ("rebound", True, True, "remote-host"))
        # The old manager's principal never travels to the new object ID.
        self.assertEqual(store.read_json(self.config), v2_document(manager=OTHER_MANAGER, principal=None))
        self.assert_only_config()
        with self.assertRaises(store.StateError):
            store.rebind_manager(ACCOUNT, MANAGER, MANAGER, self.config)
        again = store.rebind_manager(ACCOUNT, OTHER_MANAGER, OTHER_MANAGER, self.config,
                                     new_principal="rafa@example.com", profile="local")
        self.assertEqual((again["rebound"], again["profile"]), (True, "local"))
        self.assertEqual(store.resolve_manager(), {"object_id": OTHER_MANAGER, "principal": "rafa@example.com"})
        self.assertEqual(store.resolve_profile(), "local")
        with patch.object(store.os, "replace", side_effect=OSError("simulated interruption")):
            before = self.config.read_bytes()
            with self.assertRaises(store.StateError):
                store.rebind_manager(ACCOUNT, OTHER_MANAGER, MANAGER, self.config)
        self.assertEqual(self.config.read_bytes(), before)
        self.assert_only_config()

    def test_future_or_unknown_config_shapes_are_rejected_without_rewriting(self):
        mismatch = "config version mismatch; explicit migration required"
        for document in (dict(v2_document(), config_version=3),
                         {"account": ACCOUNT, "config_version": 1},
                         {"account": ACCOUNT, "manager": {"object_id": MANAGER, "principal": None}},
                         dict(v2_document(), extra=True),
                         dict(v2_document(), config_version=True),
                         dict(v2_document(), config_version="2")):
            with self.subTest(document=document):
                original = self.write_raw(document)
                with self.assertRaises(store.StateError) as caught:
                    store.load_config()
                self.assertEqual(str(caught.exception), mismatch)
                self.assertIsInstance(caught.exception, store.ConfigMismatch)
                with self.assertRaises(store.StateError):
                    store.resolve_account()
                with self.assertRaises(store.StateError):
                    store.resolve_manager()
                with self.assertRaises(store.StateError):
                    store.initialize_config(ACCOUNT, self.config)
                with self.assertRaises(store.StateError):
                    store.migrate_config(ACCOUNT, MANAGER, self.config)
                with self.assertRaises(store.StateError):
                    store.rebind_manager(ACCOUNT, MANAGER, OTHER_MANAGER, self.config)
                self.assertEqual(self.config.read_bytes(), original)
                self.assert_only_config()
        invalid = "installation config has an invalid manager binding"
        for document in (v2_document(profile="cloud"),
                         v2_document(manager="{manager-object-id}"),
                         v2_document(manager=NIL_GUID),
                         dict(v2_document(), manager={"object_id": MANAGER}),
                         dict(v2_document(), manager=MANAGER),
                         v2_document(principal="{principal}")):
            with self.subTest(document=document):
                original = self.write_raw(document)
                with self.assertRaises(store.StateError) as caught:
                    store.load_config()
                self.assertNotIsInstance(caught.exception, store.ConfigMismatch)
                self.assertIn(str(caught.exception), (invalid, "manager must be an Entra object ID (GUID)",
                                                      "manager principal must be an explicit, non-placeholder "
                                                      "principal without whitespace"))
                with self.assertRaises(store.StateError):
                    store.resolve_account()
                self.assertEqual(self.config.read_bytes(), original)
        # The account alone is still validated by resolve_account, as before.
        self.write_raw({"account": None})
        with self.assertRaises(store.SetupRequired):
            store.resolve_account()
        self.write_raw({"account": "{placeholder}"})
        with self.assertRaises(store.StateError):
            store.resolve_account()
        self.config.write_text("{broken", encoding="utf-8")
        with self.assertRaises(store.StateError):
            store.load_config()
        self.assertEqual(self.config.read_text(), "{broken")

    def test_doctor_reports_binding_states_without_creating_state(self):
        code, report = self.doctor("--account", ACCOUNT)
        self.assertEqual(code, 0)
        self.assertEqual(report["binding"], {"status": "unbound", "config_version": None, "manager_configured": False,
                                             "profile": "local", "remote_harness_ready": False,
                                             "action": report["binding"]["action"]})
        self.assertIn("init", report["binding"]["action"])
        self.assertEqual(report["status"], "attention-needed")
        store.initialize_config(ACCOUNT, self.config)
        code, report = self.doctor()
        self.assertEqual(code, 0)
        self.assertEqual((report["binding"]["status"], report["binding"]["config_version"],
                          report["binding"]["manager_configured"], report["binding"]["remote_harness_ready"]),
                         ("unbound", 1, False, False))
        self.assertTrue(report["state"]["account_configured"])
        deployment = self.root / "deployment.json"
        deployment.write_text("{}", encoding="utf-8")
        code, report = self.doctor(deployment=deployment)
        self.assertEqual(code, 0)
        self.assertEqual((report["binding"]["status"], report["binding"]["config_version"]), ("migration-required", 1))
        self.assertIn("migrate-config", report["binding"]["action"])
        self.assertEqual(report["status"], "attention-needed")
        self.assertEqual(self.doctor(deployment=deployment, strict=True)[0], 1)
        store.migrate_config(ACCOUNT, MANAGER, self.config, manager_principal=MANAGER_PRINCIPAL)
        for anchor in (None, deployment):
            code, report = self.doctor(deployment=anchor)
            self.assertEqual(code, 0)
            self.assertEqual(report["binding"], {"status": "bound", "config_version": 2, "manager_configured": True,
                                                 "profile": "remote-host", "remote_harness_ready": True,
                                                 "action": None})
        store.rebind_manager(ACCOUNT, MANAGER, MANAGER, self.config, profile="local")
        code, report = self.doctor()
        self.assertEqual((report["binding"]["status"], report["binding"]["profile"],
                          report["binding"]["remote_harness_ready"]), ("bound", "local", False))
        self.assertIn("remote-host", report["binding"]["action"])
        self.write_raw(dict(v2_document(), config_version=3))
        code, report = self.doctor("--account", ACCOUNT)
        self.assertEqual(code, 0)
        self.assertEqual((report["binding"]["status"], report["binding"]["config_version"],
                          report["binding"]["manager_configured"]), ("migration-required", 3, False))
        self.assertEqual(report["status"], "attention-needed")
        self.assertEqual(self.doctor("--account", ACCOUNT, strict=True)[0], 1)
        self.assertEqual(store.read_json(self.config)["config_version"], 3)
        # A config this version cannot read is reported, never repaired; corrupt JSON is still exit 2.
        self.config.write_text("{broken", encoding="utf-8")
        code, report = self.doctor("--account", ACCOUNT)
        self.assertEqual((code, report["status"]), (2, "error"))
        self.assertEqual(self.config.read_text(), "{broken")
        self.config.unlink()
        code, report = self.doctor()
        self.assertEqual(code, 0)
        self.assertEqual((report["status"], report["binding"]["status"], report["binding"]["remote_harness_ready"]),
                         ("setup-needed", "setup-needed", False))
        self.assertFalse(report["state"]["account_configured"])

    def test_doctor_strict_exit_codes_treat_unbound_as_healthy_and_migration_as_attention(self):
        with patch.object(doctor, "state_health", return_value={
                "status": "ok", "account_configured": True, "all_clear": True}), \
                patch.object(doctor, "memory_health", return_value={"status": "not-initialized"}), \
                patch.object(doctor, "task_health", return_value={"status": "not-initialized"}), \
                patch.object(doctor, "inspect_snapshot", return_value={"status": "healthy"}), \
                patch.object(doctor, "inspect_installation_manifest", return_value={"status": "healthy"}), \
                patch.object(doctor, "configuration_fields", return_value={"status": "complete"}):
            code, report = self.doctor("--account", ACCOUNT, strict=True)
            self.assertEqual((code, report["status"], report["binding"]["status"]), (0, "healthy", "unbound"))
            store.initialize_config(ACCOUNT, self.config)
            code, report = self.doctor("--account", ACCOUNT, strict=True)
            self.assertEqual((code, report["status"], report["binding"]["status"]), (0, "healthy", "unbound"))
            deployment = self.root / "deployment.json"
            deployment.write_text("{}", encoding="utf-8")
            code, report = self.doctor("--account", ACCOUNT, strict=True, deployment=deployment)
            self.assertEqual((code, report["status"], report["binding"]["status"]),
                             (1, "attention-needed", "migration-required"))
            store.migrate_config(ACCOUNT, MANAGER, self.config)
            code, report = self.doctor("--account", ACCOUNT, strict=True, deployment=deployment)
            self.assertEqual((code, report["status"], report["binding"]["status"]), (0, "healthy", "bound"))
            self.write_raw(dict(v2_document(), config_version=3))
            code, report = self.doctor("--account", ACCOUNT, strict=True)
            self.assertEqual((code, report["status"], report["binding"]["status"]),
                             (1, "attention-needed", "migration-required"))
            self.assertEqual(self.doctor("--account", ACCOUNT)[0], 0)


if __name__ == "__main__":
    unittest.main()
