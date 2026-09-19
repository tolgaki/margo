"""Static agent/skill contracts; these do not evaluate a model's routing."""

from pathlib import Path
import unittest


REPO = Path(__file__).resolve().parents[1]


class SkillContractTests(unittest.TestCase):
    def test_agent_and_skill_names_have_separate_namespaces(self):
        agent = REPO / "agents/margo.agent.md"
        self.assertRegex(agent.read_text(encoding="utf-8"), r"(?m)^name: margo$")
        self.assertFalse((REPO / "agents/chief-of-staff.agent.md").exists())
        self.assertFalse((REPO / "skills/margo").exists())
        for path in sorted((REPO / "skills").glob("*/SKILL.md")):
            with self.subTest(skill=path.parent.name):
                text = path.read_text(encoding="utf-8")
                self.assertTrue(text.startswith("---\n"))
                frontmatter, body = text[4:].split("\n---\n", 1)
                self.assertIn("name: " + path.parent.name, frontmatter.splitlines())
                self.assertNotRegex(frontmatter, r"(?i)\bmargo\b")
                self.assertNotRegex(body, r"(?im)^You are\b")

    def test_playbook_routes_by_task_without_assigning_persona(self):
        text = (REPO / "skills/chief-of-staff/SKILL.md").read_text(encoding="utf-8")
        frontmatter = text[4:].split("\n---\n", 1)[0]
        for trigger in ("brief me", "triage my inbox", "prep me for my 2pm",
                        "what PRs need me"):
            with self.subTest(trigger=trigger):
                self.assertIn("'" + trigger + "'", frontmatter)
        self.assertIn("Trigger on the requested routine, not an assistant name or greeting.",
                      frontmatter)
        self.assertIn("Loading it does not select, rename, or impersonate an agent.", text)
        self.assertIn("agent selection, and unrelated coding or general questions", text)
        self.assertIn("**procedure, not personality**", text)
        self.assertIn("**Drafts are always in the user's voice**", text)
        self.assertIn("default session does not introduce a persona.", text)

    def test_margo_loads_procedures_without_restricting_general_capability(self):
        text = (REPO / "agents/margo.agent.md").read_text(encoding="utf-8")
        self.assertIn("You are **Margo**", text)
        self.assertIn("code, shell, search, sessions, PRs, files", text)
        self.assertIn("For a request matching a `chief-of-staff` routine", text)
        self.assertIn("`chief-of-staff` skill **first**", text)
        self.assertIn("without loading the playbook solely because the user used your name", text)
        self.assertIn("`copilot --agent margo` or its agent picker", text)
        self.assertIn("loading `chief-of-staff` does not create a Margo persona", text)


if __name__ == "__main__":
    unittest.main()
