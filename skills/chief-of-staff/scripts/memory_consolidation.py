"""Bounded deterministic review proposals and minimal, once-per-evidence delivery receipts."""

import json

from margo_store import StateError, canonical_json, utc_now
from memory_learning import _data, _digest, _file


def _stored(memory, key):
    row = memory.conn.execute("SELECT value FROM memory_meta WHERE key=?", (key,)).fetchone()
    return json.loads(row[0]) if row else None


def _sources(memory, record, seen=None, budget=None, depth=0):
    seen = set(seen or ())
    budget = budget if budget is not None else [200]
    if record["id"] in seen or depth >= 10:
        return ["dependency_cycle_or_depth_limit"]
    seen.add(record["id"])
    result = []
    refs = record.get("source_refs", [])
    if len(refs) > 200:
        return ["source_budget_exceeded"]
    for ref in refs:
        budget[0] -= 1
        if budget[0] < 0:
            result.append("source_budget_exceeded")
            break
        if ref["kind"] == "memory_record":
            try:
                current = memory.show(ref["ref"])
                stamp = utc_now()
                available = (
                    current.get("authority") != "inferred" and current.get("sensitivity") != "sensitive"
                    and current.get("valid_from", stamp) <= stamp
                    and current.get("valid_to", "9999") > stamp
                    and current.get("review_after", "9999") > stamp)
                observed = [current["id"], current["revision"], current["status"],
                            available,
                            _sources(memory, current, seen, budget, depth + 1)]
            except StateError:
                observed = ["unavailable"]
        elif ref["kind"] == "file":
            try:
                _, source = _file(ref["ref"])
                observed = source["revision"]
            except (StateError, ValueError):
                observed = "unavailable"
        elif ref["kind"] == "work_source":
            observed = memory._work_sources_current({"source_refs": [ref]})
        else:
            observed = ref.get("revision")
        result.append([ref, observed])
    return result


def _current_sources(snapshot):
    for entry in snapshot:
        if not isinstance(entry, list) or len(entry) != 2 or not isinstance(entry[0], dict):
            return False
        ref, observed = entry
        if ref["kind"] == "memory_record" and (
                not isinstance(observed, list) or len(observed) != 5
                or str(observed[1]) != ref.get("revision") or observed[2] != "active"
                or not observed[3] or not _current_sources(observed[4])):
            return False
        if ref["kind"] == "file" and observed != ref.get("revision"):
            return False
        if ref["kind"] == "work_source" and observed is not True:
            return False
    return True


def _trend_evidence(record):
    metadata = record["metadata"]
    independent_outcomes = sorted({
        (event["independence_key"], event["matched"])
        for event in metadata.get("resolved_events", [])
    })
    return _digest(["trend_review", {
        "trend_type": metadata.get("trend_type"),
        "definition": metadata.get("definition"),
        "definition_revision": metadata.get("definition_revision"),
        "window_start": metadata.get("window_start"),
        "window_end": metadata.get("window_end"),
        "coverage": metadata.get("coverage"),
        "coverage_fraction": metadata.get("coverage_fraction"),
        "expected_population": metadata.get("expected_population"),
        "population": metadata.get("population"),
        "observations": metadata.get("observations"),
        "rate": metadata.get("rate"),
        "independent_event_ids": metadata.get("independent_event_ids", []),
        "independent_outcomes": independent_outcomes,
    }])


def _proposal(memory, reason, records, environment):
    refs = [{"id": row["id"], "revision": row["revision"]} for row in records]
    refs.sort(key=lambda row: row["id"])
    identity = "learning-proposal:" + _digest([reason, [ref["id"] for ref in refs]])
    if reason == "review_trend":
        evidence = [_trend_evidence(records[0])]
        target_evidence = [{"id": records[0]["id"], "evidence": evidence}]
    else:
        evidence = [_digest([_data(row), _sources(memory, row), environment])
                    for row in records]
        target_evidence = [{"id": row["id"], "evidence": [token]} for row, token in zip(records, evidence)]
    if not evidence:
        return None
    prior = _stored(memory, "learning:surfaced:" + identity)
    if prior and (set(evidence) == set(prior["evidence"]) if reason == "review_trend"
                  else not set(evidence) - set(prior["evidence"])):
        return None
    unseen = False
    for target in target_evidence:
        previous = _stored(memory, "learning:target-surfaced:" + target["id"]) or {"evidence": []}
        unseen = unseen or (set(target["evidence"]) != set(previous["evidence"])
                            if reason == "review_trend"
                            else bool(set(target["evidence"]) - set(previous["evidence"])))
    if not unseen:
        return None
    return {
        "id": identity, "reason": reason, "targets": refs,
        "source_fingerprint": _digest([_sources(memory, row) for row in records]),
        "evidence": sorted(set(evidence)), "target_evidence": target_evidence, "requires_review": True,
        "action": "Review exact records; no merge, activation, retirement or permission change is performed.",
    }


def consolidation_plan(memory, limit=10, environment=None, after=None, scan_limit=100):
    """Inspect one bounded page; propose review, never rewrite or promote memories.

    Host/account mismatches are excluded. A version change in the same host may propose
    revalidation but cannot downgrade a human-confirmed lesson. Duplicate detection is
    exact and page-local; it deliberately makes no global semantic-deduplication claim.
    """
    if type(limit) is not int or not 1 <= limit <= 50:
        raise StateError("consolidation limit must be between 1 and 50")
    if type(scan_limit) is not int or not 1 <= scan_limit <= 200:
        raise StateError("consolidation scan_limit must be between 1 and 200")
    if after is not None and not isinstance(after, str):
        raise StateError("consolidation cursor must be a string")
    if environment is not None:
        if not isinstance(environment, dict) or environment.get("account") != memory.account:
            raise StateError("consolidation environment must name this account")
    with memory.transaction():
        rows = memory.conn.execute(
            "SELECT id FROM memory_records WHERE account=? AND id>? "
            "AND status IN ('candidate','active') ORDER BY id LIMIT ?",
            (memory.account, after or "", scan_limit + 1)).fetchall()
        scanned = [memory.show(row["id"]) for row in rows[:scan_limit]]
        groups = {}
        for record in scanned:
            requirements = record.get("metadata", {}).get("environment_requirements", {})
            if environment is not None and any(
                    requirements.get(key) and requirements[key] != environment.get(key)
                    for key in ("host", "account")):
                continue
            signature = _digest([record["domain"], record["kind"], record.get("scope"),
                                 record.get("text"), requirements])
            groups.setdefault(signature, []).append(record)
        items, handled, last = [], set(), after
        for record in scanned:
            last = record["id"]
            if record["id"] in handled:
                continue
            requirements = record.get("metadata", {}).get("environment_requirements", {})
            if environment is not None and any(
                    requirements.get(key) and requirements[key] != environment.get(key)
                    for key in ("host", "account")):
                continue
            signature = _digest([record["domain"], record["kind"], record.get("scope"),
                                 record.get("text"), requirements])
            duplicates = [row for row in groups.get(signature, []) if row["status"] == "candidate"]
            proposal = None
            if len(duplicates) > 1:
                proposal = _proposal(memory, "review_exact_duplicates", duplicates, environment)
                handled.update(row["id"] for row in duplicates)
            elif record["kind"] == "lesson" and (
                    not _current_sources(_sources(memory, record))
                    or (environment is not None and not memory._environment_subset(requirements, environment))):
                proposal = _proposal(memory, "revalidate_lesson", [record], environment)
            elif record["kind"] == "capability" and record.get("metadata", {}).get("state") == "degraded":
                proposal = _proposal(memory, "review_capability_failure", [record], environment)
            elif record["status"] == "candidate":
                reason = "review_trend" if record["kind"] == "trend" else "review_candidate"
                proposal = _proposal(memory, reason, [record], environment)
            if proposal:
                items.append(proposal)
                if len(items) >= limit:
                    break
        manifest = {
            "account": memory.account, "items": items,
            "parameters": {"limit": limit, "environment": environment, "after": after, "scan_limit": scan_limit},
            "scanned": sum(record["id"] <= last for record in scanned) if last else 0,
            "next_after": last if rows and (len(rows) > scan_limit or last != scanned[-1]["id"]) else None,
            "deduplication_scope": "exact_content_within_bounded_page_only",
        }
        return dict(manifest, subject_id="consolidation:" + _digest(manifest), revision=1)


def surface_consolidation(memory, plan):
    """Record that this exact proposal page was delivered; repeated delivery is a no-op.

    A stale revision or source snapshot is rejected even on replay. Minimal hashes and
    IDs are persisted, not proposal wording, source excerpts, prompts or transcripts.
    """
    if (not isinstance(plan, dict) or plan.get("account") != memory.account
            or type(plan.get("revision")) is not int or plan["revision"] != 1):
        raise StateError("invalid account-scoped consolidation plan")
    manifest = {key: value for key, value in plan.items() if key not in {"subject_id", "revision"}}
    if plan.get("subject_id") != "consolidation:" + _digest(manifest):
        raise StateError("consolidation plan content changed")
    items = plan.get("items")
    if not isinstance(items, list) or len(items) > 50:
        raise StateError("invalid bounded proposal list")
    with memory.transaction():
        for item in items:
            records = []
            if not isinstance(item, dict) or not isinstance(item.get("targets"), list) or len(item["targets"]) > 200:
                raise StateError("invalid consolidation targets")
            for target in item["targets"]:
                row = memory.show(target["id"])
                if row["revision"] != target["revision"] or row["status"] not in {"candidate", "active"}:
                    raise StateError("consolidation revision conflict; refresh the proposal page")
                records.append(row)
            if _digest([_sources(memory, row) for row in records]) != item.get("source_fingerprint"):
                raise StateError("consolidation source evidence changed; refresh the proposal page")
        receipt_key = "learning:delivery:" + plan["subject_id"]
        if _stored(memory, receipt_key) is not None:
            return {"surfaced": [], "replayed": True, "subject_id": plan["subject_id"]}
        parameters = plan.get("parameters")
        if not isinstance(parameters, dict) or set(parameters) != {"limit", "environment", "after", "scan_limit"}:
            raise StateError("invalid consolidation parameters")
        fresh = consolidation_plan(memory, **parameters)
        if fresh != plan:
            raise StateError("consolidation proposals changed; refresh the proposal page")
        for item in items:
            marker = "learning:surfaced:" + item["id"]
            previous = _stored(memory, marker) or {"evidence": []}
            evidence = (sorted(set(item["evidence"])) if item["reason"] == "review_trend"
                        else sorted(set(previous["evidence"]) | set(item["evidence"])))
            if len(evidence) > 4096:
                raise StateError("delivery history budget exceeded; explicit maintenance review required")
            memory.conn.execute(
                "INSERT INTO memory_meta VALUES (?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                (marker, canonical_json({"evidence": evidence})))
            for target in item["target_evidence"]:
                marker = "learning:target-surfaced:" + target["id"]
                previous = _stored(memory, marker) or {"evidence": []}
                evidence = (sorted(set(target["evidence"])) if item["reason"] == "review_trend"
                            else sorted(set(previous["evidence"]) | set(target["evidence"])))
                if len(evidence) > 4096:
                    raise StateError("target delivery history budget exceeded; explicit maintenance review required")
                memory.conn.execute(
                    "INSERT INTO memory_meta VALUES (?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                    (marker, canonical_json({"evidence": evidence})))
        memory.conn.execute("INSERT INTO memory_meta VALUES (?,?)",
                            (receipt_key, canonical_json({"targets": [item["targets"] for item in items]})))
        return {"surfaced": items, "replayed": False, "subject_id": plan["subject_id"]}
