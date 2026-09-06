import json
import hashlib
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "skills/chief-of-staff/scripts/memory_state.py"


class MemoryCLITests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name).resolve()
        self.environment = dict(os.environ, MARGO_ALLOW_UNSAFE_STATE_DIR="1",
                                MARGO_ACCOUNT="synthetic-cli-account", MARGO_STATE_DIR=str(self.root / "state"),
                                COPILOT_HOME=str(self.root / "copilot"))

    def tearDown(self):
        self.temporary.cleanup()

    def run_cli(self, *args, payload=None, script=SCRIPT, success=True):
        result = subprocess.run([sys.executable, "-B", str(script)] + list(args),
                                input=json.dumps(payload) if payload is not None else None,
                                capture_output=True, text=True, env=self.environment)
        if success:
            self.assertEqual(result.returncode, 0, result.stderr)
            return json.loads(result.stdout)
        self.assertEqual(result.returncode, 2, result.stdout)
        return json.loads(result.stderr)

    def test_fresh_session_inspection_and_scope_denial(self):
        result = self.run_cli("init")
        self.assertEqual(result["schema_version"], "2")
        self.assertFalse(result["collected"])
        policy = self.run_cli("policy")
        self.assertFalse(policy["data"]["capture"]["enabled"])
        data = {"domain": "user", "kind": "episode", "title": "Synthetic CLI memory",
                "text": "A private fictional observation.", "authority": "source_observed", "scope": "personal",
                "source_refs": [{"kind": "tool_result", "ref": "synthetic:cli"}]}
        self.run_cli("capture", "blocked", "--input", "-", payload=data, success=False)
        row = self.run_cli("put", "explicit", "--input", "-", payload={"data": data, "status": "active"})
        inspect = self.run_cli("inspect", row["id"])
        self.assertEqual(inspect["memory"]["text"], data["text"])
        self.assertEqual(len(inspect["history"]), 1)
        self.assertEqual(inspect["forget_preview"]["affected"][0]["id"], row["id"])
        packet = self.run_cli("context", "--mode", "lexical", "--input", "-",
                              payload={"query": "fictional observation"})
        self.assertEqual(packet["status"], "ready")
        self.assertIn(row["id"], [entry["id"] for entry in packet["entries"]])
        graph = self.run_cli("graph", row["id"], "--depth", "1", "--limit", "2")
        self.assertIn("nodes", graph)
        self.assertLessEqual(len(graph["nodes"]), 2)
        self.assertIn("gaps", self.run_cli("explain", row["id"]))

    def test_learning_commands_preserve_evidence_and_review_boundaries(self):
        manifest = self.root / "tools.json"
        manifest.write_text(json.dumps({
            "environment": {"host": "synthetic-cli", "account": self.environment["MARGO_ACCOUNT"]},
            "observed_at": "2026-08-01T00:00:00Z", "complete": True,
            "tools": [{"name": "fixture-query", "version": "v1", "exposed": True,
                       "input_schema": {"type": "object", "required": ["query"],
                                        "properties": {"query": {"type": "string"}}}}],
        }))
        inventory = self.run_cli("capabilities", "--tools", str(manifest))
        cap = self.run_cli("show", inventory["memory_ids"][0])
        fixture = self.root / "exercise.json"
        fixture.write_text(json.dumps({"synthetic": True, "capability_id": cap["id"],
                                       "environment": cap["metadata"]["environment_requirements"],
                                       "input": {"query": "Synthetic query"}}))
        observation = {"kind": "synthetic_observation",
                       "fixture": {"path": str(fixture), "sha256": hashlib.sha256(fixture.read_bytes()).hexdigest()}}
        observation_file = self.root / "observation.json"
        observation_file.write_text(json.dumps(observation))
        validated = self.run_cli("validate-capability", cap["id"], "--revision", "1",
                                 "--evidence", str(observation_file))
        self.assertFalse(validated["metadata"]["last_validation"]["runtime_validated"])
        lesson = {"title": "Prepare a valid query", "scope": "synthetic query", "trigger": "Before a query",
                  "goal": "Avoid invalid input", "preconditions": ["Use the scoped schema"],
                  "procedure": ["Provide a string query"], "risk": "read_only",
                  "capability_refs": [{"id": cap["id"], "revision": validated["revision"]}],
                  "evidence": [{"capability_id": cap["id"], "observation": observation}],
                  "counterexamples": []}
        candidate = self.run_cli("propose-lesson", "query-shape", "--input", "-", payload=lesson)
        self.assertEqual(candidate["status"], "candidate")
        approval = self.root / "decision.json"
        approval.write_text(json.dumps({
            "kind": "human_confirmation", "actor": "dana@example.com",
            "subject_id": candidate["id"], "revision": candidate["revision"], "decision": "confirm",
            "statement": "Confirm this synthetic input-shape lesson, not runtime competence.",
            "evidence_ref": "conversation:synthetic-cli", "decided_at": datetime.now(timezone.utc).isoformat(),
        }))
        active = self.run_cli("activate-lesson", candidate["id"], "--revision", str(candidate["revision"]),
                              "--evidence", str(approval))
        self.assertEqual(active["status"], "active")
        page = self.run_cli("consolidate", "--limit", "1", "--scan-limit", "10")
        self.assertEqual(page["account"], self.environment["MARGO_ACCOUNT"])

    @unittest.skipIf(os.name == "nt" or not shutil.which("bash"), "POSIX installed-copy path")
    def test_installed_copy_contains_memory_modules_and_cli(self):
        destination = self.root / "installed"
        result = subprocess.run(["bash", str(ROOT / "install.sh"), "--all", "--yes", "--dest", str(destination)],
                                cwd=ROOT, env=self.environment, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        scripts = destination / "skills/chief-of-staff/scripts"
        for name in ("memory_state.py", "memory_store.py", "memory_search.py", "memory_governance.py",
                     "memory_learning.py", "memory_consolidation.py", "memory_context.py"):
            self.assertTrue((scripts / name).is_file(), name)
        result = self.run_cli("init", script=scripts / "memory_state.py")
        self.assertFalse(result["collected"])
        self.assertFalse(self.run_cli("policy", script=scripts / "memory_state.py")["configured"])


if __name__ == "__main__":
    unittest.main()
