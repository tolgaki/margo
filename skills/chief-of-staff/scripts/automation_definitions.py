#!/usr/bin/env python3
"""Registry-only Markdown scenario authoring. No scheduler registration or scenario execution."""

import argparse
import calendar
from datetime import date
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import uuid
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

import margo_profile
import margo_store as store

HEADINGS = ("Description/Purpose", "Conditions", "Inputs", "Steps", "Outputs",
            "Completion/Idempotency", "Failure/Retry", "Permissions/Review", "Source references")
START, END = "<!-- scenario-registry -->", "<!-- end-scenario-registry -->"
ID = re.compile(r"^[a-z0-9][a-z0-9-]{2,79}$")
MAX_FILE = 256 * 1024
MAX_CONTROLLER = 1024 * 1024


def hash_bytes(raw):
    return hashlib.sha256(raw).hexdigest()


def fields(value, allowed, required=None):
    if (not isinstance(value, dict) or set(value) - set(allowed)
            or not set(allowed if required is None else required) <= set(value)):
        raise store.StateError("Missing or unsupported automation fields.")


def bounded(value, maximum, nullable=False):
    if nullable and value is None:
        return
    if not isinstance(value, str) or not value.strip() or len(value) > maximum or "\x00" in value:
        raise store.StateError("Expected bounded nonempty automation text.")


def cron(expression):
    if not isinstance(expression, str) or len(expression) > 150 or not re.fullmatch(r"[0-9*,/-]+(?: [0-9*,/-]+){4}", expression):
        raise store.StateError("Schedule must have five fields separated by single spaces.")
    selected = []
    for part, (low, high) in zip(expression.split(" "), [(0, 59), (0, 23), (1, 31), (1, 12), (0, 6)]):
        seen = set()
        for atom in part.split(","):
            if atom == "*":
                values = range(low, high + 1)
            elif re.fullmatch(r"\*/[1-9]\d*", atom):
                step = int(atom[2:])
                values = range(low, high + 1, step)
            elif re.fullmatch(r"\d+-\d+", atom):
                start, end = map(int, atom.split("-"))
                if not low <= start < end <= high:
                    raise store.StateError("Cron range must be ascending and within field bounds.")
                values = range(start, end + 1)
            elif re.fullmatch(r"\d+", atom):
                values = [int(atom)]
            else:
                raise store.StateError("Unsupported cron atom.")
            values = set(values)
            if not values or min(values) < low or max(values) > high or seen & values:
                raise store.StateError("Cron fields contain duplicate or out-of-range values.")
            seen |= values
        selected.append(seen)
    # All fields are ANDed, including day-of-month/day-of-week.
    if not any((date(year, month, day).weekday() + 1) % 7 in selected[4]
               for year in range(2000, 2029) for month in selected[3] for day in selected[2]
               if day <= calendar.monthrange(year, month)[1]):
        raise store.StateError("Cron expression has an empty calendar.")
    return expression


def metadata(value):
    fields(value, {"schema_version", "id", "title", "enabled", "review_status", "timezone", "schedule",
                   "approval_ref", "review_issues", "source_automation_id", "source_enabled", "provenance"})
    if type(value["schema_version"]) is not int or value["schema_version"] != 1 or not isinstance(value["id"], str) or not ID.fullmatch(value["id"]):
        raise store.StateError("Unsupported scenario version or ID.")
    bounded(value["title"], 200)
    if "\n" in value["title"] or "\r" in value["title"]:
        raise store.StateError("Scenario title must be one line.")
    if type(value["enabled"]) is not bool or value["review_status"] not in ("review_required", "approved"):
        raise store.StateError("Invalid enablement/review status.")
    issues = value["review_issues"]
    if not isinstance(issues, list) or len(issues) > 100 or any(
            not isinstance(issue, str) or not re.fullmatch(r"[A-Z][A-Z0-9_-]*", issue) for issue in issues) or len(set(issues)) != len(issues):
        raise store.StateError("Review issues must be unique uppercase issue codes.")
    bounded(value["approval_ref"], 1000, True)
    if value["timezone"] is not None:
        if not isinstance(value["timezone"], str) or not re.fullmatch(r"[A-Za-z_+-]+(?:/[A-Za-z0-9_+.-]+)+", value["timezone"]):
            raise store.StateError("Timezone must be an explicit IANA area/location.")
        try:
            ZoneInfo(value["timezone"])
        except (ValueError, ZoneInfoNotFoundError) as exc:
            raise store.StateError("IANA timezone is invalid or unavailable.") from exc
    if value["schedule"] is not None:
        fields(value["schedule"], {"kind", "expression"})
        if value["schedule"]["kind"] != "cron":
            raise store.StateError("Only version-1 cron schedules are supported.")
        cron(value["schedule"]["expression"])
    if value["review_status"] == "approved" and (value["timezone"] is None or value["schedule"] is None or not value["approval_ref"] or issues):
        raise store.StateError("Approved descriptors need a resolved schedule/timezone, approval reference and no review issues.")
    if value["enabled"] and value["review_status"] != "approved":
        raise store.StateError("Unreviewed scenario descriptors cannot be enabled.")
    provenance = value["provenance"]
    fields(provenance, {"kind", "source_path", "source_sha256", "definition_sha256", "captured_at", "step_count"})
    if provenance["kind"] not in ("manual", "scout") or type(provenance["step_count"]) is not int or provenance["step_count"] < 0:
        raise store.StateError("Invalid source provenance.")
    store.validate_timestamp(provenance["captured_at"])
    for key in ("source_sha256", "definition_sha256"):
        if provenance[key] is not None and (not isinstance(provenance[key], str) or not re.fullmatch("[a-f0-9]{64}", provenance[key])):
            raise store.StateError("Invalid provenance digest.")
    bounded(provenance["source_path"], 4000, True)
    bounded(value["source_automation_id"], 500, True)
    if provenance["kind"] == "scout":
        if (value["source_enabled"] is not True or value["source_automation_id"] is None or provenance["step_count"] < 1
                or any(provenance[key] is None for key in ("source_path", "source_sha256", "definition_sha256"))):
            raise store.StateError("Imported provenance must identify its originally enabled source.")
    elif (value["source_enabled"] is not None or value["source_automation_id"] is not None or provenance["step_count"] != 0
          or any(provenance[key] is not None for key in ("source_path", "source_sha256", "definition_sha256"))):
        raise store.StateError("Manual provenance cannot claim an imported source.")
    return value


def parse(raw):
    if len(raw) > MAX_FILE:
        raise store.StateError("Scenario exceeds 256 KiB.")
    try:
        text = raw.decode("utf-8")
    except UnicodeError as exc:
        raise store.StateError("Scenario must be UTF-8.") from exc
    match = re.match(r"\A---\r?\n(.*?)\r?\n---(?:\r?\n|\Z)", text, re.S)
    if not match:
        raise store.StateError("Scenario needs JSON frontmatter inside --- delimiters.")
    meta = metadata(store.parse_json(match[1]))
    body = text[match.end():]
    headings, h1, positions = [], [], []
    title_offset = None
    fence = None
    offset = 0
    for line in body.splitlines(keepends=True):
        fence_match = re.match(r" {0,3}(`{3,}|~{3,})(.*)", line.rstrip("\r\n"))
        if fence_match:
            marker = fence_match[1]
            if fence is None:
                fence = marker
            elif marker[0] == fence[0] and len(marker) >= len(fence) and not fence_match[2].strip():
                fence = None
        elif fence is None:
            if line.startswith("# "):
                h1.append(line[2:].rstrip("\r\n"))
                title_offset = offset + 2
            if line.startswith("## "):
                headings.append(line[3:].rstrip("\r\n"))
                positions.append((offset, offset + len(line)))
        offset += len(line)
    if fence is not None or headings != list(HEADINGS) or h1 != [meta["title"]]:
        raise store.StateError("Scenario title/headings/fences do not match the version-1 structure.")
    sections = {heading: body[positions[index][1]:positions[index + 1][0] if index + 1 < len(positions) else len(body)].strip()
                for index, heading in enumerate(HEADINGS)}
    if any(not content for content in sections.values()):
        raise store.StateError("All nine scenario sections require content.")
    return {"metadata": meta, "body": body, "sections": sections, "revision": hash_bytes(raw), "title_offset": title_offset}


def serialize(meta, body):
    raw = ("---\n" + json.dumps(meta, ensure_ascii=False, indent=2) + "\n---\n" + body).encode("utf-8")
    parse(raw)
    return raw


def read(path, limit, missing=False):
    margo_profile._no_redirects(path)
    try:
        info = path.stat()
        if not path.is_file() or info.st_nlink != 1:
            raise store.StateError("Managed automation paths must be regular, non-hardlinked files.")
        with path.open("rb") as stream:
            raw = stream.read(limit + 1)
    except FileNotFoundError:
        if missing:
            return None
        raise store.StateError("Registered automation file is missing: " + path.name)
    if len(raw) > limit:
        raise store.StateError("Managed automation file exceeds its read limit: " + path.name)
    return raw


def registry(raw):
    if raw is None:
        return {"schema_version": 1, "scenario_registry": []}, None
    try:
        text = raw.decode("utf-8")
    except UnicodeError as exc:
        raise store.StateError("Controller must be UTF-8.") from exc
    if text.count(START) != 1 or text.count(END) != 1:
        raise store.StateError("Controller requires one exact scenario-registry span.")
    start, end = text.index(START) + len(START), text.index(END)
    match = re.fullmatch(r"\s*```json\r?\n(.*?)\r?\n```\s*", text[start:end], re.S)
    if end < start or not match:
        raise store.StateError("Controller registry must be a fenced JSON object.")
    value = store.parse_json(match[1])
    fields(value, {"schema_version", "scenario_registry"})
    if type(value["schema_version"]) is not int or value["schema_version"] != 1 or not isinstance(value["scenario_registry"], list) or len(value["scenario_registry"]) > 100:
        raise store.StateError("Unsupported controller registry version or more than 100 scenarios.")
    seen = set()
    for row in value["scenario_registry"]:
        fields(row, {"id", "path"})
        if not isinstance(row["id"], str) or not ID.fullmatch(row["id"]) or row["path"] != "automations/" + row["id"] + ".md" or row["id"] in seen:
            raise store.StateError("Registry IDs/paths must be unique direct kebab-case Markdown names.")
        seen.add(row["id"])
    return value, (text, start, end)


def scope():
    profile = margo_profile.show()
    if profile["work_root"]["status"] != "available":
        raise store.StateError("Configured workspace is unavailable; no automation directory fallback.")
    root = margo_profile.work_root(profile["work_root"]["path"])
    margo_profile._no_redirects(root / "automations")
    raw = read(root / "AUTOMATIONS.md", MAX_CONTROLLER, True)
    values, span = registry(raw)
    return profile, root, raw, values, span


def list_definitions():
    profile, root, raw, values, _ = scope()
    rows, errors = [], []
    for entry in values["scenario_registry"]:
        try:
            record = parse(read(root / "automations" / (entry["id"] + ".md"), MAX_FILE))
            if record["metadata"]["id"] != entry["id"]:
                raise store.StateError("Frontmatter ID does not match registry.")
            rows.append({"metadata": record["metadata"], "revision": record["revision"],
                         "description": record["sections"]["Description/Purpose"][:500],
                         "execution_readiness": "unverified_private_approval_and_native_binding", "last_result": None})
        except (store.StateError, OSError) as exc:
            errors.append({"id": entry["id"], "error": str(exc)})
    duplicates = {}
    for row in rows:
        meta = row["metadata"]
        if meta["schedule"]:
            key = meta["timezone"] + ":" + meta["schedule"]["expression"] if meta["timezone"] else None
            if key:
                duplicates.setdefault(key, []).append(meta["id"])
    return {"status": "partial" if errors else "available" if raw else "not_configured", "workspace": str(root),
            "profile_revision": profile["revision"], "controller_revision": hash_bytes(raw) if raw else "missing",
            "scenarios": rows, "errors": errors, "same_schedule_groups": [ids for ids in duplicates.values() if len(ids) > 1],
            "native_controller": {"status": "unbound", "workflow_id": None, "enabled": None,
                                  "reason": "No authoritative native controller binding exists. Scenario flags do not register or enable it."},
            "margo_starter_suite": "separate; not managed by this controller", "dependency_readiness": "not_evaluated"}


def show(identity):
    if not isinstance(identity, str) or not ID.fullmatch(identity):
        raise store.StateError("Invalid scenario ID.")
    profile, root, raw, values, _ = scope()
    if not any(row["id"] == identity for row in values["scenario_registry"]):
        raise store.StateError("Scenario is not registered in this workspace.")
    result = parse(read(root / "automations" / (identity + ".md"), MAX_FILE))
    if result["metadata"]["id"] != identity:
        raise store.StateError("Registered ID does not match scenario content.")
    return dict(result, profile_revision=profile["revision"], controller_revision=hash_bytes(raw),
                native_execution_authorized=False)


def change_plan(value):
    fields(value, {"operation", "id", "expected_revision", "controller_revision", "profile_revision", "patch"},
           {"operation", "id", "expected_revision", "controller_revision", "profile_revision", "patch"})
    if value["operation"] not in {"create", "update", "enable", "disable"} or not isinstance(value["id"], str) or not ID.fullmatch(value["id"]):
        raise store.StateError("Unsupported scenario operation or ID.")
    profile, root, raw, values, span = scope()
    if profile["revision"] != value["profile_revision"] or (hash_bytes(raw) if raw else "missing") != value["controller_revision"]:
        raise store.StateError("Workspace/profile/controller revision conflict; reread before authoring.")
    path = root / "automations" / (value["id"] + ".md")
    registered = any(row["id"] == value["id"] for row in values["scenario_registry"])
    create = value["operation"] == "create"
    if create:
        if registered or value["expected_revision"] != "missing" or path.exists():
            raise store.StateError("Scenario already exists or create revision is not missing.")
        fields(value["patch"], {"title", "timezone", "schedule", "sections"})
        meta = {"schema_version": 1, "id": value["id"], "title": value["patch"]["title"], "enabled": False,
                "review_status": "review_required", "timezone": value["patch"]["timezone"], "schedule": value["patch"]["schedule"],
                "approval_ref": None, "review_issues": ["RUNTIME", "PERMISSIONS"], "source_automation_id": None,
                "source_enabled": None, "provenance": {"kind": "manual", "source_path": None, "source_sha256": None,
                    "definition_sha256": None, "captured_at": store.utc_now(), "step_count": 0}}
        body = ""
    else:
        if not registered:
            raise store.StateError("Scenario is not registered.")
        original = parse(read(path, MAX_FILE))
        if original["revision"] != value["expected_revision"]:
            raise store.StateError("Scenario revision conflict; local draft is not saved.")
        meta, body = dict(original["metadata"]), original["body"]
    if value["operation"] in {"enable", "disable"}:
        fields(value["patch"], set())
        meta["enabled"] = value["operation"] == "enable"
    else:
        fields(value["patch"], {"title", "timezone", "schedule", "sections"}, set())
        if not value["patch"]:
            raise store.StateError("Empty scenario change.")
        old_title = meta["title"]
        meta.update({key: entry for key, entry in value["patch"].items() if key != "sections"})
        meta.update(enabled=False, review_status="review_required", approval_ref=None,
                    review_issues=sorted(set(meta["review_issues"]) | {"DEFINITION_CHANGED"}))
        if "sections" in value["patch"]:
            sections = value["patch"]["sections"]
            fields(sections, set(HEADINGS))
            for content in sections.values():
                bounded(content, 60000)
            body = "\n# " + meta["title"] + "\n\n" + "".join("## " + heading + "\n\n" + sections[heading] + "\n\n" for heading in HEADINGS)
        elif meta["title"] != old_title:
            offset = original["title_offset"]
            body = body[:offset] + meta["title"] + body[offset + len(old_title):]
    metadata(meta)
    result = serialize(meta, body)
    return profile, root, raw, values, span, path, result


def preview(value):
    *_, raw = change_plan(value)
    return {"change": value, "preview_hash": hash_bytes(store.canonical_json(value).encode()), "after": parse(raw)["metadata"],
            "native_execution_authorized": False, "warning": "This edits only managed Markdown. A descriptor approval reference is not private execution approval or native enablement."}


def atomic(path, raw, expected):
    current = read(path, MAX_CONTROLLER, True)
    if (hash_bytes(current) if current else "missing") != expected:
        raise store.StateError("File revision conflict before write.")
    temporary = path.with_name("." + path.name + "." + uuid.uuid4().hex + ".new")
    try:
        descriptor = os.open(str(temporary), os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(raw); stream.flush(); os.fsync(stream.fileno())
        current = read(path, MAX_CONTROLLER, True)
        if (hash_bytes(current) if current else "missing") != expected:
            raise store.StateError("File changed during save; nothing overwritten.")
        if expected == "missing":
            os.link(str(temporary), str(path)); temporary.unlink()
        else:
            os.replace(str(temporary), str(path))
    finally:
        temporary.unlink(missing_ok=True)


def commit(value, preview_hash):
    if preview_hash != hash_bytes(store.canonical_json(value).encode()):
        raise store.StateError("Preview hash does not match the exact proposed change.")
    _, root, *_ = change_plan(value)
    directory = root / "automations"
    directory.mkdir(exist_ok=True)
    margo_profile._no_redirects(directory)
    lock = directory / ".authoring.lock"
    owned = False
    try:
        descriptor = os.open(str(lock), os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
        os.close(descriptor); owned = True
        _, root, controller, values, span, target, raw = change_plan(value)
        atomic(target, raw, value["expected_revision"])
        if value["operation"] == "create":
            values["scenario_registry"].append({"id": value["id"], "path": "automations/" + value["id"] + ".md"})
            replacement = "\n```json\n" + json.dumps(values, ensure_ascii=False, indent=2) + "\n```\n"
            text = span[0][:span[1]] + replacement + span[0][span[2]:] if span else "# Automations\n\n" + START + replacement + END + "\n"
            try:
                atomic(root / "AUTOMATIONS.md", text.encode("utf-8"), value["controller_revision"])
            except (store.StateError, OSError) as exc:
                raise store.StateError("Scenario file created but registry save failed. It is unregistered/inert; inspect both exact files before retrying. " + str(exc)) from exc
        return dict(show(value["id"]), saved=True, schedules_registered=0)
    finally:
        if owned:
            lock.unlink()


def dispatch(value):
    fields(value, {"operation", "input"})
    operation, data = value["operation"], value["input"]
    if operation == "list":
        fields(data, set()); return list_definitions()
    if operation == "show":
        fields(data, {"id"}); return show(data["id"])
    if operation == "preview":
        return preview(data)
    if operation == "commit":
        fields(data, {"change", "preview_hash"}); return commit(data["change"], data["preview_hash"])
    raise store.StateError("Automation management operation unavailable.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["tool"])
    parser.parse_args()
    try:
        raw = sys.stdin.read(MAX_FILE + 1)
        if len(raw.encode("utf-8")) > MAX_FILE:
            raise store.StateError("Automation input exceeds limit.")
        print(store.canonical_json(dispatch(store.parse_json(raw))))
        return 0
    except (store.StateError, OSError, TypeError, KeyError, ValueError) as exc:
        print(store.canonical_json({"error": str(exc), "code": "revision_conflict" if "conflict" in str(exc).lower() else "automation_error"}), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
