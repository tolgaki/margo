#!/usr/bin/env python3
"""Validate synthetic journey coverage without claiming model behavior."""

import argparse
import ast
import importlib.util
import json
import sys
from pathlib import Path
from feature_catalog import parse_canvas_groups
from journey_json import load as load_contract_json


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SCENARIOS = ROOT / "tests/fixtures/journeys/scenarios.json"
DEFAULT_SOURCES = ROOT / "tests/fixtures/journeys/sources.json"
DEFAULT_EVALS = ROOT / "evals/scenarios-v1.json"
FEATURE_IDS = (
    "setup", "account-storage", "personalization", "daily-brief", "catch-up", "end-of-day",
    "week-ahead", "inbox-triage", "teams-triage", "drafting", "executive-followup",
    "calendar-scheduling", "calendar-reschedule", "calendar-rsvp", "calendar-hygiene",
    "meeting-prep", "meeting-debrief", "meeting-lifecycle", "one-on-ones", "relationships",
    "document-queue", "file-download", "file-copy", "file-upload", "file-sharing",
    "commitments", "follow-through", "action-desk", "approval-execution", "outcomes",
    "capacity", "feedback", "rules", "do-not-learn", "work-products", "artifact-delivery",
    "github-reviews", "ado-work-items", "engage", "teams-feedback", "decision-extraction",
    "decision-answer", "decision-supersession", "decision-digest", "decision-audit",
    "automation-morning", "automation-eod", "automation-week-ahead", "automation-commitments",
    "automation-hourly", "automation-ambient", "source-coverage", "output-delivery", "doctor",
    "action-desk-canvas", "upgrade-migration", "uninstall", "containers", "memory-capture",
    "memory-retrieval", "memory-control", "memory-learning", "memory-trends", "memory-export",
    "memory-canvas", "task-progress", "task-recovery", "dream",
)
COVERAGE_KINDS = {"runtime", "procedure-contract", "model-evaluation"}


class ContractError(ValueError):
    pass


def load_json(path):
    return load_contract_json(path, ContractError)


def _strings(value, label, minimum=1):
    if (not isinstance(value, list) or len(value) < minimum
            or any(not isinstance(item, str) or not item.strip() for item in value)):
        raise ContractError("%s must contain at least %d nonempty strings" % (label, minimum))
    return value


def _fixture(source_data, reference):
    prefix = "sources.json#"
    if not isinstance(reference, str) or not reference.startswith(prefix):
        raise ContractError("source_fixture must use sources.json#<fixture-id>")
    identity = reference[len(prefix):]
    fixtures = source_data.get("fixtures", {})
    if identity not in fixtures:
        raise ContractError("unknown source fixture: " + identity)
    fixture = fixtures[identity]
    if not isinstance(fixture, dict) or not fixture.get("version") or not fixture.get("sources"):
        raise ContractError("source fixture %s needs a version and sources" % identity)
    return fixture


def _selector_exists(selector, root):
    if not isinstance(selector, str) or selector.count(".") < 2:
        return False, "selector must be module.Class.test_method"
    module, class_name, method = selector.rsplit(".", 2)
    if not method.startswith("test_"):
        return False, "runtime selector must name a test method"
    relative = Path(*module.split(".")).with_suffix(".py")
    candidates = [root / relative, root / "tests" / relative]
    path = next((candidate for candidate in candidates if candidate.is_file()
                 and candidate.resolve().is_relative_to((root / "tests").resolve())), None)
    if path is None:
        return False, "selector module does not exist: " + module
    try:
        tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
    except (OSError, SyntaxError) as exc:
        return False, "cannot parse selector module: %s" % exc
    for node in tree.body:
        if isinstance(node, ast.ClassDef) and node.name == class_name:
            if any(isinstance(child, (ast.FunctionDef, ast.AsyncFunctionDef))
                   and child.name == method for child in node.body):
                return True, None
            return False, "selector method does not exist: " + selector
    return False, "selector class does not exist: " + selector


def _cli_commands(reference, root):
    if not isinstance(reference, dict) or set(reference) != {"path", "commands"}:
        raise ContractError("cli_refs entries require path and commands")
    commands = _strings(reference["commands"], "cli_refs.commands")
    if (not isinstance(reference["path"], str) or Path(reference["path"]).is_absolute()
            or ".." in Path(reference["path"]).parts):
        raise ContractError("CLI reference must remain inside the repository")
    path = root / reference["path"]
    allowed = {"work_state.py", "memory_state.py", "task_state.py", "proactive_state.py"}
    if (path.name not in allowed or
            path.resolve().parent != (root / "skills/chief-of-staff/scripts").resolve()):
        raise ContractError("CLI parser is not in the safe introspection allowlist")
    if not path.is_file():
        raise ContractError("CLI parser path does not exist: " + reference["path"])
    module_name = "_journey_contract_cli_" + str(abs(hash(str(path))))
    added = str(path.parent) not in sys.path
    if added:
        sys.path.insert(0, str(path.parent))
    try:
        spec = importlib.util.spec_from_file_location(module_name, path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        parser = module.parser()
        choices = set()
        for action in parser._actions:
            if isinstance(action, argparse._SubParsersAction):
                choices.update(action.choices)
        missing = sorted(set(commands) - choices)
        if missing:
            raise ContractError("CLI parser is missing commands: " + ", ".join(missing))
    except (AttributeError, ImportError, OSError, TypeError) as exc:
        raise ContractError("cannot introspect CLI parser %s: %s" % (reference["path"], exc))
    finally:
        sys.modules.pop(module_name, None)
        if added:
            sys.path.remove(str(path.parent))


def _canvas_actions(reference, root):
    if not isinstance(reference, dict) or set(reference) != {"id", "actions"}:
        raise ContractError("canvas_refs entries require id and actions")
    identity = reference["id"]
    if not isinstance(identity, str) or not identity.strip():
        raise ContractError("canvas_refs.id must be nonempty")
    actions = _strings(reference["actions"], "canvas_refs.actions")
    for path in sorted((root / ".github/extensions").glob("*/extension.mjs")):
        groups = parse_canvas_groups(path.read_text(encoding="utf-8"))
        if identity not in groups:
            continue
        missing = [action for action in actions if action not in groups[identity]]
        if missing:
            raise ContractError("%s is missing canvas actions: %s"
                                % (identity, ", ".join(missing)))
        return
    raise ContractError("canvas declaration does not exist: " + identity)


def validate_contract(scenarios, sources, eval_scenarios, root=ROOT):
    errors = []
    limitations = [
        "Coverage metadata proves mapping and fixture integrity, not end-to-end model correctness.",
        "Procedure contracts validate required evidence and forbidden effects, not generated prose.",
        "Model-evaluation scenarios require imported host/model traces; absent traces are not evaluated.",
    ]
    if not isinstance(scenarios, dict) or scenarios.get("schema_version") != 1:
        errors.append("journey contract schema_version must equal 1")
        rows = []
    else:
        rows = scenarios.get("scenarios")
        if not isinstance(rows, list):
            errors.append("scenarios must be a list")
            rows = []
    if not isinstance(sources, dict) or sources.get("schema_version") != 1:
        errors.append("source fixture schema_version must equal 1")
    eval_by_id = {}
    if isinstance(eval_scenarios, dict) and eval_scenarios.get("schema_version") == 1:
        for row in eval_scenarios.get("scenarios", []):
            if isinstance(row, dict) and isinstance(row.get("id"), str):
                if row["id"] in eval_by_id:
                    errors.append("duplicate evaluation scenario: " + row["id"])
                eval_by_id[row["id"]] = row
    else:
        errors.append("evaluation scenario schema_version must equal 1")

    seen = set()
    counts = {kind: 0 for kind in sorted(COVERAGE_KINDS)}
    for index, row in enumerate(rows):
        label = "scenario[%d]" % index
        if not isinstance(row, dict):
            errors.append(label + " must be an object")
            continue
        required = {
            "id", "feature_id", "title", "coverage_kind", "source_fixture", "routing",
            "expected", "evidence",
        }
        optional = {"model_scenario_id", "cli_refs", "canvas_refs"}
        if set(row) - required - optional:
            errors.append(label + " has unknown fields: " + ", ".join(sorted(set(row) - required - optional)))
        missing = required - set(row)
        if missing:
            errors.append(label + " is missing: " + ", ".join(sorted(missing)))
            continue
        identity = row["id"]
        if identity != row["feature_id"]:
            errors.append(label + " primary id must equal feature_id")
        if identity in seen:
            errors.append("duplicate feature scenario: " + str(identity))
        seen.add(identity)
        if identity not in FEATURE_IDS:
            errors.append("unknown feature id: " + str(identity))
        kind = row["coverage_kind"]
        if kind not in COVERAGE_KINDS:
            errors.append("%s has invalid coverage_kind" % label)
        else:
            counts[kind] += 1
        if not isinstance(row["title"], str) or not row["title"].strip():
            errors.append(label + " title must be nonempty")
        fixture = None
        try:
            fixture = _fixture(sources, row["source_fixture"])
        except ContractError as exc:
            errors.append("%s: %s" % (label, exc))
        routing = row["routing"]
        if not isinstance(routing, dict) or set(routing) != {"expected_skills", "forbidden_skills"}:
            errors.append(label + " routing requires expected_skills and forbidden_skills")
        else:
            try:
                _strings(routing["expected_skills"], label + ".routing.expected_skills")
                if not isinstance(routing["forbidden_skills"], list):
                    raise ContractError(label + ".routing.forbidden_skills must be a list")
                _strings(routing["forbidden_skills"], label + ".routing.forbidden_skills", minimum=0)
            except ContractError as exc:
                errors.append(str(exc))
        expected = row["expected"]
        if not isinstance(expected, dict) or set(expected) != {
                "user_visible_invariants", "forbidden_effects", "evidence_limitations"}:
            errors.append(label + " expected contract has the wrong fields")
        else:
            for field in expected:
                try:
                    _strings(expected[field], label + ".expected." + field)
                except ContractError as exc:
                    errors.append(str(exc))
        evidence = row["evidence"]
        if not isinstance(evidence, dict) or set(evidence) != {"selectors", "proves", "does_not_prove"}:
            errors.append(label + " evidence requires selectors, proves and does_not_prove")
            continue
        try:
            _strings(evidence["proves"], label + ".evidence.proves")
            _strings(evidence["does_not_prove"], label + ".evidence.does_not_prove")
        except ContractError as exc:
            errors.append(str(exc))
        selectors = evidence.get("selectors")
        if not isinstance(selectors, list):
            errors.append(label + ".evidence.selectors must be a list")
            selectors = []
        if kind == "runtime":
            if not selectors:
                errors.append(label + " runtime coverage requires a concrete unittest selector")
            for selector in selectors:
                exists, error = _selector_exists(selector, root)
                if not exists:
                    errors.append("%s: %s" % (label, error))
        elif selectors:
            errors.append(label + " non-runtime coverage cannot cite runtime selectors")
        model_id = row.get("model_scenario_id")
        if kind == "model-evaluation":
            if not model_id or model_id not in eval_by_id:
                errors.append(label + " must reference a versioned evaluation scenario")
            else:
                evaluation = eval_by_id[model_id]
                if evaluation.get("journey_feature_id") != identity:
                    errors.append(label + " evaluation scenario belongs to another feature")
                if fixture and evaluation.get("fixture_version") != fixture.get("version"):
                    errors.append(label + " evaluation fixture version differs from source_fixture")
        elif model_id is not None:
            errors.append(label + " model_scenario_id is only valid for model-evaluation")
        for field, checker in (("cli_refs", _cli_commands), ("canvas_refs", _canvas_actions)):
            references = row.get(field, [])
            if not isinstance(references, list):
                errors.append(label + "." + field + " must be a list")
                continue
            for reference in references:
                try:
                    checker(reference, root)
                except ContractError as exc:
                    errors.append("%s.%s: %s" % (label, field, exc))

    missing = sorted(set(FEATURE_IDS) - seen)
    extra = sorted(seen - set(FEATURE_IDS))
    if missing:
        errors.append("missing feature scenarios: " + ", ".join(missing))
    if extra:
        errors.append("unexpected feature scenarios: " + ", ".join(extra))
    if len(rows) != len(FEATURE_IDS):
        errors.append("expected %d scenarios, found %d" % (len(FEATURE_IDS), len(rows)))
    return {
        "schema_version": 1,
        "status": "valid" if not errors else "invalid",
        "feature_count": len(FEATURE_IDS),
        "scenario_count": len(rows),
        "coverage_by_kind": counts,
        "errors": errors,
        "evidence_limitations": limitations,
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="validate the journey and evaluation fixtures")
    parser.add_argument("--scenarios", default=str(DEFAULT_SCENARIOS))
    parser.add_argument("--sources", default=str(DEFAULT_SOURCES))
    parser.add_argument("--eval-scenarios", default=str(DEFAULT_EVALS))
    args = parser.parse_args(argv)
    if not args.check:
        parser.error("--check is required")
    try:
        report = validate_contract(
            load_json(args.scenarios), load_json(args.sources), load_json(args.eval_scenarios))
    except ContractError as exc:
        report = {"schema_version": 1, "status": "invalid", "errors": [str(exc)],
                  "evidence_limitations": ["Fixtures could not be loaded; no coverage was evaluated."]}
    json.dump(report, sys.stdout, indent=2, sort_keys=True)
    sys.stdout.write("\n")
    return 0 if report["status"] == "valid" else 1


if __name__ == "__main__":
    raise SystemExit(main())
