"""Answers the C# engine tests' questions with the Python engine, skills, parity and statecmd modules.

Reads one JSON request on stdin and writes one JSON answer on stdout. The C# tests compute the same values with
Margo.Cloud.Engine and compare the two as text, so the Python modules stay the specification. Nothing here reaches a
network, a model or real state: the guard gets a recording fake transport, and the state tool never finds a program
to run (a case that passes every check ends in "could not start the command").

    python3 -B engine_cases.py REPO_ROOT < request.json
"""

import argparse
import asyncio
import importlib
import json
import sys
from datetime import datetime
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve()
sys.path.insert(0, str(ROOT / "skills" / "chief-of-staff" / "scripts"))
sys.path.insert(0, str(ROOT / "autopilot" / "src"))
autopilot = importlib.import_module("margo_autopilot")
engine = importlib.import_module("margo_autopilot.engine")
parity = importlib.import_module("margo_autopilot.parity")
skills = importlib.import_module("margo_autopilot.skills")
statecmd = importlib.import_module("margo_autopilot.statecmd")

BINDING = autopilot.InstanceBinding("agent-identity-1", "margo-user-1", "principal-1", "chat-principal-1")


class Tokens:
    def token_for(self, subject):
        return subject + "-token"


class Transport:
    endpoint = autopilot.WORKIQ_MCP_URL

    def __init__(self):
        self.calls = []

    def call(self, tool, arguments, *, bearer):
        self.calls.append([tool, arguments, bearer])
        return {"statusCode": 200, "data": {"value": []}}


class Office:
    def __init__(self, broken):
        self.broken = broken

    def pending(self):
        if self.broken:
            raise RuntimeError("fictional queue failure")
        return [{"from": "Rafa", "trust": "untrusted"}]


def library_answers(spec):
    library = skills.SkillLibrary(spec["root"], spec.get("preferences"))
    return {
        "persona": library.persona(),
        "skills": [[s["name"], s["description"], s["directory"].name] for s in library.skills()],
        "index": library.index(),
        "files": [library.files(name) for name in spec.get("names", [])],
        "load": [library.load(name) for name in spec.get("names", [])],
        "read": [library.read(name, path) for name, path in spec.get("reads", [])],
    }


def dispatch_answers(cases, library):
    async def run():
        answers = []
        for name, raw, mode, office in cases:
            transport = Transport()
            guard = autopilot.WorkIQGuard(BINDING, Tokens(), transport)
            office_reader = None if office is None else Office(office == "broken")
            result = await engine.Engine._dispatch(None, name, raw, mode, guard, office_reader, library, None)
            answers.append({"result": result, "sent": transport.calls})
        return answers

    return asyncio.run(run())


def state_answers(cases, preferences):
    answers = []
    for arguments, mode in cases:
        receipts = []
        state = statecmd.StateCommands(scripts_dir="/nonexistent-margo-scripts", account=BINDING.account,
                                       state_root="/nonexistent-margo-state", preferences=preferences,
                                       recorder=receipts.append, python="/nonexistent-margo-python")
        try:
            result = state.run(arguments, mode=mode)
        except Exception as exc:  # the C# port raises the matching Python exception type
            result = {"raised": type(exc).__name__, "message": str(exc)}
        for receipt in receipts:
            receipt.pop("at", None)
            receipt.pop("ms", None)
        answers.append({"result": result, "receipts": receipts})
    return answers


def state_exec_answers(spec):
    """Runs classified commands against fake scripts (spec["scripts_dir"]) with this Python, as the C# test does."""
    answers = []
    for arguments, mode in spec["cases"]:
        receipts = []
        state = statecmd.StateCommands(scripts_dir=spec["scripts_dir"], account=BINDING.account,
                                       state_root="/nonexistent-margo-state", recorder=receipts.append,
                                       timeout=spec.get("timeout", statecmd.TIMEOUT_SECONDS), python=sys.executable)
        try:
            result = state.run(arguments, mode=mode)
        except Exception as exc:
            result = {"raised": type(exc).__name__, "message": str(exc)}
        for receipt in receipts:
            receipt.pop("at", None)
            receipt.pop("ms", None)
        answers.append({"result": result, "receipts": receipts})
    return answers


class ScriptedModel:
    """Scripted Responses API: each create() returns the next scripted response (a copy)."""

    def __init__(self, script):
        self.script, self.requests = list(script), []
        self.responses = self

    async def create(self, **kwargs):
        self.requests.append({key: value for key, value in kwargs.items() if key not in ("instructions", "tools")})
        return json.loads(json.dumps(self.script.pop(0)))


def run_answers(cases):
    answers = []
    for case in cases:
        model = ScriptedModel(case["script"])
        transport = Transport()
        guard = autopilot.WorkIQGuard(BINDING, Tokens(), transport)
        runner = engine.Engine(model=model, deployment="fictional-model", repo_root=ROOT, max_rounds=case["max_rounds"])
        try:
            result = asyncio.run(runner.run(case["text"], mode=case["mode"], guard=guard, office=Office(False),
                                            previous_response_id=case.get("previous"),
                                            now=datetime.fromisoformat("2026-10-17T08:00:00+00:00")))
        except (AttributeError, TypeError) as exc:
            result = {"raised": "malformed"}
        answers.append({"result": result, "requests": model.requests, "sent": transport.calls})
    return answers


def subcommands(script):
    """The subcommands a stateful script's argparse parser accepts (as tests/test_autopilot_parity.py reads them)."""
    parser = importlib.import_module(script[:-3]).parser()
    actions = [action for action in parser._actions if isinstance(action, argparse._SubParsersAction)]
    return sorted(actions[0].choices) if actions else []


def main():
    request = json.loads(sys.stdin.read())
    answer = {
        "function_tools": {mode: engine.function_tools(mode) for mode in request.get("modes", [])},
        "summary": parity.summary(),
        "not_hosted": {"programs": parity.NOT_HOSTED_PROGRAMS, "scripts": parity.NOT_HOSTED_SCRIPTS,
                       "workiq": parity.NOT_HOSTED_WORKIQ},
        "commands": {script: {klass: sorted(c for c in commands if c is not None) if None not in commands else [None]
                              for klass, commands in classes.items()}
                     for script, classes in statecmd.COMMANDS.items()},
        "stateful": sorted(statecmd.STATEFUL),
        "not_hosted_commands": [[s, c, r] for (s, c), r in sorted(statecmd.NOT_HOSTED_COMMANDS.items())],
        "limits": [statecmd.MAX_ARGS, statecmd.MAX_ARG, statecmd.MAX_ARGS_TOTAL, statecmd.MAX_INPUT, statecmd.MAX_FILES,
                   statecmd.MAX_FILES_TOTAL, statecmd.MAX_OUTPUT, statecmd.TIMEOUT_SECONDS,
                   list(statecmd.FORBIDDEN_OPTIONS), list(statecmd.ENV_PASSTHROUGH),
                   {mode: sorted(classes) for mode, classes in statecmd.MODE_CLASSES.items()}],
        "engine": [engine.PREFIX, engine.MAX_OUTPUT_CHARS, engine.STOPPED, engine.HOSTED_ADDENDUM, engine.STATE_TOOL,
                   skills.MAX_FILE_CHARS, list(skills.PRIVATE_PREFERENCES), list(skills.LEDGER_ONLY),
                   sorted(skills.EXCLUDED_DIRS)],
        "relative": [skills.SkillLibrary._relative(path) for path in request.get("relative", [])],
        "split": [list(skills.split_front_matter(text)) for text in request.get("split", [])],
        "clip": [engine._clip(value) for value in request.get("clip", [])],
    }
    for item in request.get("instructions", []):
        answer.setdefault("instructions", []).append(engine.load_instructions(
            item.get("root", str(ROOT)), timezone=item["timezone"], now=datetime.fromisoformat(item["now"]),
            library=skills.SkillLibrary(item.get("root", str(ROOT)), item.get("preferences"))))
    if request.get("subcommands"):
        answer["subcommands"] = {script: subcommands(script) for script in sorted(statecmd.STATEFUL)}
    if "library" in request:
        answer["library"] = library_answers(request["library"])
        library = skills.SkillLibrary(request["library"]["root"], request["library"].get("preferences"))
    else:
        library = skills.SkillLibrary(ROOT)
    answer["dispatch"] = dispatch_answers(request.get("dispatch", []), library)
    answer["state"] = state_answers(request.get("state", []), request.get("state_preferences"))
    if "runs" in request:
        answer["runs"] = run_answers(request["runs"])
    if "state_exec" in request:
        answer["state_exec"] = state_exec_answers(request["state_exec"])
    sys.stdout.write(json.dumps(answer, ensure_ascii=True))


if __name__ == "__main__":
    main()
