import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import tarfile
import textwrap
import unittest
import zipfile


REPO = Path(__file__).resolve().parents[1]


class InstallCLITests(unittest.TestCase):
    def exercise_remote_updates(self, is_ps):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            source, remote, dest = (root / name for name in ("source", "remote", "copilot"))
            files = {
                "VERSION": "1.2.0\n",
                "agents/margo.agent.md": "local agent\n",
                "skills/chief-of-staff/SKILL.md": "local skill\n",
                "skills/chief-of-staff/preferences.md": "template\n",
                "skills/chief-of-staff/commitments.md": "template\n",
                "skills/decision-log/SKILL.md": "decision skill\n",
                "skills/decision-log/config.md": "template\n",
                "automations/morning.md": "shipped prompt\n",
                "tools/margo-scheduled.sh": "fixture wrapper\n",
                "tools/margo-scheduled.ps1": "fixture wrapper\n",
                ".github/extensions/margo-action-desk/extension.mjs": "export {};\n",
            }
            for name, content in files.items():
                path = source / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(content, encoding="utf-8")
            helper = "skills/chief-of-staff/scripts/install_manifest.py"
            (source / helper).parent.mkdir(parents=True)
            shutil.copy2(REPO / helper, source / helper)
            for name in ("install.sh", "install.ps1"):
                shutil.copy2(REPO / name, source / name)
            shutil.copytree(source, remote)
            (remote / "agents/margo.agent.md").write_text("remote agent\n")
            (remote / "skills/chief-of-staff/SKILL.md").write_text("remote skill\n")
            (remote / ".github/extensions/margo-action-desk/extension.mjs").write_text(
                "export const updated = true;\n")

            revision = "a" * 40
            metadata = root / "version"
            archive = root / ("payload.zip" if is_ps else "payload.tar.gz")
            requests = root / "requests"

            def publish(version="1.2.0", payload_version=None):
                metadata.write_text(version + "\n")
                (remote / "VERSION").write_text((payload_version or version) + "\n")
                if is_ps:
                    with zipfile.ZipFile(archive, "w") as bundle:
                        for path in sorted(remote.rglob("*")):
                            if path.is_file():
                                bundle.write(path, "margo-fixture/" + path.relative_to(remote).as_posix())
                else:
                    with tarfile.open(archive, "w:gz") as bundle:
                        bundle.add(remote, arcname="margo-fixture")

            publish()
            env = dict(os.environ, MARGO_BRANCH="main", MARGO_TEST_REVISION=revision,
                       MARGO_TEST_METADATA=str(metadata), MARGO_TEST_ARCHIVE=str(archive),
                       MARGO_TEST_REQUESTS=str(requests), MARGO_TEST_FAILURE="")
            if is_ps:
                harness = root / "network.ps1"
                harness.write_text(textwrap.dedent("""\
                    param([Parameter(Position=0)][string]$Command, [string]$Dest,
                          [switch]$All, [switch]$ActionDesk, [switch]$Check,
                          [switch]$Reinstall, [switch]$DryRun, [switch]$Link)
                    function Invoke-WebRequest {
                        param($Uri, $OutFile, $Headers, [switch]$UseBasicParsing, $TimeoutSec)
                        Add-Content $env:MARGO_TEST_REQUESTS $Uri
                        if ($env:MARGO_TEST_FAILURE -eq 'offline') { throw 'fixture offline' }
                        if ($Uri -eq 'https://api.github.com/repos/tolgaki/margo/commits/main') {
                            if ($Headers.Accept -ne 'application/vnd.github.sha') { throw 'missing SHA accept' }
                            return [pscustomobject]@{Content=[Text.Encoding]::UTF8.GetBytes($env:MARGO_TEST_REVISION)}
                        }
                        $revision = $env:MARGO_TEST_REVISION
                        if ($Uri -eq "https://raw.githubusercontent.com/tolgaki/margo/$revision/VERSION") {
                            return [pscustomobject]@{Content=(Get-Content $env:MARGO_TEST_METADATA -Raw)}
                        }
                        if ($Uri -eq "https://codeload.github.com/tolgaki/margo/zip/$revision") {
                            if ($env:MARGO_TEST_FAILURE -eq 'archive') { throw 'fixture archive failure' }
                            Copy-Item $env:MARGO_TEST_ARCHIVE $OutFile
                            return
                        }
                        throw "Unexpected network request: $Uri"
                    }
                    & $env:MARGO_TEST_INSTALLER @PSBoundParameters
                    exit $LASTEXITCODE
                    """), encoding="utf-8")
                env["MARGO_TEST_INSTALLER"] = str(source / "install.ps1")
                command = [shutil.which("pwsh"), "-NoProfile", "-NonInteractive", "-File", str(harness)]
            else:
                binaries = root / "bin"
                binaries.mkdir()
                curl = binaries / "curl"
                curl.write_text("#!" + sys.executable + "\n" + textwrap.dedent("""\
                    import os, sys
                    from pathlib import Path
                    url = sys.argv[-1]
                    with open(os.environ["MARGO_TEST_REQUESTS"], "a") as stream:
                        stream.write(url + "\\n")
                    if os.environ["MARGO_TEST_FAILURE"] == "offline":
                        sys.exit(7)
                    revision = os.environ["MARGO_TEST_REVISION"]
                    if url == "https://api.github.com/repos/tolgaki/margo/commits/main":
                        assert "Accept: application/vnd.github.sha" in sys.argv
                        print(revision)
                    elif url == f"https://raw.githubusercontent.com/tolgaki/margo/{revision}/VERSION":
                        sys.stdout.write(Path(os.environ["MARGO_TEST_METADATA"]).read_text())
                    elif url == f"https://codeload.github.com/tolgaki/margo/tar.gz/{revision}":
                        if os.environ["MARGO_TEST_FAILURE"] == "archive":
                            sys.exit(7)
                        sys.stdout.buffer.write(Path(os.environ["MARGO_TEST_ARCHIVE"]).read_bytes())
                    else:
                        sys.exit("Unexpected network request: " + url)
                    """), encoding="utf-8")
                curl.chmod(0o755)
                env["PATH"] = str(binaries) + os.pathsep + env["PATH"]
                command = [shutil.which("bash"), str(source / "install.sh")]

            flags = {name: "-" + name if is_ps else "--" + name.lower()
                     for name in ("Dest", "All", "Check", "Reinstall", "DryRun", "Link")}
            flags["DryRun"] = "-DryRun" if is_ps else "--dry-run"
            flags["ActionDesk"] = "-ActionDesk" if is_ps else "--action-desk"

            def run(verb, *extra, target=dest, success=True):
                result = subprocess.run(
                    command + [verb, flags["Dest"], str(target)] + list(extra),
                    env=env, cwd=source, text=True, stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE, timeout=120,
                )
                output = result.stdout + result.stderr
                if success:
                    self.assertEqual(result.returncode, 0, output)
                else:
                    self.assertNotEqual(result.returncode, 0, output)
                return output

            def snapshot():
                return {p.relative_to(dest).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
                        for p in dest.rglob("*") if p.is_file()}

            def installed():
                return dict(line.split("=", 1) for line in
                            (dest / ".margo-install").read_text(encoding="utf-8-sig").splitlines())

            run("install", flags["All"], flags["ActionDesk"])
            kept = {
                "skills/chief-of-staff/preferences.md": "private preference\n",
                "skills/chief-of-staff/commitments.md": "private commitment\n",
                "skills/decision-log/config.md": "private decision config\n",
                "skills/chief-of-staff/state/nested/legacy.txt": "legacy state\n",
                "automations/morning.md": "custom prompt\n",
                "margo/state/fixture/margo.sqlite3": "private runtime fixture\n",
            }
            for name, content in kept.items():
                path = dest / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(content)

            before = snapshot()
            self.assertIn("Update available", run("update", flags["Check"]))
            self.assertEqual(snapshot(), before)
            run("update", flags["DryRun"])
            self.assertEqual(snapshot(), before)
            run("update")
            self.assertEqual(installed()["revision"], revision)
            self.assertEqual(installed()["version"], "1.2.0")
            self.assertEqual((dest / "agents/margo.agent.md").read_text(), "remote agent\n")
            self.assertEqual((dest / "extensions/margo-action-desk/extension.mjs").read_text(),
                             "export const updated = true;\n")
            self.assertEqual((source / "agents/margo.agent.md").read_text(), "local agent\n")

            before = snapshot()
            requests.write_text("")
            self.assertIn("Up to date", run("update"))
            self.assertNotIn("codeload", requests.read_text())
            self.assertEqual(snapshot(), before)
            (dest / "agents/margo.agent.md").write_text("code to refresh\n")
            run("update", flags["Reinstall"])
            self.assertEqual((dest / "agents/margo.agent.md").read_text(), "remote agent\n")
            for name, content in kept.items():
                self.assertEqual((dest / name).read_text(), content)

            env["MARGO_TEST_REVISION"] = "b" * 40
            self.assertIn("Update available", run("update", flags["Check"]))
            run("update")
            self.assertEqual(installed()["version"], "1.2.0")
            self.assertEqual(installed()["revision"], "b" * 40)
            env["MARGO_TEST_REVISION"] = "c" * 40
            publish("1.3.0")
            run("update")
            self.assertEqual(installed()["version"], "1.3.0")
            self.assertEqual(installed()["revision"], "c" * 40)
            for name, content in kept.items():
                self.assertEqual((dest / name).read_text(), content)

            before = snapshot()
            for failure in ("offline", "archive", "invalid-revision", "multiline-revision", "invalid-version",
                            "mismatched-payload", "downgrade"):
                with self.subTest(failure=failure):
                    env["MARGO_TEST_REVISION"] = "d" * 40
                    env["MARGO_TEST_FAILURE"] = failure
                    publish("1.3.0")
                    if failure == "invalid-revision":
                        env["MARGO_TEST_REVISION"] = "not-a-revision"
                    elif failure == "multiline-revision":
                        env["MARGO_TEST_REVISION"] = "d" * 40 + "\nunexpected-line"
                    elif failure == "invalid-version":
                        metadata.write_text("not-a-version\n")
                    elif failure == "mismatched-payload":
                        publish("1.3.0", payload_version="1.2.0")
                    elif failure == "downgrade":
                        publish("1.2.0")
                    if failure not in ("archive", "mismatched-payload"):
                        run("update", flags["Check"], success=False)
                    run("update", success=False)
                    self.assertEqual(snapshot(), before)

            env["MARGO_TEST_FAILURE"] = ""
            publish("1.3.0")
            subset = root / "subset"
            run("install", target=subset)
            run("update", flags["All"], target=subset)
            self.assertFalse((subset / "skills/decision-log").exists())
            self.assertFalse((subset / "extensions/margo-action-desk").exists())
            env["MARGO_TEST_REVISION"] = "e" * 40
            publish("1.4.0-rc.1+fixture")
            run("update")
            self.assertEqual(installed()["version"], "1.4.0-rc.1+fixture")
            linked = root / "linked"
            run("install", flags["Link"], target=linked)
            requests.write_text("")
            env["MARGO_TEST_FAILURE"] = "offline"
            self.assertIn("linked install", run("update", target=linked))
            self.assertEqual(requests.read_text(), "")

    @unittest.skipUnless(shutil.which("bash") and not sys.platform.startswith("win"),
                         "POSIX installer")
    def test_shell_remote_updates_preserve_state(self):
        self.exercise_remote_updates(False)

    @unittest.skipUnless(shutil.which("pwsh"), "PowerShell not installed")
    def test_powershell_remote_updates_preserve_state(self):
        self.exercise_remote_updates(True)

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
