"""Repository checks for the Margo Cloud deployment files (cloud/): what the scripts need, the infrastructure
provides; every role runs the C# image; the instance template matches its source; nothing of Foundry remains.

The C# runtime itself is tested by `dotnet test` in cloud/; these checks read only the deployment files, so they run
with the rest of the desktop suite (Python 3.9+, standard library).
"""

import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CLOUD = ROOT / "cloud"


def read(*parts):
    return CLOUD.joinpath(*parts).read_text(encoding="utf-8")


class Deployment(unittest.TestCase):
    def test_postprovision_needs_only_what_main_bicep_outputs(self):
        bicep = read("infra", "main.bicep")
        outputs = set(re.findall(r"^output (\w+)", bicep, re.MULTILINE))
        script = read("scripts", "postprovision.sh")
        needed = re.search(r"^need ((?:[^\n]*\\\n)*[^\n]*)", script, re.MULTILINE).group(1)
        names = set(re.findall(r"[A-Z][A-Z0-9_]+", needed))
        self.assertTrue(names)
        self.assertEqual(sorted(names - outputs), [])
        self.assertIn("MESSAGING_ENDPOINT", outputs)
        self.assertRegex(bicep, r"output MESSAGING_ENDPOINT string = '[^']*/api/messages'")

    def test_no_foundry_hosted_agent_remains(self):
        files = [CLOUD / "Dockerfile"]
        files += sorted((CLOUD / "infra").glob("*.bicep"))
        files += sorted((CLOUD / "scripts").glob("*.sh"))
        files += sorted(path for path in (CLOUD / "src").rglob("*.cs") if "bin" not in path.parts and "obj" not in path.parts)
        self.assertGreater(len(files), 20)
        for path in files:
            text = path.read_text(encoding="utf-8")
            for word in ("foundry", "IdentityProxyManager", "azure-ai-projects", "AIProjectClient"):
                self.assertNotIn(word.lower(), text.lower(), "%s mentions %s" % (path.relative_to(ROOT), word))

    def test_instance_template_matches_its_source_and_stays_bounded(self):
        template = json.loads(read("infra", "instance.json"))
        source = read("infra", "instance.bicep")
        declared = set(re.findall(r"^param (\w+)", source, re.MULTILINE))
        self.assertEqual(set(template["parameters"]), declared, "rebuild instance.json from instance.bicep")
        text = json.dumps(template)
        self.assertIn('"maxReplicas": 1', text)
        self.assertIn('"external": false', text)
        self.assertNotIn("storageAccounts/delete", text)
        env = re.findall(r"createObject\('name', '([A-Z0-9_]+)'", text)
        for name in re.findall(r"\{ name: '([A-Z0-9_]+)'", source):
            self.assertIn(name, env, "instance.json lacks %s; rebuild it from instance.bicep" % name)

    def test_every_role_runs_the_csharp_image(self):
        dockerfile = read("Dockerfile")
        main = read("infra", "main.bicep")
        instance = read("infra", "instance.bicep")
        template = json.loads(read("infra", "instance.json"))
        self.assertIn("command: [ 'dotnet', '/app/router/Margo.Cloud.Router.dll' ]", main)
        self.assertIn("command: [ 'dotnet', '/app/provisioner/Margo.Cloud.Provisioner.dll', 'run' ]", main)
        self.assertIn("command: [ 'dotnet', '/app/host/Margo.Cloud.Host.dll' ]", instance)
        containers = [container for resource in template["resources"]
                      for container in resource.get("properties", {}).get("template", {}).get("containers", [])]
        self.assertIn(["dotnet", "/app/host/Margo.Cloud.Host.dll"], [c.get("command") for c in containers])
        for app in ("host/Margo.Cloud.Host", "router/Margo.Cloud.Router", "provisioner/Margo.Cloud.Provisioner"):
            name = app.split("/")[1]
            self.assertRegex(dockerfile, r"dotnet publish src/%s/%s\.csproj -c Release -o /out/%s\b"
                             % (re.escape(name), re.escape(name), app.split("/")[0]))
        self.assertIn('CMD ["dotnet", "/app/host/Margo.Cloud.Host.dll"]', dockerfile)
        self.assertIn("EXPOSE 8088", dockerfile)
        self.assertRegex(dockerfile, r"(?m)^USER \$APP_UID$")
        self.assertRegex(dockerfile, r"apk add --no-cache tzdata")
        self.assertNotIn("MargoStateProbes", dockerfile)
        self.assertNotRegex(dockerfile, r"(?m)^COPY .*state/")

    def test_every_copy_source_exists_and_the_build_scripts_point_at_cloud(self):
        dockerfile = read("Dockerfile")
        for source in re.findall(r"(?m)^COPY (?!--from)(.+) \S+$", dockerfile):
            for item in source.split():
                self.assertTrue((ROOT / item).exists(), "COPY source %s does not exist" % item)
        self.assertIn('--file "$CLOUD/Dockerfile"', read("scripts", "build-image.sh"))
        for script in sorted((CLOUD / "scripts").glob("*.sh")):
            text = script.read_text(encoding="utf-8")
            self.assertNotIn("margo_autopilot", text, script.name)
            self.assertNotIn("$AUTOPILOT", text, script.name)

    def test_dockerignore_keeps_state_and_build_outputs_out(self):
        ignored = set((ROOT / ".dockerignore").read_text(encoding="utf-8").split())
        for entry in ("**/state/", "**/bin/", "**/obj/", "cloud/tests/", "tests/", "docs/", "**/*.sqlite3", "**/.env"):
            self.assertIn(entry, ignored)

    def test_dockerignore_keeps_what_git_ignores_as_private_out_of_the_registry_upload(self):
        # build-image.sh uploads the repository to the registry build and only refuses *uncommitted* changes, so a file
        # git ignores is uploaded unless .dockerignore drops it too: the Agent 365 CLI's files (tenant identifiers and
        # possibly a client secret, which .gitignore expects to find here by mistake), local agent settings and
        # worktrees, and the publish outputs cloud/README.md writes.
        ignored = set((ROOT / ".dockerignore").read_text(encoding="utf-8").split())
        for entry in ("**/a365.config.json", "**/a365.generated.config.json", ".claude/", "cloud/out/",
                      "cloud/artifacts/"):
            self.assertIn(entry, ignored)
        gitignored = set((ROOT / ".gitignore").read_text(encoding="utf-8").split())
        self.assertIn("cloud/out/", gitignored)
        self.assertIn("-o out/host", read("README.md"))


if __name__ == "__main__":
    unittest.main()
