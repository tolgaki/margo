"""Reviewed capture policy, retention, deletion recovery and private recipe export."""

import hashlib
import os
import re
import tempfile
from datetime import datetime, timedelta, timezone
from pathlib import Path

import margo_store
from memory_store import DOMAINS, KINDS, SOURCE_KINDS
from margo_store import StateError, canonical_json, parse_json, utc_now, validate_timestamp


def digest(value):
    return hashlib.sha256(canonical_json(value).encode("utf-8")).hexdigest()


def policy(memory):
    row = memory.conn.execute("SELECT * FROM memory_policy WHERE account=?", (memory.account,)).fetchone()
    default = {
        "capture": {"enabled": False, "domains": [], "kinds": [], "scopes": [], "source_kinds": []},
        "allow_sensitive": False, "usage_enabled": False, "usage_retention_days": 30,
        "retention_days": {}, "review_days": {},
    }
    return {"account": memory.account, "revision": row["revision"] if row else 0,
            "configured": row is not None, "data": parse_json(row["data"]) if row else default}


def normalise_policy(data):
    if not isinstance(data, dict) or set(data) != {
            "capture", "allow_sensitive", "usage_enabled", "usage_retention_days", "retention_days", "review_days"}:
        raise StateError("policy requires capture, allow_sensitive, usage_enabled, usage_retention_days, retention_days and review_days")
    capture = data["capture"]
    if not isinstance(capture, dict) or set(capture) != {"enabled", "domains", "kinds", "scopes", "source_kinds"}:
        raise StateError("capture requires enabled, domains, kinds, scopes and source_kinds")
    for value in (capture["enabled"], data["allow_sensitive"], data["usage_enabled"]):
        if type(value) is not bool:
            raise StateError("policy switches must be booleans")
    normal = dict(data, capture=dict(capture))
    for field, allowed in (("domains", DOMAINS), ("kinds", KINDS), ("scopes", None), ("source_kinds", SOURCE_KINDS)):
        values = capture[field]
        if (not isinstance(values, list) or len(values) > 100 or
                any(not isinstance(value, str) or not value.strip() or (allowed and value not in allowed) for value in values)):
            raise StateError("invalid capture " + field)
        if capture["enabled"] and not values:
            raise StateError("enabled capture needs an explicit nonempty " + field)
        normal["capture"][field] = sorted(set(values))
    for field in ("retention_days", "review_days"):
        values = data[field]
        if not isinstance(values, dict) or any(
                key not in KINDS or type(days) is not int or not 1 <= days <= 3650 for key, days in values.items()):
            raise StateError(field + " must map memory kinds to 1..3650 days")
        normal[field] = dict(values)
    if type(data["usage_retention_days"]) is not int or not 1 <= data["usage_retention_days"] <= 3650:
        raise StateError("usage_retention_days must be between 1 and 3650")
    return normal


def policy_preview(memory, data):
    current = policy(memory)
    normal = normalise_policy(data)
    roots, affected = [], set()
    stamp = datetime.now(timezone.utc)
    for record in memory.list():
        days = normal["retention_days"].get(record["kind"])
        if (record["status"] != "forgotten" and days is not None
                and datetime.fromisoformat(record["updated_at"]) + timedelta(days=days) <= stamp):
            roots.append({"id": record["id"], "revision": record["revision"]})
            affected.update(memory._derived_descendants(record["id"]))
    impact = {"due_roots": sorted(roots, key=lambda row: row["id"])[:50],
              "due_root_count": len(roots), "affected_record_count": len(affected),
              "truncated": len(roots) > 50}
    value = {"account": memory.account, "expected_revision": current["revision"], "data": normal,
             "current_retention_impact": impact}
    return dict(value, subject_id="memory-policy:" + digest(value), revision=1,
                warning="Retention authorizes local erasure of matching memories and their derived records; it does not delete sources.")


def set_policy(memory, data, evidence):
    with memory.transaction():
        preview = policy_preview(memory, data)
        memory._human(evidence, preview["subject_id"], 1, "configure")
        memory.conn.execute(
            "INSERT INTO memory_policy VALUES(?,?,?,?,?) ON CONFLICT(account) DO UPDATE SET "
            "revision=excluded.revision,data=excluded.data,evidence=excluded.evidence,updated_at=excluded.updated_at",
            (memory.account, preview["expected_revision"] + 1, canonical_json(preview["data"]),
             canonical_json(evidence), utc_now()))
        return policy(memory)


def authorize_capture(memory, normal, allow_confirmation=False):
    current = policy(memory)
    rules = current["data"]["capture"]
    if not rules["enabled"]:
        raise StateError("passive capture is disabled; review a scoped capture policy first")
    for field, value in (("domains", normal["domain"]), ("kinds", normal["kind"]), ("scopes", normal["scope"])):
        if value not in rules[field]:
            raise StateError("capture policy excludes this " + field)
    if any(ref["kind"] not in rules["source_kinds"] for ref in normal["source_refs"]):
        raise StateError("capture policy excludes this source category")
    if normal["sensitivity"] == "sensitive" and not current["data"]["allow_sensitive"]:
        raise StateError("sensitive capture needs a separate explicit policy opt-in")
    if normal["authority"] == "user_confirmed" and not allow_confirmation:
        raise StateError("passive capture cannot manufacture user confirmation")
    return current


def capture(memory, key, data, revision=None):
    with memory.transaction():
        normal = memory._resolve_work_sources(memory._normalise_data(data))
        current = authorize_capture(memory, normal)
        metadata = dict(normal["metadata"], capture_policy_revision=current["revision"])
        normal["metadata"] = metadata
        if normal["kind"] in current["data"]["review_days"] and not normal.get("review_after"):
            previous = memory.conn.execute("SELECT data FROM memory_records WHERE id=? AND account=?",
                                           (memory.record_id(normal["domain"], key), memory.account)).fetchone()
            old = parse_json(previous["data"]) if previous else {}
            normal["review_after"] = (
                old["review_after"] if old.get("review_after") and old.get("source_refs") == normal["source_refs"]
                else (datetime.now(timezone.utc) + timedelta(days=current["data"]["review_days"][normal["kind"]])).isoformat()
            )
        status = "active" if normal["authority"] == "source_observed" and normal["kind"] not in {"preference", "lesson"} else "candidate"
        previous = memory.conn.execute("SELECT status FROM memory_records WHERE id=? AND account=?",
                                       (memory.record_id(normal["domain"], key), memory.account)).fetchone()
        if previous is not None and previous["status"] in {"disputed", "stale"}:
            status = previous["status"]
        return memory.put(key, normal, status=status, revision=revision)


def retention_authorized(memory, record, expected_revision):
    current = policy(memory)
    if type(expected_revision) is not int or current["revision"] != expected_revision or not current["configured"]:
        raise StateError("retention policy changed or is not configured")
    days = current["data"]["retention_days"].get(record["kind"])
    if days is None or datetime.fromisoformat(record["updated_at"]) + timedelta(days=days) > datetime.now(timezone.utc):
        raise StateError("memory is not due under the approved retention policy")


def maintain(memory, limit=100, after=None):
    if type(limit) is not int or not 1 <= limit <= 1000:
        raise StateError("maintenance limit must be between 1 and 1000")
    if after is not None and not isinstance(after, str):
        raise StateError("maintenance cursor must be a string")
    with memory.transaction():
        current = policy(memory)
        rows = memory.conn.execute(
            "SELECT id FROM memory_records WHERE account=? AND id>? AND status<>'forgotten' ORDER BY id LIMIT ?",
            (memory.account, after or "", limit + 1)).fetchall()
        erased, review = [], []
        stamp = datetime.now(timezone.utc)
        for row in rows[:limit]:
            record = memory.show(row["id"])
            if record["status"] == "forgotten":
                continue
            days = current["data"]["retention_days"].get(record["kind"])
            if days is not None and datetime.fromisoformat(record["updated_at"]) + timedelta(days=days) <= stamp:
                erased.extend(memory.forget(record["id"], record["revision"],
                                            policy_revision=current["revision"])["forgotten_ids"])
            elif record.get("review_after") and datetime.fromisoformat(record["review_after"]) <= stamp:
                review.append({"id": record["id"], "revision": record["revision"], "reason": "review_due"})
        cutoff = (stamp - timedelta(days=current["data"]["usage_retention_days"])).isoformat()
        memory.conn.execute("DELETE FROM memory_usage WHERE id IN "
                            "(SELECT id FROM memory_usage WHERE account=? AND created_at<? ORDER BY created_at LIMIT ?)",
                            (memory.account, cutoff, limit))
        return {"processed": min(len(rows), limit), "forgotten_ids": sorted(set(erased)),
                "review_due": review, "next_cursor": rows[limit - 1]["id"] if len(rows) > limit else None,
                "policy_revision": current["revision"], "outbound_actions": False}


def tombstones(memory):
    return {"format": 1, "account": memory.account, "tombstones": [
        dict(row) for row in memory.conn.execute(
            "SELECT id,key_hash,forgotten_at FROM memory_tombstones WHERE account=? ORDER BY id", (memory.account,))]}


def tombstones_preview(memory, bundle):
    if not isinstance(bundle, dict) or set(bundle) != {"format", "account", "tombstones"}:
        raise StateError("invalid tombstone journal")
    if type(bundle["format"]) is not int or bundle["format"] != 1 or bundle["account"] != memory.account:
        raise StateError("tombstone journal belongs to another account or format")
    entries = bundle["tombstones"]
    if not isinstance(entries, list) or len(entries) > 10000:
        raise StateError("tombstone restore accepts at most 10000 entries")
    ids, affected = set(), []
    for entry in entries:
        if (not isinstance(entry, dict) or set(entry) != {"id", "key_hash", "forgotten_at"}
                or not isinstance(entry["key_hash"], str) or not re.fullmatch(r"[0-9a-f]{64}", entry["key_hash"])
                or entry["id"] != "mem_" + entry["key_hash"][:40] or entry["id"] in ids):
            raise StateError("invalid or duplicate tombstone identity")
        validate_timestamp(entry["forgotten_at"])
        ids.add(entry["id"])
        row = memory.conn.execute("SELECT id,revision,status FROM memory_records WHERE id=? AND account=?",
                                  (entry["id"], memory.account)).fetchone()
        if row and row["status"] != "forgotten":
            affected.append({"id": row["id"], "revision": row["revision"]})
    known = {entry["id"] for entry in affected}
    from memory_dream import source_keys
    for row in memory.conn.execute(
            "SELECT r.memory_id,r.data,m.revision FROM memory_revisions r JOIN memory_records m "
            "ON m.id=r.memory_id WHERE m.account=? AND m.status<>'forgotten'", (memory.account,)):
        if row["memory_id"] in known:
            continue
        if any(ref["kind"] == "session_checkpoint"
               and any(memory.record_id("user", key) in ids for key in source_keys(ref))
               for ref in parse_json(row["data"]).get("source_refs", [])):
            affected.append({"id": row["memory_id"], "revision": row["revision"]})
            known.add(row["memory_id"])
    affected.sort(key=lambda row: row["id"])
    value = {"account": memory.account, "journal_hash": digest(bundle), "affected": affected}
    return dict(value, subject_id="memory-restore:" + digest(value), revision=1)


def restore_tombstones(memory, bundle, evidence):
    with memory.transaction():
        preview = tombstones_preview(memory, bundle)
        memory._human(evidence, preview["subject_id"], 1, "restore-erasures")
        for target in preview["affected"]:
            # The reviewed restore names every resurrected root and binds its current revision.
            confirmation = dict(evidence, subject_id=target["id"], revision=target["revision"], decision="forget",
                                approved_restore_subject=preview["subject_id"])
            memory.forget(target["id"], target["revision"], confirmation)
        for entry in bundle["tombstones"]:
            memory.conn.execute("INSERT OR IGNORE INTO memory_tombstones VALUES(?,?,?,?)",
                                (entry["id"], memory.account, entry["key_hash"], entry["forgotten_at"]))
        return {"restored_tombstones": len(bundle["tombstones"]), "erased_roots": len(preview["affected"]),
                "outbound_actions": False}


def export_preview(memory, memory_id, recipe):
    row = memory.show(memory_id)
    if row["kind"] != "lesson" or row["status"] != "active" or row["authority"] != "user_confirmed":
        raise StateError("only an active, explicitly confirmed lesson can be exported")
    if not memory._work_sources_current(row) or row.get("sensitivity") == "sensitive":
        raise StateError("lesson is stale, inaccessible or sensitive; export refused")
    stamp = utc_now()
    if (row.get("valid_from", stamp) > stamp or row.get("valid_to", "9999") <= stamp
            or row.get("review_after", "9999") <= stamp):
        raise StateError("lesson validity or review period has expired")
    if not isinstance(recipe, dict) or set(recipe) != {"title", "goal", "preconditions", "steps", "limitations"}:
        raise StateError("recipe requires title, goal, preconditions, steps and limitations only")
    for field, value in recipe.items():
        if not isinstance(value, str) or not value.strip() or len(value) > 4000:
            raise StateError("recipe fields must be nonempty strings of at most 4000 characters")
    encoded = canonical_json(recipe)
    patterns = (r"\b[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}\b", r"https?://", r"/(?:Users|home)/\S+",
                r"[A-Za-z]:\\", r"\b[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\b",
                r"\b(?:mem_|act_|work_)[0-9a-f]{16,}", r"(?i)\b(?:bearer|password|refresh.token|api.key)\s*[:=]")
    if any(re.search(pattern, encoded) for pattern in patterns):
        raise StateError("recipe contains an identifier, URL, path or credential marker; replace it with a generic placeholder")
    value = {"account": memory.account, "memory_id": memory_id, "memory_revision": row["revision"], "recipe": recipe}
    return dict(value, subject_id="memory-export:" + digest(value), revision=1,
                warning="Automated checks cannot recognize all private context. Review every word. Export is local, not permission to publish.")


def export_recipe(memory, memory_id, recipe, evidence, output):
    with memory.transaction():
        preview = export_preview(memory, memory_id, recipe)
        memory._human(evidence, preview["subject_id"], 1, "export")
        destination = Path(output).expanduser().absolute()
        margo_store._no_symlinks(destination)
        parent = margo_store._safe_directory(destination.parent, explicit_override=True)
        margo_store._mkdir_private(parent)
        margo_store._private(parent, directory=True)
        content = "# " + recipe["title"].strip() + "\n\n"
        content += "\n\n".join("## " + field.capitalize() + "\n\n" + recipe[field].strip()
                                for field in ("goal", "preconditions", "steps", "limitations")) + "\n"
        temporary = None
        try:
            with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=parent,
                                             prefix=".memory-export-", delete=False) as handle:
                temporary = Path(handle.name)
                handle.write(content)
                handle.flush()
                os.fsync(handle.fileno())
            os.link(temporary, destination)
        finally:
            if temporary is not None:
                temporary.unlink()
        return {"output": str(destination), "sha256": hashlib.sha256(content.encode("utf-8")).hexdigest(),
                "published": False, "source_metadata_exported": False}
