"""Contracts for deploy/azure: placeholders only, hardened units, pinned shapes, gate-only Copilot config.

Everything here reads the shipped templates; nothing deploys, mounts or talks to Azure. A pass proves
the templates keep the contract this repository can check (schema keys, hardening directives, no
GUID or secret-shaped literal, executable scripts, the MCP config bootstrap writes). It proves
nothing about a tenant: that evidence comes from verify-phase0.sh on the host.
"""

import contextlib
import io
import json
import os
import re
import shlex
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEPLOY = ROOT / "deploy/azure"
SCRIPTS = ROOT / "skills/chief-of-staff/scripts"
sys.path.insert(0, str(SCRIPTS))

import manager_directives as md  # noqa: E402
from margo_store import StateError  # noqa: E402

GUID = re.compile(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")
PLACEHOLDER = re.compile(r"^\{[a-z0-9-]+\}$")
EMAIL = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")
HOME_PATH = re.compile(r"/home/([A-Za-z][A-Za-z0-9_.-]*)")
CLOUD_INIT_TOKEN = re.compile(r"__[A-Z0-9_]+__")

# The contract's deployment schema, key for key.
CONTRACT = {
    "root": {"schema_version", "profile", "margo", "manager", "control", "workiq", "harness", "gate"},
    "control": {"cli_logins", "teams_chat_id", "teams_enabled", "email_enabled", "require_authentication_results",
                "max_replies_per_hour", "approval_ttl_hours"},
    "workiq": {"command", "env", "server_name", "read_tools", "write_tools", "argument_keys", "call_templates"},
    "harness": {"poll_seconds", "copilot_command", "copilot_check", "max_backoff_seconds", "startup_verb", "health_path",
                "automations_dir", "scheduled_wrapper"},
    "gate": {"socket", "audit_log", "margo_root_paths", "manager_root_paths"},
}
READ_TOOLS = ["fetch", "retrieve", "ask", "call_function", "get_schema", "search_paths", "fetch_blob"]
WRITE_TOOLS = ["do_action", "create_entity", "update_entity", "delete_entity"]
SCRIPT_NAMES = ["lib.sh", "bootstrap.sh", "verify-phase0.sh", "backup-state.sh", "reauth.sh", "decommission.sh"]
UNIT_NAMES = ["margo-gate.service", "margo-harness.service", "margo-backup.service", "margo-backup.timer"]
ENVIRONMENT = {"COPILOT_HOME=/var/lib/margo/copilot", "MARGO_DEPLOYMENT=/etc/margo/deployment.json",
               "MARGO_GATE_SOCKET=/run/margo/gate.sock"}
GATE_CLIENT = "/var/lib/margo/copilot/skills/chief-of-staff/scripts/workiq_gate_client.py"
BICEP = shutil.which("bicep") or os.environ.get("MARGO_BICEP")


def read(relative):
    return (DEPLOY / relative).read_text(encoding="utf-8")


def deploy_files():
    return sorted(path for path in DEPLOY.rglob("*") if path.is_file())


def parse_unit(text):
    """Minimal INI reader: {section: {key: [values]}}; repeated keys keep every value in order."""
    sections = {}
    current = None
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("[") and line.endswith("]"):
            current = line[1:-1]
            sections.setdefault(current, {})
            continue
        key, separator, value = line.partition("=")
        if current is None or not separator:
            raise AssertionError("unit line outside a section or without '=': %r" % raw)
        sections[current].setdefault(key.strip(), []).append(value.strip())
    return sections


def mcp_writer_source():
    """The Python heredoc bootstrap.sh runs to write Copilot's MCP config, extracted verbatim."""
    text = read("scripts/bootstrap.sh")
    start = text.index("write_mcp_config()")
    body = text[start:]
    # The heredoc opener shares its line with the shell's `|| die ...`; the body starts on the next line.
    begin = body.index("\n", body.index("<<'EOF'")) + 1
    end = body.index("\nEOF\n", begin)
    return body[begin:end] + "\n"


class DeploymentExampleTests(unittest.TestCase):
    def setUp(self):
        self.document = json.loads(read("deployment.example.json"))

    def test_example_matches_the_contract_schema_key_for_key(self):
        self.assertEqual(set(self.document), CONTRACT["root"])
        for section in ("control", "workiq", "harness", "gate"):
            self.assertEqual(set(self.document[section]), CONTRACT[section], section)
        self.assertEqual(self.document["schema_version"], 1)
        self.assertEqual(self.document["profile"], "remote-host")
        self.assertEqual(self.document["workiq"]["server_name"], "workiq-gate")
        self.assertEqual(self.document["workiq"]["read_tools"], READ_TOOLS)
        self.assertEqual(self.document["workiq"]["write_tools"], WRITE_TOOLS)
        self.assertEqual(set(self.document["workiq"]["call_templates"]), {"teams_message", "email_message"})
        self.assertEqual(self.document["harness"]["startup_verb"], "startup")
        self.assertEqual(self.document["gate"]["socket"], "/run/margo/gate.sock")

    def test_example_loads_with_placeholders_and_is_refused_strictly(self):
        loaded = md.load_deployment(DEPLOY / "deployment.example.json", allow_placeholders=True)
        self.assertEqual(loaded["margo"]["object_id"], "{margo-object-id}")
        # The failure that would look like success: an example that passes the strict loader holds
        # a real-looking object id, which is exactly what must never be in the repository.
        with self.assertRaises(StateError):
            md.load_deployment(DEPLOY / "deployment.example.json")

    def test_identity_fields_are_placeholders_or_fictional(self):
        for section in ("margo", "manager"):
            self.assertRegex(self.document[section]["object_id"], PLACEHOLDER, section)
            self.assertTrue(self.document[section]["principal"].endswith("@example.com"), section)
        for login in self.document["control"]["cli_logins"]:
            self.assertRegex(login, PLACEHOLDER)
        self.assertRegex(self.document["control"]["teams_chat_id"], PLACEHOLDER)
        for path in self.document["gate"]["margo_root_paths"] + self.document["gate"]["manager_root_paths"]:
            self.assertTrue("{" in path or "example.com" in path or path == "/me/", path)
        self.assertIn("{", self.document["workiq"]["command"][-1])


class SanitizationTests(unittest.TestCase):
    def test_no_guid_shaped_literal_anywhere_under_deploy(self):
        # Prove the detector is live before trusting its silence.
        self.assertTrue(GUID.search(str(uuid.uuid5(uuid.NAMESPACE_DNS, "manager.example.com"))))
        for path in deploy_files():
            with self.subTest(file=str(path.relative_to(ROOT))):
                self.assertIsNone(GUID.search(path.name))
                self.assertIsNone(GUID.search(path.read_text(encoding="utf-8")))

    def test_no_real_addresses_tenants_or_home_names(self):
        for path in deploy_files():
            text = path.read_text(encoding="utf-8")
            with self.subTest(file=str(path.relative_to(ROOT))):
                for address in EMAIL.findall(text):
                    domain = address.rsplit("@", 1)[1].lower()
                    self.assertTrue(domain in ("example.com", "example.org") or domain.endswith(".example"), address)
                for match in re.finditer(r"[A-Za-z0-9{}-]+\.onmicrosoft\.com", text):
                    self.assertTrue(match.group(0).startswith("{"), match.group(0))
                for name in HOME_PATH.findall(text):
                    self.assertEqual(name, "margo", "home path leaks a login name: /home/%s" % name)

    def test_tree_has_no_state_or_home_directories(self):
        for path in DEPLOY.rglob("*"):
            if path.is_dir():
                self.assertNotIn(path.name, ("state", "home", "Users", ".github", "packaging"), str(path))


class UnitFileTests(unittest.TestCase):
    def setUp(self):
        self.units = {name: parse_unit(read("systemd/" + name)) for name in UNIT_NAMES}

    def assert_hardened(self, name):
        service = self.units[name]["Service"]
        self.assertEqual(service["User"], ["margo"], name)
        self.assertEqual(service["NoNewPrivileges"], ["yes"], name)
        self.assertEqual(service["ProtectSystem"], ["strict"], name)
        self.assertEqual(service["ProtectHome"], ["yes"], name)
        self.assertEqual(service["PrivateTmp"], ["yes"], name)
        self.assertEqual(service["Restart"], ["on-failure"], name)
        environment = set(" ".join(service["Environment"]).split())
        self.assertTrue(ENVIRONMENT <= environment, (name, environment))
        self.assertEqual(self.units[name]["Unit"]["ConditionPathExists"], ["/etc/margo/deployment.json"], name)
        return service

    def test_gate_unit_owns_the_token_group_and_runtime_directory(self):
        service = self.assert_hardened("margo-gate.service")
        self.assertEqual(service["SupplementaryGroups"], ["margo-gate"])
        self.assertEqual(service["RuntimeDirectory"], ["margo"])
        self.assertEqual(service["SyslogIdentifier"], ["margo-gate"])
        paths = set(" ".join(service["ReadWritePaths"]).split())
        self.assertEqual(paths, {"/var/lib/margo", "/run/margo", "/var/lib/margo-gate"})
        self.assertIn("workiq_gate.py serve", service["ExecStart"][0])
        self.assertTrue(service["ExecStart"][0].split()[1].startswith("/opt/margo/"), service["ExecStart"])

    def test_harness_unit_depends_on_the_gate_and_never_restarts_a_block(self):
        service = self.assert_hardened("margo-harness.service")
        unit = self.units["margo-harness.service"]["Unit"]
        self.assertEqual(service["RestartPreventExitStatus"], ["3"])
        self.assertEqual(unit["Requires"], ["margo-gate.service"])
        self.assertIn("margo-gate.service", " ".join(unit["After"]).split())
        self.assertEqual(service["SyslogIdentifier"], ["margo-harness"])
        # The failure that would look like success: a harness with the token group would let every
        # Copilot session it starts read Margo's tokens.
        self.assertNotIn("SupplementaryGroups", service)
        paths = set(" ".join(service["ReadWritePaths"]).split())
        self.assertEqual(paths, {"/var/lib/margo", "/run/margo"})
        self.assertIn("remote_harness.py run", service["ExecStart"][0])
        self.assertEqual(service["EnvironmentFile"], ["-/etc/margo/copilot.env"])

    def test_backup_timer_and_service_point_at_the_shipped_script(self):
        timer = self.units["margo-backup.timer"]
        self.assertEqual(timer["Timer"]["Unit"], ["margo-backup.service"])
        self.assertEqual(timer["Timer"]["Persistent"], ["true"])
        self.assertEqual(timer["Install"]["WantedBy"], ["timers.target"])
        service = self.units["margo-backup.service"]["Service"]
        self.assertEqual(service["Type"], ["oneshot"])
        self.assertEqual(service["ExecStart"], ["/opt/margo/deploy/azure/scripts/backup-state.sh"])
        self.assertNotIn("User", service)  # root: it stops the harness and writes a root-only directory
        for name in ("margo-gate.service", "margo-harness.service"):
            self.assertEqual(self.units[name]["Install"]["WantedBy"], ["multi-user.target"])

    @unittest.skipUnless(sys.platform.startswith("linux") and shutil.which("systemd-analyze"), "needs systemd-analyze")
    def test_systemd_analyze_reports_nothing_but_the_absent_host_paths(self):
        # `verify` exits 0 on unknown keys and bad values and only logs them, so every output line
        # counts. The one tolerated line is the missing /opt/margo executable on a machine that is
        # not the host; CI stages the checkout there first and tolerates nothing.
        command = ["systemd-analyze", "verify", "--man=no"] + [str(DEPLOY / "systemd" / name) for name in UNIT_NAMES]
        result = subprocess.run(command, capture_output=True, text=True, check=False)
        tolerated = re.compile(r"Command (/opt/margo/\S+) is not executable: No such file or directory")
        unexplained = []
        for line in (result.stdout + result.stderr).splitlines():
            match = tolerated.search(line)
            if match and not Path(match.group(1)).exists():
                continue
            if line.strip():
                unexplained.append(line)
        self.assertEqual(unexplained, [], result.stdout + result.stderr)


class CloudInitAndBicepTests(unittest.TestCase):
    def test_cloud_init_is_cloud_config_and_writes_only_the_bootstrap_env(self):
        text = read("cloud-init.yaml")
        self.assertTrue(text.startswith("#cloud-config\n"))
        self.assertEqual(re.findall(r"(?m)^\s+- path: (\S+)", text), ["/etc/margo/bootstrap.env"])
        self.assertIn("/opt/margo/deploy/azure/scripts/bootstrap.sh", text)
        for forbidden in ("Bearer", "access_token", "GH_TOKEN=", "password"):
            self.assertNotIn(forbidden, text)
        # Every token cloud-init carries is rendered by main.bicep, and nothing else is.
        tokens = set(CLOUD_INIT_TOKEN.findall(text))
        rendered = set(re.findall(r"\['(__[A-Z0-9_]+__)',", read("main.bicep")))
        self.assertEqual(tokens, rendered)
        self.assertTrue({"__KEY_VAULT_NAME__", "__REPO_REVISION__", "__NODE_VERSION__", "__COPILOT_VERSION__"} <= tokens)

    def test_main_bicep_declares_the_contract_parameters_and_outputs(self):
        text = read("main.bicep")
        params = set(re.findall(r"(?m)^param (\w+) ", text))
        required = {"location", "vmName", "vmSize", "adminObjectId", "managerVmLogin", "keyVaultName", "vnetName",
                    "subnetName", "bastionName", "roleDefinitionIds", "repoRevision", "nodeVersion", "copilotVersion",
                    "alertEmail"}
        self.assertTrue(required <= params, required - params)
        self.assertIn("param roleDefinitionIds object", text)
        outputs = set(re.findall(r"(?m)^output (\w+) ", text))
        self.assertTrue({"principalId", "keyVaultUri", "vmId"} <= outputs, outputs)
        for needle in ("identity: { type: 'SystemAssigned' }", "enableRbacAuthorization: true", "enablePurgeProtection: true",
                       "enableSoftDelete: true", "'ubuntu-24_04-lts'", "AADSSHLoginForLinux", "Microsoft.Network/bastionHosts",
                       "roleDefinitionIds.virtualMachineAdministratorLogin", "roleDefinitionIds.keyVaultSecretsOfficer",
                       "roleDefinitionIds.keyVaultSecretsUser", "subscriptionResourceId('Microsoft.Authorization/roleDefinitions'",
                       "modules/monitoring.bicep", "modules/firewall.bicep", "loadTextContent('cloud-init.yaml')"):
            self.assertIn(needle, text, needle)
        nic = text[text.index("resource nic "):text.index("// ---", text.index("resource nic "))]
        self.assertNotIn("publicIPAddress", nic)
        monitoring = read("modules/monitoring.bicep")
        self.assertIn('ProcessName == "margo-harness" and SeverityLevel == "err"', monitoring)
        self.assertIn("VmAvailabilityMetric", monitoring)
        self.assertIn("emailReceivers", monitoring)

    def test_example_parameters_target_main_and_hold_placeholders(self):
        text = read("main.example.bicepparam")
        self.assertIn("using './main.bicep'", text)
        declared = set(re.findall(r"(?m)^param (\w+) ", read("main.bicep")))
        assigned = dict(re.findall(r"(?m)^param (\w+) = (.*)$", text))
        self.assertTrue(set(assigned) <= declared, set(assigned) - declared)
        for name in ("adminObjectId", "repoRevision", "copilotVersion"):
            self.assertRegex(assigned[name].strip("'"), PLACEHOLDER, name)
        self.assertEqual(assigned["alertEmail"], "'dana@example.com'")
        self.assertEqual(text.count("'{role-definition-id}'"), 3)

    @unittest.skipUnless(BICEP, "needs the bicep CLI (or MARGO_BICEP); CI compiles with az bicep")
    def test_templates_compile_with_the_bicep_cli(self):
        for relative in ("main.bicep", "modules/firewall.bicep", "modules/monitoring.bicep"):
            result = subprocess.run([BICEP, "build", str(DEPLOY / relative), "--stdout"], capture_output=True, text=True,
                                    check=False)
            self.assertEqual(result.returncode, 0, result.stderr)
        result = subprocess.run([BICEP, "build-params", str(DEPLOY / "main.example.bicepparam"), "--stdout"],
                                capture_output=True, text=True, check=False)
        self.assertEqual(result.returncode, 0, result.stderr)


class ScriptTests(unittest.TestCase):
    def test_every_script_is_bash_with_strict_mode(self):
        found = sorted(path.name for path in (DEPLOY / "scripts").glob("*.sh"))
        self.assertEqual(found, sorted(SCRIPT_NAMES))
        for name in SCRIPT_NAMES:
            text = read("scripts/" + name)
            with self.subTest(script=name):
                self.assertTrue(text.startswith("#!/usr/bin/env bash\n"))
                self.assertIn("\nset -euo pipefail\n", text)
                self.assertNotIn("\r", text)
                if name != "lib.sh":
                    self.assertIn('# shellcheck source=lib.sh\n. "$SELF_DIR/lib.sh"', text)

    @unittest.skipIf(os.name == "nt", "the executable bit is not meaningful on Windows")
    def test_every_script_is_executable(self):
        for name in SCRIPT_NAMES:
            mode = (DEPLOY / "scripts" / name).stat().st_mode
            self.assertTrue(mode & stat.S_IXUSR and mode & stat.S_IXOTH, name)

    @unittest.skipIf(os.name == "nt" or not shutil.which("bash"), "needs bash")
    def test_every_script_parses(self):
        for name in SCRIPT_NAMES:
            result = subprocess.run(["bash", "-n", str(DEPLOY / "scripts" / name)], capture_output=True, text=True, check=False)
            self.assertEqual(result.returncode, 0, (name, result.stderr))

    def test_scripts_fail_closed_on_the_boundaries_that_matter(self):
        bootstrap = read("scripts/bootstrap.sh")
        self.assertIn("is a member of $MARGO_GATE_GROUP; remove it", bootstrap)
        self.assertIn("--profile remote-host", bootstrap)
        self.assertIn('"$SELF_DIR/verify-phase0.sh" || rc=$?', bootstrap)
        self.assertIn('exit "$rc"', bootstrap)
        self.assertNotIn("usermod -aG margo-gate", bootstrap)
        verify = read("scripts/verify-phase0.sh")
        self.assertIn("/evidence", read("scripts/lib.sh"))
        self.assertIn("exit 1", verify)
        backup = read("scripts/backup-state.sh")
        self.assertIn("source.backup(copy)", backup)
        self.assertIn("systemctl stop margo-harness.service", backup)
        self.assertIn("trap cleanup EXIT", backup)
        decommission = read("scripts/decommission.sh")
        self.assertIn("refusing without --yes", decommission)
        self.assertNotIn("az ", decommission)  # revokes nothing automatically

    def test_control_cli_invocations_parse_with_the_shipped_parser(self):
        # The scripts call margo_control.py with --socket; that option lives on the root parser
        # of the shipped module, so it must come before the subcommand. Parse each script's
        # exact argv with the real parser rather than trusting the order by eye.
        import margo_control  # noqa: E402  (shipped sibling module; imports manager_directives)

        invocation = re.compile(r'python3 "\$MARGO_SCRIPTS/margo_control\.py" ([^\n]*)')
        seen = []
        for name in SCRIPT_NAMES:
            for match in invocation.finditer(read("scripts/" + name)):
                tail = re.split(r"\s2>|\s\||\)|\s\\$", match.group(1), maxsplit=1)[0]
                argv = shlex.split(tail)
                seen.append((name, argv))
                with self.subTest(script=name, argv=argv), contextlib.redirect_stderr(io.StringIO()) as stderr:
                    try:
                        parsed = margo_control.parser().parse_args(argv)
                    except SystemExit:
                        self.fail("%s: margo_control.py rejects %r: %s" % (name, argv, stderr.getvalue().strip()))
                    self.assertEqual(parsed.socket, "$MARGO_GATE_SOCKET")
                    self.assertEqual(parsed.command, "health")
        self.assertEqual(sorted(name for name, _ in seen), ["reauth.sh", "verify-phase0.sh"])

    def run_mcp_writer(self, directory, existing=None):
        path = Path(directory) / "mcp-config.json"
        if existing is not None:
            path.write_text(json.dumps(existing), encoding="utf-8")
        result = subprocess.run([sys.executable, "-I", "-", str(path), GATE_CLIENT], input=mcp_writer_source(),
                                capture_output=True, text=True, check=False)
        return result, path

    def test_bootstrap_mcp_config_registers_the_gate_only(self):
        with tempfile.TemporaryDirectory() as directory:
            result, path = self.run_mcp_writer(directory)
            self.assertEqual(result.returncode, 0, result.stderr)
            config = json.loads(path.read_text(encoding="utf-8"))
            self.assertEqual(config, {"mcpServers": {"workiq-gate": {"type": "stdio", "command": "python3", "args": [GATE_CLIENT]}}})
            self.assertNotIn("workiq", {name.lower() for name in config["mcpServers"]} - {"workiq-gate"})

    def test_bootstrap_mcp_config_keeps_other_servers_and_refuses_a_direct_workiq_server(self):
        with tempfile.TemporaryDirectory() as directory:
            other = {"mcpServers": {"github": {"type": "stdio", "command": "gh-mcp"}}}
            result, path = self.run_mcp_writer(directory, other)
            self.assertEqual(result.returncode, 0, result.stderr)
            config = json.loads(path.read_text(encoding="utf-8"))
            self.assertEqual(set(config["mcpServers"]), {"github", "workiq-gate"})
        for direct in ({"workiq": {"type": "stdio", "command": "npx", "args": ["-y", "@microsoft/workiq-mcp"]}},
                       {"m365": {"type": "stdio", "command": "npx", "args": ["@microsoft/workiq-mcp@1.0.0"]}}):
            with tempfile.TemporaryDirectory() as directory:
                before = {"mcpServers": direct}
                result, path = self.run_mcp_writer(directory, before)
                self.assertNotEqual(result.returncode, 0, "a direct Work IQ server must be refused, not merged")
                self.assertIn("direct Work IQ server", result.stderr)
                self.assertEqual(json.loads(path.read_text(encoding="utf-8")), before)


class RepositoryWiringTests(unittest.TestCase):
    def test_docs_exist_with_the_sections_other_pages_link_to(self):
        readme = read("README.md")
        runbooks = read("RUNBOOKS.md")
        for heading in ("## Phase 0 verification", "## Pilot checklist", "## What CI validates and what needs a tenant"):
            self.assertIn(heading, readme)
        for heading in ("## Re-authentication", "## Rotation", "## Revocation", "## Manager change", "## Restore",
                        "## Upgrade", "## Decommission"):
            self.assertIn(heading, runbooks)
        for page in (readme, runbooks):
            for link in re.findall(r"\]\(([^)]+)\)", page):
                if link.startswith(("http", "mailto:")):
                    continue
                target = link.split("#", 1)[0]
                if target:
                    self.assertTrue((DEPLOY / target).resolve().exists(), link)

    def test_private_copies_are_ignored_and_line_endings_pinned(self):
        gitignore = (ROOT / ".gitignore").read_text(encoding="utf-8").splitlines()
        self.assertIn("deploy/**/*.local.*", gitignore)
        self.assertIn("*.bicepparam.local", gitignore)
        attributes = (ROOT / ".gitattributes").read_text(encoding="utf-8")
        self.assertRegex(attributes, r"(?m)^deploy/\*\*\s+text eol=lf$")

    def test_packagers_exclude_deploy_and_ci_checks_the_templates(self):
        self.assertIn("grep -vE '^(\\.github|packaging|deploy)/'", (ROOT / "packaging/macos/build-pkg.sh").read_text(encoding="utf-8"))
        self.assertIn("$excludeDirs = @('.github', 'packaging', 'deploy')",
                      (ROOT / "packaging/windows/build-exe.ps1").read_text(encoding="utf-8"))
        self.assertIn("`deploy/`", (ROOT / "packaging/README.md").read_text(encoding="utf-8"))
        ci = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        self.assertIn("\n  deploy-templates:\n", ci)
        for name in SCRIPT_NAMES:
            self.assertIn("bash -n deploy/azure/scripts/" + name, ci)
        self.assertIn("systemd-analyze verify", ci)
        self.assertIn("az bicep install --version v", ci)


if __name__ == "__main__":
    unittest.main()
