import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


REPO = Path(__file__).resolve().parents[1]


class AutomationContractTests(unittest.TestCase):
    def commands(self):
        result = []
        if shutil.which("bash") and sys.platform != "win32":
            result.append([shutil.which("bash"), str(REPO / "tools/margo-scheduled.sh"),
                           "brief", "--show-prompt"])
        if shutil.which("pwsh"):
            result.append([shutil.which("pwsh"), "-NoProfile", "-NonInteractive", "-File",
                           str(REPO / "tools/margo-scheduled.ps1"), "brief", "-ShowPrompt"])
        return result

    def test_prompts_match_and_carry_state_contract(self):
        outputs = []
        for command in self.commands():
            result = subprocess.run(command, cwd=REPO, capture_output=True, text=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("state-operations.md", result.stdout)
            self.assertIn("publication receipt", result.stdout)
            outputs.append(result.stdout.strip())
        if len(outputs) == 2:
            self.assertEqual(outputs[0], outputs[1])

    def test_missing_mode_or_routine_and_invalid_tier_fail_closed(self):
        prompt = (REPO / "automations/morning-brief.md").read_text()
        variants = [
            prompt.replace("mode: autopilot\n", ""),
            prompt.replace("routine: daily-brief.md (full)\n", ""),
            prompt.replace("mode: autopilot", "mode: plan"),
            prompt.replace("tier: anchor", "tier: unsupported"),
        ]
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            env = dict(os.environ, MARGO_AUTOMATIONS=directory)
            for variant in variants:
                (root / "morning-brief.md").write_text(variant, encoding="utf-8")
                for command in self.commands():
                    result = subprocess.run(command, cwd=REPO, env=env, text=True,
                                            capture_output=True, timeout=30)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertNotIn("Load the `chief-of-staff`", result.stdout)


if __name__ == "__main__":
    unittest.main()
