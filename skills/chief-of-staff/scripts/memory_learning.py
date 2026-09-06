"""Explicit, private, execution-backed learning; stored recipes are never executed."""

import hashlib
import json
import math
import sqlite3
from contextlib import nullcontext
from pathlib import Path
from datetime import datetime, timezone

from margo_store import StateError, canonical_json, parse_json, utc_now, validate_timestamp


MAX_OBSERVATIONS = 200
MAX_FILE_BYTES = 1024 * 1024
TREND_TYPES = {
    "meeting_preparation", "focus_fragmentation", "workstream_dependency",
    "draft_edits", "tool_failures", "retrieval_latency", "notification_value",
}
DEFAULT_TREND = {
    "min_independent_events": 3, "window_days": 90, "min_population": 3,
    "min_coverage": 1.0, "require_complete": True,
}


def _digest(value):
    return hashlib.sha256(canonical_json(value).encode("utf-8")).hexdigest()


def _text(value, field):
    if not isinstance(value, str) or not value.strip() or len(value) > 4000:
        raise StateError(field + " requires a bounded nonempty string")
    return value


def _strings(value, field, allow_empty=False):
    if (not isinstance(value, list) or len(value) > MAX_OBSERVATIONS
            or (not value and not allow_empty)):
        raise StateError(field + " requires a bounded list")
    return [_text(item, field) for item in value]


def _time(value):
    return datetime.fromisoformat(validate_timestamp(value))


def _environment(memory, value):
    if not isinstance(value, dict) or not value:
        raise StateError("environment requires host and account")
    _text(value.get("host"), "environment.host")
    if value.get("account") != memory.account:
        raise StateError("environment account does not match this private store")
    if len(canonical_json(value)) > 16000:
        raise StateError("environment exceeds the observation budget")
    return json.loads(canonical_json(value))


def _file(path, expected=None):
    if not isinstance(path, (str, Path)):
        raise StateError("observation path must name a local file")
    source = Path(path).expanduser()
    if source.is_symlink():
        raise StateError("observation must be a regular local file, not a symlink")
    source = source.resolve()
    try:
        with source.open("rb") as stream:
            payload = stream.read(MAX_FILE_BYTES + 1)
    except OSError as exc:
        raise StateError("observation file is unavailable") from exc
    if len(payload) > MAX_FILE_BYTES:
        raise StateError("observation exceeds the file budget")
    fingerprint = hashlib.sha256(payload).hexdigest()
    if expected is not None and fingerprint != expected:
        raise StateError("observation file revision changed")
    return payload, {"kind": "file", "ref": str(source), "revision": fingerprint}


def _json_file(path, expected=None):
    payload, source = _file(path, expected)
    try:
        data = parse_json(payload)
    except (UnicodeDecodeError, ValueError) as exc:
        raise StateError("observation must contain valid JSON") from exc
    if not isinstance(data, dict):
        raise StateError("observation JSON must be an object")
    return data, source


def _data(record):
    return {key: value for key, value in record.items()
            if key not in {"id", "account", "revision", "status", "created_at", "updated_at"}}


def _refs(values):
    return sorted({_digest(ref): ref for ref in values}.values(), key=canonical_json)


def _prior(memory, key, domain="agent"):
    identity = memory.record_id(domain, key)
    return next((row for row in memory.list(domain=domain) if row["id"] == identity), None)


def _conditional(prior, revision):
    if revision is not None and type(revision) is not int:
        raise StateError("learning revision must be an integer")
    if prior and (type(revision) is not int or revision != prior["revision"]):
        raise StateError("learning revision conflict; review the current record")
    if not prior and revision not in (None, 1):
        raise StateError("new learning revision must be 1")


def _normalise_proposal(memory, data):
    if hasattr(memory, "_normalise_data"):
        return memory._resolve_work_sources(memory._normalise_data(data))
    return data


def _validation_scope(metadata):
    return _digest([metadata.get("base_environment"), metadata.get("content_hash")])


def capabilities(memory, skills_directory=None, environment=None, tools=None):
    """Observe files/host-exported manifests only; never install, invoke or grant tools.

    ``tools`` is a local JSON manifest with environment, observed_at, complete and tools.
    A tool entry has name, version, input_schema, exposed, dependencies and permissions.
    A host export is evidence of exposure, not evidence of successful execution.
    """
    with memory.transaction() if hasattr(memory, "transaction") else nullcontext():
        return _capabilities(memory, skills_directory, environment, tools)


def _capabilities(memory, skills_directory, environment, tools):
    account = getattr(memory, "account", "unbound")
    supplied_environment = environment is not None
    env = _environment(memory, environment) if supplied_environment else {
        "host": "unbound", "account": account}
    inputs, origins = [], []
    if skills_directory is not None:
        root = Path(skills_directory).expanduser().resolve()
        if not root.is_dir():
            raise StateError("installed skills directory does not exist")
        paths = sorted(root.glob("*/SKILL.md"))
        if len(paths) > MAX_OBSERVATIONS:
            raise StateError("skill inventory exceeds the bounded observation budget")
        origin = "skills:" + _digest(str(root))
        origins.append((origin, env))
        for path in paths:
            if path.is_symlink() or path.parent.is_symlink():
                continue
            _, source = _file(path)
            name = path.parent.name
            inputs.append({
                "name": name, "capability_kind": "skill", "state": "installed",
                "content_hash": source["revision"], "version": source["revision"],
                "origin": origin, "source_refs": [source], "base_environment": env,
                "availability": "installed_file_only", "required_permissions": [], "dependencies": {},
            })
    if tools is not None:
        manifest, source = _json_file(tools)
        tool_env = _environment(memory, manifest.get("environment"))
        if supplied_environment and tool_env != env:
            raise StateError("manifest environment does not match requested environment")
        stamp = validate_timestamp(manifest.get("observed_at"))
        if _time(stamp) > datetime.now(timezone.utc):
            raise StateError("host observation cannot be in the future")
        entries = manifest.get("tools")
        if not isinstance(entries, list) or len(entries) > MAX_OBSERVATIONS:
            raise StateError("host manifest needs a bounded tools list")
        if type(manifest.get("complete")) is not bool:
            raise StateError("host manifest must state whether coverage is complete")
        collection = _text(manifest.get("scope", "host-tools"), "manifest scope")
        origin = "tools:" + _digest([tool_env["account"], tool_env["host"], collection])
        legacy_origin = "tools:" + _digest(source["ref"])
        incoming_names = {entry.get("name") for entry in entries if isinstance(entry, dict)
                          and isinstance(entry.get("name"), str)}
        clock_key = "learning:inventory-clock:" + origin
        clock = memory.conn.execute("SELECT value FROM memory_meta WHERE key=?", (clock_key,)).fetchone()
        previous_stamps = [json.loads(clock[0])["observed_at"]] if clock else []
        previous_stamps.extend(
            row["metadata"]["observed_at"] for row in memory.list(domain="agent")
            if row["kind"] == "capability" and row.get("metadata", {}).get("capability_kind") == "tool"
            and row["metadata"].get("base_environment", {}).get("host") == tool_env["host"]
            and (row["metadata"].get("origin") in {origin, legacy_origin}
                 or row["metadata"].get("name") in incoming_names)
            and row["metadata"].get("observed_at"))
        if previous_stamps and _time(stamp) < max(_time(value) for value in previous_stamps):
            raise StateError("host inventory is older than an already accepted observation")
        memory.conn.execute(
            "INSERT INTO memory_meta VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            (clock_key, canonical_json({"observed_at": stamp, "complete": manifest["complete"]})))
        if manifest["complete"]:
            origins.append((origin, tool_env))
            origins.append((legacy_origin, tool_env))
        names = set()
        for entry in entries:
            if not isinstance(entry, dict):
                raise StateError("tool entry must be an object")
            name = _text(entry.get("name"), "tool name")
            if name in names:
                raise StateError("duplicate tool identity in host manifest")
            names.add(name)
            _text(entry.get("version"), "tool version")
            if not isinstance(entry.get("input_schema"), dict):
                raise StateError("tool entry requires its exposed input_schema")
            if type(entry.get("exposed")) is not bool:
                raise StateError("tool entry requires observed exposed boolean")
            permissions = _strings(entry.get("required_permissions", []), "permissions", True)
            dependencies = entry.get("dependencies", {})
            if not isinstance(dependencies, dict):
                raise StateError("tool dependencies must be versioned values")
            fingerprint = _digest({key: entry.get(key, default) for key, default in (
                ("version", None), ("input_schema", None), ("required_permissions", []),
                ("dependencies", {}))})
            inputs.append({
                "name": name, "capability_kind": "tool", "version": entry["version"],
                "content_hash": fingerprint, "state": "installed" if entry["exposed"] else "discovered",
                "availability": "host_exposed" if entry["exposed"] else "documented_not_exposed",
                "input_schema": entry["input_schema"], "source_refs": [source],
                "origin": origin, "base_environment": tool_env, "observed_at": stamp,
                "inventory_scope": collection,
                "required_permissions": permissions, "dependencies": dependencies,
            })
    if skills_directory is None and tools is None:
        raise StateError("provide installed skills or a host tool manifest")
    observed, retired = [], []
    prior_rows = memory.list(domain="agent")
    for item in inputs:
        kind, name = item["capability_kind"], item["name"]
        base = item["base_environment"]
        key = ("installed-skill:" + name if kind == "skill" and base["host"] == "unbound"
               else "capability:" + _digest([base["account"], base["host"], kind, name]))
        prior = next((row for row in prior_rows if row["id"] == memory.record_id("agent", key)), None)
        if prior and prior["status"] in {"forgotten", "suppressed", "rejected"}:
            continue
        requirements = dict(base)
        requirements[kind + ":" + name] = {
            "version": item["version"], "content_hash": item["content_hash"],
            "dependencies": item["dependencies"], "required_permissions": item["required_permissions"],
        }
        metadata = dict(item, environment_requirements=requirements, inventory_source_refs=item["source_refs"],
                        validation="not_validated", no_execution_authority=True)
        metadata.pop("source_refs")
        if kind == "skill":
            metadata["skill_name"] = name
        record_data = {
            "domain": "agent", "kind": "capability", "title": kind.capitalize() + " capability: " + name,
            "text": "Observed %s for %s. Discovery or installation does not establish competence or grant permission."
                    % (item["availability"], name),
            "authority": "source_observed", "scope": "capabilities:" + base["host"],
            "sensitivity": "private", "allowed_uses": ["reasoning"], "entities": [kind + ":" + name],
            "routines": [], "source_refs": item["source_refs"], "metadata": metadata,
        }
        if prior:
            old = prior.get("metadata", {})
            # Observation refresh may reset readiness, but cannot erase scoped failure/replay history.
            metadata["validation_history"] = [
                dict(event, scope=event.get("scope", _validation_scope(old)))
                for event in old.get("validation_history", [])
            ]
            comparable = ("content_hash", "base_environment", "availability", "origin")
            if old.get("state") != "retired" and all(old.get(field) == metadata.get(field) for field in comparable):
                # Refreshing the export file must not silently transfer validation to a new source.
                if (old.get("inventory_source_refs", prior["source_refs"]) == record_data["source_refs"]
                        and (not hasattr(memory, "_work_sources_current") or memory._work_sources_current(prior))):
                    observed.append(prior["id"])
                    continue
        record = memory.put(key, record_data, status="active", revision=prior["revision"] if prior else None)
        observed.append(record["id"])
    for prior in prior_rows:
        meta = prior.get("metadata", {})
        if (prior["kind"] != "capability" or prior["id"] in observed
                or prior["status"] != "active" or meta.get("state") == "retired"
                or (meta.get("origin"), meta.get("base_environment")) not in origins):
            continue
        changed = _data(prior)
        changed["metadata"] = dict(meta, state="retired", validation="not_validated")
        changed["text"] = "This capability is absent from the current complete inventory; no permission was changed."
        if tools is not None and meta.get("capability_kind") == "tool":
            changed["source_refs"] = [source]
            changed["metadata"]["observed_at"] = stamp
        record = memory.revise(prior["id"], changed, "active", prior["revision"])
        retired.append(record["id"])
    return {"observed": len(observed), "memory_ids": observed, "retired_ids": retired,
            "note": "Files and host manifests only; no tools executed and no competence inferred."}


def classify_error(code, *, confirmed_defect=False):
    """Conservative taxonomy; an unfamiliar error is not automatically a product defect."""
    normalized = str(code or "").casefold().replace("-", "_")
    groups = {
        "caller_mistake": {"invalid_argument", "invalid_query", "bad_request", "400", "inefficientfilter"},
        "stale_host_binding": {"tool_not_found", "binding_stale", "schema_changed", "unknown_tool_binding"},
        "policy_denial": {"access_denied", "forbidden", "policy_denied", "403"},
        "missing_capability": {"not_implemented", "capability_missing", "unsupported_operation"},
        "bad_data": {"invalid_json", "invalid_input_shape", "malformed_data"},
        "transient_environment_failure": {"timeout", "rate_limited", "429", "503", "oauth_expired", "401"},
    }
    for category, codes in groups.items():
        if normalized in codes:
            return category
    return "actual_defect" if normalized == "confirmed_defect" and confirmed_defect else "unclassified"


def _input_contract(value, schema, depth=0):
    """Bounded JSON-schema subset. Unsupported keywords fail closed, not a false pass."""
    supported = {"type", "required", "properties", "additionalProperties", "items", "enum",
                 "description", "title", "$schema", "minimum", "maximum", "minLength", "maxLength"}
    if not isinstance(schema, dict) or depth > 10 or set(schema) - supported:
        raise StateError("synthetic validator cannot establish this schema; use an existing execution receipt")
    for field in ("minimum", "maximum"):
        if field in schema and (type(schema[field]) not in (int, float) or not math.isfinite(schema[field])):
            raise StateError("invalid numeric constraint in exposed schema")
    for field in ("minLength", "maxLength"):
        if field in schema and (type(schema[field]) is not int or schema[field] < 0):
            raise StateError("invalid length constraint in exposed schema")
    if "enum" in schema and (not isinstance(schema["enum"], list) or not schema["enum"]):
        raise StateError("invalid enum in exposed schema")
    kind = schema.get("type")
    types = {"object": dict, "array": list, "string": str, "boolean": bool,
             "integer": int, "number": (int, float), "null": type(None)}
    if kind is not None and (not isinstance(kind, str) or kind not in types):
        raise StateError("synthetic validator does not support this schema type")
    if kind is not None and (not isinstance(value, types[kind])
                             or (kind in {"integer", "number"} and isinstance(value, bool))):
        return False
    if "enum" in schema and canonical_json(value) not in [canonical_json(option) for option in schema["enum"]]:
        return False
    if isinstance(value, dict):
        properties = schema.get("properties", {})
        required = schema.get("required", [])
        if (not isinstance(properties, dict) or not isinstance(required, list)
                or any(not isinstance(key, str) for key in required)):
            raise StateError("invalid exposed object schema")
        if any(key not in value for key in required):
            return False
        extra = set(value) - set(properties)
        additional = schema.get("additionalProperties", True)
        if not isinstance(additional, (bool, dict)):
            raise StateError("invalid additionalProperties in exposed schema")
        if additional is False and extra:
            return False
        if isinstance(additional, dict) and any(
                not _input_contract(value[key], additional, depth + 1) for key in extra):
            return False
        if any(not _input_contract(value[key], nested, depth + 1)
               for key, nested in properties.items() if key in value):
            return False
    if isinstance(value, list) and "items" in schema:
        if len(value) > MAX_OBSERVATIONS:
            raise StateError("synthetic array exceeds the exercise budget")
        if any(not _input_contract(item, schema["items"], depth + 1) for item in value):
            return False
    if isinstance(value, str):
        if len(value) < schema.get("minLength", 0) or len(value) > schema.get("maxLength", MAX_FILE_BYTES):
            return False
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        if not math.isfinite(value) or value < schema.get("minimum", -math.inf) or value > schema.get("maximum", math.inf):
            return False
    return True


def _observation(memory, capability, evidence):
    if not isinstance(evidence, dict):
        raise StateError("validation requires an evidence reference, not an assertion of success")
    meta = capability["metadata"]
    requirements = _environment(memory, meta.get("environment_requirements"))
    if requirements["host"] == "unbound":
        raise StateError("validation requires a named host environment")
    if evidence.get("kind") == "synthetic_observation":
        if set(evidence) != {"kind", "fixture"} or not isinstance(evidence["fixture"], dict):
            raise StateError("synthetic evidence requires only its pinned fixture")
        fixture = evidence["fixture"]
        if set(fixture) != {"path", "sha256"}:
            raise StateError("synthetic fixture requires path and sha256")
        _text(fixture["sha256"], "fixture hash")
        document, source = _json_file(fixture["path"], fixture["sha256"])
        if (document.get("synthetic") is not True or document.get("environment") != requirements
                or document.get("capability_id") != capability["id"] or "input" not in document):
            raise StateError("synthetic fixture must pin the capability and exact environment")
        if meta.get("capability_kind") != "tool" or meta.get("availability") != "host_exposed":
            raise StateError("synthetic input-contract exercise requires an exposed tool schema")
        if not set(meta["input_schema"]) & {"type", "required", "properties", "enum"}:
            raise StateError("an unconstrained schema cannot establish an input-contract validation")
        success = _input_contract(document["input"], meta["input_schema"])
        return {
            "id": "synthetic:" + _digest([capability["id"], source["revision"]]),
            "outcome": "succeeded" if success else "failed",
            "error_category": None if success else "bad_data",
            "level": "synthetic_input_contract_only", "runtime_validated": False,
            "source_refs": [source], "evidence": evidence,
            "environment": requirements,
            "note": "Only the supplied input shape was exercised locally; remote execution was not tested.",
        }
    if evidence.get("kind") != "execution_receipt" or set(evidence) != {"kind", "attempt_id"}:
        raise StateError("use a reproducible synthetic fixture or an existing execution_receipt attempt_id")
    _text(evidence["attempt_id"], "attempt_id")
    try:
        row = memory.conn.execute(
            "SELECT e.* FROM work_executions e JOIN work_actions a ON a.id=e.action_id "
            "WHERE e.id=? AND a.account=?", (evidence["attempt_id"], memory.account)).fetchone()
    except sqlite3.OperationalError as exc:
        if "no such table" not in str(exc):
            raise
        row = None
    if row is None or row["state"] not in {"succeeded", "failed"} or not row["receipt"]:
        raise StateError("validation needs a settled, existing execution in this account")
    from work_ledger import Ledger
    receipt = json.loads(row["receipt"])
    Ledger.validate_receipt(receipt, row["state"])
    if (receipt.get("environment") != requirements or receipt.get("capability_id") != capability["id"]
            or receipt.get("capability_hash") != meta["content_hash"]):
        raise StateError("stored receipt does not prove this exact capability/version/host environment")
    category = None if row["state"] == "succeeded" else classify_error(
        receipt.get("error_code"), confirmed_defect=bool(receipt.get("defect_reference")))
    return {
        "id": "execution:" + row["id"], "outcome": row["state"], "error_category": category,
        "executed_at": validate_timestamp(receipt["recorded_at"]),
        "level": "existing_execution_receipt", "runtime_validated": row["state"] == "succeeded",
        "source_refs": [{"kind": "tool_result", "ref": "execution:" + row["id"], "revision": _digest(receipt)}],
        "evidence": evidence, "environment": requirements,
        "note": "Existing receipt only; no action was performed for validation.",
    }


def validation(memory, capability_id, revision, evidence):
    """Record a reproducible local exercise or a settled receipt; no tool is invoked."""
    with memory.transaction():
        current = memory.show(capability_id)
        _conditional(current, revision)
        if current["kind"] != "capability" or current["status"] != "active":
            raise StateError("validation requires an active capability observation")
        if current["metadata"].get("state") in {"discovered", "retired"}:
            raise StateError("unavailable capabilities cannot be validated")
        if not memory._work_sources_current(current):
            raise StateError("capability evidence changed; refresh the inventory before validation")
        result = _observation(memory, current, evidence)
        signature = _digest(result)
        previous = current["metadata"].get("last_validation", {})
        history = current["metadata"].get("validation_history", [])
        scope = _validation_scope(current["metadata"])
        scoped_history = [event for event in history if event.get("scope", scope) == scope]
        if (previous.get("fingerprint") == signature
                or any(event["id"] == result["id"] for event in scoped_history)):
            return current
        if len(history) >= MAX_OBSERVATIONS:
            raise StateError("capability validation history budget exceeded; review maintenance before adding evidence")
        if result.get("executed_at") and any(
                event.get("executed_at", "") > result["executed_at"] for event in scoped_history):
            raise StateError("older execution evidence cannot supersede a newer validation outcome")
        event = {key: result[key] for key in ("id", "outcome", "error_category", "level", "executed_at")
                 if key in result}
        event["scope"] = scope
        data = _data(current)
        data["source_refs"] = _refs(current["source_refs"] + result["source_refs"])
        data["metadata"] = dict(current["metadata"],
                                state="validated" if result["outcome"] == "succeeded" else "degraded",
                                validation=result["level"], last_validation=dict(result, fingerprint=signature),
                                validation_history=history + [event])
        data["text"] = "%s: %s. %s No new execution authority." % (
            current["metadata"]["name"], result["outcome"], result["note"])
        return memory.revise(capability_id, data, "active", revision)


def _feedback(memory, reference):
    if (not isinstance(reference, dict) or set(reference) != {"id", "revision"}
            or type(reference["revision"]) is not int):
        raise StateError("feedback requires its exact id and revision")
    try:
        row = memory.conn.execute(
            "SELECT r.*,v.evidence FROM work_records r JOIN work_record_revisions v "
            "ON v.record_id=r.id AND v.revision=r.revision WHERE r.id=? AND r.account=?",
            (reference["id"], memory.account)).fetchone()
    except sqlite3.OperationalError as exc:
        if "no such table" not in str(exc):
            raise
        row = None
    if (row is None or row["kind"] != "feedback" or row["revision"] != reference["revision"]
            or row["state"] != "recorded"):
        raise StateError("feedback must be existing, exact, and not marked do_not_learn")
    data = json.loads(row["data"])
    if data.get("do_not_learn") or not row["evidence"]:
        raise StateError("feedback does not permit learning or lacks recorded human evidence")
    from work_ledger import human
    confirmation = json.loads(row["evidence"])
    human(confirmation, row["id"], row["revision"], "confirm")
    return {"kind": "tool_result", "ref": "feedback:" + row["id"],
            "revision": _digest([data, confirmation])}


def lesson_plan(memory, key, data, revision=None):
    """Persist a candidate recipe grounded in local observations, never executable code.

    Required: title, scope, trigger, goal, preconditions[], procedure[], risk,
    capability_refs[{id,revision}], evidence[{capability_id,observation}], counterexamples[].
    Counterexamples contain capability_id, observation and condition. feedback_refs is optional.
    """
    if not isinstance(data, dict):
        raise StateError("lesson proposal must be an object")
    for field in ("title", "scope", "trigger", "goal"):
        _text(data.get(field), field)
    preconditions = _strings(data.get("preconditions"), "preconditions")
    procedure = _strings(data.get("procedure"), "procedure")
    if data.get("risk") not in {"read_only", "local_mutation", "approval_required_external"}:
        raise StateError("lesson must preserve an explicit action boundary")
    references = data.get("capability_refs")
    if not isinstance(references, list) or not 1 <= len(references) <= 20:
        raise StateError("lesson needs bounded pinned capability_refs")
    with memory.transaction():
        caps, sources, environment = {}, [], {}
        for reference in references:
            if (not isinstance(reference, dict) or set(reference) != {"id", "revision"}
                    or type(reference["revision"]) is not int):
                raise StateError("capability ref requires exact id and revision")
            cap = memory.show(reference["id"])
            if (cap["kind"] != "capability" or cap["status"] != "active"
                    or cap["revision"] != reference["revision"]
                    or cap["metadata"].get("state") not in {"validated", "degraded"}
                    or not memory._work_sources_current(cap)):
                raise StateError("lesson requires current, exercised capability evidence")
            if cap["id"] in caps:
                raise StateError("duplicate capability reference")
            for name, value in cap["metadata"]["environment_requirements"].items():
                if name in environment and environment[name] != value:
                    raise StateError("lesson cannot combine incompatible environments")
                environment[name] = value
            caps[cap["id"]] = cap
            sources.append({"kind": "memory_record", "ref": cap["id"], "revision": str(cap["revision"])})
        observations, counterexamples, observed_ids = [], [], set()
        for field in ("evidence", "counterexamples"):
            entries = data.get(field)
            if (not isinstance(entries, list) or len(entries) > MAX_OBSERVATIONS
                    or (field == "evidence" and not entries)):
                raise StateError("lesson requires bounded evidence and explicit counterexamples lists")
            for entry in entries:
                if not isinstance(entry, dict) or entry.get("capability_id") not in caps:
                    raise StateError("lesson evidence must identify a pinned capability")
                result = _observation(memory, caps[entry["capability_id"]], entry.get("observation"))
                if result["id"] in observed_ids:
                    raise StateError("repeated evidence is not an independent lesson observation")
                observed_ids.add(result["id"])
                if field == "counterexamples":
                    _text(entry.get("condition"), "counterexample condition")
                    if result["outcome"] != "failed":
                        raise StateError("a counterexample requires observed failure, not an invented assertion")
                    counterexamples.append(dict(entry, error_category=result["error_category"]))
                else:
                    observations.append(dict(entry, outcome=result["outcome"], level=result["level"],
                                             error_category=result["error_category"]))
                sources.extend(result["source_refs"])
        feedback_refs = data.get("feedback_refs", [])
        if not isinstance(feedback_refs, list) or len(feedback_refs) > 20:
            raise StateError("feedback_refs exceeds the lesson budget")
        for reference in feedback_refs:
            sources.append(_feedback(memory, reference))
        successful = [row for row in observations if row["outcome"] == "succeeded"]
        metadata = {
            "trigger": data["trigger"], "goal": data["goal"], "preconditions": preconditions,
            "procedure": procedure, "risk": data["risk"], "counterexamples": counterexamples,
            "environment_requirements": environment, "capability_refs": references,
            "observations": observations, "feedback_refs": feedback_refs,
            "evidence_ids": sorted(observed_ids), "no_execution_authority": True,
            "confidence_basis": "bounded_success_evidence" if successful else "failure_or_correction_only",
            "runtime_validated": any(row["level"] == "existing_execution_receipt" for row in successful),
            "known_capability_failures": [
                {"capability_id": cap["id"], **event}
                for cap in caps.values() for event in cap["metadata"].get("validation_history", [])
                if event["outcome"] == "failed"
                and event.get("scope", _validation_scope(cap["metadata"])) == _validation_scope(cap["metadata"])],
            "applicability": "candidate_requires_exact_review",
        }
        lesson_text = "\n".join([
            "Trigger: " + data["trigger"], "Goal: " + data["goal"],
            "Preconditions:", *("- " + item for item in preconditions),
            "Proposed procedure (advisory data):", *("- " + item for item in procedure),
            "Counterexamples:", *("- " + item["condition"] + " [" + item["error_category"] + "]"
                                  for item in counterexamples),
            "Capability failure categories: " + ", ".join(sorted({
                item["error_category"] or "unclassified" for item in metadata["known_capability_failures"]}))
            if metadata["known_capability_failures"] else "No additional capability failures were supplied.",
            ("Evidence limit: an existing scoped execution receipt supports a bounded success, not general competence."
             if metadata["runtime_validated"] else
             "Evidence limit: synthetic input-contract checks only; remote execution is not validated.")
            if successful else "Evidence limit: failure or correction only; the proposed recovery is not validated.",
            "Action boundary: " + data["risk"] + "; no stored lesson grants permissions or lowers existing approvals.",
        ])
        if len(lesson_text) > 10000:
            raise StateError("lesson exceeds the bounded recipe budget; propose a smaller scoped lesson")
        record_data = {
            "domain": "agent", "kind": "lesson", "title": data["title"],
            "text": lesson_text,
            "authority": "inferred", "scope": data["scope"], "sensitivity": "private",
            "allowed_uses": ["reasoning"], "entities": data.get("entities", []),
            "routines": data.get("routines", []), "source_refs": _refs(sources), "metadata": metadata,
        }
        if data.get("review_after"):
            record_data["review_after"] = validate_timestamp(data["review_after"])
        record_data = _normalise_proposal(memory, record_data)
        prior = _prior(memory, key)
        if prior and prior["status"] != "candidate":
            raise StateError("reviewed lessons are immutable through proposal creation; propose a separate replacement")
        if (prior and _data(prior) == record_data
                and (revision is None or type(revision) is int and revision == prior["revision"])):
            return prior
        _conditional(prior, revision)
        return memory.put(key, record_data, status="candidate", revision=revision)


def activate_lesson(memory, memory_id, revision, evidence):
    """Exact human review activates a scoped recipe, not permission to execute it."""
    with memory.transaction():
        current = memory.show(memory_id)
        _conditional(current, revision)
        if current["kind"] != "lesson" or current["status"] != "candidate":
            raise StateError("only a current lesson candidate can be activated")
        if current.get("review_after") and _time(current["review_after"]) <= datetime.now(timezone.utc):
            raise StateError("lesson review deadline passed; revalidate before activation")
        if not memory._work_sources_current(current):
            raise StateError("lesson evidence changed; create and review a fresh proposal")
        metadata = current["metadata"]
        if metadata.get("confidence_basis") != "bounded_success_evidence":
            raise StateError("a failed-only recipe needs a successful bounded exercise before activation")
        for reference in metadata["capability_refs"]:
            cap = memory.show(reference["id"])
            if cap["revision"] != reference["revision"] or cap["metadata"]["state"] != "validated":
                raise StateError("activation requires currently validated capabilities")
        for entry in metadata["observations"] + metadata["counterexamples"]:
            cap = memory.show(entry["capability_id"])
            observed = _observation(memory, cap, entry["observation"])
            if (any(ref not in current["source_refs"] for ref in observed["source_refs"])
                    or observed["outcome"] != entry.get("outcome", "failed")):
                raise StateError("lesson execution evidence changed; review a fresh proposal")
        for reference in metadata["feedback_refs"]:
            _feedback(memory, reference)
        memory._human(evidence, memory_id, revision, "confirm")
        changed = _data(current)
        changed["authority"] = "user_confirmed"
        changed["metadata"] = dict(metadata, applicability="reviewed_scoped_recipe")
        return memory.revise(memory_id, changed, "active", revision, evidence)


def _definition(value):
    if not isinstance(value, dict) or set(value) != set(DEFAULT_TREND):
        raise StateError("trend definition requires " + ", ".join(sorted(DEFAULT_TREND)))
    for field, lower, upper in (("min_independent_events", 3, MAX_OBSERVATIONS),
                                ("window_days", 1, 366), ("min_population", 3, 1000000)):
        if type(value[field]) is not int or not lower <= value[field] <= upper:
            raise StateError("invalid trend threshold: " + field)
    if (type(value["min_coverage"]) not in (int, float) or not 0 < value["min_coverage"] <= 1
            or type(value["require_complete"]) is not bool):
        raise StateError("invalid trend coverage threshold")
    if value["min_population"] < value["min_independent_events"]:
        raise StateError("trend population threshold must include supporting events")
    return dict(value)


def configure_trend(memory, name, definition, revision=None, evidence=None):
    """Preview without evidence; persist only an exact reviewed configuration."""
    if name not in TREND_TYPES:
        raise StateError("unsupported aggregate trend type; people scoring is not supported")
    definition = _definition(definition)
    with memory.transaction():
        meta_key = "learning:trend-definition:" + name
        row = memory.conn.execute("SELECT value FROM memory_meta WHERE key=?", (meta_key,)).fetchone()
        current = json.loads(row[0]) if row else {"revision": 0}
        if revision is not None and (type(revision) is not int or revision != current["revision"]):
            raise StateError("trend definition revision conflict")
        manifest = {"account": memory.account, "name": name, "definition": definition,
                    "expected_revision": current["revision"]}
        preview = dict(manifest, subject_id="trend-definition:" + _digest(manifest), revision=1)
        if evidence is None:
            return preview
        memory._human(evidence, preview["subject_id"], 1, "configure")
        saved = dict(manifest, revision=current["revision"] + 1, evidence=evidence)
        memory.conn.execute("INSERT INTO memory_meta VALUES (?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                            (meta_key, canonical_json(saved)))
        return saved


def trend(memory, key, data, revision=None):
    """Aggregate supplied resolved events; duplicate reports are corroboration, not samples.

    Events contain id, independence_key, occurred_at, matched and source_refs. Coverage
    is complete/partial or {status, observed_population, expected_population}. Population
    always describes the observed population, never a missing-as-zero extrapolation.
    Legacy event_ids remain explicitly caller-resolved, with no verified event dates.
    """
    with memory.transaction() if hasattr(memory, "transaction") else nullcontext():
        return _trend(memory, key, data, revision)


def _trend(memory, key, data, revision):
    if not isinstance(data, dict):
        raise StateError("trend input must be an object")
    for name in ("title", "text", "scope", "window_start", "window_end"):
        _text(data.get(name), name)
    start, end = validate_timestamp(data["window_start"]), validate_timestamp(data["window_end"])
    now = _time(utc_now())
    if _time(start) >= _time(end):
        raise StateError("trend window must be ordered")
    if _time(end) > now:
        raise StateError("trend window must end at or before the observation time")
    name = data.get("trend_type", "meeting_preparation")
    if name not in TREND_TYPES:
        raise StateError("unsupported aggregate trend type; people scoring is not supported")
    definition, definition_revision = dict(DEFAULT_TREND), 0
    if hasattr(memory, "conn"):
        configured = memory.conn.execute(
            "SELECT value FROM memory_meta WHERE key=?", ("learning:trend-definition:" + name,)).fetchone()
        if configured:
            config = json.loads(configured[0])
            definition, definition_revision = _definition(config["definition"]), config["revision"]
    if (_time(end) - _time(start)).total_seconds() > definition["window_days"] * 86400:
        raise StateError("trend window exceeds its configured duration")
    entities = data.get("entities", [])
    if (not isinstance(entities, list)
            or any(not isinstance(item, str) or item.startswith(("person:", "people:", "colleague:")) for item in entities)):
        raise StateError("trends are aggregate observations, not named-person scoring")
    events, groups, sources = {}, {}, []
    supplied = data.get("events")
    if supplied is not None:
        if not isinstance(supplied, list) or not 1 <= len(supplied) <= MAX_OBSERVATIONS:
            raise StateError("trend needs a bounded resolved event list")
        for original in supplied:
            if not isinstance(original, dict):
                raise StateError("resolved event must be an object")
            identity = _text(original.get("id"), "event.id")
            independent = _text(original.get("independence_key"), "event.independence_key")
            stamp = validate_timestamp(original.get("occurred_at"))
            if not _time(start) <= _time(stamp) < _time(end):
                raise StateError("supporting event lies outside the declared window")
            if _time(stamp) > now:
                raise StateError("a future event is not an observed trend sample")
            if type(original.get("matched")) is not bool:
                raise StateError("event requires a resolved matched boolean")
            refs = original.get("source_refs")
            if not isinstance(refs, list) or not refs:
                raise StateError("each resolved event requires citable sources")
            normalized = {"id": identity, "independence_key": independent,
                          "occurred_at": stamp, "matched": original["matched"]}
            if identity in events and events[identity] != normalized:
                raise StateError("conflicting reports for the same event")
            if independent in groups and groups[independent] != original["matched"]:
                raise StateError("contradictory reports for one independent event")
            events[identity], groups[independent] = normalized, original["matched"]
            sources.extend(refs)
        event_ids = sorted(identity for identity, event in events.items() if event["matched"])
        independent_ids = sorted(key for key, matched in groups.items() if matched)
        independence = "caller_resolved_event_groups; cross-source independence_not_independently_verified"
    else:
        event_ids = data.get("event_ids")
        if (not isinstance(event_ids, list) or len(event_ids) > MAX_OBSERVATIONS
                or not all(isinstance(event, str) and event.strip() for event in event_ids)
                or len(set(event_ids)) != len(event_ids)):
            raise StateError("legacy event_ids must be distinct caller-resolved IDs")
        event_ids = sorted(event_ids)
        independent_ids = event_ids
        independence = "caller_declared_independence_and_dates_not_independently_verified"
        sources = data.get("source_refs", [])
    count, denominator = len(independent_ids), data.get("population")
    if count < definition["min_independent_events"]:
        raise StateError("insufficient independent events for the configured trend threshold")
    if (type(denominator) is not int or denominator < max(
            len(groups), count, definition["min_population"])):
        raise StateError("trend population must include all resolved supporting and nonmatching events")
    coverage = data.get("coverage")
    if isinstance(coverage, str):
        if coverage != "complete":
            raise StateError("partial coverage requires observed and expected population counts")
        coverage = {"status": coverage, "observed_population": denominator, "expected_population": denominator}
    if (not isinstance(coverage, dict) or coverage.get("status") not in {"complete", "partial"}
            or coverage.get("observed_population") != denominator
            or type(coverage.get("expected_population")) is not int
            or coverage["expected_population"] < denominator):
        raise StateError("coverage must identify observed and expected populations")
    fraction = denominator / coverage["expected_population"]
    if coverage["status"] == "complete" and fraction != 1:
        raise StateError("complete coverage cannot have missing population")
    if ((definition["require_complete"] and coverage["status"] != "complete")
            or fraction < definition["min_coverage"]):
        raise StateError("incomplete coverage cannot establish this configured trend; retain a gap")
    if not isinstance(sources, list) or not sources:
        raise StateError("trend needs citable source references")
    record = {
        "domain": data.get("domain", "agent"), "kind": "trend", "title": data["title"],
        "text": data["text"], "authority": "inferred", "scope": data["scope"],
        "source_refs": _refs(sources), "entities": entities, "routines": data.get("routines", []),
        "sensitivity": "private", "allowed_uses": ["reasoning"],
        "metadata": {
            "trend_type": name, "definition": definition, "definition_revision": definition_revision,
            "event_ids": event_ids, "independent_event_ids": independent_ids,
            "resolved_events": sorted(events.values(), key=lambda event: event["id"]),
            "window_start": start, "window_end": end, "coverage": coverage["status"],
            "coverage_fraction": fraction, "expected_population": coverage["expected_population"],
            "population": denominator, "observations": count, "rate": count / denominator,
            "independence": independence, "status": "hypothesis_not_confirmed",
            "rate_scope": "observed_population_only", "no_execution_authority": True,
        },
    }
    record = _normalise_proposal(memory, record)
    prior = _prior(memory, key, record["domain"])
    if prior:
        if (_data(prior) == record
                and (revision is None or type(revision) is int and revision == prior["revision"])):
            return prior
        _conditional(prior, revision)
        if prior["status"] != "candidate":
            raise StateError("reviewed or rejected trends cannot be overwritten by aggregation")
        previous = {event["id"]: event for event in prior["metadata"].get("resolved_events", [])}
        if any(event["id"] in previous and event != previous[event["id"]] for event in events.values()):
            raise StateError("resolved event changed; review the contradictory evidence separately")
        independent_outcomes = {event["independence_key"]: event["matched"] for event in previous.values()}
        if any(key in independent_outcomes and matched != independent_outcomes[key] for key, matched in groups.items()):
            raise StateError("independent event outcome changed; review the contradictory evidence separately")
    return memory.put(key, record, status="candidate", revision=revision)


def _preference_sections(content):
    import re

    # Template fields can wrap lines. Drop balanced spans before extracting sections.
    cleaned, depth = [], 0
    for char in content:
        if char == "{":
            depth += 1
        elif char == "}":
            if not depth:
                raise StateError("unbalanced preference placeholder; review the file before import")
            depth -= 1
        elif not depth:
            cleaned.append(char)
    if depth:
        raise StateError("unclosed preference placeholder; review the file before import")
    content = "".join(cleaned)
    pattern = re.compile(r"(?m)^[-*]\s+\*\*[^*\n]+:\*\*[ \t]*\(")
    while True:
        match = pattern.search(content)
        if not match:
            break
        start = match.end() - 1
        depth, end = 1, start + 1
        while end < len(content) and depth:
            depth += (content[end] == "(") - (content[end] == ")")
            end += 1
        if depth:
            raise StateError("unclosed example field; review preferences before import")
        content = content[:start] + content[end:]
    sections, heading, lines = [], None, []
    for line in content.splitlines():
        if re.match(r"^## ", line):
            if heading and lines:
                sections.append((heading, "\n".join(lines).strip()))
            heading, lines = line[3:].strip(), []
        elif heading:
            lines.append(line)
    if heading and lines:
        sections.append((heading, "\n".join(lines).strip()))
    selected = []
    for title, body in sections:
        cleaned = []
        in_fence = False
        for line in body.splitlines():
            if line.startswith("```"):
                in_fence = not in_fence
            # Unfilled examples are not user facts. Leave substantive saved prose intact.
            if not in_fence and (re.search(r"\{[^}]+\}", line)
                                 or re.search(r":\*\*\s*(?:\(e\.g\.|$)", line)):
                continue
            cleaned.append(line)
        text = "\n".join(cleaned).strip()
        if text and any(char.isalnum() for char in text):
            selected.append((title, text))
    return selected


def _preference_file(path):
    source = Path(path).expanduser().resolve()
    if not source.is_file():
        raise StateError("preferences file is missing")
    try:
        payload, source_ref = _file(source)
    except StateError as exc:
        raise StateError(
            "preferences file is unavailable or exceeds the 1 MiB source budget") from exc
    try:
        content = payload.decode("utf-8")
    except UnicodeDecodeError as exc:
        raise StateError("preferences file must contain valid UTF-8") from exc
    return source, content, source_ref


def preference_sections(path):
    """Preserve source sections rather than guessing people IDs or extracting a biography."""
    _, content, _ = _preference_file(path)
    return _preference_sections(content)


def preferences_plan(memory, path):
    """Bind approval to source content AND the exact target revisions it would replace."""
    source, content, source_ref = _preference_file(path)
    fingerprint = source_ref["revision"]
    records, omitted = [], []
    for section_number, (title, text) in enumerate(_preference_sections(content), 1):
        key_base = "preferences:" + hashlib.sha256(str(source).encode("utf-8")).hexdigest() + ":" + title
        # Each search record is short enough to embed without silently truncating a whole section.
        paragraphs = [part.strip() for part in text.split("\n\n") if part.strip()]
        chunks, chunk = [], ""
        for paragraph in paragraphs:
            if len(paragraph) > 1500:
                for line_number, line in enumerate(paragraph.splitlines()):
                    if len(line) > 1500:
                        raise StateError(
                            "preference section %d contains a line over the 1500-character "
                            "memory chunk budget; review/split that section first" % section_number)
                    separator = "\n\n" if line_number == 0 else "\n"
                    if not chunk:
                        chunk = line
                    elif len(chunk) + len(separator) + len(line) <= 1500:
                        chunk += separator + line
                    else:
                        chunks.append(chunk)
                        chunk = line
            else:
                if not chunk:
                    chunk = paragraph
                elif len(chunk) + 2 + len(paragraph) <= 1500:
                    chunk += "\n\n" + paragraph
                else:
                    chunks.append(chunk)
                    chunk = paragraph
        if chunk:
            chunks.append(chunk)
        for offset, chunk in enumerate(chunks):
            kind = "preference" if any(word in title.casefold() for word in (
                "scheduling", "communication", "triage", "brief preferences")) else "profile"
            routines = []
            if "scheduling" in title.casefold():
                routines = ["calendar", "outcomes"]
            elif "communication" in title.casefold():
                routines = ["drafting", "meeting-prep", "work-products"]
            record_data = {
                "domain": "user", "kind": kind, "title": title + (" (%d)" % (offset + 1) if len(chunks) > 1 else ""),
                "text": chunk, "authority": "user_confirmed" if kind == "preference" else "source_observed",
                "scope": "personal", "sensitivity": "private", "allowed_uses": ["reasoning"],
                "entities": ["user"], "routines": routines,
                "source_refs": [source_ref],
                "metadata": {"imported_configuration": True, "source_heading": title,
                             "not_independently_verified": kind != "preference"},
            }
            try:
                record_data = _normalise_proposal(memory, record_data)
            except StateError as exc:
                raise StateError(
                    "preference section %d chunk %d is not a valid memory record: %s"
                    % (section_number, offset + 1, exc)) from exc
            key = key_base + ":" + str(offset)
            try:
                identity = memory.record_id("user", key)
            except StateError as exc:
                raise StateError(
                    "preference section %d cannot be assigned a valid memory identity: %s"
                    % (section_number, exc)) from exc
            prior = next((record for record in memory.list(domain="user") if record["id"] == identity), None)
            if prior and prior["status"] == "forgotten":
                omitted.append(identity)
                continue
            records.append({"id": identity, "key": key, "expected_revision": prior["revision"] if prior else None,
                            "expected_status": prior["status"] if prior else None, "data": record_data})
    try:
        _file(source, fingerprint)
    except StateError as exc:
        raise StateError("preferences changed while building the import preview") from exc
    manifest = {"account": memory.account, "source_hash": fingerprint, "records": records, "omitted_forgotten": omitted}
    subject = "preferences-import:" + hashlib.sha256(canonical_json(manifest).encode("utf-8")).hexdigest()
    return dict(manifest, subject_id=subject, revision=1, requires_human_import=True)


def import_preferences(memory, path, evidence):
    """Apply one revision-bound import plan atomically, never rebind a stale approval."""
    from work_ledger import human
    with memory.transaction():
        if not isinstance(evidence, dict):
            raise StateError("preferences import requires explicit human evidence")
        receipt_key = "import-receipt:" + str(evidence.get("subject_id", ""))
        previous = memory.conn.execute("SELECT value FROM memory_meta WHERE key=?", (receipt_key,)).fetchone()
        if previous:
            receipt = json.loads(previous[0])
            human(evidence, evidence["subject_id"], 1, "import")
            try:
                _, current_source = _file(Path(path).expanduser().resolve())
            except StateError as exc:
                raise StateError("import source changed; obtain approval of a fresh preview") from exc
            if current_source["revision"] != receipt["source_hash"]:
                raise StateError("import source changed; obtain approval of a fresh preview")
            for expected in receipt["targets"]:
                current = memory.show(expected["id"])
                if current["revision"] != expected["revision"]:
                    raise StateError("import target changed since approval; obtain a fresh preview")
            return {"imported": 0, "memory_ids": [row["id"] for row in receipt["targets"]],
                    "replayed": True, "source_hash": receipt["source_hash"]}
        plan = preferences_plan(memory, path)
        human(evidence, plan["subject_id"], 1, "import")
        result = []
        for item in plan["records"]:
            target_revision = item["expected_revision"] or 1
            confirmation = dict(
                evidence, subject_id=item["id"], revision=target_revision,
                decision="edit" if item["expected_status"] == "active" else "confirm",
                approved_import_subject=plan["subject_id"], approved_import_revision=1,
            )
            record = memory.put(item["key"], item["data"], status="active",
                                revision=target_revision, evidence=confirmation)
            result.append(record["id"])
        receipt = {"source_hash": plan["source_hash"],
                   "targets": [{"id": identity, "revision": memory.show(identity)["revision"]} for identity in result]}
        memory.conn.execute("INSERT INTO memory_meta VALUES (?,?)", (receipt_key, canonical_json(receipt)))
    return {"imported": len(result), "memory_ids": result, "source_hash": plan["source_hash"], "replayed": False,
            "note": "Imported only saved configuration; profile observations are not independently verified facts."}
