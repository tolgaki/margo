#!/usr/bin/env python3
"""Read-only health report for private Margo configuration and durable state.

This command never calls a host API or opens the host database. Optional
--install-root DIR (default ~/.copilot) checks DIR/.margo-files.json, a JSON map
of managed relative paths to expected SHA256 hashes. Missing/empty manifests are
unknown, not healthy; mismatched or missing managed files indicate local drift.
The manifest is never rewritten, and no installation repair is attempted.

Optional
--snapshot FILE supplies JSON captured through supported host APIs:
{
  "captured_at": "2026-09-05T18:00:00Z",
  "installation": [{"path": "/installed/file", "sha256": "<expected SHA256>"}],
  "workflows": [{
    "id": "morning", "enabled": true, "status": "failed",
    "expected_at": "2026-09-05T15:00:00Z", "last_started_at": "2026-09-04T15:00:00Z",
    "error_class": "workspace_configuration", "output_available": false,
    "expected_prompt_sha256": "<expected SHA256>", "prompt": "<actual prompt>"
  }]
}
Snapshot arrays are optional; installation entries require path and sha256, and
workflow entries require id. Missing/stale snapshot evidence is
unknown, never healthy. Only structured error categories are emitted, not raw host
errors. Hash comparisons are local. Configuration checks inspect required field
values, not a document's title or generic template warning. No personal values are
printed. Defaults inspect installed preference/decision-config files; explicit
--preferences and --decision-config select supplied copies.

Exit 0: report generated (including setup-needed/degraded/unknown). Exit 2: invalid
snapshot or corrupt/unavailable state. --strict also exits 1 for nonhealthy reports.
"""

import argparse
import hashlib
import importlib.util
import re
import sqlite3
import sys
from datetime import datetime
from pathlib import Path, PurePosixPath

from margo_store import (SetupRequired, StateError, add_state_arguments, canonical_json,
                         connect, copilot_home, read_json, state_path, utc_now, validate_timestamp)


REQUIRED_PREFERENCES = ("Name / preferred name", "Role / team", "Time zone & working hours",
                        "Focus-time blocks to protect",
                        "Current top priorities (this week/quarter)")
REQUIRED_DECISION_CONFIG = ("Repo", "Local clone", "Team", "ID prefix")
HASH = re.compile(r"[0-9a-fA-F]{64}")
SAFE_ERRORS = {"workspace_configuration", "access_denied", "timeout", "network",
               "throttled", "unavailable", "invalid_response", "authentication_required",
               "binding_unavailable", "unknown"}


def configuration_fields(path, required):
    result = {"path": str(path), "fields": {}, "status": "incomplete"}
    try:
        content = Path(path).read_text(encoding="utf-8")
    except FileNotFoundError:
        result["fields"] = {field: "missing" for field in required}
        return result
    except (OSError, UnicodeError) as exc:
        raise StateError("configuration file is unreadable") from exc
    # Delimit on the next named field or heading, not on braces elsewhere in the
    # document. A filled profile underneath a template title is still filled.
    pattern = re.compile(r"\*\*([^*\n]+?)(?::)?\*\*\s*:?\s*")
    matches = list(pattern.finditer(content))
    for field in required:
        found = next((index for index, match in enumerate(matches)
                      if match.group(1).rstrip(":").strip().casefold() == field.casefold()), None)
        if found is None:
            result["fields"][field] = "missing"
            continue
        start = matches[found].end()
        end = matches[found + 1].start() if found + 1 < len(matches) else len(content)
        value = re.split(r"\n(?:\s*\n|#|[-*]\s)", content[start:end], maxsplit=1)[0]
        value = value.strip(" \n\t`·-")
        if not value:
            status = "missing"
        elif re.search(r"\{[^{}]*\}|<[^>]*(?:your|placeholder)[^>]*>", value, re.I):
            status = "placeholder"
        elif value.casefold() in ("todo", "tbd", "not configured") or value.casefold().startswith("(e.g."):
            status = "placeholder"
        else:
            status = "complete"
        result["fields"][field] = status
    if all(value == "complete" for value in result["fields"].values()):
        result["status"] = "complete"
    return result


def _hash(value, field):
    if not isinstance(value, str) or not HASH.fullmatch(value):
        raise StateError(field + " must be a SHA256 hex digest")
    return value.lower()


def inspect_installation_manifest(install_root):
    try:
        root = Path(install_root).expanduser().resolve()
    except (OSError, RuntimeError) as exc:
        raise StateError("installation root is unreadable") from exc
    manifest = root / ".margo-files.json"
    result = {"status": "unknown", "manifest_path": str(manifest),
              "checked_files": 0, "matching_files": 0, "issues": []}
    if manifest.is_symlink():
        raise StateError("managed-file manifest must not be a symlink")
    if not manifest.exists():
        result["reason"] = "manifest-missing"
        return result
    entries = read_json(manifest)
    if not isinstance(entries, dict):
        raise StateError("managed-file manifest must map relative paths to SHA256 digests")
    if not entries:
        result["reason"] = "manifest-empty"
        return result
    seen = set()
    for relative, expected in sorted(entries.items()):
        if not isinstance(relative, str) or not relative or "\x00" in relative or ":" in relative:
            raise StateError("managed-file manifest requires safe relative paths")
        relative_path = PurePosixPath(relative.replace("\\", "/"))
        if relative_path.is_absolute() or not relative_path.parts or ".." in relative_path.parts:
            raise StateError("managed-file manifest path escapes the installation")
        normalized = relative_path.as_posix()
        if normalized in seen:
            raise StateError("managed-file manifest contains duplicate normalized paths")
        seen.add(normalized)
        path = root.joinpath(*relative_path.parts)
        try:
            path.resolve().relative_to(root)
        except (ValueError, RuntimeError) as exc:
            raise StateError("managed-file manifest path escapes the installation") from exc
        expected = _hash(expected, "managed-file sha256")
        actual = None
        try:
            actual = hashlib.sha256(path.read_bytes()).hexdigest()
            status = "match" if actual == expected else "drift"
        except FileNotFoundError:
            status = "missing"
        except OSError:
            status = "unreadable"
        result["checked_files"] += 1
        if status == "match":
            result["matching_files"] += 1
        else:
            result["issues"].append({"path": normalized, "status": status,
                                     "expected_sha256": expected, "actual_sha256": actual})
    result["status"] = "degraded" if result["issues"] else "healthy"
    return result


def inspect_snapshot(snapshot, max_age_seconds=86400, installation_status=None):
    if not isinstance(snapshot, dict):
        raise StateError("snapshot must be a JSON object")
    if isinstance(max_age_seconds, bool) or not isinstance(max_age_seconds, int) or max_age_seconds < 1:
        raise StateError("snapshot max age must be a positive integer")
    now = utc_now()
    result = {"status": "unknown", "captured_at": None, "fresh": False, "installation": [], "workflows": []}
    if snapshot.get("captured_at") is not None:
        result["captured_at"] = validate_timestamp(snapshot["captured_at"])
        if result["captured_at"] > now:
            raise StateError("snapshot captured_at cannot be in the future")
        age = (datetime.fromisoformat(now) - datetime.fromisoformat(result["captured_at"])).total_seconds()
        result["fresh"] = age <= max_age_seconds
    installation = snapshot.get("installation", [])
    workflows = snapshot.get("workflows", [])
    if not isinstance(installation, list) or not isinstance(workflows, list):
        raise StateError("snapshot installation and workflows must be arrays")
    for entry in installation:
        if not isinstance(entry, dict) or not isinstance(entry.get("path"), str):
            raise StateError("installation entry requires path and sha256")
        expected = _hash(entry.get("sha256"), "installation sha256")
        path = Path(entry["path"]).expanduser()
        try:
            actual = hashlib.sha256(path.read_bytes()).hexdigest()
            status = "match" if actual == expected else "drift"
        except FileNotFoundError:
            status = "missing"
        except OSError:
            status = "unreadable"
        result["installation"].append({"path": str(path), "status": status})
    known_ids = set()
    for workflow in workflows:
        if not isinstance(workflow, dict) or not isinstance(workflow.get("id"), str) or not workflow["id"]:
            raise StateError("workflow entry requires a nonempty id")
        if workflow["id"] in known_ids:
            raise StateError("snapshot contains duplicate workflow IDs")
        known_ids.add(workflow["id"])
        status = workflow.get("status", "unknown")
        if status not in ("unknown", "pending", "running", "completed", "failed", "cancelled", "missed", "disabled"):
            raise StateError("invalid workflow status")
        enabled = workflow.get("enabled", True)
        if not isinstance(enabled, bool):
            raise StateError("workflow enabled must be boolean")
        expected = validate_timestamp(workflow["expected_at"]) if workflow.get("expected_at") is not None else None
        started = validate_timestamp(workflow["last_started_at"]) if workflow.get("last_started_at") is not None else None
        if started is not None and started > now:
            raise StateError("actual workflow start cannot be in the future")
        missed = bool(enabled and expected and expected <= now and (started is None or started < expected))
        output = workflow.get("output_available")
        if output is not None and not isinstance(output, bool):
            raise StateError("output_available must be boolean or null")
        prompt_status = "unknown"
        if workflow.get("expected_prompt_sha256") is not None:
            expected_hash = _hash(workflow["expected_prompt_sha256"], "expected_prompt_sha256")
            if "prompt" in workflow:
                if not isinstance(workflow["prompt"], str):
                    raise StateError("prompt must be a string")
                actual = hashlib.sha256(workflow["prompt"].encode("utf-8")).hexdigest()
                prompt_status = "match" if actual == expected_hash else "drift"
        error = workflow.get("error_class")
        error = error if isinstance(error, str) and error in SAFE_ERRORS else "unknown" if error else None
        result["workflows"].append({"id": workflow["id"], "status": status, "enabled": enabled,
                                    "expected_at": expected, "last_started_at": started,
                                    "missed_expected_start": missed, "error_class": error,
                                    "retry_requires_reauthentication": error == "authentication_required",
                                    "prompt": prompt_status, "output_available": output,
                                    "source_coverage_implied": False})
    unhealthy = installation_status == "degraded" or any(
        item["status"] != "match" for item in result["installation"]) or any(
        item["enabled"] and (item["status"] in ("failed", "cancelled", "missed") or item["missed_expected_start"]
                             or item["prompt"] == "drift" or item["output_available"] is False)
        for item in result["workflows"])
    unknown = ((not installation and installation_status != "healthy") or not workflows or not result["fresh"] or any(
        item["enabled"] and (item["status"] == "unknown" or item["expected_at"] is None
                             or item["prompt"] == "unknown" or item["output_available"] is None)
        for item in result["workflows"]))
    result["status"] = "degraded" if unhealthy else "unknown" if unknown else "healthy"
    return result


def _proactive_module():
    path = Path(__file__).resolve().with_name("proactive_state.py")
    if not path.is_file():
        raise StateError("proactive_state.py is missing from this installation")
    spec = importlib.util.spec_from_file_location("margo_doctor_proactive", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def state_health(account=None, state_root=None):
    principal, path = state_path(account, state_root)
    if not path.exists():
        return {"status": "not-initialized", "account_configured": True, "all_clear": False}
    connection = connect(principal, state_root)
    try:
        tables = {row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        if "proactive_meta" not in tables:
            if any(name.startswith("proactive_") for name in tables):
                raise StateError("proactive schema is malformed")
            return {"status": "not-initialized", "account_configured": True, "all_clear": False}
        proactive = _proactive_module()
        proactive.validate_schema(connection)
        return proactive.status_report(connection)
    finally:
        connection.close()


def memory_health(account=None, state_root=None):
    principal, path = state_path(account, state_root)
    if not path.exists():
        return {"status": "not-initialized"}
    connection = connect(principal, state_root, read_only=True)
    try:
        tables = {row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        if "memory_records" not in tables:
            return {"status": "not-initialized"}
        marker = connection.execute(
            "SELECT value FROM margo_meta WHERE key='memory_schema_version'").fetchone()
        if marker is not None and marker[0] == "1":
            return {"status": "migration-required",
                    "action": "Pause memory writers, back up private state and deletion journals, then run memory_state.py migrate."}
        counts = [dict(row) for row in connection.execute(
            "SELECT domain,status,count(*) AS count FROM memory_records GROUP BY domain,status")]
        index_count = (connection.execute("SELECT count(*) FROM semantic_vectors").fetchone()[0]
                       if "semantic_vectors" in tables else None)
    finally:
        connection.close()
    from memory_store import MemoryStore
    memory = MemoryStore(principal, state_root, read_only=True)
    try:
        health = memory.health()
        capture_policy = memory.policy()
    finally:
        memory.close()
    from memory_encoder import EmbeddingError, status_local
    try:
        encoder = status_local()
    except EmbeddingError as exc:
        encoder = {"status": "unavailable", "error": str(exc)}
    optional_absent = (encoder["status"] in {"missing_runtime", "missing_model"}
                       and not encoder.get("configured", False))
    status = ("attention-needed" if health["stale_sources"] or health["review_due"] or health["conflict_links"]
              or health["blocked_job_count"] else "available" if encoder["status"] == "available"
              else "semantic-unavailable" if optional_absent else "attention-needed")
    return {"status": status,
            "counts": counts, "indexed_vectors": index_count, "embedding_runtime": encoder,
            "health": health, "policy": capture_policy,
            "action": (health["recovery_actions"][0] if health["recovery_actions"] else
                       "Use explicit --mode lexical, or install the optional semantic runtime if wanted."
                       if optional_absent else "Inspect memory health and repair the configured runtime or source gaps."
                       if status == "attention-needed" else None),
            "note": "Memory relevance does not establish source truth, permissions or action approval."}


def task_health(account=None, state_root=None):
    principal, path = state_path(account, state_root)
    if not path.exists():
        return {"status": "not-initialized"}
    connection = connect(principal, state_root, read_only=True)
    try:
        marker = connection.execute("SELECT value FROM margo_meta WHERE key='task_schema_version'").fetchone()
        if marker is None:
            return {"status": "not-initialized"}
    finally:
        connection.close()
    from task_runs import TaskStore
    tasks = TaskStore(principal, state_root, read_only=True)
    try:
        return tasks.health()
    finally:
        tasks.close()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    add_state_arguments(parser)
    parser.add_argument("--install-root", default=str(copilot_home()),
                        help="installation containing .margo-files.json (default: ~/.copilot)")
    parser.add_argument("--snapshot", help="JSON export supplied by caller; no direct host access")
    parser.add_argument("--snapshot-max-age-seconds", type=int, default=86400,
                        help="older snapshot evidence is unknown, not healthy (default: 86400)")
    parser.add_argument("--preferences", help="defaults to the selected install root's preferences")
    parser.add_argument("--decision-config", help="defaults to the selected install root's decision config")
    parser.add_argument("--strict", action="store_true", help="exit 1 if report is not healthy")
    args = parser.parse_args(argv)
    try:
        root = Path(args.install_root).expanduser()
        preferences = Path(args.preferences).expanduser() if args.preferences else root / "skills/chief-of-staff/preferences.md"
        decisions = Path(args.decision_config).expanduser() if args.decision_config else root / "skills/decision-log/config.md"
        config = {"preferences": configuration_fields(preferences, REQUIRED_PREFERENCES),
                  "decision_log": configuration_fields(decisions, REQUIRED_DECISION_CONFIG)}
        installation = inspect_installation_manifest(args.install_root)
        snapshot = inspect_snapshot(read_json(args.snapshot) if args.snapshot else {},
                                    args.snapshot_max_age_seconds, installation["status"])
        try:
            state = state_health(args.account, args.state_dir)
            memory = memory_health(args.account, args.state_dir)
            tasks = task_health(args.account, args.state_dir)
        except SetupRequired as exc:
            state = {"status": "setup-needed", "account_configured": False, "all_clear": False, "action": str(exc)}
            memory = {"status": "setup-needed"}
            tasks = {"status": "setup-needed"}
        healthy = (state.get("all_clear", False) and snapshot["status"] == "healthy"
                   and memory["status"] in ("available", "not-initialized", "semantic-unavailable")
                   and tasks["status"] in ("available", "not-initialized")
                   and all(group["status"] == "complete" for group in config.values()))
        status = "healthy" if healthy else "setup-needed" if not state["account_configured"] else "attention-needed"
        report = {"status": status, "checked_at": utc_now(), "configuration": config,
                  "state": state, "memory": memory, "tasks": tasks,
                  "host_snapshot": snapshot, "managed_installation": installation,
                  "limitations": ["No host API/database inspected.",
                                  "Host completed does not establish source coverage or human review.",
                                  "Configured priority labels do not establish outcome definitions, deadlines or capacity feasibility.",
                                  "No notification can run while the machine is asleep.",
                                  "SQLite permissions are not encryption; use protected backups."]}
        print(canonical_json(report))
        return 1 if args.strict and not healthy else 0
    except (StateError, OSError, sqlite3.Error, ValueError, TypeError, KeyError) as exc:
        print(canonical_json({"status": "error", "all_clear": False,
                              "error": str(exc) if isinstance(exc, StateError)
                              else "state unavailable or malformed; no reset performed"}))
        return 2


if __name__ == "__main__":
    sys.exit(main())
