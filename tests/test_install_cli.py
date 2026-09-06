import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


REPO = Path(__file__).resolve().parents[1]


class InstallCLITests(unittest.TestCase):
    def exercise(self, command):
        with tempfile.TemporaryDirectory() as directory:
            dest = Path(directory).resolve() / "copilot"

            def run(verb, extra=()):
                result = subprocess.run(
                    command + [verb] + list(extra), cwd=REPO, text=True,
                    stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=120,
                )
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                return result.stdout

            is_ps = command[0].endswith(("pwsh", "pwsh.exe"))
            dest_args = ["-Dest" if is_ps else "--dest", str(dest)]
            all_args = ["-All" if is_ps else "--all"]
            run("install", dest_args + all_args)
            self.assertTrue((dest / ".margo-install").is_file())
            manifest = json.loads((dest / ".margo-files.json").read_text())
            self.assertIn("skills/chief-of-staff/scripts/capacity.py", manifest)
            self.assertNotIn("skills/chief-of-staff/preferences.md", manifest)
            personal = dest / "skills/chief-of-staff/preferences.md"
            personal.write_text("- **Name / preferred name:** Example User\n", encoding="utf-8")
            runtime = dest / "margo/state/example/margo.db"
            runtime.parent.mkdir(parents=True)
            runtime.write_bytes(b"private runtime fixture")
            run("install", dest_args + all_args)
            self.assertIn("Example User", personal.read_text())
            self.assertIn("personalized", run("status", dest_args))
            run("uninstall", dest_args + (["-Yes"] if is_ps else ["--yes"]))
            self.assertEqual(runtime.read_bytes(), b"private runtime fixture")
            self.assertFalse((dest / ".margo-files.json").exists())

    @unittest.skipUnless(shutil.which("bash") and not sys.platform.startswith("win"),
                         "POSIX installer")
    def test_shell_install_reinstall_uninstall(self):
        self.exercise([shutil.which("bash"), str(REPO / "install.sh")])

    @unittest.skipUnless(shutil.which("pwsh"), "PowerShell not installed")
    def test_powershell_install_reinstall_uninstall(self):
        self.exercise([shutil.which("pwsh"), "-NoProfile", "-NonInteractive",
                       "-File", str(REPO / "install.ps1")])


if __name__ == "__main__":
    unittest.main()
