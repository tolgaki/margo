"""Decision desk projection and local request dispatch over existing state owners."""

import re
from datetime import timedelta
from pathlib import Path

import margo_store
import proactive_state
import margo_profile
from task_runs import TaskStore, clock, zero
from work_ledger import StateError, canonical_json, digest, timestamp


INTENTS = ("prepare", "recommend", "review")
PREFIX = "host-interaction:canvas-decision:"
LIMIT = 50


def time_preferences(path=None):
    """Read only the time field, not the rest of the private profile."""
    path = path or Path(__file__).resolve().parent.parent / "preferences.md"
    try:
        with Path(path).open(encoding="utf-8") as stream:
            content = stream.read(262145)
    except FileNotFoundError:
        return {"status": "missing", "value": None}
    except (OSError, UnicodeError) as exc:
        raise StateError("Time preferences are unreadable; no defaults applied.") from exc
    if len(content) > 262144:
        raise StateError("Preferences exceed the 256 KiB read limit.")
    match = re.search(r"^\s*-\s*\*\*Time zone & working hours:\*\*\s*([^\n]*)",
                      content, re.MULTILINE | re.IGNORECASE)
    value = match.group(1).strip() if match else None
    if not value or re.search(r"\{[^}]*\}", value):
        return {"status": "missing", "value": None}
    return {"status": "recorded", "value": value, "revision": digest(value)}


def subject(item, intent):
    return {"id": item["id"], "revision": item["revision"],
            "action_hash": item.get("action_hash"), "intent": intent}


def request_key(binding):
    return PREFIX + digest(binding)


def request_status(run):
    steps = {step["key"]: step for step in run["steps"]}
    dispatch, prepare = steps["dispatch"], steps["prepare"]
    if run["status"] in {"paused", "cancelled", "blocked", "interrupted", "outcome_unknown"}:
        phase = run["status"]
    elif prepare["state"] in {"failed", "partial"}:
        phase = prepare["state"]
    elif run["status"] in {"failed", "partial"}:
        phase = run["status"]
    elif prepare["state"] == "succeeded":
        phase = "ready" if not prepare.get("source_evidence_changed") else "blocked"
    elif prepare["state"] == "running":
        phase = "working"
    elif dispatch["state"] == "succeeded":
        phase = "accepted"
    elif dispatch["state"] == "failed":
        phase = "failed"
    else:
        phase = "dispatching"
    return {"run_id": run["id"], "phase": phase, "updated_at": run["updated_at"],
            "deadline_at": run["plan"]["limits"]["deadline_at"],
            "lease_expires": prepare.get("lease_expires") or dispatch.get("lease_expires"),
            "result": prepare.get("result"),
            "blocked_reasons": run["blocked_reasons"] + prepare["blocked_reasons"],
            "approved": False}


def snapshot(ledger, preferences_path=None):
    result = ledger.list("all", limit=LIMIT)
    result["read_at"] = margo_store.utc_now()
    result["time_preferences"] = time_preferences(preferences_path)
    result["profile"] = margo_profile.show(ledger.account)
    try:
        proactive_state.validate_schema(ledger.conn)
        result["coverage"] = {"status": "available", "sources": proactive_state.coverage_status(ledger.conn)}
    except StateError as exc:
        result["coverage"] = {"status": "unavailable", "reason": str(exc), "sources": []}
    result["requests"] = {}
    try:
        tasks = TaskStore.from_connection(ledger.conn, ledger.account)
    except StateError as exc:
        result["request_capability"] = {"available": False, "reason": str(exc)}
    else:
        result["request_capability"] = {"available": True}
        for item in result["items"] + result["actions"] + result["records"]:
            for intent in INTENTS:
                run = tasks.find(request_key(subject(item, intent)))
                if run:
                    result["requests"].setdefault(item["id"], {})[intent] = request_status(run)
    return result


def validate_request(ledger, value):
    if (not isinstance(value, dict)
            or set(value) - {"id", "expected_revision", "expected_hash", "intent", "host"}
            or not {"id", "expected_revision", "intent", "host"} <= set(value)):
        raise StateError("Decision request requires exact identity, intent and host.")
    if value["intent"] not in INTENTS:
        raise StateError("Unsupported decision intent.")
    if (not isinstance(value["host"], str) or not re.fullmatch(r"[A-Za-z0-9._:-]{1,200}", value["host"])):
        raise StateError("Invalid request host.")
    item = ledger.show(value["id"])
    if item["type"] not in {"item", "action", "record"}:
        raise StateError("Only work, proposals and typed records support decision requests.")
    if type(value["expected_revision"]) is not int or value["expected_revision"] != item["revision"]:
        raise StateError("Revision conflict; reload this item before requesting help.")
    if value.get("expected_hash") != item.get("action_hash"):
        raise StateError("Action hash conflict; reload this item before requesting help.")
    if item.get("kind") == "feedback":
        raise StateError("Feedback records are historical; review them in the conversation.")
    if value["intent"] == "prepare":
        if item["state"] in {"executing", "partial", "outcome_unknown", "succeeded", "dismissed",
                             "resolved", "rejected", "cancelled", "achieved", "carried_forward",
                             "revoked", "superseded"} or item.get("stale"):
            raise StateError("Preparation is blocked for historical/unresolved or changed-source work; request review.")
        if (item.get("kind") == "meeting" and item["state"] in {"scheduled", "agenda_accumulating", "prepped"}
                and timestamp(item["data"]["scheduled_end"]) <= clock()):
            raise StateError("This scheduled meeting has ended; request review, not upcoming preparation.")
    return item


def request(ledger, value):
    tasks = TaskStore.from_connection(ledger.conn, ledger.account)
    with ledger.transaction():
        item = validate_request(ledger, value)
        binding = subject(item, value["intent"])
        key = request_key(binding)
        previous = tasks.find(key)
        if previous:
            return {"account": ledger.account, "request": request_status(previous), "subject": binding, "replayed": True}
        now = clock()
        environment = {"account": ledger.account, "host": value["host"],
                       "capabilities": {"canvas.dispatch": "1", "local.prepare": "1"},
                       "observed_at": now.isoformat()}
        dispatch_cost = dict(zero(), tool_calls=1, output_chars=500)
        prepare_cost = dict(zero(), tool_calls=8, model_calls=1, items=20, output_chars=8000)
        plan = {
            "goal": "Local %s for %s revision %s" % (value["intent"], item["id"], item["revision"]),
            "routine": "action-desk", "request_ref": PREFIX + canonical_json(binding),
            "mode": "foreground", "environment": environment,
            "window": {"start": (now - timedelta(seconds=1)).isoformat(), "end": now.isoformat()},
            "limits": dict(zero(), tool_calls=9, model_calls=1, items=20, output_chars=8500,
                           max_steps=2, max_attempts_per_step=1, max_parallel=1,
                           deadline_at=(now + timedelta(minutes=30)).isoformat(), lease_seconds=900),
            "steps": [
                {"key": "dispatch", "title": "Request conversation assistance", "kind": "local",
                 "capability": "canvas.dispatch", "depends_on": [], "allow_partial": False, "cost": dispatch_cost},
                {"key": "prepare", "title": "Prepare local recommendation for human review", "kind": "local",
                 "capability": "local.prepare", "depends_on": ["dispatch"],
                 "allow_partial": False, "cost": prepare_cost},
            ],
        }
        run = tasks.create(key, plan)
        claim = tasks.start(run["id"], "dispatch", 1, run["plan_hash"], environment)
        tasks.charge(claim["attempt_id"], claim["token"], "dispatch", dict(zero(), tool_calls=1))
        return {"account": ledger.account, "request": request_status(tasks.show(run["id"])), "subject": binding,
                "replayed": False, "claim": {"attempt_id": claim["attempt_id"], "token": claim["token"]}}


def dispatched(ledger, value):
    if (not isinstance(value, dict) or set(value) != {"attempt_id", "token", "accepted", "reference"}
            or type(value["accepted"]) is not bool):
        raise StateError("Invalid dispatch receipt.")
    tasks = TaskStore.from_connection(ledger.conn, ledger.account)
    run = tasks.finish(value["attempt_id"], value["token"],
                       "succeeded" if value["accepted"] else "outcome_unknown",
                       {"kind": "local_result", "reference": value["reference"],
                        "summary": "Conversation accepted request; preparation has not started."
                        if value["accepted"] else "Dispatch outcome unknown; inspect conversation, never resend automatically."})
    return {"request": request_status(run), "approved": False}


def start(ledger, run_id, host):
    """Claim local preparation only after atomically rechecking the exact subject."""
    tasks = TaskStore.from_connection(ledger.conn, ledger.account)
    with ledger.transaction():
        run = tasks.show(run_id)
        ref = run["plan"]["request_ref"]
        if not ref.startswith(PREFIX):
            raise StateError("Not a decision workspace request.")
        if host != run["plan"]["environment"]["host"]:
            raise StateError("Request host changed; continue in the original conversation or review a new plan.")
        binding = margo_store.parse_json(ref[len(PREFIX):])
        validate_request(ledger, {
            "id": binding["id"], "expected_revision": binding["revision"],
            "expected_hash": binding["action_hash"], "intent": binding["intent"],
            "host": host,
        })
        if any(step["kind"] != "local" for step in run["plan"]["steps"]):
            raise StateError("Decision requests cannot execute external actions.")
        environment = {"account": ledger.account, "host": host,
                       "capabilities": {"canvas.dispatch": "1", "local.prepare": "1"},
                       "observed_at": margo_store.utc_now()}
        step = next(step for step in run["steps"] if step["key"] == "prepare")
        return tasks.start(run_id, "prepare", step["revision"], run["plan_hash"], environment)
