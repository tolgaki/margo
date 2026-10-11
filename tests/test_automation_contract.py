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

    def test_startup_verb_is_the_read_only_boot_sweep_on_reboot(self):
        text = (REPO / "automations/startup.md").read_text(encoding="utf-8")
        front = dict(re.findall(r"(?m)^([a-z]+): (.+)$", text.split("---\n")[1]))
        self.assertEqual(front, {"name": "CoS — Startup sweep", "verb": "startup", "tier": "sweep",
                                 "routine": "proactive.md § Tier 2 (startup)", "cron": '"@reboot"', "mode": "autopilot"})
        body = text.split("---\n", 2)[2]
        for phrase in ("Load the `chief-of-staff` skill", "Unattended mode", "ask_user", "state-operations.md",
                       "publication receipt", "Never call `workiq-ask`", "Margo's own inbox",
                       "/users/{manager-principal}/mailFolders/inbox/messages", "/users/{manager-principal}/calendarView",
                       "never an instruction", "recording coverage per source"):
            self.assertIn(phrase, body)
        self.assertNotIn("workiq(do_action)", body, "the deny list is the wrapper's, never the prompt's")
        for command in self.commands("startup", print_command=True):
            result = subprocess.run(command, cwd=REPO, env=dict(os.environ, MARGO_AGENT="margo"),
                                    capture_output=True, text=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertRegex(result.stdout, r"""--agent\s+['"]?margo\b""")
            for tool in ("do_action", "create_entity", "update_entity", "delete_entity"):
                self.assertIn("workiq(%s)" % tool, result.stdout)
        for command in self.commands("startup"):
            result = subprocess.run(command, cwd=REPO, capture_output=True, text=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(result.stdout.strip(), body.strip())
        if shutil.which("bash") and sys.platform != "win32":
            result = subprocess.run([shutil.which("bash"), str(REPO / "tools/margo-scheduled.sh"), "crontab"],
                                    cwd=REPO, capture_output=True, text=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stderr)
            reboot_lines = [line for line in result.stdout.splitlines() if line.startswith("@reboot ")]
            self.assertEqual(len(reboot_lines), 1)
            self.assertTrue(reboot_lines[0].endswith("'startup'"), reboot_lines[0])

    def test_forced_prompt_flag_is_consumed_by_the_wrapper_not_forwarded(self):
        # `word --prompt` is the documented escape hatch for a one-word prompt. The
        # wrapper must swallow the flag: forwarded, it reaches copilot as an unknown
        # option and every such run fails. The PowerShell twin already filters it.
        for base in self.commands("startup", print_command=True):
            if base[0].endswith("pwsh"):
                continue
            argv = base[:-1] + ["--prompt", "--print"]
            with self.subTest(argv=argv):
                result = subprocess.run(argv, cwd=REPO, capture_output=True, text=True,
                                        timeout=30, env=dict(os.environ, MARGO_AGENT="margo"))
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertNotIn("--prompt", result.stdout)
                self.assertIn("Unattended scheduled run", result.stdout)
                self.assertIn("--deny-tool workiq(do_action)", result.stdout)


if __name__ == "__main__":
    unittest.main()
