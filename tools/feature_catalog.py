#!/usr/bin/env python3
"""Validate docs/feature-catalog.json and generate its bounded navigational sections.

Standard library only. This script does not read a mailbox, call Work IQ, or perform
any network access — it only inspects files already in this checkout.

    python3 tools/feature_catalog.py               # validate + check generated sections (default)
    python3 tools/feature_catalog.py --check        # same as above, explicit
    python3 tools/feature_catalog.py --write         # validate, then (re)write generated sections

Validation covers: catalog schema and the exact 67-feature-ID set, mandatory field shapes,
enum values, planned-status misuse, that every source/guide/router/automation/CLI/canvas
reference actually exists, that guide anchors resolve to a real heading, that every skill
router row and every automation manifest is referenced by at least one feature, that every
shipped CLI subcommand group and registered canvas id/action is covered by the catalog (not
just referenced correctly, but not silently dropped either), and that catalog scenario IDs
and declared maturity are consistent with the required tests/fixtures/journeys/scenarios.json.
Scheduled features must identify their shipped automation manifests.

This script never executes a Work IQ call, a data-access CLI subcommand, or JavaScript; it
only imports Python argparse-builder functions (which build a parser object and touch no
data) and statically parses the canvas extension's source text.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import re
import sys
from pathlib import Path
from journey_json import load as load_contract_json

ROOT = Path(__file__).resolve().parent.parent
CATALOG_PATH = ROOT / "docs" / "feature-catalog.json"
FEATURES_DOC = ROOT / "docs" / "features.md"
HOWTO_INDEX = ROOT / "docs" / "how-to" / "README.md"
AUTOMATIONS_DIR = ROOT / "automations"
SCENARIO_MANIFEST = ROOT / "tests" / "fixtures" / "journeys" / "scenarios.json"
EXTENSION_FILE = ROOT / ".github" / "extensions" / "margo-action-desk" / "extension.mjs"
ROUTER_FILES = [
    ROOT / "skills" / "chief-of-staff" / "SKILL.md",
    ROOT / "skills" / "decision-log" / "SKILL.md",
]

FEATURES_MARKERS = ("<!-- BEGIN GENERATED: feature-catalog -->", "<!-- END GENERATED: feature-catalog -->")
HOWTO_MARKERS = ("<!-- BEGIN GENERATED: feature-index -->", "<!-- END GENERATED: feature-index -->")

EXPECTED_FEATURE_IDS = [
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
    "action-desk-canvas", "upgrade-migration", "uninstall", "containers",
    "memory-capture", "memory-retrieval", "memory-control", "memory-learning", "memory-trends",
    "memory-export", "memory-canvas", "task-progress", "task-recovery",
]

AVAILABILITY_VALUES = {"implemented", "optional", "limited", "planned"}
IMPLEMENTATION_KIND_VALUES = {"runtime", "procedure", "design"}

REQUIRED_STRING_FIELDS = (
    "id", "name", "user_goal", "availability", "implementation_kind", "since_version",
    "approval", "maintainer_role",
)
REQUIRED_LIST_FIELDS = (
    "prerequisites", "surfaces", "sources", "router_refs", "automation_refs", "cli_refs",
    "canvas_refs", "scenario_ids", "limitations",
)

# Scripts whose parser() (or equivalent builder) can be called directly with no side effects:
# constructing an argparse.ArgumentParser touches no file, network or Work IQ tool.
DYNAMIC_CLI_MODULES = {
    "skills/chief-of-staff/scripts/work_state.py": "parser",
    "skills/chief-of-staff/scripts/proactive_state.py": "parser",
    "skills/chief-of-staff/scripts/memory_state.py": "parser",
    "skills/chief-of-staff/scripts/task_state.py": "parser",
    "skills/chief-of-staff/scripts/memory_encoder.py": "build_parser",
}
# Scripts that build their subcommands inline in main() with literal string names only;
# read statically (via ast) instead of importing, since they have no standalone builder.
STATIC_CLI_MODULES = [
    "skills/chief-of-staff/scripts/m365_files.py",
    "skills/chief-of-staff/scripts/margo_store.py",
]


class CatalogError(Exception):
    pass


def load_catalog(path=CATALOG_PATH):
    if not path.exists():
        raise CatalogError(f"catalog not found: {path}")
    return load_contract_json(path, CatalogError)


# ---------------------------------------------------------------------------
# Markdown heading / anchor handling (GitHub-style slugify)
# ---------------------------------------------------------------------------

def slugify(text):
    text = re.sub(r"[`*_]", "", text).strip().lower()
    text = re.sub(r"[^\w\- ]", "", text, flags=re.UNICODE)
    text = re.sub(r"\s+", "-", text)
    return text


def extract_headings(markdown_text):
    headings = []
    in_fence = False
    for line in markdown_text.splitlines():
        stripped = line.strip()
        if stripped.startswith("```") or stripped.startswith("~~~"):
            in_fence = not in_fence
            continue
        if in_fence:
            continue
        match = re.match(r"^(#{1,6})\s+(.*)$", line)
        if match:
            headings.append(match.group(2).strip())
    return headings


def anchors_for_file(path):
    """Return the set of valid GitHub-style anchors for every heading in path."""
    text = path.read_text(encoding="utf-8")
    seen = {}
    anchors = set()
    for heading in extract_headings(text):
        base = slugify(heading)
        count = seen.get(base, 0)
        anchor = base if count == 0 else f"{base}-{count}"
        seen[base] = count + 1
        anchors.add(anchor)
    return anchors


# ---------------------------------------------------------------------------
# Skill router table parsing
# ---------------------------------------------------------------------------

def parse_router_rows(skill_md_path, heading="## Core routines"):
    text = skill_md_path.read_text(encoding="utf-8")
    lines = text.splitlines()
    start = None
    for i, line in enumerate(lines):
        if line.strip() == heading:
            start = i + 1
            break
    if start is None:
        raise CatalogError(f"{skill_md_path}: no '{heading}' section found")
    rows = []
    seen_header = False
    for line in lines[start:]:
        if line.startswith("## "):
            break
        stripped = line.strip()
        if not stripped.startswith("|"):
            continue
        if re.match(r"^\|[\s:|-]+\|$", stripped):
            continue
        cells = [c.strip() for c in stripped.strip("|").split("|")]
        if not cells:
            continue
        if not seen_header:
            seen_header = True
            continue
        label = re.sub(r"\*\*", "", cells[0]).strip()
        if label:
            rows.append(label)
    if not rows:
        raise CatalogError(f"{skill_md_path}: '{heading}' table has no data rows")
    return rows


# ---------------------------------------------------------------------------
# CLI subcommand discovery
# ---------------------------------------------------------------------------

def _discover_dynamic_commands(rel_path, function_name, errors):
    abs_path = ROOT / rel_path
    if not abs_path.exists():
        errors.append(f"CLI module missing: {rel_path}")
        return None
    scripts_dir = str(abs_path.parent)
    inserted = scripts_dir not in sys.path
    if inserted:
        sys.path.insert(0, scripts_dir)
    module_name = "_feature_catalog_check_" + abs_path.stem
    try:
        spec = importlib.util.spec_from_file_location(module_name, abs_path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)  # only defines functions/classes; no data access
        builder = getattr(module, function_name, None)
        if builder is None:
            errors.append(f"{rel_path}: no {function_name}() to introspect")
            return None
        root_parser = builder()
        for action in root_parser._actions:  # noqa: SLF001 - intentional argparse introspection
            if isinstance(action, argparse._SubParsersAction):  # noqa: SLF001
                return set(action.choices.keys())
        errors.append(f"{rel_path}: {function_name}() has no subcommands")
        return None
    except (AttributeError, ImportError, OSError, SyntaxError, TypeError, ValueError, argparse.ArgumentError) as exc:
        errors.append(f"{rel_path}: failed to introspect CLI parser: {exc}")
        return None
    finally:
        sys.modules.pop(module_name, None)
        if inserted and scripts_dir in sys.path:
            sys.path.remove(scripts_dir)


def _discover_static_commands(rel_path, errors):
    import ast

    abs_path = ROOT / rel_path
    if not abs_path.exists():
        errors.append(f"CLI module missing: {rel_path}")
        return None
    try:
        tree = ast.parse(abs_path.read_text(encoding="utf-8"), filename=str(abs_path))
    except SyntaxError as exc:
        errors.append(f"{rel_path}: cannot parse for CLI introspection: {exc}")
        return None
    commands = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Call):
            func = node.func
            name = func.attr if isinstance(func, ast.Attribute) else getattr(func, "id", None)
            if name == "add_parser" and node.args:
                arg0 = node.args[0]
                if isinstance(arg0, ast.Constant) and isinstance(arg0.value, str):
                    commands.add(arg0.value)
    if not commands:
        errors.append(f"{rel_path}: no add_parser(...) subcommands found")
        return None
    return commands


def discover_cli_commands(errors):
    """Return {relative_path: set(commands)} for every known CLI module."""
    discovered = {}
    for rel_path, function_name in DYNAMIC_CLI_MODULES.items():
        commands = _discover_dynamic_commands(rel_path, function_name, errors)
        if commands is not None:
            discovered[rel_path] = commands
    for rel_path in STATIC_CLI_MODULES:
        commands = _discover_static_commands(rel_path, errors)
        if commands is not None:
            discovered[rel_path] = commands
    return discovered


# ---------------------------------------------------------------------------
# Canvas discovery (static parse of the extension's JS source; no execution)
# ---------------------------------------------------------------------------

def discover_canvas_groups(errors):
    if not EXTENSION_FILE.exists():
        errors.append(f"canvas extension missing: {EXTENSION_FILE}")
        return {}
    text = EXTENSION_FILE.read_text(encoding="utf-8")
    groups = {}
    parts = text.split("createCanvas({")
    for chunk in parts[1:]:
        id_match = re.search(r'\bid:\s*"([^"]+)"', chunk)
        if not id_match:
            continue
        canvas_id = id_match.group(1)
        actions_match = re.search(r"actions:\s*\[(.*?)\n\s*\],\n\s*open:", chunk, re.DOTALL)
        region = actions_match.group(1) if actions_match else chunk
        names = re.findall(r'\bname:\s*"([^"]+)"', region)
        groups.setdefault(canvas_id, set()).update(names)
    if not groups:
        errors.append(f"{EXTENSION_FILE}: no createCanvas({{ id: ... }}) declarations found")
    return groups


# ---------------------------------------------------------------------------
# Automation manifest discovery
# ---------------------------------------------------------------------------

def discover_automation_files():
    if not AUTOMATIONS_DIR.exists():
        return []
    return sorted(
        f"automations/{p.name}" for p in AUTOMATIONS_DIR.glob("*.md") if p.name != "README.md"
    )


# ---------------------------------------------------------------------------
# Scenario manifest (shared, separately owned; read-only, tolerate absence/invalidity)
# ---------------------------------------------------------------------------

def load_scenario_manifest(path=SCENARIO_MANIFEST):
    if not path.exists():
        return None, f"scenario manifest not found: {path}"
    try:
        data = load_contract_json(path, CatalogError)
    except CatalogError as exc:
        return None, f"scenario manifest is missing or not valid JSON ({path}): {exc}"
    if (not isinstance(data, dict) or type(data.get("schema_version")) is not int
            or data["schema_version"] != 1 or not isinstance(data.get("scenarios"), list)):
        return None, f"scenario manifest at {path} does not have a 'scenarios' array"
    return data, None


# ---------------------------------------------------------------------------
# Validation
# ---------------------------------------------------------------------------

def _ref_path(rel_path, errors, context):
    if (not isinstance(rel_path, str) or not rel_path or Path(rel_path).is_absolute()
            or ".." in Path(rel_path).parts):
        errors.append(f"{context}: reference must be a repository-relative path")
        return None
    path = ROOT / rel_path
    if not path.resolve().is_relative_to(ROOT.resolve()):
        errors.append(f"{context}: reference leaves the repository")
        return None
    return path


def _check_ref_path_exists(rel_path, errors, context):
    path = _ref_path(rel_path, errors, context)
    if path is not None and not path.exists():
        errors.append(f"{context}: referenced path does not exist: {rel_path}")


def validate_catalog(catalog, scenario_path=SCENARIO_MANIFEST):
    errors = []

    if not isinstance(catalog, dict):
        return ["catalog root must be a JSON object"]
    if type(catalog.get("schema_version")) is not int or catalog["schema_version"] != 1:
        errors.append(f"schema_version must be 1, got {catalog.get('schema_version')!r}")
    features = catalog.get("features")
    if not isinstance(features, list):
        errors.append("catalog must contain a 'features' array")
        return errors
    extra_top_keys = set(catalog.keys()) - {"schema_version", "features"}
    if extra_top_keys:
        errors.append(f"catalog has unexpected top-level keys: {sorted(extra_top_keys)}")

    ids = []
    by_id = {}
    for i, feature in enumerate(features):
        if not isinstance(feature, dict):
            errors.append(f"features[{i}] is not an object")
            continue
        fid = feature.get("id")
        if not isinstance(fid, str) or not fid:
            errors.append(f"features[{i}] has no valid string id")
            continue
        ids.append(fid)
        by_id.setdefault(fid, []).append(feature)

    duplicates = sorted({fid for fid in ids if ids.count(fid) > 1})
    if duplicates:
        errors.append(f"duplicate feature ids: {duplicates}")

    id_set = set(ids)
    expected_set = set(EXPECTED_FEATURE_IDS)
    missing = sorted(expected_set - id_set)
    extra = sorted(id_set - expected_set)
    if missing:
        errors.append(f"catalog is missing required feature ids: {missing}")
    if extra:
        errors.append(f"catalog has unexpected feature ids not in the shared contract: {extra}")
    if len(id_set) != 67 or (not missing and not extra and len(ids) != 67):
        if not missing and not extra and len(ids) != 67:
            errors.append(f"catalog must have exactly 67 features, found {len(ids)}")

    router_rows = {}
    for router_file in ROUTER_FILES:
        try:
            router_rows[str(router_file.relative_to(ROOT))] = set(parse_router_rows(router_file))
        except CatalogError as exc:
            errors.append(str(exc))
    covered_router_rows = {path: set() for path in router_rows}

    automation_files = discover_automation_files()
    covered_automations = set()

    cli_errors = []
    cli_groups = discover_cli_commands(cli_errors)
    errors.extend(cli_errors)
    referenced_cli_commands = {path: set() for path in cli_groups}

    canvas_errors = []
    canvas_groups = discover_canvas_groups(canvas_errors)
    errors.extend(canvas_errors)
    referenced_canvas_actions = {cid: set() for cid in canvas_groups}

    manifest, manifest_error = load_scenario_manifest(scenario_path)
    if manifest_error:
        errors.append(manifest_error)
    manifest_by_id = {}
    if manifest is not None:
        for scenario in manifest.get("scenarios", []):
            if isinstance(scenario, dict) and isinstance(scenario.get("id"), str):
                if scenario["id"] in manifest_by_id:
                    errors.append("duplicate scenario id: " + scenario["id"])
                manifest_by_id[scenario["id"]] = scenario

    for feature in features:
        if not isinstance(feature, dict) or not isinstance(feature.get("id"), str) or not feature["id"]:
            continue
        fid = feature["id"]
        ctx = f"feature '{fid}'"
        unknown_fields = set(feature) - set(REQUIRED_STRING_FIELDS) - set(REQUIRED_LIST_FIELDS) - {"guide"}
        if unknown_fields:
            errors.append(f"{ctx}: unknown fields: {sorted(unknown_fields)}")

        for field in REQUIRED_STRING_FIELDS:
            value = feature.get(field)
            if not isinstance(value, str) or not value.strip():
                errors.append(f"{ctx}: missing or empty required string field '{field}'")
        for field in REQUIRED_LIST_FIELDS:
            value = feature.get(field)
            if not isinstance(value, list):
                errors.append(f"{ctx}: field '{field}' must be a list")
            elif field not in {"router_refs", "cli_refs", "canvas_refs"} and any(
                    not isinstance(item, str) or not item.strip() for item in value):
                errors.append(f"{ctx}: field '{field}' must contain nonempty strings")
        for field in ("sources", "surfaces", "scenario_ids", "limitations"):
            if not feature.get(field):
                errors.append(f"{ctx}: field '{field}' must not be empty")
        if any(not isinstance(feature.get(field), list) for field in REQUIRED_LIST_FIELDS):
            continue
        if any(not isinstance(item, str) or not item.strip()
               for field in ("prerequisites", "surfaces", "sources", "automation_refs", "scenario_ids", "limitations")
               for item in feature[field]):
            continue

        guide = feature.get("guide")
        if (not isinstance(guide, dict) or set(guide) != {"path", "anchor"}
                or not isinstance(guide.get("path"), str) or not guide["path"]
                or not isinstance(guide.get("anchor"), str) or not guide["anchor"]):
            errors.append(f"{ctx}: 'guide' must be an object with 'path' and 'anchor'")
            guide = None

        availability = feature.get("availability")
        if not isinstance(availability, str) or availability not in AVAILABILITY_VALUES:
            errors.append(f"{ctx}: invalid availability '{availability}'")
        kind = feature.get("implementation_kind")
        if not isinstance(kind, str) or kind not in IMPLEMENTATION_KIND_VALUES:
            errors.append(f"{ctx}: invalid implementation_kind '{kind}'")
        if (kind == "design") != (availability == "planned"):
            errors.append(f"{ctx}: design-only features must be explicitly planned, not presented as available")
        if "scheduled" in feature["surfaces"] and not feature["automation_refs"]:
            errors.append(f"{ctx}: scheduled surface requires an automation_refs mapping")

        # Sources must exist.
        for src in feature.get("sources") or []:
            if isinstance(src, str):
                _check_ref_path_exists(src, errors, ctx)
            else:
                errors.append(f"{ctx}: source entries must be path strings")

        # Guide path/anchor must resolve to a real heading.
        if guide:
            guide_path = _ref_path(guide["path"], errors, ctx)
            if guide_path is None:
                continue
            if not guide_path.is_file():
                errors.append(f"{ctx}: guide path does not exist: {guide['path']}")
            else:
                try:
                    anchors = anchors_for_file(guide_path)
                except OSError as exc:
                    errors.append(f"{ctx}: cannot read guide {guide['path']}: {exc}")
                    anchors = set()
                if guide["anchor"] not in anchors:
                    errors.append(
                        f"{ctx}: guide anchor '#{guide['anchor']}' not found as a heading in "
                        f"{guide['path']}"
                    )

        # Router refs must point at a real row in a real router file.
        for ref in feature.get("router_refs") or []:
            if (not isinstance(ref, dict) or set(ref) != {"path", "label"}
                    or not isinstance(ref.get("path"), str) or not isinstance(ref.get("label"), str)
                    or not ref["path"] or not ref["label"]):
                errors.append(f"{ctx}: router_refs entries need 'path' and 'label'")
                continue
            rows = router_rows.get(ref["path"])
            if rows is None:
                errors.append(f"{ctx}: router_refs path not a known router file: {ref['path']}")
                continue
            if ref["label"] not in rows:
                errors.append(
                    f"{ctx}: router_refs label '{ref['label']}' not found in {ref['path']} "
                    "Core routines table"
                )
            else:
                covered_router_rows[ref["path"]].add(ref["label"])

        # Automation refs must point at a real automation file.
        for ref in feature.get("automation_refs") or []:
            if not isinstance(ref, str):
                errors.append(f"{ctx}: automation_refs entries must be path strings")
                continue
            _check_ref_path_exists(ref, errors, ctx)
            if ref in automation_files:
                covered_automations.add(ref)
            elif (path := _ref_path(ref, errors, ctx)) is not None and path.exists():
                errors.append(f"{ctx}: automation_refs path is not a recognised automation: {ref}")

        # CLI refs must be a real module with real commands.
        for ref in feature.get("cli_refs") or []:
            if (not isinstance(ref, dict) or set(ref) != {"path", "commands"}
                    or not isinstance(ref.get("path"), str) or not ref["path"]
                    or not isinstance(ref.get("commands"), list)
                    or any(not isinstance(command, str) or not command for command in ref["commands"])):
                errors.append(f"{ctx}: cli_refs entries need 'path' and a 'commands' list")
                continue
            rel = ref["path"]
            actual = cli_groups.get(rel)
            if actual is None:
                errors.append(f"{ctx}: cli_refs path not a known/introspectable CLI module: {rel}")
                continue
            unknown = sorted(set(ref["commands"]) - actual)
            if unknown:
                errors.append(f"{ctx}: cli_refs for {rel} name commands that do not exist: {unknown}")
            referenced_cli_commands.setdefault(rel, set()).update(ref["commands"])

        # Canvas refs must be a real canvas id with real actions.
        for ref in feature.get("canvas_refs") or []:
            if (not isinstance(ref, dict) or set(ref) != {"id", "actions"}
                    or not isinstance(ref.get("id"), str) or not ref["id"]
                    or not isinstance(ref.get("actions"), list)
                    or any(not isinstance(action, str) or not action for action in ref["actions"])):
                errors.append(f"{ctx}: canvas_refs entries need 'id' and an 'actions' list")
                continue
            actual = canvas_groups.get(ref["id"])
            if actual is None:
                errors.append(f"{ctx}: canvas_refs id not a known canvas: {ref['id']}")
                continue
            unknown = sorted(set(ref["actions"]) - actual)
            if unknown:
                errors.append(f"{ctx}: canvas_refs for {ref['id']} name actions that do not exist: {unknown}")
            referenced_canvas_actions.setdefault(ref["id"], set()).update(ref["actions"])

        # Scenario contract: the feature's own id must be its primary scenario id.
        scenario_ids = feature.get("scenario_ids") or []
        if fid not in scenario_ids:
            errors.append(f"{ctx}: scenario_ids must include the feature's own id '{fid}'")
        if manifest is not None:
            for sid in scenario_ids:
                scenario = manifest_by_id.get(sid)
                if scenario is None:
                    errors.append(f"{ctx}: scenario id '{sid}' not found in scenario manifest")
                    continue
                if scenario.get("feature_id") != fid:
                    errors.append(
                        f"{ctx}: scenario '{sid}' declares feature_id "
                        f"'{scenario.get('feature_id')}', expected '{fid}'"
                    )
                if availability == "planned" and scenario.get("coverage_kind") == "runtime":
                    errors.append(
                        f"{ctx}: availability is 'planned' but scenario '{sid}' claims "
                        "'runtime' coverage — a planned feature cannot have working runtime evidence"
                    )

        # Planned features must not claim existing router/automation/cli/canvas integration.
        if availability == "planned":
            if feature.get("cli_refs"):
                errors.append(f"{ctx}: availability 'planned' must not carry cli_refs")
            if feature.get("canvas_refs"):
                errors.append(f"{ctx}: availability 'planned' must not carry canvas_refs")
            if feature.get("automation_refs"):
                errors.append(f"{ctx}: availability 'planned' must not carry automation_refs")

    # Router coverage: every row in every router file must be claimed by some feature.
    for path, rows in router_rows.items():
        uncovered = sorted(rows - covered_router_rows.get(path, set()))
        if uncovered:
            errors.append(f"{path}: Core routines row(s) not covered by any feature: {uncovered}")

    # Automation coverage: every schedule manifest must be claimed by some feature.
    uncovered_automations = sorted(set(automation_files) - covered_automations)
    if uncovered_automations:
        errors.append(f"automations not referenced by any feature: {uncovered_automations}")

    # CLI coverage: every real subcommand must be named by some feature (not silently dropped).
    for rel, actual in cli_groups.items():
        missing_cmds = sorted(actual - referenced_cli_commands.get(rel, set()))
        if missing_cmds:
            errors.append(f"{rel}: shipped subcommands not covered by any feature: {missing_cmds}")

    # Canvas coverage: every real action must be named by some feature.
    for cid, actual in canvas_groups.items():
        missing_actions = sorted(actual - referenced_canvas_actions.get(cid, set()))
        if missing_actions:
            errors.append(f"canvas '{cid}': registered actions not covered by any feature: {missing_actions}")

    return errors


# ---------------------------------------------------------------------------
# Generated navigational sections
# ---------------------------------------------------------------------------

AVAILABILITY_LABEL = {
    "implemented": "Implemented",
    "optional": "Optional",
    "limited": "Limited",
    "planned": "Planned",
}


def render_features_section(catalog):
    features = sorted(catalog["features"], key=lambda f: (f["guide"]["path"], f["id"]))
    lines = [
        "| Feature | User goal | Availability | Kind | Guide |",
        "| --- | --- | --- | --- | --- |",
    ]
    for f in features:
        guide_link = f"[{f['guide']['path'].split('/')[-1]}](../{f['guide']['path']}#{f['guide']['anchor']})" \
            if f["guide"]["path"].startswith("docs/") else f"{f['guide']['path']}#{f['guide']['anchor']}"
        # Links in docs/features.md are relative to docs/, so drop the leading docs/.
        rel = f["guide"]["path"]
        if rel.startswith("docs/"):
            rel = rel[len("docs/"):]
        guide_link = f"[{f['name']}]({rel}#{f['guide']['anchor']})"
        lines.append(
            f"| `{f['id']}` | {f['user_goal']} | {AVAILABILITY_LABEL.get(f['availability'], f['availability'])} "
            f"| {f['implementation_kind']} | {guide_link} |"
        )
    return "\n".join(lines) + "\n"


def render_howto_section(catalog):
    by_guide = {}
    for f in catalog["features"]:
        by_guide.setdefault(f["guide"]["path"], []).append(f)
    lines = ["| Guide | Features covered |", "| --- | --- |"]
    for guide_path in sorted(by_guide):
        entries = sorted(by_guide[guide_path], key=lambda f: f["id"])
        rel = guide_path
        if rel.startswith("docs/how-to/"):
            rel = rel[len("docs/how-to/"):]
            display = rel
        elif rel.startswith("docs/"):
            rel = "../" + rel[len("docs/"):]
            display = rel
        else:
            display = rel
        names = ", ".join(f"[{f['name']}]({rel}#{f['guide']['anchor']})" for f in entries)
        lines.append(f"| [{display}]({rel}) | {names} |")
    return "\n".join(lines) + "\n"


def apply_markers(original_text, markers, body):
    begin, end = markers
    if begin not in original_text or end not in original_text:
        raise CatalogError(f"markers {begin!r} / {end!r} not found in target file")
    begin_idx = original_text.index(begin) + len(begin)
    end_idx = original_text.index(end)
    if end_idx < begin_idx:
        raise CatalogError("END marker appears before BEGIN marker")
    return original_text[:begin_idx] + "\n" + body + original_text[end_idx:]


def current_section(text, markers):
    begin, end = markers
    if begin not in text or end not in text:
        return None
    begin_idx = text.index(begin) + len(begin)
    end_idx = text.index(end)
    return text[begin_idx:end_idx].strip("\n")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--write", action="store_true", help="write the generated sections in place")
    parser.add_argument("--check", action="store_true", help="check only; this is also the default")
    args = parser.parse_args(argv)

    try:
        catalog = load_catalog()
    except CatalogError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    errors = validate_catalog(catalog)
    if errors:
        print(f"feature catalog validation failed with {len(errors)} problem(s):", file=sys.stderr)
        for err in errors:
            print(f"  - {err}", file=sys.stderr)
        return 1

    targets = [
        (FEATURES_DOC, FEATURES_MARKERS, render_features_section(catalog)),
        (HOWTO_INDEX, HOWTO_MARKERS, render_howto_section(catalog)),
    ]

    if args.write:
        for path, markers, body in targets:
            try:
                text = path.read_text(encoding="utf-8")
                new_text = apply_markers(text, markers, body)
            except (OSError, CatalogError) as exc:
                print(f"error: {path}: {exc}", file=sys.stderr)
                return 1
            if new_text != text:
                path.write_text(new_text, encoding="utf-8")
                print(f"wrote generated section in {path.relative_to(ROOT)}")
            else:
                print(f"{path.relative_to(ROOT)} already up to date")
        print("catalog valid; 67 features; generated sections written")
        return 0

    stale = []
    for path, markers, body in targets:
        try:
            text = path.read_text(encoding="utf-8")
        except OSError as exc:
            print(f"error: cannot read {path}: {exc}", file=sys.stderr)
            return 1
        existing = current_section(text, markers)
        if existing is None:
            stale.append(f"{path.relative_to(ROOT)}: generated markers not found")
            continue
        if existing != body.strip("\n"):
            stale.append(f"{path.relative_to(ROOT)}: generated section is stale; run --write")
    if stale:
        print("feature catalog docs are stale:", file=sys.stderr)
        for item in stale:
            print(f"  - {item}", file=sys.stderr)
        return 1

    print("catalog valid; 67 features; generated sections up to date")
    return 0


if __name__ == "__main__":
    sys.exit(main())
