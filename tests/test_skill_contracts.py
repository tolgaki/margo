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

    def test_router_references_exist_and_new_procedures_are_routed(self):
        import re
        skill = REPO / "skills/chief-of-staff"
        text = (skill / "SKILL.md").read_text(encoding="utf-8")
        rows = "\n".join(line for line in text.splitlines() if line.startswith("| **"))
        for ref in set(re.findall(r"`references/([a-z0-9-]+\.md)", rows)):
            with self.subTest(ref=ref):
                self.assertTrue((skill / "references" / ref).exists())
        for ref in ("delta.md", "people.md", "publishing.md", "sharepoint.md", "copilot-agents.md"):
            self.assertIn("`references/" + ref + "`", text)

    def test_tool_table_and_dependency_statement_are_accurate(self):
        text = (REPO / "skills/chief-of-staff/SKILL.md").read_text(encoding="utf-8")
        self.assertNotIn("No local package is needed", text)
        self.assertIn("scripts/m365_files.py", text)
        for tool in ("workiq-fetch_blob", "workiq-list_agents", "workiq-get_schema"):
            self.assertIn("| `" + tool, text)
        self.assertIn("10–30 s", text)
        self.assertIn("@odata.nextLink", text)

    def test_delta_recipe_uses_only_existing_proactive_state_commands(self):
        import importlib.util
        import re
        import sys
        scripts = REPO / "skills/chief-of-staff/scripts"
        sys.path.insert(0, str(scripts))
        try:
            spec = importlib.util.spec_from_file_location(
                "_contract_proactive_state", scripts / "proactive_state.py")
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            parser = module.parser()
        finally:
            sys.path.remove(str(scripts))
        commands = set()
        for action in parser._actions:
            if hasattr(action, "choices") and isinstance(action.choices, dict):
                commands.update(action.choices)
        recipe = (REPO / "skills/chief-of-staff/references/delta.md").read_text(encoding="utf-8")
        used = set(re.findall(r"proactive_state\.py ([a-z-]+)", recipe))
        used |= set(re.findall(r"`(coverage-[a-z]+)", recipe))
        self.assertTrue({"coverage-start", "coverage-resume", "coverage-page", "coverage-finish"} <= used)
        self.assertLessEqual(used, commands)
        self.assertIn("410", recipe)
        self.assertIn("expired_token", recipe)

    def test_unattended_runs_do_not_attempt_availability_actions(self):
        calendar = (REPO / "skills/chief-of-staff/references/calendar.md").read_text(encoding="utf-8")
        self.assertIn("availability not checked (needs an attended run)", calendar)
        wrapper = (REPO / "tools/margo-scheduled.sh").read_text(encoding="utf-8")
        self.assertIn('DENY_TOOLS="do_action create_entity update_entity delete_entity"', wrapper)

    def test_references_use_fictional_hosts(self):
        for path in sorted((REPO / "skills/chief-of-staff/references").glob("*.md")):
            with self.subTest(path=path.name):
                self.assertNotIn("sharepoint-df", path.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
