import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import unittest


REPO = Path(__file__).resolve().parents[1]


class AutomationContractTests(unittest.TestCase):
    def commands(self, verb="brief", print_command=False):
        result = []
        if shutil.which("bash") and sys.platform != "win32":
            result.append([shutil.which("bash"), str(REPO / "tools/margo-scheduled.sh"),
                           verb, "--print" if print_command else "--show-prompt"])
        if shutil.which("pwsh"):
            result.append([shutil.which("pwsh"), "-NoProfile", "-NonInteractive", "-File",
                           str(REPO / "tools/margo-scheduled.ps1"), verb,
                           "-Print" if print_command else "-ShowPrompt"])
        return result

    def test_schedules_select_agent_and_load_skill_separately(self):
        manifests = sorted(path for path in (REPO / "automations").glob("*.md")
                           if path.name != "README.md")
        self.assertTrue(manifests)
        for path in manifests:
            text = path.read_text(encoding="utf-8")
            verb = re.search(r"(?m)^verb: (.+)$", text).group(1)
            with self.subTest(automation=path.name):
                self.assertIn("Load the `chief-of-staff` skill", text)
                for command in self.commands(verb, print_command=True):
                    result = subprocess.run(
                        command, cwd=REPO, env=dict(os.environ, MARGO_AGENT="margo"),
                        capture_output=True, text=True, timeout=30)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertRegex(result.stdout, r"""--agent\s+['"]?margo\b""")
                    self.assertIn("chief-of-staff", result.stdout)

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
