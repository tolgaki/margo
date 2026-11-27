"""Margo as a plugin for GitHub Copilot CLI and Claude Code: the manifests stay true to the repository."""

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def load(path):
    return json.loads((ROOT / path).read_text(encoding="utf-8"))


class PluginManifestTest(unittest.TestCase):
    def setUp(self):
        self.version = (ROOT / "VERSION").read_text(encoding="utf-8").strip()
        self.claude = load(".claude-plugin/plugin.json")
        self.copilot = load(".github/plugin/plugin.json")
        self.codex = load(".codex-plugin/plugin.json")
        self.markets = [load(".claude-plugin/marketplace.json"), load("marketplace.json")]

    def test_every_manifest_carries_the_repository_version(self):
        self.assertEqual(self.claude["version"], self.version)
        self.assertEqual(self.copilot["version"], self.version)
        self.assertEqual(self.codex["version"], self.version)
        for market in self.markets:
            self.assertEqual([plugin["version"] for plugin in market["plugins"]], [self.version])

    def test_both_hosts_get_the_same_agent_and_skills(self):
        for manifest in (self.claude, self.copilot):
            self.assertEqual(manifest["name"], "margo")
            self.assertEqual(manifest["skills"], "./skills/")
            self.assertEqual(manifest["agents"], "./agents/")
        self.assertEqual(self.claude["description"], self.copilot["description"])
        # Codex: skills only, as Microsoft's own Codex manifest declares (no agent entry).
        self.assertEqual((self.codex["name"], self.codex["skills"], self.codex["description"]),
                         ("margo", "./skills/", self.claude["description"]))
        self.assertNotIn("agents", self.codex)
        self.assertTrue((ROOT / "agents" / "margo.agent.md").is_file())
        for skill in ("chief-of-staff", "decision-log"):
            text = (ROOT / "skills" / skill / "SKILL.md").read_text(encoding="utf-8")
            self.assertTrue(text.startswith("---\nname: %s\n" % skill), skill)

    def test_the_two_marketplace_files_agree_and_point_at_the_repository_root(self):
        self.assertEqual(self.markets[0], self.markets[1])
        (entry,) = self.markets[0]["plugins"]
        self.assertEqual((entry["name"], entry["source"]), ("margo", "./"))
        self.assertEqual(entry["description"], self.claude["description"])

    def test_personal_files_live_outside_the_plugin(self):
        # A plugin update replaces the plugin's folder: Margo reads personal copies from ~/.copilot/margo first.
        skill = (ROOT / "skills" / "chief-of-staff" / "SKILL.md").read_text(encoding="utf-8")
        self.assertIn("~/.copilot/margo/preferences.md", skill)
        decisions = (ROOT / "skills" / "decision-log" / "SKILL.md").read_text(encoding="utf-8")
        self.assertIn("~/.copilot/margo/decision-log-config.md", decisions)
        for reference in (ROOT / "skills").rglob("*.md"):
            self.assertNotIn("python3 ~/.copilot/skills/", reference.read_text(encoding="utf-8"), reference)


if __name__ == "__main__":
    unittest.main()
