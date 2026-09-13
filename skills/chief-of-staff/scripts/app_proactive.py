#!/usr/bin/env python3
"""Restricted app-automation preparation. No network, settings writes from tools, or external effects."""

import argparse
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import re
import sqlite3
import sys
from urllib.parse import urlsplit
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

import margo_profile
import margo_store as store
import proactive_state as proactive
from task_runs import TaskStore, zero
from work_ledger import Ledger, digest
from work_productivity import Productivity


ROUTINES = ("morning", "sweep", "eod")
SOURCES = ("mail", "calendar", "direct_messages", "mentions")
TASK_ROUTINE = "app-proactive"
MAX_BYTES = 96 * 1024
MAX_RECORDS = 25
MAX_OUTPUT = 8000


def clock():
    return datetime.now(timezone.utc)


def obj(value, fields, required=None):
    if (not isinstance(value, dict) or set(value) - set(fields)
            or not set(fields if required is None else required) <= set(value)):
        raise store.StateError("Missing or unsupported structured fields.")
    return value


def text(value, maximum=1000, nullable=False):
    if nullable and value is None:
        return None
    if not isinstance(value, str) or not value.strip() or len(value) > maximum or "\x00" in value:
        raise store.StateError("Expected bounded nonempty text.")
    return value


def policy(value):
    obj(value, {"enabled", "timezone", "workdays", "slots", "grace_minutes"})
    if type(value["enabled"]) is not bool:
        raise store.StateError("Policy enabled must be explicit.")
    try:
        ZoneInfo(text(value["timezone"], 100))
    except (ZoneInfoNotFoundError, ValueError) as exc:
        raise store.StateError("IANA timezone data is unavailable or invalid; no timezone was guessed.") from exc
    days = value["workdays"]
    if (not isinstance(days, list) or not days or len(days) > 7
            or any(type(day) is not int or not 0 <= day <= 6 for day in days) or len(set(days)) != len(days)):
        raise store.StateError("Workdays are distinct ISO weekdays 0=Monday through 6=Sunday.")
    slots = obj(value["slots"], set(ROUTINES))
    for routine, values in slots.items():
        if (not isinstance(values, list) or not 1 <= len(values) <= (12 if routine == "sweep" else 1)
                or len(set(values)) != len(values)
                or any(not isinstance(slot, str) or not re.fullmatch(r"(?:[01]\d|2[0-3]):[0-5]\d", slot) for slot in values)):
            raise store.StateError("Each routine needs explicit local HH:MM slots, at most one anchor or twelve sweeps.")
    all_slots = sorted(slot for values in slots.values() for slot in values)
    grace = value["grace_minutes"]
    if type(grace) is not int or not 1 <= grace <= 15:
        raise store.StateError("Slot grace must be 1-15 minutes.")
    minutes = [int(slot[:2]) * 60 + int(slot[3:]) for slot in all_slots]
    if any(b - a < 15 for a, b in zip(minutes, minutes[1:])):
        raise store.StateError("Routine slots must be separated by at least 15 minutes.")
    return value


def read_policy():
    _, config, revision = margo_profile._configuration()
    account = store.resolve_account()
    policies = config.get("app_proactivity", {})
    if not isinstance(policies, dict):
        raise store.StateError("Invalid app_proactivity configuration.")
    value = policies.get(account)
    return {"account": account, "revision": revision, "configured": value is not None,
            "policy": policy(value) if value is not None else None}


def configure(value, revision):
    value = policy(value)
    def update(data, account):
        values = data.setdefault("app_proactivity", {})
        if not isinstance(values, dict):
            raise store.StateError("Invalid app_proactivity configuration.")
        values[account] = value
    margo_profile.update_configuration(None, None, revision, update)
    return dict(read_policy(), schedules_changed=False, private_data_initialized=False)


def due(value, routine, now):
    if routine not in ROUTINES:
        raise store.StateError("Unknown routine.")
    if value is None:
        return {"status": "skipped", "reason": "policy_not_configured"}
    if not value["enabled"]:
        return {"status": "skipped", "reason": "local_preparation_disabled"}
    zone = ZoneInfo(value["timezone"])
    local = now.astimezone(zone)
    if local.weekday() not in value["workdays"]:
        return {"status": "skipped", "reason": "outside_workdays"}
    valid = []
    for slot in value["slots"][routine]:
        hour, minute = map(int, slot.split(":"))
        candidate = local.replace(hour=hour, minute=minute, second=0, microsecond=0)
        utc = candidate.astimezone(timezone.utc)
        # A skipped DST wall time never becomes a delayed synthetic slot.
        if utc.astimezone(zone).replace(fold=0) != candidate.replace(fold=0):
            continue
        elapsed = (now - utc).total_seconds()
        if 0 <= elapsed < value["grace_minutes"] * 60:
            valid.append((slot, utc))
    if not valid:
        return {"status": "skipped", "reason": "outside_slot_grace", "missed_slots_replayed": False}
    slot, instant = valid[-1]
    key = "app-proactive:%s:%s:%s" % (routine, local.date().isoformat(), slot)
    start = local.replace(hour=0, minute=0, second=0, microsecond=0)
    end = start + timedelta(days=1)
    return {"status": "due", "key": key, "routine": routine, "slot": slot,
            "local_date": local.date().isoformat(), "timezone": value["timezone"],
            "deadline": (instant + timedelta(minutes=value["grace_minutes"])).isoformat(),
            "window": {"start": (now - timedelta(hours=24)).isoformat(), "end": now.isoformat()},
            "calendar_window": {"start": start.astimezone(timezone.utc).isoformat(), "end": end.astimezone(timezone.utc).isoformat()}}


def open_tasks(writable=False):
    account = store.resolve_account()
    check = TaskStore(account, read_only=True)
    try:
        proactive.validate_schema(check.conn)
    except Exception:
        check.close()
        raise
    if not writable:
        return check
    check.close()
    connection = store.connect(account)
    try:
        tasks = TaskStore.from_connection(connection, account)
        tasks._owns_connection = True
        return tasks
    except Exception:
        connection.close()
        raise


def context(routine):
    settings = read_policy()
    timing = due(settings["policy"], routine, clock())
    result = dict(timing, account=settings["account"], policy_revision=settings["revision"],
                  memory_required=False, m365_authentication="not_checked",
                  scope=list(SOURCES) + ["existing_local_commitments"])
    if timing["status"] != "due":
        return result
    tasks = open_tasks()
    try:
        previous = tasks.find(timing["key"])
        if previous:
            return dict(result, status="skipped", reason="slot_already_recorded", run_id=previous["id"],
                        previous_status=previous["status"], output_replayed=False)
        if tasks.has_live_claim(TASK_ROUTINE):
            return dict(result, status="skipped", reason="another_routine_claim_active")
        listed = tasks.ledger.list("all", limit=40)
        result["commitments"] = [{"id": item["id"], "revision": item["revision"], "state": item["state"],
            "confirmed": item["confirmed"], "data": {key: item["data"].get(key) for key in
                ("title", "owner", "direction", "due", "next_step", "blocker")}}
            for item in listed["items"] if item["state"] not in {"resolved", "rejected", "cancelled"}]
        result["commitments_truncated"] = "items" in listed["truncated"]
    finally:
        tasks.close()
    path = Path(__file__).resolve().parent.parent / "preferences.md"
    with path.open(encoding="utf-8") as stream:
        content = stream.read(65537)
    if len(content) > 65536:
        raise store.StateError("Preferences exceed restricted context budget; no broad read performed.")
    allowed = {"About me", "Priorities & projects", "Communication & drafting voice"}
    result["preferences"] = {heading: body.strip() for heading, body in
        re.findall(r"^## ([^\n]+)\n(.*?)(?=^## |\Z)", content, re.M | re.S) if heading in allowed}
    if len(store.canonical_json(result)) > 24000:
        raise store.StateError("Restricted context exceeds budget; no silent truncation of settings.")
    result["assistant_name"] = margo_profile.show(settings["account"])["assistant_name"]
    result["limits"] = {"provider_calls": 12, "observations_per_source": MAX_RECORDS,
                        "candidates": 5, "private_artifacts": 1, "output_chars": MAX_OUTPUT}
    return result


def start(value, host):
    obj(value, {"routine", "identity"})
    identity = obj(value["identity"], {"principal", "observed_at", "evidence_ref"})
    settings = read_policy()
    if identity["principal"] != settings["account"]:
        raise store.StateError("Reported provider identity does not match the configured owner; no ingestion allowed.")
    observed = datetime.fromisoformat(store.validate_timestamp(identity["observed_at"]))
    if not timedelta(0) <= clock() - observed <= timedelta(minutes=5):
        raise store.StateError("Identity observation must be a current provider read within five minutes.")
    if not text(identity["evidence_ref"], 300).startswith("tool:"):
        raise store.StateError("Provider identity needs actual tool-result provenance, not source instructions.")
    timing = due(settings["policy"], value["routine"], clock())
    if timing["status"] != "due":
        return timing
    tasks = open_tasks(True)
    try:
        with tasks.transaction():
            old = tasks.find(timing["key"])
            if old:
                return {"status": "skipped", "reason": "slot_already_recorded", "run_id": old["id"], "output_replayed": False}
            if tasks.has_live_claim(TASK_ROUTINE):
                return {"status": "skipped", "reason": "another_routine_claim_active"}
            bound = {"account": tasks.account, "host": host,
                     "capabilities": {"app.prepare": "1", "policy.revision": settings["revision"]},
                     "observed_at": clock().isoformat()}
            cost = dict(zero(), tool_calls=16, model_calls=1, pages=4, items=100, output_chars=MAX_OUTPUT)
            plan = {"goal": "Prepare %s slot %s" % (value["routine"], timing["slot"]), "routine": TASK_ROUTINE,
                    "request_ref": "automation:" + timing["key"] + "#" + identity["evidence_ref"], "mode": "unattended", "environment": bound,
                    "window": timing["window"], "limits": dict(cost, max_steps=1, max_attempts_per_step=1, max_parallel=1,
                        lease_seconds=900, deadline_at=timing["deadline"]),
                    "steps": [{"key": "prepare", "title": "Collect scoped evidence and prepare private output", "kind": "local",
                               "capability": "app.prepare", "depends_on": [], "allow_partial": True, "cost": cost}]}
            run = tasks.create(timing["key"], plan)
            claim = tasks.start(run["id"], "prepare", 1, run["plan_hash"], bound)
            return {"status": "started", "run_id": run["id"], "timing": timing,
                    "claim": {"attempt_id": claim["attempt_id"], "token": claim["token"]},
                    "policy_revision": settings["revision"]}
    finally:
        tasks.close()


def owned(tasks, claim, host):
    obj(claim, {"attempt_id", "token"})
    item = tasks.inspect_claim(claim["attempt_id"], claim["token"])
    run = item["run"]
    if run["plan"]["routine"] != TASK_ROUTINE or run["plan"]["environment"]["host"] != host:
        raise store.StateError("Claim belongs to another routine or host.")
    return item


def charge(value, claim, host):
    obj(value, {"event_id"})
    tasks = open_tasks(True)
    try:
        owned(tasks, claim, host)
        return tasks.charge(claim["attempt_id"], claim["token"], text(value["event_id"], 150), dict(zero(), tool_calls=1))
    finally:
        tasks.close()


def observation(value):
    obj(value, {"id", "revision", "title", "summary", "web_link", "sensitivity", "ask", "due_at"},
        {"id", "revision", "title", "summary", "web_link", "sensitivity"})
    for key, maximum in [("id", 300), ("revision", 200), ("title", 200), ("summary", 800), ("web_link", 2000)]:
        text(value[key], maximum)
    url = urlsplit(value["web_link"])
    if url.scheme != "https" or not url.netloc or url.username or url.password:
        raise store.StateError("Source links must be absolute HTTPS without credentials.")
    if value["sensitivity"] not in {"normal", "private", "restricted", "unknown"}:
        raise store.StateError("Explicit sensitivity is required.")
    if value["sensitivity"] in {"restricted", "unknown"} and (value["summary"] != "Content withheld." or value.get("ask")):
        raise store.StateError("Restricted/unknown sensitivity permits metadata only, not excerpts or preparation.")
    if value.get("ask") is not None:
        text(value["ask"], 500)
    if value.get("due_at") is not None:
        store.validate_timestamp(value["due_at"])
    return dict(value)


def finish(value, claim, host):
    obj(value, {"sources", "summary", "candidates", "artifact"}, {"sources", "summary", "candidates"})
    text(value["summary"], 4000)
    sources = value["sources"]
    if not isinstance(sources, list) or len(sources) != 4 or {row.get("source") for row in sources if isinstance(row, dict)} != set(SOURCES):
        raise store.StateError("Report each of the four source scopes exactly once, including failures.")
    for row in sources:
        obj(row, {"source", "status", "kind", "error_class", "evidence_ref", "observations"},
            {"source", "status", "kind", "evidence_ref", "observations"})
        if row["status"] not in {"complete", "partial", "blocked", "failed"} or row["kind"] not in {"enumeration", "search"}:
            raise store.StateError("Unsupported coverage result.")
        if row["status"] == "complete" and row["kind"] != "enumeration":
            raise store.StateError("Search cannot establish complete source coverage.")
        if row.get("error_class") not in proactive.ERROR_CLASSES | {None}:
            raise store.StateError("Unsupported coverage error class.")
        if not text(row["evidence_ref"], 300).startswith("tool:"):
            raise store.StateError("Source results require tool provenance.")
        if not isinstance(row["observations"], list) or len(row["observations"]) > MAX_RECORDS:
            raise store.StateError("Source observation budget exceeded.")
        for item in row["observations"]:
            observation(item)
    candidates = value["candidates"]
    if not isinstance(candidates, list) or len(candidates) > 5:
        raise store.StateError("At most five sourced candidate asks may be staged.")
    tasks = open_tasks(True)
    try:
        with tasks.transaction():
            current = owned(tasks, claim, host)
            run = current["run"]
            fingerprint = digest(value)
            old = run["steps"][0].get("result")
            if old:
                if old["reference"] != "app-preparation:" + fingerprint:
                    raise store.StateError("Preparation replay differs from the recorded result.")
                return {"status": run["status"], "replayed": True, "display": "silent", "publication_id": old.get("publication_id")}
            settings = read_policy()
            if not settings["policy"] or not settings["policy"]["enabled"]:
                raise store.StateError("Local preparation is disabled; no output written.")
            if settings["revision"] != run["plan"]["environment"]["capabilities"]["policy.revision"]:
                raise store.StateError("Private policy/config changed during this run; no preparation written.")
            charged = tasks.charge(claim["attempt_id"], claim["token"], "finish:" + fingerprint, dict(zero(), tool_calls=1, model_calls=1))
            if not charged["execute"]:
                raise store.StateError("Finish claim already reserved; inspect existing task before retrying.")
            refs, source_ids, outputs, gaps = {}, set(), [], []
            for row in sources:
                window = run["plan"]["window"]
                if row["source"] == "calendar":
                    day = run["plan"]["request_ref"].split(":")[3]
                    start_day = datetime.fromisoformat(day).replace(tzinfo=ZoneInfo(settings["policy"]["timezone"]))
                    window = {"start": start_day.astimezone(timezone.utc).isoformat(),
                              "end": (start_day + timedelta(days=1)).astimezone(timezone.utc).isoformat()}
                collection = proactive.coverage_start(tasks.conn, {"family": "app-" + row["source"], "scope": {"owner": tasks.account, "surface": row["source"]},
                    "window": window, "run_id": run["id"], "capability": "workiq.read", "query_version": "restricted-app-v1",
                    "kind": row["kind"], "cadence_seconds": 3600})
                proactive.coverage_page(tasks.conn, collection["attempt_id"], {
                    "page": 1, "observations": row["observations"], "final": row["status"] == "complete", "continuation": None})
                state = {"status": row["status"]}
                if row["status"] == "complete":
                    state["checkpoint_at"] = run["plan"]["window"]["end"]
                else:
                    state["error_class"] = row.get("error_class", "access_denied" if row["status"] == "blocked" else "unavailable")
                    gaps.append("%s: %s (%s)" % (row["source"], row["status"], state["error_class"]))
                proactive.coverage_finish(tasks.conn, collection["attempt_id"], state)
                for item in row["observations"]:
                    key = row["source"] + ":" + item["id"]
                    if key in refs:
                        raise store.StateError("Duplicate source observation.")
                    ref = tasks.ledger.source("app-" + row["source"], "personal", item["id"], item["revision"],
                        {"title": item["title"], "summary": item["summary"], "ask": item.get("ask"), "due_at": item.get("due_at")},
                        item["web_link"], sensitivity=item["sensitivity"])
                    refs[key] = (ref, item)
                    source_ids.add(ref["source_id"])
                    if item.get("ask") and item.get("due_at") and item["sensitivity"] in {"normal", "private"}:
                        due_at = datetime.fromisoformat(store.validate_timestamp(item["due_at"]))
                        if clock() - timedelta(hours=24) <= due_at <= clock() + timedelta(hours=1):
                            proactive.queue_add(tasks.conn, {"id": ref["source_id"], "revision": item["revision"], "family": TASK_ROUTINE,
                                "scope": "personal", "title": item["title"], "ask": item["ask"], "due_at": item["due_at"], "source_ref": ref,
                                "web_link": item["web_link"]}, tier="sweep")
            for candidate in candidates:
                obj(candidate, {"source", "source_id", "claim_key", "title", "owner", "direction", "due", "next_step"})
                key = candidate["source"] + ":" + candidate["source_id"]
                if key not in refs or refs[key][1]["sensitivity"] not in {"normal", "private"}:
                    raise store.StateError("Candidate must reference an eligible observation from this run.")
                for name in ("claim_key", "title", "next_step"):
                    text(candidate[name], 500)
                text(candidate["owner"], 200, nullable=True)
                text(candidate["due"], 100, nullable=True)
                item = tasks.ledger.ingest({name: candidate[name] for name in ("title", "owner", "direction", "due", "next_step")}
                    | {"source_refs": [refs[key][0]]}, candidate["claim_key"])
                outputs.append(item["id"])
            if value.get("artifact") is not None:
                artifact = obj(value["artifact"], {"title", "markdown", "source_keys", "work_item_id"})
                text(artifact["title"], 200)
                text(artifact["markdown"], 4000)
                if (not isinstance(artifact["source_keys"], list) or not 1 <= len(artifact["source_keys"]) <= 5
                        or any(key not in refs or refs[key][1]["sensitivity"] not in {"normal", "private"} for key in artifact["source_keys"])):
                    raise store.StateError("Private artifact requires eligible current source references.")
                record = Productivity(tasks.ledger).put("artifact", run["id"], {
                    "artifact_kind": "status_update", "title": artifact["title"], "markdown": artifact["markdown"],
                    "audience": "User only", "purpose": "Private scheduled preparation or proposed resolution; not delivered",
                    "sensitivity": "private", "proposed_next_action": "Review in foreground conversation",
                    "open_questions": gaps, "source_refs": [refs[key][0] for key in artifact["source_keys"]],
                    "work_item_id": artifact["work_item_id"]})
                outputs.append(record["id"])
            queued = proactive.queue_drain(tasks.conn, owner=run["id"], lease_seconds=60, limit=1, family=TASK_ROUTINE)
            timely = [row for row in queued if datetime.fromisoformat(store.validate_timestamp(row["due_at"])) >= clock() - timedelta(hours=24)]
            routine = run["plan"]["request_ref"].split(":")[2]
            display = "notify" if routine != "sweep" or gaps or timely else "silent"
            lines = [value["summary"]] if display == "notify" else ["No new time-sensitive asks in the recorded complete scopes."]
            if gaps:
                lines += ["Source coverage gaps:"] + gaps
            for row in timely:
                lines.append("%s: %s (due %s) %s" % (row["title"], row["ask"], row["due_at"], row["web_link"]))
            lines += ["Recorded coverage evidence: " + "; ".join(row["source"] + "=" + row["evidence_ref"] for row in sources)]
            if queued and not timely:
                lines.append("An old queued alert was recorded without re-notifying; no stale wake was replayed.")
            lines.append("Local preparation only. No messages sent; no obligations confirmed or closed.")
            content = "\n".join(lines)
            if len(content) > MAX_OUTPUT:
                raise store.StateError("Output exceeds the fixed preparation limit.")
            publication = proactive.publication_record(tasks.conn, queued[0]["batch"] if queued else None,
                {"id": run["id"], "content": content, "status": "available", "receipt": {"kind": "local"},
                 **({"item_keys": [row["item_key"] for row in queued]} if queued else {})})
            if queued:
                proactive.queue_ack(tasks.conn, publication["publication_id"], batch_id=queued[0]["batch"])
            tasks.finish(claim["attempt_id"], claim["token"], "partial" if gaps else "succeeded",
                {"kind": "local_result", "reference": "app-preparation:" + fingerprint, "summary": value["summary"],
                 "work_ids": outputs, "publication_id": publication["publication_id"]})
            return {"status": "partial" if gaps else "ready", "display": display, "output": content if display == "notify" else None,
                    "publication_id": publication["publication_id"], "work_ids": outputs,
                    "host_delivered": False, "human_reviewed": False, "approval_granted": False}
    finally:
        tasks.close()


def dispatch(value):
    obj(value, {"operation", "input", "host", "claim"}, {"operation", "input", "host"})
    text(value["host"], 200)
    operation, data = value["operation"], value["input"]
    if operation == "context":
        obj(data, {"routine"})
        return context(data["routine"])
    if operation == "start":
        return start(data, value["host"])
    if operation in {"charge", "finish"}:
        return (charge if operation == "charge" else finish)(data, value.get("claim"), value["host"])
    raise store.StateError("Operation is not exposed by restricted app preparation.")


def load():
    raw = sys.stdin.read(MAX_BYTES + 1)
    if len(raw.encode("utf-8")) > MAX_BYTES:
        raise store.StateError("Restricted preparation input exceeds 96 KiB.")
    return store.parse_json(raw)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("policy-show")
    configure_parser = sub.add_parser("policy-set")
    configure_parser.add_argument("--expected-revision", required=True)
    sub.add_parser("tool")
    args = parser.parse_args()
    try:
        result = read_policy() if args.command == "policy-show" else configure(load(), args.expected_revision) if args.command == "policy-set" else dispatch(load())
        print(store.canonical_json(result))
        return 0
    except (store.StateError, OSError, sqlite3.Error, ValueError, KeyError, TypeError) as exc:
        print(store.canonical_json({"error": str(exc), "code": store.error_code(exc) or "preparation_blocked"}), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
