from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "skills/chief-of-staff/scripts/task_state.py"
sys.path.insert(0, str(SCRIPT.parent))
from memory_store import MemoryStore
from task_runs import zero


def stamp(seconds=0):
    return (datetime.now(timezone.utc) + timedelta(seconds=seconds)).isoformat()


class TaskCLITests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()
        self.account = "task-cli-fixture@example.com"
        self.env = dict(os.environ, MARGO_ALLOW_UNSAFE_STATE_DIR="1",
                        COPILOT_HOME=str(self.root / "home"),
                        MARGO_ACCOUNT=self.account, MARGO_STATE_DIR=str(self.root / "state"))

    def tearDown(self):
        self.temp.cleanup()

    def call(self, *args, payload=None, script=SCRIPT, success=True):
        result = subprocess.run([sys.executable, "-B", str(script), *args], env=self.env,
                                input=json.dumps(payload) if payload is not None else None,
                                capture_output=True, text=True)
        if success:
            self.assertEqual(result.returncode, 0, result.stderr)
            return json.loads(result.stdout)
        self.assertEqual(result.returncode, 2, result.stdout)
        return json.loads(result.stderr)

    def plan(self):
        return {
            "goal": "Prepare a private synthetic note", "routine": "work-products",
            "request_ref": "conversation:synthetic-cli", "mode": "foreground",
            "environment": {"account": self.account, "host": "synthetic-cli",
                            "capabilities": {"local.prepare": "v1"}, "observed_at": stamp()},
            "window": {"start": stamp(-60), "end": stamp()},
            "limits": dict(zero(), max_steps=2, max_attempts_per_step=2, max_parallel=1,
                           output_chars=4000, deadline_at=stamp(600), lease_seconds=60),
            "steps": [{"key": "prepare", "title": "Prepare note", "kind": "local",
                       "capability": "local.prepare", "depends_on": [], "allow_partial": False,
                       "cost": dict(zero(), output_chars=2000)}],
        }

    def test_read_setup_and_full_local_workflow_across_processes(self):
        self.assertEqual(self.call("list", success=False)["code"], "not_initialized")
        self.assertFalse((self.root / "state").exists())
        identity = self.call("record-id", "note")["id"]
        self.assertFalse((self.root / "state").exists())
        self.assertFalse(self.call("init")["collected"])
        plan = self.plan()
        run = self.call("create", "note", "--input", "-", payload=plan)
        self.assertEqual(run["id"], identity)
        paused = self.call("pause", identity, "--reason", "Review scope")
        self.assertEqual(paused["ready_steps"], [])
        self.call("resume", "--input", "-", payload={
            "run_id": identity, "revision": paused["revision"], "binding": plan["environment"]})
        run = self.call("show", identity)
        claim = self.call("start", "--input", "-", payload={
            "run_id": identity, "step_key": "prepare", "revision": 1,
            "plan_hash": run["plan_hash"], "binding": plan["environment"]})
        done = self.call("finish", "--input", "-", payload={
            "attempt_id": claim["attempt_id"], "token": claim["token"], "outcome": "succeeded",
            "result": {"kind": "local_result", "reference": "test-fixture:note", "summary": "A private fixture note."}})
        self.assertEqual(done["status"], "succeeded")
        history = self.call("history", identity)
        self.assertNotIn(claim["token"], json.dumps(history))
        listed = self.call("list")
        self.assertEqual(listed["runs"][0]["goal"], plan["goal"])
        self.assertNotIn("plan", listed["runs"][0])
        self.assertEqual(self.call("health")["unreconciled_attempts"], 0)

    @unittest.skipIf(os.name == "nt" or not shutil.which("bash"), "POSIX installed-copy engine")
    def test_installed_task_core_preserves_existing_memory_and_customizations(self):
        destination = self.root / "installed"
        install = ["bash", str(ROOT / "install.sh"), "--all", "--yes", "--dest", str(destination)]
        first = subprocess.run(install, cwd=ROOT, env=self.env, capture_output=True, text=True)
        self.assertEqual(first.returncode, 0, first.stderr)
        scripts = destination / "skills/chief-of-staff/scripts"
        self.assertTrue((scripts / "task_runs.py").is_file())
        self.assertTrue((scripts / "task_state.py").is_file())
        preferences = destination / "skills/chief-of-staff/preferences.md"
        preferences.write_text("# Synthetic private preferences\nPreserve this local customization.\n")
        old = dict(os.environ)
        os.environ.update(self.env)
        try:
            memory = MemoryStore(self.account, str(self.root / "state"))
            record = memory.put("preserved", {
                "domain": "user", "kind": "episode", "title": "Synthetic existing context",
                "text": "Preserve this memory.", "authority": "source_observed", "scope": "fixture",
                "source_refs": [{"kind": "tool_result", "ref": "synthetic:existing"}],
            }, status="active")
            memory.close()
            self.call("init", script=scripts / "task_state.py")
            again = subprocess.run(install, cwd=ROOT, env=self.env, capture_output=True, text=True)
            self.assertEqual(again.returncode, 0, again.stderr)
            memory = MemoryStore(self.account, str(self.root / "state"), read_only=True)
            try:
                self.assertEqual(memory.show(record["id"])["text"], record["text"])
                self.assertFalse(memory.policy()["data"]["capture"]["enabled"])
            finally:
                memory.close()
            self.assertIn("Preserve this local customization", preferences.read_text())
            self.assertEqual(self.call("health", script=scripts / "task_state.py")["runs_by_state"], {})
        finally:
            os.environ.clear()
            os.environ.update(old)


if __name__ == "__main__":
    unittest.main()
