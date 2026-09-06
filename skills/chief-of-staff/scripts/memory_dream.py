"""Opted-in session checkpoints and bounded manual reflection in the memory owner.

The host supplies observations and reasoning. This module never discovers host history,
calls a model, or treats a citation as proof that an interpretation is true.
"""

from datetime import date, datetime, time, timedelta, timezone
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

from margo_store import StateError, canonical_json, parse_json, utc_now, validate_timestamp
from memory_governance import digest
from task_runs import TaskStore, zero
import proactive_state


MAX_INPUT = 100000
MAX_EVENTS = 100
MAX_SCAN = 500
MAX_CLAIMS = 20
INTERPRETATIONS = {"hypothesis", "decision", "rationale", "alternative", "direction", "question", "discussion"}


def bounded(value, fields):
    if not isinstance(value, dict) or set(value) != set(fields.split()):
        raise StateError("Dream input requires exactly: " + fields)
    if len(canonical_json(value)) > MAX_INPUT:
        raise StateError("Dream input exceeds 100000 characters")
    return value


def text(value, field, maximum=1000):
    if not isinstance(value, str) or not value.strip() or len(value) > maximum:
        raise StateError(field + " must be a bounded nonempty string")
    return value


def scope_for(account, host, workspace):
    for name, value in (("account", account), ("host", host), ("workspace", workspace)):
        text(value, name, 200)
    return "dream:" + digest([account, host, workspace])


def capability(account, value):
    bounded(value, "account host workspace adapter content_access session_list authorized")
    if value["account"] != account:
        raise StateError("checkpoint account differs from the configured account")
    scope_for(account, value["host"], value["workspace"])
    if value["adapter"] != "margo-session-checkpoint-v1":
        raise StateError("unsupported history adapter; only explicit Margo session checkpoints are supported")
    if value["content_access"] != "current-session" or value["authorized"] is not True:
        raise StateError("authorized current-session content access is required; session listing is not content access")
    if type(value["session_list"]) is not bool:
        raise StateError("session_list must be boolean")
    return value


def source_keys(ref):
    identity = parse_json(ref["ref"])
    return ("dream-source:" + canonical_json(identity[:5]),
            "dream-locator:" + canonical_json(identity[:3] + [identity[5]]))


def validate_source(memory, ref):
    """Prevent reimport under an alternate memory key or source revision."""
    try:
        identity = parse_json(ref["ref"])
    except (ValueError, TypeError):
        raise StateError("invalid session checkpoint source identity")
    if (not isinstance(identity, list) or len(identity) != 6
            or identity[0] != memory.account
            or any(not isinstance(part, str) or not part.strip() for part in identity)
            or canonical_json(identity) != ref["ref"]):
        raise StateError("session source requires canonical account, host, workspace, session, event and locator")
    text(ref.get("revision"), "source revision", 200)
    for key in source_keys(ref):
        _, key_hash = memory._identity("user", key)
        if memory.conn.execute(
                "SELECT 1 FROM memory_tombstones WHERE account=? AND key_hash=?",
                (memory.account, key_hash)).fetchone():
            raise StateError("forgotten session evidence cannot be reimported under another identity")


def _data(title, body, scope, refs, metadata, entities=None, uses=None, sensitivity="private",
          authority="source_observed", kind="episode"):
    return {"domain": "user", "kind": kind, "title": title, "text": body,
            "scope": scope, "authority": authority, "source_refs": refs,
            "sensitivity": sensitivity, "allowed_uses": ["reasoning"] if uses is None else uses,
            "entities": entities or [], "metadata": metadata}


def checkpoint(memory, value):
    bounded(value, "capability session event revision expected_revision timestamp speaker locator text "
                   "origin sensitivity allowed_uses entities work_refs")
    cap = capability(memory.account, value["capability"])
    if value["origin"] != "margo-session":
        raise StateError("Dream outputs, Dream runs and imported history are not session evidence")
    for field in ("session", "event", "revision", "speaker", "locator"):
        text(value[field], field, 1000 if field == "locator" else 200)
    if value["speaker"] not in {"user", "assistant", "tool"}:
        raise StateError("speaker must be user, assistant or tool")
    if value["expected_revision"] is not None and (
            type(value["expected_revision"]) is not int or value["expected_revision"] < 1):
        raise StateError("expected_revision must be null or a positive memory revision")
    observed = validate_timestamp(value["timestamp"])
    if datetime.fromisoformat(observed) > datetime.now(timezone.utc):
        raise StateError("event timestamp is in the future")
    text(value["text"], "checkpoint text", 6000)
    if not isinstance(value["work_refs"], list) or len(value["work_refs"]) > 20:
        raise StateError("work_refs must be a bounded list of exact canonical IDs")
    if value["work_refs"]:
        tasks = TaskStore.from_connection(memory.conn, memory.account)
        for identity in value["work_refs"]:
            tasks.ledger.show(text(identity, "canonical work ID", 200))
    identity = canonical_json([memory.account, cap["host"], cap["workspace"], value["session"], value["event"]])
    source = canonical_json(parse_json(identity) + [value["locator"]])
    key = "dream-event:" + digest(identity)
    with memory.transaction():
        previous = memory.conn.execute("SELECT id FROM memory_records WHERE id=? AND account=?",
                                       (memory.record_id("user", key), memory.account)).fetchone()
        old = memory.show(previous["id"]) if previous else None
        meta = {"dream": "checkpoint", "host": cap["host"], "workspace": cap["workspace"],
                "session": value["session"], "event": value["event"], "speaker": value["speaker"],
                "locator": value["locator"], "timestamp": observed, "origin": value["origin"],
                "adapter": cap["adapter"], "work_refs": value["work_refs"]}
        data = _data("Session checkpoint", value["text"], scope_for(memory.account, cap["host"], cap["workspace"]),
                     [{"kind": "session_checkpoint", "ref": source, "revision": value["revision"]}],
                     meta, value["entities"], value["allowed_uses"], value["sensitivity"])
        if old:
            if old["status"] != "active":
                raise StateError("checkpoint is not active; inspect/review it rather than reactivating by capture")
            prior = old["source_refs"][0]["revision"]
            # Host revisions are opaque, but reusing one for changed substance is invalid.
            if prior == value["revision"]:
                clean = dict(data, metadata=dict(meta, capture_policy_revision=old["metadata"]["capture_policy_revision"]))
                compared = {key: old[key] for key in clean}
                if clean != compared:
                    raise StateError("same source revision has changed checkpoint content")
            elif value["expected_revision"] != old["revision"]:
                raise StateError("changed checkpoint requires its current expected_revision")
            elif any(ref.get("revision") == value["revision"]
                     for revision in memory.conn.execute(
                         "SELECT data FROM memory_revisions WHERE memory_id=?", (old["id"],))
                     for ref in parse_json(revision["data"]).get("source_refs", [])
                     if ref["kind"] == "session_checkpoint"):
                raise StateError("an older host revision cannot be replayed as a new checkpoint")
        return memory.capture(key, data, revision=value["expected_revision"])


def _window(value):
    bounded(value, "host workspace day timezone cutoff lookback_days after")
    text(value["host"], "host", 200)
    text(value["workspace"], "workspace", 200)
    if type(value["lookback_days"]) is not int or not 0 <= value["lookback_days"] <= 7:
        raise StateError("lookback_days must be between 0 and 7")
    if value["after"] is not None:
        text(value["after"], "scan cursor", 100)
    try:
        zone = ZoneInfo(value["timezone"])
        day = date.fromisoformat(value["day"])
        cutoff = time.fromisoformat(value["cutoff"])
        if cutoff.tzinfo or cutoff.second or cutoff.microsecond:
            raise ValueError("cutoff must be local HH:MM")
        start = datetime.combine(day, cutoff, zone)
        end = datetime.combine(day + timedelta(days=1), cutoff, zone)
        # Reject ambiguous/nonexistent local cutoffs instead of guessing across DST.
        for point in (start, end):
            if (point.replace(fold=0).utcoffset() != point.replace(fold=1).utcoffset()
                    or point.astimezone(timezone.utc).astimezone(zone) != point):
                raise ValueError("ambiguous/nonexistent cutoff; choose another local time")
        if end > datetime.now(timezone.utc):
            raise ValueError("the daily cutoff has not completed")
    except (ValueError, TypeError, ZoneInfoNotFoundError) as exc:
        raise StateError("invalid Dream daily window: " + str(exc))
    return start.astimezone(timezone.utc), end.astimezone(timezone.utc)


def _current(memory, row):
    return (row["status"] == "active" and row.get("authority") == "source_observed"
            and row.get("sensitivity") != "sensitive"
            and "reasoning" in row.get("allowed_uses", [])
            and row.get("valid_from", utc_now()) <= utc_now()
            and row.get("valid_to", "9999") > utc_now()
            and row.get("review_after", "9999") > utc_now()
            and memory._work_sources_current(row))


def plan(memory, value):
    start, end = _window(value)
    scope = scope_for(memory.account, value["host"], value["workspace"])
    with memory.transaction():
        rules = memory.policy()
        if scope not in rules["data"]["capture"]["scopes"] or not rules["data"]["capture"]["enabled"]:
            raise StateError("Dream is not opted in for this exact account/host/workspace")
        capture = rules["data"]["capture"]
        if ("user" not in capture["domains"] or "episode" not in capture["kinds"]
                or not {"session_checkpoint", "memory_record"} <= set(capture["source_kinds"])):
            raise StateError("Dream requires user episodes and session_checkpoint/memory_record capture")
        # Bound the scan before decoding metadata; never silently call a truncated scan complete.
        scanned = memory.conn.execute(
            "SELECT id FROM memory_records WHERE account=? AND domain='user' AND kind='episode' "
            "AND status<>'forgotten' AND id>? ORDER BY id LIMIT ?",
            (memory.account, value["after"] or "", MAX_SCAN + 1)).fetchall()
        events, excluded = [], 0
        for item in scanned[:MAX_SCAN]:
            row = memory.show(item["id"])
            if row.get("scope") != scope or row.get("metadata", {}).get("dream") != "checkpoint":
                continue
            source = next((ref for ref in row["source_refs"] if ref["kind"] == "session_checkpoint"), None)
            if source is None or row["id"] != memory.record_id(
                    "user", "dream-event:" + digest(canonical_json(parse_json(source["ref"])[:5]))):
                excluded += 1
                continue
            observed = datetime.fromisoformat(row["metadata"]["timestamp"])
            if not start - timedelta(days=value["lookback_days"]) <= observed < end:
                continue
            if not _current(memory, row):
                excluded += 1
                continue
            events.append(row)
        over = len(events) > MAX_EVENTS
        events = events[:MAX_EVENTS]
        # Packet text has its own bound, independent of event count.
        packet, size = [], 0
        for row in events:
            cost = len(canonical_json(row))
            if size + cost > 14000:
                over = True
                break
            size += cost
            packet.append(row)
        cursor = packet[-1]["id"] if over and packet else (
            scanned[MAX_SCAN - 1]["id"] if len(scanned) > MAX_SCAN else None)
        packet.sort(key=lambda row: (row["metadata"]["timestamp"], row["id"]))
        coverage = {"status": "partial", "source": "opted-in-checkpoints-only",
                    "history_access": "unsupported", "window": {"start": start.isoformat(), "end": end.isoformat()},
                    "timezone": value["timezone"], "cutoff": value["cutoff"],
                    "lookback_days": value["lookback_days"], "excluded": excluded,
                    "scan_truncated": len(scanned) > MAX_SCAN, "intake_truncated": over,
                    "next_cursor": cursor,
                    "pagination": "not-enumerated", "late_arrivals": "bounded-lookback; rerun with a new key",
                    "observed_events": len(packet)}
        refs = [{"kind": "memory_record", "ref": row["id"], "revision": str(row["revision"])} for row in packet]
        result = {"account": memory.account, "scope": scope, "request": value, "policy_revision": rules["revision"],
                  "coverage": coverage, "source_refs": refs}
        return dict(result, snapshot_hash=digest(result), events=packet)


def start(memory, key, value):
    bounded(value, "request snapshot_hash request_ref")
    text(key, "Dream run key", 200)
    tasks = TaskStore.from_connection(memory.conn, memory.account)
    with memory.transaction():
        snapshot = plan(memory, value["request"])
        if snapshot["snapshot_hash"] != value["snapshot_hash"]:
            raise StateError("Dream intake or policy changed; inspect a fresh plan")
        if not snapshot["source_refs"]:
            raise StateError("no eligible checkpoints; no reflection was completed")
        snapshot_key = "dream-snapshot:" + key
        snapshot_id = memory.record_id("user", snapshot_key)
        previous = memory.conn.execute("SELECT id FROM memory_records WHERE id=? AND account=?",
                                       (snapshot_id, memory.account)).fetchone()
        if previous:
            saved = memory.show(snapshot_id)
            if saved.get("metadata", {}).get("snapshot_hash") != snapshot["snapshot_hash"]:
                raise StateError("run key already exists or was forgotten; inspect it, never overwrite")
            run = tasks.show(saved["metadata"]["task_id"])
        else:
            now = datetime.now(timezone.utc)
            environment = {"account": memory.account, "host": value["request"]["host"],
                           "capabilities": {"local.dream": "checkpoint-v1"}, "observed_at": now.isoformat()}
            cost = dict(zero(), model_calls=1, items=len(snapshot["source_refs"]), output_chars=24000)
            task_plan = {"goal": "Reflect on opted-in checkpoints", "routine": "dream",
                         "request_ref": value["request_ref"], "mode": "foreground",
                         "environment": environment, "window": snapshot["coverage"]["window"],
                         "limits": dict(cost, max_steps=1, max_attempts_per_step=2, max_parallel=1,
                                        deadline_at=(now + timedelta(hours=1)).isoformat(), lease_seconds=900),
                         "steps": [{"key": "reflect", "title": "Prepare sourced reflection", "kind": "local",
                                    "capability": "local.dream", "depends_on": [], "allow_partial": False, "cost": cost}]}
            run = tasks.create("dream:" + digest([memory.account, key]), task_plan)
            meta = {name: snapshot[name] for name in ("snapshot_hash", "request", "coverage", "policy_revision")}
            meta.update(dream="snapshot", task_id=run["id"])
            saved = memory.capture(snapshot_key, _data("Dream collection snapshot", "Bounded checkpoint selection.",
                                                       snapshot["scope"], snapshot["source_refs"], meta))
        if run["state"] == "completed":
            return inspect(memory, saved["id"])
        step = run["steps"][0]
        binding = dict(run["plan"]["environment"], observed_at=utc_now())
        claim = tasks.start(run["id"], "reflect", step["revision"], run["plan_hash"], binding)
        grant = tasks.charge(claim["attempt_id"], claim["token"], "host-reflection",
                             dict(zero(), model_calls=1, items=len(snapshot["source_refs"])))
        return {"snapshot_id": saved["id"], "claim": claim, "model_grant": grant,
                "events": snapshot["events"], "coverage": snapshot["coverage"],
                "instructions": "Reason outside transactions; submit only cited candidate interpretations."}


def finish(memory, value):
    bounded(value, "snapshot_id attempt_id token claims")
    claims = value["claims"]
    if not isinstance(claims, list) or len(claims) > MAX_CLAIMS or len(canonical_json(claims)) > 8000:
        raise StateError("reflection accepts at most 20 claims and 8000 characters")
    tasks = TaskStore.from_connection(memory.conn, memory.account)
    with memory.transaction():
        snapshot = memory.show(value["snapshot_id"])
        meta = snapshot.get("metadata", {})
        if meta.get("dream") != "snapshot" or not _current(memory, snapshot):
            raise StateError("Dream snapshot is unavailable, stale, suppressed or forgotten")
        if memory.policy()["revision"] != meta["policy_revision"]:
            raise StateError("capture policy changed during reflection; prepare a new run")
        run = tasks.show(meta["task_id"])
        attempt = run["steps"][0].get("latest_attempt")
        if attempt != value["attempt_id"]:
            raise StateError("claim belongs to another Dream run")
        events = [memory.show(ref["ref"]) for ref in snapshot["source_refs"]]
        by_id = {row["id"]: row for row in events}
        prepared = []
        for claim in claims:
            bounded(claim, "key type text citations")
            text(claim["key"], "claim key", 100)
            text(claim["text"], "claim text", 2000)
            if claim["type"] not in INTERPRETATIONS:
                raise StateError("unsupported interpretation; preferences/lessons require separate reviewed workflows")
            if not isinstance(claim["citations"], list) or not 1 <= len(claim["citations"]) <= 10:
                raise StateError("every claim requires 1..10 source-locatable citations")
            sources = []
            for citation in claim["citations"]:
                bounded(citation, "id revision quote")
                row = by_id.get(citation["id"])
                if row is None or type(citation["revision"]) is not int or citation["revision"] != row["revision"]:
                    raise StateError("citation is outside the exact checkpoint snapshot")
                text(citation["quote"], "citation quote", 2000)
                if citation["quote"] not in row["text"]:
                    raise StateError("citation quote is not present; this check does not prove entailment")
                sources.append(row)
            prepared.append((claim, sources))
        if len({claim["key"] for claim, _ in prepared}) != len(prepared):
            raise StateError("duplicate claim keys")
        payload_hash = digest(claims)
        # A completed replay verifies payload and token but never recaptures erased outputs.
        receipt = {"kind": "local_result", "reference": snapshot["id"] + ":" + payload_hash}
        if run["state"] == "completed":
            tasks.finish(value["attempt_id"], value["token"], "succeeded", receipt)
            return inspect(memory, snapshot["id"])
        tasks.charge(value["attempt_id"], value["token"], "persist:" + payload_hash,
                     dict(zero(), output_chars=24000))
        outputs = []
        # Episodes are exact observations, not model-written summaries or confirmations.
        for event in events:
            refs = [{"kind": "memory_record", "ref": event["id"], "revision": str(event["revision"])}]
            title = "Dream sourced episode: %s at %s" % (event["metadata"]["speaker"], event["metadata"]["timestamp"])
            data = _data(title, event["text"], event["scope"], refs,
                         {"dream": "episode", "chronology": event["metadata"],
                          "environment_requirements": {"account": memory.account,
                                                       "host": meta["request"]["host"],
                                                       "workspace": meta["request"]["workspace"]},
                          "work_refs": event["metadata"]["work_refs"]},
                         event["entities"], event["allowed_uses"], event["sensitivity"])
            outputs.append(memory.capture("dream-episode:" + event["id"] + ":" + str(event["revision"]), data)["id"])
        for claim, sources in prepared:
            refs = [{"kind": "memory_record", "ref": row["id"], "revision": str(row["revision"])}
                    for row in {row["id"]: row for row in sources}.values()]
            refs.append({"kind": "memory_record", "ref": snapshot["id"], "revision": str(snapshot["revision"])})
            uses = sorted(set.intersection(*(set(row["allowed_uses"]) for row in sources)))
            if not uses:
                raise StateError("cited sources have no common allowed use")
            sensitivity = "private" if any(row["sensitivity"] == "private" for row in sources) else "public"
            data = _data("Dream candidate: " + claim["type"], claim["text"], snapshot["scope"], refs,
                         {"dream": "interpretation", "snapshot_id": snapshot["id"], "type": claim["type"],
                          "citations": claim["citations"],
                          "environment_requirements": {"account": memory.account,
                                                       "host": meta["request"]["host"],
                                                       "workspace": meta["request"]["workspace"]},
                          "work_refs": sorted({ref for row in sources for ref in row["metadata"]["work_refs"]})},
                         sorted({entity for row in sources for entity in row["entities"]}), uses, sensitivity,
                         authority="inferred", kind="decision" if claim["type"] == "decision" else "episode")
            outputs.append(memory.capture("dream-claim:" + snapshot["id"] + ":" + claim["key"], data)["id"])
        # Publication content is a pointer only. Erasable prose stays exclusively in memory.
        proactive_state.publication_record(memory.conn, None,
                                          {"id": "dream:" + snapshot["id"], "content": snapshot["id"], "status": "prepared"})
        tasks.finish(value["attempt_id"], value["token"], "succeeded", receipt)
        return dict(inspect(memory, snapshot["id"]), output_ids=outputs)


def inspect(memory, snapshot_id):
    snapshot = memory.show(snapshot_id)
    meta = snapshot.get("metadata", {})
    if meta.get("dream") != "snapshot":
        return {"snapshot_id": snapshot_id, "status": snapshot["status"], "available": False}
    tasks = TaskStore.from_connection(memory.conn, memory.account)
    run = tasks.show(meta["task_id"])
    rows = memory.conn.execute(
        "SELECT id FROM memory_records WHERE account=? AND status<>'forgotten' ORDER BY id LIMIT ?",
        (memory.account, MAX_SCAN + 1)).fetchall()
    outputs = [memory.show(row["id"]) for row in rows[:MAX_SCAN]]
    event_ids = {ref["ref"] for ref in snapshot["source_refs"]}
    outputs = [row for row in outputs if row.get("metadata", {}).get("snapshot_id") == snapshot_id
               or (row.get("metadata", {}).get("dream") == "episode"
                   and any(ref["ref"] in event_ids for ref in row["source_refs"]))]
    current = _current(memory, snapshot) and memory.policy()["revision"] == meta["policy_revision"]
    publication = (proactive_state.publication_show(memory.conn, "dream:" + snapshot_id)
                   if run["state"] == "completed" else None)
    return {"snapshot_id": snapshot_id, "task_id": run["id"], "collection": meta["coverage"],
            "reflection": run["state"], "sources_current": current,
            "availability": publication["status"] if current and publication else "unavailable",
            "human_review": "output-reviewed" if publication and publication["status"] == "reviewed" else "not-established",
            "outputs": outputs, "inspection_truncated": len(rows) > MAX_SCAN,
            "output_receipt": "dream:" + snapshot_id, "outbound_actions": False}


def status(memory, host, workspace):
    scope = scope_for(memory.account, host, workspace)
    rules = memory.policy()
    capture = rules["data"]["capture"]
    return {"account": memory.account, "host": host, "workspace": workspace, "scope": scope,
            "capture_policy": rules, "opted_in": capture["enabled"] and scope in capture["scopes"]
            and "user" in capture["domains"] and "episode" in capture["kinds"]
            and {"session_checkpoint", "memory_record"} <= set(capture["source_kinds"]),
            "adapter": "margo-session-checkpoint-v1", "history_access": "unsupported",
            "schedule": "not-installed", "review": "memory_state.py inspect/revise/consolidate"}
