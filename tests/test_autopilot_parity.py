"""Hosted Margo is desktop Margo: the same persona and skills, with every difference named.

These checks fail when the hosted engine would drift from what desktop Margo uses: a persona or
skill not loaded word for word, a skill file it cannot read, a script subcommand nobody
classified, or a program, script or Work IQ tool a procedure uses that is neither hosted nor
listed as not hosted yet (with a reason) in ``autopilot/src/margo_autopilot/parity.py``.
"""

import argparse
import importlib
import re
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = ROOT / "skills" / "chief-of-staff" / "scripts"
sys.path.insert(0, str(SCRIPTS))
sys.path.insert(0, str(ROOT / "autopilot" / "src"))
engine = importlib.import_module("margo_autopilot.engine")
guard = importlib.import_module("margo_autopilot.guard")
parity = importlib.import_module("margo_autopilot.parity")
skills = importlib.import_module("margo_autopilot.skills")
statecmd = importlib.import_module("margo_autopilot.statecmd")

PROCEDURE_ROOTS = ("skills", "automations", "agents")
# Programs a desktop procedure might run. Any of these in a command position must be hosted or
# listed in parity.NOT_HOSTED_PROGRAMS; python3 is how the hosted state tool runs the scripts.
PROGRAMS = {"gh", "az", "git", "curl", "jq", "npm", "npx", "node", "pip", "pip3", "docker", "kubectl", "pwsh",
            "bash", "sh", "zsh", "wget", "ssh", "osascript", "open", "pbcopy", "powershell"}
COMMAND_POSITION = re.compile(r"(?:^|[|;&(]|\$\(|\$ )\s*(?:[A-Z_][A-Z0-9_]*=\S+\s+)*([a-z][a-z0-9]*)\b(?=\s|$)",
                              re.MULTILINE)


def procedures():
    for top in PROCEDURE_ROOTS:
        for path in sorted((ROOT / top).rglob("*.md")):
            if "state" not in path.relative_to(ROOT).parts:
                yield path, path.read_text(encoding="utf-8")


def code_spans(text):
    """Fenced blocks and inline code: the places a procedure tells the agent to run something."""
    fenced = re.findall(r"```[^\n]*\n(.*?)```", text, re.DOTALL)
    stripped = re.sub(r"```.*?```", "", text, flags=re.DOTALL)
    return fenced + re.findall(r"`([^`\n]+)`", stripped)


def subcommands(script):
    module = importlib.import_module(script[:-3])
    parser = module.parser()
    actions = [action for action in parser._actions if isinstance(action, argparse._SubParsersAction)]
    return set(actions[0].choices) if actions else set()


class PersonaAndSkillsAreUnchanged(unittest.TestCase):
    def setUp(self):
        self.library = skills.SkillLibrary(ROOT)
        self.instructions = engine.load_instructions(ROOT, library=self.library)

    def test_the_persona_is_loaded_word_for_word(self):
        persona = skills.split_front_matter((ROOT / "agents" / "margo.agent.md").read_text(encoding="utf-8"))[1]
        self.assertTrue(persona.strip())
        self.assertTrue(self.instructions.startswith(persona))

    def test_every_skill_is_listed_and_loads_word_for_word(self):
        directories = sorted(path.parent.name for path in (ROOT / "skills").glob("*/SKILL.md"))
        self.assertEqual(sorted(skill["directory"].name for skill in self.library.skills()), directories)
        self.assertGreaterEqual(len(directories), 2)
        for skill in self.library.skills():
            with self.subTest(skill=skill["name"]):
                self.assertIn("- **%s**: %s" % (skill["name"], skill["description"]), self.instructions)
                self.assertTrue(skill["description"])
                text = (skill["directory"] / "SKILL.md").read_text(encoding="utf-8")
                self.assertEqual(self.library.load(skill["name"])["text"], skills.split_front_matter(text)[1])

    def test_every_skill_file_is_reachable(self):
        private = {skills.PRIVATE_PREFERENCES, skills.LEDGER_ONLY}
        for skill in self.library.skills():
            for path in sorted(skill["directory"].rglob("*.md")):
                relative = path.relative_to(skill["directory"]).as_posix()
                if relative.split("/")[0] in skills.EXCLUDED_DIRS or (skill["name"], relative) in private:
                    continue
                with self.subTest(skill=skill["name"], path=relative):
                    result = self.library.read(skill["name"], relative)
                    self.assertEqual(result["status"], "ok")
                    self.assertEqual(result["text"], path.read_text(encoding="utf-8")[:skills.MAX_FILE_CHARS])

    def test_the_image_carries_the_persona_and_every_skill(self):
        dockerfile = (ROOT / "autopilot" / "Dockerfile").read_text(encoding="utf-8")
        self.assertIn("COPY agents/ /app/agents/", dockerfile)
        self.assertIn("COPY skills/ /app/skills/", dockerfile)


class EveryDifferenceIsNamed(unittest.TestCase):
    def test_every_script_a_procedure_names_is_hosted_or_listed(self):
        named = set()
        for _, text in procedures():
            named.update(re.findall(r"\b([a-z0-9_]+\.py)\b", text))
        self.assertTrue(named)
        for script in sorted(named):
            with self.subTest(script=script):
                self.assertTrue(script in statecmd.COMMANDS or script in parity.NOT_HOSTED_SCRIPTS,
                                "%s is used by a procedure but neither hosted nor listed as not hosted" % script)
        for script, reason in parity.NOT_HOSTED_SCRIPTS.items():
            self.assertTrue(reason.strip())
            self.assertNotIn(script, statecmd.COMMANDS)

    def test_every_subcommand_of_a_hosted_script_is_classified(self):
        for script in sorted(statecmd.STATEFUL):
            available = subcommands(script)
            classified = {command for commands in statecmd.COMMANDS[script].values() for command in commands}
            listed = {command for (name, command) in statecmd.NOT_HOSTED_COMMANDS if name == script}
            with self.subTest(script=script):
                self.assertEqual(sorted(available - classified - listed), [],
                                 "new subcommands must be classified in statecmd.COMMANDS or NOT_HOSTED_COMMANDS")
                self.assertEqual(sorted((classified | listed) - available), [], "classified commands that no longer exist")
                self.assertEqual(classified & listed, set())
        for script in set(statecmd.COMMANDS) - statecmd.STATEFUL:
            self.assertEqual(statecmd.COMMANDS[script], {statecmd.READ: {None}})
            self.assertTrue((SCRIPTS / script).is_file())

    def test_every_program_a_procedure_runs_is_listed(self):
        seen = {}
        for path, text in procedures():
            for span in code_spans(text):
                for program in COMMAND_POSITION.findall(span):
                    if program in PROGRAMS:
                        seen.setdefault(program, path.relative_to(ROOT).as_posix())
        self.assertIn("gh", seen)  # the scan itself must keep finding real commands
        for program, where in sorted(seen.items()):
            with self.subTest(program=program, where=where):
                self.assertIn(program, parity.NOT_HOSTED_PROGRAMS,
                              "%s runs %s, which hosted Margo cannot run; list it with a reason" % (where, program))

    def test_every_work_iq_tool_a_procedure_names_is_offered_or_listed(self):
        offered = {tool["name"] for tool in engine.function_tools(guard.ATTENDED)}
        named = set()
        for _, text in procedures():
            named.update(re.findall(r"\bworkiq-([a-z_]+)", text))
        self.assertIn("fetch", named)
        for tool in sorted(named):
            with self.subTest(tool=tool):
                self.assertTrue(engine.PREFIX + tool in offered or tool in parity.NOT_HOSTED_WORKIQ,
                                "workiq-%s is used by a procedure but neither offered nor listed" % tool)

    def test_the_model_is_told_everything_that_is_missing(self):
        instructions = engine.load_instructions(ROOT)
        for name in list(parity.NOT_HOSTED_PROGRAMS) + list(parity.NOT_HOSTED_SCRIPTS):
            self.assertIn("`%s`" % name, instructions)
        for script, command in statecmd.NOT_HOSTED_COMMANDS:
            self.assertIn("`%s %s`" % (script, command), instructions)
        for tool in parity.NOT_HOSTED_WORKIQ:
            self.assertIn("`workiq-%s`" % tool, instructions)


if __name__ == "__main__":
    unittest.main()
