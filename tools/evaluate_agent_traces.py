#!/usr/bin/env python3
"""Evaluate imported model/host traces against versioned synthetic scenarios."""

import argparse
from datetime import datetime, timedelta, timezone
import hashlib
import json
import sys
from pathlib import Path
from journey_json import canonical, load as load_contract_json


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SCENARIOS = ROOT / "evals/scenarios-v1.json"
DEFAULT_FIXTURES = ROOT / "tests/fixtures/journeys/sources.json"
COUNTERS = ("tool_calls", "model_calls", "input_tokens", "output_tokens", "elapsed_ms")
PROVENANCE = {
    "model", "model_version", "host", "host_version", "skill_versions", "source_versions",
}
TRACE_FIELDS = {
    "schema_version", "scenario_id", "fixture_version", "provenance", "output", "events",
    "counters", "human_review", "fixture_sha256",
}
TOOL_EFFECTS = {
    **{name: "read" for name in (
        "calendar.get", "calendar.list", "files.read", "github.list-review-requests", "mail.get",
        "mail.search", "meeting.recap-status", "people.lookup", "teams.get-thread", "teams.search",
        "memory.search", "task.show", "task.list")},
    **{name: "write" for name in (
        "mail.reply", "mail.send", "mail.forward", "calendar.create", "calendar.update", "calendar.cancel",
        "calendar.accept", "calendar.decline", "calendar.tentative", "teams.post", "teams.reply",
        "teams.react", "files.upload", "files.copy", "files.share", "planner.create", "planner.update",
        "ado.create", "ado.update")},
    **{name: "local" for name in ("task.create", "task.pause", "task.cancel", "memory.capture")},
}
SCENARIO_FIELDS = {
    "id", "journey_feature_id", "fixture_version", "prompt", "assertions", "human_rubric",
}
ASSERTION_FIELDS = {
    "required_routes", "forbidden_routes", "required_tools", "required_source_refs",
    "required_labels", "forbidden_claims", "max_counters", "approval_before_write",
    "forbid_write",
}


class TraceError(ValueError):
    pass


def load_json(path):
    return load_contract_json(path, TraceError)


def fixture_hash(fixtures, version):
    matches = [value for value in fixtures.get("fixtures", {}).values() if value.get("version") == version]
    if len(matches) != 1:
        raise TraceError("fixture version must resolve to exactly one source bundle")
    return hashlib.sha256(canonical(matches[0]).encode("utf-8")).hexdigest()


def _nonempty(value):
    return isinstance(value, str) and bool(value.strip())


def _check(identifier, status, detail, actual=None, expected=None):
    result = {"id": identifier, "status": status, "detail": detail}
    if actual is not None:
        result["actual"] = actual
    if expected is not None:
        result["expected"] = expected
    return result


def validate_scenarios(scenarios):
    if not isinstance(scenarios, dict) or type(scenarios.get("schema_version")) is not int or scenarios["schema_version"] != 1:
        raise TraceError("evaluation scenarios must use schema_version 1")
    if not _nonempty(scenarios.get("version")):
        raise TraceError("evaluation scenarios require a nonempty version")
    rows = scenarios.get("scenarios")
    if not isinstance(rows, list) or not rows:
        raise TraceError("evaluation scenarios require a nonempty scenarios list")
    seen = set()
    for index, scenario in enumerate(rows):
        label = "scenario[%d]" % index
        if not isinstance(scenario, dict) or set(scenario) != SCENARIO_FIELDS:
            raise TraceError(label + " has missing or unknown fields")
        for field in ("id", "journey_feature_id", "fixture_version", "prompt"):
            if not _nonempty(scenario[field]):
                raise TraceError("%s.%s must be nonempty" % (label, field))
        if scenario["id"] in seen:
            raise TraceError("duplicate evaluation scenario: " + scenario["id"])
        seen.add(scenario["id"])
        assertions = scenario["assertions"]
        if not isinstance(assertions, dict) or set(assertions) != ASSERTION_FIELDS:
            raise TraceError(label + ".assertions has missing or unknown fields")
        for field in ASSERTION_FIELDS - {"max_counters", "approval_before_write", "forbid_write"}:
            value = assertions[field]
            if (not isinstance(value, list)
                    or any(not _nonempty(item) for item in value)
                    or len(value) != len(set(value))):
                raise TraceError("%s.assertions.%s must be distinct nonempty strings" % (label, field))
        for field in ("approval_before_write", "forbid_write"):
            if type(assertions[field]) is not bool:
                raise TraceError("%s.assertions.%s must be boolean" % (label, field))
        counters = assertions["max_counters"]
        if (not isinstance(counters, dict) or not counters or set(counters) - set(COUNTERS)
                or any(isinstance(value, bool) or not isinstance(value, int) or value < 0
                       for value in counters.values())):
            raise TraceError(label + ".assertions.max_counters must contain nonnegative known counters")
        rubric = scenario["human_rubric"]
        if (not isinstance(rubric, list) or not rubric
                or any(not _nonempty(item) for item in rubric)
                or len(rubric) != len(set(rubric))):
            raise TraceError(label + ".human_rubric must contain distinct nonempty dimensions")
    return rows


def validate_fixture_bindings(rows, fixtures):
    if not isinstance(fixtures, dict) or fixtures.get("schema_version") != 1:
        raise TraceError("source fixtures must use schema_version 1")
    versions = {}
    for identity, fixture in fixtures.get("fixtures", {}).items():
        if not isinstance(fixture, dict) or not _nonempty(fixture.get("version")):
            raise TraceError("source fixture %s has no version" % identity)
        if fixture["version"] in versions:
            raise TraceError("duplicate source fixture version: " + fixture["version"])
        sources = fixture.get("sources")
        if not isinstance(sources, list) or not sources:
            raise TraceError("source fixture %s has no sources" % identity)
        source_ids = []
        for source in sources:
            if not isinstance(source, dict) or not _nonempty(source.get("id")):
                raise TraceError("source fixture %s contains a source without an id" % identity)
            source_ids.append(source["id"])
        if len(source_ids) != len(set(source_ids)):
            raise TraceError("source fixture %s contains duplicate source ids" % identity)
        versions[fixture["version"]] = set(source_ids)
    for scenario in rows:
        available = versions.get(scenario["fixture_version"])
        if available is None:
            raise TraceError("unknown fixture_version for scenario " + scenario["id"])
        missing = sorted(set(scenario["assertions"]["required_source_refs"]) - available)
        if missing:
            raise TraceError("%s cites sources absent from its fixture: %s"
                             % (scenario["id"], ", ".join(missing)))


def _strict_trace_errors(trace, scenario):
    errors = []
    if not isinstance(trace, dict):
        return ["trace must be an object"]
    if set(trace) - TRACE_FIELDS:
        errors.append("unknown trace fields: " + ", ".join(sorted(set(trace) - TRACE_FIELDS)))
    if type(trace.get("schema_version")) is not int or trace["schema_version"] != 1:
        errors.append("trace schema_version must equal 1")
    if trace.get("scenario_id") != scenario["id"]:
        errors.append("trace scenario_id does not match the selected scenario")
    if trace.get("fixture_version") != scenario["fixture_version"]:
        errors.append("trace fixture_version does not match the scenario")
    provenance = trace.get("provenance")
    if not isinstance(provenance, dict) or set(provenance) != PROVENANCE:
        errors.append("provenance must contain exactly " + ", ".join(sorted(PROVENANCE)))
    else:
        for field in ("model", "model_version", "host", "host_version"):
            if not _nonempty(provenance[field]):
                errors.append("provenance.%s must be nonempty" % field)
        for field in ("skill_versions", "source_versions"):
            if not isinstance(provenance[field], dict) or not provenance[field]:
                errors.append("provenance.%s must be a nonempty version map" % field)
            elif any(not _nonempty(key) or not _nonempty(value) for key, value in provenance[field].items()):
                errors.append("provenance.%s keys and values must be nonempty strings" % field)
        if isinstance(provenance.get("skill_versions"), dict):
            missing_skills = sorted(set(scenario["assertions"]["required_routes"])
                                    - set(provenance["skill_versions"]))
            if missing_skills:
                errors.append("skill_versions missing required routes: " + ", ".join(missing_skills))
        if (isinstance(provenance.get("source_versions"), dict)
                and scenario["fixture_version"] not in provenance["source_versions"]):
            errors.append("source_versions must include the exact fixture_version")
    counters = trace.get("counters")
    if not isinstance(counters, dict) or set(counters) != set(COUNTERS):
        errors.append("counters must explicitly contain " + ", ".join(COUNTERS))
    elif any(value is not None and (isinstance(value, bool) or not isinstance(value, int) or value < 0)
             for value in counters.values()):
        errors.append("counter values must be nonnegative integers or null for unknown")
    output = trace.get("output")
    if output is not None:
        if not isinstance(output, dict) or set(output) != {"text", "source_refs", "labels"}:
            errors.append("output must contain exactly text, source_refs and labels")
        else:
            if not _nonempty(output["text"]):
                errors.append("output.text must be nonempty")
            for field in ("source_refs", "labels"):
                if (not isinstance(output[field], list)
                        or any(not _nonempty(item) for item in output[field])):
                    errors.append("output.%s must be a list of nonempty strings" % field)
    events = trace.get("events")
    if events is not None:
        if not isinstance(events, list):
            errors.append("events must be a list")
        else:
            sequences = []
            for event in events:
                if not isinstance(event, dict) or type(event.get("seq")) is not int or not _nonempty(event.get("type")):
                    errors.append("each event requires integer seq and nonempty type")
                    continue
                sequences.append(event["seq"])
                kind = event["type"]
                shapes = {
                    "route": ({"seq", "type", "skill"}, set()),
                    "tool_call": ({"seq", "type", "tool", "effect"}, {"account", "action_id", "revision", "action_hash", "request"}),
                    "human_approval": ({"seq", "type", "decision", "account", "action_id", "revision", "action_hash", "request"}, set()),
                }
                if kind not in shapes:
                    errors.append("unsupported event type: " + kind)
                    continue
                required, optional = shapes[kind]
                if not required <= set(event) or set(event) - required - optional:
                    errors.append("event has missing/unknown fields: " + kind)
                    continue
                if kind == "route" and not _nonempty(event["skill"]):
                    errors.append("route event must identify a skill")
                if kind == "tool_call":
                    expected = TOOL_EFFECTS.get(event["tool"]) if isinstance(event["tool"], str) else None
                    if expected is None or event["effect"] != expected:
                        errors.append("tool effect is unknown or contradicts the versioned tool vocabulary")
                if kind == "human_approval" and event["decision"] not in {"approve", "reject", "revoke"}:
                    errors.append("unsupported human decision")
                if kind == "human_approval" or (kind == "tool_call" and event.get("effect") == "write"):
                    if (any(not _nonempty(event.get(key)) for key in ("account", "action_id", "action_hash"))
                            or type(event.get("revision")) is not int or event["revision"] < 1
                            or not isinstance(event.get("request"), dict) or not event["request"]):
                        errors.append("approval/write events require exact account, action, revision, hash and request")
                    try:
                        canonical(event.get("request", {}))
                    except (TypeError, ValueError, RecursionError):
                        errors.append("event request must be finite bounded JSON")
            if sequences != list(range(1, len(sequences) + 1)):
                errors.append("event seq values must be contiguous starting at 1")
    review = trace.get("human_review")
    if review is not None:
        if not isinstance(review, dict) or set(review) != {"reviewer", "reviewed_at", "judgments"}:
            errors.append("human_review requires reviewer, reviewed_at and judgments")
        elif not _nonempty(review["reviewer"]) or not _nonempty(review["reviewed_at"]) or not isinstance(review["judgments"], dict):
            errors.append("human_review fields are malformed")
        else:
            try:
                reviewed = datetime.fromisoformat(review["reviewed_at"].replace("Z", "+00:00"))
                if reviewed.tzinfo is None or reviewed > datetime.now(timezone.utc) + timedelta(minutes=1):
                    raise ValueError()
            except ValueError:
                errors.append("human review needs an actual explicitly zoned, nonfuture timestamp")
    return errors


def _event_routes(events):
    return [event.get("skill") for event in events if event.get("type") == "route" and _nonempty(event.get("skill"))]


def _write_events(events):
    return [event for event in events if event.get("type") == "tool_call" and event.get("effect") == "write"]


def evaluate_trace(trace, scenario, fixtures=None):
    limitations = [
        "Deterministic trace checks do not judge prose usefulness.",
        "Human rubric judgments are reported separately and are never replaced by one model score.",
        "A synthetic trace does not prove authenticated identity, live provider behavior, or production routing.",
    ]
    errors = _strict_trace_errors(trace, scenario)
    if fixtures is not None and isinstance(trace, dict):
        if trace.get("fixture_sha256") != fixture_hash(fixtures, scenario["fixture_version"]):
            errors.append("fixture content fingerprint differs from the imported trace")
    missing = []
    if isinstance(trace, dict):
        if trace.get("output") is None:
            missing.append("output")
        if trace.get("events") is None:
            missing.append("events")
    else:
        missing.extend(("output", "events"))
    if errors or missing:
        return {
            "scenario_id": scenario["id"],
            "status": "not_evaluated",
            "binding_errors": errors,
            "missing_evidence": missing,
            "checks": [],
            "human_judgments": {},
            "evidence_limitations": limitations,
        }

    assertions = scenario["assertions"]
    output, events, counters = trace["output"], trace["events"], trace["counters"]
    checks = []
    if fixtures is None:
        checks.append(_check("fixture-content", "unknown", "No source bundle was supplied for content verification."))
    routes = _event_routes(events)
    for skill in assertions["required_routes"]:
        checks.append(_check("route-required:" + skill, "pass" if skill in routes else "fail",
                             "Required route was observed." if skill in routes else "Required route was absent.",
                             routes, skill))
    for skill in assertions["forbidden_routes"]:
        checks.append(_check("route-forbidden:" + skill, "pass" if skill not in routes else "fail",
                             "Forbidden route was absent." if skill not in routes else "Forbidden route was observed.",
                             routes, skill))
    event_tools = [event.get("tool") for event in events
                   if event.get("type") == "tool_call" and _nonempty(event.get("tool"))]
    for tool in assertions["required_tools"]:
        checks.append(_check("tool-required:" + tool, "pass" if tool in event_tools else "fail",
                             "Required tool family was observed." if tool in event_tools else "Required tool family was absent.",
                             event_tools, tool))
    for source in assertions["required_source_refs"]:
        checks.append(_check("source-required:" + source, "pass" if source in output["source_refs"] else "fail",
                             "Required source was cited." if source in output["source_refs"] else "Required source was not cited.",
                             output["source_refs"], source))
    available_sources = set(assertions["required_source_refs"])
    if fixtures is not None:
        available_sources = {source["id"] for fixture in fixtures["fixtures"].values()
                             if fixture["version"] == scenario["fixture_version"] for source in fixture["sources"]}
    unexpected_sources = sorted(set(output["source_refs"]) - available_sources)
    checks.append(_check(
        "source-allowlist", "pass" if not unexpected_sources else "fail",
        "All citations belong to the scenario fixture." if not unexpected_sources
        else "Output cited sources outside the scenario fixture.",
        unexpected_sources))
    for label in assertions["required_labels"]:
        checks.append(_check("label-required:" + label, "pass" if label in output["labels"] else "fail",
                             "Required uncertainty/state label was present." if label in output["labels"] else "Required label was absent.",
                             output["labels"], label))
    lowered = output["text"].casefold()
    for claim in assertions["forbidden_claims"]:
        present = claim.casefold() in lowered
        checks.append(_check("claim-forbidden:" + claim, "fail" if present else "pass",
                             "Forbidden claim was present." if present else "Forbidden claim was absent."))
    budget = assertions["max_counters"]
    for metric, maximum in budget.items():
        actual = counters[metric]
        if actual is None:
            checks.append(_check("budget:" + metric, "unknown",
                                 "Metric was unavailable and is not treated as zero.", expected=maximum))
        else:
            checks.append(_check("budget:" + metric, "pass" if actual <= maximum else "fail",
                                 "Measured resource use was within budget." if actual <= maximum
                                 else "Measured resource use exceeded budget.", actual, maximum))
    for metric in set(COUNTERS) - set(budget):
        if counters[metric] is None:
            checks.append(_check("metric:" + metric, "unknown", "Metric was not supplied; no zero value was inferred."))
    observed_tool_calls = sum(event.get("type") == "tool_call" for event in events)
    if counters["tool_calls"] is not None:
        checks.append(_check(
            "counter-consistency:tool_calls",
            "pass" if counters["tool_calls"] == observed_tool_calls else "fail",
            "Measured tool-call counter matches the imported events."
            if counters["tool_calls"] == observed_tool_calls
            else "Measured tool-call counter differs from the imported events.",
            counters["tool_calls"], observed_tool_calls))
    if assertions["approval_before_write"]:
        writes = _write_events(events)
        grants, ordered = set(), bool(writes)
        for event in events:
            if event["type"] == "human_approval" or (
                    event["type"] == "tool_call" and event["effect"] == "write"):
                identity = (event["account"], event["action_id"], event["revision"],
                            event["action_hash"], canonical(event["request"]))
                if event["type"] == "human_approval":
                    if event["decision"] == "approve":
                        grants.add(identity)
                    else:
                        grants.discard(identity)
                else:
                    ordered = ordered and identity in grants
                    grants.discard(identity)
        checks.append(_check("approval-ordering", "pass" if ordered else "fail",
                             "Every write followed approval for its exact action hash." if ordered
                             else "A write lacked preceding approval for its exact action hash."))
    if assertions["forbid_write"]:
        writes = _write_events(events)
        checks.append(_check("no-write", "pass" if not writes else "fail",
                             "No outbound write was observed." if not writes else "Outbound write was observed."))

    human = {}
    review = trace.get("human_review")
    required_dimensions = scenario["human_rubric"]
    for dimension in required_dimensions:
        judgment = review.get("judgments", {}).get(dimension) if review else None
        if (not isinstance(judgment, dict) or set(judgment) != {"rating", "notes"}
                or judgment.get("rating") not in {"pass", "fail", "needs-review"}
                or not _nonempty(judgment.get("notes"))):
            human[dimension] = {"rating": "not-reviewed", "notes": "No valid human judgment was imported."}
        else:
            human[dimension] = judgment
    deterministic_fail = any(check["status"] == "fail" for check in checks)
    deterministic_unknown = any(check["status"] == "unknown" for check in checks)
    human_fail = any(row["rating"] == "fail" for row in human.values())
    human_incomplete = any(row["rating"] in {"not-reviewed", "needs-review"} for row in human.values())
    if deterministic_fail or human_fail:
        status = "fail"
    elif deterministic_unknown or human_incomplete:
        status = "needs_review"
    else:
        status = "pass"
    return {
        "scenario_id": scenario["id"],
        "status": status,
        "binding_errors": [],
        "missing_evidence": [],
        "checks": checks,
        "human_judgments": human,
        "provenance": trace["provenance"],
        "counters": counters,
        "evidence_limitations": limitations,
    }


def evaluate_results(results, scenarios, fixtures=None):
    rows = validate_scenarios(scenarios)
    if fixtures is not None:
        validate_fixture_bindings(rows, fixtures)
    by_id = {row["id"]: row for row in rows}
    if (not isinstance(results, dict) or set(results) != {"schema_version", "traces"}
            or type(results["schema_version"]) is not int or results["schema_version"] != 1):
        raise TraceError("results must contain schema_version 1 and traces")
    traces = results.get("traces")
    if not isinstance(traces, list):
        raise TraceError("results must be an object with a traces list")
    supplied = {}
    duplicate_ids = set()
    for trace in traces:
        identity = trace.get("scenario_id") if isinstance(trace, dict) else None
        if identity in supplied:
            duplicate_ids.add(identity)
        supplied[identity] = trace
    reports = []
    for identity, scenario in by_id.items():
        if identity not in supplied:
            reports.append({
                "scenario_id": identity, "status": "not_evaluated", "binding_errors": [],
                "missing_evidence": ["trace"], "checks": [], "human_judgments": {},
                "evidence_limitations": ["No imported host/model trace was supplied; this is not a pass."],
            })
        elif identity in duplicate_ids:
            reports.append({
                "scenario_id": identity, "status": "not_evaluated",
                "binding_errors": ["duplicate scenario trace"], "missing_evidence": [],
                "checks": [], "human_judgments": {},
                "evidence_limitations": ["Ambiguous duplicate traces are not evaluated."],
            })
        else:
            reports.append(evaluate_trace(supplied[identity], scenario, fixtures))
    for identity in sorted(set(supplied) - set(by_id), key=lambda value: str(value)):
        reports.append({
            "scenario_id": identity, "status": "not_evaluated",
            "binding_errors": ["unknown scenario_id"], "missing_evidence": [],
            "checks": [], "human_judgments": {},
            "evidence_limitations": ["Unknown scenario bindings are not evaluated."],
        })
    counts = {}
    for report in reports:
        counts[report["status"]] = counts.get(report["status"], 0) + 1
    if counts.get("fail"):
        status = "fail"
    elif set(counts) == {"pass"} and counts["pass"] == len(by_id):
        status = "pass"
    else:
        status = "incomplete"
    return {
        "schema_version": 1,
        "scenario_version": scenarios.get("version"),
        "status": status,
        "counts": counts,
        "results": reports,
        "evidence_limitations": [
            "Only explicitly imported traces are evaluated.",
            "Null resource counters remain unknown.",
            "Passing synthetic cases do not establish production reliability.",
        ],
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results", required=True, help="JSON file containing imported traces")
    parser.add_argument("--scenarios", default=str(DEFAULT_SCENARIOS))
    parser.add_argument("--fixtures", default=str(DEFAULT_FIXTURES))
    args = parser.parse_args(argv)
    try:
        report = evaluate_results(
            load_json(args.results), load_json(args.scenarios), load_json(args.fixtures))
    except TraceError as exc:
        report = {"schema_version": 1, "counts": {"not_evaluated": 1}, "results": [],
                  "errors": [str(exc)],
                  "evidence_limitations": ["The results bundle was not evaluated."]}
        code = 2
    else:
        code = 0 if report["status"] == "pass" else 1
    json.dump(report, sys.stdout, indent=2, sort_keys=True)
    sys.stdout.write("\n")
    return code


if __name__ == "__main__":
    raise SystemExit(main())
